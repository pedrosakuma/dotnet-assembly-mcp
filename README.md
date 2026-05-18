# dotnet-assembly-mcp

> **Status:** scoping / not yet implemented. This repository is bootstrapped to host the design discussion and an eventual implementation.

An **MCP server** for *static* navigation of compiled .NET assemblies — types, methods, attributes, signatures, references, and on-demand decompilation — designed to be a **token-efficient alternative to feeding source code into an LLM context**.

## Why

When an AI agent needs to understand what a method does, the default today is to read the source file. For a 1,000-LOC class that's ~4–8k tokens, and most of it is irrelevant.

This server lets the agent **drill down**:

```
get_type(handle)          →  list of members (~50 tokens)
get_method(handle)        →  signature, attributes, IL size, references (~30 tokens)
decompile_method(handle)  →  C# body, only when needed (~200–500 tokens)
find_callers(handle)      →  cross-cutting reference graph
```

The agent pays only for what it actually needs to see.

## Companion project

This project is intentionally scope-disjoint from [`pedrosakuma/dotnet-diagnostics-mcp`](https://github.com/pedrosakuma/dotnet-diagnostics-mcp), which performs **dynamic** diagnostics (attach, EventPipe sampling, GC, exceptions) on a running .NET process.

Together they form a complete loop:

```
[dotnet-diagnostics-mcp]            [dotnet-assembly-mcp]
   ──────────────────────             ──────────────────────
   list_dotnet_processes              load_assembly
   collect_cpu_sample        ──┐  ┌─→ get_method
   collect_exceptions          │  │   decompile_method
                               ▼  │   find_callers
                        (method handle)
```

A CPU hotspot identified by `dotnet-diagnostics-mcp` is described by a method identity that this server can resolve into a signature, attributes, body, and call graph — without the agent ever having to read the repo.

The handoff contract is being designed in [`dotnet-diagnostics-mcp#18`](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/18); the consumer-side spec lives here in [`docs/handoff-contract.md`](./docs/handoff-contract.md).

## Where it complements SourceLink

This server **does not** replace SourceLink / TraceLog source resolution — those give file/line and original C# from the user's repo when symbols and source URLs are available. This server is what the agent reaches for when:

- the deployed binary has no PDB or no SourceLink,
- the target is a third-party NuGet dependency,
- the runtime is NativeAOT-trimmed and metadata at runtime is sparse,
- or the agent just wants a structural overview without pulling 8 kB of source.

## Building blocks

The implementation will lean on mature .NET libraries:

- [`ICSharpCode.Decompiler`](https://github.com/icsharpcode/ILSpy) — full decompiler engine used by ILSpy
- [`Mono.Cecil`](https://github.com/jbevain/cecil) or [`AsmResolver`](https://github.com/Washi1337/AsmResolver) — fast metadata-only reads
- [`Microsoft.CodeAnalysis`](https://github.com/dotnet/roslyn) — only if Roslyn-based call graph is needed

The MCP surface follows the same principles as the companion project:

- ≤10 tools
- handle-based drill-down (summary → detail)
- structured output, deterministic ordering
- discoverable via `serverInfo.description`, tool titles, and `instructions`

## Status

Empty. Design is happening in the linked issue first; code follows once the handoff contract is agreed.

## License

MIT — see [`LICENSE`](./LICENSE).
