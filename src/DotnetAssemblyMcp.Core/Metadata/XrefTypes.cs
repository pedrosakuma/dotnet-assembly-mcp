using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Identity;

namespace DotnetAssemblyMcp.Core.Metadata;

/// <summary>
/// A single caller emitted by <see cref="IMetadataIndex.FindCallers"/>. Same shape as a
/// resolved identity (MVID + MethodDef token) plus a one-line display string so MCP clients
/// can render results without a follow-up <c>get_method</c> per caller.
/// </summary>
public sealed record CallerRef(
    Guid ModuleVersionId,
    int MetadataToken,
    string Handle,
    string Display);

/// <summary>Tier-4 payload for <c>find_callers</c>.</summary>
public sealed record FindCallersResult(
    Guid CalleeModuleVersionId,
    int CalleeMetadataToken,
    string CalleeHandle,
    IReadOnlyList<CallerRef> Callers,
    int ModulesSearched,
    bool FromCache);

/// <summary>Result of <see cref="IMetadataIndex.FindCallers"/>.</summary>
public readonly record struct FindCallersReadResult(FindCallersResult? Result, AssemblyError? Error)
{
    public bool IsSuccess => Result is not null;
    public static FindCallersReadResult Ok(FindCallersResult r) => new(r, null);
    public static FindCallersReadResult Fail(AssemblyError e) => new(null, e);
}

/// <summary>
/// Per-module cached cross-reference data. <see cref="Intra"/> is the same-module callee→callers
/// map; <see cref="Outbound"/> records every call this module emits to a method defined in
/// another assembly, so cross-module <c>find_callers</c> queries can match them by signature.
/// </summary>
internal sealed record XrefData(
    Dictionary<int, List<int>> Intra,
    List<OutboundCallRef> Outbound,
    Dictionary<int, List<TypeReferenceSite>> TypeIntra,
    List<OutboundTypeRef> TypeOutbound);

/// <summary>
/// A single cross-module call site recorded while scanning a module's IL. The target is
/// described purely by symbolic name + arity so it can be matched against a callee resolved
/// in any other loaded module without holding on to that module's metadata reader.
/// </summary>
internal sealed record OutboundCallRef(
    int CallerToken,
    string TargetAssemblyName,
    string TargetTypeFullName,
    string TargetMethodName,
    int ParameterCount,
    int GenericArity,
    string ParameterSignature,
    byte CallingConvention)
{
    public bool Matches(CalleeKey key) =>
        ParameterCount == key.ParameterCount
        && GenericArity == key.GenericArity
        && CallingConvention == key.CallingConvention
        && string.Equals(TargetMethodName, key.MethodName, StringComparison.Ordinal)
        && string.Equals(TargetTypeFullName, key.TypeFullName, StringComparison.Ordinal)
        && string.Equals(TargetAssemblyName, key.AssemblyName, StringComparison.Ordinal)
        && string.Equals(ParameterSignature, key.ParameterSignature, StringComparison.Ordinal);
}

/// <summary>Signature-level identity used to match cross-module call sites against a callee.</summary>
internal readonly record struct CalleeKey(
    string AssemblyName,
    string TypeFullName,
    string MethodName,
    int ParameterCount,
    int GenericArity,
    string ParameterSignature,
    byte CallingConvention);

/// <summary>
/// How a single type-reference site uses the target type. Matches the buckets a refactor
/// impact-analysis question typically asks: "where is this type used as a field, as a
/// parameter, as a local, in an opcode (cast/box/typeof/newobj)?".
/// </summary>
public enum TypeReferenceKind
{
    FieldType,
    PropertyType,
    EventType,
    MethodParameter,
    MethodReturn,
    MethodLocal,
    IlOpcode,
}

