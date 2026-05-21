namespace DotnetAssemblyMcp.Core.Metadata;

using DotnetAssemblyMcp.Core;

/// <summary>
/// One ManifestResource row of a module (table 0x28).
/// </summary>
/// <remarks>
/// A managed assembly carries three flavours of resource, and exactly one of
/// <see cref="LinkedFileName"/> / <see cref="LinkedAssemblyName"/> / <see cref="Offset"/> + <see cref="Length"/>
/// is meaningful depending on which:
/// <list type="bullet">
///   <item><b>In-PE</b> — <see cref="Implementation"/> is the nil row. The bytes live inside this PE's
///   <c>.mresources</c> section starting at <see cref="Offset"/>; <see cref="Length"/> is decoded from
///   the 4-byte length prefix at that offset. <see cref="LinkedFileName"/> and <see cref="LinkedAssemblyName"/>
///   are both <c>null</c>.</item>
///   <item><b>Linked external file</b> — <see cref="Implementation"/> is a <c>File</c> row (multi-module
///   assembly; rare modern usage). <see cref="LinkedFileName"/> carries the file name; <see cref="Offset"/>
///   and <see cref="Length"/> are <c>null</c>.</item>
///   <item><b>Forwarded to satellite assembly</b> — <see cref="Implementation"/> is an <c>AssemblyRef</c>
///   (typical localized <c>*.resources.dll</c> scenario). <see cref="LinkedAssemblyName"/> carries the
///   target assembly simple name; <see cref="Offset"/> and <see cref="Length"/> are <c>null</c>.</item>
/// </list>
/// Reading the resource bytes themselves is intentionally out of scope for this tool — keep the surface
/// metadata-only. A future Tier-3 <c>get_resource_bytes</c> can opt-in to materialize them.
/// </remarks>
/// <param name="MetadataToken">ManifestResource metadata token (table 0x28).</param>
/// <param name="Name">Manifest resource name (e.g. <c>MyApp.Strings.resources</c>).</param>
/// <param name="IsPublic">True when the resource has the <c>Public</c> visibility flag; false when <c>Private</c>.</param>
/// <param name="Implementation">Classifier — <c>InPe</c> / <c>LinkedFile</c> / <c>ForwardedToAssembly</c>.</param>
/// <param name="Offset">Byte offset into the PE's <c>.mresources</c> section when <see cref="Implementation"/> is <c>InPe</c>; otherwise <c>null</c>.</param>
/// <param name="Length">Decoded payload length in bytes when <see cref="Implementation"/> is <c>InPe</c>; otherwise <c>null</c>.</param>
/// <param name="LinkedFileName">Linked-file name when <see cref="Implementation"/> is <c>LinkedFile</c>; otherwise <c>null</c>.</param>
/// <param name="LinkedAssemblyName">Forwarded target assembly simple name when <see cref="Implementation"/> is <c>ForwardedToAssembly</c>; otherwise <c>null</c>.</param>
public sealed record ResourceSummary(
    int MetadataToken,
    string Name,
    bool IsPublic,
    ResourceImplementationKind Implementation,
    long? Offset = null,
    int? Length = null,
    string? LinkedFileName = null,
    string? LinkedAssemblyName = null);

/// <summary>
/// Classifier for the <c>Implementation</c> column of a <see cref="ResourceSummary"/>.
/// </summary>
public enum ResourceImplementationKind
{
    /// <summary>Resource bytes live in this PE's <c>.mresources</c> section.</summary>
    InPe = 0,
    /// <summary>Resource is in a separate file alongside this PE (multi-module assembly).</summary>
    LinkedFile = 1,
    /// <summary>Resource is forwarded to another assembly (typical satellite-assembly localization).</summary>
    ForwardedToAssembly = 2,
}

/// <summary>Result of <see cref="IMetadataIndex.ListResources"/>.</summary>
public sealed record ListResourcesPage(
    Guid ModuleVersionId,
    IReadOnlyList<ResourceSummary> Resources);

/// <summary>Read-result wrapper for <see cref="IMetadataIndex.ListResources"/>.</summary>
public readonly record struct ListResourcesResult(ListResourcesPage? Page, AssemblyError? Error)
{
    public bool IsSuccess => Page is not null;
    public static ListResourcesResult Ok(ListResourcesPage p) => new(p, null);
    public static ListResourcesResult Fail(AssemblyError e) => new(null, e);
}
