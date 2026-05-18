using DotnetAssemblyMcp.Core.Metadata;
using DotnetAssemblyMcp.Server.Tools;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddSimpleConsole(o =>
{
    o.IncludeScopes = true;
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss.fff ";
});

builder.Services.AddSingleton<IMetadataIndex, MetadataIndex>();

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
