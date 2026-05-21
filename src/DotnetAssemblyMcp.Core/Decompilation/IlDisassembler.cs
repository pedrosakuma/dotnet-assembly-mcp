using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Identity;
using DotnetAssemblyMcp.Core.Metadata;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.Metadata;

namespace DotnetAssemblyMcp.Core.Decompilation;

/// <summary>
/// <see cref="IIlDisassembler"/> backed by <c>ICSharpCode.Decompiler.Disassembler.ReflectionDisassembler</c>.
/// Holds a bounded LRU cache of <see cref="MethodIlText"/> results keyed by
/// <c>(MVID, token, maxLines)</c> and a per-MVID <see cref="PEFile"/> kept open across calls
/// (the disassembler resolves operand tokens against this file).
/// </summary>
/// <remarks>
/// Cache budget mirrors <see cref="Decompiler"/>: at most <see cref="MaxEntries"/> entries,
/// at most <see cref="MaxResidentBytes"/> resident chars-as-bytes, <see cref="EntryTtl"/>
/// wall time per entry.
/// </remarks>
public sealed class IlDisassembler : IIlDisassembler, IDisposable
{
    public const int MaxEntries = 256;
    public const long MaxResidentBytes = 64L * 1024 * 1024;
    public const int DefaultMaxLines = 256;
    public const int HardMaxLines = 4096;
    public static readonly TimeSpan EntryTtl = TimeSpan.FromMinutes(10);

    private readonly IMetadataIndex _index;
    private readonly Func<DateTime> _now;
    private readonly object _lruLock = new();
    private readonly LinkedList<Entry> _lru = new();
    private readonly Dictionary<CacheKey, LinkedListNode<Entry>> _byKey = new();
    private readonly ConcurrentDictionary<Guid, CachedFile> _files = new();
    private long _residentBytes;

    public IlDisassembler(IMetadataIndex index) : this(index, () => DateTime.UtcNow) { }

