using DotnetAssemblyMcp.Core.Decompilation;

namespace DotnetAssemblyMcp.Core.Metadata;

/// <summary>
/// Discriminator for the collapsed <c>get_method_il</c> tool: selects which view of a
/// method's IL is returned. <see cref="Raw"/> emits hex bytes + body metadata,
/// <see cref="Text"/> emits an ildasm-style textual dump, <see cref="Scan"/> emits the
/// structural outbound references (calls / fields / types / strings).
/// </summary>
public enum MethodIlFormat
{
    /// <summary>Raw IL bytes (hex) plus max-stack / EH-region / instruction count.</summary>
    Raw,
    /// <summary>ildasm-style textual disassembly (capped, LRU-cached).</summary>
    Text,
    /// <summary>Structural outbound references parsed from the IL.</summary>
    Scan,
}

/// <summary>
/// Single-envelope return shape for the collapsed <c>get_method_il(format=...)</c> tool.
/// Exactly one of <see cref="Raw"/>, <see cref="Text"/>, <see cref="Scan"/> is populated;
/// the others are <c>null</c>. <see cref="Format"/> echoes the requested discriminator so
/// MCP clients can dispatch without inspecting the payload fields.
/// </summary>
/// <remarks>
/// Every nullable positional parameter declares a default value. The
/// <c>ToolReturnTypeSchemaContractTests</c> contract walks reachable return types and
/// rejects any nullable record positional that does not — strict MCP clients otherwise
/// drop omitted fields and reject the response.
/// </remarks>
public sealed record MethodIlResult(
    MethodIlFormat Format,
    IlMethodBody? Raw = null,
    MethodIlText? Text = null,
    IlScanResult? Scan = null);

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
