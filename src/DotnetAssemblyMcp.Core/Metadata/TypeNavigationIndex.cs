using System.Collections.Frozen;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using static DotnetAssemblyMcp.Core.Metadata.MetadataDisplay;

namespace DotnetAssemblyMcp.Core.Metadata;

/// <summary>
/// Shared caches for type-graph navigation: a per-module
/// <see cref="FrozenDictionary{TKey, TValue}"/> mapping <c>fullName → TypeDef token</c> for O(1)
/// <see cref="MetadataIndex.FindTypeByFullName"/> lookups, and a global cache of the cross-module
/// parent-edge maps consumed by <see cref="MetadataIndex.ListDerivedTypes"/>.
/// </summary>
/// <remarks>
/// Both caches are built lazily on first read. Per-module invalidation is wired through
/// <see cref="IModuleScopedCache.Invalidate"/>: the name map for that MVID is dropped (via the
/// shared <see cref="ModuleScopedCache{TData}"/>) and the global parent maps are reset (any
/// parent map could mention the reloaded module). New-load detection (no reload event fires
/// for a first-time load) piggy-backs on a snapshot of the captured MVID set:
/// <see cref="GetParentMaps"/> rebuilds when the current <see cref="ModuleStore.Modules"/> set
/// differs from the cached snapshot.
/// </remarks>
internal sealed class TypeNavigationIndex : IModuleScopedCache
{
    private readonly ModuleStore _store;
    private readonly ModuleScopedCache<FrozenDictionary<string, int>> _nameToToken = new();

    // Global cache of the parent-edge graph. Null when invalidated.
    private ParentMapCacheEntry? _parentMaps;
    private readonly Lock _parentMapsLock = new();

    public TypeNavigationIndex(ModuleStore store) { _store = store; }

    public void Invalidate(Guid mvid)
    {
        _nameToToken.Invalidate(mvid);
        lock (_parentMapsLock) _parentMaps = null;
    }

    // Exposed for tests asserting cache state per the IModuleScopedCache contract.
    internal bool HasNameCacheEntry(Guid mvid) => _nameToToken.HasEntry(mvid);
    internal bool HasParentMapsEntry => _parentMaps is not null;

    /// <summary>
    /// Returns the metadata token of the TypeDef whose full name matches <paramref name="typeFullName"/>,
    /// or <c>null</c> if no match is found. Builds and caches the per-module name map on first call
    /// via the shared <see cref="ModuleScopedCache{TData}"/> (handle-pinned + stamp-validated).
    /// </summary>
    public int? TryFindTypeToken(ModuleHandle module, string typeFullName)
    {
        var map = _nameToToken.GetOrBuild(module, BuildNameMap);
        return map.TryGetValue(typeFullName, out var token) ? token : null;
    }

    private static FrozenDictionary<string, int> BuildNameMap(ModuleHandle module)
    {
        var builder = new Dictionary<string, int>(StringComparer.Ordinal);
        int total;
        try { total = module.MD.TypeDefinitions.Count; }
        catch (BadImageFormatException) { return FrozenDictionary<string, int>.Empty; }

        for (int row = 1; row <= total; row++)
        {
            TypeDefinition td;
            try { td = module.MD.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(row)); }
            catch (BadImageFormatException) { continue; }

            string full;
            try { full = TypeName(module, td); }
            catch (BadImageFormatException) { continue; }

            int token = MetadataTokens.GetToken(MetadataTokens.TypeDefinitionHandle(row));
            // First definition wins; duplicates (rare in well-formed PEs) are ignored so the
            // semantics match the pre-cache linear scan in MetadataIndex.FindTypeByFullName.
            builder.TryAdd(full, token);
        }
        return builder.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>
    /// Returns the cached parent-edge maps spanning every loaded module, rebuilding when the
    /// loaded-module set has changed since the last build. The builder delegate carries the
    /// per-edge logic (BaseType + InterfaceImpls + TypeSpec walk) that lives in
    /// <see cref="MetadataIndex"/> so this cache stays metadata-format-agnostic.
    /// </summary>
    public (
        Dictionary<(Guid, int), List<(Guid mvid, int token, IReadOnlyList<string>? args)>> Local,
        Dictionary<(Guid, int), List<(string asm, string full, IReadOnlyList<string>? args)>> Cross)
        GetParentMaps(Action<ParentMapBuilder> build)
    {
        // Snapshot the current MVID set on every call (cheap — ModuleStore.Modules is a small
        // ConcurrentDictionary.Values walk). When the set differs from the cached snapshot,
        // a module has been loaded or unloaded since the last build, and we must rebuild.
        var current = SnapshotMvids();
        lock (_parentMapsLock)
        {
            if (_parentMaps is { } cached && cached.Mvids.SetEquals(current))
                return (cached.Local, cached.Cross);

            var local = new Dictionary<(Guid, int), List<(Guid mvid, int token, IReadOnlyList<string>? args)>>();
            var cross = new Dictionary<(Guid, int), List<(string asm, string full, IReadOnlyList<string>? args)>>();
            build(new ParentMapBuilder(local, cross));
            _parentMaps = new ParentMapCacheEntry(current, local, cross);
            return (local, cross);
        }
    }

    private HashSet<Guid> SnapshotMvids()
    {
        var set = new HashSet<Guid>();
        foreach (var m in _store.Modules) set.Add(m.Mvid);
        return set;
    }

    private sealed record ParentMapCacheEntry(
        HashSet<Guid> Mvids,
        Dictionary<(Guid, int), List<(Guid mvid, int token, IReadOnlyList<string>? args)>> Local,
        Dictionary<(Guid, int), List<(string asm, string full, IReadOnlyList<string>? args)>> Cross);

    /// <summary>Builder handed to the parent-map build delegate.</summary>
    internal sealed class ParentMapBuilder
    {
        public Dictionary<(Guid, int), List<(Guid mvid, int token, IReadOnlyList<string>? args)>> Local { get; }
        public Dictionary<(Guid, int), List<(string asm, string full, IReadOnlyList<string>? args)>> Cross { get; }
        public ParentMapBuilder(
            Dictionary<(Guid, int), List<(Guid mvid, int token, IReadOnlyList<string>? args)>> local,
            Dictionary<(Guid, int), List<(string asm, string full, IReadOnlyList<string>? args)>> cross)
        {
            Local = local;
            Cross = cross;
        }
    }
}
