# Handoff contract: `MethodIdentity`

> **Status:** Consumer side fully implemented in this repo since v0.3.0; §3.5 generic instantiations + MethodSpec fast-path shipped in v0.5.0. Producer side tracked at [`pedrosakuma/dotnet-diagnostics-mcp#18`](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/18).

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

### 3.1 `assemblyPathHint` — single-call resolution from a producer payload

Every `(MVID, token)`-keyed tool (`get_method`, `decompile_method`, `get_method_il`, `scan_method_il`, `find_callers`) accepts an optional `assemblyPathHint` parameter. It is the recommended way for an agent that consumes a `MethodIdentity` from `dotnet-diagnostics-mcp` to resolve a hotspot in a single call instead of `load_assembly` + tool.

```jsonc
get_method({
  "moduleVersionId":   "1f5b2e84-…",
  "metadataToken":     "0x06000020",
  "assemblyPathHint":  "/app/MyApp.dll"   // == identity.modulePath from the producer
})
```

Semantics, applied before the underlying resolution:

| Situation | Behavior |
|---|---|
| MVID already loaded | Hint ignored. Resolve immediately. |
| MVID not loaded, hint absent | `module_not_found`. |
| MVID not loaded, hint present, **on-disk MVID matches** | Open and load the PE idempotently (same code path as `load_assembly`), then resolve. |
| MVID not loaded, hint present, **on-disk MVID differs** | `mvid_mismatch` carrying both the requested MVID and the MVID actually found at the hinted path. **The wrong binary is not loaded silently.** |

The hint is a hint — trust is anchored to the MVID match, never to the path. Producers SHOULD populate the hint from `MethodIdentity.modulePath`.

### 3.2 `import_assembly_manifest` — bulk handshake from a sidecar producer

When the producer can enumerate every loaded assembly inside the target process (e.g. `dotnet-diagnostics-mcp` reading `TraceLog.ModuleFile`), it SHOULD hand the consumer a single bulk manifest instead of one `assemblyPathHint` per hotspot:

```jsonc
import_assembly_manifest({
  "entries": [
    { "moduleVersionId": "1f5b2e84-…", "path": "/app/MyApp.dll",  "name": "MyApp.dll"  },
    { "moduleVersionId": "2a3b4c5d-…", "path": "/app/Lib.dll",    "name": "Lib.dll"    }
    // …
  ],
  "mode": "lazy"   // default. "tier1" opens every PE eagerly.
})
→
{
  "mode":       "lazy",
  "loaded":     [{ "moduleVersionId": "…", "moduleName": "…", "methodCount": 1234, "status": "loaded"|"already_loaded" }],
  "registered": [{ "moduleVersionId": "…", "path": "…" }],
  "skipped":    [{ "moduleVersionId": "…", "path": "…", "reason": "mvid_mismatch_with_path"|"file_not_found"|"invalid_argument"|"module_load_failed", "detail": "…" }]
}
```

Semantics:

- **`lazy` mode (default)** records each `(mvid → path)` in the resolver without opening the PE. A subsequent `get_method` for that MVID — *with no explicit `assemblyPathHint`* — uses the stored path automatically. Cheap for large manifests where the agent only drills into a small fraction of modules.
- **`tier1` mode** opens every entry eagerly (same code path as `load_assembly`) and adds it to the metadata index.
- Every entry is verified against the on-disk MVID before use. An entry whose actual MVID differs from the manifest is **rejected** with reason `mvid_mismatch_with_path` and the PE is **not** loaded — same safety property as §3.1.
- Re-importing an already-loaded MVID is a no-op (`status: "already_loaded"`); the manifest is idempotent.
- Imported lazy paths join the per-directory `FileSystemWatcher` so subsequent rebuilds are observed.

The three result buckets partition the input: every entry appears in exactly one of `loaded`, `registered` or `skipped`.

### 3.3 Batch tools — one round-trip for N hotspots

`get_methods`, `scan_methods_il` and `find_callers_batch` are batch variants of the matching single-call tools, sized for the common case where a producer hotspot dump contains 10–25 identities.

