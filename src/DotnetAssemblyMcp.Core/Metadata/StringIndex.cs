using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Handles;
using static DotnetAssemblyMcp.Core.Metadata.MetadataDisplay;

namespace DotnetAssemblyMcp.Core.Metadata;

/// <summary>
/// Per-module index of every <c>ldstr</c> user-string literal observed in IL bodies,
/// keyed by literal text. Extracted from MetadataIndex (#82) so the cache lifecycle
/// is owned by a single small class subscribed to module reloads via
/// <see cref="IModuleScopedCache"/>.
/// </summary>
/// <remarks>
/// In-memory cache entries are stamped with the file length + last-write-time the index
/// was built from. <see cref="FindStringReferences"/> rebuilds when those drift —
/// belt-and-suspenders for the explicit invalidation path through
/// <see cref="Invalidate"/>, so an unwatched file change between explicit reloads is
/// still caught.
/// </remarks>
internal sealed class StringIndex : IModuleScopedCache
{
    private readonly ModuleStore _store;
    private readonly ConcurrentDictionary<Guid, CacheEntry> _cache = new();

    public StringIndex(ModuleStore store) { _store = store; }

    public void Invalidate(Guid mvid) => _cache.TryRemove(mvid, out _);

    // Exposed for the IModuleScopedCache contract test — asserts the cache becomes empty
    // for a given MVID after Invalidate is called.
    internal bool HasCacheEntry(Guid mvid) => _cache.ContainsKey(mvid);

    public FindStringReferencesReadResult FindStringReferences(
        string query,
        StringMatchMode matchMode,
        Guid moduleVersionIdFilter = default,
        int maxHits = 0,
        CancellationToken cancellationToken = default)
    {
        if (query is null)
            return FindStringReferencesReadResult.Fail(new AssemblyError(ErrorKinds.InvalidArgument, "query is required."));
        if (matchMode == StringMatchMode.Exact && query.Length == 0)
            return FindStringReferencesReadResult.Fail(new AssemblyError(ErrorKinds.InvalidArgument, "query cannot be empty for exact match."));

        const int DefaultMaxHits = 1000;
        const int HardMaxHits = 10_000;
        if (maxHits <= 0) maxHits = DefaultMaxHits;
        if (maxHits > HardMaxHits) maxHits = HardMaxHits;

        System.Text.RegularExpressions.Regex? regex = null;
        if (matchMode == StringMatchMode.Regex)
        {
            try
            {
                regex = new System.Text.RegularExpressions.Regex(
                    query,
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant
                        | System.Text.RegularExpressions.RegexOptions.Compiled,
                    TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException ex)
            {
                return FindStringReferencesReadResult.Fail(new AssemblyError(
                    ErrorKinds.InvalidArgument, "regex pattern is invalid.", ex.Message));
            }
        }

        IEnumerable<ModuleHandle> targets;
        if (moduleVersionIdFilter != Guid.Empty)
        {
            if (!_store.TryGet(moduleVersionIdFilter, out var only))
            {
                return FindStringReferencesReadResult.Fail(new AssemblyError(
                    ErrorKinds.ModuleNotFound,
                    $"no loaded module has MVID {moduleVersionIdFilter:D}."));
            }
            targets = new[] { only };
        }
        else
        {
            targets = _store.Modules;
        }

        var hits = new List<StringReferenceRef>();
        var fromCache = true;
        var modulesSearched = 0;
        var truncated = false;

        foreach (var module in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            modulesSearched++;

            var index = GetOrBuild(module, cancellationToken, out var wasCached);
            if (!wasCached) fromCache = false;

            switch (matchMode)
            {
                case StringMatchMode.Exact:
                    if (index.ByLiteral.TryGetValue(query, out var exactSites))
                    {
                        if (!AppendHits(module, query, exactSites, hits, maxHits))
                        {
                            truncated = true;
                            goto done;
                        }
                    }
                    break;

                case StringMatchMode.Contains:
                    foreach (var (literal, sites) in index.ByLiteral)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (literal.Contains(query, StringComparison.Ordinal))
                        {
                            if (!AppendHits(module, literal, sites, hits, maxHits))
                            {
                                truncated = true;
                                goto done;
                            }
                        }
                    }
                    break;

                case StringMatchMode.Regex:
                    foreach (var (literal, sites) in index.ByLiteral)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        bool isMatch;
                        try
                        {
                            isMatch = regex!.IsMatch(literal);
                        }
                        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
                        {
                            return FindStringReferencesReadResult.Fail(new AssemblyError(
                                ErrorKinds.PatternTooBroad,
                                "regex evaluation exceeded the per-literal timeout (1s); refine the pattern."));
                        }
                        if (isMatch)
                        {
                            if (!AppendHits(module, literal, sites, hits, maxHits))
                            {
                                truncated = true;
                                goto done;
                            }
                        }
                    }
                    break;
            }
        }
    done:

