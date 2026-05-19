# MCP server conventions

> Conventions for the `dotnet-assembly-mcp` server. These mirror what the companion
> [`dotnet-diagnostics-mcp`](https://github.com/pedrosakuma/dotnet-diagnostics-mcp)
> does — the two servers are designed to compose, and drift between them is the #1
> source of wasted debugging time.

Source-of-truth references (in priority order):

1. The MCP specification, currently [2025-06-18](https://modelcontextprotocol.io/specification/2025-06-18).
2. The [`ModelContextProtocol` C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) (v1.3.0+).
3. The companion repo's actual server code, especially `Program.cs`, `DiagnosticTools.cs`, and `DiagnosticResult.cs`.

When in doubt, mirror the companion. Don't invent a new pattern.

---

## 1. Project layout

Two projects, one solution:

```
src/
  DotnetAssemblyMcp.Core/      ← domain logic, NO MCP package references
  DotnetAssemblyMcp.Server/    ← MCP tools, transport, auth; thin wrapper over Core
tests/
  DotnetAssemblyMcp.Core.Tests/
  DotnetAssemblyMcp.Server.IntegrationTests/
samples|fixtures/
  SampleLib/                   ← already exists on spike/metadata-lib
```

- **Core is testable in isolation.** Anything that needs MCP attribute decoration
  belongs in `Server`.
- **Server SDK** is `Microsoft.NET.Sdk.Web` (we expose HTTP). `Core` is plain `Microsoft.NET.Sdk`.

## 2. Tool design

### 2.1 Budget: **~10 tools, justify every addition**

Anthropic recommends ≤10 tools per LLM context. This server currently exposes **23**
because the consumer-side handoff (§3.5) plus the full Tier-4 cross-reference suite
each earn their slot — but the bar for adding the 24th must stay high. Before
adding a new tool, check the alternatives:

1. Extend an existing tool with a parameter, or
2. Expose the capability as an MCP **Resource** (`audience: ["assistant"]`) — does
   **not** count against the budget. Use for guides, schemas, the handoff contract.
3. Use **handle-based drill-down**: one summary tool returns small payload + handles;
   detail tools take a handle and return more. This is exactly the `(MVID, token)`
   handle pattern from [`docs/handoff-contract.md`](./handoff-contract.md).

If you still need a new tool, justify it in the PR description: which producer
hotspot or agent workflow needs it, why a Resource or parameter wouldn't carry the
same payload, and what the total count will be after the addition.

Current surface (23 tools — keep this table in sync with `AssemblyTools.cs`):

| Tool | Purpose | Tier |
|---|---|---|
| `load_assembly` | Register an explicit path outside configured search roots | n/a |
| `list_assemblies` | Enumerate currently loaded modules (MVID, name) | T1 |
| `list_assembly_references` | Outbound `AssemblyRef` table for a module | T1 |
| `import_assembly_manifest` | Bulk register a producer's path → MVID map | n/a |
| `list_types` | Enumerate type definitions for a module (paged) | T1 |
| `get_type` | Type summary by `(MVID, typeToken)` | T1 |
| `list_derived_types` | Subtype / implementer search | T1 |
| `list_members` | Enumerate fields / properties / events of a type | T1 |
| `list_methods` | Enumerate methods of a type (paged) | T1 |
| `list_attributes` | Custom attributes for a method / type / module | T1 |
| `find_method` | Module-wide method search by regex | T1 |
| `get_method` | Method summary by `(MVID, token)` | T1 |
| `get_method_il` | Raw IL bytes for a method | T2 |
| `get_method_il_text` | Disassembled IL listing for a method | T2 |
| `scan_method_il` | Structured IL summary (outbound refs) | T2.5 |
| `decompile_method` | C# decompilation by `(MVID, token)` | T3 |
| `get_method_source` | PDB second-chance source location | T2 |
| `find_callers` | Inbound method xref | T4 |
| `find_type_references` | Inbound type xref | T4 |
| `find_string_references` | Inbound string-literal xref | T4 |
| `find_attribute_targets` | Reverse attribute index | T4 |
| `find_field_references` | Inbound field-access xref | T4 |
| `find_property_references` | Inbound property-accessor xref | T4 |

### 2.2 Attribute checklist

Every tool MUST be a static method on a `[McpServerToolType]` class and carry these
attributes. Anything missing here costs the LLM discoverability points:

```csharp
[McpServerTool(
    Name = "get_method",              // snake_case
    Title = "Get method summary",      // human, sentence case
    Destructive = false,
    ReadOnly = true,
    Idempotent = true,
    UseStructuredContent = true)]      // surfaces outputSchema
[Description(
    "Returns a method summary (signature, attributes, IL size, declaring type) " +
    "resolved deterministically from its (moduleVersionId, metadataToken) handoff " +
    "identity. The first tool to call after receiving a frame from dotnet-diagnostics-mcp.")]
public static AssemblyResult<MethodSummary> GetMethod(
    IMetadataIndex index,
    [Description("Module GUID (PE MVID) from the handoff contract. Lowercase 8-4-4-4-12.")] string moduleVersionId,
    [Description("Method metadata token (MethodDef row in single-int form).")] int metadataToken,
    CancellationToken cancellationToken)
{ … }
```

Rules:

- **`Name`**: `snake_case`, verb-first, ≤30 chars.
- **`Title`**: human sentence, ≤40 chars.
- **`Description`** on the class and on every parameter — this becomes the JSON schema
  the LLM reads. Write full sentences. Lead with what the tool returns and why the agent
  would call it.
- **`Destructive`**: `true` only if the call mutates state visible to anyone else.
  This server is read-only; everything is `false`.
- **`ReadOnly`**: `true` for queries.
- **`Idempotent`**: `true` if repeated calls with the same args return the same result.
- **`UseStructuredContent = true`** so the SDK emits an `outputSchema`. Always on.
- **DI services come first** in the parameter list (the SDK injects them); user-supplied
  args follow.

### 2.3 Naming

- Tools: `snake_case`, verb-first (`get_method`, `find_callers`, `decompile_method`).
- DTO record types: `PascalCase`, suffix with their role (`MethodSummary`, `IlScan`, not `MethodSummaryDto`).
- Error `Kind` codes: `snake_case` strings, stable forever (see [`handoff-contract.md` §4](./handoff-contract.md#4-error-shape)).

## 3. Response envelope

Every tool returns a `AssemblyResult<T>` (mirrors counterpart's `DiagnosticResult<T>`).
The shape is non-negotiable:

```csharp
public sealed record AssemblyResult<T>(
    string Summary,                              // one-sentence human-readable
    T? Data,                                     // typed payload
    IReadOnlyList<NextActionHint> Hints,         // suggested next tool calls
    AssemblyError? Error = null)                 // null = success
{
    public bool IsError => Error is not null;
}

public sealed record NextActionHint(
    string NextTool,
    string Reason,
    IReadOnlyDictionary<string, object?>? SuggestedArguments = null);

public sealed record AssemblyError(
    string Kind,                                 // stable identifier (see handoff §4)
    string Message,                              // human
    string? Detail = null);
```

Why this envelope:

- The `Summary` is what a low-context LLM reads first. Always present, even on errors.
- `Hints` describe the next call to make — the agent doesn't need to re-read
  `serverInstructions` on every turn. Put `SuggestedArguments` whenever the
  follow-up call is obvious.
- `Error.Kind` is stable; client code branches on it. `Message` is for humans.
- On error, **`Hints` must contain at least one recovery suggestion.** "Try again"
  is not a recovery — name a tool.

## 4. Server bootstrap (`Program.cs`)

The server is an ASP.NET Core app. Three pieces are mandatory:

- **`ServerInfo`** — `Name`, `Title`, `Version`, `Description`, `WebsiteUrl`. The
  description is surfaced verbatim by most clients (Claude Desktop, Cursor, Copilot
  CLI) — write it for a human picking a server to install.
- **`ServerInstructions`** — short, action-oriented operating manual injected at
  session start. Tell the LLM **how to drive an investigation**, not just what
  exists. Recommended call order, how to control cost, when to use which tool.
  Aim for ≤ 30 lines.
- **`ProtocolVersion`** — pin explicitly to the spec version we have validated against
  (currently `"2025-11-25"`, the latest the SDK 1.3.0 negotiates). The SDK negotiates
  down for older clients.

Transport: both stdio (default for `WithStdioServerTransport`) and HTTP. We use
`WithHttpTransport()` + `MapMcp("/mcp")`. Also expose `/health` for ops.

Auth: HTTP transport requires bearer-token middleware (mirror counterpart's
`BearerTokenMiddleware` and `MCP_BEARER_TOKEN` env var, ephemeral fallback at startup
with a logged warning).

## 5. Resources

Use Resources (`[McpServerResourceType]` + `[McpServerResource]`) for:

- The `MethodIdentity` handoff contract (`assembly://contract/method-identity`) —
  expose `docs/handoff-contract.md` as a resource so the agent can read it on demand
  without us spending an entire tool slot on it.
- The investigation playbook for "I got a method handle from diagnostics, now what?".
- Server-side configuration introspection ("which paths are watched?").

URI scheme: `assembly://<category>/<id>`. MimeType is usually `text/markdown` for
guides, `application/json` for machine-readable contracts.

## 6. Bounded output

Every collection-returning tool MUST accept a bound parameter:

- `topN` for sorted lists (default 50, hard max 500).
- `maxRecent` for time-bounded lists.
- `pageSize` + `cursor` if pagination is genuinely needed (the spec supports it
  on `tools/list`; we add our own on tool *responses* via cursors).

Default to the **smallest useful** bound. The agent can always ask for more by
calling again with a higher bound or with a cursor; it can't undo a 5 MB response.

## 7. Errors

- Throw an exception only for **programmer errors** (bugs). Expected failures
  (unknown MVID, missing path, malformed token) return `AssemblyResult` with
  `Error` populated. The MCP SDK turns uncaught exceptions into a generic
  protocol-level error which the agent can't act on intelligently.
- `Error.Kind` values are documented in [`handoff-contract.md` §4](./handoff-contract.md#4-error-shape)
  plus any server-only codes (`module_load_failed`, `path_not_allowed`, …).
  Once published, never repurpose a code; add a new one instead.

## 8. Logging & telemetry

- `Microsoft.Extensions.Logging` with `AddSimpleConsole` (single-line, ms timestamps),
  matches counterpart.
- Log tool entry/exit at `Information` with structured args; the tool name + duration
  + result `Kind` are the high-signal triple.
- **Never** log raw IL bytes, decompiled bodies, or assembly contents — they can
  contain anything, including secrets pickled into resource strings. Log handles and
  sizes.

## 9. CI conventions

Mirror counterpart:

- `Directory.Build.props`: `TreatWarningsAsErrors=true`, `EnforceCodeStyleInBuild=true`,
  `AnalysisLevel=latest-recommended`, `Nullable=enable`, `ImplicitUsings=enable`.
- Central Package Management (`Directory.Packages.props`). PackageReferences in csproj
  carry **no `Version`** attribute.
- `slnx` solution file (XML format, simpler than `.sln`).
- `global.json` pins the SDK with `rollForward: latestFeature`.

## 10. What this server is NOT

- Not a sandbox executor. We never `Load()` an assembly into the process AppDomain;
  metadata-only via `PEReader`.
- Not a code modifier. Read-only across the board.
- Not a decompiler-as-a-service. Decompilation is a Tier-3 capability behind a
  per-method handle; this server is about the *graph* of an assembly, not its prose.
- Not a substitute for SourceLink / PDB resolution. That composes with us via the
  handoff contract (see [`handoff-contract.md` §2.3](./handoff-contract.md#23-non-fields-deliberately-excluded)).
