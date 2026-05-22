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

public sealed partial class MetadataIndex
{
    /// <inheritdoc />
    public ListMethodsResult ListMethods(Guid moduleVersionId, int typeMetadataToken, ListMethodsQuery query)
    {
        query ??= new ListMethodsQuery();
        if (moduleVersionId == Guid.Empty)
            return ListMethodsResult.Fail(new AssemblyError(ErrorKinds.IdentityMalformed, "moduleVersionId is required."));
        if (!_store.TryGet(moduleVersionId, out var module))
            return ListMethodsResult.Fail(new AssemblyError(ErrorKinds.ModuleNotFound,
                $"no loaded module has MVID {moduleVersionId}."));

        // typeMetadataToken must be a TypeDef (table 0x02). Anything else is a user error;
        // we won't try to dereference TypeRefs/TypeSpecs here.
        EntityHandle handle;
        try { handle = (EntityHandle)MetadataTokens.Handle(typeMetadataToken); }
        catch (ArgumentException ex)
        {
            return ListMethodsResult.Fail(new AssemblyError(
                ErrorKinds.IdentityMalformed,
                $"could not interpret token 0x{typeMetadataToken:X8} as a metadata handle.",
                ex.Message));
        }
        if (handle.Kind != HandleKind.TypeDefinition)
        {
            return ListMethodsResult.Fail(new AssemblyError(
                ErrorKinds.TokenWrongTable,
                $"token 0x{typeMetadataToken:X8} is in table {handle.Kind}, expected TypeDefinition (0x02)."));
        }

        var typeHandle = (TypeDefinitionHandle)handle;
        TypeDefinition td;
        try { td = module.MD.GetTypeDefinition(typeHandle); }
        catch (Exception ex) when (ex is BadImageFormatException or ArgumentException)
        {
            return ListMethodsResult.Fail(new AssemblyError(
                ErrorKinds.TokenOutOfRange,
                $"TypeDef token 0x{typeMetadataToken:X8} is not present in this module."));
        }

        var typeFullName = TypeName(module, td);
        var methodHandles = td.GetMethods();
        var pageSize = query.PageSize <= 0 ? ListMethodsQuery.DefaultPageSize
            : Math.Min(query.PageSize, ListMethodsQuery.MaxPageSize);
        var nameFilter = string.IsNullOrEmpty(query.NamePattern) ? null : query.NamePattern;
        var startToken = query.Cursor is { } c && c > 0 ? c : 0;

        var results = new List<MethodSummary>(pageSize);
        int? nextCursor = null;
        bool truncated = false;

        foreach (var mh in methodHandles)
        {
            var token = MetadataTokens.GetToken(mh);
            // Cursor is exclusive — a cursor value says "start at the row AFTER this token".
            // Callers echo back NextCursor verbatim and naturally pick up where they left off.
            if (token <= startToken) continue;

            MethodSummary summary;
            try { summary = SummarizeMethod(module, mh, token); }
            catch (BadImageFormatException) { continue; }

            if (nameFilter is not null
                && summary.MethodName.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;

            if (results.Count == pageSize)
            {
                // Set the cursor to the last token we *included* so the next page starts strictly after it.
                nextCursor = results[^1].MetadataToken;
                truncated = true;
                break;
            }
            results.Add(summary);
        }

        return ListMethodsResult.Ok(new ListMethodsPage(
            moduleVersionId, typeMetadataToken, typeFullName, results, nextCursor, truncated));
    }
    /// <inheritdoc />
    public FindMethodResult FindMethod(Guid moduleVersionId, FindMethodQuery query, CancellationToken cancellationToken = default)
    {
        if (query is null)
            return FindMethodResult.Fail(new AssemblyError(ErrorKinds.InvalidArgument, "query is required."));
        if (string.IsNullOrEmpty(query.NamePattern))
            return FindMethodResult.Fail(new AssemblyError(ErrorKinds.InvalidArgument, "namePattern is required."));
        if (moduleVersionId == Guid.Empty)
            return FindMethodResult.Fail(new AssemblyError(ErrorKinds.IdentityMalformed, "moduleVersionId is required."));
        if (!_store.TryGet(moduleVersionId, out var module))
            return FindMethodResult.Fail(new AssemblyError(ErrorKinds.ModuleNotFound,
                $"no loaded module has MVID {moduleVersionId}."));

        System.Text.RegularExpressions.Regex regex;
        try
        {
            regex = new System.Text.RegularExpressions.Regex(query.NamePattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(200));
        }
        catch (ArgumentException ex)
        {
            return FindMethodResult.Fail(new AssemblyError(
                ErrorKinds.InvalidArgument,
                $"namePattern is not a valid regular expression: {ex.Message}"));
        }

        var pageSize = query.PageSize <= 0 ? FindMethodQuery.DefaultPageSize
            : Math.Min(query.PageSize, FindMethodQuery.MaxPageSize);
        var sigFilter = string.IsNullOrEmpty(query.SignatureContains) ? null : query.SignatureContains;
        var startToken = query.Cursor is { } c && c > 0 ? c : 0;

        var results = new List<MethodMatch>(pageSize);
        int? nextCursor = null;
        bool truncated = false;

        foreach (var mh in module.MD.MethodDefinitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var token = MetadataTokens.GetToken(mh);
            if (token <= startToken) continue;

            string methodName;
            try { methodName = module.MD.GetString(module.MD.GetMethodDefinition(mh).Name); }
            catch (BadImageFormatException) { continue; }

            bool nameMatches;
            try { nameMatches = regex.IsMatch(methodName); }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { continue; }
            if (!nameMatches) continue;

            MethodSummary summary;
            try { summary = SummarizeMethod(module, mh, token); }
            catch (BadImageFormatException) { continue; }

            if (sigFilter is not null
                && summary.Signature.IndexOf(sigFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;

            if (results.Count == pageSize)
            {
                nextCursor = results[^1].MetadataToken;
                truncated = true;
                break;
            }
            results.Add(new MethodMatch(
                summary.ModuleVersionId, summary.MetadataToken, summary.Handle,
                summary.TypeFullName, summary.MethodName, summary.Signature));
        }

        return FindMethodResult.Ok(new FindMethodPage(
            moduleVersionId, query.NamePattern, results, nextCursor, truncated));
    }
    /// <inheritdoc />
    public ResolveResult Resolve(MethodIdentity identity) => _methodResolver.Resolve(identity);
    /// <inheritdoc />
    public IlBodyResult GetIlBody(MethodIdentity identity, int maxBytes = 0, CancellationToken cancellationToken = default)
        => _ilBodyReader.GetIlBody(identity, maxBytes, cancellationToken);
    /// <inheritdoc />
    public IlScanReadResult ScanIl(MethodIdentity identity, CancellationToken cancellationToken = default)
        => _ilBodyReader.ScanIl(identity, cancellationToken);
    /// <inheritdoc />
    public FindCallersReadResult FindCallers(MethodIdentity callee, CancellationToken cancellationToken = default)
    {
        var common = _methodResolver.TryResolveMethod(callee);
        if (common.Error is not null) return FindCallersReadResult.Fail(common.Error);
        var module = common.Module!;
        var methodHandle = common.Handle;

        // Same-module callers.
        var fromCache = true;
        XrefData xref;
        try
        {
            xref = _xrefIndex.LoadOrBuildXref(module, ref fromCache, cancellationToken);
        }
        catch (ModuleTooLargeException ex)
        {
            return FindCallersReadResult.Fail(new AssemblyError(
                ErrorKinds.ModuleTooLarge,
                "xref index for the callee's module would exceed the per-module budget.",
                ex.Message));
        }

        var callers = new List<CallerRef>();
        if (xref.Intra.TryGetValue(callee.MetadataToken, out var localCallers))
        {
            foreach (var token in localCallers)
            {
                var h = (MethodDefinitionHandle)MetadataTokens.Handle(token);
                callers.Add(new CallerRef(
                    module.Mvid, token, HandleSyntax.FormatMethod(module.Mvid, token),
                    RenderMethodDef(module, h)));
            }
        }

        // Cross-module: compute the callee's signature key once and probe every other loaded module.
        var calleeKey = XrefIndex.BuildCalleeKey(module, methodHandle);
        var modulesSearched = 1;
        List<Guid>? skipped = null;
        foreach (var other in _store.Modules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (other.Mvid == module.Mvid) continue;
            modulesSearched++;

            XrefData otherXref;
            try
            {
                otherXref = _xrefIndex.LoadOrBuildXref(other, cancellationToken);
            }
            catch (ModuleTooLargeException)
            {
                // SECURITY: surface which MVIDs were skipped so the caller can detect that
                // results are partial — an oversized loaded assembly would otherwise silently
                // hide cross-module callers.
                (skipped ??= new List<Guid>()).Add(other.Mvid);
                continue;
            }
            foreach (var outbound in otherXref.Outbound)
            {
                if (!outbound.Matches(calleeKey)) continue;
                var h = (MethodDefinitionHandle)MetadataTokens.Handle(outbound.CallerToken);
                callers.Add(new CallerRef(
                    other.Mvid, outbound.CallerToken,
                    HandleSyntax.FormatMethod(other.Mvid, outbound.CallerToken),
                    RenderMethodDef(other, h)));
            }
        }

        // §3.5 Phase Ω(e): if the caller supplied closed instantiation info (either as explicit
        // string args, or as a methodSpec fast-path, or both), decode + cross-check it, then
        // narrow the candidate set by post-walking each candidate's IL for a call-site whose
        // instantiation matches element-wise. The xref index doesn't persist per-edge
        // instantiation info, so this is a per-request post-pass.
        IReadOnlyList<string>? expectedTypeArgs = null;
        IReadOnlyList<string>? expectedMethodArgs = null;

        if (callee.MethodSpec is { } specRef)
        {
            bool hasExplicitArgs = callee.TypeGenericArguments is { Count: > 0 }
                                   || callee.MethodGenericArguments is { Count: > 0 };
            var specDecoded = _methodResolver.TryDecodeMethodSpec(specRef, allowMissingModule: hasExplicitArgs);
            if (specDecoded.Error is not null) return FindCallersReadResult.Fail(specDecoded.Error);

            if (specDecoded.SpecModule is not null && specDecoded.SpecRow is { } specRow)
            {
                if (!_methodResolver.MethodSpecTargetsMethodDef(
                        specDecoded.SpecModule, specRow,
                        callee.ModuleVersionId, callee.MetadataToken,
                        out var targetErr))
                {
                    return FindCallersReadResult.Fail(targetErr!);
                }
                expectedTypeArgs = specDecoded.TypeRendered;
                expectedMethodArgs = specDecoded.MethodRendered;
            }
        }

        if (callee.MethodGenericArguments is { Count: > 0 } methodArgsAst)
        {
            var readers = _store.SnapshotReaders();
            var (rendered, renderErr) = GenericArgResolver.RenderAndValidate(
                methodArgsAst, callee.ModuleVersionId, readers);
            if (renderErr is not null) return FindCallersReadResult.Fail(renderErr);
            if (expectedMethodArgs is not null && !MethodResolver.RenderedSequenceEqual(expectedMethodArgs, rendered!))
            {
                return FindCallersReadResult.Fail(new AssemblyError(
                    ErrorKinds.GenericInstantiationMismatch,
                    $"methodSpec encodes method-args [{string.Join(",", expectedMethodArgs)}] but genericMethodArguments has [{string.Join(",", rendered!)}]."));
            }
            expectedMethodArgs = rendered!;
        }

        if (callee.TypeGenericArguments is { Count: > 0 } typeArgsAst)
        {
            var readers = _store.SnapshotReaders();
            var (rendered, renderErr) = GenericArgResolver.RenderAndValidate(
                typeArgsAst, callee.ModuleVersionId, readers);
            if (renderErr is not null) return FindCallersReadResult.Fail(renderErr);
            if (expectedTypeArgs is not null && !MethodResolver.RenderedSequenceEqual(expectedTypeArgs, rendered!))
            {
                return FindCallersReadResult.Fail(new AssemblyError(
                    ErrorKinds.GenericInstantiationMismatch,
                    $"methodSpec encodes type-args [{string.Join(",", expectedTypeArgs)}] but genericTypeArguments has [{string.Join(",", rendered!)}]."));
            }
            expectedTypeArgs = rendered!;
        }

        if ((expectedTypeArgs is { Count: > 0 }) || (expectedMethodArgs is { Count: > 0 }))
        {
            var filtered = new List<CallerRef>(callers.Count);
            foreach (var c in callers)
            {
                if (!_store.TryGet(c.ModuleVersionId, out var callerMod)) continue;
                if (CallerInstantiationMatcher.CallerHasMatchingInstantiation(
                        callerMod, c.MetadataToken, module, methodHandle, calleeKey,
                        expectedTypeArgs, expectedMethodArgs))
                {
                    filtered.Add(c);
                }
            }
            callers = filtered;
        }

        var calleeHandleStr = HandleSyntax.FormatMethod(module.Mvid, callee.MetadataToken);
        return FindCallersReadResult.Ok(new FindCallersResult(
            module.Mvid, callee.MetadataToken, calleeHandleStr,
            callers, modulesSearched, FromCache: fromCache,
            SkippedOverBudgetModules: skipped));
    }
    /// <inheritdoc />
    public NativeBodyResult GetNativeBodyRef(Guid moduleVersionId, int methodMetadataToken)
    {
        if (moduleVersionId == Guid.Empty)
            return NativeBodyResult.Fail(new AssemblyError(ErrorKinds.IdentityMalformed, "moduleVersionId is required."));
        if (!_store.TryGet(moduleVersionId, out var module))
            return NativeBodyResult.Fail(new AssemblyError(ErrorKinds.ModuleNotFound, $"Module {moduleVersionId:D} is not loaded."));

        const int MethodDefTable = 0x06;
        int tableId = (int)((uint)methodMetadataToken >> 24);
        int rid = methodMetadataToken & 0x00FFFFFF;
        if (tableId != MethodDefTable || rid <= 0)
            return NativeBodyResult.Fail(new AssemblyError(ErrorKinds.IdentityMalformed,
                $"metadataToken 0x{methodMetadataToken:X8} is not a MethodDef token."));

        var r2r = _r2rCache.GetOrBuild(module, m =>
        {
            try
            {
                return new R2RReaderBox(R2R.R2RReader.TryCreate(m.PE, out var built) ? built : null);
            }
            catch (BadImageFormatException)
            {
                return new R2RReaderBox(null);
            }
        }).Reader;

        if (r2r is null)
            return NativeBodyResult.NotFound();

        NativeArchitecture arch = r2r.Machine switch
        {
            Machine.Amd64 => NativeArchitecture.X64,
            Machine.Arm64 => NativeArchitecture.Arm64,
            Machine.I386 => NativeArchitecture.X86,
            _ => NativeArchitecture.Unknown,
        };

        // V1 ships X64 only — native-mcp's Iced decoder is x86/x64 today.
        if (arch != NativeArchitecture.X64)
            return NativeBodyResult.NotFound();

        try
        {
            if (!r2r.TryGetHotRegion(rid, out var hot, out int runtimeFunctionIndex) || hot is null)
                return NativeBodyResult.NotFound();

            IReadOnlyList<NativeIlMapEntry>? ilMap = null;
            if (r2r.TryGetIlMap(runtimeFunctionIndex, out var decoded))
                ilMap = decoded;

            return NativeBodyResult.Ok(new NativeBodyRef(
                Source: NativeBodySource.R2R,
                PePath: module.Path,
                Architecture: arch,
                HotRegion: hot,
                ColdRegion: null,
                IlMap: ilMap));
        }
        catch (BadImageFormatException)
        {
            // Malformed R2R metadata downstream of the reader probe (NativeArray entry,
            // RuntimeFunctions table, DebugInfo NibbleReader, etc.). Surface as NotFound
            // rather than letting a raw BadImageFormatException escape the MCP envelope.
            return NativeBodyResult.NotFound();
        }
    }
    /// <inheritdoc />
    public MethodSourceResult GetMethodSource(MethodIdentity identity) => _pdbResolver.GetMethodSource(identity);

    /// <summary>Default cap on raw IL bytes encoded by <see cref="GetIlBody"/>. 4 KiB.</summary>
    public const int DefaultIlMaxBytes = Resolvers.IlBodyReader.DefaultIlMaxBytes;
}