/// <summary>
/// A single resolved type-reference site emitted by <see cref="IMetadataIndex.FindTypeReferences"/>.
/// <see cref="SiteKind"/> tells you which member table <see cref="MetadataToken"/> belongs to
/// (Field / Property / Event / Method); <see cref="ReferenceKind"/> tells you *how* that member
/// uses the target type.
/// </summary>
public sealed record TypeReferenceRef(
    Guid ModuleVersionId,
    int MetadataToken,
    MemberKind SiteKind,
    TypeReferenceKind ReferenceKind,
    string Handle,
    string Display);

/// <summary>Tier-4 payload for <c>find_type_references</c>.</summary>
public sealed record FindTypeReferencesResult(
    Guid TargetModuleVersionId,
    int TargetMetadataToken,
    string TargetHandle,
    IReadOnlyList<TypeReferenceRef> References,
    int ModulesSearched,
    bool FromCache);

/// <summary>Result of <see cref="IMetadataIndex.FindTypeReferences"/>.</summary>
public readonly record struct FindTypeReferencesReadResult(FindTypeReferencesResult? Result, AssemblyError? Error)
{
    public bool IsSuccess => Result is not null;
    public static FindTypeReferencesReadResult Ok(FindTypeReferencesResult r) => new(r, null);
    public static FindTypeReferencesReadResult Fail(AssemblyError e) => new(null, e);
}

/// <summary>A single intra-module type-reference site recorded by the xref builder.</summary>
internal readonly record struct TypeReferenceSite(
    int SiteToken,
    MemberKind SiteKind,
    TypeReferenceKind ReferenceKind);

/// <summary>
/// A single cross-module type-reference site recorded while scanning a module. The target is
/// described purely by (assembly simple name, type full name with '+'-joined nested types) so
/// it can be matched against any other loaded module's TypeDef without holding that module's
/// metadata reader.
/// </summary>
internal sealed record OutboundTypeRef(
    int SiteToken,
    MemberKind SiteKind,
    TypeReferenceKind ReferenceKind,
    string TargetAssemblyName,
    string TargetTypeFullName);

/// <summary>Signature-level identity for a cross-module type lookup.</summary>
internal readonly record struct TypeKey(string AssemblyName, string TypeFullName);

/// <summary>How <see cref="IMetadataIndex.FindStringReferences"/> matches the query against indexed user-string literals.</summary>
public enum StringMatchMode
{
    /// <summary>Exact case-sensitive equality. O(1) per module after the index is built.</summary>
    Exact,
    /// <summary>Case-sensitive substring match. O(unique-literals) per module.</summary>
    Contains,
    /// <summary>.NET regular expression. O(unique-literals) per module. Capped server-side.</summary>
    Regex,
}

/// <summary>A single resolved string-literal site emitted by <see cref="IMetadataIndex.FindStringReferences"/>.</summary>
public sealed record StringReferenceRef(
    Guid ModuleVersionId,
    int MethodMetadataToken,
    string MethodHandle,
    string MethodDisplay,
    int IlOffset,
    string Literal);

/// <summary>Tier-4 payload for <c>find_string_references</c>.</summary>
public sealed record FindStringReferencesResult(
    string Query,
    StringMatchMode MatchMode,
    IReadOnlyList<StringReferenceRef> Hits,
    int ModulesSearched,
    bool FromCache,
    bool Truncated);

/// <summary>Result of <see cref="IMetadataIndex.FindStringReferences"/>.</summary>
public readonly record struct FindStringReferencesReadResult(FindStringReferencesResult? Result, AssemblyError? Error)
{
    public bool IsSuccess => Result is not null;
    public static FindStringReferencesReadResult Ok(FindStringReferencesResult r) => new(r, null);
    public static FindStringReferencesReadResult Fail(AssemblyError e) => new(null, e);
}

/// <summary>Per-module string-literal index: literal → list of (caller MethodDef token, IL offset of the ldstr opcode).</summary>
internal sealed record StringIndexData(Dictionary<string, List<(int MethodToken, int IlOffset)>> ByLiteral);
