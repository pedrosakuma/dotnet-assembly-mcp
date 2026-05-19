namespace DotnetAssemblyMcp.Core.Metadata;

/// <summary>Where the PDB used to resolve a method's source location came from.</summary>
public enum PdbKind
{
    /// <summary>No PDB was found for the module.</summary>
    None,
    /// <summary>Portable PDB embedded inside the PE's debug directory.</summary>
    Embedded,
    /// <summary>Sibling <c>.pdb</c> file next to the assembly on disk (portable format).</summary>
    Portable,
    /// <summary>Sibling <c>.pdb</c> file in the legacy Windows PDB format. Currently unsupported for read.</summary>
    Windows,
}

/// <summary>
/// Source-location triple for a single method. Returned by <c>get_method_source</c>. When
/// <see cref="Found"/> is <c>false</c> the other fields are best-effort metadata about why
/// the lookup failed (e.g. <see cref="PdbKind"/>=<c>None</c>, or non-null <c>PdbKind</c>
/// but no sequence points for the method).
/// </summary>
public sealed record MethodSourceLocation(
    Guid ModuleVersionId,
    int MetadataToken,
    string Handle,
    bool Found,
    string? File,
    int? StartLine,
    int? EndLine,
    string? SourceLink,
    PdbKind PdbKind,
    int? PdbAge,
    string? Reason);

/// <summary>Result of <see cref="IMetadataIndex.GetMethodSource"/>.</summary>
public readonly record struct MethodSourceResult(MethodSourceLocation? Location, AssemblyError? Error)
{
    public bool IsSuccess => Location is not null;
    public static MethodSourceResult Ok(MethodSourceLocation l) => new(l, null);
    public static MethodSourceResult Fail(AssemblyError e) => new(null, e);
}
