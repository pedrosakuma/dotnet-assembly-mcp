using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace DotnetAssemblyMcp.Server.Auth;

/// <summary>
/// Static bearer-token gate for the HTTP transport. Mirrors the shape of
/// <c>dotnet-diagnostics-mcp</c>'s <c>BearerTokenMiddleware</c> so operators can wire both
/// servers behind the same expectation: <c>Authorization: Bearer &lt;token&gt;</c>, fixed-time
/// comparison, <c>/health</c> always exempt.
/// </summary>
/// <remarks>
/// The middleware is opt-in: <see cref="BearerTokenOptions.TryLoad"/> returns <c>null</c>
/// when neither <c>ASSEMBLY_MCP_BEARER_TOKEN</c> nor <c>MCP_BEARER_TOKEN</c> is set, and
/// <c>Program.cs</c> only wires the middleware when a token is present. That preserves the
/// back-compat path for local stdio + 127.0.0.1 single-node deployments called out by the
/// k8s topology doc. Out of scope here: OAuth/OIDC, mTLS, per-tool ACLs.
/// </remarks>
internal sealed class BearerTokenMiddleware
{
    private readonly RequestDelegate _next;
    private readonly byte[] _expectedTokenBytes;

    public BearerTokenMiddleware(RequestDelegate next, BearerTokenOptions options)
    {
        _next = next;
        _expectedTokenBytes = Encoding.UTF8.GetBytes(options.Token);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Health endpoint is always exempt so liveness probes don't need the token.
        if (context.Request.Path.StartsWithSegments("/health"))
        {
            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue("Authorization", out StringValues header) ||
            header.Count == 0 ||
            !TryExtractToken(header[0], out var presented) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(presented),
                _expectedTokenBytes))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await context.Response.WriteAsync("Unauthorized");
            return;
        }

        await _next(context);
    }

    private static bool TryExtractToken(string? header, out string token)
    {
        token = string.Empty;
        if (string.IsNullOrWhiteSpace(header)) return false;
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        token = header[prefix.Length..].Trim();
        return token.Length > 0;
    }
}

/// <summary>
/// Resolved bearer-token configuration for <see cref="BearerTokenMiddleware"/>. Loaded from
/// the <c>ASSEMBLY_MCP_BEARER_TOKEN</c> environment variable (falling back to
/// <c>MCP_BEARER_TOKEN</c> so operators can share a single token across both MCP servers in
/// a sidecar topology). Returns <c>null</c> when neither is set — the caller is expected to
/// skip wiring the middleware so the HTTP transport remains unauthenticated, matching the
/// back-compat policy spelled out in <c>deploy/k8s/README.md</c>.
/// </summary>
internal sealed partial class BearerTokenOptions
{
    public required string Token { get; init; }

    /// <summary>Environment variable consulted first; preferred for this server.</summary>
    public const string PrimaryEnvVar = "ASSEMBLY_MCP_BEARER_TOKEN";

    /// <summary>Cross-server fallback shared with <c>dotnet-diagnostics-mcp</c>.</summary>
    public const string FallbackEnvVar = "MCP_BEARER_TOKEN";

    public static BearerTokenOptions? TryLoad(ILogger logger)
    {
        var token = Environment.GetEnvironmentVariable(PrimaryEnvVar);
        var source = PrimaryEnvVar;
        if (string.IsNullOrWhiteSpace(token))
        {
            token = Environment.GetEnvironmentVariable(FallbackEnvVar);
            source = FallbackEnvVar;
        }
        if (string.IsNullOrWhiteSpace(token))
        {
            LogUnauthenticated(logger, PrimaryEnvVar, FallbackEnvVar);
            return null;
        }
        LogTokenLoaded(logger, source);
        return new BearerTokenOptions { Token = token! };
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "{Primary} not set (fallback {Fallback} also empty) — HTTP transport will run unauthenticated. Set one to require Bearer auth.")]
    private static partial void LogUnauthenticated(ILogger logger, string primary, string fallback);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Bearer token loaded from {Source}.")]
    private static partial void LogTokenLoaded(ILogger logger, string source);
}