```jsonc
get_methods({
  "items": [
    { "moduleVersionId": "…", "metadataToken": "0x06000020", "assemblyPathHint": "/app/A.dll" },
    { "moduleVersionId": "…", "metadataToken": "0x06000031", "assemblyPathHint": "/app/A.dll" }
    // …
  ]
})
→
{
  "results": [
    { "index": 0, "item": {…}, "ok": true,  "data":  { /* same shape as get_method */ } },
    { "index": 1, "item": {…}, "ok": false, "error": { "kind": "token_out_of_range", "message": "…" } }
    // …
  ],
  "okCount":    1,
  "errorCount": 1
}
```

Contract:

- **Order preserved.** `results[i]` corresponds to `items[i]`; `index` is echoed for clients that re-order on receipt.
- **Per-item ok/error.** A single bad item does not fail the batch — only the cap check does.
- **Hint composition.** Each item may carry its own `assemblyPathHint` (semantics in §3.1). Combined with §3.2's lazy hint map, a single `import_assembly_manifest` + one `get_methods` call is usually enough to enrich an entire hotspot table.
- **Cap = 100.** Sending more items returns the structured error `batch_too_large`; split the input and retry.
- **`decompile_method` is single-call by design** (heavy + cache-friendly only on a small N). `scan_methods_il` and `find_callers_batch` share the xref/scan caches across items.

### 3.4 `get_method_source` — PDB second-chance for SourceLink

`get_method_source(moduleVersionId, metadataToken, assemblyPathHint?)` reads the module's PDB and returns the file/startLine/endLine triple plus a resolved SourceLink URL when available. Pair it with `dotnet-diagnostics-mcp`: when that server emits a hotspot whose `SourceLocation` is `null` (PDB missing in the live process, or SourceLink stripped from the runtime image), the agent calls this tool against the on-disk PE for a second chance.

```jsonc
get_method_source({
  "moduleVersionId": "1f5b2e84-…",
  "metadataToken":   "0x06000020"
})
→
{
  "found":      true,
  "file":       "/_/src/CoreClrSample/HotPath.cs",
  "startLine":  42,
  "endLine":    58,
  "sourceLink": "https://raw.githubusercontent.com/owner/repo/<sha>/src/HotPath.cs",
  "pdbKind":    "embedded" | "portable" | "windows" | "none",
  "pdbAge":     1,
  "reason":     null
}
```

Resolution order:
1. **Embedded portable PDB** in the PE's debug directory (`EmbeddedPortablePdb` entry).
2. **Sibling `.pdb`** next to the assembly, portable format (`BSJB` signature).
3. Otherwise `pdbKind = "none"`, `found = false`.

Semantics:
- **No HTTP.** SourceLink JSON in the PDB's CustomDebugInformation (Guid `CC110556-A091-4D38-9FEC-25AB9A351A6A`) is parsed locally; the URL is constructed by substituting the document path into the matching pattern. Fetching the URL is the agent's job.
- **`found = false` is not an error** — it's the documented outcome when no PDB exists, or the method has only hidden sequence points (compiler-generated bodies). The accompanying `reason` field explains which case.
- **Composes with `assemblyPathHint`** (§3.1) — same load semantics; once the module is loaded, its PDB is opened once and cached for the lifetime of the index.
- Legacy Windows PDBs are detected (`pdbKind = "windows"`) but not read — `System.Reflection.Metadata` only handles portable PDBs.

### 3.5 Generic instantiations (`MethodSpec` handoff) — `genericTypeArguments`

> **Status:** Shipped in v0.5.0. Producer counterpart: [`dotnet-diagnostics-mcp#21`](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/21).

The base `MethodIdentity` (§2) resolves to the **open** `MethodDef`. Runtime hotspots, however, are almost always **closed** generic instantiations: `List<int>.Add`, not `List<T>.Add`. Collapsing both into the same identity loses exactly the signal a perf agent cares about — *which* instantiation is hot. This section extends the contract to carry instantiations end-to-end without losing the `(MVID, token)` anchor.

#### Wire shape — MCP tool parameters (canonical)

The MCP tools expose generic instantiation as **flat optional parameters**. Pass them alongside the base `(moduleVersionId, metadataToken)` on the tool call:

