# MCP 2026 Draft Migration Assessment

**Issue**: #166 · **Assessment date**: 2026-08-29
**Companion doc**: [`dotnet-diagnostics`'s assessment](https://github.com/pedrosakuma/dotnet-diagnostics/blob/main/docs/research/mcp-2026-draft-migration.md)
(issue [pedrosakuma/dotnet-diagnostics#546](https://github.com/pedrosakuma/dotnet-diagnostics/issues/546)) —
that repo already **shipped** its production migration in v0.24.0 (2026-08-19), bumping
`ModelContextProtocol`/`ModelContextProtocol.AspNetCore` `1.4.0` → `2.2.0`. This document only
records what is *different* or *repo-specific* for `dotnet-assembly-mcp`; read the companion doc
for the full per-SEP technical inventory (SEP-2567/2575/2322) and the general SDK API-delta
findings — they are reused here, not re-derived.

**Status**: assessment/spike complete. **Recommendation: go** — narrower scope than the companion
repo, and NuGet already lists `ModelContextProtocol`/`ModelContextProtocol.AspNetCore` `2.2.0` as
stable (confirmed live against `api.nuget.org` on 2026-08-29). A follow-up issue for the actual
package bump is warranted; see [Follow-up](#follow-up).

## 1. Confirmed: no session/elicitation/sampling/roots dependency

A source grep (excluding `bin/`/`obj/` build output) across `src/` and `tests/` for `SessionId`,
`ElicitAsync`, `Sampling`, `RootsAsync`, `WithPrompts`, `WithResources`, `ClientCapabilities`
returns **zero matches** in this repo's own code. The only relevant hit anywhere is
`options.ProtocolVersion = "2025-11-25"` in `src/DotnetAssemblyMcp.Server/Program.cs:166`.

This repo's MCP surface is narrower than `dotnet-diagnostics-mcp`'s:

- **Registration** is a single `.WithTools<AssemblyTools>()` call
  (`src/DotnetAssemblyMcp.Server/Program.cs:247`) — no `.WithPrompts<...>()`, no
  `.WithResources<...>()`, no custom `server/discover` handler.
- **No handle/session-binding store of any kind** — unlike `dotnet-diagnostics-mcp`'s
  `IDiagnosticHandleStore` (already SEP-2567-shaped) *and* its orchestrator
  `InvestigationHandle`/`IInvestigationSessionBinder` (the repo's actual migration blocker, fixed
  by #554/PR #559 there). This repo has no orchestrator/proxy/attach concept at all — every tool
  (`load_assembly`, `get_method`, `decompile_method`, `find_callers`, etc.) takes its own
  self-contained arguments (path, mvid, metadata token, method handle-in-argument) and returns a
  result in the same call. There is nothing to redesign for SEP-2567 here.
- **No native elicitation/sampling/roots usage** — nothing analogous to
  `dotnet-diagnostics-mcp`'s `DumpApprovalElicitation` / `collect_process_dump` MRTR rewrite.
  Every tool in this repo is a single-round read of on-disk metadata; there is no
  destructive-action-needing-approval workflow. **SEP-2322 requires no code changes here.**
- **Auth is ordinary ASP.NET Core middleware** (`BearerTokenMiddleware`), keyed off the
  `Authorization` header, not the handshake or session lifecycle — same shape as the companion
  repo, same conclusion: unaffected by SEP-2575's handshake changes.

**Conclusion:** this repo's migration surface reduces to an SDK/package version bump plus the
generic wire/behavior deltas below — no architecture change, no MRTR rewrite, no handle-store
redesign.

## 2. Live spike against the stable 2.2.0 SDK

A throwaway ASP.NET Core app (outside this repo, deleted after the spike) was built with:

- `ModelContextProtocol` `2.2.0` (confirmed latest stable on NuGet as of 2026-08-29 — the
  registry also lists `1.4.0`/`1.4.1`, `2.0.0-preview.1..3`, `2.0.0-rc.1..2`, `2.0.0`, `2.1.0`)
- `ModelContextProtocol.AspNetCore` `2.2.0`
- SDK `10.0.201` (matches this repo's `global.json`)

It mirrored this repo's exact registration shape (`AddMcpServer(options => { options.ProtocolVersion;
options.ServerInfo; options.ServerInstructions; })`, `.WithHttpTransport()`, `.WithTools<T>()`,
`app.MapMcp("/mcp")`) and one tool with `RequestContext<CallToolRequestParams>` plus a DI-injected
service parameter, matching `AssemblyTools.Lifecycle.cs`'s `LoadAssembly(IMetadataIndex index, ...)`
pattern.

**Findings:**

- **Compiles unchanged.** Every pattern above compiles against `2.2.0` with zero source changes —
  consistent with the companion repo's `2.0.0-preview.1` finding that no broad mechanical rewrite
  is needed for ordinary tool registration.
- **`initialize` still works and the server is already stateless.** A raw HTTP round trip against
  the running spike server showed: `initialize` succeeds and returns the classic
  `protocolVersion`/`capabilities`/`serverInfo` shape; **no `Mcp-Session-Id` response header was
  ever issued**; a `tools/call` sent cold (no prior `initialize` in the same connection, no session
  header at all) succeeded immediately. This SDK version's Streamable HTTP transport does not
  depend on protocol-level sessions today, which matches this repo's own stateless design (each
  HTTP POST is already handled independently — see `app.MapMcp("/mcp")`, no session middleware).
- **`server/discover` exists but is not yet mandatory in this SDK version.** Calling it without
  per-request `_meta` protocol-version metadata returns a structured JSON-RPC error
  (`-32602`, "requires per-request metadata declaring a supported protocol version") rather than
  succeeding — i.e., the method is present but the *2026-07-28 revision's* mandatory-`_meta`
  enforcement is not fully switched on in `2.2.0`'s default configuration. `tools/list` and
  `tools/call` both still respond with the pre-MRTR result shape (no `resultType` field observed),
  confirming `2.2.0` still targets the pre-2026-07-28 wire contract by default, same as the
  companion repo's `1.4.0` baseline — the `resultType`/mandatory-discover changes are validated
  only against the SDK's `preview`/`rc` line in the companion repo's spike, not yet in a stable
  release.

## 3. What changes anyway (regardless of the low session/elicitation exposure)

- `options.ProtocolVersion = "2025-11-25"` (`Program.cs:166`) is a stale literal once a real
  migration lands — same finding as the companion doc for its
  `DiagnosticServiceRegistration:260` equivalent.
- Comments/docs that describe cancellation via `notifications/cancelled` for the HTTP transport
  should be checked; none were found referencing that in this repo's `Program.cs` or `Tools/`
  during this spike, but re-check during the real migration in case new code has been added.
- `docs/mcp-conventions.md` and `docs/cli-usage.md` should be spot-checked for any protocol-version
  or handshake language that assumes `2025-11-25` semantics once the bump happens.

## Follow-up

Given the above, the real package bump for this repo is materially lower-risk and lower-effort
than `dotnet-diagnostics-mcp`'s (no orchestrator redesign, no MRTR rewrite). Recommend opening a
follow-up implementation issue scoped to:

1. Bump `ModelContextProtocol`/`ModelContextProtocol.AspNetCore` to the then-current stable in
   `Directory.Packages.props`.
2. Update `options.ProtocolVersion` to the then-current spec revision.
3. Re-run the full test suite and a manual `tools/list`/`tools/call` smoke test over both
   transports (stdio and HTTP).
4. Doc pass over `docs/mcp-conventions.md` / `docs/cli-usage.md` for stale protocol-version
   references.

No architecture change is anticipated; this is a version-bump-and-verify issue, not a redesign.
