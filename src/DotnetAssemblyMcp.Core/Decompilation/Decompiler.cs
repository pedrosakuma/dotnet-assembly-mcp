using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Identity;
using DotnetAssemblyMcp.Core.Metadata;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;

namespace DotnetAssemblyMcp.Core.Decompilation;

/// <summary>
/// <see cref="IDecompiler"/> backed by ICSharpCode.Decompiler (the engine that powers ILSpy).
/// Holds a bounded LRU cache of <see cref="DecompiledMethod"/> results keyed by
/// <c>(MVID, token, maxChars)</c>.
/// </summary>
/// <remarks>
/// Cache budget per <c>docs/mcp-conventions.md §2.1</c>: at most <see cref="MaxEntries"/>
/// entries, at most <see cref="MaxResidentBytes"/> resident chars-as-bytes (UTF-16),
/// and at most <see cref="EntryTtl"/> wall time per entry. The first cap hit wins.
///
/// Each module reload (a new MVID) makes the previous MVID's cache entries dead — they
/// remain in the LRU until they age out or get evicted by space pressure, which is fine:
/// they are keyed by MVID, so no stale source can ever be served.
/// </remarks>
public sealed class Decompiler : IDecompiler
{
    /// <summary>Hard cap on entries. Older entries are evicted on insert.</summary>
    public const int MaxEntries = 256;

    /// <summary>Hard cap on resident bytes (UTF-16 chars × 2). 64 MiB per conventions §2.1.</summary>
    public const long MaxResidentBytes = 64L * 1024 * 1024;

    /// <summary>Default upper bound on a single response. Mirrors the MCP "bounded output" rule.</summary>
    public const int DefaultMaxChars = 16 * 1024;

    /// <summary>Per-entry wall-time TTL.</summary>
    public static readonly TimeSpan EntryTtl = TimeSpan.FromMinutes(10);

    private readonly IMetadataIndex _index;
    private readonly Func<DateTime> _now;
    private readonly object _lruLock = new();
    private readonly LinkedList<Entry> _lru = new();
    private readonly Dictionary<CacheKey, LinkedListNode<Entry>> _byKey = new();
    private readonly ConcurrentDictionary<Guid, CachedDecompiler> _engines = new();
    private long _residentBytes;

    public Decompiler(IMetadataIndex index) : this(index, () => DateTime.UtcNow) { }

    internal Decompiler(IMetadataIndex index, Func<DateTime> clock)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _now = clock ?? throw new ArgumentNullException(nameof(clock));

