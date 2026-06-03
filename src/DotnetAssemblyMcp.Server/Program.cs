using DotnetAssemblyMcp.Core.Decompilation;
using DotnetAssemblyMcp.Core.Metadata;
using DotnetAssemblyMcp.Server;
using DotnetAssemblyMcp.Server.Auth;
using DotnetAssemblyMcp.Server.Tools;

// `--health-check` short-circuits before any host is built: probe the HTTP
// `/health` endpoint and exit 0/1. Used by systemd `ExecStartPre`, the Docker
// HEALTHCHECK fallback, and Kubernetes `exec` probes when the network shape
// makes httpGet awkward. Default URL matches our pinned port (8788); override
// via `--url=` or `ASSEMBLY_MCP_HEALTH_URL` env var.
if (args.Contains("--health-check"))
{
    var healthUrl =
        args.FirstOrDefault(a => a.StartsWith("--url=", StringComparison.Ordinal))?["--url=".Length..]
        ?? Environment.GetEnvironmentVariable("ASSEMBLY_MCP_HEALTH_URL")
        ?? "http://127.0.0.1:8788/health";
    return await HealthCheck.RunAsync(healthUrl).ConfigureAwait(false);
}

// Transport selection. `dotnet-assembly-mcp` is dual-mode:
//
//   * Default (HTTP/streamable, /mcp): for Docker / sidecar / multi-client setups.
//   * `--stdio` (or ASSEMBLY_MCP_TRANSPORT=stdio): for local tool-style installs
//     (Claude Desktop, Cursor, VS Code, Copilot CLI). Stdio MUST keep stdout
//     reserved for the JSON-RPC stream — every log goes to stderr.
var useStdio = args.Contains("--stdio")
    || string.Equals(
        Environment.GetEnvironmentVariable("ASSEMBLY_MCP_TRANSPORT"),
        "stdio",
        StringComparison.OrdinalIgnoreCase);

if (useStdio)
{
    var stdioBuilder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);

    // stdio: route logs to STDERR so they cannot corrupt the JSON-RPC stream on STDOUT.
    stdioBuilder.Logging.ClearProviders();
    stdioBuilder.Logging.AddConsole(o =>
    {
        // Route all log records to STDERR so STDOUT remains a pure JSON-RPC channel.
        o.LogToStandardErrorThreshold = Microsoft.Extensions.Logging.LogLevel.Trace;
    });
    stdioBuilder.Logging.AddSimpleConsole(o =>
    {
        o.IncludeScopes = true;
        o.SingleLine = true;
        o.TimestampFormat = "HH:mm:ss.fff ";
    });

    RegisterCoreServices(stdioBuilder.Services, stdioBuilder.Configuration);
    ConfigureMcpServer(stdioBuilder.Services)
        .WithStdioServerTransport();

    await stdioBuilder.Build().RunAsync().ConfigureAwait(false);
    return 0;
}

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddSimpleConsole(o =>
{
    o.IncludeScopes = true;
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss.fff ";
});

RegisterCoreServices(builder.Services, builder.Configuration);
ConfigureMcpServer(builder.Services)
    .WithHttpTransport();

var app = builder.Build();