```jsonc
get_method({
  "moduleVersionId":           "…MVID of List`1's module…",
  "metadataToken":             "0x06000028",                 // open MethodDef (unchanged)

  // Closed instantiation, explicit string form:
  "genericTypeArguments":      ["System.Int32"],             // type-level args, in declaration order. Omit / empty if type is non-generic.
  "genericMethodArguments":    [],                           // method-level args, in declaration order. Omit / empty if method is non-generic.

  // Closed instantiation, fast-path form (alternative or complementary):
  "methodSpecModuleVersionId": "…callerMvid…",               // MVID of the caller module that owns a MethodSpec row encoding this instantiation.
  "methodSpecMetadataToken":   "0x2B000007"                  // MethodSpec token (table 0x2B) inside that module.
})
```

When both forms are present, the consumer cross-checks them element-wise and yields `generic_instantiation_mismatch` if they disagree. When only the fast-path is present and the spec module is loaded, the consumer decodes it natively (no string resolution). When only the fast-path is present and the spec module is **not** loaded, the consumer returns `module_not_found`. When both are present and the spec module is not loaded, the fast-path is skipped and validation proceeds via the explicit strings.

> **Note on producer-side wire shape.** Producers that need a self-describing JSON envelope (e.g. for the diagnostics handoff payload) may serialize the same information as a nested object — `{ "genericTypeArguments": { "type": [...], "method": [...] }, "methodSpec": { "moduleVersionId": …, "metadataToken": … } }` — and flatten it at the MCP call boundary. The canonical MCP parameter shape is flat, as shown above.

#### Canonical format for `genericTypeArguments` strings

Both producer and consumer MUST use **CLR reflection-style full names without assembly qualification**:

- **Namespace + name**: `System.Int32`, `System.Collections.Generic.Dictionary`2`.
- **Generic arity backtick**: `List`1`, `Dictionary`2` — required even when the type appears as a top-level arg (`"System.Collections.Generic.List`1[System.String]"`).
- **Nested types**: `+` separator, CLR-style (`Outer+Inner`), NOT `.`. Disambiguates nested vs namespaced.
- **Closed generic args inline**: bracketed comma list — `System.Collections.Generic.Dictionary`2[System.Int32,System.String]`. Recursive.
- **Arrays**: `T[]` (SZ), `T[,]` (rank-2), `T[*]` (MD rank-1 with non-zero lower bound).
- **By-ref / pointer**: `T&`, `T*`. Rare in instantiations but reserved.
- **No assembly qualification.** The consumer resolves each type name first in the module that owns the open `MethodDef`, then in any other loaded module (`assembly://manifest/loaded`). If the name resolves in 2+ modules with conflicting identities, the consumer fails with `generic_instantiation_ambiguous` (echoing the candidate MVIDs). If it doesn't resolve in any loaded module, `generic_instantiation_unresolvable` — the agent's recovery is to call `load_assembly` for the missing dependency (or supply `assemblyPathHint`).

> **TypeRef policy.** As of v0.5.0 the consumer resolves type-arg names against **loaded `TypeDef` rows** plus a small well-known allowlist (e.g. `System.Int32`, `System.String`). Non-well-known framework or third-party `TypeRef`s are reported as `generic_instantiation_unresolvable` until the defining assembly is loaded. This keeps the resolver hermetic — the consumer never fetches dependencies on its own.

#### Resolution semantics on the consumer

The consumer:
1. Resolves the open `MethodDef` per §3 (same path as today).
2. If `methodSpec*` is supplied, decodes the spec row natively from the caller module's metadata; validates that `MethodSpec.Method` resolves to the requested `MethodDef` (else `generic_instantiation_mismatch`).
3. If `genericTypeArguments` / `genericMethodArguments` are supplied, resolves each type-arg name into a `TypeDef` handle in some loaded module and renders it in wire format.
4. Cross-checks the two sources when both are present (`generic_instantiation_mismatch` on disagreement).
5. Materializes a synthetic closed signature in memory (no row written to the metadata stream).
6. Returns the **closed** signature in the response, while keeping the original `(MVID, token)` of the open def as the identity anchor.

Tools that accept the §3.5 parameters: `get_method`, `find_callers`, plus the batch variants (`get_methods`, `find_callers_batch`). `find_callers` with a closed identity restricts results to `MethodSpec` rows whose `Method` resolves to the open def AND whose `Instantiation` blob matches — i.e. *only* callers of the int instantiation, not all callers of `List<T>.Add`. Type-level filtering (e.g. `Box<int>.ctor` vs `Box<string>.ctor`) is supported by matching the `MemberRef`'s `TypeSpec` parent against the requested `genericTypeArguments`.

`decompile_method` intentionally does **not** accept the generic args: ICSharpCode.Decompiler operates on `MethodDef`, not on closed instantiations, so it always emits the open C# (`T Echo(T value)`). The open form is the correct decompiler output — for a closed *signature* view, use `get_method` with `genericTypeArguments` / `genericMethodArguments` (or the `methodSpec` fast-path). Tracked in [issue #10](https://github.com/pedrosakuma/dotnet-assembly-mcp/issues/10) if demand arises for a header-substitution mode.

`scan_method_il`, `scan_methods_il`, and `get_method_source` do not accept the §3.5 parameters either: IL token references are invariant across instantiations (the IL of an open generic method is identical regardless of how it's invoked), and PDB sequence points anchor on the open `MethodDef`. Pass the closed args to `get_method` if you need a closed signature alongside the IL/source view.

#### Out of scope

- **Open generics in the args** (e.g. arg referencing `!0` of an outer scope) — runtime instantiations are always closed; if a producer ever emits an open arg, the consumer rejects with `generic_instantiation_open`.
- **Variance / constraints** — irrelevant for resolution; consumer doesn't validate.
- **Inferring instantiations from `displayName`** — the consumer does NOT parse `List<Int32>` out of a display string. Without `genericTypeArguments` or `methodSpec`, the consumer resolves the **open** def as today.

---

## 4. Error shape

When resolution fails, the consumer MUST return a structured error with one of the following `code` values. The MCP tool surface (TBD) will translate these into the standard error envelope.

| `code`               | When |
|----------------------|------|
| `module_not_found`   | No loaded module matches `moduleVersionId`, and load-from-path / load-by-name fallbacks failed. Also returned when the §3.5 `methodSpec` module is not loaded and no explicit `genericTypeArguments` fallback was supplied. |
| `module_load_failed` | `load_assembly` failed (file missing, bad PE, permission denied) or the decompiler could not open the underlying assembly. |
| `mvid_mismatch`      | A module was found by `modulePath` or `moduleName`, but its MVID differs from the one in the identity. Producer and consumer are looking at different builds of the same assembly. |
| `token_out_of_range` | The `MethodDef` row id encoded in `metadataToken` exceeds the module's `MethodDef` table size. |
| `token_wrong_table`  | `metadataToken` (or `methodSpecMetadataToken`) decodes to a table other than the expected one (`MethodDef` 0x06 / `MethodSpecification` 0x2B). |
| `token_trimmed`      | The requested method has no body in the target module (trimmed / NativeAOT). |
| `identity_malformed` | Required field missing or wrong type in the `MethodIdentity` payload. |
| `invalid_argument`   | A parameter failed validation before any resolution was attempted (e.g. unparseable token, malformed GUID, paired `methodSpec*` fields supplied incompletely). |
| `path_not_allowed`   | The supplied path is outside the configured search roots and explicit loading is disabled by configuration. |
| `batch_too_large`    | A batch tool received more than 100 items in a single call. Split the input and retry. |
| `generic_instantiation_unresolvable` | A type-arg name in `genericTypeArguments` (§3.5) did not resolve in any loaded module. Recovery: `load_assembly` for the missing dependency, or supply `assemblyPathHint`. |
| `generic_instantiation_ambiguous`    | A type-arg name in `genericTypeArguments` (§3.5) resolved in 2+ modules with conflicting MVIDs. Error echoes candidate MVIDs; producer should qualify or consumer should narrow the manifest. |
| `generic_instantiation_open`         | A type-arg referenced an open type parameter (`!0` / `!!0`). Instantiations on the wire MUST be closed. |
| `generic_instantiation_mismatch`     | (a) Both `methodSpec*` and `genericTypeArguments` (§3.5) were supplied and they decode to different instantiations, OR (b) the `methodSpec.Method` does not resolve to the requested open `MethodDef`. |

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
