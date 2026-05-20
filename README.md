# dotnet-assembly-mcp

> **Status:** 24 tools shipped, dual transport (stdio + HTTP), packaged as `dotnet tool`, Docker image, and self-contained single-file binaries. Latest release: [`v0.11.0`](https://github.com/pedrosakuma/dotnet-assembly-mcp/releases/tag/v0.11.0) — `find_event_references` + cross-module `list_derived_types` (subclasses & interface implementers).

An **MCP server** for *static* navigation of compiled .NET assemblies — types, methods, attributes, signatures, IL, cross-references, and on-demand decompilation — designed as a **token-efficient alternative to feeding source code into an LLM context**.

## Why

When an AI agent needs to understand what a method does, the default today is to read the source file. For a 1,000-LOC class that's ~4–8k tokens, and most of it is irrelevant.

This server lets the agent **drill down**:

```
load_assembly(path)                  →  moduleVersionId + method count   (~30 tokens)
get_method(mvid, token)              →  signature, attributes, IL size   (~30 tokens)
get_method_il(mvid, token)           →  raw IL hex, instruction count    (~80 tokens)
scan_method_il(mvid, token)          →  outbound calls / fields / types  (~150 tokens)
decompile_method(mvid, token)        →  C# body, hard-capped             (~200–500 tokens)
find_callers(mvid, token)            →  reverse call graph (intra+cross) (~100 tokens)
get_method_source(mvid, token)       →  PDB file/lines + SourceLink URL  (~40 tokens)
```

Closed generic instantiations are first-class: `get_method` / `find_callers` accept `genericTypeArguments` so the agent gets `int Echo(int)` instead of `T Echo(T)` and `find_callers` narrows to callers whose `MethodSpec` matches the requested instantiation. Producers that already have a `MethodSpec` row in the caller's module can skip the string-rendering step and supply `methodSpecModuleVersionId` + `methodSpecMetadataToken` as a fast-path; when both forms are present they are cross-checked and a mismatch yields `generic_instantiation_mismatch`. See [`docs/handoff-contract.md §3.5`](./docs/handoff-contract.md#35-generic-instantiations).

The agent pays only for what it actually needs to see.

## Install

For the full guide (single-file binaries, systemd / launchd / Scheduled Task supervisors, Kubernetes manifest), see [`docs/consumer-install.md`](./docs/consumer-install.md).

### As a global `dotnet tool` (stdio — local MCP clients)

```bash
dotnet tool install -g dotnet-assembly-mcp
dotnet-assembly-mcp --stdio    # speak MCP over STDIN/STDOUT
```

Requires the .NET 10 runtime. Logs go to STDERR so STDOUT stays a clean JSON-RPC channel.

### As a Docker image (HTTP — sidecar / multi-client)

```bash
docker run --rm -p 8788:8080 \
  -v /path/to/assemblies:/assemblies:ro \
  ghcr.io/pedrosakuma/dotnet-assembly-mcp:latest
# MCP endpoint: http://localhost:8788/mcp
# Health:       http://localhost:8788/health
```

Or build locally: `docker build -t dotnet-assembly-mcp:dev -f deploy/Dockerfile .`.

### Joint with `dotnet-diagnostics-mcp` (recommended, optional)

The diagnostics server emits `MethodIdentity` / `TypeIdentity` handles. As of
[`dotnet-diagnostics-mcp` #28](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/28),
the diagnostics server already resolves PDBs locally and stamps `SourceLocation` directly
onto every CPU-sample hotspot identity — so for dev workflows where the source tree is open
in your editor, the diagnostics server is sufficient on its own.

Pairing **this** server with diagnostics is recommended when you also want:

- Stripped binaries / NativeAOT (no PDB, no inline source).
- Third-party assemblies you don't have source for.
- Decompilation (`decompile_method`), reverse cross-reference (`find_callers`, `find_type_references`, …).

Run both together:

```bash
export ASSEMBLIES_DIR=/abs/path/to/your/published/binaries
docker compose -f deploy/docker-compose.yml up -d
# diagnostics: http://localhost:8787/mcp
# assembly:    http://localhost:8788/mcp
```

The same `docker-compose.yml` ships in
[`pedrosakuma/dotnet-diagnostics-mcp:deploy/docker-compose.yml`](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/blob/main/deploy/docker-compose.yml)
— bring it up from either checkout. Set `MCP_BEARER_TOKEN` on the host to
gate both servers with one shared token.

## Client configuration

### Claude Desktop / Cursor / VS Code / Copilot CLI (stdio)

`mcp.json` (Claude Desktop: `~/Library/Application Support/Claude/claude_desktop_config.json`):

```jsonc
{
  "mcpServers": {
    "dotnet-assembly-mcp": {
      "command": "dotnet-assembly-mcp",
      "args": ["--stdio"]
    }
  }
}
```

If the tool isn't on `PATH`, point `command` at the absolute path (e.g. `~/.dotnet/tools/dotnet-assembly-mcp`).

### Streamable HTTP

```jsonc
{
  "mcpServers": {
    "dotnet-assembly-mcp": {
      "url": "http://localhost:8788/mcp"
    }
  }
}
```

## Tools

All tools share the same response envelope (`summary`, `data`, `hints`, `error`); `hints` advertise the suggested next tool so an agent can chain without rediscovering the API. Cross-module xref tools (`find_callers`, `find_type_references`, `find_field_references`, `find_property_references`, `find_event_references`, `find_string_references`, `find_attribute_targets`, `list_derived_types`) all use the same matching convention: same-module hits by metadata token, cross-module hits by `(assembly simple name, type full name, member signature)` against the child module's `TypeRef` / `MemberRef` rows.

### Discovery & loading
| Tool | Purpose |
|---|---|
| `load_assembly` | Load a `.dll`/`.exe` from disk (idempotent by MVID) |
| `list_assemblies` | List currently loaded modules |
| `list_assembly_references` | Outbound `AssemblyRef` rows for one module |
| `import_assembly_manifest` | Bulk-register a list of paths under configured roots |

### Type & method enumeration (Tier-1)
| Tool | Purpose |
|---|---|
| `list_types` | Paginated TypeDef listing, filterable by namespace / name / kind |
| `get_type` | Resolve `(mvid, token)` to a type summary (base type, interfaces, kind) |
| `list_derived_types` | Walk subclasses **and** interface implementers across every loaded module (`directOnly` / transitive) |
| `list_members` | Enumerate fields / properties / events of a type |
| `list_methods` | Paginated MethodDef listing, filterable by declaring type |
| `find_method` | Module-wide MethodDef search by regex on name / signature |
| `list_attributes` | Custom attributes on a module / type / method / parameter / field / property / event |

### Single-method resolution (Tier-2 / Tier-3)
| Tool | Purpose |
|---|---|
| `get_method` | Resolve `(mvid, token)` to a method summary; accepts `genericTypeArguments` / `genericMethodArguments` for a closed signature view |
| `get_method_il` | Raw IL bytes (hex), max-stack, instruction count |
| `get_method_il_text` | ildasm-like textual IL listing (capped, LRU-cached) |
| `scan_method_il` | Structured outbound references parsed from IL (calls, fields, types, strings) |
| `decompile_method` | C# body via ICSharpCode.Decompiler (hard-capped, LRU-cached) |
| `get_method_source` | PDB-resolved file/lines plus SourceLink URL (embedded PDB or sibling `.pdb`) |

### Reverse cross-reference (Tier-4)
| Tool | Purpose |
|---|---|
| `find_callers` | Every method whose IL calls a given method; narrows by instantiation when `genericMethodArguments` is supplied |
| `find_type_references` | Every site referencing a TypeDef (field/parameter/return/local types + `newobj` / `castclass` / `isinst` / `box` / `ldtoken` / generic args) |
| `find_field_references` | Every method whose IL touches a field (six opcodes: `ldfld`/`ldsfld`/`stfld`/`stsfld`/`ldflda`/`ldsflda`), filterable by read/write/all |
| `find_property_references` | Every call to a property's getter or setter, filterable by accessor |
| `find_event_references` | Every call to an event's add/remove/raise accessor, filterable by accessor |
| `find_string_references` | Every method whose IL emits `ldstr` for a given literal (exact / contains / regex) |
| `find_attribute_targets` | Reverse custom-attribute index: every assembly/type/method/parameter/field/property/event bearing a given attribute |

## Companion project

Scope-disjoint from [`pedrosakuma/dotnet-diagnostics-mcp`](https://github.com/pedrosakuma/dotnet-diagnostics-mcp), which performs **dynamic** diagnostics (attach, EventPipe sampling, GC, exceptions) on a running .NET process. Together they form a closed loop:

```
[dotnet-diagnostics-mcp]            [dotnet-assembly-mcp]
   ──────────────────────             ──────────────────────
   list_dotnet_processes              load_assembly
   collect_cpu_sample        ──┐  ┌─→ get_method
   collect_exceptions          │  │   get_method_il / scan_method_il
                               │  │   decompile_method
                               ▼  │   find_callers
                        (MethodIdentity)
```

The handoff contract — `MethodIdentity = (moduleVersionId, metadataToken)` plus optional `genericTypeArguments` (§3.5) — lives in [`docs/handoff-contract.md`](./docs/handoff-contract.md) and is also served at `assembly://contract/method-identity` as an MCP resource.

### MCP resources

In addition to the tool surface, the server publishes a small set of read-only **resources** that MCP clients can subscribe to or fetch directly. None of them require a tool call:

| URI                                 | Content |
|-------------------------------------|---------|
| `assembly://contract/method-identity` | Full text of [`docs/handoff-contract.md`](./docs/handoff-contract.md) — the producer/consumer wire contract for `MethodIdentity`. |
| `assembly://manifest/loaded`        | JSON array of every currently-loaded module (`mvid`, `path`, `methodCount`). Mirrors `list_assemblies` without consuming a tool slot. |
| `assembly://manifest/loaded/{mvid}` | JSON object for one module by MVID. Returns 404-style empty body when the MVID isn't loaded. |

Clients that support resource subscriptions get a notification whenever the loaded-module set changes (e.g. after `load_assembly` or a file-watcher reload).

## Where it complements SourceLink

This server **does not** replace SourceLink / TraceLog source resolution. It is what the agent reaches for when:

- the deployed binary has no PDB or no SourceLink,
- the target is a third-party NuGet dependency,
- the runtime is NativeAOT-trimmed and metadata at runtime is sparse,
- or the agent just wants a structural overview without pulling 8 KB of source.

`get_method_source` is the second-chance source resolver: it reads the on-disk PDB (embedded portable PDB first, then sibling `.pdb`) so the agent doesn't need a separate SourceLink fetch when one is available locally.

## Building blocks

- [`System.Reflection.Metadata`](https://learn.microsoft.com/dotnet/standard/metadata-and-self-describing-components) — metadata-only reads, never `Assembly.Load`
- [`ICSharpCode.Decompiler`](https://github.com/icsharpcode/ILSpy) — full decompiler engine used by ILSpy
- [`ModelContextProtocol`](https://github.com/modelcontextprotocol/csharp-sdk) C# SDK 1.3.0

## License

MIT — see [`LICENSE`](./LICENSE).

