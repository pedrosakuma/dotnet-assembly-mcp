using DotnetAssemblyMcp.Core.Identity;

namespace DotnetAssemblyMcp.Core.Metadata;

/// <summary>
/// Process-wide registry of loaded module handles. Handles are keyed by MVID; loading the
/// same physical file twice returns the same handle. All methods are thread-safe.
/// </summary>
/// <remarks>
/// Tier 1 from <c>docs/mcp-conventions.md §2.1</c>: metadata-only, resident, cheap to query.
/// Tier 2+ (IL bytes, decompile, xref) will compose on top of this — they do not own their
/// own cache of <c>PEReader</c> instances.
/// </remarks>
public interface IMetadataIndex
{
    /// <summary>Loads an assembly from disk (or returns the cached handle if its MVID is already known).</summary>
    /// <param name="path">Absolute path to a .NET PE assembly.</param>
    /// <returns>The module summary on success, or a load error.</returns>
    LoadResult Load(string path);

    /// <summary>Snapshot of currently loaded modules.</summary>
    IReadOnlyList<ModuleSummary> List();

    /// <summary>
    /// Resolves a method identity to a <see cref="MethodSummary"/>. Implements the resolution
    /// algorithm from <c>docs/handoff-contract.md §3</c>.
    /// </summary>
    ResolveResult Resolve(MethodIdentity identity);

    /// <summary>
    /// Returns the raw IL of the method as a hex string plus body metadata. The hex output
    /// is capped at <paramref name="maxBytes"/> bytes of IL; the actual <see cref="IlMethodBody.IlSize"/>
    /// reports the full size.
    /// </summary>
    /// <param name="identity">A resolved method identity.</param>
    /// <param name="maxBytes">Hard upper bound on the bytes encoded in the response. Pass 0 for the default (4 KiB).</param>
    IlBodyResult GetIlBody(MethodIdentity identity, int maxBytes = 0);

    /// <summary>
    /// Walks the IL of the method and returns the structural references it contains:
    /// outbound calls, field accesses, type uses and string literals. No decompilation.
    /// </summary>
    IlScanReadResult ScanIl(MethodIdentity identity);

    /// <summary>
    /// Tier-4 reverse-call lookup: returns every method in the callee's own module whose IL
    /// emits a call/callvirt/newobj/ldftn/ldvirtftn to <paramref name="callee"/>. The index
    /// is built lazily per module and cached both in memory and at
    /// <c>~/.cache/dotnet-assembly-mcp/&lt;mvid&gt;.xref</c> (rebuilt when the underlying file
    /// changes). Cross-module callers are out of scope for this iteration and tracked
    /// separately.
    /// </summary>
    FindCallersReadResult FindCallers(MethodIdentity callee);

    /// <summary>
    /// Tier-1 type enumeration: walks the module's <c>TypeDef</c> table, filters by namespace,
    /// name substring and coarse <see cref="TypeKind"/>, and returns a paginated slice.
    /// Synthetic types (the global <c>&lt;Module&gt;</c> placeholder) are skipped.
    /// </summary>
    /// <param name="moduleVersionId">MVID of a loaded module.</param>
    /// <param name="query">Filter and paging options. Defaults yield 50 types in metadata order.</param>
    ListTypesResult ListTypes(Guid moduleVersionId, ListTypesQuery query);

    /// <summary>
    /// Tier-1 method enumeration scoped to one type: walks the type's method list, optionally
    /// filtering by case-insensitive name substring, and returns a paginated slice.
    /// </summary>
    /// <param name="moduleVersionId">MVID of a loaded module.</param>
    /// <param name="typeMetadataToken">TypeDef metadata token (table 0x02) of the declaring type.</param>
    /// <param name="query">Filter and paging options.</param>
    ListMethodsResult ListMethods(Guid moduleVersionId, int typeMetadataToken, ListMethodsQuery query);

    /// <summary>
    /// Module-wide method search. Matches every <c>MethodDef</c> whose short name matches the
    /// supplied regex (and optionally whose signature contains a substring). Iterates the
    /// <c>MethodDef</c> table directly so it is O(n) per call; pagination uses the metadata
    /// token as an exclusive cursor.
    /// </summary>
    /// <param name="moduleVersionId">MVID of a loaded module.</param>
    /// <param name="query">Required name pattern plus optional signature substring, cursor and page size.</param>
    FindMethodResult FindMethod(Guid moduleVersionId, FindMethodQuery query);
}

/// <summary>Result of <see cref="IMetadataIndex.Load"/>.</summary>
public readonly record struct LoadResult(ModuleSummary? Module, AssemblyError? Error)
{
    public bool IsSuccess => Module is not null;
    public static LoadResult Ok(ModuleSummary m) => new(m, null);
    public static LoadResult Fail(AssemblyError e) => new(null, e);
}

/// <summary>Result of <see cref="IMetadataIndex.Resolve"/>.</summary>
public readonly record struct ResolveResult(MethodSummary? Method, AssemblyError? Error)
{
    public bool IsSuccess => Method is not null;
    public static ResolveResult Ok(MethodSummary m) => new(m, null);
    public static ResolveResult Fail(AssemblyError e) => new(null, e);
}
