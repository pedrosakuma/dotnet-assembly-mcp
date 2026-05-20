using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Handles;
using DotnetAssemblyMcp.Core.Identity;

namespace DotnetAssemblyMcp.Core.Metadata.Resolvers;

/// <summary>
/// PDB-backed source-location resolver. Extracted from <see cref="MetadataIndex"/> in issue
/// #92. Owns the per-module PDB cache directly (replacing the old <c>PdbCacheAdapter</c>),
/// implementing <see cref="IModuleScopedCache"/> for automatic invalidation on module
/// reload, and <see cref="IDisposable"/> to release embedded-portable-PDB providers that
/// pin native memory.
/// </summary>
internal sealed class PdbResolver : IModuleScopedCache, IDisposable
{
    // Cache one open MetadataReaderProvider per module so repeated get_method_source calls
    // don't re-open the PDB. Disposed alongside the resolver.
    private readonly ConcurrentDictionary<Guid, PdbHandle?> _sourceCache = new();

    private static readonly Guid SourceLinkCdiKind =
        new("CC110556-A091-4D38-9FEC-25AB9A351A6A");

    private readonly MethodResolver _methods;

    public PdbResolver(MethodResolver methods) => _methods = methods;

    private sealed record PdbHandle(MetadataReaderProvider? Provider, MetadataReader? Reader, PdbKind Kind, int Age);

    public MethodSourceResult GetMethodSource(MethodIdentity identity)
    {
        var common = _methods.TryResolveMethod(identity);
        if (common.Error is not null) return MethodSourceResult.Fail(common.Error);
        var module = common.Module!;
        var methodHandle = common.Handle;
        var handleStr = HandleSyntax.FormatMethod(module.Mvid, identity.MetadataToken);

        var pdb = _sourceCache.GetOrAdd(module.Mvid, _ => TryOpenPdb(module));
        if (pdb is null)
        {
            return MethodSourceResult.Ok(new MethodSourceLocation(
                module.Mvid, identity.MetadataToken, handleStr,
                Found: false, File: null, StartLine: null, EndLine: null, SourceLink: null,
                PdbKind: PdbKind.None, PdbAge: null,
                Reason: "no PDB found (embedded or sibling .pdb)"));
        }
        if (pdb.Reader is null)
        {
            // Currently the only path here is a Windows (MSF7) PDB sibling that we can't read with
            // System.Reflection.Metadata. Surface kind so consumers know a PDB exists but is unsupported.
            return MethodSourceResult.Ok(new MethodSourceLocation(
                module.Mvid, identity.MetadataToken, handleStr,
                Found: false, File: null, StartLine: null, EndLine: null, SourceLink: null,
                PdbKind: pdb.Kind, PdbAge: pdb.Age,
                Reason: "PDB present but unsupported (Windows/MSF7 format; portable PDB required)"));
        }

        // PDB MethodDebugInformation table is parallel to MethodDef — same row id.
        var rid = MetadataTokens.GetRowNumber(methodHandle);
        var debugHandle = MetadataTokens.MethodDebugInformationHandle(rid);

        MethodDebugInformation debugInfo;
        try { debugInfo = pdb.Reader!.GetMethodDebugInformation(debugHandle); }
        catch (BadImageFormatException)
        {
            return MethodSourceResult.Ok(NoSeqPoints(module.Mvid, identity.MetadataToken, handleStr, pdb,
                "method has no debug information in this PDB"));
        }

        if (debugInfo.Document.IsNil && debugInfo.SequencePointsBlob.IsNil)
        {
            return MethodSourceResult.Ok(NoSeqPoints(module.Mvid, identity.MetadataToken, handleStr, pdb,
                "method has no sequence points (compiler-generated or trimmed)"));
        }

        string? file = null;
        int? startLine = null;
        int? endLine = null;
        DocumentHandle docHandle = default;

        foreach (var sp in debugInfo.GetSequencePoints())
        {
            if (sp.IsHidden) continue;
            if (file is null)
            {
                docHandle = sp.Document;
                if (!docHandle.IsNil)
                    file = pdb.Reader.GetString(pdb.Reader.GetDocument(docHandle).Name);
                startLine = sp.StartLine;
            }
            endLine = sp.EndLine;
        }

        if (file is null || startLine is null)
        {
            return MethodSourceResult.Ok(NoSeqPoints(module.Mvid, identity.MetadataToken, handleStr, pdb,
                "method has only hidden sequence points"));
        }

        string? sourceLink = TryBuildSourceLink(pdb.Reader, file);

        return MethodSourceResult.Ok(new MethodSourceLocation(
            module.Mvid, identity.MetadataToken, handleStr,
            Found: true, File: file, StartLine: startLine, EndLine: endLine,
            SourceLink: sourceLink, PdbKind: pdb.Kind, PdbAge: pdb.Age,
            Reason: null));
    }