    internal IlDisassembler(IMetadataIndex index, Func<DateTime> clock)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _now = clock ?? throw new ArgumentNullException(nameof(clock));
        _index.ModuleReloaded += OnModuleReloaded;
    }

    /// <inheritdoc />
    public int CachedEntries
    {
        get { lock (_lruLock) return _byKey.Count; }
    }

    /// <inheritdoc />
    public DisassembleResult Disassemble(MethodIdentity identity, int maxLines = 0, CancellationToken cancellationToken = default)
    {
        if (identity is null)
            return DisassembleResult.Fail(new AssemblyError(ErrorKinds.InvalidArgument, "identity is required."));

        cancellationToken.ThrowIfCancellationRequested();

        var cap = maxLines > 0 ? maxLines : DefaultMaxLines;
        if (cap > HardMaxLines) cap = HardMaxLines;
        var key = new CacheKey(identity.ModuleVersionId, identity.MetadataToken, cap);

        if (TryGetCached(key, out var cached))
            return DisassembleResult.Ok(cached! with { CacheHit = true });

        var resolved = _index.Resolve(identity);
        if (!resolved.IsSuccess)
            return DisassembleResult.Fail(resolved.Error!);

        var modulePath = TryGetLoadedModulePath(identity.ModuleVersionId);
        if (modulePath is null)
        {
            return DisassembleResult.Fail(new AssemblyError(
                ErrorKinds.ModuleNotFound,
                $"no loaded module has MVID {identity.ModuleVersionId}."));
        }

        var fileResult = GetOrOpen(identity.ModuleVersionId, modulePath);
        if (!fileResult.IsSuccess)
            return DisassembleResult.Fail(fileResult.Error!);

        var fileToken = fileResult.Token!;
        cancellationToken.ThrowIfCancellationRequested();

        string raw;
        try
        {
            var output = new PlainTextOutput();
            var disasm = new ReflectionDisassembler(output, cancellationToken)
            {
                DetectControlStructure = false,
            };
            var handle = (MethodDefinitionHandle)MetadataTokens.EntityHandle(identity.MetadataToken);
            disasm.DisassembleMethod(fileToken.File, handle);
            raw = output.ToString();
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or NotSupportedException)
        {
            return DisassembleResult.Fail(new AssemblyError(
                ErrorKinds.ModuleLoadFailed,
                $"disassembly failed: {ex.GetType().Name}.",
                ex.Message));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _files.TryRemove(identity.ModuleVersionId, out _);
            return DisassembleResult.Fail(new AssemblyError(
                ErrorKinds.ModuleLoadFailed,
                $"disassembly failed: {ex.GetType().Name}.",
                ex.Message));
        }

        var lines = raw.Split('\n');
        var instructionCount = CountInstructions(lines);

        bool truncated = false;
        string text;
        int lineCount;
        if (lines.Length > cap)
        {
            var kept = new List<string>(cap + 1);
            for (int i = 0; i < cap; i++) kept.Add(lines[i]);
            var remainingInstructions = CountInstructions(lines, cap);
            kept.Add($"// ... truncated, {remainingInstructions} more instructions");
            text = string.Join('\n', kept);
            lineCount = kept.Count;
            truncated = true;
        }
        else
        {
            text = raw;
            lineCount = lines.Length;
        }

        var summary = resolved.Method!;
        var entry = new MethodIlText(
            summary.ModuleVersionId,
            summary.MetadataToken,
            summary.Handle,
            summary.TypeFullName,
            summary.MethodName,
            text,
            lineCount,
            instructionCount,
            truncated,
            CacheHit: false);

        Insert(key, entry, fileToken);
        return DisassembleResult.Ok(entry);
    }

    private static int CountInstructions(string[] lines, int startIndex = 0)
    {
        int n = 0;
        for (int i = startIndex; i < lines.Length; i++)
        {
            var trimmed = lines[i].AsSpan().TrimStart();
            if (trimmed.StartsWith("IL_".AsSpan(), StringComparison.Ordinal))
                n++;
        }
        return n;
    }

    private FileResult GetOrOpen(Guid mvid, string modulePath)
    {
        if (_files.TryGetValue(mvid, out var cached) && string.Equals(cached.Path, modulePath, StringComparison.Ordinal))
            return new FileResult(cached, null);

        try
        {
            var pef = new PEFile(modulePath);
            var token = new CachedFile(modulePath, pef);
            _files[mvid] = token;
            return new FileResult(token, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException)
        {
            return new FileResult(null, new AssemblyError(
                ErrorKinds.ModuleLoadFailed,
                $"disassembler could not open module: {ex.GetType().Name}.",
                ex.Message));
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException)
        {
            return new FileResult(null, new AssemblyError(
                ErrorKinds.ModuleLoadFailed,
                $"disassembler could not open module: {ex.GetType().Name}.",
                ex.Message));
        }
    }

    private void OnModuleReloaded(object? sender, ModuleReloadedEventArgs e)
    {
        if (e.OldMvid is Guid old)
        {
            if (_files.TryRemove(old, out var dead))
                dead.File.Dispose();
            lock (_lruLock)
            {
                var deadKeys = _byKey.Keys.Where(k => k.Mvid == old).ToList();
                foreach (var k in deadKeys)
                    if (_byKey.TryGetValue(k, out var node))
                        RemoveNode(node);
            }
        }
    }

    private bool TryGetCached(CacheKey key, out MethodIlText? value)
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

    private void Insert(CacheKey key, MethodIlText value, CachedFile fileToken)
    {
        var bytes = (long)value.Text.Length * 2;
        lock (_lruLock)
        {
            if (!_files.TryGetValue(key.Mvid, out var live) || !ReferenceEquals(live, fileToken))
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

    private string? TryGetLoadedModulePath(Guid mvid) =>
        _index.List().FirstOrDefault(m => m.ModuleVersionId == mvid)?.ModulePath;

    private readonly record struct CacheKey(Guid Mvid, int Token, int MaxLines);
    private sealed record Entry(CacheKey Key, MethodIlText Value, DateTime InsertedAt, long Bytes);
    private sealed record CachedFile(string Path, PEFile File);
    private readonly record struct FileResult(CachedFile? Token, AssemblyError? Error)
    {
        public bool IsSuccess => Error is null;
    }

    public void Dispose()
    {
        _index.ModuleReloaded -= OnModuleReloaded;
        foreach (var f in _files.Values)
            f.File.Dispose();
        _files.Clear();
        lock (_lruLock)
        {
            _lru.Clear();
            _byKey.Clear();
            _residentBytes = 0;
        }
    }
}
