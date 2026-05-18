# Handoff contract: `MethodIdentity`

> **Status:** design spec, consumer side. No code in this repo yet.
> Counterpart (producer side): [`pedrosakuma/dotnet-diagnostics-mcp#18`](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/18).

This document defines the wire format that [`dotnet-diagnostics-mcp`](https://github.com/pedrosakuma/dotnet-diagnostics-mcp) emits alongside every method reference in its diagnostic output (CPU samples, exception stacks, GC events, …) and that [`dotnet-assembly-mcp`](https://github.com/pedrosakuma/dotnet-assembly-mcp) consumes to deterministically resolve a method.

Both servers MUST treat this document as the source of truth for the handoff shape. Producer and consumer evolve independently; the contract is the only coupling point.

---

## 1. Why a contract

Diagnostic output today references methods by *display name*, e.g.:

```
MyApp.Services.OrderService.Process
```

Names are lossy:

- **Overloads collide.** `Process(int)` and `Process(string)` share the same display name.
- **Generics stringify inconsistently.** `List`1<T>`, `List<T>`, `List<System.Int32>` — different tools, different conventions.
- **Compiler-synthesized members.** Async state machines (`<Process>d__7.MoveNext`), closures (`<>c__DisplayClass3_0.<Process>b__0`), and iterators don't round-trip from their display name to a source method.
- **No anchor.** The agent has no robust way to ask a follow-up tool *"show me the body of **this** method"* — it can only re-search by name.

Reading the original source file is the current workaround, and it is the most expensive thing the agent can do (~4–8 kB of tokens for a 1k-LOC file). It also doesn't work for third-party NuGet code, release binaries without source, or NativeAOT-trimmed builds.

The fix is to attach a **structured method identity** to every method mention. `(moduleVersionId, metadataToken)` is the canonical pair: it round-trips exactly to a single `MethodDefinition` in a single physical assembly, regardless of name mangling, generics, or compiler synthesis.

---

## 2. Canonical shape

Every diagnostic primitive that references a method MUST embed a `method` object alongside the human-readable `displayName`.

```jsonc
{
  "displayName": "MyApp.Services.OrderService.Process(System.Int32)",
  "method": {
    "moduleVersionId": "8f3a1c2d-9b4e-4a7f-b1c0-2d6e5f8a9b10",
    "metadataToken":   100663400,

    "moduleName":      "MyApp.dll",
    "modulePath":      "/app/MyApp.dll",

    "typeFullName":    "MyApp.Services.OrderService",
    "methodName":      "Process",
    "genericArity":    0
  }
}
```

### 2.1 Required fields (producer MUST emit, consumer MAY rely on)

| Field             | Type     | Meaning |
|-------------------|----------|---------|
| `moduleVersionId` | `string` (GUID, lowercase, 8-4-4-4-12) | PE **MVID** of the module that owns the method. Stable across copies of the same build; differs across rebuilds. Resolution anchor. |
| `metadataToken`   | `integer` | The method's metadata token *as a single 32-bit value* (`table << 24 \| rid`). For methods this is always in the `MethodDef` table (`0x06000000` mask). Resolution anchor. |

`(moduleVersionId, metadataToken)` together are sufficient to resolve a method. Everything else in §2.2 is for display and best-effort fallback.

### 2.2 Optional fields (producer SHOULD emit when known, consumer MUST NOT require)

| Field          | Type      | Meaning |
|----------------|-----------|---------|
| `moduleName`   | `string`  | Bare PE file name, e.g. `"MyApp.dll"`. Useful for human display and for fallback lookup when the producer didn't capture a path. |
| `modulePath`   | `string`  | Absolute path on the producer's host. **MAY be unusable on the consumer's host** (different container, different machine). Treat as a hint. |
| `typeFullName` | `string`  | Declaring type's full name in **reflection notation** (nested types separated by `+`, e.g. `"MyApp.Outer+Inner"`). |
| `methodName`   | `string`  | Bare method name (no signature, no generic suffix). For compiler-synthesized methods, the actual synthesized name (`MoveNext`, `<Process>b__0`, …). |
| `genericArity` | `integer` | Number of method-level generic parameters. `0` for non-generic. Does **not** include the declaring type's generic parameters. |

### 2.3 Non-fields (deliberately excluded)

- **Source file / line.** Different concern; resolved via PDB / SourceLink. See [`dotnet-diagnostics-mcp#11`](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/11). The two compose: `MethodIdentity` resolves to a `MethodDef`, SourceLink resolves to a file/line.
- **IL bytes, signature blob, attributes.** Returned by `dotnet-assembly-mcp` tools when the agent asks for them, not embedded in the handoff.
- **Assembly-qualified name / strong name / version triple.** Intentionally not used as the resolution key — MVID is stronger (it disambiguates rebuilds with the same `AssemblyVersion`).

---

## 3. Consumer-side resolution

This server resolves a `MethodIdentity` to an in-memory `MethodDefinition` (or equivalent) as follows.

```
1. Find a loaded module M where M.Mvid == identity.moduleVersionId.
   - If none, try to load from identity.modulePath (best-effort; path is a hint).
   - If still none, try to load by identity.moduleName from the configured search roots.
   - If still none → error: module_not_found.

2. Resolve identity.metadataToken against M's metadata.
   - Token MUST be in the MethodDef table (mask 0x06000000).
   - If the row id is out of range → error: token_out_of_range.
   - If the token resolves to a different table → error: token_wrong_table.

3. Return the resolved MethodDefinition. The consumer MAY cross-check the
   optional fields (typeFullName, methodName, genericArity) and emit a
   non-fatal warning on mismatch — useful for catching producer/consumer
   version skew without breaking resolution.
```

**Library choice: TBD.** The candidates are [`Mono.Cecil`](https://github.com/jbevain/cecil), [`AsmResolver`](https://github.com/Washi1337/AsmResolver), and [`System.Reflection.Metadata`](https://learn.microsoft.com/dotnet/api/system.reflection.metadata). All three expose MVID and token-based lookup; the decision is deferred to the implementation phase and does not affect this contract — the spec is written in terms of behavior (`(MVID, token) → MethodDef`), not in terms of a specific API.

---

## 4. Error shape

When resolution fails, the consumer MUST return a structured error with one of the following `code` values. The MCP tool surface (TBD) will translate these into the standard error envelope.

| `code`               | When |
|----------------------|------|
| `module_not_found`   | No loaded module matches `moduleVersionId`, and load-from-path / load-by-name fallbacks failed. |
| `mvid_mismatch`      | A module was found by `modulePath` or `moduleName`, but its MVID differs from the one in the identity. Producer and consumer are looking at different builds of the same assembly. |
| `token_out_of_range` | The `MethodDef` row id encoded in `metadataToken` exceeds the module's `MethodDef` table size. |
| `token_wrong_table`  | `metadataToken` decodes to a table other than `MethodDef` (`0x06`). |
| `identity_malformed` | Required field missing or wrong type. |

Errors SHOULD include the offending identity (echoed back) to help the agent debug without a second round trip.

---

## 5. Worked example

Producer (`dotnet-diagnostics-mcp.collect_cpu_sample`) emits a hotspot:

```jsonc
{
  "samples": 412,
  "percent": 18.7,
  "frame": {
    "displayName": "MyApp.Services.OrderService.Process(System.Int32)",
    "method": {
      "moduleVersionId": "8f3a1c2d-9b4e-4a7f-b1c0-2d6e5f8a9b10",
      "metadataToken":   100663400,
      "moduleName":      "MyApp.dll",
      "modulePath":      "/app/MyApp.dll",
      "typeFullName":    "MyApp.Services.OrderService",
      "methodName":      "Process",
      "genericArity":    0
    }
  }
}
```

Agent forwards the `method` object to this server:

```jsonc
// hypothetical tool call (surface TBD)
get_method({
  "moduleVersionId": "8f3a1c2d-9b4e-4a7f-b1c0-2d6e5f8a9b10",
  "metadataToken":   100663400
})
```

Server resolves `(MVID, token) → MethodDefinition` and replies with a compact handle plus the structural summary the agent paid for:

```jsonc
{
  "handle": "m:8f3a1c2d…:100663400",
  "signature": "void Process(int orderId)",
  "attributes": ["public", "virtual"],
  "ilSize": 87,
  "declaringType": "MyApp.Services.OrderService"
}
```

From there the agent can drill into `decompile_method(handle)` or `find_callers(handle)` only if it actually needs to — paying tokens proportional to what it reads.

---

## 6. Versioning policy

- The contract is **additive**. New optional fields MAY be added at any time.
- Consumers MUST ignore unknown fields. Producers MUST NOT remove or repurpose existing fields.
- A breaking change requires a new top-level field name (e.g. `method_v2`) emitted in parallel during a deprecation window.
- Field semantics in §2 are normative; field ordering in JSON is not.

---

## 7. References

- Producer-side design issue: [`pedrosakuma/dotnet-diagnostics-mcp#18`](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/18)
- PE metadata tokens: <https://learn.microsoft.com/dotnet/api/system.reflection.metadata.metadatatokens>
- ECMA-335, §II.22.26 (`MethodDef` table), §II.24.2.6 (`#GUID` heap / MVID)

---

## 8. Implementation notes

### 8.1 Metadata library

**Chosen:** [`System.Reflection.Metadata`](https://learn.microsoft.com/dotnet/api/system.reflection.metadata) (BCL).

Decided via a comparison spike (#2) that implemented the same small surface
(`Open`, `Resolve(mvid, token)`, `ListMethods`, `GetIlBytes`, `ScanIl`) against
three candidates and ran them against a `net9.0` fixture from a `net10.0`
runner. All three libraries produced byte-identical IL summary results
(outbound calls, field refs, string literals, tokens), confirming correctness
parity. SRM dominated on perf and footprint, by wide margins:

| Library | Open (ms) | ListMethods (ms) | Alloc/run (B) | Resident ×10 (B) |
|---|---:|---:|---:|---:|
| Mono.Cecil  | 0.077 | 0.428 | 100,712 | 698,128 |
| AsmResolver | 0.549 | 1.006 | 158,088 | 1,180,648 |
| **SRM**     | **0.063** | **0.055** | **24,528** | **83,784** |

Decision rationale:

- **In-box on net10** — zero NuGet dependency, no supply-chain surface for the
  most-loaded component of the server.
- **Lowest overhead** — for a server whose Tier-1 indexes are resident across
  potentially many assemblies, a ~10× advantage in resident memory and ~8×
  advantage in list time materially changes capacity planning.
- **Free interop with `ICSharpCode.Decompiler`** — the decompiler is built on
  `MetadataReader` internally, so the Tier-3 decompile path can reuse the same
  `PEReader` we already hold, avoiding a second PE parse per method.
- **Stable** — `System.Reflection.Metadata` ships with the runtime; no
  third-party release cadence to track.

Trade-off accepted:

- SRM is more verbose. The spike adapter is ~210 LOC vs ~100 for Cecil /
  AsmResolver, mostly because IL operand decoding and signature
  pretty-printing have to be written explicitly. This is a one-time cost paid
  in the consumer-side resolver; the verbosity is encapsulated and does not
  leak into the contract or the MCP tool surface.

The spike code lives on the throwaway `spike/metadata-lib` branch and is not
intended to merge into `main`.
