namespace DotnetAssemblyMcp.Core.Metadata;

using DotnetAssemblyMcp.Core;

/// <summary>
/// Kind of structural member reported by <see cref="IMetadataIndex.ListMembers"/>. Methods
/// are intentionally excluded — they have their own dedicated <c>list_methods</c> surface
/// because the IL-size / generic-arity columns don't apply to fields / properties / events.
/// </summary>
public enum MemberKind
{
    Field,
    Property,
    Event,
}

/// <summary>
/// Compact summary of a field, property, or event. <see cref="Handle"/> is a prefix-tagged
/// member handle (<c>f:&lt;mvid&gt;:0x&lt;token&gt;</c>, <c>p:&lt;mvid&gt;:0x&lt;token&gt;</c>,
/// or <c>e:&lt;mvid&gt;:0x&lt;token&gt;</c>) and is accepted by <c>list_attributes</c> as a target.
/// <see cref="Signature"/> renders the member's type in C#-ish form
/// (<c>int Field</c>, <c>string Property { get; set; }</c>, <c>EventHandler Event</c>).
/// </summary>
public sealed record MemberSummary(
    Guid ModuleVersionId,
    int MetadataToken,
    string Handle,
    MemberKind Kind,
    string Name,
    string Signature,
    IReadOnlyList<string> Attributes);

/// <summary>
/// Filter / paging knobs accepted by <see cref="IMetadataIndex.ListMembers"/>. All fields
/// except the type identity are optional; the defaults return up to <see cref="PageSize"/>
/// members of the type in metadata order across all kinds.
/// </summary>
public sealed record ListMembersQuery(
    MemberKind? Kind = null,
    string? NamePattern = null,
    string? SignatureContains = null,
    int? Cursor = null,
    int PageSize = ListMembersQuery.DefaultPageSize)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 500;
}

/// <summary>Paginated result of <see cref="IMetadataIndex.ListMembers"/>.</summary>
public sealed record ListMembersPage(
    Guid ModuleVersionId,
    int TypeMetadataToken,
    string TypeFullName,
    IReadOnlyList<MemberSummary> Members,
    int? NextCursor = null,
    bool Truncated = false);

/// <summary>Result of <see cref="IMetadataIndex.ListMembers"/>.</summary>
public readonly record struct ListMembersResult(ListMembersPage? Page, AssemblyError? Error)
{
    public bool IsSuccess => Page is not null;
    public static ListMembersResult Ok(ListMembersPage p) => new(p, null);
    public static ListMembersResult Fail(AssemblyError e) => new(null, e);
}
