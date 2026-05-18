using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;

namespace DotnetAssemblyMcp.Server.Resources;

/// <summary>
/// Read-only resources exposing the handoff contract specification to MCP clients. Keeping
/// the contract as a Resource (not a Tool) preserves the ≤10-tool budget while letting the
/// model fetch the canonical semantics on demand.
/// </summary>
[McpServerResourceType]
public sealed class ContractResources
{
    [McpServerResource(
        UriTemplate = "assembly://contract/method-identity",
        Name = "method-identity-contract",
        Title = "MethodIdentity handoff contract",
        MimeType = "text/markdown")]
    [Description(
        "The full specification for the (moduleVersionId, metadataToken) handoff contract " +
        "shared with dotnet-diagnostics-mcp: JSON shape, field semantics, resolution algorithm, " +
        "error kinds and worked example. Read this before debugging an unexpected get_method failure.")]
    public static string MethodIdentityContract()
    {
        var asm = typeof(ContractResources).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("handoff-contract.md", StringComparison.Ordinal));
        if (name is null)
            return "# handoff-contract.md not embedded in this build.";
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
