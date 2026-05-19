namespace DotnetAssemblyMcp.Core.Metadata;

using DotnetAssemblyMcp.Core;

/// <summary>
/// Kind of metadata entity whose <c>CustomAttribute</c> rows we want to enumerate. The
/// <see cref="IMetadataIndex.ListAttributes"/> tool consumes a polymorphic target handle and
/// derives the kind from the handle's prefix; this enum is the canonical surface for it.
/// </summary>
public enum AttributeTargetKind
{
    Assembly,
    Type,
    Method,
    Parameter,
}

/// <summary>
/// Fully parsed target of a <see cref="IMetadataIndex.ListAttributes"/> call. Construction
/// goes through <see cref="AttributeTarget.Assembly"/> / <see cref="Type"/> / <see cref="Method"/> /
/// <see cref="Parameter"/> so callers cannot build an inconsistent (kind, token) tuple.
/// </summary>
public sealed record AttributeTarget(
    AttributeTargetKind Kind,
    Guid ModuleVersionId,
    int MetadataToken,
    int ParameterSequence)
{
    public static AttributeTarget Assembly(Guid mvid) =>
        new(AttributeTargetKind.Assembly, mvid, 0, 0);
    public static AttributeTarget Type(Guid mvid, int typeToken) =>
        new(AttributeTargetKind.Type, mvid, typeToken, 0);
    public static AttributeTarget Method(Guid mvid, int methodToken) =>
        new(AttributeTargetKind.Method, mvid, methodToken, 0);

    /// <summary>
    /// Targets a single parameter of a method. <paramref name="parameterSequence"/> is the
    /// 1-based parameter index (the parameter table's <c>Sequence</c> column); pass 0 to
    /// target the method's return value (where some attributes live, e.g. <c>[return: ...]</c>).
    /// </summary>
    public static AttributeTarget Parameter(Guid mvid, int methodToken, int parameterSequence) =>
        new(AttributeTargetKind.Parameter, mvid, methodToken, parameterSequence);
}

/// <summary>
/// A single decoded constructor or named argument on a custom attribute. <see cref="Name"/>
/// is non-null for named arguments (properties / fields set by the attribute usage); for
/// positional constructor arguments it is null. <see cref="Value"/> is rendered with the
/// attribute decoder and is one of: a primitive, a string, a string rendering of a Type
/// (no assembly qualification), an <c>IReadOnlyList&lt;object?&gt;</c> for array arguments,
/// or null. Enum values are reported as their underlying integer.
/// </summary>
public sealed record AttributeArgument(
    string TypeName,
    object? Value = null,
    string? Name = null);

/// <summary>
/// Decoded custom attribute. <see cref="AssemblyName"/> is the simple name of the assembly
/// that owns the attribute type (e.g. <c>System.Private.CoreLib</c>) and may be null when
/// the attribute is declared in the same module being inspected.
/// </summary>
public sealed record AttributeSummary(
    string AttributeTypeFullName,
    int MetadataToken,
    IReadOnlyList<AttributeArgument> FixedArguments,
    IReadOnlyList<AttributeArgument> NamedArguments,
    string? AssemblyName = null);

/// <summary>
/// Filter / paging knobs accepted by <see cref="IMetadataIndex.ListAttributes"/>. Defaults
/// return up to <see cref="PageSize"/> attributes in metadata order. <see cref="NameContains"/>
/// applies a case-insensitive substring filter to the attribute type's full name.
/// </summary>
public sealed record ListAttributesQuery(
    string? NameContains = null,
    int? Cursor = null,
    int PageSize = ListAttributesQuery.DefaultPageSize)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 500;
}

/// <summary>Paginated result of <see cref="IMetadataIndex.ListAttributes"/>.</summary>
public sealed record ListAttributesPage(
    Guid ModuleVersionId,
    AttributeTargetKind TargetKind,
    int TargetMetadataToken,
    int ParameterSequence,
    IReadOnlyList<AttributeSummary> Attributes,
    int? NextCursor = null,
    bool Truncated = false);

/// <summary>Result of <see cref="IMetadataIndex.ListAttributes"/>.</summary>
public readonly record struct ListAttributesResult(ListAttributesPage? Page, AssemblyError? Error)
{
    public bool IsSuccess => Page is not null;
    public static ListAttributesResult Ok(ListAttributesPage p) => new(p, null);
    public static ListAttributesResult Fail(AssemblyError e) => new(null, e);
}