    private static MethodSourceLocation NoSeqPoints(
        Guid mvid, int token, string handleStr, PdbHandle pdb, string reason)
        => new(mvid, token, handleStr,
            Found: false, File: null, StartLine: null, EndLine: null, SourceLink: null,
            PdbKind: pdb.Kind, PdbAge: pdb.Age, Reason: reason);

    private static PdbHandle? TryOpenPdb(ModuleHandle module)
    {
        // 1) Embedded portable PDB.
        try
        {
            foreach (var entry in module.PE.ReadDebugDirectory())
            {
                if (entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb)
                {
                    var provider = module.PE.ReadEmbeddedPortablePdbDebugDirectoryData(entry);
                    return new PdbHandle(provider, provider.GetMetadataReader(), PdbKind.Embedded, entry.MinorVersion);
                }
            }
        }
        catch (BadImageFormatException) { /* fall through to sibling lookup */ }

        // 2) Sibling .pdb next to the assembly.
        var sibling = Path.ChangeExtension(module.Path, ".pdb");
        if (!File.Exists(sibling)) return null;

        try
        {
            var bytes = File.ReadAllBytes(sibling);
            // Portable PDB blobs start with the ECMA-335 metadata signature "BSJB" (0x424A5342).
            if (bytes.Length >= 4 && BitConverter.ToUInt32(bytes, 0) == 0x424A5342)
            {
                var provider = MetadataReaderProvider.FromPortablePdbStream(
                    new MemoryStream(bytes, writable: false), MetadataStreamOptions.PrefetchMetadata);
                return new PdbHandle(provider, provider.GetMetadataReader(), PdbKind.Portable, 0);
            }
            // Windows PDB ('Microsoft C/C++ MSF 7.00\r\n…'): not readable via System.Reflection.Metadata.
            // Return a sentinel with Reader=null so callers can surface a meaningful "unsupported" reason
            // without crashing when they try to read sequence points.
            return new PdbHandle(Provider: null, Reader: null, PdbKind.Windows, 0);
        }
        catch (BadImageFormatException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string? TryBuildSourceLink(MetadataReader reader, string sourceFile)
    {
        // SourceLink JSON lives in a module-level CustomDebugInformation row keyed by the
        // ModuleDefinition handle (token 0x00000001 from the ModuleHandle table).
        var moduleHandle = (EntityHandle)MetadataTokens.Handle(0x00000001);
        string? json = null;
        foreach (var cdiHandle in reader.GetCustomDebugInformation(moduleHandle))
        {
            var cdi = reader.GetCustomDebugInformation(cdiHandle);
            if (reader.GetGuid(cdi.Kind) != SourceLinkCdiKind) continue;
            var bytes = reader.GetBlobBytes(cdi.Value);
            json = System.Text.Encoding.UTF8.GetString(bytes);
            break;
        }
        if (json is null) return null;

        // Parse minimally — no schema validation, just walk { "documents": { … } }.
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("documents", out var documents)) return null;
            foreach (var entry in documents.EnumerateObject())
            {
                var pattern = entry.Name;
                var url = entry.Value.GetString();
                if (url is null) continue;
                var resolved = TryApplySourceLinkPattern(pattern, url, sourceFile);
                if (resolved is not null) return resolved;
            }
        }
        catch (System.Text.Json.JsonException) { return null; }
        return null;
    }

    private static string? TryApplySourceLinkPattern(string pattern, string url, string sourceFile)
    {
        // SourceLink mapping: the pattern's '*' captures a relative substring of the build-time
        // path; the same substring is substituted into the URL's '*'. Patterns without '*' are
        // exact full-path matches.
        var starIdx = pattern.IndexOf('*');
        if (starIdx < 0)
        {
            return string.Equals(NormalizeSlashes(pattern), NormalizeSlashes(sourceFile), StringComparison.OrdinalIgnoreCase)
                ? url : null;
        }

        var prefix = pattern[..starIdx];
        var suffix = pattern[(starIdx + 1)..];

        var normSource = NormalizeSlashes(sourceFile);
        var normPrefix = NormalizeSlashes(prefix);
        var normSuffix = NormalizeSlashes(suffix);

        if (!normSource.StartsWith(normPrefix, StringComparison.OrdinalIgnoreCase)) return null;
        if (normSuffix.Length > 0 && !normSource.EndsWith(normSuffix, StringComparison.OrdinalIgnoreCase)) return null;
        var capture = normSource.Substring(normPrefix.Length, normSource.Length - normPrefix.Length - normSuffix.Length);
        return url.Replace("*", capture, StringComparison.Ordinal);
    }

    private static string NormalizeSlashes(string s) => s.Replace('\\', '/');

    /// <inheritdoc />
    public void Invalidate(Guid mvid)
    {
        if (_sourceCache.TryRemove(mvid, out var pdb))
            pdb?.Provider?.Dispose();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var pdb in _sourceCache.Values)
        {
            // Embedded portable PDB providers pin native memory; releasing them is mandatory.
            pdb?.Provider?.Dispose();
        }
        _sourceCache.Clear();
    }
}
