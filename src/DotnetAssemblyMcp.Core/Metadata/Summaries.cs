namespace DotnetAssemblyMcp.Core.Metadata;

using DotnetAssemblyMcp.Core;

/// <summary>
/// Compact summary of a loaded module suitable for <c>list_assemblies</c>. Contains only
/// Tier-1 (metadata-only) data; full PE parsing happens lazily inside <see cref="IMetadataIndex"/>.
/// </summary>
public sealed record ModuleSummary(
    Guid ModuleVersionId,
    string ModuleName,
    string ModulePath,
    int MethodCount);

/// <summary>
/// Structural summary of a single method. Returned by <c>get_method</c> after resolving a
/// <see cref="Identity.MethodIdentity"/>.
/// </summary>
public sealed record MethodSummary(
    Guid ModuleVersionId,
    int MetadataToken,
    string Handle,
    string TypeFullName,
    string MethodName,
    string Signature,
    int IlSize,
    int GenericArity,
    IReadOnlyList<string> Attributes);

/// <summary>
/// Coarse-grained kind of a type definition. Mirrors the buckets a user-facing client cares
/// about; computed from <see cref="System.Reflection.TypeAttributes"/> + base-type heuristics
/// rather than reflected raw attributes so the tool surface stays stable.
/// </summary>
public enum TypeKind
{
    Class,
    Struct,
    Interface,
    Enum,
    Delegate,
}

/// <summary>
/// Tier-1 summary of a type definition. Returned by <c>list_types</c>.
/// </summary>
public sealed record TypeSummary(
    Guid ModuleVersionId,
    int MetadataToken,
    string Handle,
    string FullName,
    TypeKind Kind,
    int MethodCount,
    bool IsPublic);

/// <summary>
/// Filter / paging knobs accepted by <see cref="IMetadataIndex.ListTypes"/>. All fields are
/// optional; the defaults return up to <see cref="PageSize"/> non-synthetic types in metadata order.
/// </summary>
public sealed record ListTypesQuery(
    string? NamespacePrefix = null,
    string? NameContains = null,
    TypeKind? Kind = null,
    int? Cursor = null,
    int PageSize = ListTypesQuery.DefaultPageSize)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 500;
}

/// <summary>Paginated result of <see cref="IMetadataIndex.ListTypes"/>.</summary>
public sealed record ListTypesPage(
    Guid ModuleVersionId,
    IReadOnlyList<TypeSummary> Types,
    int? NextCursor,
    bool Truncated);

/// <summary>Result of <see cref="IMetadataIndex.ListTypes"/>.</summary>
public readonly record struct ListTypesResult(ListTypesPage? Page, AssemblyError? Error)
{
    public bool IsSuccess => Page is not null;
    public static ListTypesResult Ok(ListTypesPage p) => new(p, null);
    public static ListTypesResult Fail(AssemblyError e) => new(null, e);
}
