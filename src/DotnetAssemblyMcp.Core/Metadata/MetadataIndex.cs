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
public sealed class MetadataIndex : IMetadataIndex, IDisposable
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
    private readonly List<IModuleScopedCache> _moduleScopedCaches;

    private readonly ConcurrentDictionary<Guid, R2R.R2RReader?> _r2rCache = new();
    private readonly string _xrefCacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cache", "dotnet-assembly-mcp");

    /// <summary>Raised after a watched file change has been processed (success or failure).</summary>
    public event EventHandler<ModuleReloadedEventArgs>? ModuleReloaded;

    /// <summary>Creates an index without filesystem watching (default).</summary>
    public MetadataIndex() : this(watchForChanges: false) { }

    /// <summary>Creates an index, optionally installing per-directory file watchers.</summary>
    /// <param name="watchForChanges">When true, reloads modules on disk changes and invalidates the old MVID.</param>
    public MetadataIndex(bool watchForChanges)
    {
        _store = new ModuleStore(watchForChanges);

        _xrefIndex = new XrefIndex(_xrefCacheDir);
        _stringIndex = new StringIndex(_store);
        _attributeIndex = new AttributeIndex(_store);
        _fieldAccessIndex = new FieldAccessIndex(_store, FindCallers);
        _methodResolver = new MethodResolver(_store);
        _ilBodyReader = new IlBodyReader(_methodResolver);
        _pdbResolver = new PdbResolver(_methodResolver);

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
            new R2RCacheAdapter(_r2rCache),
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

    private sealed class R2RCacheAdapter(ConcurrentDictionary<Guid, R2R.R2RReader?> cache) : IModuleScopedCache
    {
        public void Invalidate(Guid mvid) => cache.TryRemove(mvid, out _);
    }

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
    public ListTypesResult ListTypes(Guid moduleVersionId, ListTypesQuery query)
    {
        query ??= new ListTypesQuery();
        if (moduleVersionId == Guid.Empty)
            return ListTypesResult.Fail(new AssemblyError(ErrorKinds.IdentityMalformed, "moduleVersionId is required."));
        if (!_store.TryGet(moduleVersionId, out var module))
            return ListTypesResult.Fail(new AssemblyError(ErrorKinds.ModuleNotFound,
                $"no loaded module has MVID {moduleVersionId}."));

        var pageSize = query.PageSize <= 0 ? ListTypesQuery.DefaultPageSize
            : Math.Min(query.PageSize, ListTypesQuery.MaxPageSize);
        var startRow = query.Cursor is { } c && c > 0 ? c : 1;
        var nsFilter = query.NamespacePrefix;
        var nameFilter = query.NameContains;
        var kindFilter = query.Kind;

        var results = new List<TypeSummary>(pageSize);
        int? nextCursor = null;
        bool truncated = false;

        var totalRows = module.MD.TypeDefinitions.Count;
        for (int row = startRow; row <= totalRows; row++)
        {
            // Defensive: bad metadata shouldn't take the whole enumeration down.
            TypeSummary? summary;
            try { summary = TrySummarizeType(module, row); }
            catch (BadImageFormatException) { continue; }
            if (summary is null) continue;

            if (nsFilter is not null && !MatchesNamespace(summary.FullName, nsFilter)) continue;
            if (nameFilter is not null
                && summary.FullName.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (kindFilter is { } k && summary.Kind != k) continue;

            if (results.Count == pageSize)
            {
                // We already filled the page — this match becomes the next cursor.
                nextCursor = row;
                truncated = true;
                break;
            }
            results.Add(summary);
        }

        return ListTypesResult.Ok(new ListTypesPage(moduleVersionId, results, nextCursor, truncated));
    }

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

    private static bool MatchesNamespace(string fullName, string nsPrefix)
    {
        // `fullName` is "NS.Outer+Inner" or just "Name". Match the bare namespace portion
        // so "MyApp" matches both "MyApp.Foo" and "MyApp.Bar.Baz" but not "MyAppExt.Foo".
        var plus = fullName.IndexOf('+');
        var head = plus >= 0 ? fullName[..plus] : fullName;
        var dot = head.LastIndexOf('.');
        var ns = dot >= 0 ? head[..dot] : string.Empty;
        if (ns.Length == 0) return nsPrefix.Length == 0;
        if (!ns.StartsWith(nsPrefix, StringComparison.Ordinal)) return false;
        return ns.Length == nsPrefix.Length || ns[nsPrefix.Length] == '.';
    }

    private static TypeSummary? TrySummarizeType(ModuleHandle module, int row)
    {
        var handle = MetadataTokens.TypeDefinitionHandle(row);
        var td = module.MD.GetTypeDefinition(handle);
        var name = module.MD.GetString(td.Name);
        // Skip the synthetic <Module> row (always row 1 in well-formed assemblies).
        if (name == "<Module>") return null;

        var fullName = TypeName(module, td);
        var token = MetadataTokens.GetToken(handle);
        var kind = ClassifyTypeKind(module, td);
        var methodCount = td.GetMethods().Count;
        var vis = td.Attributes & TypeAttributes.VisibilityMask;
        var isPublic = vis == TypeAttributes.Public || vis == TypeAttributes.NestedPublic;
        var baseType = TryRenderTypeReferenceSummary(module, td.BaseType);
        var interfaces = ReadInterfaceImplementations(module, td);
        var generics = MetadataDisplay.DecodeGenericParameters(module, td.GetGenericParameters());
        return new TypeSummary(module.Mvid, token, HandleSyntax.FormatType(module.Mvid, token),
            fullName, kind, methodCount, isPublic, baseType, interfaces,
            Instantiation: null, GenericParameters: generics);
    }

    private static TypeReferenceSummary? TryRenderTypeReferenceSummary(ModuleHandle module, EntityHandle handle)
        => MetadataDisplay.TryRenderTypeReferenceSummary(module, handle);

    private static IReadOnlyList<TypeReferenceSummary> ReadInterfaceImplementations(ModuleHandle module, TypeDefinition td)
    {
        var impls = td.GetInterfaceImplementations();
        if (impls.Count == 0) return Array.Empty<TypeReferenceSummary>();
        var list = new List<TypeReferenceSummary>(impls.Count);
        foreach (var ih in impls)
        {
            try
            {
                var imp = module.MD.GetInterfaceImplementation(ih);
                var summary = TryRenderTypeReferenceSummary(module, imp.Interface);
                if (summary is not null) list.Add(summary);
            }
            catch (BadImageFormatException) { /* skip malformed row */ }
        }
        return list;
    }

    private static TypeKind ClassifyTypeKind(ModuleHandle module, TypeDefinition td)
    {
        if ((td.Attributes & TypeAttributes.Interface) != 0) return TypeKind.Interface;

        // Resolve the base type's full name through a TypeRef or TypeDef. Anything we can't
        // resolve safely defaults to Class — that's the right answer for object-rooted types.
        var baseHandle = td.BaseType;
        if (baseHandle.IsNil) return TypeKind.Class;

        string? baseFullName = baseHandle.Kind switch
        {
            HandleKind.TypeReference => RenderTypeRef(module, (TypeReferenceHandle)baseHandle),
            HandleKind.TypeDefinition => RenderTypeDef(module, (TypeDefinitionHandle)baseHandle),
            _ => null,
        };
        return baseFullName switch
        {
            "System.Enum" => TypeKind.Enum,
            "System.ValueType" => TypeKind.Struct,
            "System.MulticastDelegate" or "System.Delegate" => TypeKind.Delegate,
            _ => TypeKind.Class,
        };
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
        var xref = _xrefIndex.LoadOrBuildXref(module, ref fromCache, cancellationToken);

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
        foreach (var other in _store.Modules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (other.Mvid == module.Mvid) continue;
            modulesSearched++;

            var otherXref = _xrefIndex.LoadOrBuildXref(other, cancellationToken);
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
            callers, modulesSearched, FromCache: fromCache));
    }

    /// <inheritdoc />
    public FindStringReferencesReadResult FindStringReferences(
        string query,
        StringMatchMode matchMode,
        Guid moduleVersionIdFilter = default,
        int maxHits = 0,
        CancellationToken cancellationToken = default)
        => _stringIndex.FindStringReferences(query, matchMode, moduleVersionIdFilter, maxHits, cancellationToken);

    /// <inheritdoc />
    public FindAttributeTargetsReadResult FindAttributeTargets(
        string attributeTypeFullName,
        Guid moduleVersionIdFilter = default,
        IReadOnlyCollection<AttributeTargetKind>? targetKindsFilter = null,
        int maxHits = 0,
        CancellationToken cancellationToken = default)
        => _attributeIndex.FindAttributeTargets(attributeTypeFullName, moduleVersionIdFilter, targetKindsFilter, maxHits, cancellationToken);

    /// <inheritdoc />
    public FindFieldReferencesReadResult FindFieldReferences(
        Guid moduleVersionId,
        int fieldMetadataToken,
        FieldAccessMode mode = FieldAccessMode.All,
        int maxHits = 0,
        CancellationToken cancellationToken = default)
        => _fieldAccessIndex.FindFieldReferences(moduleVersionId, fieldMetadataToken, mode, maxHits, cancellationToken);

    /// <inheritdoc />
    public FindPropertyReferencesReadResult FindPropertyReferences(
        Guid moduleVersionId,
        int propertyMetadataToken,
        PropertyAccessorFilter accessor = PropertyAccessorFilter.All,
        int maxHits = 0,
        CancellationToken cancellationToken = default)
        => _fieldAccessIndex.FindPropertyReferences(moduleVersionId, propertyMetadataToken, accessor, maxHits, cancellationToken);

    /// <inheritdoc />
    public FindEventReferencesReadResult FindEventReferences(
        Guid moduleVersionId,
        int eventMetadataToken,
        EventAccessorFilter accessor = EventAccessorFilter.All,
        int maxHits = 0,
        CancellationToken cancellationToken = default)
        => _fieldAccessIndex.FindEventReferences(moduleVersionId, eventMetadataToken, accessor, maxHits, cancellationToken);

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

        R2R.R2RReader? r2r = _r2rCache.GetOrAdd(module.Mvid, _ =>
        {
            try
            {
                return R2R.R2RReader.TryCreate(module.PE, out var built) ? built : null;
            }
            catch (BadImageFormatException)
            {
                return null;
            }
        });

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
    public FindTypeReferencesReadResult FindTypeReferences(Guid moduleVersionId, int typeMetadataToken, CancellationToken cancellationToken = default)
    {
        if (moduleVersionId == Guid.Empty)
            return FindTypeReferencesReadResult.Fail(new AssemblyError(ErrorKinds.IdentityMalformed, "moduleVersionId is required."));
        if (!_store.TryGet(moduleVersionId, out var module))
            return FindTypeReferencesReadResult.Fail(new AssemblyError(ErrorKinds.ModuleNotFound,
                $"no loaded module has MVID {moduleVersionId:D}."));

        EntityHandle targetHandle;
        try { targetHandle = (EntityHandle)MetadataTokens.Handle(typeMetadataToken); }
        catch (ArgumentException ex)
        {
            return FindTypeReferencesReadResult.Fail(new AssemblyError(ErrorKinds.IdentityMalformed,
                $"could not interpret token 0x{typeMetadataToken:X8} as a metadata handle.", ex.Message));
        }
        if (targetHandle.Kind != HandleKind.TypeDefinition)
            return FindTypeReferencesReadResult.Fail(new AssemblyError(ErrorKinds.TokenWrongTable,
                $"token 0x{typeMetadataToken:X8} is in table {targetHandle.Kind}, expected TypeDefinition (0x02)."));

        var targetRow = MetadataTokens.GetRowNumber((TypeDefinitionHandle)targetHandle);
        if (targetRow <= 0 || targetRow > module.MD.TypeDefinitions.Count)
            return FindTypeReferencesReadResult.Fail(new AssemblyError(ErrorKinds.TokenOutOfRange,
                $"TypeDef token 0x{typeMetadataToken:X8} is not present in this module."));

        // Resolve target identity for cross-module matching once.
        var targetDef = module.MD.GetTypeDefinition((TypeDefinitionHandle)targetHandle);
        var targetFullName = TypeName(module, targetDef);
        var targetAssemblyName = module.MD.IsAssembly
            ? module.MD.GetString(module.MD.GetAssemblyDefinition().Name)
            : null;

        var fromCache = true;
        var xref = _xrefIndex.LoadOrBuildXref(module, ref fromCache, cancellationToken);

        var references = new List<TypeReferenceRef>();

        // Intra-module sites.
        if (xref.TypeIntra.TryGetValue(typeMetadataToken, out var localSites))
        {
            foreach (var site in localSites)
                references.Add(RenderTypeReferenceSite(module, site));
        }

        // Cross-module sites: probe every other loaded module's outbound type-refs.
        var modulesSearched = 1;
        if (targetAssemblyName is not null)
        {
            foreach (var other in _store.Modules)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (other.Mvid == module.Mvid) continue;
                modulesSearched++;

                var otherXref = _xrefIndex.LoadOrBuildXref(other, cancellationToken);
                foreach (var entry in otherXref.TypeOutbound)
                {
                    if (!string.Equals(entry.TargetAssemblyName, targetAssemblyName, StringComparison.Ordinal)) continue;
                    if (!string.Equals(entry.TargetTypeFullName, targetFullName, StringComparison.Ordinal)) continue;
                    references.Add(RenderTypeReferenceSite(other,
                        new TypeReferenceSite(entry.SiteToken, entry.SiteKind, entry.ReferenceKind)));
                }
            }
        }

        var targetHandleStr = HandleSyntax.FormatType(module.Mvid, typeMetadataToken);
        return FindTypeReferencesReadResult.Ok(new FindTypeReferencesResult(
            module.Mvid, typeMetadataToken, targetHandleStr,
            references, modulesSearched, FromCache: fromCache));
    }

    private static TypeReferenceRef RenderTypeReferenceSite(ModuleHandle module, TypeReferenceSite site)
    {
        string handle;
        string display;
        switch (site.SiteKind)
        {
            case MemberKind.Method:
            {
                var mh = (MethodDefinitionHandle)MetadataTokens.Handle(site.SiteToken);
                handle = HandleSyntax.FormatMethod(module.Mvid, site.SiteToken);
                display = RenderMethodDef(module, mh);
                break;
            }
            case MemberKind.Field:
            {
                handle = HandleSyntax.FormatField(module.Mvid, site.SiteToken);
                display = RenderFieldDef(module, (FieldDefinitionHandle)MetadataTokens.Handle(site.SiteToken));
                break;
            }
            case MemberKind.Property:
            {
                handle = HandleSyntax.FormatProperty(module.Mvid, site.SiteToken);
                display = RenderPropertyDef(module, (PropertyDefinitionHandle)MetadataTokens.Handle(site.SiteToken));
                break;
            }
            case MemberKind.Event:
            {
                handle = HandleSyntax.FormatEvent(module.Mvid, site.SiteToken);
                display = RenderEventDef(module, (EventDefinitionHandle)MetadataTokens.Handle(site.SiteToken));
                break;
            }
            case MemberKind.Type:
            {
                handle = HandleSyntax.FormatType(module.Mvid, site.SiteToken);
                try
                {
                    var tdh = (TypeDefinitionHandle)MetadataTokens.Handle(site.SiteToken);
                    var td = module.MD.GetTypeDefinition(tdh);
                    display = TypeName(module, td);
                }
                catch (BadImageFormatException) { display = $"<type 0x{site.SiteToken:X8}>"; }
                break;
            }
            default:
                handle = $"?:{module.Mvid:D}:0x{site.SiteToken:X8}";
                display = $"<unknown site kind {site.SiteKind}>";
                break;
        }
        return new TypeReferenceRef(module.Mvid, site.SiteToken, site.SiteKind, site.ReferenceKind, handle, display);
    }



    /// <inheritdoc />
    public MethodSourceResult GetMethodSource(MethodIdentity identity) => _pdbResolver.GetMethodSource(identity);

    /// <summary>Default cap on raw IL bytes encoded by <see cref="GetIlBody"/>. 4 KiB.</summary>
    public const int DefaultIlMaxBytes = Resolvers.IlBodyReader.DefaultIlMaxBytes;

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _store.Dispose();
        _pdbResolver.Dispose();
    }

    /// <inheritdoc />
    public GetTypeResult GetTypeDefinition(Guid moduleVersionId, int typeMetadataToken)
    {
        if (moduleVersionId == Guid.Empty)
            return GetTypeResult.Fail(new AssemblyError(ErrorKinds.IdentityMalformed, "moduleVersionId is required."));
        if (!_store.TryGet(moduleVersionId, out var module))
            return GetTypeResult.Fail(new AssemblyError(ErrorKinds.ModuleNotFound,
                $"no loaded module has MVID {moduleVersionId:D}."));

        EntityHandle handle;
        try { handle = (EntityHandle)MetadataTokens.Handle(typeMetadataToken); }
        catch (ArgumentException ex)
        {
            return GetTypeResult.Fail(new AssemblyError(ErrorKinds.IdentityMalformed,
                $"could not interpret token 0x{typeMetadataToken:X8} as a metadata handle.", ex.Message));
        }
        if (handle.Kind != HandleKind.TypeDefinition)
        {
            return GetTypeResult.Fail(new AssemblyError(ErrorKinds.TokenWrongTable,
                $"token 0x{typeMetadataToken:X8} is in table {handle.Kind}, expected TypeDefinition (0x02)."));
        }

        var row = MetadataTokens.GetRowNumber((TypeDefinitionHandle)handle);
        if (row <= 0 || row > module.MD.TypeDefinitions.Count)
            return GetTypeResult.Fail(new AssemblyError(ErrorKinds.TokenOutOfRange,
                $"TypeDef token 0x{typeMetadataToken:X8} is not present in this module."));

        TypeSummary? summary;
        try { summary = TrySummarizeType(module, row); }
        catch (BadImageFormatException ex)
        {
            return GetTypeResult.Fail(new AssemblyError(ErrorKinds.TokenOutOfRange,
                $"could not read TypeDef row {row}: {ex.Message}"));
        }
        if (summary is null)
            return GetTypeResult.Fail(new AssemblyError(ErrorKinds.TokenOutOfRange,
                $"TypeDef row {row} is the synthetic <Module> row."));
        return GetTypeResult.Ok(summary);
    }

    /// <inheritdoc />
    public ListDerivedTypesResult ListDerivedTypes(Guid moduleVersionId, int baseTypeMetadataToken, ListDerivedTypesQuery query)
    {
        query ??= new ListDerivedTypesQuery();
        if (moduleVersionId == Guid.Empty)
            return ListDerivedTypesResult.Fail(new AssemblyError(ErrorKinds.IdentityMalformed, "moduleVersionId is required."));
        if (!_store.TryGet(moduleVersionId, out var module))
            return ListDerivedTypesResult.Fail(new AssemblyError(ErrorKinds.ModuleNotFound,
                $"no loaded module has MVID {moduleVersionId:D}."));

        EntityHandle baseHandle;
        try { baseHandle = (EntityHandle)MetadataTokens.Handle(baseTypeMetadataToken); }
        catch (ArgumentException ex)
        {
            return ListDerivedTypesResult.Fail(new AssemblyError(ErrorKinds.IdentityMalformed,
                $"could not interpret token 0x{baseTypeMetadataToken:X8} as a metadata handle.", ex.Message));
        }
        if (baseHandle.Kind != HandleKind.TypeDefinition)
        {
            return ListDerivedTypesResult.Fail(new AssemblyError(ErrorKinds.TokenWrongTable,
                $"token 0x{baseTypeMetadataToken:X8} is in table {baseHandle.Kind}, expected TypeDefinition (0x02)."));
        }
        TypeDefinition baseTd;
        try { baseTd = module.MD.GetTypeDefinition((TypeDefinitionHandle)baseHandle); }
        catch (Exception ex) when (ex is BadImageFormatException or ArgumentException)
        {
            return ListDerivedTypesResult.Fail(new AssemblyError(ErrorKinds.TokenOutOfRange,
                $"TypeDef token 0x{baseTypeMetadataToken:X8} is not present in this module."));
        }
        var baseFullName = TypeName(module, baseTd);
        var baseAsmName = module.MD.GetString(module.MD.GetAssemblyDefinition().Name);
        var baseKey = (Asm: baseAsmName, Full: baseFullName);

        var pageSize = query.PageSize <= 0 ? ListDerivedTypesQuery.DefaultPageSize
            : Math.Min(query.PageSize, ListDerivedTypesQuery.MaxPageSize);
        var startOffset = query.Cursor is { } c && c > 0 ? c : 0;

        // Build the parent relation across every loaded module:
        //  - localParents: (childMvid, childToken) -> [(parentMvid, parentToken, args?)] (same module
        //    via TypeDef, or via TypeSpec whose CLASS token resolves to a same-module TypeDef).
        //  - crossParents: (childMvid, childToken) -> [(parentAsm, parentFull, args?)] (TypeRef or
        //    TypeSpec whose CLASS token resolves to a cross-module TypeRef).
        // BaseType and every InterfaceImplementation are captured so a single walk covers both
        // class derivation and interface implementation. TypeSpec parents (e.g.
        // `class OrderHandler : IRequestHandler<int,string>`) populate `args` with the closed
        // instantiation in CLR wire format ([ "System.Int32", "System.String" ]); non-spec edges
        // carry null args. Per issue #67 the `args` payload feeds both the MatchInstantiation
        // filter and the TypeSummary.Instantiation stamp on the returned page.
        var localParents = new Dictionary<(Guid, int), List<(Guid mvid, int token, IReadOnlyList<string>? args)>>();
        var crossParents = new Dictionary<(Guid, int), List<(string asm, string full, IReadOnlyList<string>? args)>>();

        foreach (var m in _store.Modules)
        {
            var childMvid = m.Mvid;
            var md = m.MD;
            int n;
            try { n = md.TypeDefinitions.Count; }
            catch (BadImageFormatException) { continue; }

            for (int row = 1; row <= n; row++)
            {
                TypeDefinition td;
                try { td = md.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(row)); }
                catch (BadImageFormatException) { continue; }

                int childToken = MetadataTokens.GetToken(MetadataTokens.TypeDefinitionHandle(row));
                var childKey = (childMvid, childToken);

                AddParentEdge(m, childMvid, childKey, td.BaseType, localParents, crossParents);

                InterfaceImplementationHandleCollection iis;
                try { iis = td.GetInterfaceImplementations(); }
                catch (BadImageFormatException) { continue; }
                foreach (var iih in iis)
                {
                    EntityHandle ih;
                    try { ih = md.GetInterfaceImplementation(iih).Interface; }
                    catch (BadImageFormatException) { continue; }
                    AddParentEdge(m, childMvid, childKey, ih, localParents, crossParents);
                }
            }
        }

        // Pre-render the MatchInstantiation filter into CLR wire-format strings so per-edge
        // comparison is a flat string equality check. Null/empty => open match.
        string[]? expectedArgs = null;
        if (query.MatchInstantiation is { Count: > 0 } mi)
        {
            expectedArgs = new string[mi.Count];
            for (int i = 0; i < mi.Count; i++) expectedArgs[i] = mi[i].Format();
        }

        bool MatchesInstantiationFilter(IReadOnlyList<string>? candidate)
        {
            if (expectedArgs is null) return true;
            if (candidate is null) return false;
            if (candidate.Count != expectedArgs.Length) return false;
            for (int i = 0; i < expectedArgs.Length; i++)
                if (!string.Equals(candidate[i], expectedArgs[i], StringComparison.Ordinal))
                    return false;
            return true;
        }

        // Edge test: does (childMvid, childToken) have `baseKey` as one of its direct parents,
        // and does the parent edge satisfy the MatchInstantiation filter? On success returns
        // the matched edge's args (may be null for non-spec edges) so the caller can stamp
        // them on the resulting TypeSummary.
        bool IsDirectChild((Guid mvid, int token) child, out IReadOnlyList<string>? matchedArgs)
        {
            matchedArgs = null;
            if (child.mvid == moduleVersionId
                && localParents.TryGetValue(child, out var lps))
            {
                foreach (var p in lps)
                {
                    if (p.mvid == moduleVersionId && p.token == baseTypeMetadataToken
                        && MatchesInstantiationFilter(p.args))
                    {
                        matchedArgs = p.args;
                        return true;
                    }
                }
            }
            if (crossParents.TryGetValue(child, out var cps))
            {
                foreach (var p in cps)
                {
                    if (p.asm == baseKey.Asm && p.full == baseKey.Full
                        && MatchesInstantiationFilter(p.args))
                    {
                        matchedArgs = p.args;
                        return true;
                    }
                }
            }
            return false;
        }

        // Direct children of the root, used both as the answer in DirectOnly mode and as the
        // BFS seed in transitive mode. directInstantiation captures the matched-edge args so
        // the page can stamp TypeSummary.Instantiation on the closed-generic hits.
        var direct = new HashSet<(Guid, int)>();
        var directInstantiation = new Dictionary<(Guid, int), IReadOnlyList<string>?>();
        foreach (var child in localParents.Keys)
        {
            if (IsDirectChild(child, out var args) && direct.Add(child))
                directInstantiation[child] = args;
        }
        foreach (var child in crossParents.Keys)
        {
            if (IsDirectChild(child, out var args) && direct.Add(child))
                directInstantiation[child] = args;
        }

        HashSet<(Guid, int)> hits;
        if (query.DirectOnly)
        {
            hits = direct;
        }
        else
        {
            // Reverse parent edges so we can BFS downwards. Local edges produce
            // (parentMvid, parentToken) -> child; cross edges produce (asm, full) -> child.
            var childrenByLocal = new Dictionary<(Guid, int), List<(Guid, int)>>();
            var childrenByCross = new Dictionary<(string, string), List<(Guid, int)>>();
            foreach (var kv in localParents)
                foreach (var p in kv.Value)
                {
                    var key = (p.mvid, p.token);
                    if (!childrenByLocal.TryGetValue(key, out var list))
                        childrenByLocal[key] = list = new List<(Guid, int)>();
                    list.Add(kv.Key);
                }
            foreach (var kv in crossParents)
                foreach (var p in kv.Value)
                {
                    var key = (p.asm, p.full);
                    if (!childrenByCross.TryGetValue(key, out var list))
                        childrenByCross[key] = list = new List<(Guid, int)>();
                    list.Add(kv.Key);
                }

            hits = new HashSet<(Guid, int)>();
            var queue = new Queue<(Guid, int)>();
            foreach (var d in direct) { hits.Add(d); queue.Enqueue(d); }

            // Cross-module edges that match the root by (asm, full). Note: when the user
            // supplied MatchInstantiation, these transitive root-cross edges are also gated
            // by the same filter so descendants behind a non-matching closed parent aren't
            // accidentally pulled in.
            if (expectedArgs is null && childrenByCross.TryGetValue(baseKey, out var rootCross))
                foreach (var crossChild in rootCross)
                    if (hits.Add(crossChild)) queue.Enqueue(crossChild);

            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                // Each descendant's own (asm, full) lets other modules' TypeRefs reach it.
                if (!_store.TryGet(node.Item1, out var nodeModule)) continue;
                string nodeAsm;
                string nodeFull;
                try
                {
                    var td = nodeModule.MD.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(
                        MetadataTokens.GetRowNumber((EntityHandle)MetadataTokens.Handle(node.Item2))));
                    nodeFull = TypeName(nodeModule, td);
                    nodeAsm = nodeModule.MD.GetString(nodeModule.MD.GetAssemblyDefinition().Name);
                }
                catch (Exception ex) when (ex is BadImageFormatException or ArgumentException) { continue; }

                if (childrenByLocal.TryGetValue(node, out var localKids))
                    foreach (var k in localKids)
                        if (hits.Add(k)) queue.Enqueue(k);
                if (childrenByCross.TryGetValue((nodeAsm, nodeFull), out var crossKids))
                    foreach (var k in crossKids)
                        if (hits.Add(k)) queue.Enqueue(k);
            }
        }

        // Stable order: by module path, then by token within the module.
        var ordered = hits
            .Select(h => (Hit: h, Path: _store.TryGet(h.Item1, out var mm) ? mm.Path : string.Empty))
            .OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Hit.Item2)
            .Select(t => t.Hit)
            .ToList();

        var results = new List<TypeSummary>(Math.Min(pageSize, 16));
        int? nextCursor = null;
        bool truncated = false;

        for (int i = startOffset; i < ordered.Count; i++)
        {
            var (hitMvid, hitToken) = ordered[i];
            if (!_store.TryGet(hitMvid, out var hitModule)) continue;
            int hitRow;
            try { hitRow = MetadataTokens.GetRowNumber((EntityHandle)MetadataTokens.Handle(hitToken)); }
            catch (ArgumentException) { continue; }

            TypeSummary? summary;
            try { summary = TrySummarizeType(hitModule, hitRow); }
            catch (BadImageFormatException) { continue; }
            if (summary is null) continue;

            // Stamp the closed instantiation that satisfied the match on direct hits reached
            // via a TypeSpec parent (issue #67). Transitive descendants and non-spec direct
            // hits leave Instantiation null.
            if (directInstantiation.TryGetValue((hitMvid, hitToken), out var stamp) && stamp is not null)
                summary = summary with { Instantiation = stamp };

            if (results.Count == pageSize)
            {
                nextCursor = i;
                truncated = true;
                break;
            }
            results.Add(summary);
        }

        return ListDerivedTypesResult.Ok(new ListDerivedTypesPage(
            moduleVersionId, baseTypeMetadataToken, baseFullName, results, nextCursor, truncated));
    }

    private static void AddParentEdge(
        ModuleHandle childModule,
        Guid childMvid,
        (Guid, int) childKey,
        EntityHandle parentHandle,
        Dictionary<(Guid, int), List<(Guid mvid, int token, IReadOnlyList<string>? args)>> localParents,
        Dictionary<(Guid, int), List<(string asm, string full, IReadOnlyList<string>? args)>> crossParents)
    {
        if (parentHandle.IsNil) return;
        switch (parentHandle.Kind)
        {
            case HandleKind.TypeDefinition:
                if (!localParents.TryGetValue(childKey, out var lps))
                    localParents[childKey] = lps = new List<(Guid, int, IReadOnlyList<string>?)>();
                lps.Add((childMvid, MetadataTokens.GetToken(parentHandle), null));
                break;
            case HandleKind.TypeReference:
                var full = ResolveTypeRefName(childModule, (TypeReferenceHandle)parentHandle, out var asm);
                if (full is null || asm is null) return;
                if (!crossParents.TryGetValue(childKey, out var cps))
                    crossParents[childKey] = cps = new List<(string, string, IReadOnlyList<string>?)>();
                cps.Add((asm, full, null));
                break;
            case HandleKind.TypeSpecification:
                AddSpecParentEdge(childModule, childMvid, childKey,
                    (TypeSpecificationHandle)parentHandle, localParents, crossParents);
                break;
        }
    }

    /// <summary>
    /// Decodes a TypeSpec parent edge (issue #67). Generic-instantiation base/interface
    /// signatures (e.g. <c>: IRequestHandler&lt;int,string&gt;</c>) materialize as a
    /// <c>0x15 GENERICINST</c> blob: byte stream is
    /// <c>0x15 (CLASS|VALUETYPE) &lt;coded TypeDefOrRefOrSpec&gt; &lt;argCount&gt; &lt;arg sigs&gt;</c>.
    /// We peek the underlying TypeDef/TypeRef to identify the OPEN parent (so the matcher
    /// can answer queries against the open form, e.g. <c>IRequestHandler`2</c>), and decode
    /// the full spec via <see cref="WireFormatSignatureProvider"/> to capture the closed
    /// args in CLR wire format. Non-GENERICINST specs (rare for base types) are skipped.
    /// </summary>
    private static void AddSpecParentEdge(
        ModuleHandle childModule,
        Guid childMvid,
        (Guid, int) childKey,
        TypeSpecificationHandle handle,
        Dictionary<(Guid, int), List<(Guid mvid, int token, IReadOnlyList<string>? args)>> localParents,
        Dictionary<(Guid, int), List<(string asm, string full, IReadOnlyList<string>? args)>> crossParents)
    {
        try
        {
            var ts = childModule.MD.GetTypeSpecification(handle);
            var sigReader = childModule.MD.GetBlobReader(ts.Signature);
            if (sigReader.RemainingBytes < 2) return;
            if (sigReader.ReadByte() != 0x15 /* GENERICINST */) return;
            var classOrValue = sigReader.ReadByte();
            if (classOrValue != 0x12 /* CLASS */ && classOrValue != 0x11 /* VALUETYPE */) return;
            var encoded = sigReader.ReadCompressedInteger();
            var openHandle = EntityHandleFromCoded(encoded);

            // Decode the closed args via the typed provider then re-parse to extract each
            // arg as a canonical wire-format string ("System.Int32", "List`1[System.Int32]", …).
            var decoded = ts.DecodeSignature(new WireFormatSignatureProvider(), genericContext: (object?)null);
            if (!GenericTypeName.TryParse(decoded, out var node, out _, out _)) return;
            if (node is not GenericTypeName.Named named) return;
            if (named.TypeArguments.IsDefaultOrEmpty) return;
            var args = new string[named.TypeArguments.Length];
            for (int i = 0; i < args.Length; i++) args[i] = named.TypeArguments[i].Format();

            switch (openHandle.Kind)
            {
                case HandleKind.TypeDefinition:
                    if (!localParents.TryGetValue(childKey, out var lps))
                        localParents[childKey] = lps = new List<(Guid, int, IReadOnlyList<string>?)>();
                    lps.Add((childMvid, MetadataTokens.GetToken(openHandle), args));
                    break;
                case HandleKind.TypeReference:
                    var full = ResolveTypeRefName(childModule, (TypeReferenceHandle)openHandle, out var asm);
                    if (full is null || asm is null) return;
                    if (!crossParents.TryGetValue(childKey, out var cps))
                        crossParents[childKey] = cps = new List<(string, string, IReadOnlyList<string>?)>();
                    cps.Add((asm, full, args));
                    break;
            }
        }
        catch (BadImageFormatException) { /* skip malformed spec */ }
    }

    /// <inheritdoc />
    public ListAssemblyReferencesResult ListAssemblyReferences(Guid moduleVersionId)
    {
        if (moduleVersionId == Guid.Empty)
            return ListAssemblyReferencesResult.Fail(new AssemblyError(ErrorKinds.IdentityMalformed, "moduleVersionId is required."));
        if (!_store.TryGet(moduleVersionId, out var module))
            return ListAssemblyReferencesResult.Fail(new AssemblyError(ErrorKinds.ModuleNotFound,
                $"no loaded module has MVID {moduleVersionId:D}."));

        var refs = new List<AssemblyReferenceSummary>(module.MD.AssemblyReferences.Count);
        foreach (var arh in module.MD.AssemblyReferences)
        {
            try
            {
                var ar = module.MD.GetAssemblyReference(arh);
                var name = module.MD.GetString(ar.Name);
                var culture = ar.Culture.IsNil ? null : module.MD.GetString(ar.Culture);
                if (string.IsNullOrEmpty(culture)) culture = null;
                string? pkt = null;
                if (!ar.PublicKeyOrToken.IsNil)
                {
                    var bytes = module.MD.GetBlobBytes(ar.PublicKeyOrToken);
                    if (bytes.Length > 0) pkt = Convert.ToHexString(bytes).ToLowerInvariant();
                }
                var token = MetadataTokens.GetToken(arh);
                refs.Add(new AssemblyReferenceSummary(
                    MetadataToken: token,
                    // Issue #80: canonical 'a:<mvid>' handle of the containing module — the
                    // previous ad-hoc 'a:<mvid>:0x<token>' was not parseable by
                    // HandleSyntax.TryParseAssembly. The row id stays in MetadataToken.
                    Handle: HandleSyntax.FormatAssembly(module.Mvid),
                    Name: name,
                    Version: ar.Version.ToString(),
                    Culture: culture,
                    PublicKeyTokenHex: pkt,
                    Flags: (int)ar.Flags));
            }
            catch (BadImageFormatException) { /* skip malformed row */ }
        }

        return ListAssemblyReferencesResult.Ok(new ListAssemblyReferencesPage(module.Mvid, refs));
    }

    /// <inheritdoc />
    public ListResourcesResult ListResources(Guid moduleVersionId)
    {
        if (moduleVersionId == Guid.Empty)
            return ListResourcesResult.Fail(new AssemblyError(ErrorKinds.IdentityMalformed, "moduleVersionId is required."));
        if (!_store.TryGet(moduleVersionId, out var module))
            return ListResourcesResult.Fail(new AssemblyError(ErrorKinds.ModuleNotFound,
                $"no loaded module has MVID {moduleVersionId:D}."));

        var md = module.MD;
        var list = new List<ResourceSummary>(md.ManifestResources.Count);

        // Resolve the .mresources section once so per-resource length decoding is O(1).
        var pe = module.PE;
        var resourceDir = pe.PEHeaders.CorHeader?.ResourcesDirectory;
        // GetSectionData over a default RVA returns an empty PEMemoryBlock which is safe to slice.
        var resBlock = (resourceDir is { RelativeVirtualAddress: > 0 })
            ? pe.GetSectionData(resourceDir.Value.RelativeVirtualAddress)
            : default;
        // GetSectionData runs to the end of the containing PE section — typically far beyond the
        // CLI resources data directory. Use Size as the real upper bound, clamped to the actual
        // block length in case Size is malformed.
        int resLimit = resourceDir is { Size: > 0 }
            ? Math.Min(resBlock.Length, resourceDir.Value.Size)
            : 0;

        foreach (var mrh in md.ManifestResources)
        {
            try
            {
                var mr = md.GetManifestResource(mrh);
                var name = md.GetString(mr.Name);
                var token = MetadataTokens.GetToken(mrh);
                var isPublic = (mr.Attributes & ManifestResourceAttributes.Public) != 0;

                if (mr.Implementation.IsNil)
                {
                    // In-PE resource: decode the 4-byte little-endian length prefix at the offset.
                    // All bounds arithmetic in long to defeat malformed offsets near int.MaxValue
                    // (offset+4 evaluated as int would overflow negative and slip past the check).
                    int? length = null;
                    long off = mr.Offset;
                    if (resLimit > 0 && off >= 0 && off + 4L <= resLimit)
                    {
                        int offset = (int)off;
                        var reader = resBlock.GetReader(offset, 4);
                        uint declared = reader.ReadUInt32();
                        // Sanity: the declared payload must fit inside the resources directory.
                        long endExclusive = (long)offset + 4L + declared;
                        if (declared <= int.MaxValue && endExclusive <= resLimit)
                            length = (int)declared;
                    }

                    list.Add(new ResourceSummary(
                        MetadataToken: token,
                        Name: name,
                        IsPublic: isPublic,
                        Implementation: ResourceImplementationKind.InPe,
                        Offset: mr.Offset,
                        Length: length));
                }
                else if (mr.Implementation.Kind == HandleKind.AssemblyFile)
                {
                    var fileHandle = (AssemblyFileHandle)mr.Implementation;
                    var file = md.GetAssemblyFile(fileHandle);
                    list.Add(new ResourceSummary(
                        MetadataToken: token,
                        Name: name,
                        IsPublic: isPublic,
                        Implementation: ResourceImplementationKind.LinkedFile,
                        LinkedFileName: md.GetString(file.Name)));
                }
                else if (mr.Implementation.Kind == HandleKind.AssemblyReference)
                {
                    var refHandle = (AssemblyReferenceHandle)mr.Implementation;
                    var asmRef = md.GetAssemblyReference(refHandle);
                    list.Add(new ResourceSummary(
                        MetadataToken: token,
                        Name: name,
                        IsPublic: isPublic,
                        Implementation: ResourceImplementationKind.ForwardedToAssembly,
                        LinkedAssemblyName: md.GetString(asmRef.Name)));
                }
                // Other Implementation kinds are not defined by ECMA-335 §22.24 — skip silently.
            }
            catch (BadImageFormatException) { /* skip malformed row */ }
        }

        return ListResourcesResult.Ok(new ListResourcesPage(module.Mvid, list));
    }

    /// <inheritdoc />
    public ListMembersResult ListMembers(Guid moduleVersionId, int typeMetadataToken, ListMembersQuery query)
    {
        query ??= new ListMembersQuery();
        if (moduleVersionId == Guid.Empty)
            return ListMembersResult.Fail(new AssemblyError(ErrorKinds.IdentityMalformed, "moduleVersionId is required."));
        if (!_store.TryGet(moduleVersionId, out var module))
            return ListMembersResult.Fail(new AssemblyError(ErrorKinds.ModuleNotFound,
                $"no loaded module has MVID {moduleVersionId:D}."));

        EntityHandle typeHandle;
        try { typeHandle = (EntityHandle)MetadataTokens.Handle(typeMetadataToken); }
        catch (ArgumentException ex)
        {
            return ListMembersResult.Fail(new AssemblyError(ErrorKinds.IdentityMalformed,
                $"could not interpret token 0x{typeMetadataToken:X8} as a metadata handle.", ex.Message));
        }
        if (typeHandle.Kind != HandleKind.TypeDefinition)
            return ListMembersResult.Fail(new AssemblyError(ErrorKinds.TokenWrongTable,
                $"token 0x{typeMetadataToken:X8} is in table {typeHandle.Kind}, expected TypeDefinition (0x02)."));

        var typeRow = MetadataTokens.GetRowNumber((TypeDefinitionHandle)typeHandle);
        if (typeRow <= 0 || typeRow > module.MD.TypeDefinitions.Count)
            return ListMembersResult.Fail(new AssemblyError(ErrorKinds.TokenOutOfRange,
                $"TypeDef token 0x{typeMetadataToken:X8} is not present in this module."));

        var td = module.MD.GetTypeDefinition((TypeDefinitionHandle)typeHandle);
        var fullName = TypeName(module, td);
        var provider = new StringSignatureProvider(module.MD);

        var pageSize = query.PageSize <= 0 ? ListMembersQuery.DefaultPageSize
            : Math.Min(query.PageSize, ListMembersQuery.MaxPageSize);
        var startToken = query.Cursor is { } c && c > 0 ? c : 0;
        var nameFilter = string.IsNullOrEmpty(query.NamePattern) ? null : query.NamePattern;
        var sigFilter = string.IsNullOrEmpty(query.SignatureContains) ? null : query.SignatureContains;

        // Collect first so we can apply a single cursor across all kinds in a deterministic
        // order. The order is: fields (0x04), properties (0x17), events (0x14) — matching
        // how list_members is expected to surface DTOs / POCOs (fields + props) before events.
        var all = new List<MemberSummary>(capacity: 32);
        if (query.Kind is null or MemberKind.Field) CollectFields(module, td, provider, all);
        if (query.Kind is null or MemberKind.Property) CollectProperties(module, td, provider, all);
        if (query.Kind is null or MemberKind.Event) CollectEvents(module, td, provider, all);

        // Cursor is exclusive on a synthetic 32-bit composite (kind << 28 | row) so paging
        // across kinds is stable; we surface only the underlying token outwards via
        // NextCursor (the last emitted member's token), which is unique within its table.
        // To keep the surface simple we instead use a sequential offset: NextCursor = last
        // composite-position index (1-based). That stays stable as long as the kind filter
        // and metadata order don't change between calls — which they don't (Tier-1).
        var results = new List<MemberSummary>(Math.Min(pageSize, 16));
        int? nextCursor = null;
        bool truncated = false;
        for (int i = 0; i < all.Count; i++)
        {
            var ordinal = i + 1;
            if (ordinal <= startToken) continue;
            var m = all[i];
            if (nameFilter is not null && m.Name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (sigFilter is not null && m.Signature.IndexOf(sigFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            results.Add(m);
            if (results.Count >= pageSize)
            {
                if (i + 1 < all.Count) { truncated = true; nextCursor = ordinal; }
                break;
            }
        }

        return ListMembersResult.Ok(new ListMembersPage(
            moduleVersionId, typeMetadataToken, fullName, results, nextCursor, truncated));
    }

    private static void CollectFields(ModuleHandle module, TypeDefinition td, StringSignatureProvider provider, List<MemberSummary> sink)
    {
        foreach (var fh in td.GetFields())
        {
            try
            {
                var f = module.MD.GetFieldDefinition(fh);
                var name = module.MD.GetString(f.Name);
                var fieldType = f.DecodeSignature(provider, genericContext: null);
                var token = MetadataTokens.GetToken(fh);
                var attrs = FormatFieldAttributes(f.Attributes);
                sink.Add(new MemberSummary(
                    module.Mvid, token, HandleSyntax.FormatField(module.Mvid, token),
                    MemberKind.Field, name, $"{fieldType} {name}", attrs));
            }
            catch (BadImageFormatException) { /* skip malformed row */ }
        }
    }

    private static void CollectProperties(ModuleHandle module, TypeDefinition td, StringSignatureProvider provider, List<MemberSummary> sink)
    {
        foreach (var ph in td.GetProperties())
        {
            try
            {
                var p = module.MD.GetPropertyDefinition(ph);
                var name = module.MD.GetString(p.Name);
                var sig = p.DecodeSignature(provider, genericContext: null);
                var accessors = p.GetAccessors();
                var hasGet = !accessors.Getter.IsNil;
                var hasSet = !accessors.Setter.IsNil;
                var accessorsRender = (hasGet, hasSet) switch
                {
                    (true, true) => "{ get; set; }",
                    (true, false) => "{ get; }",
                    (false, true) => "{ set; }",
                    _ => "{ }",
                };
                var paramList = sig.ParameterTypes.Length == 0
                    ? string.Empty
                    : $"[{string.Join(", ", sig.ParameterTypes)}]";
                var token = MetadataTokens.GetToken(ph);
                var attrs = new List<string>(2);
                if ((p.Attributes & PropertyAttributes.SpecialName) != 0) attrs.Add("specialname");
                if ((p.Attributes & PropertyAttributes.RTSpecialName) != 0) attrs.Add("rtspecialname");
                sink.Add(new MemberSummary(
                    module.Mvid, token, HandleSyntax.FormatProperty(module.Mvid, token),
                    MemberKind.Property, name, $"{sig.ReturnType} {name}{paramList} {accessorsRender}", attrs));
            }
            catch (BadImageFormatException) { /* skip malformed row */ }
        }
    }

    private static void CollectEvents(ModuleHandle module, TypeDefinition td, StringSignatureProvider provider, List<MemberSummary> sink)
    {
        foreach (var eh in td.GetEvents())
        {
            try
            {
                var e = module.MD.GetEventDefinition(eh);
                var name = module.MD.GetString(e.Name);
                var typeName = e.Type.Kind switch
                {
                    HandleKind.TypeDefinition => RenderTypeDef(module, (TypeDefinitionHandle)e.Type),
                    HandleKind.TypeReference => ResolveTypeRefName(module, (TypeReferenceHandle)e.Type, out _) ?? "?",
                    HandleKind.TypeSpecification => DecodeTypeSpec(module, (TypeSpecificationHandle)e.Type, provider),
                    _ => "?",
                };
                var token = MetadataTokens.GetToken(eh);
                var attrs = new List<string>(2);
                if ((e.Attributes & EventAttributes.SpecialName) != 0) attrs.Add("specialname");
                if ((e.Attributes & EventAttributes.RTSpecialName) != 0) attrs.Add("rtspecialname");
                sink.Add(new MemberSummary(
                    module.Mvid, token, HandleSyntax.FormatEvent(module.Mvid, token),
                    MemberKind.Event, name, $"event {typeName} {name}", attrs));
            }
            catch (BadImageFormatException) { /* skip malformed row */ }
        }
    }

    private static string DecodeTypeSpec(ModuleHandle module, TypeSpecificationHandle handle, StringSignatureProvider provider)
    {
        try
        {
            var spec = module.MD.GetTypeSpecification(handle);
            return spec.DecodeSignature(provider, genericContext: null);
        }
        catch (BadImageFormatException) { return "?"; }
    }

    private static List<string> FormatFieldAttributes(FieldAttributes a)
    {
        var list = new List<string>(4);
        switch (a & FieldAttributes.FieldAccessMask)
        {
            case FieldAttributes.Public: list.Add("public"); break;
            case FieldAttributes.Family: list.Add("protected"); break;
            case FieldAttributes.Assembly: list.Add("internal"); break;
            case FieldAttributes.FamORAssem: list.Add("protected internal"); break;
            case FieldAttributes.Private: list.Add("private"); break;
            case FieldAttributes.PrivateScope: list.Add("compiler-generated"); break;
            case FieldAttributes.FamANDAssem: list.Add("private protected"); break;
        }
        if ((a & FieldAttributes.Static) != 0) list.Add("static");
        if ((a & FieldAttributes.InitOnly) != 0) list.Add("readonly");
        if ((a & FieldAttributes.Literal) != 0) list.Add("const");
        if ((a & FieldAttributes.SpecialName) != 0) list.Add("specialname");
        return list;
    }

    /// <inheritdoc />
    public ListAttributesResult ListAttributes(AttributeTarget target, ListAttributesQuery query)
    {
        if (target is null)
            return ListAttributesResult.Fail(new AssemblyError(ErrorKinds.InvalidArgument, "target is required."));
        query ??= new ListAttributesQuery();

        if (target.ModuleVersionId == Guid.Empty)
            return ListAttributesResult.Fail(new AssemblyError(ErrorKinds.IdentityMalformed, "moduleVersionId is required."));
        if (!_store.TryGet(target.ModuleVersionId, out var module))
            return ListAttributesResult.Fail(new AssemblyError(ErrorKinds.ModuleNotFound,
                $"no loaded module has MVID {target.ModuleVersionId:D}."));

        CustomAttributeHandleCollection handles;
        try
        {
            handles = GetAttributeHandles(module, target, out var owningError);
            if (owningError is not null) return ListAttributesResult.Fail(owningError);
        }
        catch (BadImageFormatException ex)
        {
            return ListAttributesResult.Fail(new AssemblyError(ErrorKinds.TokenOutOfRange,
                $"could not read attributes for token 0x{target.MetadataToken:X8}: {ex.Message}"));
        }

        var pageSize = query.PageSize <= 0 ? ListAttributesQuery.DefaultPageSize
            : Math.Min(query.PageSize, ListAttributesQuery.MaxPageSize);
        var startToken = query.Cursor is { } c && c > 0 ? c : 0;
        var nameFilter = string.IsNullOrEmpty(query.NameContains) ? null : query.NameContains;
        var provider = new StringCustomAttributeTypeProvider(module.MD);

        var results = new List<AttributeSummary>(Math.Min(pageSize, 16));
        int? nextCursor = null;
        bool truncated = false;

        foreach (var ah in handles)
        {
            var token = MetadataTokens.GetToken(ah);
            // Exclusive cursor: skip everything at or below the last token we emitted.
            if (token <= startToken) continue;

            AttributeSummary? summary;
            try { summary = TryDecodeAttribute(module, ah, token, provider); }
            catch (BadImageFormatException) { continue; }
            if (summary is null) continue;

            if (nameFilter is not null
                && summary.AttributeTypeFullName.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;

            if (results.Count == pageSize)
            {
                nextCursor = results[^1].MetadataToken;
                truncated = true;
                break;
            }
            results.Add(summary);
        }

        return ListAttributesResult.Ok(new ListAttributesPage(
            target.ModuleVersionId,
            target.Kind,
            target.MetadataToken,
            target.ParameterSequence,
            results,
            nextCursor,
            truncated));
    }

    private static CustomAttributeHandleCollection GetAttributeHandles(
        ModuleHandle module, AttributeTarget target, out AssemblyError? error)
    {
        error = null;
        switch (target.Kind)
        {
            case AttributeTargetKind.Assembly:
            {
                if (module.MD.IsAssembly)
                    return module.MD.GetAssemblyDefinition().GetCustomAttributes();
                error = new AssemblyError(ErrorKinds.TokenWrongTable,
                    "module is a netmodule without an AssemblyDef row.");
                return default;
            }
            case AttributeTargetKind.Type:
            {
                if (!TryHandleOfKind<TypeDefinitionHandle>(target.MetadataToken, HandleKind.TypeDefinition,
                    "TypeDefinition (0x02)", out var h, out error)) return default;
                return module.MD.GetTypeDefinition(h).GetCustomAttributes();
            }
            case AttributeTargetKind.Method:
            {
                if (!TryHandleOfKind<MethodDefinitionHandle>(target.MetadataToken, HandleKind.MethodDefinition,
                    "MethodDefinition (0x06)", out var h, out error)) return default;
                return module.MD.GetMethodDefinition(h).GetCustomAttributes();
            }
            case AttributeTargetKind.Parameter:
            {
                if (!TryHandleOfKind<MethodDefinitionHandle>(target.MetadataToken, HandleKind.MethodDefinition,
                    "MethodDefinition (0x06)", out var methodH, out error)) return default;
                var method = module.MD.GetMethodDefinition(methodH);
                foreach (var ph in method.GetParameters())
                {
                    var p = module.MD.GetParameter(ph);
                    if (p.SequenceNumber == target.ParameterSequence)
                        return p.GetCustomAttributes();
                }
                // The parameter row may be absent when the parameter has no attributes
                // and no metadata-affecting modifier; an empty result is the right answer.
                return default;
            }
            case AttributeTargetKind.Field:
            {
                if (!TryEntityHandleOfKind(target.MetadataToken, HandleKind.FieldDefinition,
                    "FieldDefinition (0x04)", out var eh, out error)) return default;
                return module.MD.GetFieldDefinition((FieldDefinitionHandle)eh).GetCustomAttributes();
            }
            case AttributeTargetKind.Property:
            {
                if (!TryEntityHandleOfKind(target.MetadataToken, HandleKind.PropertyDefinition,
                    "PropertyDefinition (0x17)", out var eh, out error)) return default;
                return module.MD.GetPropertyDefinition((PropertyDefinitionHandle)eh).GetCustomAttributes();
            }
            case AttributeTargetKind.Event:
            {
                if (!TryEntityHandleOfKind(target.MetadataToken, HandleKind.EventDefinition,
                    "EventDefinition (0x14)", out var eh, out error)) return default;
                return module.MD.GetEventDefinition((EventDefinitionHandle)eh).GetCustomAttributes();
            }
            default:
                error = new AssemblyError(ErrorKinds.InvalidArgument, $"unsupported target kind '{target.Kind}'.");
                return default;
        }
    }

    private static bool TryHandleOfKind<THandle>(int token, HandleKind expectedKind, string expectedLabel,
        out THandle handle, out AssemblyError? error) where THandle : struct
    {
        handle = default;
        EntityHandle eh;
        try { eh = (EntityHandle)MetadataTokens.Handle(token); }
        catch (ArgumentException ex)
        {
            error = new AssemblyError(ErrorKinds.IdentityMalformed,
                $"could not interpret token 0x{token:X8} as a metadata handle.", ex.Message);
            return false;
        }
        if (eh.Kind != expectedKind)
        {
            error = new AssemblyError(ErrorKinds.TokenWrongTable,
                $"token 0x{token:X8} is in table {eh.Kind}, expected {expectedLabel}.");
            return false;
        }
        // EntityHandle's implicit conversions cover both TypeDef and MethodDef.
        handle = (THandle)(object)(expectedKind == HandleKind.TypeDefinition
            ? (object)(TypeDefinitionHandle)eh
            : (object)(MethodDefinitionHandle)eh);
        error = null;
        return true;
    }

    private static bool TryEntityHandleOfKind(int token, HandleKind expectedKind, string expectedLabel,
        out EntityHandle handle, out AssemblyError? error)
    {
        handle = default;
        try { handle = (EntityHandle)MetadataTokens.Handle(token); }
        catch (ArgumentException ex)
        {
            error = new AssemblyError(ErrorKinds.IdentityMalformed,
                $"could not interpret token 0x{token:X8} as a metadata handle.", ex.Message);
            return false;
        }
        if (handle.Kind != expectedKind)
        {
            error = new AssemblyError(ErrorKinds.TokenWrongTable,
                $"token 0x{token:X8} is in table {handle.Kind}, expected {expectedLabel}.");
            return false;
        }
        error = null;
        return true;
    }

    private static AttributeSummary? TryDecodeAttribute(
        ModuleHandle module, CustomAttributeHandle handle, int token,
        StringCustomAttributeTypeProvider provider)
    {
        var ca = module.MD.GetCustomAttribute(handle);
        // Resolve the attribute type by walking the constructor's Parent. The constructor is
        // a MemberRef (cross-module) or a MethodDef (same module).
        string typeFullName;
        string? assemblyName = null;
        switch (ca.Constructor.Kind)
        {
            case HandleKind.MemberReference:
            {
                var mr = module.MD.GetMemberReference((MemberReferenceHandle)ca.Constructor);
                typeFullName = ResolveOutboundTypeName(module, mr.Parent, out assemblyName) ?? "?";
                break;
            }
            case HandleKind.MethodDefinition:
            {
                var md = module.MD.GetMethodDefinition((MethodDefinitionHandle)ca.Constructor);
                var td = module.MD.GetTypeDefinition(md.GetDeclaringType());
                typeFullName = TypeName(module, td);
                break;
            }
            default:
                return null;
        }

        CustomAttributeValue<string> value;
        try { value = ca.DecodeValue(provider); }
        catch (BadImageFormatException) { return null; }
        catch (NotSupportedException) { return null; }
        catch (UnknownTypeException) { return null; }

        var fixedArgs = new List<AttributeArgument>(value.FixedArguments.Length);
        foreach (var fa in value.FixedArguments)
            fixedArgs.Add(new AttributeArgument(fa.Type ?? "?", RenderAttributeValue(fa.Value)));
        var namedArgs = new List<AttributeArgument>(value.NamedArguments.Length);
        foreach (var na in value.NamedArguments)
            namedArgs.Add(new AttributeArgument(na.Type ?? "?", RenderAttributeValue(na.Value), na.Name));

        return new AttributeSummary(typeFullName, token, fixedArgs, namedArgs, assemblyName);
    }

    private static object? RenderAttributeValue(object? raw)
    {
        // The decoder hands us primitives, strings, type-as-string (from our provider), nulls,
        // and ImmutableArray<CustomAttributeTypedArgument<string>> for arrays. Flatten arrays
        // recursively so the response is plain JSON-friendly objects.
        if (raw is null) return null;
        if (raw is not ImmutableArray<CustomAttributeTypedArgument<string>> array) return raw;
        var list = new List<object?>(array.Length);
        foreach (var element in array)
            list.Add(RenderAttributeValue(element.Value));
        return list;
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
                $"assemblyPathHint '{hint}' has MVID {probe.Mvid:D} but the caller requested {moduleVersionId:D}.");
        }
        var load = _store.Load(hint);
        return load.IsSuccess ? null : load.Error;
    }

    /// <inheritdoc />
    public FindTypeByNameResult FindTypeByFullName(Guid moduleVersionId, string typeFullName)
    {
        if (moduleVersionId == Guid.Empty)
            return FindTypeByNameResult.Fail(new AssemblyError(ErrorKinds.IdentityMalformed, "moduleVersionId is required."));
        if (string.IsNullOrWhiteSpace(typeFullName))
            return FindTypeByNameResult.Fail(new AssemblyError(ErrorKinds.InvalidArgument, "typeFullName is required."));
        if (!_store.TryGet(moduleVersionId, out var module))
            return FindTypeByNameResult.Fail(new AssemblyError(ErrorKinds.ModuleNotFound,
                $"no loaded module has MVID {moduleVersionId:D}."));

        var total = module.MD.TypeDefinitions.Count;
        for (int row = 1; row <= total; row++)
        {
            TypeSummary? summary;
            try { summary = TrySummarizeType(module, row); }
            catch (BadImageFormatException) { continue; }
            if (summary is null) continue;
            if (string.Equals(summary.FullName, typeFullName, StringComparison.Ordinal))
                return FindTypeByNameResult.Ok(summary);
        }
        return FindTypeByNameResult.Fail(new AssemblyError(ErrorKinds.IdentityMalformed,
            $"type '{typeFullName}' not found in module {moduleVersionId:D}."));
    }
}

