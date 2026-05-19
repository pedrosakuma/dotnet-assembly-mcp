namespace DotnetAssemblyMcp.Core.Metadata;

using DotnetAssemblyMcp.Core;

/// <summary>
/// One method-identity input to a batch tool (<c>get_methods</c>, <c>scan_methods_il</c>,
/// <c>find_callers_batch</c>). Mirrors the single-call argument list. Per-item
/// <see cref="AssemblyPathHint"/> composes with §3.1 of the handoff contract.
/// </summary>
public sealed record MethodBatchItem(
    string ModuleVersionId,
    string MetadataToken,
    string? AssemblyPathHint = null,
    IReadOnlyList<string>? GenericTypeArguments = null,
    IReadOnlyList<string>? GenericMethodArguments = null);

/// <summary>
/// One result slot in a batch response. Either <see cref="Data"/> is populated and
/// <see cref="Ok"/> is true, or <see cref="Error"/> is populated and <see cref="Ok"/> is
/// false. The <see cref="Item"/> field echoes the original input so the agent can correlate
/// without relying on positional order.
/// </summary>
public sealed record BatchItemResult<T>(
    int Index,
    MethodBatchItem Item,
    bool Ok,
    T? Data,
    AssemblyError? Error);

/// <summary>
/// Aggregate payload for a batch tool. <see cref="Results"/> contains exactly one entry per
/// input item, in the same order. <see cref="OkCount"/> and <see cref="ErrorCount"/> are
/// pre-summed for ergonomics.
/// </summary>
public sealed record BatchResponse<T>(
    IReadOnlyList<BatchItemResult<T>> Results,
    int OkCount,
    int ErrorCount);
