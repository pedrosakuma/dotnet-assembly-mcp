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
/// a sidecar topology). When neither is set <see cref="TryLoad"/> returns <c>null</c>;
/// <c>Program.cs</c> then refuses to start the HTTP transport unless the operator has
/// explicitly acknowledged the risk via <see cref="AllowUnauthenticatedEnvVar"/>.
/// </summary>
internal sealed partial class BearerTokenOptions
{
    public required string Token { get; init; }

    /// <summary>Environment variable consulted first; preferred for this server.</summary>
    public const string PrimaryEnvVar = "ASSEMBLY_MCP_BEARER_TOKEN";

    /// <summary>Cross-server fallback shared with <c>dotnet-diagnostics-mcp</c>.</summary>
    public const string FallbackEnvVar = "MCP_BEARER_TOKEN";

    /// <summary>
    /// When set to <c>1</c> / <c>true</c> the HTTP transport may start without a bearer
    /// token (e.g. local 127.0.0.1 single-node development). Default-deny otherwise — the
    /// historical behavior of silently exposing an unauthenticated <c>/mcp</c> endpoint
    /// when the env var was missing was the highest-severity finding in the v0.18.1
    /// security audit.
    /// </summary>
    public const string AllowUnauthenticatedEnvVar = "ASSEMBLY_MCP_ALLOW_UNAUTHENTICATED_HTTP";

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
            return null;
        }
        LogTokenLoaded(logger, source);
        return new BearerTokenOptions { Token = token! };
    }

    /// <summary>
    /// Returns <c>true</c> when the operator has explicitly opted into running the HTTP
    /// transport without bearer authentication. Accepts the standard truthy spellings
    /// (<c>1</c>, <c>true</c>, <c>yes</c>, <c>on</c>) case-insensitively.
    /// </summary>
    public static bool IsUnauthenticatedHttpAllowed()
    {
        var raw = Environment.GetEnvironmentVariable(AllowUnauthenticatedEnvVar);
        if (string.IsNullOrWhiteSpace(raw)) return false;
        return raw.Trim() switch
        {
            "1" => true,
            _ when string.Equals(raw.Trim(), "true", StringComparison.OrdinalIgnoreCase) => true,
            _ when string.Equals(raw.Trim(), "yes", StringComparison.OrdinalIgnoreCase) => true,
            _ when string.Equals(raw.Trim(), "on", StringComparison.OrdinalIgnoreCase) => true,
            _ => false,
        };
    }

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Bearer token loaded from {Source}.")]
    private static partial void LogTokenLoaded(ILogger logger, string source);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
        Message = "HTTP transport starting WITHOUT bearer authentication ({EnvVar}=true). Bind to 127.0.0.1 only; do not expose this port to untrusted networks.")]
    public static partial void LogUnauthenticatedHttpAllowed(ILogger logger, string envVar);
}
