using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Identity;

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
    /// <summary>Debounce window applied to <see cref="FileSystemWatcher"/> events.</summary>
    public static readonly TimeSpan WatchDebounce = TimeSpan.FromMilliseconds(250);

    private readonly ConcurrentDictionary<Guid, Module> _modules = new();
    private readonly ConcurrentDictionary<Guid, string> _pathHints = new();
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _pendingReloads =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _watch;
    private int _disposed;

    private readonly ConcurrentDictionary<Guid, XrefData> _xrefCache = new();
    private readonly ConcurrentDictionary<Guid, StringIndexData> _stringIndexCache = new();
    private readonly ConcurrentDictionary<Guid, AttributeIndexData> _attributeIndexCache = new();
    private readonly ConcurrentDictionary<Guid, FieldAccessIndexData> _fieldAccessCache = new();
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
    public MetadataIndex(bool watchForChanges) => _watch = watchForChanges;

    /// <inheritdoc />
    public LoadResult Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return LoadResult.Fail(new AssemblyError(ErrorKinds.InvalidArgument, "path is required."));
        if (!File.Exists(path))
            return LoadResult.Fail(new AssemblyError(ErrorKinds.ModuleLoadFailed, $"file not found: {path}"));

        var fullPath = Path.GetFullPath(path);
        var loaded = OpenAndRegister(fullPath);
        if (!loaded.IsSuccess) return loaded;

        if (_watch) EnsureWatcher(fullPath);
        return loaded;
    }

    private LoadResult OpenAndRegister(string fullPath)
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
                var replacement = new Module(mvid, fullPath, opened.PE!, opened.MD!);
                if (_modules.TryUpdate(mvid, replacement, existing))
                {
                    existing.PE.Dispose();
                    // Even when the MVID hasn't changed, the IL we just opened may differ from
                    // what built the xref / source caches (deterministic rebuild, manual file
                    // swap). Drop those caches and fan the event out so subscribers (e.g. the
                    // decompiler engine cache) can refresh too.
                    InvalidateXref(mvid);
                    ModuleReloaded?.Invoke(this, new ModuleReloadedEventArgs(fullPath, mvid, mvid, null));
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
        Module? Module, PEReader? PE, MetadataReader? MD, AssemblyError? Error);

    private static OpenedModule OpenModule(string fullPath)
    {
        try
        {
            // Read the bytes once and back the PEReader with a MemoryStream so the file on disk
            // stays unlocked. Required for the Tier-1 watcher to be able to observe rewrites on
            // Windows, where File.Move(overwrite: true) needs the destination to be free of
            // open writable handles. Per the spike, fixture-sized assemblies cost ~tens of KB
            // resident — well within the Tier-1 budget.
            var bytes = File.ReadAllBytes(fullPath);
            var pe = new PEReader(new MemoryStream(bytes, writable: false));
            if (!pe.HasMetadata)
            {
                pe.Dispose();
                return new OpenedModule(null, null, null,
                    new AssemblyError(ErrorKinds.ModuleLoadFailed, $"not a managed PE: {fullPath}"));
            }
            var md = pe.GetMetadataReader();
            var mvid = md.GetGuid(md.GetModuleDefinition().Mvid);
            return new OpenedModule(new Module(mvid, fullPath, pe, md), pe, md, null);
        }
        catch (BadImageFormatException ex)
        {
            return new OpenedModule(null, null, null,
                new AssemblyError(ErrorKinds.ModuleLoadFailed, "invalid PE/CLI image.", ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            return new OpenedModule(null, null, null,
                new AssemblyError(ErrorKinds.ModuleLoadFailed, "permission denied.", ex.Message));
        }
        catch (IOException ex)
        {
            return new OpenedModule(null, null, null,
                new AssemblyError(ErrorKinds.ModuleLoadFailed, "i/o error opening assembly.", ex.Message));
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
            stale.PE.Dispose();
            InvalidateXref(prev);
        }
        else if (oldMvid is { } same && same == newMvid)
        {
            InvalidateXref(same);
        }

        ModuleReloaded?.Invoke(this, new ModuleReloadedEventArgs(fullPath, oldMvid, newMvid, null));
    }

    /// <inheritdoc />
    public IReadOnlyList<ModuleSummary> List()
    {
        var list = new List<ModuleSummary>(_modules.Count);
        foreach (var m in _modules.Values)
            list.Add(SummarizeModule(m));
        return list;
    }

    /// <inheritdoc />
    public ProbeResult Probe(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return ProbeResult.Fail(new AssemblyError(ErrorKinds.InvalidArgument, "path is required."));
        if (!File.Exists(path))
            return ProbeResult.Fail(new AssemblyError(ErrorKinds.ModuleLoadFailed, $"file not found: {path}"));
        var fullPath = Path.GetFullPath(path);
        var opened = OpenModule(fullPath);
        if (opened.Error is not null) return ProbeResult.Fail(opened.Error);
        try { return ProbeResult.Ok(opened.Module!.Mvid); }
        finally { opened.PE!.Dispose(); }
    }

    /// <inheritdoc />
    public void RegisterPathHint(Guid moduleVersionId, string path)
    {
        if (moduleVersionId == Guid.Empty || string.IsNullOrWhiteSpace(path)) return;
        _pathHints[moduleVersionId] = Path.GetFullPath(path);
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
    public IReadOnlyDictionary<Guid, string> PathHints => _pathHints;

    /// <inheritdoc />
    public void WatchPath(string path)
    {
        if (!_watch || string.IsNullOrWhiteSpace(path)) return;
        EnsureWatcher(Path.GetFullPath(path));
    }

    /// <inheritdoc />
    public ListTypesResult ListTypes(Guid moduleVersionId, ListTypesQuery query)
    {
        query ??= new ListTypesQuery();
        if (moduleVersionId == Guid.Empty)
            return ListTypesResult.Fail(new AssemblyError(ErrorKinds.IdentityMalformed, "moduleVersionId is required."));
        if (!_modules.TryGetValue(moduleVersionId, out var module))
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
        if (!_modules.TryGetValue(moduleVersionId, out var module))
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
        if (!_modules.TryGetValue(moduleVersionId, out var module))
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

    private static TypeSummary? TrySummarizeType(Module module, int row)
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
        return new TypeSummary(module.Mvid, token, HandleFormat.FormatType(module.Mvid, token),
            fullName, kind, methodCount, isPublic, baseType, interfaces);
    }

    private static TypeReferenceSummary? TryRenderTypeReferenceSummary(Module module, EntityHandle handle)
    {
        if (handle.IsNil) return null;
        try
        {
            switch (handle.Kind)
            {
                case HandleKind.TypeReference:
                {
                    var name = ResolveTypeRefName(module, (TypeReferenceHandle)handle, out var asmName);
                    return name is null ? null : new TypeReferenceSummary(name, asmName);
                }
                case HandleKind.TypeDefinition:
                    return new TypeReferenceSummary(RenderTypeDef(module, (TypeDefinitionHandle)handle));
                case HandleKind.TypeSpecification:
                {
                    var name = ResolveOutboundTypeName(module, handle, out var asmName);
                    return name is null ? null : new TypeReferenceSummary(name, asmName);
                }
                default:
                    return null;
            }
        }
        catch (BadImageFormatException) { return null; }
    }

    private static IReadOnlyList<TypeReferenceSummary> ReadInterfaceImplementations(Module module, TypeDefinition td)
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

    private static TypeKind ClassifyTypeKind(Module module, TypeDefinition td)
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
    public ResolveResult Resolve(MethodIdentity identity)
    {
        if (identity.ModuleVersionId == Guid.Empty)
            return ResolveResult.Fail(new AssemblyError(ErrorKinds.IdentityMalformed, "moduleVersionId is required."));
        if (identity.MetadataToken == 0)
            return ResolveResult.Fail(new AssemblyError(ErrorKinds.IdentityMalformed, "metadataToken is required."));

        if (!_modules.TryGetValue(identity.ModuleVersionId, out var module))
        {
            return ResolveResult.Fail(new AssemblyError(
                ErrorKinds.ModuleNotFound,
                $"no loaded module has MVID {identity.ModuleVersionId}.",
                "call load_assembly with the path to the assembly first, or list_assemblies to see what is loaded."));
        }

        var handle = MetadataTokens.Handle(identity.MetadataToken);
        if (handle.Kind != HandleKind.MethodDefinition)
        {
            return ResolveResult.Fail(new AssemblyError(
                ErrorKinds.TokenWrongTable,
                $"metadataToken 0x{identity.MetadataToken:X8} is a {handle.Kind}, expected MethodDefinition (table 0x06)."));
        }

        var methodHandle = (MethodDefinitionHandle)handle;
        var rid = MetadataTokens.GetRowNumber(methodHandle);
        if (rid <= 0 || rid > module.MD.MethodDefinitions.Count)
        {
            return ResolveResult.Fail(new AssemblyError(
                ErrorKinds.TokenOutOfRange,
                $"MethodDef row {rid} exceeds the module's table size ({module.MD.MethodDefinitions.Count})."));
        }

        var summary = SummarizeMethod(module, methodHandle, identity.MetadataToken);

        // §3.5: optional closed generic instantiation. Either (or both) of:
        //   - explicit TypeGenericArguments / MethodGenericArguments string lists, validated
        //     against loaded modules.
        //   - MethodSpec fast-path (#9): a (mvid, token) into a MethodSpec row whose
        //     Instantiation blob carries the method-level args, and whose Method.Parent
        //     (if a TypeSpec) carries the type-level args.
        // When both are present they are cross-checked element-wise; a mismatch yields
        // GenericInstantiationMismatch.
        IReadOnlyList<string>? typeRendered = null;
        IReadOnlyList<string>? methodRendered = null;

        if (identity.MethodSpec is { } specRef)
        {
            // §3.5 fallback: if explicit args were supplied AND the methodSpec module is not loaded,
            // skip the fast-path and let the explicit-args branch handle validation/substitution.
            bool hasExplicitArgs = identity.TypeGenericArguments is { Count: > 0 }
                                   || identity.MethodGenericArguments is { Count: > 0 };
            var specDecoded = TryDecodeMethodSpec(specRef, allowMissingModule: hasExplicitArgs);
            if (specDecoded.Error is not null) return ResolveResult.Fail(specDecoded.Error);

            if (specDecoded.SpecModule is not null && specDecoded.SpecRow is { } specRow)
            {
                // §3.5 target validation: the MethodSpec.Method must resolve to the requested MethodDef.
                if (!MethodSpecTargetsMethodDef(
                        specDecoded.SpecModule, specRow,
                        identity.ModuleVersionId, identity.MetadataToken,
                        out var targetErr))
                {
                    return ResolveResult.Fail(targetErr!);
                }

                typeRendered = specDecoded.TypeRendered;
                methodRendered = specDecoded.MethodRendered;
            }
        }

        bool hasTypeArgs = identity.TypeGenericArguments is { Count: > 0 };
        bool hasMethodArgs = identity.MethodGenericArguments is { Count: > 0 };
        if (hasTypeArgs || hasMethodArgs)
        {
            var def0 = module.MD.GetMethodDefinition(methodHandle);
            var typeDef0 = module.MD.GetTypeDefinition(def0.GetDeclaringType());
            int typeArity = typeDef0.GetGenericParameters().Count;
            int methodArity = def0.GetGenericParameters().Count;

            int gotType = identity.TypeGenericArguments?.Count ?? 0;
            int gotMethod = identity.MethodGenericArguments?.Count ?? 0;

            if (gotType != 0 && gotType != typeArity)
                return ResolveResult.Fail(new AssemblyError(
                    ErrorKinds.InvalidArgument,
                    $"genericTypeArguments.type carries {gotType} args but the declaring type has arity {typeArity}."));
            if (gotMethod != 0 && gotMethod != methodArity)
                return ResolveResult.Fail(new AssemblyError(
                    ErrorKinds.InvalidArgument,
                    $"genericTypeArguments.method carries {gotMethod} args but the method has arity {methodArity}."));

            var readers = SnapshotReaders();

            if (hasTypeArgs)
            {
                var (rendered, err) = GenericArgResolver.RenderAndValidate(
                    identity.TypeGenericArguments!, identity.ModuleVersionId, readers);
                if (err is not null) return ResolveResult.Fail(err);
                if (typeRendered is not null && !RenderedSequenceEqual(typeRendered, rendered!))
                    return ResolveResult.Fail(new AssemblyError(
                        ErrorKinds.GenericInstantiationMismatch,
                        $"methodSpec encodes type-args [{string.Join(",", typeRendered)}] but genericTypeArguments has [{string.Join(",", rendered!)}]."));
                typeRendered = rendered!;
            }

            if (hasMethodArgs)
            {
                var (rendered, err) = GenericArgResolver.RenderAndValidate(
                    identity.MethodGenericArguments!, identity.ModuleVersionId, readers);
                if (err is not null) return ResolveResult.Fail(err);
                if (methodRendered is not null && !RenderedSequenceEqual(methodRendered, rendered!))
                    return ResolveResult.Fail(new AssemblyError(
                        ErrorKinds.GenericInstantiationMismatch,
                        $"methodSpec encodes method-args [{string.Join(",", methodRendered)}] but genericMethodArguments has [{string.Join(",", rendered!)}]."));
                methodRendered = rendered!;
            }
        }

        if (typeRendered is not null || methodRendered is not null)
        {
            var def = module.MD.GetMethodDefinition(methodHandle);
            var typeDef = module.MD.GetTypeDefinition(def.GetDeclaringType());

            var provider = new SubstitutingStringSignatureProvider(
                module.MD, typeRendered ?? Array.Empty<string>(), methodRendered ?? Array.Empty<string>());
            var sig = def.DecodeSignature(provider, genericContext: null);
            var paramList = string.Join(", ", sig.ParameterTypes);
            var fullType = TypeName(module, typeDef);
            var methodName = module.MD.GetString(def.Name);
            var closedSig = $"{sig.ReturnType} {fullType}.{methodName}({paramList})";

            summary = summary with { Signature = closedSig };
        }

        return ResolveResult.Ok(summary);
    }

    private static bool RenderedSequenceEqual(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>
    /// §3.5 fast-path decoder. Returns the type/method instantiation rendered in wire format
    /// from a <c>MethodSpec</c> row. When <paramref name="allowMissingModule"/> is true and the
    /// spec module is not loaded, returns success with all-null payload (caller falls back to
    /// explicit args). Otherwise an unloaded module yields <see cref="ErrorKinds.ModuleNotFound"/>.
    /// </summary>
    private MethodSpecDecodeResult TryDecodeMethodSpec(MethodSpecHandle specRef, bool allowMissingModule)
    {
        if (!_modules.TryGetValue(specRef.ModuleVersionId, out var specModule))
        {
            if (allowMissingModule) return default;
            return new MethodSpecDecodeResult(null, null, null, null, new AssemblyError(
                ErrorKinds.ModuleNotFound,
                $"methodSpec module {specRef.ModuleVersionId:D} is not loaded; load it first or omit methodSpec."));
        }

        EntityHandle specHandle;
        try { specHandle = (EntityHandle)MetadataTokens.Handle(specRef.MetadataToken); }
        catch (ArgumentException)
        {
            return new MethodSpecDecodeResult(null, null, null, null, new AssemblyError(
                ErrorKinds.InvalidArgument,
                $"methodSpec token 0x{specRef.MetadataToken:X8} is not a valid metadata token."));
        }
        if (specHandle.Kind != HandleKind.MethodSpecification)
        {
            return new MethodSpecDecodeResult(null, null, null, null, new AssemblyError(
                ErrorKinds.TokenWrongTable,
                $"methodSpec token 0x{specRef.MetadataToken:X8} is a {specHandle.Kind}, expected MethodSpecification (table 0x2B)."));
        }

        MethodSpecification specRow;
        try { specRow = specModule.MD.GetMethodSpecification((MethodSpecificationHandle)specHandle); }
        catch (BadImageFormatException)
        {
            return new MethodSpecDecodeResult(null, null, null, null, new AssemblyError(
                ErrorKinds.InvalidArgument,
                $"methodSpec token 0x{specRef.MetadataToken:X8} could not be decoded."));
        }

        var wireProvider = new WireFormatSignatureProvider();
        IReadOnlyList<string> methodRendered;
        try
        {
            methodRendered = specRow.DecodeSignature(wireProvider, genericContext: (object?)null);
        }
        catch (BadImageFormatException)
        {
            return new MethodSpecDecodeResult(null, null, null, null, new AssemblyError(
                ErrorKinds.InvalidArgument,
                "methodSpec Instantiation blob could not be decoded."));
        }

        IReadOnlyList<string>? typeRendered = null;
        if (specRow.Method.Kind == HandleKind.MemberReference)
        {
            try
            {
                var mr = specModule.MD.GetMemberReference((MemberReferenceHandle)specRow.Method);
                if (mr.Parent.Kind == HandleKind.TypeSpecification)
                {
                    var ts = specModule.MD.GetTypeSpecification((TypeSpecificationHandle)mr.Parent);
                    var typeDecoded = ts.DecodeSignature(wireProvider, genericContext: (object?)null);
                    if (GenericTypeName.TryParse(typeDecoded, out var node, out _, out _)
                        && node is GenericTypeName.Named named
                        && !named.TypeArguments.IsDefaultOrEmpty)
                    {
                        typeRendered = named.TypeArguments.Select(a => a.Format()).ToArray();
                    }
                }
            }
            catch (BadImageFormatException) { /* leave typeRendered null */ }
        }

        return new MethodSpecDecodeResult(specModule, specRow, typeRendered, methodRendered, null);
    }

    private readonly record struct MethodSpecDecodeResult(
        Module? SpecModule,
        MethodSpecification? SpecRow,
        IReadOnlyList<string>? TypeRendered,
        IReadOnlyList<string>? MethodRendered,
        AssemblyError? Error);

    /// <summary>
    /// §3.5 target validation: verifies that <paramref name="specRow"/>.<c>Method</c> resolves
    /// to the requested <c>(targetMvid, targetMethodDefToken)</c>. Returns false (with a
    /// <see cref="ErrorKinds.GenericInstantiationMismatch"/> error) when the spec was
    /// authored against a different MethodDef.
    /// </summary>
    private bool MethodSpecTargetsMethodDef(
        Module specModule, MethodSpecification specRow,
        Guid targetMvid, int targetMethodDefToken,
        out AssemblyError? validationError)
    {
        validationError = null;
        switch (specRow.Method.Kind)
        {
            case HandleKind.MethodDefinition:
                if (specModule.Mvid != targetMvid)
                {
                    validationError = new AssemblyError(
                        ErrorKinds.GenericInstantiationMismatch,
                        $"methodSpec.Method is a MethodDef in module {specModule.Mvid:D} but the target method lives in {targetMvid:D}.");
                    return false;
                }
                var specMethodToken = MetadataTokens.GetToken(specRow.Method);
                if (specMethodToken != targetMethodDefToken)
                {
                    validationError = new AssemblyError(
                        ErrorKinds.GenericInstantiationMismatch,
                        $"methodSpec.Method targets MethodDef 0x{specMethodToken:X8} but the requested identity is 0x{targetMethodDefToken:X8}.");
                    return false;
                }
                return true;

            case HandleKind.MemberReference:
                if (!_modules.TryGetValue(targetMvid, out var targetMod))
                {
                    validationError = new AssemblyError(
                        ErrorKinds.ModuleNotFound,
                        $"target module {targetMvid:D} is not loaded; cannot cross-check methodSpec target.");
                    return false;
                }
                MethodDefinitionHandle targetHandle;
                try { targetHandle = (MethodDefinitionHandle)MetadataTokens.Handle(targetMethodDefToken); }
                catch (ArgumentException)
                {
                    validationError = new AssemblyError(
                        ErrorKinds.InvalidArgument,
                        $"target token 0x{targetMethodDefToken:X8} is not a valid metadata token.");
                    return false;
                }
                var key = BuildCalleeKey(targetMod, targetHandle);
                try
                {
                    var mr = specModule.MD.GetMemberReference((MemberReferenceHandle)specRow.Method);
                    if (mr.GetKind() != MemberReferenceKind.Method
                        || !MemberRefMatchesCalleeKey(specModule, mr, key))
                    {
                        validationError = new AssemblyError(
                            ErrorKinds.GenericInstantiationMismatch,
                            "methodSpec.Method (MemberRef) does not resolve to the requested MethodDef (assembly/type/name/signature mismatch).");
                        return false;
                    }
                }
                catch (BadImageFormatException)
                {
                    validationError = new AssemblyError(
                        ErrorKinds.InvalidArgument,
                        "methodSpec.Method MemberRef row could not be decoded.");
                    return false;
                }
                return true;

            default:
                validationError = new AssemblyError(
                    ErrorKinds.InvalidArgument,
                    $"methodSpec.Method has unsupported kind {specRow.Method.Kind}.");
                return false;
        }
    }

    private Dictionary<Guid, Func<MetadataReader>> SnapshotReaders()
    {
        var dict = new Dictionary<Guid, Func<MetadataReader>>(_modules.Count);
        foreach (var (mvid, mod) in _modules)
        {
            var local = mod; // capture
            dict[mvid] = () => local.MD;
        }
        return dict;
    }

    /// <inheritdoc />
    public IlBodyResult GetIlBody(MethodIdentity identity, int maxBytes = 0, CancellationToken cancellationToken = default)
    {
        var common = TryResolveMethod(identity);
        if (common.Error is not null) return IlBodyResult.Fail(common.Error);
        var (module, methodHandle) = (common.Module!, common.Handle);

        var def = module.MD.GetMethodDefinition(methodHandle);
        if (def.RelativeVirtualAddress == 0)
        {
            // Abstract / extern / trimmed body — emit an empty body rather than failing.
            var handleStr = HandleFormat.Format(module.Mvid, identity.MetadataToken);
            return IlBodyResult.Ok(new IlMethodBody(
                module.Mvid, identity.MetadataToken, handleStr,
                IlSize: 0, MaxStack: 0, ExceptionRegionCount: 0, InstructionCount: 0,
                IlHex: string.Empty, IlTruncated: false));
        }

        try
        {
            var body = module.PE.GetMethodBody(def.RelativeVirtualAddress);
            var ilBytes = body.GetILBytes() ?? Array.Empty<byte>();
            var cap = maxBytes > 0 ? maxBytes : DefaultIlMaxBytes;
            var hexLen = Math.Min(ilBytes.Length, cap);
            var hex = Convert.ToHexString(ilBytes.AsSpan(0, hexLen));
            var truncated = hexLen < ilBytes.Length;
            var instructions = CountInstructions(ilBytes, cancellationToken);

            return IlBodyResult.Ok(new IlMethodBody(
                module.Mvid, identity.MetadataToken,
                HandleFormat.Format(module.Mvid, identity.MetadataToken),
                IlSize: ilBytes.Length,
                MaxStack: body.MaxStack,
                ExceptionRegionCount: body.ExceptionRegions.Length,
                InstructionCount: instructions,
                IlHex: hex,
                IlTruncated: truncated));
        }
        catch (BadImageFormatException ex)
        {
            return IlBodyResult.Fail(new AssemblyError(ErrorKinds.ModuleLoadFailed,
                "method body is malformed.", ex.Message));
        }
    }

    /// <inheritdoc />
    public IlScanReadResult ScanIl(MethodIdentity identity, CancellationToken cancellationToken = default)
    {
        var common = TryResolveMethod(identity);
        if (common.Error is not null) return IlScanReadResult.Fail(common.Error);
        var (module, methodHandle) = (common.Module!, common.Handle);

        var def = module.MD.GetMethodDefinition(methodHandle);
        var handleStr = HandleFormat.Format(module.Mvid, identity.MetadataToken);

        if (def.RelativeVirtualAddress == 0)
        {
            return IlScanReadResult.Ok(new IlScanResult(
                module.Mvid, identity.MetadataToken, handleStr,
                InstructionCount: 0,
                Calls: Array.Empty<IlSymbolRef>(),
                Fields: Array.Empty<IlSymbolRef>(),
                Types: Array.Empty<IlSymbolRef>(),
                Strings: Array.Empty<string>()));
        }

        byte[] ilBytes;
        try
        {
            var body = module.PE.GetMethodBody(def.RelativeVirtualAddress);
            ilBytes = body.GetILBytes() ?? Array.Empty<byte>();
        }
        catch (BadImageFormatException ex)
        {
            return IlScanReadResult.Fail(new AssemblyError(ErrorKinds.ModuleLoadFailed,
                "method body is malformed.", ex.Message));
        }

        var calls = new List<IlSymbolRef>();
        var fields = new List<IlSymbolRef>();
        var types = new List<IlSymbolRef>();
        var strings = new List<string>();
        int instructions = 0;

        var span = ilBytes.AsSpan();
        int pos = 0;
        int sinceCancelCheck = 0;
        while (pos < span.Length)
        {
            // Cancellation is checked every 256 instructions to keep the hot loop cheap; an
            // unbounded malformed body otherwise spins until the whole IL is walked.
            if ((sinceCancelCheck++ & 0xFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            instructions++;
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
            if (size == -1) // switch: 4-byte N followed by N 4-byte offsets
            {
                if (pos + 4 > span.Length) break;
                var n = BitConverter.ToInt32(span.Slice(pos, 4));
                // Validate without overflow: n quads must fit in the remaining IL.
                if (n < 0 || n > (span.Length - pos - 4) / 4) break;
                pos += 4 + n * 4;
                continue;
            }

            int token = 0;
            if (size == 4 && pos + 4 <= span.Length)
                token = BitConverter.ToInt32(span.Slice(pos, 4));

            switch (op)
            {
                case IlOpcodeTable.Op.InlineMethod:
                    calls.Add(BuildSymbolRef(module, token));
                    break;
                case IlOpcodeTable.Op.InlineField:
                    fields.Add(BuildSymbolRef(module, token));
                    break;
                case IlOpcodeTable.Op.InlineType:
                    types.Add(BuildSymbolRef(module, token));
                    break;
                case IlOpcodeTable.Op.InlineTok:
                    // Could be method/field/type — classify by handle kind.
                    AddTokenRef(module, token, calls, fields, types);
                    break;
                case IlOpcodeTable.Op.InlineString:
                    var s = TryReadUserString(module, token);
                    if (s is not null) strings.Add(s);
                    break;
            }

            pos += Math.Max(0, size);
        }

        return IlScanReadResult.Ok(new IlScanResult(
            module.Mvid, identity.MetadataToken, handleStr,
            instructions, calls, fields, types, strings));
    }

    /// <inheritdoc />
    public FindCallersReadResult FindCallers(MethodIdentity callee, CancellationToken cancellationToken = default)
    {
        var common = TryResolveMethod(callee);
        if (common.Error is not null) return FindCallersReadResult.Fail(common.Error);
        var module = common.Module!;
        var methodHandle = common.Handle;

        var fromCache = true;
        var xref = _xrefCache.GetOrAdd(module.Mvid, _ =>
        {
            fromCache = false;
            return LoadOrBuildXref(module, cancellationToken);
        });

        var callers = new List<CallerRef>();

        // Same-module callers.
        if (xref.Intra.TryGetValue(callee.MetadataToken, out var localCallers))
        {
            foreach (var token in localCallers)
            {
                var h = (MethodDefinitionHandle)MetadataTokens.Handle(token);
                callers.Add(new CallerRef(
                    module.Mvid, token, HandleFormat.Format(module.Mvid, token),
                    RenderMethodDef(module, h)));
            }
        }

        // Cross-module: compute the callee's signature key once and probe every other loaded module.
        var calleeKey = BuildCalleeKey(module, methodHandle);
        var modulesSearched = 1;
        foreach (var other in _modules.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (other.Mvid == module.Mvid) continue;
            modulesSearched++;

            var otherXref = _xrefCache.GetOrAdd(other.Mvid, _ => LoadOrBuildXref(other, cancellationToken));
            foreach (var outbound in otherXref.Outbound)
            {
                if (!outbound.Matches(calleeKey)) continue;
                var h = (MethodDefinitionHandle)MetadataTokens.Handle(outbound.CallerToken);
                callers.Add(new CallerRef(
                    other.Mvid, outbound.CallerToken,
                    HandleFormat.Format(other.Mvid, outbound.CallerToken),
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
            var specDecoded = TryDecodeMethodSpec(specRef, allowMissingModule: hasExplicitArgs);
            if (specDecoded.Error is not null) return FindCallersReadResult.Fail(specDecoded.Error);

            if (specDecoded.SpecModule is not null && specDecoded.SpecRow is { } specRow)
            {
                if (!MethodSpecTargetsMethodDef(
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
            var readers = SnapshotReaders();
            var (rendered, renderErr) = GenericArgResolver.RenderAndValidate(
                methodArgsAst, callee.ModuleVersionId, readers);
            if (renderErr is not null) return FindCallersReadResult.Fail(renderErr);
            if (expectedMethodArgs is not null && !RenderedSequenceEqual(expectedMethodArgs, rendered!))
            {
                return FindCallersReadResult.Fail(new AssemblyError(
                    ErrorKinds.GenericInstantiationMismatch,
                    $"methodSpec encodes method-args [{string.Join(",", expectedMethodArgs)}] but genericMethodArguments has [{string.Join(",", rendered!)}]."));
            }
            expectedMethodArgs = rendered!;
        }

        if (callee.TypeGenericArguments is { Count: > 0 } typeArgsAst)
        {
            var readers = SnapshotReaders();
            var (rendered, renderErr) = GenericArgResolver.RenderAndValidate(
                typeArgsAst, callee.ModuleVersionId, readers);
            if (renderErr is not null) return FindCallersReadResult.Fail(renderErr);
            if (expectedTypeArgs is not null && !RenderedSequenceEqual(expectedTypeArgs, rendered!))
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
                if (!_modules.TryGetValue(c.ModuleVersionId, out var callerMod)) continue;
                if (CallerHasMatchingInstantiation(
                        callerMod, c.MetadataToken, module, methodHandle, calleeKey,
                        expectedTypeArgs, expectedMethodArgs))
                {
                    filtered.Add(c);
                }
            }
            callers = filtered;
        }

        var calleeHandleStr = HandleFormat.Format(module.Mvid, callee.MetadataToken);
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

        IEnumerable<Module> targets;
        if (moduleVersionIdFilter != Guid.Empty)
        {
            if (!_modules.TryGetValue(moduleVersionIdFilter, out var only))
            {
                return FindStringReferencesReadResult.Fail(new AssemblyError(
                    ErrorKinds.ModuleNotFound,
                    $"no loaded module has MVID {moduleVersionIdFilter:D}."));
            }
            targets = new[] { only };
        }
        else
        {
            targets = _modules.Values;
        }

        var hits = new List<StringReferenceRef>();
        var fromCache = true;
        var modulesSearched = 0;
        var truncated = false;

        foreach (var module in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            modulesSearched++;

            var index = _stringIndexCache.GetOrAdd(module.Mvid, _ =>
            {
                fromCache = false;
                return BuildStringIndex(module, cancellationToken);
            });

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

    private static bool AppendHits(Module module, string literal, List<(int MethodToken, int IlOffset)> sites,
        List<StringReferenceRef> output, int maxHits)
    {
        foreach (var (token, offset) in sites)
        {
            if (output.Count >= maxHits) return false;
            var h = (MethodDefinitionHandle)MetadataTokens.Handle(token);
            output.Add(new StringReferenceRef(
                module.Mvid, token, HandleFormat.Format(module.Mvid, token),
                RenderMethodDef(module, h),
                offset, literal));
        }
        return true;
    }

    private static StringIndexData BuildStringIndex(Module module, CancellationToken cancellationToken)
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

    /// <inheritdoc />
    public FindAttributeTargetsReadResult FindAttributeTargets(
        string attributeTypeFullName,
        Guid moduleVersionIdFilter = default,
        IReadOnlyCollection<AttributeTargetKind>? targetKindsFilter = null,
        int maxHits = 0,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(attributeTypeFullName))
            return FindAttributeTargetsReadResult.Fail(new AssemblyError(ErrorKinds.InvalidArgument, "attributeTypeFullName is required."));

        const int DefaultMaxHits = 1000;
        const int HardMaxHits = 10_000;
        if (maxHits <= 0) maxHits = DefaultMaxHits;
        if (maxHits > HardMaxHits) maxHits = HardMaxHits;

        IEnumerable<Module> targets;
        if (moduleVersionIdFilter != Guid.Empty)
        {
            if (!_modules.TryGetValue(moduleVersionIdFilter, out var only))
            {
                return FindAttributeTargetsReadResult.Fail(new AssemblyError(
                    ErrorKinds.ModuleNotFound,
                    $"no loaded module has MVID {moduleVersionIdFilter:D}."));
            }
            targets = new[] { only };
        }
        else
        {
            targets = _modules.Values;
        }

        HashSet<AttributeTargetKind>? kindFilter = null;
        if (targetKindsFilter is { Count: > 0 })
            kindFilter = new HashSet<AttributeTargetKind>(targetKindsFilter);

        var hits = new List<AttributeTargetRef>();
        var fromCache = true;
        var modulesSearched = 0;
        var truncated = false;

        foreach (var module in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            modulesSearched++;
            var index = _attributeIndexCache.GetOrAdd(module.Mvid, _ =>
            {
                fromCache = false;
                return BuildAttributeIndex(module, cancellationToken);
            });

            if (!index.ByAttributeType.TryGetValue(attributeTypeFullName, out var entries))
                continue;

            foreach (var (kind, targetToken, paramSeq, attrToken) in entries)
            {
                if (kindFilter is not null && !kindFilter.Contains(kind)) continue;
                if (hits.Count >= maxHits) { truncated = true; break; }
                var (handle, display) = RenderAttributeTarget(module, kind, targetToken, paramSeq);
                hits.Add(new AttributeTargetRef(
                    module.Mvid, kind, targetToken, paramSeq, handle, display, attrToken));
            }

            if (truncated) break;
        }

        return FindAttributeTargetsReadResult.Ok(new FindAttributeTargetsResult(
            attributeTypeFullName, hits, modulesSearched, fromCache, truncated));
    }

    /// <inheritdoc />
    public FindFieldReferencesReadResult FindFieldReferences(
        Guid moduleVersionId,
        int fieldMetadataToken,
        FieldAccessMode mode = FieldAccessMode.All,
        int maxHits = 0,
        CancellationToken cancellationToken = default)
    {
        if (moduleVersionId == Guid.Empty)
            return FindFieldReferencesReadResult.Fail(new AssemblyError(ErrorKinds.InvalidArgument, "moduleVersionId is required."));
        if (!_modules.TryGetValue(moduleVersionId, out var module))
            return FindFieldReferencesReadResult.Fail(new AssemblyError(
                ErrorKinds.ModuleNotFound, $"no loaded module has MVID {moduleVersionId:D}."));

        EntityHandle eh;
        try { eh = (EntityHandle)MetadataTokens.Handle(fieldMetadataToken); }
        catch (ArgumentOutOfRangeException)
        {
            return FindFieldReferencesReadResult.Fail(new AssemblyError(
                ErrorKinds.TokenOutOfRange, $"token 0x{fieldMetadataToken:X8} is not a valid metadata handle."));
        }
        if (eh.Kind != HandleKind.FieldDefinition)
            return FindFieldReferencesReadResult.Fail(new AssemblyError(
                ErrorKinds.InvalidArgument, $"token 0x{fieldMetadataToken:X8} is not a FieldDefinition (table 0x04)."));

        FieldDefinition fieldDef;
        try { fieldDef = module.MD.GetFieldDefinition((FieldDefinitionHandle)eh); }
        catch (BadImageFormatException)
        {
            return FindFieldReferencesReadResult.Fail(new AssemblyError(
                ErrorKinds.TokenOutOfRange, $"FieldDefinition 0x{fieldMetadataToken:X8} could not be read."));
        }

        const int DefaultMaxHits = 1000;
        const int HardMaxHits = 10_000;
        if (maxHits <= 0) maxHits = DefaultMaxHits;
        if (maxHits > HardMaxHits) maxHits = HardMaxHits;

        var fieldKey = BuildFieldKey(module, fieldDef);

        var hits = new List<FieldReferenceRef>();
        var fromCache = true;
        var modulesSearched = 0;

        // Same-module hits.
        modulesSearched++;
        var localIndex = _fieldAccessCache.GetOrAdd(module.Mvid, _ =>
        {
            fromCache = false;
            return BuildFieldAccessIndex(module, cancellationToken);
        });
        if (localIndex.Intra.TryGetValue(fieldMetadataToken, out var sites))
        {
            foreach (var (callerToken, ilOffset, kind) in sites)
            {
                if (!ModeMatches(mode, kind)) continue;
                if (hits.Count >= maxHits) goto done;
                var h = (MethodDefinitionHandle)MetadataTokens.Handle(callerToken);
                hits.Add(new FieldReferenceRef(
                    module.Mvid, callerToken,
                    HandleFormat.Format(module.Mvid, callerToken),
                    RenderMethodDef(module, h),
                    ilOffset, kind));
            }
        }

        // Cross-module hits.
        if (fieldKey is { } key)
        {
            foreach (var other in _modules.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (other.Mvid == module.Mvid) continue;
                modulesSearched++;
                var otherIndex = _fieldAccessCache.GetOrAdd(other.Mvid, _ =>
                {
                    fromCache = false;
                    return BuildFieldAccessIndex(other, cancellationToken);
                });
                foreach (var outbound in otherIndex.Outbound)
                {
                    if (!outbound.Matches(key)) continue;
                    if (!ModeMatches(mode, outbound.AccessKind)) continue;
                    if (hits.Count >= maxHits) goto done;
                    var h = (MethodDefinitionHandle)MetadataTokens.Handle(outbound.CallerToken);
                    hits.Add(new FieldReferenceRef(
                        other.Mvid, outbound.CallerToken,
                        HandleFormat.Format(other.Mvid, outbound.CallerToken),
                        RenderMethodDef(other, h),
                        outbound.IlOffset, outbound.AccessKind));
                }
            }
        }
    done:

        return FindFieldReferencesReadResult.Ok(new FindFieldReferencesResult(
            module.Mvid, fieldMetadataToken,
            HandleFormat.FormatField(module.Mvid, fieldMetadataToken),
            hits, modulesSearched, fromCache));
    }

    /// <inheritdoc />
    public FindPropertyReferencesReadResult FindPropertyReferences(
        Guid moduleVersionId,
        int propertyMetadataToken,
        PropertyAccessorFilter accessor = PropertyAccessorFilter.All,
        int maxHits = 0,
        CancellationToken cancellationToken = default)
    {
        if (moduleVersionId == Guid.Empty)
            return FindPropertyReferencesReadResult.Fail(new AssemblyError(ErrorKinds.InvalidArgument, "moduleVersionId is required."));
        if (!_modules.TryGetValue(moduleVersionId, out var module))
            return FindPropertyReferencesReadResult.Fail(new AssemblyError(
                ErrorKinds.ModuleNotFound, $"no loaded module has MVID {moduleVersionId:D}."));

        EntityHandle eh;
        try { eh = (EntityHandle)MetadataTokens.Handle(propertyMetadataToken); }
        catch (ArgumentOutOfRangeException)
        {
            return FindPropertyReferencesReadResult.Fail(new AssemblyError(
                ErrorKinds.TokenOutOfRange, $"token 0x{propertyMetadataToken:X8} is not a valid metadata handle."));
        }
        if (eh.Kind != HandleKind.PropertyDefinition)
            return FindPropertyReferencesReadResult.Fail(new AssemblyError(
                ErrorKinds.InvalidArgument, $"token 0x{propertyMetadataToken:X8} is not a PropertyDefinition (table 0x17)."));

        PropertyAccessors accessors;
        try { accessors = module.MD.GetPropertyDefinition((PropertyDefinitionHandle)eh).GetAccessors(); }
        catch (BadImageFormatException)
        {
            return FindPropertyReferencesReadResult.Fail(new AssemblyError(
                ErrorKinds.TokenOutOfRange, $"PropertyDefinition 0x{propertyMetadataToken:X8} could not be read."));
        }

        const int DefaultMaxHits = 1000;
        const int HardMaxHits = 10_000;
        if (maxHits <= 0) maxHits = DefaultMaxHits;
        if (maxHits > HardMaxHits) maxHits = HardMaxHits;

        var hits = new List<PropertyReferenceRef>();
        var fromCacheAll = true;
        var modulesSearchedMax = 0;

        bool wantGetter = accessor != PropertyAccessorFilter.SetterOnly;
        bool wantSetter = accessor != PropertyAccessorFilter.GetterOnly;

        if (wantGetter && !accessors.Getter.IsNil)
        {
            var r = FindCallers(BuildMethodIdentity(module, accessors.Getter), cancellationToken);
            if (r.Error is not null) return FindPropertyReferencesReadResult.Fail(r.Error);
            if (r.Result is not null)
            {
                if (!r.Result.FromCache) fromCacheAll = false;
                if (r.Result.ModulesSearched > modulesSearchedMax) modulesSearchedMax = r.Result.ModulesSearched;
                foreach (var c in r.Result.Callers)
                {
                    if (hits.Count >= maxHits) goto done;
                    hits.Add(new PropertyReferenceRef(
                        c.ModuleVersionId, c.MetadataToken, c.Handle, c.Display,
                        PropertyAccessor.Getter));
                }
            }
        }
        if (wantSetter && !accessors.Setter.IsNil)
        {
            var r = FindCallers(BuildMethodIdentity(module, accessors.Setter), cancellationToken);
            if (r.Error is not null) return FindPropertyReferencesReadResult.Fail(r.Error);
            if (r.Result is not null)
            {
                if (!r.Result.FromCache) fromCacheAll = false;
                if (r.Result.ModulesSearched > modulesSearchedMax) modulesSearchedMax = r.Result.ModulesSearched;
                foreach (var c in r.Result.Callers)
                {
                    if (hits.Count >= maxHits) goto done;
                    hits.Add(new PropertyReferenceRef(
                        c.ModuleVersionId, c.MetadataToken, c.Handle, c.Display,
                        PropertyAccessor.Setter));
                }
            }
        }
    done:

        return FindPropertyReferencesReadResult.Ok(new FindPropertyReferencesResult(
            module.Mvid, propertyMetadataToken,
            HandleFormat.FormatProperty(module.Mvid, propertyMetadataToken),
            hits, modulesSearchedMax, fromCacheAll));
    }

    public FindEventReferencesReadResult FindEventReferences(
        Guid moduleVersionId,
        int eventMetadataToken,
        EventAccessorFilter accessor = EventAccessorFilter.All,
        int maxHits = 0,
        CancellationToken cancellationToken = default)
    {
        if (moduleVersionId == Guid.Empty)
            return FindEventReferencesReadResult.Fail(new AssemblyError(ErrorKinds.InvalidArgument, "moduleVersionId is required."));
        if (!_modules.TryGetValue(moduleVersionId, out var module))
            return FindEventReferencesReadResult.Fail(new AssemblyError(
                ErrorKinds.ModuleNotFound, $"no loaded module has MVID {moduleVersionId:D}."));

        EntityHandle eh;
        try { eh = (EntityHandle)MetadataTokens.Handle(eventMetadataToken); }
        catch (ArgumentOutOfRangeException)
        {
            return FindEventReferencesReadResult.Fail(new AssemblyError(
                ErrorKinds.TokenOutOfRange, $"token 0x{eventMetadataToken:X8} is not a valid metadata handle."));
        }
        if (eh.Kind != HandleKind.EventDefinition)
            return FindEventReferencesReadResult.Fail(new AssemblyError(
                ErrorKinds.InvalidArgument, $"token 0x{eventMetadataToken:X8} is not an EventDefinition (table 0x14)."));

        EventAccessors accessors;
        try { accessors = module.MD.GetEventDefinition((EventDefinitionHandle)eh).GetAccessors(); }
        catch (BadImageFormatException)
        {
            return FindEventReferencesReadResult.Fail(new AssemblyError(
                ErrorKinds.TokenOutOfRange, $"EventDefinition 0x{eventMetadataToken:X8} could not be read."));
        }

        const int DefaultMaxHits = 1000;
        const int HardMaxHits = 10_000;
        if (maxHits <= 0) maxHits = DefaultMaxHits;
        if (maxHits > HardMaxHits) maxHits = HardMaxHits;

        var hits = new List<EventReferenceRef>();
        var fromCacheAll = true;
        var modulesSearchedMax = 0;

        bool wantAdder = accessor is EventAccessorFilter.All or EventAccessorFilter.AdderOnly;
        bool wantRemover = accessor is EventAccessorFilter.All or EventAccessorFilter.RemoverOnly;
        bool wantRaiser = accessor is EventAccessorFilter.All or EventAccessorFilter.RaiserOnly;

        bool Collect(MethodDefinitionHandle methodHandle, EventAccessor kind, out AssemblyError? err)
        {
            err = null;
            if (methodHandle.IsNil) return true;
            var r = FindCallers(BuildMethodIdentity(module, methodHandle), cancellationToken);
            if (r.Error is not null) { err = r.Error; return false; }
            if (r.Result is null) return true;
            if (!r.Result.FromCache) fromCacheAll = false;
            if (r.Result.ModulesSearched > modulesSearchedMax) modulesSearchedMax = r.Result.ModulesSearched;
            foreach (var c in r.Result.Callers)
            {
                if (hits.Count >= maxHits) return false; // signal stop, no error
                hits.Add(new EventReferenceRef(
                    c.ModuleVersionId, c.MetadataToken, c.Handle, c.Display, kind));
            }
            return true;
        }

        AssemblyError? perError;
        if (wantAdder && !Collect(accessors.Adder, EventAccessor.Adder, out perError))
        {
            if (perError is not null) return FindEventReferencesReadResult.Fail(perError);
            goto done;
        }
        if (wantRemover && !Collect(accessors.Remover, EventAccessor.Remover, out perError))
        {
            if (perError is not null) return FindEventReferencesReadResult.Fail(perError);
            goto done;
        }
        if (wantRaiser && !Collect(accessors.Raiser, EventAccessor.Raiser, out perError))
        {
            if (perError is not null) return FindEventReferencesReadResult.Fail(perError);
            goto done;
        }
    done:

        return FindEventReferencesReadResult.Ok(new FindEventReferencesResult(
            module.Mvid, eventMetadataToken,
            HandleFormat.FormatEvent(module.Mvid, eventMetadataToken),
            hits, modulesSearchedMax, fromCacheAll));
    }

    /// <inheritdoc />
    public NativeBodyResult GetNativeBodyRef(Guid moduleVersionId, int methodMetadataToken)
    {
        if (moduleVersionId == Guid.Empty)
            return NativeBodyResult.Fail(new AssemblyError(ErrorKinds.IdentityMalformed, "moduleVersionId is required."));
        if (!_modules.TryGetValue(moduleVersionId, out var module))
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

    private static MethodIdentity BuildMethodIdentity(Module module, MethodDefinitionHandle h) =>
        new(module.Mvid, MetadataTokens.GetToken(h));

    private static bool ModeMatches(FieldAccessMode mode, FieldAccessKind kind) => mode switch
    {
        FieldAccessMode.All => true,
        FieldAccessMode.Read => kind == FieldAccessKind.Read || kind == FieldAccessKind.Address,
        FieldAccessMode.Write => kind == FieldAccessKind.Write,
        _ => true,
    };

    private static FieldKey? BuildFieldKey(Module module, FieldDefinition fieldDef)
    {
        try
        {
            var declaringTd = module.MD.GetTypeDefinition(fieldDef.GetDeclaringType());
            var typeFullName = TypeName(module, declaringTd);
            var fieldName = module.MD.GetString(fieldDef.Name);
            var assemblyName = module.MD.IsAssembly
                ? module.MD.GetString(module.MD.GetAssemblyDefinition().Name)
                : null;
            if (assemblyName is null) return null;
            return new FieldKey(assemblyName, typeFullName, fieldName);
        }
        catch (BadImageFormatException) { return null; }
    }

    private static FieldAccessIndexData BuildFieldAccessIndex(Module module, CancellationToken cancellationToken)
    {
        var intra = new Dictionary<int, List<(int, int, FieldAccessKind)>>();
        var outbound = new List<FieldOutboundRef>();

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

            var callerToken = MetadataTokens.GetToken(methodHandle);
            var span = ilBytes.AsSpan();
            int pos = 0;
            while (pos < span.Length)
            {
                int opStart = pos;
                var b1 = span[pos++];
                IlOpcodeTable.Op op;
                bool isFieldOpcode = false;
                FieldAccessKind kind = FieldAccessKind.Read;
                if (b1 == 0xFE)
                {
                    if (pos >= span.Length) break;
                    op = IlOpcodeTable.TwoByteOp(span[pos++]);
                }
                else
                {
                    op = IlOpcodeTable.OneByteOp(b1);
                    switch (b1)
                    {
                        case 0x7B: kind = FieldAccessKind.Read; isFieldOpcode = true; break;     // ldfld
                        case 0x7C: kind = FieldAccessKind.Address; isFieldOpcode = true; break;  // ldflda
                        case 0x7D: kind = FieldAccessKind.Write; isFieldOpcode = true; break;    // stfld
                        case 0x7E: kind = FieldAccessKind.Read; isFieldOpcode = true; break;     // ldsfld
                        case 0x7F: kind = FieldAccessKind.Address; isFieldOpcode = true; break;  // ldsflda
                        case 0x80: kind = FieldAccessKind.Write; isFieldOpcode = true; break;    // stsfld
                    }
                }

                var size = IlOpcodeTable.OperandSize(op);
                if (size == -1)
                {
                    if (pos + 4 > span.Length) break;
                    var n = BitConverter.ToInt32(span.Slice(pos, 4));
                    if (n < 0 || n > (span.Length - pos - 4) / 4) break;
                    pos += 4 + n * 4;
                    continue;
                }

                if (isFieldOpcode && size == 4 && pos + 4 <= span.Length)
                {
                    var token = BitConverter.ToInt32(span.Slice(pos, 4));
                    ClassifyFieldToken(module, token, callerToken, opStart, kind, intra, outbound);
                }

                pos += Math.Max(0, size);
            }
        }

        return new FieldAccessIndexData(intra, outbound);
    }

    private static void ClassifyFieldToken(Module module, int token, int callerToken, int ilOffset,
        FieldAccessKind kind,
        Dictionary<int, List<(int, int, FieldAccessKind)>> intra,
        List<FieldOutboundRef> outbound)
    {
        EntityHandle h;
        try { h = (EntityHandle)MetadataTokens.Handle(token); }
        catch (ArgumentOutOfRangeException) { return; }
        catch (BadImageFormatException) { return; }

        switch (h.Kind)
        {
            case HandleKind.FieldDefinition:
                AddFieldIntra(intra, token, callerToken, ilOffset, kind);
                break;
            case HandleKind.MemberReference:
                MemberReference mr;
                try { mr = module.MD.GetMemberReference((MemberReferenceHandle)h); }
                catch (BadImageFormatException) { return; }
                if (mr.GetKind() != MemberReferenceKind.Field) return;

                var localType = ResolveLocalParentType(module, mr.Parent);
                if (localType is { } typeDefHandle)
                {
                    var local = TryFindLocalField(module, typeDefHandle, mr);
                    if (local is { } fieldToken)
                    {
                        AddFieldIntra(intra, fieldToken, callerToken, ilOffset, kind);
                        return;
                    }
                }

                try
                {
                    var typeName = ResolveOutboundTypeName(module, mr.Parent, out var asmName);
                    if (typeName is null || asmName is null) return;
                    var fieldName = module.MD.GetString(mr.Name);
                    outbound.Add(new FieldOutboundRef(
                        callerToken, ilOffset, kind, asmName, typeName, fieldName));
                }
                catch (BadImageFormatException) { return; }
                break;
        }
    }

    private static void AddFieldIntra(Dictionary<int, List<(int, int, FieldAccessKind)>> intra,
        int fieldToken, int callerToken, int ilOffset, FieldAccessKind kind)
    {
        if (!intra.TryGetValue(fieldToken, out var list))
        {
            list = new List<(int, int, FieldAccessKind)>(1);
            intra[fieldToken] = list;
        }
        list.Add((callerToken, ilOffset, kind));
    }

    private static int? TryFindLocalField(Module module, TypeDefinitionHandle parentType, MemberReference mr)
    {
        var fieldName = module.MD.GetString(mr.Name);
        var td = module.MD.GetTypeDefinition(parentType);
        foreach (var fh in td.GetFields())
        {
            var fd = module.MD.GetFieldDefinition(fh);
            if (module.MD.StringComparer.Equals(fd.Name, fieldName))
                return MetadataTokens.GetToken(fh);
        }
        return null;
    }

    private static (string Handle, string Display) RenderAttributeTarget(
        Module module, AttributeTargetKind kind, int targetToken, int paramSeq)
    {
        try
        {
            switch (kind)
            {
                case AttributeTargetKind.Assembly:
                {
                    var name = module.MD.IsAssembly
                        ? module.MD.GetString(module.MD.GetAssemblyDefinition().Name)
                        : "<module>";
                    return (HandleFormat.FormatAssembly(module.Mvid), name);
                }
                case AttributeTargetKind.Type:
                {
                    var h = (TypeDefinitionHandle)MetadataTokens.Handle(targetToken);
                    return (HandleFormat.FormatType(module.Mvid, targetToken),
                            TypeName(module, module.MD.GetTypeDefinition(h)));
                }
                case AttributeTargetKind.Method:
                {
                    var h = (MethodDefinitionHandle)MetadataTokens.Handle(targetToken);
                    return (HandleFormat.Format(module.Mvid, targetToken),
                            RenderMethodDef(module, h));
                }
                case AttributeTargetKind.Parameter:
                {
                    var h = (MethodDefinitionHandle)MetadataTokens.Handle(targetToken);
                    var methodDisplay = RenderMethodDef(module, h);
                    return (HandleFormat.FormatParameter(module.Mvid, targetToken, paramSeq),
                            $"{methodDisplay}#param={paramSeq}");
                }
                case AttributeTargetKind.Field:
                {
                    var h = (FieldDefinitionHandle)MetadataTokens.Handle(targetToken);
                    return (HandleFormat.FormatField(module.Mvid, targetToken),
                            RenderFieldDef(module, h));
                }
                case AttributeTargetKind.Property:
                {
                    var h = (PropertyDefinitionHandle)MetadataTokens.Handle(targetToken);
                    return (HandleFormat.FormatProperty(module.Mvid, targetToken),
                            RenderPropertyDef(module, h));
                }
                case AttributeTargetKind.Event:
                {
                    var h = (EventDefinitionHandle)MetadataTokens.Handle(targetToken);
                    return (HandleFormat.FormatEvent(module.Mvid, targetToken),
                            RenderEventDef(module, h));
                }
            }
        }
        catch (BadImageFormatException) { /* fall through to placeholder */ }
        return ($"<{kind} 0x{targetToken:X8}>", $"<{kind} 0x{targetToken:X8}>");
    }

    private static AttributeIndexData BuildAttributeIndex(Module module, CancellationToken cancellationToken)
    {
        var dict = new Dictionary<string, List<(AttributeTargetKind Kind, int TargetToken, int ParameterSequence, int AttributeToken)>>(StringComparer.Ordinal);
        var md = module.MD;

        void Add(string name, AttributeTargetKind kind, int targetToken, int paramSeq, int attrToken)
        {
            if (!dict.TryGetValue(name, out var list))
            {
                list = new List<(AttributeTargetKind, int, int, int)>(1);
                dict[name] = list;
            }
            list.Add((kind, targetToken, paramSeq, attrToken));
        }

        void Process(CustomAttributeHandleCollection handles, AttributeTargetKind kind, int targetToken, int paramSeq)
        {
            foreach (var ah in handles)
            {
                string? typeName;
                try { typeName = TryReadAttributeTypeFullName(module, ah); }
                catch (BadImageFormatException) { continue; }
                if (typeName is null) continue;
                Add(typeName, kind, targetToken, paramSeq, MetadataTokens.GetToken(ah));
            }
        }

        if (md.IsAssembly)
        {
            try
            {
                Process(md.GetAssemblyDefinition().GetCustomAttributes(), AttributeTargetKind.Assembly, 0, 0);
            }
            catch (BadImageFormatException) { /* skip */ }
        }

        foreach (var th in md.TypeDefinitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TypeDefinition td;
            try { td = md.GetTypeDefinition(th); }
            catch (BadImageFormatException) { continue; }
            var typeToken = MetadataTokens.GetToken(th);

            try { Process(td.GetCustomAttributes(), AttributeTargetKind.Type, typeToken, 0); }
            catch (BadImageFormatException) { /* skip */ }

            foreach (var fh in td.GetFields())
            {
                try { Process(md.GetFieldDefinition(fh).GetCustomAttributes(), AttributeTargetKind.Field, MetadataTokens.GetToken(fh), 0); }
                catch (BadImageFormatException) { }
            }
            foreach (var ph in td.GetProperties())
            {
                try { Process(md.GetPropertyDefinition(ph).GetCustomAttributes(), AttributeTargetKind.Property, MetadataTokens.GetToken(ph), 0); }
                catch (BadImageFormatException) { }
            }
            foreach (var eh in td.GetEvents())
            {
                try { Process(md.GetEventDefinition(eh).GetCustomAttributes(), AttributeTargetKind.Event, MetadataTokens.GetToken(eh), 0); }
                catch (BadImageFormatException) { }
            }
            foreach (var mh in td.GetMethods())
            {
                MethodDefinition methodDef;
                try { methodDef = md.GetMethodDefinition(mh); }
                catch (BadImageFormatException) { continue; }
                var methodToken = MetadataTokens.GetToken(mh);
                try { Process(methodDef.GetCustomAttributes(), AttributeTargetKind.Method, methodToken, 0); }
                catch (BadImageFormatException) { }
                foreach (var paramH in methodDef.GetParameters())
                {
                    Parameter param;
                    try { param = md.GetParameter(paramH); }
                    catch (BadImageFormatException) { continue; }
                    try { Process(param.GetCustomAttributes(), AttributeTargetKind.Parameter, methodToken, param.SequenceNumber); }
                    catch (BadImageFormatException) { }
                }
            }
        }

        return new AttributeIndexData(dict);
    }

    private static string? TryReadAttributeTypeFullName(Module module, CustomAttributeHandle handle)
    {
        var ca = module.MD.GetCustomAttribute(handle);
        switch (ca.Constructor.Kind)
        {
            case HandleKind.MemberReference:
            {
                var mr = module.MD.GetMemberReference((MemberReferenceHandle)ca.Constructor);
                return ResolveOutboundTypeName(module, mr.Parent, out _);
            }
            case HandleKind.MethodDefinition:
            {
                var md = module.MD.GetMethodDefinition((MethodDefinitionHandle)ca.Constructor);
                var td = module.MD.GetTypeDefinition(md.GetDeclaringType());
                return TypeName(module, td);
            }
            default:
                return null;
        }
    }

    /// <inheritdoc />
    public FindTypeReferencesReadResult FindTypeReferences(Guid moduleVersionId, int typeMetadataToken, CancellationToken cancellationToken = default)
    {
        if (moduleVersionId == Guid.Empty)
            return FindTypeReferencesReadResult.Fail(new AssemblyError(ErrorKinds.IdentityMalformed, "moduleVersionId is required."));
        if (!_modules.TryGetValue(moduleVersionId, out var module))
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
        var xref = _xrefCache.GetOrAdd(module.Mvid, _ =>
        {
            fromCache = false;
            return LoadOrBuildXref(module, cancellationToken);
        });

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
            foreach (var other in _modules.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (other.Mvid == module.Mvid) continue;
                modulesSearched++;

                var otherXref = _xrefCache.GetOrAdd(other.Mvid, _ => LoadOrBuildXref(other, cancellationToken));
                foreach (var entry in otherXref.TypeOutbound)
                {
                    if (!string.Equals(entry.TargetAssemblyName, targetAssemblyName, StringComparison.Ordinal)) continue;
                    if (!string.Equals(entry.TargetTypeFullName, targetFullName, StringComparison.Ordinal)) continue;
                    references.Add(RenderTypeReferenceSite(other,
                        new TypeReferenceSite(entry.SiteToken, entry.SiteKind, entry.ReferenceKind)));
                }
            }
        }

        var targetHandleStr = HandleFormat.FormatType(module.Mvid, typeMetadataToken);
        return FindTypeReferencesReadResult.Ok(new FindTypeReferencesResult(
            module.Mvid, typeMetadataToken, targetHandleStr,
            references, modulesSearched, FromCache: fromCache));
    }

    private static TypeReferenceRef RenderTypeReferenceSite(Module module, TypeReferenceSite site)
    {
        string handle;
        string display;
        switch (site.SiteKind)
        {
            case MemberKind.Method:
            {
                var mh = (MethodDefinitionHandle)MetadataTokens.Handle(site.SiteToken);
                handle = HandleFormat.Format(module.Mvid, site.SiteToken);
                display = RenderMethodDef(module, mh);
                break;
            }
            case MemberKind.Field:
            {
                handle = $"f:{module.Mvid:D}:0x{site.SiteToken:X8}";
                display = RenderFieldDef(module, (FieldDefinitionHandle)MetadataTokens.Handle(site.SiteToken));
                break;
            }
            case MemberKind.Property:
            {
                handle = $"p:{module.Mvid:D}:0x{site.SiteToken:X8}";
                display = RenderPropertyDef(module, (PropertyDefinitionHandle)MetadataTokens.Handle(site.SiteToken));
                break;
            }
            case MemberKind.Event:
            {
                handle = $"e:{module.Mvid:D}:0x{site.SiteToken:X8}";
                display = RenderEventDef(module, (EventDefinitionHandle)MetadataTokens.Handle(site.SiteToken));
                break;
            }
            case MemberKind.Type:
            {
                handle = HandleFormat.FormatType(module.Mvid, site.SiteToken);
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

    private static string RenderPropertyDef(Module module, PropertyDefinitionHandle h)
    {
        try
        {
            var p = module.MD.GetPropertyDefinition(h);
            // Properties have no declaring-type back-edge; walk every TypeDef once would be O(n).
            // For Tier-4 display, just return the property name — the caller can drill in via
            // list_members / get_method on its accessors.
            return module.MD.GetString(p.Name);
        }
        catch (BadImageFormatException) { return $"<property 0x{MetadataTokens.GetToken(h):X8}>"; }
    }

    private static string RenderEventDef(Module module, EventDefinitionHandle h)
    {
        try
        {
            var e = module.MD.GetEventDefinition(h);
            return module.MD.GetString(e.Name);
        }
        catch (BadImageFormatException) { return $"<event 0x{MetadataTokens.GetToken(h):X8}>"; }
    }

    /// <summary>
    /// Walks the caller's IL looking for any call site whose closed instantiation matches the
    /// requested <paramref name="expectedTypeArgs"/> and/or <paramref name="expectedMethodArgs"/>.
    /// Matches three shapes: (a) <c>MethodSpec</c> rows (method-level generics, optionally on a
    /// TypeSpec parent for type-level generics); (b) <c>MemberRef</c> rows with a TypeSpec
    /// parent (closed type-level instantiation, non-generic method); (c) intra-module
    /// <c>MethodDef</c> tokens — these never carry instantiation info, so they're skipped when
    /// either expected arg list is non-empty.
    /// </summary>
    private static bool CallerHasMatchingInstantiation(
        Module callerModule, int callerToken,
        Module calleeModule, MethodDefinitionHandle calleeHandle,
        CalleeKey calleeKey,
        IReadOnlyList<string>? expectedTypeArgs,
        IReadOnlyList<string>? expectedMethodArgs)
    {
        MethodDefinitionHandle callerHandle;
        try { callerHandle = (MethodDefinitionHandle)MetadataTokens.Handle(callerToken); }
        catch (ArgumentOutOfRangeException) { return false; }
        catch (InvalidCastException) { return false; }

        MethodDefinition callerDef;
        try { callerDef = callerModule.MD.GetMethodDefinition(callerHandle); }
        catch (BadImageFormatException) { return false; }
        if (callerDef.RelativeVirtualAddress == 0) return false;

        byte[] ilBytes;
        try
        {
            var body = callerModule.PE.GetMethodBody(callerDef.RelativeVirtualAddress);
            ilBytes = body.GetILBytes() ?? Array.Empty<byte>();
        }
        catch (BadImageFormatException) { return false; }

        var calleeIsSameModule = callerModule.Mvid == calleeModule.Mvid;
        var calleeIntraToken = MetadataTokens.GetToken(calleeHandle);
        bool wantMethodArgs = expectedMethodArgs is { Count: > 0 };
        bool wantTypeArgs = expectedTypeArgs is { Count: > 0 };

        var span = ilBytes.AsSpan();
        int pos = 0;
        var provider = new WireFormatSignatureProvider();
        while (pos < span.Length)
        {
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
            if (size == -1)
            {
                if (pos + 4 > span.Length) break;
                var n = BitConverter.ToInt32(span.Slice(pos, 4));
                if (n < 0 || n > (span.Length - pos - 4) / 4) break;
                pos += 4 + n * 4;
                continue;
            }
            if (size == 4 && pos + 4 <= span.Length && op == IlOpcodeTable.Op.InlineMethod)
            {
                var token = BitConverter.ToInt32(span.Slice(pos, 4));
                if (TryMatchInstantiatedCall(
                        callerModule, token, calleeIsSameModule, calleeIntraToken, calleeKey,
                        expectedTypeArgs, expectedMethodArgs, wantTypeArgs, wantMethodArgs,
                        provider))
                {
                    return true;
                }
            }
            pos += Math.Max(0, size);
        }
        return false;
    }

    private static bool TryMatchInstantiatedCall(
        Module callerModule, int token,
        bool calleeIsSameModule, int calleeIntraToken, CalleeKey calleeKey,
        IReadOnlyList<string>? expectedTypeArgs,
        IReadOnlyList<string>? expectedMethodArgs,
        bool wantTypeArgs, bool wantMethodArgs,
        WireFormatSignatureProvider provider)
    {
        EntityHandle h;
        try { h = (EntityHandle)MetadataTokens.Handle(token); }
        catch (ArgumentException) { return false; }

        switch (h.Kind)
        {
            case HandleKind.MethodSpecification:
                return TryMatchMethodSpecCall(
                    callerModule, (MethodSpecificationHandle)h,
                    calleeIsSameModule, calleeIntraToken, calleeKey,
                    expectedTypeArgs, expectedMethodArgs, wantTypeArgs, wantMethodArgs, provider);

            case HandleKind.MemberReference:
                // MemberRef-only path: matches when the caller wants only type-level generics
                // (no method-level instantiation) and the call site uses a TypeSpec parent.
                if (wantMethodArgs) return false;
                if (!wantTypeArgs) return false;
                return TryMatchMemberRefCall(
                    callerModule, (MemberReferenceHandle)h,
                    calleeKey, expectedTypeArgs!, provider);

            case HandleKind.MethodDefinition:
                // Intra-module MethodDef tokens carry no instantiation; skipped when either
                // expected arg list is non-empty.
                return false;

            default:
                return false;
        }
    }

    private static bool TryMatchMethodSpecCall(
        Module callerModule, MethodSpecificationHandle handle,
        bool calleeIsSameModule, int calleeIntraToken, CalleeKey calleeKey,
        IReadOnlyList<string>? expectedTypeArgs,
        IReadOnlyList<string>? expectedMethodArgs,
        bool wantTypeArgs, bool wantMethodArgs,
        WireFormatSignatureProvider provider)
    {
        MethodSpecification spec;
        try { spec = callerModule.MD.GetMethodSpecification(handle); }
        catch (BadImageFormatException) { return false; }

        // Does spec.Method resolve to the callee?
        switch (spec.Method.Kind)
        {
            case HandleKind.MethodDefinition:
                if (!calleeIsSameModule) return false;
                if (MetadataTokens.GetToken(spec.Method) != calleeIntraToken) return false;
                if (wantTypeArgs) return false; // MethodDef parent has no closed type args
                break;
            case HandleKind.MemberReference:
                MemberReference mr;
                try { mr = callerModule.MD.GetMemberReference((MemberReferenceHandle)spec.Method); }
                catch (BadImageFormatException) { return false; }
                if (mr.GetKind() != MemberReferenceKind.Method) return false;
                if (!MemberRefMatchesCalleeKey(callerModule, mr, calleeKey)) return false;
                if (wantTypeArgs)
                {
                    if (mr.Parent.Kind != HandleKind.TypeSpecification) return false;
                    if (!TypeSpecMatchesTypeArgs(
                            callerModule, (TypeSpecificationHandle)mr.Parent,
                            expectedTypeArgs!, provider))
                        return false;
                }
                break;
            default:
                return false;
        }

        if (wantMethodArgs)
        {
            ImmutableArray<string> decoded;
            try { decoded = spec.DecodeSignature(provider, genericContext: (object?)null); }
            catch (BadImageFormatException) { return false; }
            if (decoded.Length != expectedMethodArgs!.Count) return false;
            for (int i = 0; i < decoded.Length; i++)
                if (!string.Equals(decoded[i], expectedMethodArgs[i], StringComparison.Ordinal))
                    return false;
        }

        return true;
    }

    private static bool TryMatchMemberRefCall(
        Module callerModule, MemberReferenceHandle handle,
        CalleeKey calleeKey,
        IReadOnlyList<string> expectedTypeArgs,
        WireFormatSignatureProvider provider)
    {
        MemberReference mr;
        try { mr = callerModule.MD.GetMemberReference(handle); }
        catch (BadImageFormatException) { return false; }
        if (mr.GetKind() != MemberReferenceKind.Method) return false;
        if (!MemberRefMatchesCalleeKey(callerModule, mr, calleeKey)) return false;
        if (mr.Parent.Kind != HandleKind.TypeSpecification) return false;
        return TypeSpecMatchesTypeArgs(
            callerModule, (TypeSpecificationHandle)mr.Parent, expectedTypeArgs, provider);
    }

    private static bool TypeSpecMatchesTypeArgs(
        Module callerModule, TypeSpecificationHandle handle,
        IReadOnlyList<string> expectedTypeArgs,
        WireFormatSignatureProvider provider)
    {
        try
        {
            var ts = callerModule.MD.GetTypeSpecification(handle);
            var decoded = ts.DecodeSignature(provider, genericContext: (object?)null);
            if (!GenericTypeName.TryParse(decoded, out var node, out _, out _)) return false;
            if (node is not GenericTypeName.Named named) return false;
            if (named.TypeArguments.IsDefaultOrEmpty) return false;
            if (named.TypeArguments.Length != expectedTypeArgs.Count) return false;
            for (int i = 0; i < expectedTypeArgs.Count; i++)
            {
                var formatted = named.TypeArguments[i].Format();
                if (!string.Equals(formatted, expectedTypeArgs[i], StringComparison.Ordinal))
                    return false;
            }
            return true;
        }
        catch (BadImageFormatException) { return false; }
    }

    private static bool MemberRefMatchesCalleeKey(Module callerModule, MemberReference mr, CalleeKey key)
    {
        try
        {
            var typeName = ResolveOutboundTypeName(callerModule, mr.Parent, out var assemblyName);
            if (typeName is null || assemblyName is null) return false;
            if (!string.Equals(assemblyName, key.AssemblyName, StringComparison.Ordinal)) return false;
            if (!string.Equals(typeName, key.TypeFullName, StringComparison.Ordinal)) return false;
            var methodName = callerModule.MD.GetString(mr.Name);
            if (!string.Equals(methodName, key.MethodName, StringComparison.Ordinal)) return false;
            var decoder = new SignatureDecoder<string, object?>(
                new StringSignatureProvider(callerModule.MD), callerModule.MD, genericContext: null);
            var blob = callerModule.MD.GetBlobReader(mr.Signature);
            var sig = decoder.DecodeMethodSignature(ref blob);
            if (sig.Header.RawValue != key.CallingConvention) return false;
            if (sig.RequiredParameterCount != key.ParameterCount) return false;
            if (sig.GenericParameterCount != key.GenericArity) return false;
            var paramSig = sig.RequiredParameterCount == sig.ParameterTypes.Length
                ? string.Join(",", sig.ParameterTypes)
                : string.Join(",", sig.ParameterTypes.Take(sig.RequiredParameterCount));
            return string.Equals(paramSig, key.ParameterSignature, StringComparison.Ordinal);
        }
        catch (BadImageFormatException) { return false; }
    }

    private void InvalidateXref(Guid mvid)
    {
        _xrefCache.TryRemove(mvid, out _);
        _stringIndexCache.TryRemove(mvid, out _);
        _attributeIndexCache.TryRemove(mvid, out _);
        _fieldAccessCache.TryRemove(mvid, out _);
        _r2rCache.TryRemove(mvid, out _);
        try
        {
            var path = XrefCachePath(mvid);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { /* best-effort */ }
        catch (UnauthorizedAccessException) { /* best-effort */ }
        if (_sourceCache.TryRemove(mvid, out var pdb))
            pdb?.Provider?.Dispose();
    }

    // ---- Source-location (PDB / SourceLink) -----------------------------------------------
    // Cache one open MetadataReaderProvider per module so repeated get_method_source calls
    // don't re-open the PDB. Disposed alongside the index.
    private readonly ConcurrentDictionary<Guid, PdbHandle?> _sourceCache = new();
    private static readonly Guid SourceLinkCdiKind =
        new("CC110556-A091-4D38-9FEC-25AB9A351A6A");

    private sealed record PdbHandle(MetadataReaderProvider? Provider, MetadataReader? Reader, PdbKind Kind, int Age);

    /// <inheritdoc />
    public MethodSourceResult GetMethodSource(MethodIdentity identity)
    {
        var common = TryResolveMethod(identity);
        if (common.Error is not null) return MethodSourceResult.Fail(common.Error);
        var module = common.Module!;
        var methodHandle = common.Handle;
        var handleStr = HandleFormat.Format(module.Mvid, identity.MetadataToken);

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

    private static PdbHandle? TryOpenPdb(Module module)
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
        // ModuleDefinition handle (token 0x00000001 from the Module table).
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

    private string XrefCachePath(Guid mvid) => Path.Combine(_xrefCacheDir, $"{mvid:N}.xref");

    private XrefData LoadOrBuildXref(Module module, CancellationToken cancellationToken = default)
    {
        var cachePath = XrefCachePath(module.Mvid);
        if (TryReadXrefCache(cachePath, module, out var cached))
            return cached;

        var built = BuildXref(module, cancellationToken);
        TryWriteXrefCache(cachePath, module, built);
        return built;
    }

    private static XrefData BuildXref(Module module, CancellationToken cancellationToken = default)
    {
        var data = new XrefData(
            new Dictionary<int, List<int>>(),
            new List<OutboundCallRef>(),
            new Dictionary<int, List<TypeReferenceSite>>(),
            new List<OutboundTypeRef>());
        // Per-method dedup sets reset between methods: a single method may emit the same call
        // multiple times non-consecutively (e.g. call Foo; call Bar; call Foo), and we want each
        // pair (caller, target) recorded only once on either side.
        var intraSeen = new HashSet<long>();
        var outboundSeen = new HashSet<OutboundCallRef>();
        // Per-method (typeToken, refKind) dedup for type-xref so the same type isn't recorded
        // twice for the same site through different IL opcodes / signature positions.
        var typeIntraSeen = new HashSet<long>();
        var typeOutboundSeen = new HashSet<OutboundTypeRef>();
        var typeCollector = new TypeTokenCollectorProvider(module.MD);
        var i = 0;
        foreach (var methodHandle in module.MD.MethodDefinitions)
        {
            if ((++i & 0xFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            var def = module.MD.GetMethodDefinition(methodHandle);

            var callerToken = MetadataTokens.GetToken(methodHandle);
            typeIntraSeen.Clear();

            // 1) Method signature: parameters + return type.
            try
            {
                typeCollector.Reset();
                def.DecodeSignature(typeCollector, genericContext: null);
                EmitCollectedTypes(module, typeCollector, callerToken, MemberKind.Method,
                    TypeReferenceKind.MethodParameter, data, typeIntraSeen, typeOutboundSeen);
            }
            catch (BadImageFormatException) { /* skip malformed signature */ }

            if (def.RelativeVirtualAddress == 0)
            {
                continue;
            }

            byte[] ilBytes;
            MethodBodyBlock body;
            try
            {
                body = module.PE.GetMethodBody(def.RelativeVirtualAddress);
                ilBytes = body.GetILBytes() ?? Array.Empty<byte>();
            }
            catch (BadImageFormatException) { continue; }

            intraSeen.Clear();
            outboundSeen.Clear();
            ScanCallsFromIl(module, ilBytes, callerToken, data, intraSeen, outboundSeen);

            // 2) Local variables (StandAloneSig).
            if (!body.LocalSignature.IsNil)
            {
                try
                {
                    var localSig = module.MD.GetStandaloneSignature(body.LocalSignature);
                    typeCollector.Reset();
                    localSig.DecodeLocalSignature(typeCollector, genericContext: null);
                    EmitCollectedTypes(module, typeCollector, callerToken, MemberKind.Method,
                        TypeReferenceKind.MethodLocal, data, typeIntraSeen, typeOutboundSeen);
                }
                catch (BadImageFormatException) { /* skip malformed local sig */ }
            }

            // 3) Type-bearing IL opcodes.
            ScanTypesFromIl(module, ilBytes, callerToken, data, typeIntraSeen, typeOutboundSeen);
        }

        // 4) Field / Property / Event declarations: record their declared type.
        foreach (var fh in module.MD.FieldDefinitions)
        {
            try
            {
                var fd = module.MD.GetFieldDefinition(fh);
                typeCollector.Reset();
                fd.DecodeSignature(typeCollector, genericContext: null);
                var siteToken = MetadataTokens.GetToken(fh);
                EmitCollectedTypes(module, typeCollector, siteToken, MemberKind.Field,
                    TypeReferenceKind.FieldType, data, perSiteSeen: null, typeOutboundSeen);
            }
            catch (BadImageFormatException) { /* skip */ }
        }
        foreach (var ph in module.MD.PropertyDefinitions)
        {
            try
            {
                var pd = module.MD.GetPropertyDefinition(ph);
                typeCollector.Reset();
                pd.DecodeSignature(typeCollector, genericContext: null);
                var siteToken = MetadataTokens.GetToken(ph);
                EmitCollectedTypes(module, typeCollector, siteToken, MemberKind.Property,
                    TypeReferenceKind.PropertyType, data, perSiteSeen: null, typeOutboundSeen);
            }
            catch (BadImageFormatException) { /* skip */ }
        }
        foreach (var eh in module.MD.EventDefinitions)
        {
            try
            {
                var ed = module.MD.GetEventDefinition(eh);
                var siteToken = MetadataTokens.GetToken(eh);
                ClassifyTypeReferenceHandle(module, ed.Type, siteToken, MemberKind.Event,
                    TypeReferenceKind.EventType, data, perSiteSeen: null, typeOutboundSeen);
            }
            catch (BadImageFormatException) { /* skip */ }
        }

        // 5) Type hierarchy: BaseType + InterfaceImplementation per TypeDef. These edges live
        // on the TypeDef itself (no enclosing method/field/etc.) so the site is the TypeDef
        // token. Targets may be TypeDef, TypeRef, or TypeSpec — the helper handles all three.
        foreach (var tdh in module.MD.TypeDefinitions)
        {
            try
            {
                var td = module.MD.GetTypeDefinition(tdh);
                var siteToken = MetadataTokens.GetToken(tdh);

                if (!td.BaseType.IsNil)
                {
                    ClassifyTypeReferenceHandle(module, td.BaseType, siteToken, MemberKind.Type,
                        TypeReferenceKind.BaseType, data, perSiteSeen: null, typeOutboundSeen);
                }

                foreach (var iih in td.GetInterfaceImplementations())
                {
                    var ii = module.MD.GetInterfaceImplementation(iih);
                    if (ii.Interface.IsNil) continue;
                    ClassifyTypeReferenceHandle(module, ii.Interface, siteToken, MemberKind.Type,
                        TypeReferenceKind.InterfaceImplementation, data, perSiteSeen: null, typeOutboundSeen);
                }
            }
            catch (BadImageFormatException) { /* skip */ }
        }

        return data;
    }

    private static void EmitCollectedTypes(Module module, TypeTokenCollectorProvider collector,
        int siteToken, MemberKind siteKind, TypeReferenceKind refKind,
        XrefData data, HashSet<long>? perSiteSeen, HashSet<OutboundTypeRef> outboundSeen)
    {
        foreach (var handle in collector.Drain())
        {
            ClassifyTypeReferenceHandle(module, handle, siteToken, siteKind, refKind, data, perSiteSeen, outboundSeen);
        }
    }

    private static void ClassifyTypeReferenceHandle(Module module, EntityHandle handle,
        int siteToken, MemberKind siteKind, TypeReferenceKind refKind,
        XrefData data, HashSet<long>? perSiteSeen, HashSet<OutboundTypeRef> outboundSeen)
    {
        if (handle.IsNil) return;
        switch (handle.Kind)
        {
            case HandleKind.TypeDefinition:
            {
                var typeToken = MetadataTokens.GetToken(handle);
                AddTypeIntra(data.TypeIntra, typeToken,
                    new TypeReferenceSite(siteToken, siteKind, refKind), perSiteSeen);
                break;
            }
            case HandleKind.TypeReference:
            {
                try
                {
                    var tr = module.MD.GetTypeReference((TypeReferenceHandle)handle);
                    // Same-module TypeRef → record as intra if we can resolve to a local TypeDef.
                    if (tr.ResolutionScope.Kind == HandleKind.ModuleDefinition)
                    {
                        var local = FindTypeDefByName(module, tr);
                        if (local is { } tdh)
                        {
                            AddTypeIntra(data.TypeIntra, MetadataTokens.GetToken(tdh),
                                new TypeReferenceSite(siteToken, siteKind, refKind), perSiteSeen);
                            return;
                        }
                    }
                    // Cross-module: resolve to (assembly, typeFullName).
                    var typeName = ResolveOutboundTypeName(module, handle, out var assemblyName);
                    if (typeName is not null && assemblyName is not null)
                    {
                        var entry = new OutboundTypeRef(siteToken, siteKind, refKind, assemblyName, typeName);
                        if (outboundSeen.Add(entry))
                            data.TypeOutbound.Add(entry);
                    }
                }
                catch (BadImageFormatException) { /* skip */ }
                break;
            }
            case HandleKind.TypeSpecification:
            {
                // For sites that route raw entity handles (BaseType, InterfaceImplementation,
                // Event.Type), a TypeSpec wraps a generic instantiation. Decode it through a
                // collector and recursively classify each leaf (typically the open generic
                // TypeDef/TypeRef + the closed args). The IL path and signature-decoded paths
                // already feed the collector themselves before calling us, so by the time we
                // see a TypeSpec here we are the only decoder.
                try
                {
                    var spec = module.MD.GetTypeSpecification((TypeSpecificationHandle)handle);
                    var collector = new TypeTokenCollectorProvider(module.MD);
                    spec.DecodeSignature(collector, genericContext: null);
                    foreach (var leaf in collector.Drain())
                    {
                        if (leaf.Kind == HandleKind.TypeSpecification) continue; // guard against accidental recursion
                        ClassifyTypeReferenceHandle(module, leaf, siteToken, siteKind, refKind,
                            data, perSiteSeen, outboundSeen);
                    }
                }
                catch (BadImageFormatException) { /* skip malformed spec */ }
                break;
            }
        }
    }

    private static void AddTypeIntra(Dictionary<int, List<TypeReferenceSite>> intra,
        int typeToken, TypeReferenceSite site, HashSet<long>? perSiteSeen)
    {
        if (perSiteSeen is not null)
        {
            // Pack (typeToken, refKind) — site is constant within a single method/member.
            var key = ((long)typeToken << 32) | (uint)(int)site.ReferenceKind;
            if (!perSiteSeen.Add(key)) return;
        }
        if (!intra.TryGetValue(typeToken, out var list))
        {
            list = new List<TypeReferenceSite>();
            intra[typeToken] = list;
        }
        list.Add(site);
    }

    private static void ScanTypesFromIl(Module module, byte[] il, int methodToken,
        XrefData data, HashSet<long> intraSeen, HashSet<OutboundTypeRef> outboundSeen)
    {
        var span = il.AsSpan();
        int pos = 0;
        while (pos < span.Length)
        {
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
            if (size == -1)
            {
                if (pos + 4 > span.Length) break;
                var n = BitConverter.ToInt32(span.Slice(pos, 4));
                if (n < 0 || n > (span.Length - pos - 4) / 4) break;
                pos += 4 + n * 4;
                continue;
            }

            if (size == 4 && pos + 4 <= span.Length
                && (op == IlOpcodeTable.Op.InlineType || op == IlOpcodeTable.Op.InlineTok))
            {
                var token = BitConverter.ToInt32(span.Slice(pos, 4));
                ClassifyTypeBearingToken(module, token, methodToken, data, intraSeen, outboundSeen);
            }

            pos += Math.Max(0, size);
        }
    }

    private static void ClassifyTypeBearingToken(Module module, int token, int methodToken,
        XrefData data, HashSet<long> intraSeen, HashSet<OutboundTypeRef> outboundSeen)
    {
        EntityHandle h;
        try { h = (EntityHandle)MetadataTokens.Handle(token); }
        catch (ArgumentOutOfRangeException) { return; }
        catch (BadImageFormatException) { return; }

        // For InlineTok, accept only type-bearing handles (skip method / field tokens).
        if (h.Kind != HandleKind.TypeDefinition
            && h.Kind != HandleKind.TypeReference
            && h.Kind != HandleKind.TypeSpecification)
        {
            return;
        }

        if (h.Kind == HandleKind.TypeSpecification)
        {
            // Decode the TypeSpec signature to surface its underlying types (generic args + outer).
            try
            {
                var spec = module.MD.GetTypeSpecification((TypeSpecificationHandle)h);
                var collector = new TypeTokenCollectorProvider(module.MD);
                spec.DecodeSignature(collector, genericContext: null);
                EmitCollectedTypes(module, collector, methodToken, MemberKind.Method,
                    TypeReferenceKind.IlOpcode, data, intraSeen, outboundSeen);
            }
            catch (BadImageFormatException) { /* skip */ }
            return;
        }

        ClassifyTypeReferenceHandle(module, h, methodToken, MemberKind.Method,
            TypeReferenceKind.IlOpcode, data, intraSeen, outboundSeen);
    }

    /// <inheritdoc />

    private static void ScanCallsFromIl(Module module, byte[] il, int callerToken, XrefData data,
        HashSet<long> intraSeen, HashSet<OutboundCallRef> outboundSeen)
    {
        var span = il.AsSpan();
        int pos = 0;
        while (pos < span.Length)
        {
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
            if (size == -1)
            {
                if (pos + 4 > span.Length) break;
                var n = BitConverter.ToInt32(span.Slice(pos, 4));
                // Validate without overflow: n quads must fit in the remaining IL.
                if (n < 0 || n > (span.Length - pos - 4) / 4) break;
                pos += 4 + n * 4;
                continue;
            }

            // Only treat real call-edge opcodes as callers. InlineTok is `ldtoken`, which
            // takes a method handle but does not invoke it.
            if (size == 4 && pos + 4 <= span.Length && op == IlOpcodeTable.Op.InlineMethod)
            {
                var token = BitConverter.ToInt32(span.Slice(pos, 4));
                ClassifyCallToken(module, token, callerToken, data, intraSeen, outboundSeen);
            }

            pos += Math.Max(0, size);
        }
    }

    private static void ClassifyCallToken(Module module, int token, int callerToken, XrefData data,
        HashSet<long> intraSeen, HashSet<OutboundCallRef> outboundSeen)
    {
        EntityHandle h;
        try { h = (EntityHandle)MetadataTokens.Handle(token); }
        catch (ArgumentOutOfRangeException) { return; }
        catch (BadImageFormatException) { return; }

        switch (h.Kind)
        {
            case HandleKind.MethodDefinition:
                AddIntra(data.Intra, token, callerToken, intraSeen);
                break;
            case HandleKind.MemberReference:
                ClassifyMemberRef(module, (MemberReferenceHandle)h, callerToken, data,
                    intraSeen, outboundSeen);
                break;
            case HandleKind.MethodSpecification:
                try
                {
                    var spec = module.MD.GetMethodSpecification((MethodSpecificationHandle)h);
                    switch (spec.Method.Kind)
                    {
                        case HandleKind.MethodDefinition:
                            AddIntra(data.Intra,
                                MetadataTokens.GetToken(spec.Method), callerToken, intraSeen);
                            break;
                        case HandleKind.MemberReference:
                            ClassifyMemberRef(module, (MemberReferenceHandle)spec.Method,
                                callerToken, data, intraSeen, outboundSeen);
                            break;
                    }
                }
                catch (BadImageFormatException) { /* skip */ }
                break;
        }
    }

    private static void ClassifyMemberRef(Module module, MemberReferenceHandle mrh,
        int callerToken, XrefData data,
        HashSet<long> intraSeen, HashSet<OutboundCallRef> outboundSeen)
    {
        MemberReference mr;
        try { mr = module.MD.GetMemberReference(mrh); }
        catch (BadImageFormatException) { return; }
        if (mr.GetKind() != MemberReferenceKind.Method) return;

        // Try same-module first: many same-module call sites are emitted as MemberRef
        // (interface calls, generic-type instantiations, etc.). If we resolve to a local
        // MethodDef, record it as Intra. Otherwise fall back to the cross-module path.
        var localType = ResolveLocalParentType(module, mr.Parent);
        if (localType is { } typeDefHandle)
        {
            var local = TryFindLocalMethod(module, typeDefHandle, mr);
            if (local is { } methodToken)
            {
                AddIntra(data.Intra, methodToken, callerToken, intraSeen);
                return;
            }
        }

        TryAddOutbound(module, mr, callerToken, data.Outbound, outboundSeen);
    }

    private static void AddIntra(Dictionary<int, List<int>> intra, int calleeToken, int callerToken,
        HashSet<long> seen)
    {
        // Pack (callee, caller) into a 64-bit key for the per-method seen-set.
        var key = ((long)calleeToken << 32) | (uint)callerToken;
        if (!seen.Add(key)) return;
        if (!intra.TryGetValue(calleeToken, out var list))
        {
            list = new List<int>();
            intra[calleeToken] = list;
        }
        // The per-method seen-set guarantees no duplicate within one method, but the list may
        // already contain `callerToken` from a previous method (impossible — different callers)
        // or from an earlier scan; keep the legacy adjacent-dup guard as belt-and-braces.
        if (list.Count == 0 || list[^1] != callerToken)
            list.Add(callerToken);
    }

    private static void TryAddOutbound(Module module, MemberReference mr,
        int callerToken, List<OutboundCallRef> outbound, HashSet<OutboundCallRef> seen)
    {
        try
        {
            var typeName = ResolveOutboundTypeName(module, mr.Parent, out var assemblyName);
            if (typeName is null || assemblyName is null) return;

            var methodName = module.MD.GetString(mr.Name);
            var decoder = new SignatureDecoder<string, object?>(
                new StringSignatureProvider(module.MD), module.MD, genericContext: null);
            var blob = module.MD.GetBlobReader(mr.Signature);
            var sig = decoder.DecodeMethodSignature(ref blob);
            // For vararg call sites, RequiredParameterCount counts the fixed params (before the
            // sentinel). Using it instead of ParameterTypes.Length lets us match a vararg MemberRef
            // (which carries the extra args) against the vararg MethodDef (which doesn't).
            var paramCount = sig.RequiredParameterCount;
            var paramSig = paramCount == sig.ParameterTypes.Length
                ? string.Join(",", sig.ParameterTypes)
                : string.Join(",", sig.ParameterTypes.Take(paramCount));

            var entry = new OutboundCallRef(callerToken, assemblyName,
                typeName, methodName, paramCount,
                sig.GenericParameterCount, paramSig, sig.Header.RawValue);
            if (seen.Add(entry))
                outbound.Add(entry);
        }
        catch (BadImageFormatException) { /* skip */ }
    }

    /// <summary>
    /// Resolves a MemberRef Parent to a TypeDefinitionHandle in the current module, if any.
    /// Returns null when the parent points outside this assembly (handled as outbound) or
    /// cannot be resolved.
    /// </summary>
    private static TypeDefinitionHandle? ResolveLocalParentType(Module module, EntityHandle parent)
    {
        try
        {
            switch (parent.Kind)
            {
                case HandleKind.TypeDefinition:
                    return (TypeDefinitionHandle)parent;
                case HandleKind.TypeReference:
                    var tr = module.MD.GetTypeReference((TypeReferenceHandle)parent);
                    return tr.ResolutionScope.Kind == HandleKind.ModuleDefinition
                        ? FindTypeDefByName(module, tr)
                        : null;
                case HandleKind.TypeSpecification:
                    {
                        var ts = module.MD.GetTypeSpecification((TypeSpecificationHandle)parent);
                        var sigReader = module.MD.GetBlobReader(ts.Signature);
                        while (sigReader.RemainingBytes > 0)
                        {
                            var b = sigReader.ReadByte();
                            if (b == 0x12 /* CLASS */ || b == 0x11 /* VALUETYPE */)
                            {
                                var encoded = sigReader.ReadCompressedInteger();
                                if ((encoded & 0x3) == 0)
                                    return MetadataTokens.TypeDefinitionHandle(encoded >> 2);
                                return null;
                            }
                            // 0x15 GENERICINST is a 1-byte wrapper; the next byte is CLASS/VALUETYPE.
                            // Other prefixes (CMOD_OPT/REQD = 0x20/0x1F etc.) are not expected here
                            // but the loop tolerates them.
                        }
                        return null;
                    }
                default:
                    return null;
            }
        }
        catch (BadImageFormatException) { return null; }
    }

    private static TypeDefinitionHandle? FindTypeDefByName(Module module, TypeReference tr)
    {
        // Only handles the AssemblyReference/ModuleDefinition shape — nested TypeRef chains
        // for same-module references are vanishingly rare and skipped intentionally.
        var name = tr.Name;
        var ns = tr.Namespace;
        foreach (var tdh in module.MD.TypeDefinitions)
        {
            var td = module.MD.GetTypeDefinition(tdh);
            if (module.MD.StringComparer.Equals(td.Name, module.MD.GetString(name))
                && (ns.IsNil
                    ? td.Namespace.IsNil
                    : module.MD.StringComparer.Equals(td.Namespace, module.MD.GetString(ns))))
                return tdh;
        }
        return null;
    }

    private static int? TryFindLocalMethod(Module module, TypeDefinitionHandle parentType,
        MemberReference mr)
    {
        var methodName = module.MD.GetString(mr.Name);
        MethodSignature<string> mrSig;
        try
        {
            var decoder = new SignatureDecoder<string, object?>(
                new StringSignatureProvider(module.MD), module.MD, genericContext: null);
            var blob = module.MD.GetBlobReader(mr.Signature);
            mrSig = decoder.DecodeMethodSignature(ref blob);
        }
        catch (BadImageFormatException) { return null; }
        var mrParamSig = string.Join(",", mrSig.ParameterTypes);

        var td = module.MD.GetTypeDefinition(parentType);
        foreach (var mh in td.GetMethods())
        {
            var def = module.MD.GetMethodDefinition(mh);
            if (!module.MD.StringComparer.Equals(def.Name, methodName)) continue;
            if (def.GetGenericParameters().Count != mrSig.GenericParameterCount) continue;
            MethodSignature<string> defSig;
            try
            {
                var dec = new SignatureDecoder<string, object?>(
                    new StringSignatureProvider(module.MD), module.MD, genericContext: null);
                var defBlob = module.MD.GetBlobReader(def.Signature);
                defSig = dec.DecodeMethodSignature(ref defBlob);
            }
            catch (BadImageFormatException) { continue; }
            if (defSig.ParameterTypes.Length != mrSig.ParameterTypes.Length) continue;
            if (string.Join(",", defSig.ParameterTypes) != mrParamSig) continue;
            return MetadataTokens.GetToken(mh);
        }
        return null;
    }

    private static string? ResolveOutboundTypeName(Module module, EntityHandle parent,
        out string? assemblyName)
    {
        assemblyName = null;
        switch (parent.Kind)
        {
            case HandleKind.TypeReference:
                return ResolveTypeRefName(module, (TypeReferenceHandle)parent, out assemblyName);
            case HandleKind.TypeSpecification:
                // Generic instantiation (e.g. List<int>.Add): walk into the type-spec signature
                // to find the underlying TypeRef. Approximate: scan for the first TypeRef token.
                try
                {
                    var ts = module.MD.GetTypeSpecification((TypeSpecificationHandle)parent);
                    var sigReader = module.MD.GetBlobReader(ts.Signature);
                    while (sigReader.RemainingBytes > 0)
                    {
                        var b = sigReader.ReadByte();
                        if (b == 0x12 /* CLASS */ || b == 0x11 /* VALUETYPE */)
                        {
                            var encoded = sigReader.ReadCompressedInteger();
                            var handle = EntityHandle(encoded);
                            if (handle.Kind == HandleKind.TypeReference)
                                return ResolveTypeRefName(module,
                                    (TypeReferenceHandle)handle, out assemblyName);
                            return null;
                        }
                    }
                    return null;
                }
                catch (BadImageFormatException) { return null; }
            default:
                return null;
        }
    }

    private static EntityHandle EntityHandle(int codedToken)
    {
        // Decode TypeDefOrRef-or-Spec compressed coded index into an EntityHandle.
        var rowId = codedToken >> 2;
        return (codedToken & 0x3) switch
        {
            0 => MetadataTokens.TypeDefinitionHandle(rowId),
            1 => MetadataTokens.TypeReferenceHandle(rowId),
            2 => MetadataTokens.TypeSpecificationHandle(rowId),
            _ => default,
        };
    }

    private static string? ResolveTypeRefName(Module module, TypeReferenceHandle trh,
        out string? assemblyName)
    {
        assemblyName = null;
        try
        {
            var tr = module.MD.GetTypeReference(trh);
            var name = module.MD.GetString(tr.Name);
            var ns = tr.Namespace.IsNil ? string.Empty : module.MD.GetString(tr.Namespace);
            var fullName = ns.Length == 0 ? name : ns + "." + name;

            switch (tr.ResolutionScope.Kind)
            {
                case HandleKind.AssemblyReference:
                    var ar = module.MD.GetAssemblyReference(
                        (AssemblyReferenceHandle)tr.ResolutionScope);
                    assemblyName = module.MD.GetString(ar.Name);
                    return fullName;
                case HandleKind.TypeReference:
                    var outer = ResolveTypeRefName(module,
                        (TypeReferenceHandle)tr.ResolutionScope, out assemblyName);
                    return outer is null ? null : outer + "+" + name;
                case HandleKind.ModuleDefinition:
                    // Reference within the same module: not cross-module — skip.
                    return null;
                default:
                    return null;
            }
        }
        catch (BadImageFormatException) { return null; }
    }

    private static CalleeKey BuildCalleeKey(Module module, MethodDefinitionHandle handle)
    {
        var asmName = module.MD.GetString(module.MD.GetAssemblyDefinition().Name);
        var def = module.MD.GetMethodDefinition(handle);
        var methodName = module.MD.GetString(def.Name);
        var declaringType = def.GetDeclaringType();
        var typeFullName = TypeName(module, module.MD.GetTypeDefinition(declaringType));

        int paramCount = 0;
        int genericArity = def.GetGenericParameters().Count;
        var paramSig = string.Empty;
        byte callingConvention = 0;
        try
        {
            var decoder = new SignatureDecoder<string, object?>(
                new StringSignatureProvider(module.MD), module.MD, genericContext: null);
            var blob = module.MD.GetBlobReader(def.Signature);
            var sig = decoder.DecodeMethodSignature(ref blob);
            // Mirror TryAddOutbound: required count is what cross-module vararg call sites carry.
            paramCount = sig.RequiredParameterCount;
            paramSig = paramCount == sig.ParameterTypes.Length
                ? string.Join(",", sig.ParameterTypes)
                : string.Join(",", sig.ParameterTypes.Take(paramCount));
            callingConvention = sig.Header.RawValue;
        }
        catch (BadImageFormatException) { /* leave defaults */ }

        return new CalleeKey(asmName, typeFullName, methodName, paramCount, genericArity, paramSig, callingConvention);
    }

    private const uint XrefMagic = 0x52584D41; // 'AMXR'
    private const int XrefFormatVersion = 5; // v5 adds BaseType + InterfaceImplementation type-ref sites.

    private const int MaxIntraCount = 10_000_000;
    private const int MaxOutboundCount = 10_000_000;
    private const int MaxIntraCallersPerCallee = 1_000_000;

    private static bool TryReadXrefCache(string path, Module module, out XrefData data)
    {
        data = null!;
        if (!File.Exists(path)) return false;

        FileInfo info;
        try { info = new FileInfo(module.Path); }
        catch (IOException) { return false; }
        if (!info.Exists) return false;

        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            if (br.ReadUInt32() != XrefMagic) return false;
            if (br.ReadInt32() != XrefFormatVersion) return false;
            var mvidBytes = br.ReadBytes(16);
            if (mvidBytes.Length != 16 || new Guid(mvidBytes) != module.Mvid) return false;
            if (br.ReadInt64() != info.LastWriteTimeUtc.Ticks) return false;
            if (br.ReadInt64() != info.Length) return false;

            var intraCount = br.ReadInt32();
            if (intraCount < 0 || intraCount > MaxIntraCount) return false;
            var intra = new Dictionary<int, List<int>>(intraCount);
            for (int i = 0; i < intraCount; i++)
            {
                var callee = br.ReadInt32();
                var n = br.ReadInt32();
                if (n < 0 || n > MaxIntraCallersPerCallee) return false;
                var list = new List<int>(n);
                for (int j = 0; j < n; j++) list.Add(br.ReadInt32());
                intra[callee] = list;
            }

            var outboundCount = br.ReadInt32();
            if (outboundCount < 0 || outboundCount > MaxOutboundCount) return false;
            var outbound = new List<OutboundCallRef>(outboundCount);
            for (int i = 0; i < outboundCount; i++)
            {
                var caller = br.ReadInt32();
                var asm = br.ReadString();
                var type = br.ReadString();
                var method = br.ReadString();
                var pc = br.ReadInt32();
                var ga = br.ReadInt32();
                var psig = br.ReadString();
                var cc = br.ReadByte();
                outbound.Add(new OutboundCallRef(caller, asm, type, method, pc, ga, psig, cc));
            }

            data = new XrefData(intra, outbound, new Dictionary<int, List<TypeReferenceSite>>(), new List<OutboundTypeRef>());

            var typeIntraCount = br.ReadInt32();
            if (typeIntraCount < 0 || typeIntraCount > MaxIntraCount) return false;
            for (int i = 0; i < typeIntraCount; i++)
            {
                var target = br.ReadInt32();
                var n = br.ReadInt32();
                if (n < 0 || n > MaxIntraCallersPerCallee) return false;
                var list = new List<TypeReferenceSite>(n);
                for (int j = 0; j < n; j++)
                {
                    var siteToken = br.ReadInt32();
                    var siteKind = (MemberKind)br.ReadByte();
                    var refKind = (TypeReferenceKind)br.ReadByte();
                    list.Add(new TypeReferenceSite(siteToken, siteKind, refKind));
                }
                data.TypeIntra[target] = list;
            }

            var typeOutboundCount = br.ReadInt32();
            if (typeOutboundCount < 0 || typeOutboundCount > MaxOutboundCount) return false;
            for (int i = 0; i < typeOutboundCount; i++)
            {
                var siteToken = br.ReadInt32();
                var siteKind = (MemberKind)br.ReadByte();
                var refKind = (TypeReferenceKind)br.ReadByte();
                var asm = br.ReadString();
                var typeFn = br.ReadString();
                data.TypeOutbound.Add(new OutboundTypeRef(siteToken, siteKind, refKind, asm, typeFn));
            }

            return true;
        }
        // Treat any corruption shape as "cache invalid — rebuild". The expensive part of a
        // rebuild is bounded (single-module IL scan), so degrading gracefully here is cheap.
        catch (EndOfStreamException) { return false; }  // derives from IOException — list first
        catch (IOException) { return false; }
        catch (FormatException) { return false; }
        catch (ArgumentException) { return false; }
        catch (OutOfMemoryException) { return false; }
    }

    private void TryWriteXrefCache(string path, Module module, XrefData data)
    {
        try
        {
            Directory.CreateDirectory(_xrefCacheDir);
            FileInfo info;
            try { info = new FileInfo(module.Path); }
            catch (IOException) { return; }
            if (!info.Exists) return;

            var tmp = path + ".tmp";
            using (var fs = File.Create(tmp))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(XrefMagic);
                bw.Write(XrefFormatVersion);
                bw.Write(module.Mvid.ToByteArray());
                bw.Write(info.LastWriteTimeUtc.Ticks);
                bw.Write(info.Length);
                bw.Write(data.Intra.Count);
                foreach (var (callee, callers) in data.Intra)
                {
                    bw.Write(callee);
                    bw.Write(callers.Count);
                    foreach (var c in callers) bw.Write(c);
                }
                bw.Write(data.Outbound.Count);
                foreach (var o in data.Outbound)
                {
                    bw.Write(o.CallerToken);
                    bw.Write(o.TargetAssemblyName);
                    bw.Write(o.TargetTypeFullName);
                    bw.Write(o.TargetMethodName);
                    bw.Write(o.ParameterCount);
                    bw.Write(o.GenericArity);
                    bw.Write(o.ParameterSignature);
                    bw.Write(o.CallingConvention);
                }
                bw.Write(data.TypeIntra.Count);
                foreach (var (target, sites) in data.TypeIntra)
                {
                    bw.Write(target);
                    bw.Write(sites.Count);
                    foreach (var s in sites)
                    {
                        bw.Write(s.SiteToken);
                        bw.Write((byte)s.SiteKind);
                        bw.Write((byte)s.ReferenceKind);
                    }
                }
                bw.Write(data.TypeOutbound.Count);
                foreach (var o in data.TypeOutbound)
                {
                    bw.Write(o.SiteToken);
                    bw.Write((byte)o.SiteKind);
                    bw.Write((byte)o.ReferenceKind);
                    bw.Write(o.TargetAssemblyName);
                    bw.Write(o.TargetTypeFullName);
                }
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch (IOException) { /* best-effort */ }
        catch (UnauthorizedAccessException) { /* best-effort */ }
    }

    /// <summary>Default cap on raw IL bytes encoded by <see cref="GetIlBody"/>. 4 KiB.</summary>
    public const int DefaultIlMaxBytes = 4 * 1024;

    private static int CountInstructions(byte[] il, CancellationToken cancellationToken = default)
    {
        int n = 0, pos = 0;
        int sinceCancelCheck = 0;
        var span = il.AsSpan();
        while (pos < span.Length)
        {
            if ((sinceCancelCheck++ & 0xFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            n++;
            var b1 = span[pos++];
            IlOpcodeTable.Op op;
            if (b1 == 0xFE)
            {
                if (pos >= span.Length) break;
                op = IlOpcodeTable.TwoByteOp(span[pos++]);
            }
            else op = IlOpcodeTable.OneByteOp(b1);

            var size = IlOpcodeTable.OperandSize(op);
            if (size == -1)
            {
                if (pos + 4 > span.Length) break;
                var count = BitConverter.ToInt32(span.Slice(pos, 4));
                if (count < 0 || count > (span.Length - pos - 4) / 4) break;
                pos += 4 + count * 4;
                continue;
            }
            pos += Math.Max(0, size);
        }
        return n;
    }

    private static void AddTokenRef(Module m, int token, List<IlSymbolRef> calls,
        List<IlSymbolRef> fields, List<IlSymbolRef> types)
    {
        try
        {
            var h = MetadataTokens.Handle(token);
            var bucket = h.Kind switch
            {
                HandleKind.MethodDefinition or HandleKind.MemberReference or HandleKind.MethodSpecification => calls,
                HandleKind.FieldDefinition => fields,
                HandleKind.TypeDefinition or HandleKind.TypeReference or HandleKind.TypeSpecification => types,
                _ => (List<IlSymbolRef>?)null,
            };
            bucket?.Add(BuildSymbolRef(m, token));
        }
        catch (BadImageFormatException) { /* ignore malformed token */ }
    }

    private static IlSymbolRef BuildSymbolRef(Module m, int token)
    {
        var handleStr = HandleFormat.Format(m.Mvid, token);
        string display;
        try
        {
            var h = MetadataTokens.Handle(token);
            display = h.Kind switch
            {
                HandleKind.MethodDefinition => RenderMethodDef(m, (MethodDefinitionHandle)h),
                HandleKind.MemberReference => RenderMemberRef(m, (MemberReferenceHandle)h),
                HandleKind.MethodSpecification => RenderMethodSpec(m, (MethodSpecificationHandle)h),
                HandleKind.FieldDefinition => RenderFieldDef(m, (FieldDefinitionHandle)h),
                HandleKind.TypeDefinition => RenderTypeDef(m, (TypeDefinitionHandle)h),
                HandleKind.TypeReference => RenderTypeRef(m, (TypeReferenceHandle)h),
                HandleKind.TypeSpecification => RenderTypeSpec(m, (TypeSpecificationHandle)h),
                _ => IlSymbolRef.UnresolvedDisplay,
            };
        }
        catch (BadImageFormatException) { display = IlSymbolRef.UnresolvedDisplay; }
        catch (InvalidCastException) { display = IlSymbolRef.UnresolvedDisplay; }
        return new IlSymbolRef(token, handleStr, display);
    }

    private static string RenderMethodDef(Module m, MethodDefinitionHandle h)
    {
        var def = m.MD.GetMethodDefinition(h);
        var type = m.MD.GetTypeDefinition(def.GetDeclaringType());
        return $"{TypeName(m, type)}.{m.MD.GetString(def.Name)}";
    }

    private static string RenderMemberRef(Module m, MemberReferenceHandle h)
    {
        var r = m.MD.GetMemberReference(h);
        var parent = RenderParent(m, r.Parent);
        return $"{parent}.{m.MD.GetString(r.Name)}";
    }

    private static string RenderMethodSpec(Module m, MethodSpecificationHandle h)
    {
        var spec = m.MD.GetMethodSpecification(h);
        return BuildSymbolRef(m, MetadataTokens.GetToken(spec.Method)).Display + "<…>";
    }

    private static string RenderFieldDef(Module m, FieldDefinitionHandle h)
    {
        var f = m.MD.GetFieldDefinition(h);
        var type = m.MD.GetTypeDefinition(f.GetDeclaringType());
        return $"{TypeName(m, type)}.{m.MD.GetString(f.Name)}";
    }

    private static string RenderTypeDef(Module m, TypeDefinitionHandle h) => TypeName(m, m.MD.GetTypeDefinition(h));

    private static string RenderTypeRef(Module m, TypeReferenceHandle h)
    {
        var r = m.MD.GetTypeReference(h);
        var ns = m.MD.GetString(r.Namespace);
        var n = m.MD.GetString(r.Name);
        return string.IsNullOrEmpty(ns) ? n : $"{ns}.{n}";
    }

    private static string RenderTypeSpec(Module m, TypeSpecificationHandle h)
    {
        try
        {
            return m.MD.GetTypeSpecification(h).DecodeSignature(new StringSignatureProvider(m.MD), genericContext: null);
        }
        catch (BadImageFormatException) { return IlSymbolRef.UnresolvedDisplay; }
    }

    private static string RenderParent(Module m, EntityHandle parent) => parent.Kind switch
    {
        HandleKind.TypeReference => RenderTypeRef(m, (TypeReferenceHandle)parent),
        HandleKind.TypeDefinition => RenderTypeDef(m, (TypeDefinitionHandle)parent),
        HandleKind.TypeSpecification => RenderTypeSpec(m, (TypeSpecificationHandle)parent),
        _ => IlSymbolRef.UnresolvedDisplay,
    };

    private static string TypeName(Module m, TypeDefinition t)
    {
        var name = m.MD.GetString(t.Name);
        var declaring = t.GetDeclaringType();
        if (!declaring.IsNil)
        {
            var outer = TypeName(m, m.MD.GetTypeDefinition(declaring));
            return $"{outer}+{name}";
        }
        var ns = m.MD.GetString(t.Namespace);
        return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
    }

    private static string? TryReadUserString(Module m, int token)
    {
        try
        {
            var h = MetadataTokens.UserStringHandle(token & 0x00FFFFFF);
            return m.MD.GetUserString(h);
        }
        catch (BadImageFormatException) { return null; }
        catch (ArgumentException) { return null; }
    }

    private ResolvedMethod TryResolveMethod(MethodIdentity identity)
    {
        if (identity is null)
            return new ResolvedMethod(null, default, new AssemblyError(ErrorKinds.IdentityMalformed, "identity is required."));
        if (identity.ModuleVersionId == Guid.Empty)
            return new ResolvedMethod(null, default, new AssemblyError(ErrorKinds.IdentityMalformed, "moduleVersionId is required."));
        if (identity.MetadataToken == 0)
            return new ResolvedMethod(null, default, new AssemblyError(ErrorKinds.IdentityMalformed, "metadataToken is required."));

        if (!_modules.TryGetValue(identity.ModuleVersionId, out var module))
        {
            return new ResolvedMethod(null, default, new AssemblyError(
                ErrorKinds.ModuleNotFound,
                $"no loaded module has MVID {identity.ModuleVersionId}."));
        }

        EntityHandle h;
        try { h = (EntityHandle)MetadataTokens.Handle(identity.MetadataToken); }
        catch (ArgumentException ex)
        {
            return new ResolvedMethod(null, default, new AssemblyError(ErrorKinds.InvalidArgument,
                $"could not decode metadataToken 0x{identity.MetadataToken:X8}: {ex.Message}"));
        }
        if (h.Kind != HandleKind.MethodDefinition)
        {
            return new ResolvedMethod(null, default, new AssemblyError(ErrorKinds.TokenWrongTable,
                $"metadataToken 0x{identity.MetadataToken:X8} is a {h.Kind}, expected MethodDefinition (table 0x06)."));
        }

        var mh = (MethodDefinitionHandle)h;
        var rid = MetadataTokens.GetRowNumber(mh);
        if (rid <= 0 || rid > module.MD.MethodDefinitions.Count)
        {
            return new ResolvedMethod(null, default, new AssemblyError(ErrorKinds.TokenOutOfRange,
                $"MethodDef row {rid} exceeds the module's table size ({module.MD.MethodDefinitions.Count})."));
        }

        return new ResolvedMethod(module, mh, null);
    }

    private readonly record struct ResolvedMethod(Module? Module, MethodDefinitionHandle Handle, AssemblyError? Error);

    private static ModuleSummary SummarizeModule(Module m) =>
        new(m.Mvid, Path.GetFileName(m.Path), m.Path, m.MD.MethodDefinitions.Count);

    private static MethodSummary SummarizeMethod(Module m, MethodDefinitionHandle h, int token)
    {
        var def = m.MD.GetMethodDefinition(h);
        var typeDef = m.MD.GetTypeDefinition(def.GetDeclaringType());
        var fullType = TypeName(m, typeDef);
        var methodName = m.MD.GetString(def.Name);

        var sig = def.DecodeSignature(new StringSignatureProvider(m.MD), genericContext: null);
        var paramList = string.Join(", ", sig.ParameterTypes);
        var signature = $"{sig.ReturnType} {fullType}.{methodName}({paramList})";

        int ilSize = 0;
        if (def.RelativeVirtualAddress != 0)
        {
            try
            {
                var body = m.PE.GetMethodBody(def.RelativeVirtualAddress);
                ilSize = body.GetILBytes()?.Length ?? 0;
            }
            catch (BadImageFormatException)
            {
                ilSize = 0;
            }
        }

        var attrs = FormatAttributes(def.Attributes);
        var handle = HandleFormat.Format(m.Mvid, token);

        return new MethodSummary(
            m.Mvid, token, handle, fullType, methodName, signature,
            ilSize, def.GetGenericParameters().Count, attrs);
    }

    private static List<string> FormatAttributes(MethodAttributes a)
    {
        var list = new List<string>(6);
        switch (a & MethodAttributes.MemberAccessMask)
        {
            case MethodAttributes.Public: list.Add("public"); break;
            case MethodAttributes.Family: list.Add("protected"); break;
            case MethodAttributes.Assembly: list.Add("internal"); break;
            case MethodAttributes.FamORAssem: list.Add("protected internal"); break;
            case MethodAttributes.Private: list.Add("private"); break;
            case MethodAttributes.PrivateScope: list.Add("compiler-generated"); break;
            case MethodAttributes.FamANDAssem: list.Add("private protected"); break;
        }
        if ((a & MethodAttributes.Static) != 0) list.Add("static");
        if ((a & MethodAttributes.Abstract) != 0) list.Add("abstract");
        if ((a & MethodAttributes.Virtual) != 0) list.Add("virtual");
        if ((a & MethodAttributes.Final) != 0) list.Add("sealed");
        if ((a & MethodAttributes.SpecialName) != 0) list.Add("specialname");
        if ((a & MethodAttributes.PinvokeImpl) != 0) list.Add("pinvoke");
        return list;
    }

    /// <inheritdoc />
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
        foreach (var pdb in _sourceCache.Values)
        {
            // Embedded portable PDB providers pin native memory; releasing them is mandatory.
            pdb?.Provider?.Dispose();
        }
        _sourceCache.Clear();
    }

    private sealed record Module(Guid Mvid, string Path, PEReader PE, MetadataReader MD);

    /// <inheritdoc />
    public GetTypeResult GetTypeDefinition(Guid moduleVersionId, int typeMetadataToken)
    {
        if (moduleVersionId == Guid.Empty)
            return GetTypeResult.Fail(new AssemblyError(ErrorKinds.IdentityMalformed, "moduleVersionId is required."));
        if (!_modules.TryGetValue(moduleVersionId, out var module))
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
        if (!_modules.TryGetValue(moduleVersionId, out var module))
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

        foreach (var kv in _modules)
        {
            var childMvid = kv.Key;
            var m = kv.Value;
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
                if (!_modules.TryGetValue(node.Item1, out var nodeModule)) continue;
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
            .Select(h => (Hit: h, Path: _modules.TryGetValue(h.Item1, out var mm) ? mm.Path : string.Empty))
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
            if (!_modules.TryGetValue(hitMvid, out var hitModule)) continue;
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
        Module childModule,
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
        Module childModule,
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
            var openHandle = EntityHandle(encoded);

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
        if (!_modules.TryGetValue(moduleVersionId, out var module))
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
                    Handle: $"a:{module.Mvid:D}:0x{token:X8}",
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
    public ListMembersResult ListMembers(Guid moduleVersionId, int typeMetadataToken, ListMembersQuery query)
    {
        query ??= new ListMembersQuery();
        if (moduleVersionId == Guid.Empty)
            return ListMembersResult.Fail(new AssemblyError(ErrorKinds.IdentityMalformed, "moduleVersionId is required."));
        if (!_modules.TryGetValue(moduleVersionId, out var module))
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

    private static void CollectFields(Module module, TypeDefinition td, StringSignatureProvider provider, List<MemberSummary> sink)
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
                    module.Mvid, token, $"f:{module.Mvid:D}:0x{token:X8}",
                    MemberKind.Field, name, $"{fieldType} {name}", attrs));
            }
            catch (BadImageFormatException) { /* skip malformed row */ }
        }
    }

    private static void CollectProperties(Module module, TypeDefinition td, StringSignatureProvider provider, List<MemberSummary> sink)
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
                    module.Mvid, token, $"p:{module.Mvid:D}:0x{token:X8}",
                    MemberKind.Property, name, $"{sig.ReturnType} {name}{paramList} {accessorsRender}", attrs));
            }
            catch (BadImageFormatException) { /* skip malformed row */ }
        }
    }

    private static void CollectEvents(Module module, TypeDefinition td, StringSignatureProvider provider, List<MemberSummary> sink)
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
                    module.Mvid, token, $"e:{module.Mvid:D}:0x{token:X8}",
                    MemberKind.Event, name, $"event {typeName} {name}", attrs));
            }
            catch (BadImageFormatException) { /* skip malformed row */ }
        }
    }

    private static string DecodeTypeSpec(Module module, TypeSpecificationHandle handle, StringSignatureProvider provider)
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
        if (!_modules.TryGetValue(target.ModuleVersionId, out var module))
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
        Module module, AttributeTarget target, out AssemblyError? error)
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
        Module module, CustomAttributeHandle handle, int token,
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
        var t = raw.GetType();
        if (!t.IsGenericType || t.GetGenericTypeDefinition() != typeof(ImmutableArray<>))
            return raw;
        var list = new List<object?>();
        foreach (var element in (System.Collections.IEnumerable)raw)
        {
            // Each element is CustomAttributeTypedArgument<string>; reflect into its Value.
            var valueProp = element?.GetType().GetProperty("Value");
            list.Add(RenderAttributeValue(valueProp?.GetValue(element)));
        }
        return list;
    }
}

/// <summary>
/// Thrown by <see cref="StringCustomAttributeTypeProvider"/> when the metadata reader asks
/// for a type ('Type'-typed argument) that doesn't resolve cleanly. The decoder catches it
/// so a single malformed argument doesn't poison the whole attribute walk.
/// </summary>
internal sealed class UnknownTypeException(string message) : Exception(message);

/// <summary>
/// Minimal <see cref="ICustomAttributeTypeProvider{TType}"/> producing readable strings.
/// Mirrors <see cref="StringSignatureProvider"/> but answers the custom-attribute decoder's
/// type-rendering questions (no signature decoding involved).
/// </summary>
internal sealed class StringCustomAttributeTypeProvider : ICustomAttributeTypeProvider<string>
{
    private readonly MetadataReader _md;
    public StringCustomAttributeTypeProvider(MetadataReader md) => _md = md;

    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.Boolean => "bool",
        PrimitiveTypeCode.Byte => "byte",
        PrimitiveTypeCode.SByte => "sbyte",
        PrimitiveTypeCode.Char => "char",
        PrimitiveTypeCode.Int16 => "short",
        PrimitiveTypeCode.UInt16 => "ushort",
        PrimitiveTypeCode.Int32 => "int",
        PrimitiveTypeCode.UInt32 => "uint",
        PrimitiveTypeCode.Int64 => "long",
        PrimitiveTypeCode.UInt64 => "ulong",
        PrimitiveTypeCode.Single => "float",
        PrimitiveTypeCode.Double => "double",
        PrimitiveTypeCode.String => "string",
        PrimitiveTypeCode.Object => "object",
        PrimitiveTypeCode.Void => "void",
        PrimitiveTypeCode.IntPtr => "nint",
        PrimitiveTypeCode.UIntPtr => "nuint",
        PrimitiveTypeCode.TypedReference => "typedref",
        _ => typeCode.ToString(),
    };

    public string GetSystemType() => "System.Type";
    public string GetSZArrayType(string elementType) => elementType + "[]";

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var t = reader.GetTypeDefinition(handle);
        var ns = reader.GetString(t.Namespace);
        var n = reader.GetString(t.Name);
        return string.IsNullOrEmpty(ns) ? n : ns + "." + n;
    }

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var t = reader.GetTypeReference(handle);
        var ns = reader.GetString(t.Namespace);
        var n = reader.GetString(t.Name);
        return string.IsNullOrEmpty(ns) ? n : ns + "." + n;
    }

    public string GetTypeFromSerializedName(string name)
    {
        // 'name' is an assembly-qualified type name as it appears in the CustomAttribute blob
        // for typeof(...) arguments. Strip the assembly portion so the value stays consistent
        // with how we render other type references.
        var comma = name.IndexOf(',');
        return comma < 0 ? name : name[..comma].Trim();
    }

    public PrimitiveTypeCode GetUnderlyingEnumType(string type)
    {
        // For most framework enums this is Int32. We don't have a resolver to walk arbitrary
        // user-defined enum types, so fall back to Int32 — the decoder will then surface the
        // raw integer value, which is what consumers actually want.
        return PrimitiveTypeCode.Int32;
    }

    public bool IsSystemType(string type) =>
        type is "System.Type" || type.EndsWith(".Type", StringComparison.Ordinal);
}

/// <summary>Payload of <see cref="MetadataIndex.ModuleReloaded"/>.</summary>
public sealed class ModuleReloadedEventArgs : EventArgs
{
    public ModuleReloadedEventArgs(string path, Guid? oldMvid, Guid? newMvid, AssemblyError? error)
    {
        Path = path;
        OldMvid = oldMvid;
        NewMvid = newMvid;
        Error = error;
    }

    /// <summary>Absolute path of the file that was reloaded.</summary>
    public string Path { get; }
    /// <summary>MVID that was loaded before the change (null if first load).</summary>
    public Guid? OldMvid { get; }
    /// <summary>MVID after the change (null when <see cref="Error"/> is set).</summary>
    public Guid? NewMvid { get; }
    /// <summary>Populated when the reload failed (e.g. corrupted intermediate write).</summary>
    public AssemblyError? Error { get; }
}

/// <summary>Stable string handle format used across all tool responses.</summary>
public static class HandleFormat
{
    public static string Format(Guid mvid, int token) => $"m:{mvid:D}:0x{token:X8}";

    /// <summary>Format for a type-definition handle (table 0x02), distinct from method handles.</summary>
    public static string FormatType(Guid mvid, int token) => $"t:{mvid:D}:0x{token:X8}";

    /// <summary>Format for a field-definition handle (table 0x04).</summary>
    public static string FormatField(Guid mvid, int token) => $"f:{mvid:D}:0x{token:X8}";

    /// <summary>Format for a property-definition handle (table 0x17).</summary>
    public static string FormatProperty(Guid mvid, int token) => $"p:{mvid:D}:0x{token:X8}";

    /// <summary>Format for an event-definition handle (table 0x14).</summary>
    public static string FormatEvent(Guid mvid, int token) => $"e:{mvid:D}:0x{token:X8}";

    /// <summary>Format for a single parameter (1-based sequence) of a method-definition handle.</summary>
    public static string FormatParameter(Guid mvid, int methodToken, int parameterSequence)
        => $"m:{mvid:D}:0x{methodToken:X8}#param={parameterSequence}";

    /// <summary>Format for the assembly-definition row of a module (no token component).</summary>
    public static string FormatAssembly(Guid mvid) => $"a:{mvid:D}";
}

/// <summary>
/// Minimal signature decoder producing readable strings. Not a full pretty-printer — good
/// enough for the <see cref="MethodSummary.Signature"/> field.
/// </summary>
internal sealed class StringSignatureProvider : ISignatureTypeProvider<string, object?>
{
    private readonly MetadataReader _md;
    public StringSignatureProvider(MetadataReader md) => _md = md;

    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.Boolean => "bool",
        PrimitiveTypeCode.Byte => "byte",
        PrimitiveTypeCode.SByte => "sbyte",
        PrimitiveTypeCode.Char => "char",
        PrimitiveTypeCode.Int16 => "short",
        PrimitiveTypeCode.UInt16 => "ushort",
        PrimitiveTypeCode.Int32 => "int",
        PrimitiveTypeCode.UInt32 => "uint",
        PrimitiveTypeCode.Int64 => "long",
        PrimitiveTypeCode.UInt64 => "ulong",
        PrimitiveTypeCode.Single => "float",
        PrimitiveTypeCode.Double => "double",
        PrimitiveTypeCode.String => "string",
        PrimitiveTypeCode.Object => "object",
        PrimitiveTypeCode.Void => "void",
        PrimitiveTypeCode.IntPtr => "nint",
        PrimitiveTypeCode.UIntPtr => "nuint",
        PrimitiveTypeCode.TypedReference => "typedref",
        _ => typeCode.ToString(),
    };

    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetArrayType(string elementType, ArrayShape shape) =>
        elementType + "[" + new string(',', Math.Max(0, shape.Rank - 1)) + "]";
    public string GetByReferenceType(string elementType) => "ref " + elementType;
    public string GetPointerType(string elementType) => elementType + "*";
    public string GetPinnedType(string elementType) => "pinned " + elementType;
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
        genericType + "<" + string.Join(",", typeArguments) + ">";
    public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
    public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
    public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var t = reader.GetTypeDefinition(handle);
        var ns = reader.GetString(t.Namespace);
        var n = reader.GetString(t.Name);
        return string.IsNullOrEmpty(ns) ? n : ns + "." + n;
    }

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var t = reader.GetTypeReference(handle);
        var ns = reader.GetString(t.Namespace);
        var n = reader.GetString(t.Name);
        return string.IsNullOrEmpty(ns) ? n : ns + "." + n;
    }

    public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
}

