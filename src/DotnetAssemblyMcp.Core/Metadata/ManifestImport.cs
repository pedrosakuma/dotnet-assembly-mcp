namespace DotnetAssemblyMcp.Core.Metadata;

/// <summary>
/// One entry of an assembly manifest sent by a producer (typically
/// <c>dotnet-diagnostics-mcp</c>) to bulk-handshake what's loaded inside a target process.
/// </summary>
/// <param name="ModuleVersionId">MVID observed inside the producer's runtime.</param>
/// <param name="Path">Filesystem path the producer saw the module loaded from.</param>
/// <param name="Name">Optional bare file name (e.g. <c>MyApp.dll</c>) for display only.</param>
public sealed record ManifestEntry(Guid ModuleVersionId, string Path, string? Name = null);

/// <summary>
/// Strategy used by <c>import_assembly_manifest</c> when processing a <see cref="ManifestEntry"/>.
/// </summary>
public enum ManifestImportMode
{
    /// <summary>
    /// Open the PE eagerly (same code path as <c>load_assembly</c>) and add it to the metadata
    /// index. Pays the full Tier-1 cost up front; useful when the agent is about to drill into
    /// every module.
    /// </summary>
    Tier1,

    /// <summary>
    /// Default. Record the <c>(mvid → path)</c> mapping in the resolver but do not open the PE.
    /// A subsequent <c>get_method</c> for that MVID will use the stored path automatically,
    /// without an explicit <c>assemblyPathHint</c>. Cheap for large manifests where the agent
    /// only touches a small fraction of modules.
    /// </summary>
    Lazy,
}

/// <summary>An entry that was loaded (or already present) in the index.</summary>
/// <param name="ModuleVersionId">MVID, confirmed to match the on-disk file.</param>
/// <param name="ModuleName">Bare file name of the loaded module.</param>
/// <param name="MethodCount">Number of <c>MethodDef</c> rows.</param>
/// <param name="Status">
/// <c>"loaded"</c> when this call opened the PE; <c>"already_loaded"</c> when the MVID was
/// already cached.
/// </param>
public sealed record ManifestImportLoaded(
    Guid ModuleVersionId,
    string ModuleName,
    int MethodCount,
    string Status);

/// <summary>An entry whose <c>(mvid → path)</c> mapping was stored without opening the PE.</summary>
public sealed record ManifestImportRegistered(Guid ModuleVersionId, string Path);

/// <summary>An entry that was rejected (mismatch, missing file, malformed input).</summary>
/// <param name="ModuleVersionId">MVID from the entry as supplied.</param>
/// <param name="Path">Path from the entry as supplied.</param>
/// <param name="Reason">
/// One of <c>"mvid_mismatch_with_path"</c>, <c>"file_not_found"</c>, <c>"invalid_argument"</c>,
/// <c>"module_load_failed"</c>.
/// </param>
/// <param name="Detail">Optional human-readable explanation.</param>
public sealed record ManifestImportSkipped(
    Guid ModuleVersionId,
    string Path,
    string Reason,
    string? Detail = null);

/// <summary>
/// Aggregate result of <c>import_assembly_manifest</c>. The three buckets partition the
/// input entries — every entry appears in exactly one of them.
/// </summary>
public sealed record ManifestImportResult(
    ManifestImportMode Mode,
    IReadOnlyList<ManifestImportLoaded> Loaded,
    IReadOnlyList<ManifestImportRegistered> Registered,
    IReadOnlyList<ManifestImportSkipped> Skipped);