        // Drop cached engines + sources for any module that reloaded on disk.
        if (_index is MetadataIndex mi)
            mi.ModuleReloaded += OnModuleReloaded;
    }

    /// <inheritdoc />
    public int CachedEntries
    {
        get { lock (_lruLock) return _byKey.Count; }
    }

    /// <inheritdoc />
    public DecompileResult Decompile(MethodIdentity identity, int maxChars = 0)
    {
        if (identity is null)
            return DecompileResult.Fail(new AssemblyError(ErrorKinds.InvalidArgument, "identity is required."));

        var cap = maxChars > 0 ? maxChars : DefaultMaxChars;
        var key = new CacheKey(identity.ModuleVersionId, identity.MetadataToken, cap);

        if (TryGetCached(key, out var cached))
            return DecompileResult.Ok(cached! with { CacheHit = true });

        var resolved = _index.Resolve(identity);
        if (!resolved.IsSuccess)
            return DecompileResult.Fail(resolved.Error!);

        var module = TryGetLoadedModulePath(identity.ModuleVersionId);
        if (module is null)
        {
            return DecompileResult.Fail(new AssemblyError(
                ErrorKinds.ModuleNotFound,
                $"no loaded module has MVID {identity.ModuleVersionId}."));
        }

        var engineResult = GetOrCreateEngine(identity.ModuleVersionId, module);
        if (!engineResult.IsSuccess)
            return DecompileResult.Fail(engineResult.Error!);

        string source;
        try
        {
            var handle = MetadataTokens.EntityHandle(identity.MetadataToken);
            source = engineResult.Engine!.DecompileAsString(handle);
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or NotSupportedException)
        {
            return DecompileResult.Fail(new AssemblyError(
                ErrorKinds.ModuleLoadFailed,
                $"decompilation failed: {ex.GetType().Name}.",
                ex.Message));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The PE became unavailable between load and decompile (deleted, locked, perms changed).
            _engines.TryRemove(identity.ModuleVersionId, out _);
            return DecompileResult.Fail(new AssemblyError(
                ErrorKinds.ModuleLoadFailed,
                $"decompilation failed: {ex.GetType().Name}.",
                ex.Message));
        }

        var truncated = false;
        if (source.Length > cap)
        {
            source = string.Concat(source.AsSpan(0, cap), "\n// ... [truncated by server: exceeded maxChars]\n");
            truncated = true;
        }

        var summary = resolved.Method!;
        var entry = new DecompiledMethod(
            summary.ModuleVersionId,
            summary.MetadataToken,
            summary.Handle,
            summary.TypeFullName,
            summary.MethodName,
            source,
            source.Length,
            truncated,
            CacheHit: false);

        Insert(key, entry);
        return DecompileResult.Ok(entry);
    }

    private EngineResult GetOrCreateEngine(Guid mvid, string modulePath)
    {
        if (_engines.TryGetValue(mvid, out var cached) && string.Equals(cached.Path, modulePath, StringComparison.Ordinal))
            return new EngineResult(cached.Engine, null);

        try
        {
            var csd = new CSharpDecompiler(modulePath, new DecompilerSettings
            {
                ThrowOnAssemblyResolveErrors = false,
                ShowXmlDocumentation = false,
            });
            _engines[mvid] = new CachedDecompiler(modulePath, csd);
            return new EngineResult(csd, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException)
        {
            return new EngineResult(null, new AssemblyError(
                ErrorKinds.ModuleLoadFailed,
                $"decompiler could not open module: {ex.GetType().Name}.",
                ex.Message));
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException)
        {
            return new EngineResult(null, new AssemblyError(
                ErrorKinds.ModuleLoadFailed,
                $"decompiler could not open module: {ex.GetType().Name}.",
                ex.Message));
        }
    }

    private void OnModuleReloaded(object? sender, ModuleReloadedEventArgs e)
    {
        // Drop the engine + any cached source for the previous MVID. The new MVID has
        // its own keyspace, so live entries on it are unaffected.
        if (e.OldMvid is Guid old)
        {
            _engines.TryRemove(old, out _);
            lock (_lruLock)
            {
                var dead = _byKey.Keys.Where(k => k.Mvid == old).ToList();
                foreach (var k in dead)
                    if (_byKey.TryGetValue(k, out var node))
                        RemoveNode(node);
            }
        }
    }


    private bool TryGetCached(CacheKey key, out DecompiledMethod? value)
    {
        lock (_lruLock)
        {
            if (_byKey.TryGetValue(key, out var node))
            {
                if (_now() - node.Value.InsertedAt > EntryTtl)
                {
                    RemoveNode(node);
                    value = null;
                    return false;
                }
                _lru.Remove(node);
                _lru.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }
        value = null;
        return false;
    }

    private void Insert(CacheKey key, DecompiledMethod value)
    {
        var bytes = (long)value.SourceLengthChars * 2;
        lock (_lruLock)
        {
            if (_byKey.TryGetValue(key, out var existing))
                RemoveNode(existing);

            var node = new LinkedListNode<Entry>(new Entry(key, value, _now(), bytes));
            _lru.AddFirst(node);
            _byKey[key] = node;
            _residentBytes += bytes;

            while (_byKey.Count > MaxEntries || _residentBytes > MaxResidentBytes)
            {
                var last = _lru.Last;
                if (last is null) break;
                RemoveNode(last);
            }
        }
    }

    private void RemoveNode(LinkedListNode<Entry> node)
    {
        _lru.Remove(node);
        _byKey.Remove(node.Value.Key);
        _residentBytes -= node.Value.Bytes;
        if (_residentBytes < 0) _residentBytes = 0;
    }

    private string? TryGetLoadedModulePath(Guid mvid) =>
        _index.List().FirstOrDefault(m => m.ModuleVersionId == mvid)?.ModulePath;

    private readonly record struct CacheKey(Guid Mvid, int Token, int MaxChars);
    private sealed record Entry(CacheKey Key, DecompiledMethod Value, DateTime InsertedAt, long Bytes);
    private sealed record CachedDecompiler(string Path, CSharpDecompiler Engine);
    private readonly record struct EngineResult(CSharpDecompiler? Engine, AssemblyError? Error)
    {
        public bool IsSuccess => Error is null;
    }
}
