namespace DotnetAssemblyMcp.Core.Identity;

/// <summary>
/// Canonical handoff identity emitted by dotnet-diagnostics-mcp for every method reference.
/// See <c>docs/handoff-contract.md §2</c>. <see cref="ModuleVersionId"/> and
/// <see cref="MetadataToken"/> together are sufficient to resolve a method; everything
/// else is for display and best-effort fallback.
/// </summary>
public sealed record MethodIdentity(
    Guid ModuleVersionId,
    int MetadataToken,
    string? ModuleName = null,
    string? ModulePath = null,
    string? TypeFullName = null,
    string? MethodName = null,
    int GenericArity = 0);
