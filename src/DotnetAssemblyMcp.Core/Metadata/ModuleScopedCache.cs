using System.Collections.Concurrent;

namespace DotnetAssemblyMcp.Core.Metadata;

/// <summary>
/// Shared building block for per-module derived caches (string index, attribute index,
/// field-access index, type-name index, …). Owns a <c>(ModuleHandle, TData, ModuleCacheStamp)</c>
/// entry per MVID and serves cache hits only when both the in-memory handle reference
/// AND the on-disk file stamp still match the current <see cref="ModuleHandle"/> the caller
/// presents.
/// </summary>
/// <remarks>
/// <para>The two-gate freshness contract closes two distinct failure modes that surfaced
/// during code review of <see cref="TypeNavigationIndex"/> (PR #117):</para>
/// <list type="number">
/// <item><description><b>Publish-after-invalidate race on same-MVID reload.</b>
/// <see cref="ModuleStore"/> produces a fresh <see cref="ModuleHandle"/> (a record wrapping
/// a new <see cref="System.Reflection.PortableExecutable.PEReader"/> /
/// <see cref="System.Reflection.Metadata.MetadataReader"/>) on every reload — even when
/// the MVID is unchanged (deterministic rebuilds with the same source). A stale entry that
/// races with <see cref="Invalidate"/> would otherwise be published under a fresh file
/// stamp and survive every future lookup. <see cref="object.ReferenceEquals(object?,object?)"/>
/// on the cached <see cref="ModuleHandle"/> rejects it.</description></item>
/// <item><description><b>Silent file change between explicit reloads.</b>
/// <see cref="ModuleCacheStamp"/> compares <c>(file length, last-write-utc)</c>; mismatch
/// on lookup forces a rebuild.</description></item>
/// </list>
/// <para>Callers MUST resolve the <see cref="ModuleHandle"/> from <see cref="ModuleStore"/>
/// once and pass the same instance through to both <see cref="GetOrBuild(ModuleHandle,Func{ModuleHandle,TData})"/>
/// and any follow-up operation (e.g. token → summary resolution). Re-fetching from the store
/// after the lookup re-opens the same race the cache is here to close.</para>
/// </remarks>
internal sealed class ModuleScopedCache<TData> : IModuleScopedCache where TData : class
{
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    /// <summary>
    /// Returns the cached <typeparamref name="TData"/> for <paramref name="module"/>, building
    /// and publishing a new entry when no valid one exists. <paramref name="build"/> receives
    /// the same <see cref="ModuleHandle"/> the caller presented so the built data is anchored
    /// to that exact in-memory image.
    /// </summary>
    public TData GetOrBuild(ModuleHandle module, Func<ModuleHandle, TData> build) =>
        GetOrBuild(module, build, out _);

    /// <summary>
    /// Same as <see cref="GetOrBuild(ModuleHandle,Func{ModuleHandle,TData})"/>, additionally reporting
    /// via <paramref name="wasCached"/> whether the returned value was served from the cache
    /// without rebuilding. Useful for surfacing a <c>FromCache</c> flag on query responses.
    /// </summary>
    public TData GetOrBuild(ModuleHandle module, Func<ModuleHandle, TData> build, out bool wasCached)
    {
        var stamp = ModuleCacheStamp.TryCapture(module);
        if (_entries.TryGetValue(module.Mvid, out var entry)
            && ReferenceEquals(entry.Module, module)
            && entry.Stamp.Equals(stamp))
        {
            wasCached = true;
            return entry.Data;
        }

        wasCached = false;
        var data = build(module);
        _entries[module.Mvid] = new Entry(module, data, stamp);
        return data;
    }

    /// <summary>Drops the cached entry for <paramref name="mvid"/>, if any.</summary>
    public void Invalidate(Guid mvid) => _entries.TryRemove(mvid, out _);

    // Exposed for tests asserting cache state per the IModuleScopedCache contract.
    internal bool HasEntry(Guid mvid) => _entries.ContainsKey(mvid);

    private sealed record Entry(ModuleHandle Module, TData Data, ModuleCacheStamp Stamp);
}
