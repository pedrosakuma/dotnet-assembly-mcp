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
    int? NextCursor = null,
    bool Truncated = false);

/// <summary>Result of <see cref="IMetadataIndex.ListTypes"/>.</summary>
public readonly record struct ListTypesResult(ListTypesPage? Page, AssemblyError? Error)
{
    public bool IsSuccess => Page is not null;
    public static ListTypesResult Ok(ListTypesPage p) => new(p, null);
    public static ListTypesResult Fail(AssemblyError e) => new(null, e);
}

/// <summary>
/// Filter / paging knobs accepted by <see cref="IMetadataIndex.ListMethods"/>. All fields
/// except the type identity are optional; the defaults return up to <see cref="PageSize"/>
/// methods of the type in metadata order.
/// </summary>
public sealed record ListMethodsQuery(
    string? NamePattern = null,
    int? Cursor = null,
    int PageSize = ListMethodsQuery.DefaultPageSize)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 500;
}

/// <summary>Paginated result of <see cref="IMetadataIndex.ListMethods"/>.</summary>
public sealed record ListMethodsPage(
    Guid ModuleVersionId,
    int TypeMetadataToken,
    string TypeFullName,
    IReadOnlyList<MethodSummary> Methods,
    int? NextCursor = null,
    bool Truncated = false);

/// <summary>Result of <see cref="IMetadataIndex.ListMethods"/>.</summary>
public readonly record struct ListMethodsResult(ListMethodsPage? Page, AssemblyError? Error)
{
    public bool IsSuccess => Page is not null;
    public static ListMethodsResult Ok(ListMethodsPage p) => new(p, null);
    public static ListMethodsResult Fail(AssemblyError e) => new(null, e);
}

/// <summary>
/// Filter / paging knobs accepted by <see cref="IMetadataIndex.FindMethod"/>. The name pattern
/// is treated as a regular expression matched against the method's short name (not the full
/// signature). <see cref="SignatureContains"/> applies a case-insensitive substring filter on
/// the decoded signature ('void NS.Type.Method(int)' format).
/// </summary>
public sealed record FindMethodQuery(
    string NamePattern,
    string? SignatureContains = null,
    int? Cursor = null,
    int PageSize = FindMethodQuery.DefaultPageSize)
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 200;
}

/// <summary>A single hit returned by <see cref="IMetadataIndex.FindMethod"/>.</summary>
public sealed record MethodMatch(
    Guid ModuleVersionId,
    int MetadataToken,
    string Handle,
    string TypeFullName,
    string MethodName,
    string Signature);

/// <summary>Paginated result of <see cref="IMetadataIndex.FindMethod"/>.</summary>
public sealed record FindMethodPage(
    Guid ModuleVersionId,
    string NamePattern,
    IReadOnlyList<MethodMatch> Matches,
    int? NextCursor = null,
    bool Truncated = false);

/// <summary>Result of <see cref="IMetadataIndex.FindMethod"/>.</summary>
public readonly record struct FindMethodResult(FindMethodPage? Page, AssemblyError? Error)
{
    public bool IsSuccess => Page is not null;
    public static FindMethodResult Ok(FindMethodPage p) => new(p, null);
    public static FindMethodResult Fail(AssemblyError e) => new(null, e);
}
