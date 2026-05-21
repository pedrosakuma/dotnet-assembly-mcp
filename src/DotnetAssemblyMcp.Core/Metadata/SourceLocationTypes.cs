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
/// Decoded contents of a single source file embedded in the module's portable PDB
/// (<c>&lt;EmbedAllSources&gt;true&lt;/EmbedAllSources&gt;</c>). Surfaces the actual source text
/// so consumers in air-gapped or closed-source environments — where the SourceLink URL is
/// unreachable — can still read the method's source without an HTTP fetch. Only populated
/// when the PDB carries the <c>{0E8A571B-6926-466E-B4AD-8AB04611F5FE}</c> CustomDebugInformation
/// row for the document; <c>null</c> otherwise.
/// </summary>
public sealed record EmbeddedSourceText(
    string Path,
    string HashAlgorithm,
    string Hash,
    int Length,
    string Content);

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
    string? File = null,
    int? StartLine = null,
    int? EndLine = null,
    string? SourceLink = null,
    PdbKind PdbKind = PdbKind.None,
    int? PdbAge = null,
    string? Reason = null,
    EmbeddedSourceText? EmbeddedSource = null);

/// <summary>Result of <see cref="IMetadataIndex.GetMethodSource"/>.</summary>
public readonly record struct MethodSourceResult(MethodSourceLocation? Location, AssemblyError? Error)
{
    public bool IsSuccess => Location is not null;
    public static MethodSourceResult Ok(MethodSourceLocation l) => new(l, null);
    public static MethodSourceResult Fail(AssemblyError e) => new(null, e);
}
