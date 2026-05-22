using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using DotnetAssemblyMcp.Core.Handles;
using HandleKind = System.Reflection.Metadata.HandleKind;
using static DotnetAssemblyMcp.Core.Metadata.MetadataDisplay;
using static DotnetAssemblyMcp.Core.Metadata.MetadataResolver;

namespace DotnetAssemblyMcp.Core.Metadata;

/// <summary>
/// Per-module call + type-reference xref index. Extracted from MetadataIndex (#82). Owns
/// the in-memory <see cref="ModuleScopedCache{TData}"/> and the on-disk
/// <c>~/.cache/dotnet-assembly-mcp/&lt;mvid&gt;.xref</c> persistence, including the
/// format-version + (mtime, length) staleness header.
/// </summary>
/// <remarks>
/// MetadataIndex's public <c>FindCallers</c> / <c>FindTypeReferences</c> remain on the
/// façade because they share helper machinery (TryResolveMethod, GenericArgResolver,
/// signature post-walks) with other façade methods; this class is the data-plane those
/// methods read from via the <c>LoadOrBuildXref</c> overloads.
/// </remarks>
internal sealed class XrefIndex : IModuleScopedCache
{
    private readonly ModuleScopedCache<XrefData> _cache = new();
    private readonly string _xrefCacheDir;

    public XrefIndex(string xrefCacheDir)
    {
        _xrefCacheDir = xrefCacheDir;
    }

