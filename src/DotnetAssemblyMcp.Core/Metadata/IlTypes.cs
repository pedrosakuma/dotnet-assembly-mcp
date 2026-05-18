namespace DotnetAssemblyMcp.Core.Metadata;

/// <summary>
/// Tier-2 payload for <c>get_method_il</c>: the raw IL bytes of a method (hex-encoded,
/// optionally truncated by a server-side cap) plus body-level metadata (max stack,
/// exception region count, instruction count).
/// </summary>
public sealed record IlMethodBody(
    Guid ModuleVersionId,
    int MetadataToken,
    string Handle,
    int IlSize,
    int MaxStack,
    int ExceptionRegionCount,
    int InstructionCount,
    string IlHex,
    bool IlTruncated);

/// <summary>
/// Tier-2.5 payload for <c>scan_method_il</c>: the structural cross-references emitted by
/// the method's IL — outbound calls, field accesses, type uses and string literals.
/// </summary>
public sealed record IlScanResult(
    Guid ModuleVersionId,
    int MetadataToken,
    string Handle,
    int InstructionCount,
    IReadOnlyList<IlSymbolRef> Calls,
    IReadOnlyList<IlSymbolRef> Fields,
    IReadOnlyList<IlSymbolRef> Types,
    IReadOnlyList<string> Strings);

/// <summary>
/// A symbolic IL reference: the raw metadata token together with a best-effort textual
/// rendering (e.g. <c>System.Console.WriteLine(string)</c>). The rendering may be
/// <see cref="UnresolvedDisplay"/> if the token points outside the module's metadata.
/// </summary>
public sealed record IlSymbolRef(int Token, string Handle, string Display)
{
    public const string UnresolvedDisplay = "<unresolved>";
}

/// <summary>Result of <see cref="IMetadataIndex.GetIlBody"/>.</summary>
public readonly record struct IlBodyResult(IlMethodBody? Body, AssemblyError? Error)
{
    public bool IsSuccess => Body is not null;
    public static IlBodyResult Ok(IlMethodBody b) => new(b, null);
    public static IlBodyResult Fail(AssemblyError e) => new(null, e);
}

/// <summary>Result of <see cref="IMetadataIndex.ScanIl"/>.</summary>
public readonly record struct IlScanReadResult(IlScanResult? Scan, AssemblyError? Error)
{
    public bool IsSuccess => Scan is not null;
    public static IlScanReadResult Ok(IlScanResult s) => new(s, null);
    public static IlScanReadResult Fail(AssemblyError e) => new(null, e);
}
