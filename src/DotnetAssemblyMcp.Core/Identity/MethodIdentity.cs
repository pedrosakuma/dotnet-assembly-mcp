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
    int GenericArity = 0,
    // Optional closed type-level generic arguments (declaration order) — see docs/handoff-contract.md §3.5.
    IReadOnlyList<DotnetAssemblyMcp.Core.Metadata.GenericTypeName>? TypeGenericArguments = null,
    // Optional closed method-level generic arguments (declaration order).
    IReadOnlyList<DotnetAssemblyMcp.Core.Metadata.GenericTypeName>? MethodGenericArguments = null,
    // Optional fast-path: a (MVID, token) pointing at a MethodSpec row in the caller's module
    // that encodes the closed instantiation natively (§3.5). When supplied alongside the string
    // args, the two are cross-checked and a mismatch yields GenericInstantiationMismatch.
    MethodSpecHandle? MethodSpec = null);

/// <summary>
/// §3.5 fast-path pointer: a <c>MethodSpec</c> row (table 0x2B) inside a caller's module
/// that natively encodes the closed instantiation of a generic call site.
/// </summary>
public sealed record MethodSpecHandle(Guid ModuleVersionId, int MetadataToken);