/// <summary>
/// Signature provider used by <see cref="MetadataIndex.BuildXref"/> to collect every TypeDef /
/// TypeRef handle reachable from a signature (method, field, property, local). The provider's
/// return type is a dummy unit value; the side-effect is accumulation into an internal sink
/// that the caller drains with <see cref="Drain"/> between uses. <see cref="Reset"/> clears
/// the sink so a single instance can be reused across many signatures.
/// </summary>
internal sealed class TypeTokenCollectorProvider : ISignatureTypeProvider<TypeTokenCollectorProvider.Unit, object?>
{
    public readonly record struct Unit;


    private readonly MetadataReader _md;
    private readonly HashSet<EntityHandle> _seen = new();
    private readonly List<EntityHandle> _ordered = new();

    public TypeTokenCollectorProvider(MetadataReader md) => _md = md;

    public void Reset() { _seen.Clear(); _ordered.Clear(); }

    public IReadOnlyList<EntityHandle> Drain()
    {
        // Caller must consume the snapshot before resetting; we return the live list because
        // the typical caller iterates synchronously and discards the provider afterwards.
        return _ordered;
    }

    private Unit Add(EntityHandle h)
    {
        if (_seen.Add(h)) _ordered.Add(h);
        return default;
    }

    public Unit GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => Add(handle);
    public Unit GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => Add(handle);
    public Unit GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
    {
        // Recurse into the spec so generic args + outer type both surface, but don't record
        // the TypeSpec handle itself — only the leaf TypeDef/TypeRef handles are useful.
        try { reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext); }
        catch (BadImageFormatException) { /* skip */ }
        return default;
    }

    public Unit GetPrimitiveType(PrimitiveTypeCode typeCode) => default;
    public Unit GetSZArrayType(Unit elementType) => default;
    public Unit GetArrayType(Unit elementType, ArrayShape shape) => default;
    public Unit GetByReferenceType(Unit elementType) => default;
    public Unit GetPointerType(Unit elementType) => default;
    public Unit GetPinnedType(Unit elementType) => default;
    public Unit GetGenericInstantiation(Unit genericType, ImmutableArray<Unit> typeArguments) => default;
    public Unit GetGenericMethodParameter(object? genericContext, int index) => default;
    public Unit GetGenericTypeParameter(object? genericContext, int index) => default;
    public Unit GetModifiedType(Unit modifier, Unit unmodifiedType, bool isRequired) => default;
    public Unit GetFunctionPointerType(MethodSignature<Unit> signature) => default;
}
