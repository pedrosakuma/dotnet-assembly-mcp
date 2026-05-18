using DotnetAssemblyMcp.Core.Decompilation;
using DotnetAssemblyMcp.Core.Metadata;
using DotnetAssemblyMcp.Server.Tools;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddSimpleConsole(o =>
{
    o.IncludeScopes = true;
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss.fff ";
});

builder.Services.AddSingleton<IMetadataIndex>(_ =>
    new MetadataIndex(watchForChanges: builder.Configuration.GetValue("AssemblyMcp:WatchForChanges", defaultValue: true)));
builder.Services.AddSingleton<IDecompiler, Decompiler>();

builder.Services
    .AddMcpServer(options =>
    {
        // Advertise the latest spec version we have validated against.
        // SDK 1.3.0 supports negotiation back to 2024-11-05 if the client is older.
        options.ProtocolVersion = "2025-11-25";

        options.ServerInfo = new ModelContextProtocol.Protocol.Implementation
        {
            Name = "dotnet-assembly-mcp",
            Title = ".NET Assembly Navigator",
            Version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
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
              5. `get_method_il` — raw IL bytes (hex), max-stack, exception region count and
                 instruction count. Cheaper than decompile when you only need to confirm a
                 method's shape or count instructions.
              6. `scan_method_il` — symbolic outbound references parsed from the IL: called
                 methods, accessed fields, used types and string literals. The cheapest way
                 to build a "what does this call?" graph without decompiling.
              7. `find_callers` — Tier-4 reverse index: lists every method in the callee's
                 own module that emits a direct call to it. Lazily built and cached at
                 ~/.cache/dotnet-assembly-mcp/<mvid>.xref.

            Resolution is exact: the moduleVersionId must match a loaded module byte-for-byte.
            If it doesn't, call `load_assembly` with the correct file first — names are not
            sufficient because two builds of the same assembly have the same name but different
            MVIDs.

            For the full contract semantics (error kinds, generic instantiation, NativeAOT
            caveats), read the `assembly://contract/method-identity` resource.
            """;
    })
    .WithHttpTransport()
    .WithTools<AssemblyTools>()
    .WithResources<DotnetAssemblyMcp.Server.Resources.ContractResources>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapMcp("/mcp");

app.Run();

namespace DotnetAssemblyMcp.Server
{
    /// <summary>Marker partial used by integration tests via WebApplicationFactory&lt;Program&gt;.</summary>
    public partial class Program;
}
