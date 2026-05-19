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
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _pendingReloads =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _watch;
    private int _disposed;

    private readonly ConcurrentDictionary<Guid, XrefData> _xrefCache = new();
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
                return LoadResult.Fail(new AssemblyError(ErrorKinds.ModuleLoadFailed, $"not a managed PE: {fullPath}"));
            }
            var md = pe.GetMetadataReader();
            var mvid = md.GetGuid(md.GetModuleDefinition().Mvid);

            var added = _modules.GetOrAdd(mvid, _ => new Module(mvid, fullPath, pe, md));
            if (!ReferenceEquals(added.PE, pe))
            {
                // Lost a race; another thread loaded the same MVID first. Dispose our duplicate.
                pe.Dispose();
            }
            return LoadResult.Ok(SummarizeModule(added));
        }
        catch (BadImageFormatException ex)
        {
            return LoadResult.Fail(new AssemblyError(ErrorKinds.ModuleLoadFailed, "invalid PE/CLI image.", ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            return LoadResult.Fail(new AssemblyError(ErrorKinds.ModuleLoadFailed, "permission denied.", ex.Message));
        }
        catch (IOException ex)
        {
            return LoadResult.Fail(new AssemblyError(ErrorKinds.ModuleLoadFailed, "i/o error opening assembly.", ex.Message));
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
        return ResolveResult.Ok(summary);
    }

    /// <inheritdoc />
    public IlBodyResult GetIlBody(MethodIdentity identity, int maxBytes = 0)
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
            var instructions = CountInstructions(ilBytes);

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
    public IlScanReadResult ScanIl(MethodIdentity identity)
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
        while (pos < span.Length)
        {
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
                pos += 4 + Math.Max(0, n) * 4;
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
    public FindCallersReadResult FindCallers(MethodIdentity callee)
    {
        var common = TryResolveMethod(callee);
        if (common.Error is not null) return FindCallersReadResult.Fail(common.Error);
        var module = common.Module!;
        var methodHandle = common.Handle;

        var fromCache = true;
        var xref = _xrefCache.GetOrAdd(module.Mvid, _ =>
        {
            fromCache = false;
            return LoadOrBuildXref(module);
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
            if (other.Mvid == module.Mvid) continue;
            modulesSearched++;

            var otherXref = _xrefCache.GetOrAdd(other.Mvid, _ => LoadOrBuildXref(other));
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

        var calleeHandleStr = HandleFormat.Format(module.Mvid, callee.MetadataToken);
        return FindCallersReadResult.Ok(new FindCallersResult(
            module.Mvid, callee.MetadataToken, calleeHandleStr,
            callers, modulesSearched, FromCache: fromCache));
    }

    private void InvalidateXref(Guid mvid)
    {
        _xrefCache.TryRemove(mvid, out _);
        try
        {
            var path = XrefCachePath(mvid);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { /* best-effort */ }
        catch (UnauthorizedAccessException) { /* best-effort */ }
    }

    private string XrefCachePath(Guid mvid) => Path.Combine(_xrefCacheDir, $"{mvid:N}.xref");

    private XrefData LoadOrBuildXref(Module module)
    {
        var cachePath = XrefCachePath(module.Mvid);
        if (TryReadXrefCache(cachePath, module, out var cached))
            return cached;

        var built = BuildXref(module);
        TryWriteXrefCache(cachePath, module, built);
        return built;
    }

    private static XrefData BuildXref(Module module)
    {
        var data = new XrefData(new Dictionary<int, List<int>>(), new List<OutboundCallRef>());
        foreach (var methodHandle in module.MD.MethodDefinitions)
        {
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
            ScanCallsFromIl(module, ilBytes, callerToken, data);
        }
        return data;
    }

    private static void ScanCallsFromIl(Module module, byte[] il, int callerToken, XrefData data)
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
                pos += 4 + Math.Max(0, n) * 4;
                continue;
            }

            if (size == 4 && pos + 4 <= span.Length &&
                (op == IlOpcodeTable.Op.InlineMethod || op == IlOpcodeTable.Op.InlineTok))
            {
                var token = BitConverter.ToInt32(span.Slice(pos, 4));
                ClassifyCallToken(module, token, callerToken, data);
            }

            pos += Math.Max(0, size);
        }
    }

    private static void ClassifyCallToken(Module module, int token, int callerToken, XrefData data)
    {
        EntityHandle h;
        try { h = (EntityHandle)MetadataTokens.Handle(token); }
        catch (ArgumentOutOfRangeException) { return; }
        catch (BadImageFormatException) { return; }

        switch (h.Kind)
        {
            case HandleKind.MethodDefinition:
                AddIntra(data.Intra, token, callerToken);
                break;
            case HandleKind.MemberReference:
                TryAddOutbound(module, (MemberReferenceHandle)h, callerToken, data.Outbound);
                break;
            case HandleKind.MethodSpecification:
                try
                {
                    var spec = module.MD.GetMethodSpecification((MethodSpecificationHandle)h);
                    switch (spec.Method.Kind)
                    {
                        case HandleKind.MethodDefinition:
                            AddIntra(data.Intra,
                                MetadataTokens.GetToken(spec.Method), callerToken);
                            break;
                        case HandleKind.MemberReference:
                            TryAddOutbound(module,
                                (MemberReferenceHandle)spec.Method, callerToken, data.Outbound);
                            break;
                    }
                }
                catch (BadImageFormatException) { /* skip */ }
                break;
        }
    }

    private static void AddIntra(Dictionary<int, List<int>> intra, int calleeToken, int callerToken)
    {
        if (!intra.TryGetValue(calleeToken, out var list))
        {
            list = new List<int>();
            intra[calleeToken] = list;
        }
        if (list.Count == 0 || list[^1] != callerToken)
            list.Add(callerToken);
    }

    private static void TryAddOutbound(Module module, MemberReferenceHandle mrh,
        int callerToken, List<OutboundCallRef> outbound)
    {
        try
        {
            var mr = module.MD.GetMemberReference(mrh);
            if (mr.GetKind() != MemberReferenceKind.Method) return;

            var typeName = ResolveOutboundTypeName(module, mr.Parent, out var assemblyName);
            if (typeName is null || assemblyName is null) return;

            var methodName = module.MD.GetString(mr.Name);
            var decoder = new SignatureDecoder<string, object?>(
                new StringSignatureProvider(module.MD), module.MD, genericContext: null);
            var blob = module.MD.GetBlobReader(mr.Signature);
            var sig = decoder.DecodeMethodSignature(ref blob);
            var paramSig = string.Join(",", sig.ParameterTypes);

            var entry = new OutboundCallRef(callerToken, assemblyName,
                typeName, methodName, sig.ParameterTypes.Length,
                sig.GenericParameterCount, paramSig);
            if (outbound.Count == 0 || !outbound[^1].Equals(entry))
                outbound.Add(entry);
        }
        catch (BadImageFormatException) { /* skip */ }
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
        try
        {
            var decoder = new SignatureDecoder<string, object?>(
                new StringSignatureProvider(module.MD), module.MD, genericContext: null);
            var blob = module.MD.GetBlobReader(def.Signature);
            var sig = decoder.DecodeMethodSignature(ref blob);
            paramCount = sig.ParameterTypes.Length;
            paramSig = string.Join(",", sig.ParameterTypes);
        }
        catch (BadImageFormatException) { /* leave defaults */ }

        return new CalleeKey(asmName, typeFullName, methodName, paramCount, genericArity, paramSig);
    }

    private const uint XrefMagic = 0x52584D41; // 'AMXR'
    private const int XrefFormatVersion = 2;

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
            if (intraCount < 0) return false;
            var intra = new Dictionary<int, List<int>>(intraCount);
            for (int i = 0; i < intraCount; i++)
            {
                var callee = br.ReadInt32();
                var n = br.ReadInt32();
                if (n < 0 || n > 1_000_000) return false;
                var list = new List<int>(n);
                for (int j = 0; j < n; j++) list.Add(br.ReadInt32());
                intra[callee] = list;
            }

            var outboundCount = br.ReadInt32();
            if (outboundCount < 0 || outboundCount > 10_000_000) return false;
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
                outbound.Add(new OutboundCallRef(caller, asm, type, method, pc, ga, psig));
            }

            data = new XrefData(intra, outbound);
            return true;
        }
        catch (IOException) { return false; }
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
                }
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch (IOException) { /* best-effort */ }
        catch (UnauthorizedAccessException) { /* best-effort */ }
    }

    /// <summary>Default cap on raw IL bytes encoded by <see cref="GetIlBody"/>. 4 KiB.</summary>
    public const int DefaultIlMaxBytes = 4 * 1024;

    private static int CountInstructions(byte[] il)
    {
        int n = 0, pos = 0;
        var span = il.AsSpan();
        while (pos < span.Length)
        {
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
                pos += 4 + Math.Max(0, count) * 4;
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
        var ns = m.MD.GetString(t.Namespace);
        var n = m.MD.GetString(t.Name);
        return string.IsNullOrEmpty(ns) ? n : $"{ns}.{n}";
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
        var ns = m.MD.GetString(typeDef.Namespace);
        var typeName = m.MD.GetString(typeDef.Name);
        var fullType = string.IsNullOrEmpty(ns) ? typeName : $"{ns}.{typeName}";
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
    }

    private sealed record Module(Guid Mvid, string Path, PEReader PE, MetadataReader MD);
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
