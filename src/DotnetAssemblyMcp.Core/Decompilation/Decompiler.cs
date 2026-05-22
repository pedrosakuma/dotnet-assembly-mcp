using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Identity;
using DotnetAssemblyMcp.Core.Metadata;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
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
public sealed class Decompiler : IDecompiler, IDisposable
{
    /// <summary>Hard cap on entries. Older entries are evicted on insert.</summary>
    public const int MaxEntries = 256;

    /// <summary>Hard cap on resident bytes (UTF-16 chars × 2). 64 MiB per conventions §2.1.</summary>
    public const long MaxResidentBytes = 64L * 1024 * 1024;

    /// <summary>Default upper bound on a single response. Mirrors the MCP "bounded output" rule.</summary>
    public const int DefaultMaxChars = 16 * 1024;

    /// <summary>Default upper bound on a single whole-type response. Four times <see cref="DefaultMaxChars"/>
    /// because a type body naturally aggregates N method bodies plus declarations.</summary>
    public const int DefaultTypeMaxChars = 64 * 1024;

    /// <summary>
    /// Server-side absolute ceiling on a single method response. Callers may pass any
    /// <c>maxChars</c> they like — we clamp to this. Defends against a hostile assembly +
    /// caller pair that uses <c>maxChars=int.MaxValue</c> to drive arbitrary allocations.
    /// 1 MiB is &gt;&gt; the largest real method body we have ever produced.
    /// </summary>
    public const int HardMaxChars = 1 * 1024 * 1024;

    /// <summary>Same ceiling for whole-type decompiles. 4 MiB matches the 4× ratio
    /// between <see cref="DefaultMaxChars"/> and <see cref="DefaultTypeMaxChars"/>.</summary>
    public const int HardMaxTypeChars = 4 * 1024 * 1024;

    /// <summary>
    /// Wall-time cap on a single decompile call. Combined with the caller-supplied
    /// <see cref="CancellationToken"/> via <see cref="CancellationTokenSource.CreateLinkedTokenSource(CancellationToken[])"/>.
    /// Defeats pathological-assembly CPU exhaustion regardless of how cheap the input PE looks
    /// at metadata level.
    /// </summary>
    public static readonly TimeSpan DefaultDecompileTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Per-entry wall-time TTL.</summary>
    public static readonly TimeSpan EntryTtl = TimeSpan.FromMinutes(10);

    private readonly IMetadataIndex _index;
    private readonly Func<DateTime> _now;
    private readonly object _lruLock = new();
    private readonly LinkedList<Entry> _lru = new();
    private readonly Dictionary<CacheKey, LinkedListNode<Entry>> _byKey = new();
    private readonly LinkedList<TypeEntry> _typeLru = new();
    private readonly Dictionary<CacheKey, LinkedListNode<TypeEntry>> _typeByKey = new();
    private readonly ConcurrentDictionary<Guid, CachedDecompiler> _engines = new();
    private long _residentBytes;
    private long _typeResidentBytes;

    public Decompiler(IMetadataIndex index) : this(index, () => DateTime.UtcNow) { }

    internal Decompiler(IMetadataIndex index, Func<DateTime> clock)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _now = clock ?? throw new ArgumentNullException(nameof(clock));

