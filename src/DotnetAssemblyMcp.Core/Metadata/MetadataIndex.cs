using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Handles;
using DotnetAssemblyMcp.Core.Identity;
using DotnetAssemblyMcp.Core.Metadata.Resolvers;
using HandleKind = System.Reflection.Metadata.HandleKind;
using static DotnetAssemblyMcp.Core.Metadata.MetadataDisplay;
using static DotnetAssemblyMcp.Core.Metadata.MetadataResolver;

namespace DotnetAssemblyMcp.Core.Metadata;

/// <summary>
/// <see cref="IMetadataIndex"/> backed by <see cref="PEReader"/> / <see cref="MetadataReader"/>
/// (System.Reflection.Metadata). Library chosen via spike #2 — see
/// <c>docs/handoff-contract.md §8.1</c> for rationale.
/// </summary>
/// <remarks>
/// When constructed with <c>watchForChanges: true</c> the index installs a
/// <see cref="FileSystemWatcher"/> per loaded directory and re-reads the MVID on file
/// updates. A debounce window (<see cref="WatchDebounce"/>) coalesces rapid writes from
/// build tools. The watcher is opt-in so unit tests stay deterministic.
/// </remarks>
public sealed partial class MetadataIndex : IMetadataIndex, IDisposable
{
    /// <summary>Debounce window applied to <see cref="FileSystemWatcher"/> events. Mirrors <see cref="ModuleStore.WatchDebounce"/>.</summary>
    public static readonly TimeSpan WatchDebounce = ModuleStore.WatchDebounce;

    private readonly ModuleStore _store;
    private int _disposed;

    // Extracted per-module caches (#82). Each implements IModuleScopedCache and is registered
    // in _moduleScopedCaches so OnStoreModuleReloaded can fan out invalidation without
    // hardcoded knowledge of each cache. The four pre-existing ConcurrentDictionary fields
    // moved into their respective index classes.
    private readonly XrefIndex _xrefIndex;
    private readonly StringIndex _stringIndex;
    private readonly AttributeIndex _attributeIndex;
    private readonly FieldAccessIndex _fieldAccessIndex;
    private readonly MethodResolver _methodResolver;
    private readonly IlBodyReader _ilBodyReader;
    private readonly PdbResolver _pdbResolver;
    private readonly TypeNavigationIndex _typeNavigation;
    private readonly List<IModuleScopedCache> _moduleScopedCaches;

    private readonly ModuleScopedCache<R2RReaderBox> _r2rCache = new();
    private readonly string _xrefCacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cache", "dotnet-assembly-mcp");

    /// <summary>Raised after a watched file change has been processed (success or failure).</summary>
    public event EventHandler<ModuleReloadedEventArgs>? ModuleReloaded;

    /// <summary>Creates an index without filesystem watching (default).</summary>
    public MetadataIndex() : this(watchForChanges: false) { }

    /// <summary>Creates an index, optionally installing per-directory file watchers.</summary>
    /// <param name="watchForChanges">When true, reloads modules on disk changes and invalidates the old MVID.</param>
    public MetadataIndex(bool watchForChanges) : this(watchForChanges, allowedRoots: null) { }

    /// <summary>Creates an index with watcher and untrusted-path allow-list configuration.</summary>
    /// <param name="watchForChanges">When true, reloads modules on disk changes and invalidates the old MVID.</param>
    /// <param name="allowedRoots">
    /// Operator-configured trusted roots for the untrusted-path-hint contract (#150). <c>null</c>
    /// disables enforcement (back-compatible default); a non-null list restricts every filesystem
    /// load to paths whose canonical real location is contained in one of the roots.
    /// </param>
    public MetadataIndex(bool watchForChanges, IReadOnlyList<string>? allowedRoots)
    {
        _store = new ModuleStore(watchForChanges, allowedRoots);

        _xrefIndex = new XrefIndex(_xrefCacheDir);
        _stringIndex = new StringIndex(_store);
        _attributeIndex = new AttributeIndex(_store);
        _fieldAccessIndex = new FieldAccessIndex(_store, FindCallers);
        _methodResolver = new MethodResolver(_store);
        _ilBodyReader = new IlBodyReader(_methodResolver);
        _pdbResolver = new PdbResolver(_methodResolver);
        _typeNavigation = new TypeNavigationIndex(_store);

        // Subscriber list for module-reload fan-out (#82). Each entry is invalidated when a
        // module's MVID is replaced on disk. The R2R cache adapter wraps the runtime cache
        // that doesn't warrant a class of its own; the PDB cache now lives on PdbResolver
        // which implements IModuleScopedCache directly (#92).
        _moduleScopedCaches = new List<IModuleScopedCache>
        {
            _xrefIndex,
            _stringIndex,
            _attributeIndex,
            _fieldAccessIndex,
            _pdbResolver,
            _typeNavigation,
            _r2rCache,
        };

        // Forward lifecycle events: fan out to cache invalidation, then re-raise to subscribers
        // of the public MetadataIndex.ModuleReloaded event. This keeps the public API stable
        // while letting ModuleStore own the actual file-watch + load loop.
        _store.ModuleReloaded += OnStoreModuleReloaded;
    }

    private void OnStoreModuleReloaded(object? sender, ModuleReloadedEventArgs e)
    {
        // Drop downstream caches keyed by the affected MVID. Same-MVID rebuilds (oldMvid ==
        // newMvid) still need invalidation because the IL may have changed. Failed reloads
        // (Error != null) leave the still-loaded module's caches intact — the public event
        // shape is preserved, but the cache fan-out matches pre-extraction behaviour where
        // InvalidateXref was only called on the success path inside TryReload.
        if (e.Error is null && e.OldMvid is { } prev)
        {
            foreach (var cache in _moduleScopedCaches) cache.Invalidate(prev);
        }
        ModuleReloaded?.Invoke(this, e);
    }