// Static bearer-token gate (default-deny). When neither ASSEMBLY_MCP_BEARER_TOKEN nor
// MCP_BEARER_TOKEN is set the HTTP transport refuses to start unless the operator has
// explicitly opted in via ASSEMBLY_MCP_ALLOW_UNAUTHENTICATED_HTTP=true. /health is exempt
// from the bearer check either way (see BearerTokenMiddleware) so liveness probes work.
var bearerOptions = BearerTokenOptions.TryLoad(app.Logger);
if (bearerOptions is not null)
{
    app.UseMiddleware<BearerTokenMiddleware>(bearerOptions);
}
else if (BearerTokenOptions.IsUnauthenticatedHttpAllowed())
{
    BearerTokenOptions.LogUnauthenticatedHttpAllowed(app.Logger, BearerTokenOptions.AllowUnauthenticatedEnvVar);
}
else
{
    await Console.Error.WriteLineAsync(
        $"FATAL: HTTP transport refuses to start without a bearer token. Set " +
        $"{BearerTokenOptions.PrimaryEnvVar} (or {BearerTokenOptions.FallbackEnvVar}) to a " +
        $"shared secret, or set {BearerTokenOptions.AllowUnauthenticatedEnvVar}=true to " +
        $"explicitly opt into an unauthenticated 127.0.0.1-only deploy. Use --stdio for " +
        $"local single-client transports that don't need network auth.").ConfigureAwait(false);
    return 1;
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapMcp("/mcp");

app.Run();
return 0;

static void RegisterCoreServices(IServiceCollection services, IConfiguration configuration)
{
    // Build the engine through a DI factory delegate (not an eager instance) so the container
    // OWNS the created services and disposes them at host shutdown. Registering pre-created
    // instances via the AddSingleton(instance) overload would NOT be disposed by DI — the
    // MetadataIndex (file watchers, PE readers) and IlDisassembler (cached PEFiles) would leak.
    // allowedRoots threads the untrusted-path-hint allow-list (#150) through the factory so the
    // enforcement reaches the MetadataIndex the engine actually builds.
    services.AddSingleton(sp => DotnetAssemblyMcp.Application.AssemblyEngineFactory.Create(
        watchForChanges: configuration.GetValue("AssemblyMcp:WatchForChanges", defaultValue: true),
        allowedRoots: ReadAllowedRoots(configuration)));
    services.AddSingleton(sp => sp.GetRequiredService<DotnetAssemblyMcp.Application.AssemblyEngine>().Index);
    services.AddSingleton(sp => sp.GetRequiredService<DotnetAssemblyMcp.Application.AssemblyEngine>().Decompiler);
    services.AddSingleton(sp => sp.GetRequiredService<DotnetAssemblyMcp.Application.AssemblyEngine>().Disassembler);
}

// Untrusted-path-hint contract (#150): opt-in allow-list of trusted load roots. Sources are the
// 'AssemblyMcp:AllowedRoots' config array and the PathSeparator-delimited ASSEMBLY_MCP_ALLOWED_ROOTS
// env var. Returns null ONLY when neither source is present (enforcement disabled, back-compatible).
// If either source is present — even an explicit empty array or a value that yields zero valid
// roots — a non-null (possibly empty) list is returned so MetadataIndex fails closed (deny all)
// rather than silently reverting to allow-all.
static IReadOnlyList<string>? ReadAllowedRoots(IConfiguration configuration)
{
    var configured = new List<string>();
    var present = false;

    var fromConfig = configuration.GetSection("AssemblyMcp:AllowedRoots").Get<string[]>();
    if (fromConfig is not null) // an empty JSON array is still a deliberate "enforce" signal
    {
        present = true;
        configured.AddRange(fromConfig);
    }

    var fromEnv = Environment.GetEnvironmentVariable("ASSEMBLY_MCP_ALLOWED_ROOTS");
    if (fromEnv is not null) // presence is the enforce signal — even "" must fail closed, not open
    {
        present = true;
        configured.AddRange(fromEnv.Split(
            Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    if (!present) return null; // not configured — enforcement disabled

    foreach (var root in configured)
    {
        if (!Path.IsPathFullyQualified(root))
            Console.Error.WriteLine(
                $"[assembly-mcp] WARNING: configured allowed root '{root}' is not an absolute path and will be ignored.");
    }
    if (configured.Count == 0)
        Console.Error.WriteLine(
            "[assembly-mcp] WARNING: an allowed-root allow-list was configured but resolved to zero valid roots; all loads will be denied (fail closed).");
    return configured;
}

static Microsoft.Extensions.DependencyInjection.IMcpServerBuilder ConfigureMcpServer(IServiceCollection services) =>
    services
        .AddMcpServer(options =>
        {
            // Advertise the latest spec version we have validated against.
            // SDK 1.3.0 supports negotiation back to 2024-11-05 if the client is older.
            options.ProtocolVersion = "2025-11-25";

            options.ServerInfo = new ModelContextProtocol.Protocol.Implementation
            {
                Name = "dotnet-assembly-mcp",
                Title = ".NET Assembly Navigator",
                Version = ServerInfo.Version,
                Description =
                    "Static navigation over .NET assemblies on disk. Consumes the MethodIdentity " +
                    "handoff (moduleVersionId + metadataToken) emitted by dotnet-diagnostics-mcp " +
                    "and resolves it back to typed method, type and IL information without ever " +
                    "calling Assembly.Load on the target binary.",
                WebsiteUrl = "https://github.com/pedrosakuma/dotnet-assembly-mcp",
            };

            options.ServerInstructions =
                """
                This server inspects .NET assemblies on disk via System.Reflection.Metadata. It
                never executes the target code, never calls Assembly.Load, and never touches a
                running process. Pair it with dotnet-diagnostics-mcp: that server emits
                MethodIdentity records (moduleVersionId + metadataToken), this server resolves
                them back to typed methods.

                Recommended call order:

                  1. `load_assembly` — point at a .dll / .exe on disk; returns its moduleVersionId
                     and a count of methods. Idempotent: loading the same physical file twice is
                     a no-op.
                  2. `list_assemblies` — see what's currently loaded (mvid, path, method count).
                  3. `get_method` — given a (moduleVersionId, metadataToken) pair from a
                     diagnostic payload, returns the resolved type/method/signature/IL size.
                  4. `decompile_method` — heavier follow-up: returns the C# source of a single
                     method via ICSharpCode.Decompiler. Output is hard-capped (`maxChars`) and
                     LRU-cached, so it is safe to call back-to-back on the same hotspot.
                  5. `get_method_il` — IL reader for a method, with a `format` discriminator:
                     `raw` (default) returns hex-encoded IL bytes plus max-stack / EH-region /
                     instruction counts (cheapest); `text` returns an ildasm-style textual
                     listing via ICSharpCode.Decompiler's ReflectionDisassembler (LRU-cached);
                     `scan` returns symbolic outbound references parsed from the IL — called
                     methods, accessed fields, used types and string literals — the cheapest
                     way to build a "what does this call?" graph without decompiling.
                  6. `find_callers` — Tier-4 reverse index: lists every method that emits a
                     direct call to the callee, both intra-module (MethodDef) and cross-module
                     (MemberRef with matching assembly/type/method/signature). Lazily built and
                     cached at ~/.cache/dotnet-assembly-mcp/<mvid>.xref.

                Resolution is exact: the moduleVersionId must match a loaded module byte-for-byte.
                If it doesn't, call `load_assembly` with the correct file first — names are not
                sufficient because two builds of the same assembly have the same name but different
                MVIDs.

                Exploring an assembly with no MethodIdentity in hand (cold start — you have a
                .dll but no diagnostic payload):

                  1. `load_assembly` / `list_assemblies` — get a module loaded (by path or MVID).
                  2. `list_types` — enumerate types (filter by namespacePrefix / nameContains /
                     kind). Each row carries a `t:<mvid>:0x<token>` type handle.
                  3. `find_method` — or jump straight to a method by name regex across the whole
                     module; each hit carries an `m:<mvid>:0x<token>` method handle plus the raw
                     (moduleVersionId, metadataToken) pair.
                  4. `list_methods` / `list_members` — drill into a type's methods, fields,
                     properties and events.
                  5. Feed the handle (or the moduleVersionId + metadataToken) into `get_method`,
                     `decompile_method`, `decompile_type`, `get_method_il`, `get_method_source` or
                     `find_callers`.

                Every `(MVID, token)`-addressed tool also accepts the matching handle directly in
                its `moduleVersionId` argument (`m:` for method tools, `t:` for `decompile_type`),
                so a handle returned by one tool pastes straight into the next without splitting it
                into two fields. The canonical diagnostics handoff — passing the raw
                (moduleVersionId, metadataToken) pair — keeps working unchanged.

                Reverse-index ("who uses X?") entry points: `find_callers`,
                `find_type_references`, `find_member_references`, `find_string_references`,
                `find_attribute_targets`, and `list_derived_types`. `list_attributes` decodes the
                custom-attribute usage on any handle.

                For the full contract semantics (error kinds, generic instantiation, NativeAOT
                caveats), read the `assembly://contract/method-identity` resource.
                """;
        })
        .WithTools<AssemblyTools>()
        .WithResources<DotnetAssemblyMcp.Server.Resources.ContractResources>()
        .WithResources<DotnetAssemblyMcp.Server.Resources.AssemblyManifestResources>();

namespace DotnetAssemblyMcp.Server
{
    /// <summary>Marker partial used by integration tests via WebApplicationFactory&lt;Program&gt;.</summary>
    public partial class Program;
}