        // Drop cached engines + sources for any module that reloaded on disk.
        _index.ModuleReloaded += OnModuleReloaded;
    }

    /// <inheritdoc />
    public int CachedEntries
    {
        get { lock (_lruLock) return _byKey.Count + _typeByKey.Count; }
    }

    /// <inheritdoc />
    public DecompileResult Decompile(MethodIdentity identity, int maxChars = 0, CancellationToken cancellationToken = default)
    {
        if (identity is null)
            return DecompileResult.Fail(new AssemblyError(ErrorKinds.InvalidArgument, "identity is required."));

        cancellationToken.ThrowIfCancellationRequested();

        var cap = maxChars > 0 ? maxChars : DefaultMaxChars;
        if (cap > HardMaxChars) cap = HardMaxChars;
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

        // Snapshot the engine reference we're about to use. OnModuleReloaded swaps the
        // entry in _engines atomically — comparing references after Decompile lets us
        // detect a concurrent reload and skip caching a source built from the old PE.
        var engineToken = engineResult.Token!;

        cancellationToken.ThrowIfCancellationRequested();

        string source;
        bool truncated;
        try
        {
            var handle = MetadataTokens.EntityHandle(identity.MetadataToken);
            (source, truncated) = RunDecompile(
                engineResult.Engine!, engineToken, handle, cap, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return DecompileResult.Fail(new AssemblyError(
                ErrorKinds.ModuleLoadFailed,
                $"decompilation timed out after {DefaultDecompileTimeout.TotalSeconds:N0}s."));
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

        var truncatedNote = "\n// ... [truncated by server: exceeded maxChars]\n";
        if (truncated)
        {
            source = string.Concat(source, truncatedNote);
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

        Insert(key, entry, engineToken);
        return DecompileResult.Ok(entry);
    }

    /// <inheritdoc />
    public DecompileTypeResult DecompileType(Guid moduleVersionId, int typeMetadataToken, int maxChars = 0, CancellationToken cancellationToken = default)
    {
        if (moduleVersionId == Guid.Empty)
            return DecompileTypeResult.Fail(new AssemblyError(ErrorKinds.IdentityMalformed, "moduleVersionId is required."));

        cancellationToken.ThrowIfCancellationRequested();

        var cap = maxChars > 0 ? maxChars : DefaultTypeMaxChars;
        if (cap > HardMaxTypeChars) cap = HardMaxTypeChars;
        var key = new CacheKey(moduleVersionId, typeMetadataToken, cap);

        if (TryGetCachedType(key, out var cached))
            return DecompileTypeResult.Ok(cached! with { CacheHit = true });

        // Validate the token is a TypeDef before opening the engine — cheap, gives the
        // identity_malformed error path symmetry with decompile_method.
        EntityHandle entityHandle;
        try { entityHandle = MetadataTokens.EntityHandle(typeMetadataToken); }
        catch (ArgumentException)
        {
            return DecompileTypeResult.Fail(new AssemblyError(
                ErrorKinds.IdentityMalformed,
                $"'{typeMetadataToken:X8}' is not a valid metadata token."));
        }
        if (entityHandle.Kind != HandleKind.TypeDefinition)
        {
            return DecompileTypeResult.Fail(new AssemblyError(
                ErrorKinds.IdentityMalformed,
                $"token '{typeMetadataToken:X8}' is a {entityHandle.Kind}, expected TypeDefinition (table 0x02)."));
        }
        var typeHandle = (TypeDefinitionHandle)entityHandle;

        // Cross-check the type exists in the loaded module — gives a clean type_not_found rather
        // than a NullReferenceException out of the decompiler engine.
        var typeProbe = _index.GetTypeDefinition(moduleVersionId, typeMetadataToken);
        if (!typeProbe.IsSuccess)
            return DecompileTypeResult.Fail(typeProbe.Error!);
        var typeSummary = typeProbe.Type!;

        var module = TryGetLoadedModulePath(moduleVersionId);
        if (module is null)
        {
            return DecompileTypeResult.Fail(new AssemblyError(
                ErrorKinds.ModuleNotFound,
                $"no loaded module has MVID {moduleVersionId}."));
        }

        var engineResult = GetOrCreateEngine(moduleVersionId, module);
        if (!engineResult.IsSuccess)
            return DecompileTypeResult.Fail(engineResult.Error!);

        var engineToken = engineResult.Token!;

        cancellationToken.ThrowIfCancellationRequested();

        string source;
        bool truncated;
        try
        {
            (source, truncated) = RunDecompileTypes(
                engineResult.Engine!, engineToken, new[] { typeHandle }, cap, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return DecompileTypeResult.Fail(new AssemblyError(
                ErrorKinds.ModuleLoadFailed,
                $"decompilation timed out after {DefaultDecompileTimeout.TotalSeconds:N0}s."));
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or NotSupportedException)
        {
            return DecompileTypeResult.Fail(new AssemblyError(
                ErrorKinds.ModuleLoadFailed,
                $"decompilation failed: {ex.GetType().Name}.",
                ex.Message));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _engines.TryRemove(moduleVersionId, out _);
            return DecompileTypeResult.Fail(new AssemblyError(
                ErrorKinds.ModuleLoadFailed,
                $"decompilation failed: {ex.GetType().Name}.",
                ex.Message));
        }

        if (truncated)
        {
            source = string.Concat(source, "\n// ... [truncated by server: exceeded maxChars]\n");
        }

        var entry = new DecompiledType(
            moduleVersionId,
            typeMetadataToken,
            typeSummary.Handle,
            typeSummary.FullName,
            source,
            source.Length,
            truncated,
            CacheHit: false);

        InsertType(key, entry, engineToken);
        return DecompileTypeResult.Ok(entry);
    }

    private EngineResult GetOrCreateEngine(Guid mvid, string modulePath)
    {
        if (_engines.TryGetValue(mvid, out var cached) && string.Equals(cached.Path, modulePath, StringComparison.Ordinal))
            return new EngineResult(cached.Engine, cached, null);

        // SECURITY: Re-route the open through SafeFileOpener so a file that was safely loaded
        // through ModuleStore but then swapped on disk for a symlink / oversized file before
        // the first decompile cannot bypass the size cap or reparse-point check that
        // ModuleStore enforced at load time. CSharpDecompiler's (string) constructor uses
        // PEFile(string) which calls File.OpenRead under the hood — that path does NOT honor
        // O_NOFOLLOW. We hand it a MemoryStream over already-validated bytes instead.
        var streamResult = IO.SafeFileOpener.OpenReadAsStream(modulePath, IO.SafeFileOpener.DefaultMaxAssemblyBytes);
        if (!streamResult.IsSuccess)
            return new EngineResult(null, null, streamResult.Error);

        try
        {
            // PEFile owns the stream and disposes it on dispose; CSharpDecompiler owns the
            // PEFile via its IDecompilerTypeSystem and disposes it when the engine is dropped.
            // Mirror CSharpDecompiler(string, settings) -> CreateTypeSystemFromFile internals:
            // detect TFM + runtime pack from the loaded PE and prefetch metadata of resolved
            // assemblies (otherwise FileStreams stay open until GC because resolver-opened
            // PEFiles are not deterministically disposed by CSharpDecompiler).
            var settings = new DecompilerSettings
            {
                ThrowOnAssemblyResolveErrors = false,
                ShowXmlDocumentation = false,
                LoadInMemory = true,
            };
            var pef = new PEFile(
                modulePath,
                streamResult.Stream!,
                PEStreamOptions.PrefetchEntireImage,
                metadataOptions: settings.ApplyWindowsRuntimeProjections
                    ? MetadataReaderOptions.ApplyWindowsRuntimeProjections
                    : MetadataReaderOptions.None);
            var resolver = new UniversalAssemblyResolver(
                modulePath,
                throwOnError: false,
                targetFramework: pef.DetectTargetFrameworkId(),
                runtimePack: pef.DetectRuntimePack(),
                streamOptions: PEStreamOptions.PrefetchMetadata,
                metadataOptions: settings.ApplyWindowsRuntimeProjections
                    ? MetadataReaderOptions.ApplyWindowsRuntimeProjections
                    : MetadataReaderOptions.None);
            var csd = new CSharpDecompiler(pef, resolver, settings);
            var token = new CachedDecompiler(modulePath, csd);
            _engines[mvid] = token;
            return new EngineResult(csd, token, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException)
        {
            streamResult.Stream?.Dispose();
            return new EngineResult(null, null, new AssemblyError(
                ErrorKinds.ModuleLoadFailed,
                $"decompiler could not open module: {ex.GetType().Name}.",
                ErrorRedactor.Redact(ex.Message)));
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException)
        {
            streamResult.Stream?.Dispose();
            return new EngineResult(null, null, new AssemblyError(
                ErrorKinds.ModuleLoadFailed,
                $"decompiler could not open module: {ex.GetType().Name}.",
                ErrorRedactor.Redact(ex.Message)));
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
                var deadTypes = _typeByKey.Keys.Where(k => k.Mvid == old).ToList();
                foreach (var k in deadTypes)
                    if (_typeByKey.TryGetValue(k, out var node))
                        RemoveTypeNode(node);
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

    private void Insert(CacheKey key, DecompiledMethod value, CachedDecompiler engineToken)
    {
        var bytes = (long)value.SourceLengthChars * 2;
        lock (_lruLock)
        {
            // Reload race guard: between Decompile() and Insert(), OnModuleReloaded may
            // have swapped (or removed) the engine for this MVID. If the engine we used
            // is no longer the live one, drop the result on the floor — re-running the
            // decompile next call will produce one keyed to the new MVID/engine.
            if (!_engines.TryGetValue(key.Mvid, out var live) || !ReferenceEquals(live, engineToken))
                return;

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

    private bool TryGetCachedType(CacheKey key, out DecompiledType? value)
    {
        lock (_lruLock)
        {
            if (_typeByKey.TryGetValue(key, out var node))
            {
                if (_now() - node.Value.InsertedAt > EntryTtl)
                {
                    RemoveTypeNode(node);
                    value = null;
                    return false;
                }
                _typeLru.Remove(node);
                _typeLru.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }
        value = null;
        return false;
    }

    private void InsertType(CacheKey key, DecompiledType value, CachedDecompiler engineToken)
    {
        var bytes = (long)value.SourceLengthChars * 2;
        lock (_lruLock)
        {
            // Reload race guard, mirroring the method cache: if the engine we used is no longer
            // live, drop the result on the floor — a re-run will key against the new engine.
            if (!_engines.TryGetValue(key.Mvid, out var live) || !ReferenceEquals(live, engineToken))
                return;

            if (_typeByKey.TryGetValue(key, out var existing))
                RemoveTypeNode(existing);

            var node = new LinkedListNode<TypeEntry>(new TypeEntry(key, value, _now(), bytes));
            _typeLru.AddFirst(node);
            _typeByKey[key] = node;
            _typeResidentBytes += bytes;

            // The type cache has its own budget independent of the method cache. A whole-type
            // decompile is naturally larger than a method body, so giving each path its own
            // 64 MiB ceiling avoids a hot type sweep evicting cold-but-useful method results
            // (or vice-versa). Total worst-case footprint stays bounded at 2 × MaxResidentBytes.
            while (_typeByKey.Count > MaxEntries || _typeResidentBytes > MaxResidentBytes)
            {
                var last = _typeLru.Last;
                if (last is null) break;
                RemoveTypeNode(last);
            }
        }
    }

    private void RemoveTypeNode(LinkedListNode<TypeEntry> node)
    {
        _typeLru.Remove(node);
        _typeByKey.Remove(node.Value.Key);
        _typeResidentBytes -= node.Value.Bytes;
        if (_typeResidentBytes < 0) _typeResidentBytes = 0;
    }

    private static (string Source, bool Truncated) RunDecompile(
        CSharpDecompiler engine, CachedDecompiler engineToken, EntityHandle handle,
        int cap, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(DefaultDecompileTimeout);
        ICSharpCode.Decompiler.CSharp.Syntax.SyntaxTree tree;
        lock (engineToken)
        {
            // Snapshot inside the lock — concurrent same-MVID requests serialize on engineToken
            // and must not observe each other's transient CT assignments. Reading savedCt
            // outside the lock would let request B persist request A's (potentially canceled)
            // linked token back onto the engine after A finishes.
            var savedCt = engine.CancellationToken;
            engine.CancellationToken = cts.Token;
            try { tree = engine.Decompile(handle); }
            finally { engine.CancellationToken = savedCt; }
        }
        return SerializeWithCap(tree, cap);
    }

    private static (string Source, bool Truncated) RunDecompileTypes(
        CSharpDecompiler engine, CachedDecompiler engineToken,
        IEnumerable<TypeDefinitionHandle> handles, int cap, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(DefaultDecompileTimeout);
        ICSharpCode.Decompiler.CSharp.Syntax.SyntaxTree tree;
        lock (engineToken)
        {
            var savedCt = engine.CancellationToken;
            engine.CancellationToken = cts.Token;
            try { tree = engine.DecompileTypes(handles); }
            finally { engine.CancellationToken = savedCt; }
        }
        return SerializeWithCap(tree, cap);
    }

    private static (string Source, bool Truncated) SerializeWithCap(
        ICSharpCode.Decompiler.CSharp.Syntax.SyntaxTree tree, int cap)
    {
        using var writer = new LimitingStringWriter(cap);
        var truncated = false;
        try
        {
            // GetOrCreateEngine constructs CSharpDecompiler with only ThrowOnAssemblyResolveErrors /
            // ShowXmlDocumentation set; CSharpFormattingOptions are defaulted. Re-create a settings
            // instance here for the visitor — identical formatting, no need to expose the engine's
            // private Settings field.
            var settings = new DecompilerSettings();
            tree.AcceptVisitor(new CSharpOutputVisitor(writer, settings.CSharpFormattingOptions));
        }
        catch (LimitExceededException)
        {
            truncated = true;
        }
        return (writer.ToString(), truncated);
    }

    /// <summary>
    /// <see cref="StringWriter"/> that throws <see cref="LimitExceededException"/> when the
    /// total characters written exceed <c>limit</c>. Truncates the in-flight call at the
    /// last legal char so the partial output is still well-formed UTF-16.
    /// </summary>
    private sealed class LimitingStringWriter : StringWriter
    {
        private readonly int _limit;
        public LimitingStringWriter(int limit) { _limit = limit; }

        public override void Write(char value)
        {
            CheckBudget(1);
            base.Write(value);
        }

        public override void Write(string? value)
        {
            if (value is null) return;
            CheckBudget(value.Length);
            base.Write(value);
        }

        public override void Write(char[] buffer, int index, int count)
        {
            CheckBudget(count);
            base.Write(buffer, index, count);
        }

        public override void Write(ReadOnlySpan<char> buffer)
        {
            CheckBudget(buffer.Length);
            base.Write(buffer);
        }

        private void CheckBudget(int incoming)
        {
            // GetStringBuilder().Length is the authoritative length already emitted.
            if ((long)GetStringBuilder().Length + incoming > _limit)
                throw new LimitExceededException();
        }
    }

    private sealed class LimitExceededException : Exception { }

    private string? TryGetLoadedModulePath(Guid mvid) =>
        _index.List().FirstOrDefault(m => m.ModuleVersionId == mvid)?.ModulePath;

    private readonly record struct CacheKey(Guid Mvid, int Token, int MaxChars);
    private sealed record Entry(CacheKey Key, DecompiledMethod Value, DateTime InsertedAt, long Bytes);
    private sealed record TypeEntry(CacheKey Key, DecompiledType Value, DateTime InsertedAt, long Bytes);
    private sealed record CachedDecompiler(string Path, CSharpDecompiler Engine);
    private readonly record struct EngineResult(CSharpDecompiler? Engine, CachedDecompiler? Token, AssemblyError? Error)
    {
        public bool IsSuccess => Error is null;
    }

    /// <inheritdoc cref="IDisposable.Dispose"/>
    public void Dispose()
    {
        _index.ModuleReloaded -= OnModuleReloaded;
        _engines.Clear();
        lock (_lruLock)
        {
            _lru.Clear();
            _byKey.Clear();
            _residentBytes = 0;
            _typeLru.Clear();
            _typeByKey.Clear();
            _typeResidentBytes = 0;
        }
    }
}