    // ---- IModuleScopedCache adapters for caches that don't warrant their own class -------

    /// <summary>
    /// Boxes a nullable <see cref="R2R.R2RReader"/> so the cache slot can serve a negative
    /// result (no R2R section) without conflating it with a cache miss. The helper
    /// <see cref="ModuleScopedCache{TData}"/> requires a reference-type payload.
    /// </summary>
    private sealed record R2RReaderBox(R2R.R2RReader? Reader);

    /// <inheritdoc />
    public LoadResult Load(string path) => _store.Load(path);

    /// <inheritdoc />
    public IReadOnlyList<ModuleSummary> List() => _store.List();

    /// <inheritdoc />
    public ProbeResult Probe(string path) => _store.Probe(path);

    /// <inheritdoc />
    public void RegisterPathHint(Guid moduleVersionId, string path) =>
        _store.RegisterPathHint(moduleVersionId, path);

    /// <inheritdoc />
    public bool TryGetPathHint(Guid moduleVersionId, out string? path) =>
        _store.TryGetPathHint(moduleVersionId, out path);

    /// <inheritdoc />
    public IReadOnlyDictionary<Guid, string> PathHints => _store.PathHints;

    /// <inheritdoc />
    public void WatchPath(string path) => _store.WatchPath(path);

    /// <inheritdoc />
    public void Invalidate(Guid moduleVersionId)
    {
        // Preferred path: when we know a path for the MVID, delegate to ModuleStore.Reload.
        // It re-reads the PE from disk, atomically swaps the ModuleHandle (so derived caches
        // rebuilt after this call see the FRESH MetadataReader, not the stale one), removes
        // the old MVID entry if the on-disk MVID has drifted, and raises ModuleReloaded
        // — even on failure — which OnStoreModuleReloaded fans out to _moduleScopedCaches
        // and re-raises publicly (Decompiler / IlDisassembler then drop their LRU entries).
        if (_store.TryGet(moduleVersionId, out var module))
        {
            InvalidateViaStoreReload(moduleVersionId, module.Path);
            return;
        }
        if (_store.TryGetPathHint(moduleVersionId, out var hinted) && !string.IsNullOrEmpty(hinted))
        {
            InvalidateViaStoreReload(moduleVersionId, hinted!);
            return;
        }

        // Fallback: MVID was never loaded and no path hint exists — there is no PE to re-read.
        // Still fan out cache invalidation + a synthetic ModuleReloaded so any external
        // subscriber holding stale state for this MVID can drop it. The cache rebuild
        // contract isn't violated because there's no stale ModuleHandle to rebuild from
        // in the first place.
        foreach (var cache in _moduleScopedCaches) cache.Invalidate(moduleVersionId);
        ModuleReloaded?.Invoke(this, new ModuleReloadedEventArgs(string.Empty, moduleVersionId, moduleVersionId, null));
    }

    private void InvalidateViaStoreReload(Guid moduleVersionId, string path)
    {
        var result = _store.Reload(moduleVersionId, path);
        if (result.IsSuccess) return;
        // Reload failed (file vanished, IO error, corrupt PE). OnStoreModuleReloaded
        // intentionally skips internal cache fan-out on errors so that transient
        // file-watcher hiccups (ShareViolation mid-write) don't nuke valid caches.
        // Explicit Invalidate has different semantics — the caller is asserting the MVID
        // is known-stale — so we must clear our own per-module caches here too.
        foreach (var cache in _moduleScopedCaches) cache.Invalidate(moduleVersionId);
    }


    private const int MaxIntraCount = 10_000_000;
    private const int MaxOutboundCount = 10_000_000;
    private const int MaxIntraCallersPerCallee = 1_000_000;

    private static ModuleSummary SummarizeModule(ModuleHandle m) =>
        new(m.Mvid, Path.GetFileName(m.Path), m.Path, m.MD.MethodDefinitions.Count);

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _store.Dispose();
        _pdbResolver.Dispose();
    }

    /// <inheritdoc />
    public AssemblyError? EnsureLoaded(Guid moduleVersionId, string? assemblyPathHint)
    {
        if (moduleVersionId == Guid.Empty)
            return new AssemblyError(ErrorKinds.IdentityMalformed, "moduleVersionId is required.");
        if (_store.TryGet(moduleVersionId, out _)) return null;

        var hint = assemblyPathHint;
        if (string.IsNullOrWhiteSpace(hint) && _store.TryGetPathHint(moduleVersionId, out var lazy))
            hint = lazy;
        if (string.IsNullOrWhiteSpace(hint))
        {
            return new AssemblyError(ErrorKinds.ModuleNotFound,
                $"no loaded module has MVID {moduleVersionId:D}.");
        }

        var probe = _store.Probe(hint);
        if (probe.Error is not null) return probe.Error;
        if (probe.Mvid != moduleVersionId)
        {
            return new AssemblyError(
                ErrorKinds.MvidMismatch,
                $"assemblyPathHint {ErrorRedactor.RedactPath(hint)} has MVID {probe.Mvid:D} but the caller requested {moduleVersionId:D}.");
        }
        var load = _store.Load(hint);
        return load.IsSuccess ? null : load.Error;
    }
}
