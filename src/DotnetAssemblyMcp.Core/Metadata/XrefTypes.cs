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
