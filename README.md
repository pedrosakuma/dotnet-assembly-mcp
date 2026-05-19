# dotnet-assembly-mcp

> **Status:** 7/10 tools shipped, dual transport (stdio + HTTP), packaged as `dotnet tool` and Docker image. Tracking issue: [#1](https://github.com/pedrosakuma/dotnet-assembly-mcp/issues/1).

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
```

The agent pays only for what it actually needs to see.

## Install

### As a global `dotnet tool` (stdio — local MCP clients)

```bash
dotnet tool install -g dotnet-assembly-mcp
dotnet-assembly-mcp --stdio    # speak MCP over STDIN/STDOUT
```

Requires the .NET 10 runtime. Logs go to STDERR so STDOUT stays a clean JSON-RPC channel.

### As a Docker image (HTTP — sidecar / multi-client)

```bash
docker build -t dotnet-assembly-mcp:dev -f deploy/Dockerfile .
docker run --rm -p 8788:8080 \
  -v /path/to/assemblies:/assemblies:ro \
  dotnet-assembly-mcp:dev
# MCP endpoint: http://localhost:8788/mcp
# Health:       http://localhost:8788/health
```

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

| Tool | Purpose |
|---|---|
| `load_assembly` | Load a `.dll`/`.exe` from disk (idempotent by MVID) |
| `list_assemblies` | List currently loaded modules |
| `get_method` | Resolve a `(moduleVersionId, metadataToken)` to a method summary |
| `get_method_il` | Raw IL bytes (hex), max-stack, instruction count |
| `scan_method_il` | Outbound references parsed from IL (calls, fields, types, strings) |
| `decompile_method` | C# body via ICSharpCode.Decompiler (hard-capped, LRU-cached) |
| `find_callers` | Reverse call graph: intra-module (MethodDef) + cross-module (MemberRef matching) |

Every tool returns the same envelope (`summary`, `data`, `hints`, `error`); `hints` advertise the suggested next tool so an agent can chain without rediscovering the API.

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

The handoff contract — `MethodIdentity = (moduleVersionId, metadataToken)` — lives in [`docs/handoff-contract.md`](./docs/handoff-contract.md) and is also served at `assembly://contract/method-identity` as an MCP resource.

## Where it complements SourceLink

This server **does not** replace SourceLink / TraceLog source resolution. It is what the agent reaches for when:

- the deployed binary has no PDB or no SourceLink,
- the target is a third-party NuGet dependency,
- the runtime is NativeAOT-trimmed and metadata at runtime is sparse,
- or the agent just wants a structural overview without pulling 8 KB of source.

## Building blocks

- [`System.Reflection.Metadata`](https://learn.microsoft.com/dotnet/standard/metadata-and-self-describing-components) — metadata-only reads, never `Assembly.Load`
- [`ICSharpCode.Decompiler`](https://github.com/icsharpcode/ILSpy) — full decompiler engine used by ILSpy
- [`ModelContextProtocol`](https://github.com/modelcontextprotocol/csharp-sdk) C# SDK 1.3.0

## License

MIT — see [`LICENSE`](./LICENSE).

