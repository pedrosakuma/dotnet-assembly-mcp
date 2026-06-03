using System.ComponentModel;
using DotnetAssemblyMcp.Core;
using DotnetAssemblyMcp.Core.Decompilation;
using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Handles;
using DotnetAssemblyMcp.Core.Identity;
using DotnetAssemblyMcp.Core.Metadata;
using DotnetAssemblyMcp.Application;
using ModelContextProtocol.Server;

namespace DotnetAssemblyMcp.Server.Tools;

public sealed partial class AssemblyTools
{
    [McpServerTool(
        Name = "load_assembly",
        Title = "Load a .NET assembly from disk",
        Destructive = false,
        ReadOnly = false,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(AssemblyToolDescriptions.LoadAssembly_Summary)]
    public static AssemblyResult<ModuleSummary> LoadAssembly(
        IMetadataIndex index,
        [Description(AssemblyToolDescriptions.LoadAssembly_Path)] string path)
        => AssemblyOperations.LoadAssembly(index, path);

    [McpServerTool(
        Name = "list_assemblies",
        Title = "List currently loaded assemblies",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(AssemblyToolDescriptions.ListAssemblies_Summary)]
    public static AssemblyResult<IReadOnlyList<ModuleSummary>> ListAssemblies(IMetadataIndex index)
        => AssemblyOperations.ListAssemblies(index);

    [McpServerTool(
        Name = "import_assembly_manifest",
        Title = "Bulk-import an (mvid, path) manifest from a producer",
        Destructive = false,
        ReadOnly = false,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(AssemblyToolDescriptions.ImportAssemblyManifest_Summary)]
    public static AssemblyResult<ManifestImportResult> ImportAssemblyManifest(
        IMetadataIndex index,
        [Description(AssemblyToolDescriptions.ImportAssemblyManifest_Entries)] IReadOnlyList<ManifestEntry> entries,
        [Description(AssemblyToolDescriptions.ImportAssemblyManifest_Mode)] ManifestImportMode mode = ManifestImportMode.Lazy)
        => AssemblyOperations.ImportAssemblyManifest(index, entries, mode);
}
