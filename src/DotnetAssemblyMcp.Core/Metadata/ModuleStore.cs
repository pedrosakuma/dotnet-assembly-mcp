using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.IO;

namespace DotnetAssemblyMcp.Core.Metadata;

/// <summary>
/// Owns the loaded-assembly lifecycle: path → <see cref="PEReader"/> / <see cref="MetadataReader"/>
/// open + close, filesystem watching with debounced reload, path-hint registration, and the
/// deferred-disposal graveyard that protects in-flight reads from a same-MVID reload.
/// </summary>
/// <remarks>
/// Extracted from <see cref="MetadataIndex"/> as the first seam of the audit
/// god-class decomposition (#79). Every other component (xref index, navigator,
/// specialized indexes, R2R body lookup) consumes <see cref="ModuleHandle"/> as
/// an input — the lifecycle here is the single owner.
/// </remarks>
internal sealed class ModuleStore : IDisposable
{
    /// <summary>Debounce window applied to <see cref="FileSystemWatcher"/> events.</summary>
    public static readonly TimeSpan WatchDebounce = TimeSpan.FromMilliseconds(250);

    private readonly ConcurrentDictionary<Guid, ModuleHandle> _modules = new();
    private readonly ConcurrentDictionary<Guid, string> _pathHints = new();
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _pendingReloads =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _watch;
    private readonly IReadOnlyList<string>? _allowedRoots;
    private int _disposed;

    // PEReader graveyard: holds readers we logically evicted (same-MVID reload, MVID change)
    // but cannot dispose immediately because in-flight requests on other threads may still
    // hold a reference to the old ModuleHandle. The queue is drained on Dispose.
    //
    // Trade-off (audit #78): this graveyard is *intentionally unbounded* during the store's
    // lifetime — a request that's still walking an old `PEReader` cannot be detected from
    // outside without true reader ref-counting. Memory cost is bounded by the number of
    // distinct reloads in a session and by the PE size (file is fully read into a
    // MemoryStream on Load); for typical dev workflows this is < a few hundred MB. The
    // complete fix (per-reader lease + active-reader tracking) is tracked as a follow-up
    // on top of this extraction. Bounding the queue here would re-introduce the
    // dispose-while-reading race for high-churn reload bursts, which defeats the purpose
    // of the graveyard.
    private readonly ConcurrentQueue<PEReader> _evictedPe = new();

    /// <summary>Raised after any module mutation: load, reload (same/changed MVID), or load-failure on reload.</summary>
    public event EventHandler<ModuleReloadedEventArgs>? ModuleReloaded;

    public ModuleStore(bool watchForChanges) : this(watchForChanges, allowedRoots: null) { }

    /// <param name="watchForChanges">Install per-directory file watchers with debounced reload.</param>
    /// <param name="allowedRoots">
    /// Operator-configured trusted roots for the untrusted-path-hint contract (#150).
    /// <c>null</c> disables enforcement (any absolute path may be loaded, subject to the existing
    /// symlink/size/MVID defenses). A non-null list activates enforcement; an empty list (e.g. all
    /// configured roots were invalid) fails closed and denies every load.
    /// </param>
    public ModuleStore(bool watchForChanges, IReadOnlyList<string>? allowedRoots)
    {
        _watch = watchForChanges;
        _allowedRoots = CanonicalizeRoots(allowedRoots);
    }

    private static List<string>? CanonicalizeRoots(IReadOnlyList<string>? roots)
    {
        if (roots is null) return null; // enforcement disabled
        var canonical = new List<string>(roots.Count);
        foreach (var r in roots)
        {
            if (string.IsNullOrWhiteSpace(r) || !Path.IsPathFullyQualified(r)) continue;
            var real = PathPolicy.CanonicalizeRealPath(r);
            if (real is not null) canonical.Add(real);
        }
        // Non-null even when empty: an operator who configured roots that all dropped out gets
        // fail-closed behaviour (deny all) rather than a silent revert to allow-all.
        return canonical;
    }

    public int Count => _modules.Count;

    public bool TryGet(Guid mvid, out ModuleHandle module) => _modules.TryGetValue(mvid, out module!);

    public IEnumerable<ModuleHandle> Modules => _modules.Values;

    public IReadOnlyDictionary<Guid, string> PathHints => _pathHints;

