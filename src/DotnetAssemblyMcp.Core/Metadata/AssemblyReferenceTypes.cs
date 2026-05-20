namespace DotnetAssemblyMcp.Core.Metadata;

using DotnetAssemblyMcp.Core;

/// <summary>
/// One AssemblyRef row of a module. <see cref="Handle"/> is the canonical assembly handle
/// (<c>a:&lt;containingMvid&gt;</c>) of the *containing* module — consumers pivot via
/// <see cref="Name"/> + <see cref="Version"/> to load the referenced assembly from disk via
/// <c>load_assembly</c>. The <see cref="MetadataToken"/> field carries the AssemblyRef row
/// id for ordering / re-fetching within the containing module.
/// </summary>
/// <param name="MetadataToken">AssemblyRef metadata token (table 0x23).</param>
/// <param name="Handle">Canonical handle of the containing module (<c>a:&lt;containingMvid&gt;</c>).</param>
/// <param name="Name">Simple name of the referenced assembly (e.g. <c>System.Text.Json</c>).</param>
/// <param name="Version">Four-part version string (<c>major.minor.build.revision</c>).</param>
/// <param name="Culture">Culture name, or <c>null</c> when the row carries the neutral culture.</param>
/// <param name="PublicKeyTokenHex">Hex-encoded 8-byte public key token, or <c>null</c> when unsigned.</param>
/// <param name="Flags">Raw AssemblyFlags value as exposed by <c>System.Reflection</c>.</param>
public sealed record AssemblyReferenceSummary(
    int MetadataToken,
    string Handle,
    string Name,
    string Version,
    string? Culture = null,
    string? PublicKeyTokenHex = null,
    int Flags = 0);

/// <summary>Result of <see cref="IMetadataIndex.ListAssemblyReferences"/>.</summary>
public sealed record ListAssemblyReferencesPage(
    Guid ModuleVersionId,
    IReadOnlyList<AssemblyReferenceSummary> References);

/// <summary>Read-result wrapper for <see cref="IMetadataIndex.ListAssemblyReferences"/>.</summary>
public readonly record struct ListAssemblyReferencesResult(ListAssemblyReferencesPage? Page, AssemblyError? Error)
{
    public bool IsSuccess => Page is not null;
    public static ListAssemblyReferencesResult Ok(ListAssemblyReferencesPage p) => new(p, null);
    public static ListAssemblyReferencesResult Fail(AssemblyError e) => new(null, e);
}
