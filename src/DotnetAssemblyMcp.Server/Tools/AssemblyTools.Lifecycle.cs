using System.ComponentModel;
using DotnetAssemblyMcp.Core;
using DotnetAssemblyMcp.Core.Decompilation;
using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Handles;
using DotnetAssemblyMcp.Core.Identity;
using DotnetAssemblyMcp.Core.Metadata;
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
    {
        var result = index.Load(path);
        if (!result.IsSuccess)
        {
            return AssemblyResult.Fail<ModuleSummary>(
                $"Failed to load '{path}': {result.Error!.Message}",
                result.Error,
                AssemblyErrorRecovery.For(result.Error));
        }

        var m = result.Module!;
        return AssemblyResult.Ok(
            m,
            $"Loaded {m.ModuleName} (mvid={m.ModuleVersionId:D}, {m.MethodCount} methods).",
            new NextActionHint(
                "get_method",
                "Resolve a MethodIdentity from a diagnostic payload against this module.",
                new Dictionary<string, object?>
                {
                    ["moduleVersionId"] = m.ModuleVersionId.ToString("D"),
                    ["metadataToken"] = 0x06000001,
                }));
    }

    [McpServerTool(
        Name = "list_assemblies",
        Title = "List currently loaded assemblies",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(AssemblyToolDescriptions.ListAssemblies_Summary)]
    public static AssemblyResult<IReadOnlyList<ModuleSummary>> ListAssemblies(IMetadataIndex index)
    {
        var modules = index.List();
        if (modules.Count == 0)
        {
            return AssemblyResult.Ok(
                modules,
                "No assemblies loaded yet. Call load_assembly with a path to begin.",
                new NextActionHint("load_assembly", "Load the target assembly from disk before resolving identities."));
        }

        var preview = string.Join(", ", modules.Take(3).Select(m => m.ModuleName));
        return AssemblyResult.Ok(
            modules,
            $"{modules.Count} assembly(ies) loaded: {preview}{(modules.Count > 3 ? ", …" : "")}.",
            new NextActionHint("get_method", "Resolve a MethodIdentity emitted by dotnet-diagnostics-mcp against a loaded module."));
    }

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
    {
        if (entries is null || entries.Count == 0)
        {
            var empty = new ManifestImportResult(mode, Array.Empty<ManifestImportLoaded>(),
                Array.Empty<ManifestImportRegistered>(), Array.Empty<ManifestImportSkipped>());
            return AssemblyResult.Ok(
                empty,
                "Manifest is empty — nothing to import.",
                new NextActionHint("list_assemblies", "Inspect the modules currently loaded."));
        }

        var loaded = new List<ManifestImportLoaded>();
        var registered = new List<ManifestImportRegistered>();
        var skipped = new List<ManifestImportSkipped>();

        var alreadyLoaded = new HashSet<Guid>();
        foreach (var m in index.List()) alreadyLoaded.Add(m.ModuleVersionId);

        foreach (var entry in entries)
        {
            if (entry is null)
            {
                skipped.Add(new ManifestImportSkipped(Guid.Empty, string.Empty,
                    ErrorKinds.InvalidArgument, "entry is null."));
                continue;
            }
            if (entry.ModuleVersionId == Guid.Empty || string.IsNullOrWhiteSpace(entry.Path))
            {
                skipped.Add(new ManifestImportSkipped(entry.ModuleVersionId, entry.Path ?? string.Empty,
                    ErrorKinds.InvalidArgument, "moduleVersionId and path are required."));
                continue;
            }

            if (alreadyLoaded.Contains(entry.ModuleVersionId))
            {
                var existing = index.List().First(m => m.ModuleVersionId == entry.ModuleVersionId);
                loaded.Add(new ManifestImportLoaded(
                    existing.ModuleVersionId, existing.ModuleName, existing.MethodCount, "already_loaded"));
                continue;
            }

            if (!File.Exists(entry.Path))
            {
                skipped.Add(new ManifestImportSkipped(entry.ModuleVersionId, entry.Path,
                    "file_not_found", $"no file exists at '{entry.Path}'."));
                continue;
            }

            var probe = index.Probe(entry.Path);
            if (!probe.IsSuccess)
            {
                skipped.Add(new ManifestImportSkipped(entry.ModuleVersionId, entry.Path,
                    probe.Error!.Kind, probe.Error.Message));
                continue;
            }
            if (probe.Mvid != entry.ModuleVersionId)
            {
                skipped.Add(new ManifestImportSkipped(entry.ModuleVersionId, entry.Path,
                    "mvid_mismatch_with_path",
                    $"file at '{entry.Path}' has MVID {probe.Mvid:D} but the manifest claims {entry.ModuleVersionId:D}."));
                continue;
            }

            if (mode == ManifestImportMode.Tier1)
            {
                var load = index.Load(entry.Path);
                if (!load.IsSuccess)
                {
                    skipped.Add(new ManifestImportSkipped(entry.ModuleVersionId, entry.Path,
                        load.Error!.Kind, load.Error.Message));
                    continue;
                }
                alreadyLoaded.Add(load.Module!.ModuleVersionId);
                loaded.Add(new ManifestImportLoaded(
                    load.Module.ModuleVersionId, load.Module.ModuleName, load.Module.MethodCount, "loaded"));
            }
            else
            {
                index.RegisterPathHint(entry.ModuleVersionId, entry.Path);
                index.WatchPath(entry.Path);
                registered.Add(new ManifestImportRegistered(entry.ModuleVersionId, Path.GetFullPath(entry.Path)));
            }
        }

        var result = new ManifestImportResult(mode, loaded, registered, skipped);
        var summary = mode switch
        {
            ManifestImportMode.Tier1 =>
                $"Imported {loaded.Count} module(s) (tier1); {skipped.Count} skipped.",
            _ =>
                $"Registered {registered.Count} (mvid→path) hint(s); {loaded.Count} already loaded, {skipped.Count} skipped.",
        };

        NextActionHint next = skipped.Count > 0
            ? new NextActionHint(
                "list_assemblies",
                $"{skipped.Count} entry(ies) were skipped — inspect their 'reason' field and re-issue corrected entries.")
            : new NextActionHint(
                "get_method",
                "Resolve a MethodIdentity against an imported module — assemblyPathHint is no longer required for lazy-registered MVIDs.");

        return AssemblyResult.Ok(result, summary, next);
    }
}
