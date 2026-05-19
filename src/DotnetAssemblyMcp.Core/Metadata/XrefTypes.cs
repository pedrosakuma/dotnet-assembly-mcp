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
    List<OutboundCallRef> Outbound);

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
    string ParameterSignature)
{
    public bool Matches(CalleeKey key) =>
        ParameterCount == key.ParameterCount
        && GenericArity == key.GenericArity
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
    string ParameterSignature);