    public void Invalidate(Guid mvid)
    {
        _cache.Invalidate(mvid);
        try
        {
            var path = XrefCachePath(mvid);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { /* best-effort */ }
        catch (UnauthorizedAccessException) { /* best-effort */ }
    }

    internal bool HasCacheEntry(Guid mvid) => _cache.HasEntry(mvid);

    /// <summary>
    /// Returns the cached <see cref="XrefData"/> for <paramref name="module"/>, building
    /// (and persisting) it on first access. <paramref name="fromCache"/> is set to
    /// <see langword="false"/> when a fresh build happens; it is left untouched on a hit.
    /// </summary>
    public XrefData LoadOrBuildXref(ModuleHandle module, ref bool fromCache, CancellationToken cancellationToken = default)
    {
        var data = _cache.GetOrBuild(module, m => LoadOrBuild(m, cancellationToken), out var wasCached);
        if (!wasCached) fromCache = false;
        return data;
    }

    /// <summary>Convenience overload for the cross-module probe path that doesn't track fromCache.</summary>
    public XrefData LoadOrBuildXref(ModuleHandle module, CancellationToken cancellationToken = default) =>
        _cache.GetOrBuild(module, m => LoadOrBuild(m, cancellationToken));

    private XrefData LoadOrBuild(ModuleHandle module, CancellationToken cancellationToken)
    {
        var cachePath = XrefCachePath(module.Mvid);
        if (TryReadXrefCache(cachePath, module, out var cached))
            return cached;

        var built = BuildXref(module, cancellationToken);
        TryWriteXrefCache(cachePath, module, built);
        return built;
    }

    private string XrefCachePath(Guid mvid) => Path.Combine(_xrefCacheDir, $"{mvid:N}.xref");

    /// <summary>
    /// Maximum number of MethodDef rows scanned before <see cref="BuildXref"/> aborts.
    /// Roughly 10× the largest .NET BCL assembly. Above this point the index is no
    /// longer "an index" — it's a denial-of-service vector against the host's memory.
    /// </summary>
    internal const int MaxMethodsScanned = 200_000;

    /// <summary>
    /// Maximum number of references (intra + outbound, calls + type-refs) retained
    /// across all four buckets of <see cref="XrefData"/> before <see cref="BuildXref"/>
    /// aborts. 5M ≈ ~250 MiB of pointer-rich Dictionary/List overhead — beyond this we
    /// fail closed rather than chase an OOM.
    /// </summary>
    internal const long MaxRefsRetained = 5_000_000;

    private static XrefData BuildXref(ModuleHandle module, CancellationToken cancellationToken = default)
    {
        var data = new XrefData(
            new Dictionary<int, List<int>>(),
            new List<OutboundCallRef>(),
            new Dictionary<int, List<TypeReferenceSite>>(),
            new List<OutboundTypeRef>());
        var intraSeen = new HashSet<long>();
        var outboundSeen = new HashSet<OutboundCallRef>();
        var typeIntraSeen = new HashSet<long>();
        var typeOutboundSeen = new HashSet<OutboundTypeRef>();
        var typeCollector = new TypeTokenCollectorProvider(module.MD);
        var i = 0;
        foreach (var methodHandle in module.MD.MethodDefinitions)
        {
            if ((++i & 0xFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnforceRefBudget(data);
            }
            if (i > MaxMethodsScanned)
                throw new Errors.ModuleTooLargeException(nameof(MaxMethodsScanned), MaxMethodsScanned);
            var def = module.MD.GetMethodDefinition(methodHandle);

            var callerToken = MetadataTokens.GetToken(methodHandle);
            typeIntraSeen.Clear();

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

            ScanTypesFromIl(module, ilBytes, callerToken, data, typeIntraSeen, typeOutboundSeen);
        }

        var nonMethodCount = 0;
        foreach (var fh in module.MD.FieldDefinitions)
        {
            if ((++nonMethodCount & 0xFF) == 0) EnforceRefBudget(data);
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
            if ((++nonMethodCount & 0xFF) == 0) EnforceRefBudget(data);
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
            if ((++nonMethodCount & 0xFF) == 0) EnforceRefBudget(data);
            try
            {
                var ed = module.MD.GetEventDefinition(eh);
                var siteToken = MetadataTokens.GetToken(eh);
                ClassifyTypeReferenceHandle(module, ed.Type, siteToken, MemberKind.Event,
                    TypeReferenceKind.EventType, data, perSiteSeen: null, typeOutboundSeen);
            }
            catch (BadImageFormatException) { /* skip */ }
        }

        foreach (var tdh in module.MD.TypeDefinitions)
        {
            if ((++nonMethodCount & 0xFF) == 0) EnforceRefBudget(data);
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

        EnforceRefBudget(data);
        return data;
    }

    private static void EnforceRefBudget(XrefData data)
    {
        long total = data.Outbound.Count + data.TypeOutbound.Count;
        foreach (var kv in data.Intra) total += kv.Value.Count;
        foreach (var kv in data.TypeIntra) total += kv.Value.Count;
        if (total > MaxRefsRetained)
            throw new Errors.ModuleTooLargeException(nameof(MaxRefsRetained), MaxRefsRetained);
    }

    private static void EmitCollectedTypes(ModuleHandle module, TypeTokenCollectorProvider collector,
        int siteToken, MemberKind siteKind, TypeReferenceKind refKind,
        XrefData data, HashSet<long>? perSiteSeen, HashSet<OutboundTypeRef> outboundSeen)
    {
        foreach (var handle in collector.Drain())
        {
            ClassifyTypeReferenceHandle(module, handle, siteToken, siteKind, refKind, data, perSiteSeen, outboundSeen);
        }
    }

    private static void ClassifyTypeReferenceHandle(ModuleHandle module, EntityHandle handle,
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
                try
                {
                    var spec = module.MD.GetTypeSpecification((TypeSpecificationHandle)handle);
                    var collector = new TypeTokenCollectorProvider(module.MD);
                    spec.DecodeSignature(collector, genericContext: null);
                    foreach (var leaf in collector.Drain())
                    {
                        if (leaf.Kind == HandleKind.TypeSpecification) continue;
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

    private static void ScanTypesFromIl(ModuleHandle module, byte[] il, int methodToken,
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
            else if (size == 4 && pos + 4 <= span.Length
                && (op == IlOpcodeTable.Op.InlineMethod || op == IlOpcodeTable.Op.InlineField))
            {
                // Issue #69: a `newobj Box<int>::.ctor` (or `ldfld <generic-type>::field`)
                // emits InlineMethod / InlineField whose operand is a MemberRef whose Parent
                // is a TypeSpec wrapping the generic type. Without walking the parent here
                // the open generic stays invisible to find_type_references.
                var token = BitConverter.ToInt32(span.Slice(pos, 4));
                ClassifyMemberBearingTokenForParentType(module, token, methodToken,
                    data, intraSeen, outboundSeen);
            }

            pos += Math.Max(0, size);
        }
    }

    private static void ClassifyMemberBearingTokenForParentType(ModuleHandle module, int token, int methodToken,
        XrefData data, HashSet<long> intraSeen, HashSet<OutboundTypeRef> outboundSeen)
    {
        EntityHandle h;
        try { h = (EntityHandle)MetadataTokens.Handle(token); }
        catch (ArgumentOutOfRangeException) { return; }
        catch (BadImageFormatException) { return; }

        try
        {
            EntityHandle parent;
            switch (h.Kind)
            {
                case HandleKind.MemberReference:
                    parent = module.MD.GetMemberReference((MemberReferenceHandle)h).Parent;
                    break;
                case HandleKind.MethodSpecification:
                    // MethodSpec → resolved Method (MethodDef or MemberRef); walk through.
                    var spec = module.MD.GetMethodSpecification((MethodSpecificationHandle)h);
                    var method = spec.Method;
                    parent = method.Kind switch
                    {
                        HandleKind.MemberReference =>
                            module.MD.GetMemberReference((MemberReferenceHandle)method).Parent,
                        _ => default,
                    };
                    break;
                default:
                    // MethodDef / FieldDef parents are reachable via signature scans already
                    // (intra-module TypeDef entries) — no extra walk needed here.
                    return;
            }

            if (parent.IsNil) return;
            ClassifyTypeReferenceHandle(module, parent, methodToken, MemberKind.Method,
                TypeReferenceKind.IlOpcode, data, intraSeen, outboundSeen);
        }
        catch (BadImageFormatException) { /* skip malformed row */ }
    }

    private static void ClassifyTypeBearingToken(ModuleHandle module, int token, int methodToken,
        XrefData data, HashSet<long> intraSeen, HashSet<OutboundTypeRef> outboundSeen)
    {
        EntityHandle h;
        try { h = (EntityHandle)MetadataTokens.Handle(token); }
        catch (ArgumentOutOfRangeException) { return; }
        catch (BadImageFormatException) { return; }

        if (h.Kind != HandleKind.TypeDefinition
            && h.Kind != HandleKind.TypeReference
            && h.Kind != HandleKind.TypeSpecification)
        {
            return;
        }

        if (h.Kind == HandleKind.TypeSpecification)
        {
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

    private static void ScanCallsFromIl(ModuleHandle module, byte[] il, int callerToken, XrefData data,
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
                if (n < 0 || n > (span.Length - pos - 4) / 4) break;
                pos += 4 + n * 4;
                continue;
            }

            if (size == 4 && pos + 4 <= span.Length && op == IlOpcodeTable.Op.InlineMethod)
            {
                var token = BitConverter.ToInt32(span.Slice(pos, 4));
                ClassifyCallToken(module, token, callerToken, data, intraSeen, outboundSeen);
            }

            pos += Math.Max(0, size);
        }
    }

    private static void ClassifyCallToken(ModuleHandle module, int token, int callerToken, XrefData data,
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

    private static void ClassifyMemberRef(ModuleHandle module, MemberReferenceHandle mrh,
        int callerToken, XrefData data,
        HashSet<long> intraSeen, HashSet<OutboundCallRef> outboundSeen)
    {
        MemberReference mr;
        try { mr = module.MD.GetMemberReference(mrh); }
        catch (BadImageFormatException) { return; }
        if (mr.GetKind() != MemberReferenceKind.Method) return;

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
        var key = ((long)calleeToken << 32) | (uint)callerToken;
        if (!seen.Add(key)) return;
        if (!intra.TryGetValue(calleeToken, out var list))
        {
            list = new List<int>();
            intra[calleeToken] = list;
        }
        if (list.Count == 0 || list[^1] != callerToken)
            list.Add(callerToken);
    }

    private static void TryAddOutbound(ModuleHandle module, MemberReference mr,
        int callerToken, List<OutboundCallRef> outbound, HashSet<OutboundCallRef> seen)
    {
        try
        {
            var typeName = ResolveOutboundTypeName(module, mr.Parent, out var assemblyName);
            if (typeName is null || assemblyName is null) return;

            var methodName = module.MD.GetString(mr.Name);
            var decoder = new SignatureDecoder<string, object?>(
                StringSignatureProvider.WithoutModifiers(module.MD), module.MD, genericContext: null);
            var blob = module.MD.GetBlobReader(mr.Signature);
            var sig = decoder.DecodeMethodSignature(ref blob);
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

    public static CalleeKey BuildCalleeKey(ModuleHandle module, MethodDefinitionHandle handle)
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
                StringSignatureProvider.WithoutModifiers(module.MD), module.MD, genericContext: null);
            var blob = module.MD.GetBlobReader(def.Signature);
            var sig = decoder.DecodeMethodSignature(ref blob);
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
    private const int XrefFormatVersion = 7;
    private const int MaxIntraCount = 10_000_000;
    private const int MaxOutboundCount = 10_000_000;
    private const int MaxIntraCallersPerCallee = 1_000_000;

    private static bool TryReadXrefCache(string path, ModuleHandle module, out XrefData data)
    {
        data = null!;
        if (!File.Exists(path)) return false;

        // Reject any group/other permission bit on Unix. The cache may include xref data
        // derived from the assembly's metadata — owner-only is the only hygienic state.
        // Files inherited from a previous version (or from a pre-0.19.0 install under a
        // 0022 umask) may be 0644 — read-only to others but still leaked. Force-rebuild.
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                var mode = File.GetUnixFileMode(path);
                const UnixFileMode foreignAny =
                    UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
                if ((mode & foreignAny) != 0)
                {
                    try { File.Delete(path); } catch (IOException) { /* swallow */ }
                    catch (UnauthorizedAccessException) { /* swallow */ }
                    return false;
                }
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (PlatformNotSupportedException) { /* old runtime — fall through */ }
        }

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
        catch (EndOfStreamException) { return false; }
        catch (IOException) { return false; }
        catch (FormatException) { return false; }
        catch (ArgumentException) { return false; }
        catch (OutOfMemoryException) { return false; }
    }

    private void TryWriteXrefCache(string path, ModuleHandle module, XrefData data)
    {
        try
        {
            EnsureCacheDirectoryHardened();
            FileInfo info;
            try { info = new FileInfo(module.Path); }
            catch (IOException) { return; }
            if (!info.Exists) return;

            var tmp = path + ".tmp";

            // Owner-only perms on Unix (0600). On Windows the cache dir is the user profile,
            // which already inherits a user-only ACL by default; FileStreamOptions.UnixCreateMode
            // is a no-op there.
            var streamOptions = new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.SequentialScan,
            };
            if (!OperatingSystem.IsWindows())
            {
                streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var fs = new FileStream(tmp, streamOptions))
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

    /// <summary>
    /// Creates the xref cache directory if missing and tightens its mode to 0700 on Unix.
    /// Defends against a multi-user host where the directory might pre-exist with permissive
    /// umask, letting another local user read or poison cache files. No-op on Windows
    /// (the user profile already inherits a user-only ACL).
    /// </summary>
    private void EnsureCacheDirectoryHardened()
    {
        Directory.CreateDirectory(_xrefCacheDir);
        if (OperatingSystem.IsWindows()) return;
        try
        {
            const UnixFileMode ownerOnly =
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            var current = File.GetUnixFileMode(_xrefCacheDir);
            if (current != ownerOnly)
            {
                File.SetUnixFileMode(_xrefCacheDir, ownerOnly);
            }
        }
        catch (IOException) { /* best-effort — keep going even if chmod fails */ }
        catch (UnauthorizedAccessException) { /* best-effort */ }
        catch (PlatformNotSupportedException) { /* best-effort */ }
    }
}