    public LoadResult Load(string path)
    {
        var absErr = PathPolicy.RequireAbsolute(path);
        if (absErr is not null) return LoadResult.Fail(absErr);
        if (!File.Exists(path))
            return LoadResult.Fail(new AssemblyError(
                ErrorKinds.ModuleLoadFailed, $"file not found: {ErrorRedactor.RedactPath(path)}"));

        var fullPath = Path.GetFullPath(path);
        var loaded = OpenAndRegister(fullPath);
        if (!loaded.IsSuccess) return loaded;

        if (_watch) EnsureWatcher(fullPath);
        return loaded;
    }

    /// <summary>
    /// Reload semantics for an explicit invalidation against a known <paramref name="oldMvid"/>.
    /// Mirrors the file-watcher's flow (see <c>FlushReload</c>): re-reads <paramref name="path"/>,
    /// swaps the <see cref="ModuleHandle"/>, evicts the old MVID entry when the on-disk MVID
    /// has changed, and always raises <see cref="ModuleReloaded"/> with
    /// <c>(oldMvid, newMvid, error?)</c> — including on failure, so subscribers can drop stale
    /// state for the requested MVID even when the file vanished mid-call. Returns the
    /// <see cref="LoadResult"/> for callers that want to inspect the outcome.
    /// </summary>
    public LoadResult Reload(Guid oldMvid, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            var err = new AssemblyError(ErrorKinds.InvalidArgument, "path is required.");
            EvictStaleHandle(oldMvid);
            ModuleReloaded?.Invoke(this, new ModuleReloadedEventArgs(path ?? string.Empty, oldMvid, null, err));
            return LoadResult.Fail(err);
        }
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            var err = new AssemblyError(ErrorKinds.ModuleLoadFailed, $"file not found: {fullPath}");
            EvictStaleHandle(oldMvid);
            ModuleReloaded?.Invoke(this, new ModuleReloadedEventArgs(fullPath, oldMvid, null, err));
            return LoadResult.Fail(err);
        }

        // Suppress OpenAndRegister's own same-MVID event — Reload owns event emission
        // and raises exactly one ModuleReloaded at the end so the public Invalidate
        // contract ("exactly once per call") holds.
        var result = OpenAndRegister(fullPath, raiseSameMvidEvent: false);
        if (!result.IsSuccess)
        {
            EvictStaleHandle(oldMvid);
            ModuleReloaded?.Invoke(this, new ModuleReloadedEventArgs(fullPath, oldMvid, null, result.Error));
            return result;
        }

        var newMvid = result.Module!.ModuleVersionId;
        if (oldMvid != newMvid && _modules.TryRemove(oldMvid, out var stale))
        {
            // Defer disposal — in-flight readers on other threads may still hold `stale.PE`.
            _evictedPe.Enqueue(stale.PE);
        }

        // Fan out unconditionally — subscribers handle duplicate (mvid, mvid) reloads
        // idempotently (cache clear). Matches the watcher's behaviour at FlushReload.
        ModuleReloaded?.Invoke(this, new ModuleReloadedEventArgs(fullPath, oldMvid, newMvid, null));
        return result;
    }

    private void EvictStaleHandle(Guid mvid)
    {
        // Explicit Reload failure path: the caller asserts the MVID is known-stale, so we
        // must drop the still-loaded ModuleHandle. Without this, follow-up queries would
        // resolve `mvid` via TryGet, hit the in-memory PE, and immediately repopulate the
        // very caches Invalidate just cleared. PE is queued on the graveyard for deferred
        // disposal (in-flight readers on other threads may still hold the reference).
        if (_modules.TryRemove(mvid, out var stale))
        {
            _evictedPe.Enqueue(stale.PE);
        }
    }

    public ProbeResult Probe(string path)
    {
        var absErr = PathPolicy.RequireAbsolute(path);
        if (absErr is not null) return ProbeResult.Fail(absErr);
        if (!File.Exists(path))
            return ProbeResult.Fail(new AssemblyError(
                ErrorKinds.ModuleLoadFailed, $"file not found: {ErrorRedactor.RedactPath(path)}"));
        var fullPath = Path.GetFullPath(path);
        var opened = OpenModule(fullPath);
        if (opened.Error is not null) return ProbeResult.Fail(opened.Error);
        try { return ProbeResult.Ok(opened.Module!.Mvid); }
        finally { opened.PE!.Dispose(); }
    }

    public IReadOnlyList<ModuleSummary> List()
    {
        var list = new List<ModuleSummary>(_modules.Count);
        foreach (var m in _modules.Values)
            list.Add(SummarizeModule(m));
        return list;
    }

    public void RegisterPathHint(Guid moduleVersionId, string path)
    {
        if (moduleVersionId == Guid.Empty || string.IsNullOrWhiteSpace(path)) return;
        if (!Path.IsPathFullyQualified(path)) return; // silent reject — manifest entries surface via Probe
        _pathHints[moduleVersionId] = Path.GetFullPath(path);
    }

    public bool TryGetPathHint(Guid moduleVersionId, out string? path)
    {
        if (_pathHints.TryGetValue(moduleVersionId, out var p))
        {
            path = p;
            return true;
        }
        path = null;
        return false;
    }

    public void WatchPath(string path)
    {
        if (!_watch || string.IsNullOrWhiteSpace(path)) return;
        EnsureWatcher(Path.GetFullPath(path));
    }

    /// <summary>
    /// Snapshot a `mvid → MetadataReader factory` view of every currently-loaded module.
    /// Cross-module resolution paths use this to avoid repeated dictionary lookups while
    /// walking generic instantiations.
    /// </summary>
    public Dictionary<Guid, Func<MetadataReader>> SnapshotReaders()
    {
        var dict = new Dictionary<Guid, Func<MetadataReader>>(_modules.Count);
        foreach (var (mvid, mod) in _modules)
        {
            var local = mod;
            dict[mvid] = () => local.MD;
        }
        return dict;
    }

    public static ModuleSummary SummarizeModule(ModuleHandle m) =>
        new(m.Mvid, Path.GetFileName(m.Path), m.Path, m.MD.MethodDefinitions.Count);

    private LoadResult OpenAndRegister(string fullPath, bool raiseSameMvidEvent = true)
    {
        var opened = OpenModule(fullPath);
        if (opened.Error is not null) return LoadResult.Fail(opened.Error);
        var mvid = opened.Module!.Mvid;

        if (_modules.TryGetValue(mvid, out var existing))
        {
            // Same-MVID reload: atomically install the freshly-read PE and dispose the old one
            // so subsequent queries don't keep returning the stale byte buffer. Without this
            // swap, deterministic rebuilds that preserve the MVID would silently serve stale IL.
            if (string.Equals(existing.Path, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                var replacement = new ModuleHandle(mvid, fullPath, opened.PE!, opened.MD!);
                if (_modules.TryUpdate(mvid, replacement, existing))
                {
                    // Defer disposal of the old PE — in-flight requests on other threads
                    // may still be reading through `existing.PE`. The graveyard is drained
                    // on Dispose. See _evictedPe declaration for the bounding trade-off.
                    _evictedPe.Enqueue(existing.PE);
                    // Even when the MVID hasn't changed, the IL we just opened may differ from
                    // what built downstream caches (deterministic rebuild, manual file swap).
                    // Fan out via the ModuleReloaded event so subscribers can invalidate.
                    // Callers that own their own event emission (e.g. Reload) suppress this.
                    if (raiseSameMvidEvent)
                    {
                        ModuleReloaded?.Invoke(this, new ModuleReloadedEventArgs(fullPath, mvid, mvid, null));
                    }
                    return LoadResult.Ok(SummarizeModule(replacement));
                }
            }
            // Different path with the same MVID (e.g. file copy) — keep the first registration
            // and dispose our duplicate to avoid leaking the PEReader.
            opened.PE!.Dispose();
            return LoadResult.Ok(SummarizeModule(existing));
        }

        var added = _modules.GetOrAdd(mvid, _ => opened.Module!);
        if (!ReferenceEquals(added.PE, opened.PE))
        {
            // Lost a race; another thread loaded the same MVID first. Dispose our duplicate.
            opened.PE!.Dispose();
        }
        return LoadResult.Ok(SummarizeModule(added));
    }

    private readonly record struct OpenedModule(
        ModuleHandle? Module, PEReader? PE, MetadataReader? MD, AssemblyError? Error);

    private OpenedModule OpenModule(string fullPath)
    {
        // Allow-list gate (#150). When enforcement is active, open the canonical real path that
        // passed containment — NOT the original fullPath — so an ancestor symlink retargeted
        // between this check and the read cannot redirect the open outside an allowed root.
        // When enforcement is disabled, ResolveWithinRoots echoes fullPath unchanged.
        var (resolvedPath, rootErr) = PathPolicy.ResolveWithinRoots(fullPath, _allowedRoots);
        if (rootErr is not null) return new OpenedModule(null, null, null, rootErr);
        var openPath = resolvedPath!;
        try
        {
            // SafeFileOpener enforces size cap and reparse-point rejection before allocation.
            // The PEReader is backed by a MemoryStream so the file on disk stays unlocked —
            // required for the Tier-1 watcher to observe rewrites on Windows where
            // File.Move(overwrite: true) needs the destination free of open writable handles.
            var readResult = SafeFileOpener.ReadAllBytes(openPath, SafeFileOpener.DefaultMaxAssemblyBytes);
            if (!readResult.IsSuccess)
            {
                return new OpenedModule(null, null, null, readResult.Error);
            }
            var pe = new PEReader(new MemoryStream(readResult.Bytes!, writable: false));
            if (!pe.HasMetadata)
            {
                pe.Dispose();
                return new OpenedModule(null, null, null,
                    new AssemblyError(ErrorKinds.ModuleLoadFailed, $"not a managed PE: {ErrorRedactor.RedactPath(openPath)}"));
            }
            var md = pe.GetMetadataReader();
            var mvid = md.GetGuid(md.GetModuleDefinition().Mvid);
            return new OpenedModule(new ModuleHandle(mvid, openPath, pe, md), pe, md, null);
        }
        catch (BadImageFormatException ex)
        {
            return new OpenedModule(null, null, null,
                new AssemblyError(ErrorKinds.ModuleLoadFailed, "invalid PE/CLI image.", ErrorRedactor.Redact(ex.Message)));
        }
    }

    private void EnsureWatcher(string fullPath)
    {
        var dir = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(dir)) return;
        _watchers.GetOrAdd(dir, d =>
        {
            var w = new FileSystemWatcher(d)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime
                               | NotifyFilters.FileName,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };
            w.Changed += OnWatcherEvent;
            w.Created += OnWatcherEvent;
            w.Renamed += OnWatcherRenamed;
            return w;
        });
    }

    private void OnWatcherEvent(object sender, FileSystemEventArgs e) => ScheduleReload(e.FullPath);
    private void OnWatcherRenamed(object sender, RenamedEventArgs e) => ScheduleReload(e.FullPath);

    private void ScheduleReload(string fullPath)
    {
        if (_disposed != 0) return;
        // Only react to paths we actually loaded. Avoids storms on bin/obj rebuilds.
        if (!_modules.Values.Any(m => string.Equals(m.Path, fullPath, StringComparison.OrdinalIgnoreCase)))
            return;

        var now = DateTime.UtcNow;
        _pendingReloads[fullPath] = now;
        _ = Task.Delay(WatchDebounce).ContinueWith(_ => TryReload(fullPath, now), TaskScheduler.Default);
    }

    private void TryReload(string fullPath, DateTime scheduledAt)
    {
        if (_disposed != 0) return;
        // Drop stale debounce timers — only the most recent scheduling wins.
        if (!_pendingReloads.TryGetValue(fullPath, out var latest) || latest != scheduledAt) return;
        _pendingReloads.TryRemove(fullPath, out _);

        var oldEntry = _modules.Values
            .FirstOrDefault(m => string.Equals(m.Path, fullPath, StringComparison.OrdinalIgnoreCase));
        var oldMvid = oldEntry?.Mvid;

        // Tolerate transient ShareViolation/Empty mid-write by skipping; the next event will retry.
        if (!File.Exists(fullPath)) return;

        var result = OpenAndRegister(fullPath);
        if (!result.IsSuccess)
        {
            ModuleReloaded?.Invoke(this, new ModuleReloadedEventArgs(fullPath, oldMvid, null, result.Error));
            return;
        }

        var newMvid = result.Module!.ModuleVersionId;
        if (oldMvid is { } prev && prev != newMvid && _modules.TryRemove(prev, out var stale))
        {
            // Defer disposal — see _evictedPe declaration.
            _evictedPe.Enqueue(stale.PE);
        }

        // Unconditionally fan out the reload event (matches pre-extraction behaviour). In the
        // same-MVID swap case OpenAndRegister has already raised one event; subscribers are
        // expected to handle duplicate (mvid, mvid) reloads idempotently (cache clear).
        ModuleReloaded?.Invoke(this, new ModuleReloadedEventArgs(fullPath, oldMvid, newMvid, null));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var w in _watchers.Values)
        {
            w.EnableRaisingEvents = false;
            w.Dispose();
        }
        _watchers.Clear();
        foreach (var m in _modules.Values)
            m.PE.Dispose();
        _modules.Clear();
        while (_evictedPe.TryDequeue(out var pe))
            pe.Dispose();
    }
}

/// <summary>
/// Lifetime handle to a single loaded module. Lifetime is owned by <see cref="ModuleStore"/>;
/// other components consume it as an input parameter and must not dispose its
/// <see cref="PEReader"/> directly.
/// </summary>
internal sealed record ModuleHandle(Guid Mvid, string Path, PEReader PE, MetadataReader MD);
