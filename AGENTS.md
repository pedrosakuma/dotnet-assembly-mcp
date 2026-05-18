# AGENTS.md

> Operating guide for AI coding agents (Copilot CLI, Codex, Claude Code, Cursor, etc.)
> working on this repository. Humans benefit too.

## What this project is

`dotnet-assembly-mcp` is an **MCP server** for *static* navigation of compiled .NET
assemblies — types, methods, attributes, signatures, references, on-demand
decompilation — designed as a **token-efficient alternative to feeding source code
into an LLM context**.

Companion: [`dotnet-diagnostics-mcp`](https://github.com/pedrosakuma/dotnet-diagnostics-mcp)
does the *dynamic* side (live process attach, EventPipe). The two compose via the
handoff contract defined in [`docs/handoff-contract.md`](./docs/handoff-contract.md).

**Status:** design + spike phase. No production code on `main` yet. Active scaffold
work tracked in [issue #1](https://github.com/pedrosakuma/dotnet-assembly-mcp/issues/1).

## Read before contributing

1. [`docs/handoff-contract.md`](./docs/handoff-contract.md) — the wire format that
   ties us to `dotnet-diagnostics-mcp`. Do not change without coordinating
   `dotnet-diagnostics-mcp#18`.
2. [`docs/mcp-conventions.md`](./docs/mcp-conventions.md) — how MCP tools, resources,
   responses, errors, and bootstrap are structured here. Mirrors the companion repo;
   drift is what we are actively trying to prevent.
3. The companion repo's `AGENTS.md` and `src/DotnetDiagnosticsMcp.Server/Program.cs`
   — the single most important piece of prior art for this codebase.

## Critical rules (easy to violate, costly to fix)

- **Never reference MCP packages from `DotnetAssemblyMcp.Core`.** Core is the
  testable domain; the MCP attributes live in `Server`.
- **Never `Assembly.Load`.** We read metadata via `PEReader` / `MetadataReader` only.
  Loading executable code into our own AppDomain would be a sandbox escape vector
  for any malicious assembly we're asked to inspect.
- **Never decompile in `ListMethods` or any Tier-1 path.** Decompilation is Tier-3,
  per-method, on demand. See `docs/mcp-conventions.md` §2.1.
- **Never repurpose an `Error.Kind` value.** Once published, codes are forever.
  Add a new one.
- **Never exceed 10 tools.** Expose new capabilities as Resources or parameters.
  See `docs/mcp-conventions.md` §2.1.
- **Mirror the companion** for build conventions (CPM, warnings-as-errors, slnx,
  `net10.0`, SDK 10.0.201 via `global.json`). Don't invent a new convention "because
  it's our repo" — drift between the two repos is the single biggest cost.

## Build, test, run

Once the scaffold lands (see #1):

```bash
dotnet build -c Release
dotnet test -c Release --no-build
dotnet run --project src/DotnetAssemblyMcp.Server -c Release
```

Until then, the only buildable thing is the spike on the `spike/metadata-lib` branch:

```bash
git checkout spike/metadata-lib
cd spike && dotnet build -c Release
dotnet run --project src/Spike -- fixtures/SampleLib/bin/Release/net9.0/SampleLib.dll
```
