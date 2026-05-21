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
    private readonly Action<TData>? _onEvict;
    private readonly Action<TData>? _onOrphan;
    private int _disposed;

    /// <summary>
    /// Creates a new cache. When <paramref name="onEvict"/> is supplied it is invoked
    /// for every <i>published</i> entry removed from the cache, either by an explicit
    /// <see cref="Invalidate"/> or because a fresh build replaces a stale entry on the
    /// same MVID slot. Use for entries that hold unmanaged or otherwise <see cref="IDisposable"/>
    /// state (e.g. <see cref="System.Reflection.Metadata.MetadataReaderProvider"/> instances
    /// that pin native memory).
    /// <para><paramref name="onOrphan"/> is invoked for build outputs that <b>lost the
    /// publish race</b> in <see cref="GetOrBuild(ModuleHandle,Func{ModuleHandle,TData})"/>:
    /// the orphan was never returned to a caller, so anything an <paramref name="onEvict"/>
    /// implementation would defer for borrowed-reader safety (e.g. a graveyard) can be
    /// disposed immediately. Defaults to <paramref name="onEvict"/> for back-compat.</para>
    /// <para>Failures inside either callback are swallowed so a misbehaving disposer cannot
    /// leak a stale entry back into the cache.</para>
    /// </summary>
    public ModuleScopedCache(Action<TData>? onEvict = null, Action<TData>? onOrphan = null)
    {
        _onEvict = onEvict;
        _onOrphan = onOrphan ?? onEvict;
    }

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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, typeof(ModuleScopedCache<TData>));
        var stamp = ModuleCacheStamp.TryCapture(module);
        while (true)
        {
            if (_entries.TryGetValue(module.Mvid, out var existing)
                && ReferenceEquals(existing.Module, module)
                && existing.Stamp.Equals(stamp))
            {
                // Re-read the sentinel before returning the borrowed reference. A Clear()
                // landing between our TryGetValue and this point would already have drained
                // (or be about to drain) existing.Data — surface the disposal instead of
                // handing the caller a soon-to-be-evicted entry.
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, typeof(ModuleScopedCache<TData>));
                wasCached = true;
                return existing.Data;
            }

            // Build outside the dictionary so concurrent callers don't deadlock waiting on
            // each other; the loser of the publish race evicts its orphan and retries (next
            // iteration will hit the fresh entry the winner published).
            var data = build(module);
            var fresh = new Entry(module, data, stamp);

            // Pre-publish disposal check is necessary but not sufficient — Clear() can still
            // race between this read and the TryAdd/TryUpdate below. The post-publish
            // back-out below is the linearization point.
            if (Volatile.Read(ref _disposed) != 0)
            {
                SafeOrphan(data);
                ObjectDisposedException.ThrowIf(true, typeof(ModuleScopedCache<TData>));
            }

            if (existing is null)
            {
                if (_entries.TryAdd(module.Mvid, fresh))
                {
                    // Post-publish disposal back-out: if Clear() landed between our pre-check
                    // and the TryAdd, our entry may now be sitting in a "disposed" cache.
                    // The KeyValuePair Remove ensures we only release what WE published AND
                    // only when Clear hasn't already drained it (avoiding a double-dispose
                    // when SafeEvict + SafeOrphan would both fire on the same data).
                    if (Volatile.Read(ref _disposed) != 0)
                    {
                        var ours = new KeyValuePair<Guid, Entry>(module.Mvid, fresh);
                        if (((ICollection<KeyValuePair<Guid, Entry>>)_entries).Remove(ours))
                            SafeOrphan(data);
                        ObjectDisposedException.ThrowIf(true, typeof(ModuleScopedCache<TData>));
                    }
                    wasCached = false;
                    return data;
                }
            }
            else
            {
                // CAS replace: only swap when no other publisher has touched the slot since
                // our TryGetValue, so SafeEvict fires exactly once per evicted entry.
                if (_entries.TryUpdate(module.Mvid, fresh, existing))
                {
                    SafeEvict(existing.Data);
                    // Same post-publish back-out as above; only orphan if Clear hasn't beaten us.
                    if (Volatile.Read(ref _disposed) != 0)
                    {
                        var ours = new KeyValuePair<Guid, Entry>(module.Mvid, fresh);
                        if (((ICollection<KeyValuePair<Guid, Entry>>)_entries).Remove(ours))
                            SafeOrphan(data);
                        ObjectDisposedException.ThrowIf(true, typeof(ModuleScopedCache<TData>));
                    }
                    wasCached = false;
                    return data;
                }
            }

            // Lost the publish race. Dispose the orphan we just built (its owner, this call,
            // never published it) and loop to either serve the winner's entry or rebuild
            // against a still-stale slot.
            SafeOrphan(data);
        }
    }

    /// <summary>Drops the cached entry for <paramref name="mvid"/>, if any. No-op once disposed.</summary>
    public void Invalidate(Guid mvid)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (_entries.TryRemove(mvid, out var entry))
            SafeEvict(entry.Data);
    }

    /// <summary>
    /// Drops every cached entry and invokes the eviction callback on each, if any. Use from
    /// the owning component's <see cref="IDisposable.Dispose"/> implementation to release
    /// pinned resources alongside the component itself. After <c>Clear</c> returns,
    /// subsequent <see cref="GetOrBuild(ModuleHandle,Func{ModuleHandle,TData})"/> calls throw
    /// <see cref="ObjectDisposedException"/> (fail-fast against stray async continuations);
    /// <see cref="Invalidate"/> becomes a no-op. Idempotent.
    /// </summary>
    public void Clear()
    {
        // Flip the sentinel BEFORE draining so any in-flight GetOrBuild that finishes its
        // build() between our TryRemove calls sees _disposed and throws instead of publishing
        // a stray entry whose onEvict will never be called.
        Interlocked.Exchange(ref _disposed, 1);
        foreach (var mvid in _entries.Keys.ToArray())
        {
            if (_entries.TryRemove(mvid, out var entry))
                SafeEvict(entry.Data);
        }
    }

    private void SafeEvict(TData data)
    {
        if (_onEvict is null) return;
        try { _onEvict(data); }
        catch { /* swallow: a misbehaving disposer must not leave the entry pinned */ }
    }

    private void SafeOrphan(TData data)
    {
        if (_onOrphan is null) return;
        try { _onOrphan(data); }
        catch { /* swallow: see SafeEvict */ }
    }

    // Exposed for tests asserting cache state per the IModuleScopedCache contract.
    internal bool HasEntry(Guid mvid) => _entries.ContainsKey(mvid);

    // Plain sealed class — not a record — because TryUpdate uses EqualityComparer<Entry>.Default
    // as its CAS predicate. We rely on reference identity ("did anyone touch this slot since
    // our TryGetValue?"), so structural / record equality would be a correctness hazard: a
    // structurally-equal Entry published by a racing thread could spuriously satisfy the CAS
    // and let SafeEvict fire against the wrong (already-displaced) payload.
    private sealed class Entry
    {
        public Entry(ModuleHandle module, TData data, ModuleCacheStamp stamp)
        {
            Module = module;
            Data = data;
            Stamp = stamp;
        }

        public ModuleHandle Module { get; }
        public TData Data { get; }
        public ModuleCacheStamp Stamp { get; }
    }
}
