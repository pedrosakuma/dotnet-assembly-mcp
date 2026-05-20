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
/// Coarse classification of an <see cref="IlSymbolRef"/>'s target. Lets consumers
/// disambiguate cases where the wire handle alone is ambiguous — notably
/// <c>MemberReference</c> rows (table 0x0A), which can carry either a method or a
/// field signature. See issue #86.
/// </summary>
public enum IlSymbolKind
{
    /// <summary>Classification unavailable (malformed token / unknown table).</summary>
    Unknown = 0,

    /// <summary>MethodDefinition (metadata table 0x06) — same-module method.</summary>
    MethodDef,

    /// <summary>FieldDefinition (metadata table 0x04) — same-module field.</summary>
    FieldDef,

    /// <summary>TypeDefinition (metadata table 0x02) — same-module type.</summary>
    TypeDef,

    /// <summary>TypeReference (metadata table 0x01) — cross-module type.</summary>
    TypeRef,

    /// <summary>TypeSpecification (metadata table 0x1B) — generic / array / pointer composite.</summary>
    TypeSpec,

    /// <summary>MethodSpecification (metadata table 0x2B) — closed generic-method instantiation.</summary>
    MethodSpec,

    /// <summary>MemberReference (table 0x0A) whose signature is a method signature.</summary>
    MethodMemberRef,

    /// <summary>MemberReference (table 0x0A) whose signature is a field signature.</summary>
    FieldMemberRef,
}

/// <summary>
/// A symbolic IL reference: the raw metadata token together with a best-effort textual
/// rendering (e.g. <c>System.Console.WriteLine(string)</c>). The rendering may be
/// <see cref="UnresolvedDisplay"/> if the token points outside the module's metadata.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Kind"/> reflects the underlying metadata table; it is the source of truth
/// when <see cref="Handle"/> is a synthetic carrier (e.g. a field <c>MemberReference</c>
/// rendered with the <c>m:</c> prefix because the <c>f:</c> grammar formally addresses
/// <c>FieldDefinition</c> only — see issue #86).
/// </para>
/// </remarks>
public sealed record IlSymbolRef(
    int Token,
    string Handle,
    string Display,
    IlSymbolKind Kind = IlSymbolKind.Unknown)
{
    public const string UnresolvedDisplay = "<unresolved>";

    // Binary-compat overload: preserves the 3-arg constructor signature that shipped before
    // the Kind discriminator was added (issue #86). Existing compiled consumers calling
    // IlSymbolRef(int, string, string) keep working without recompilation.
    public IlSymbolRef(int token, string handle, string display)
        : this(token, handle, display, IlSymbolKind.Unknown) { }

    // Binary-compat overload: preserves the 3-out Deconstruct signature that records
    // synthesise from a 3-param primary constructor (issue #86). Existing consumers using
    // `var (token, handle, display) = symbol;` keep working without recompilation.
    public void Deconstruct(out int token, out string handle, out string display)
    {
        token = Token;
        handle = Handle;
        display = Display;
    }
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