        return FindStringReferencesReadResult.Ok(new FindStringReferencesResult(
            query, matchMode, hits, modulesSearched, FromCache: fromCache, Truncated: truncated));
    }

    private StringIndexData GetOrBuild(ModuleHandle module, CancellationToken cancellationToken, out bool wasCached)
    {
        var stamp = ModuleCacheStamp.TryCapture(module);
        if (_cache.TryGetValue(module.Mvid, out var entry) && entry.Stamp.Equals(stamp))
        {
            wasCached = true;
            return entry.Data;
        }
        wasCached = false;
        var data = BuildStringIndex(module, cancellationToken);
        _cache[module.Mvid] = new CacheEntry(data, stamp);
        return data;
    }

    private static bool AppendHits(ModuleHandle module, string literal, List<(int MethodToken, int IlOffset)> sites,
        List<StringReferenceRef> output, int maxHits)
    {
        foreach (var (token, offset) in sites)
        {
            if (output.Count >= maxHits) return false;
            var h = (MethodDefinitionHandle)MetadataTokens.Handle(token);
            output.Add(new StringReferenceRef(
                module.Mvid, token, HandleSyntax.FormatMethod(module.Mvid, token),
                RenderMethodDef(module, h),
                offset, literal));
        }
        return true;
    }

    private static StringIndexData BuildStringIndex(ModuleHandle module, CancellationToken cancellationToken)
    {
        var dict = new Dictionary<string, List<(int MethodToken, int IlOffset)>>(StringComparer.Ordinal);

        foreach (var methodHandle in module.MD.MethodDefinitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var def = module.MD.GetMethodDefinition(methodHandle);
            if (def.RelativeVirtualAddress == 0) continue;

            byte[] ilBytes;
            try
            {
                var body = module.PE.GetMethodBody(def.RelativeVirtualAddress);
                ilBytes = body.GetILBytes() ?? Array.Empty<byte>();
            }
            catch (BadImageFormatException) { continue; }

            var methodToken = MetadataTokens.GetToken(methodHandle);
            var span = ilBytes.AsSpan();
            int pos = 0;
            while (pos < span.Length)
            {
                int opStart = pos;
                var b1 = span[pos++];
                IlOpcodeTable.Op op;
                if (b1 == 0xFE)
                {
                    if (pos >= span.Length) break;
                    op = IlOpcodeTable.TwoByteOp(span[pos++]);
                }
                else
                {
                    op = IlOpcodeTable.OneByteOp(b1);
                }

                var size = IlOpcodeTable.OperandSize(op);
                if (size == -1) // switch
                {
                    if (pos + 4 > span.Length) break;
                    var n = BitConverter.ToInt32(span.Slice(pos, 4));
                    if (n < 0 || n > (span.Length - pos - 4) / 4) break;
                    pos += 4 + n * 4;
                    continue;
                }

                if (op == IlOpcodeTable.Op.InlineString && size == 4 && pos + 4 <= span.Length)
                {
                    var token = BitConverter.ToInt32(span.Slice(pos, 4));
                    var literal = TryReadUserString(module, token);
                    if (literal is not null)
                    {
                        if (!dict.TryGetValue(literal, out var list))
                        {
                            list = new List<(int, int)>(1);
                            dict[literal] = list;
                        }
                        list.Add((methodToken, opStart));
                    }
                }

                pos += Math.Max(0, size);
            }
        }

        return new StringIndexData(dict);
    }

    private sealed record CacheEntry(StringIndexData Data, ModuleCacheStamp Stamp);
}
