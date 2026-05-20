using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Handles;
using DotnetAssemblyMcp.Core.Identity;
using HandleKind = System.Reflection.Metadata.HandleKind;
using static DotnetAssemblyMcp.Core.Metadata.MetadataDisplay;
using static DotnetAssemblyMcp.Core.Metadata.MetadataResolver;

namespace DotnetAssemblyMcp.Core.Metadata;

/// <summary>
/// Per-module reverse index of every field access opcode (<c>ldfld</c>, <c>ldsfld</c>,
/// <c>stfld</c>, <c>stsfld</c>, <c>ldflda</c>, <c>ldsflda</c>) observed in IL bodies.
/// Also serves <c>FindPropertyReferences</c> / <c>FindEventReferences</c> by delegating
/// their accessor methods through the injected <see cref="XrefIndex"/> (via an
/// indirection <see cref="Func{T1,T2,TResult}"/> to avoid pulling MetadataIndex's
/// resolver machinery into this class).
/// </summary>
internal sealed class FieldAccessIndex : IModuleScopedCache
{
    private readonly ModuleStore _store;
    private readonly Func<MethodIdentity, CancellationToken, FindCallersReadResult> _findCallers;
    private readonly ConcurrentDictionary<Guid, CacheEntry> _cache = new();

    public FieldAccessIndex(
        ModuleStore store,
        Func<MethodIdentity, CancellationToken, FindCallersReadResult> findCallers)
    {
        _store = store;
        _findCallers = findCallers;
    }

    public void Invalidate(Guid mvid) => _cache.TryRemove(mvid, out _);

    internal bool HasCacheEntry(Guid mvid) => _cache.ContainsKey(mvid);

    public FindFieldReferencesReadResult FindFieldReferences(
        Guid moduleVersionId,
        int fieldMetadataToken,
        FieldAccessMode mode = FieldAccessMode.All,
        int maxHits = 0,
        CancellationToken cancellationToken = default)
    {
        if (moduleVersionId == Guid.Empty)
            return FindFieldReferencesReadResult.Fail(new AssemblyError(ErrorKinds.InvalidArgument, "moduleVersionId is required."));
        if (!_store.TryGet(moduleVersionId, out var module))
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
        var localIndex = GetOrBuild(module, cancellationToken, out var wasCachedLocal);
        if (!wasCachedLocal) fromCache = false;
        if (localIndex.Intra.TryGetValue(fieldMetadataToken, out var sites))
        {
            foreach (var (callerToken, ilOffset, kind) in sites)
            {
                if (!ModeMatches(mode, kind)) continue;
                if (hits.Count >= maxHits) goto done;
                var h = (MethodDefinitionHandle)MetadataTokens.Handle(callerToken);
                hits.Add(new FieldReferenceRef(
                    module.Mvid, callerToken,
                    HandleSyntax.FormatMethod(module.Mvid, callerToken),
                    RenderMethodDef(module, h),
                    ilOffset, kind));
            }
        }

        // Cross-module hits.
        if (fieldKey is { } key)
        {
            foreach (var other in _store.Modules)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (other.Mvid == module.Mvid) continue;
                modulesSearched++;
                var otherIndex = GetOrBuild(other, cancellationToken, out var wasCachedOther);
                if (!wasCachedOther) fromCache = false;
                foreach (var outbound in otherIndex.Outbound)
                {
                    if (!outbound.Matches(key)) continue;
                    if (!ModeMatches(mode, outbound.AccessKind)) continue;
                    if (hits.Count >= maxHits) goto done;
                    var h = (MethodDefinitionHandle)MetadataTokens.Handle(outbound.CallerToken);
                    hits.Add(new FieldReferenceRef(
                        other.Mvid, outbound.CallerToken,
                        HandleSyntax.FormatMethod(other.Mvid, outbound.CallerToken),
                        RenderMethodDef(other, h),
                        outbound.IlOffset, outbound.AccessKind));
                }
            }
        }
    done:

        return FindFieldReferencesReadResult.Ok(new FindFieldReferencesResult(
            module.Mvid, fieldMetadataToken,
            HandleSyntax.FormatField(module.Mvid, fieldMetadataToken),
            hits, modulesSearched, fromCache));
    }

    public FindPropertyReferencesReadResult FindPropertyReferences(
        Guid moduleVersionId,
        int propertyMetadataToken,
        PropertyAccessorFilter accessor = PropertyAccessorFilter.All,
        int maxHits = 0,
        CancellationToken cancellationToken = default)
    {
        if (moduleVersionId == Guid.Empty)
            return FindPropertyReferencesReadResult.Fail(new AssemblyError(ErrorKinds.InvalidArgument, "moduleVersionId is required."));
        if (!_store.TryGet(moduleVersionId, out var module))
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
            var r = _findCallers(BuildMethodIdentity(module, accessors.Getter), cancellationToken);
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
            var r = _findCallers(BuildMethodIdentity(module, accessors.Setter), cancellationToken);
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
            HandleSyntax.FormatProperty(module.Mvid, propertyMetadataToken),
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
        if (!_store.TryGet(moduleVersionId, out var module))
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
            var r = _findCallers(BuildMethodIdentity(module, methodHandle), cancellationToken);
            if (r.Error is not null) { err = r.Error; return false; }
            if (r.Result is null) return true;
            if (!r.Result.FromCache) fromCacheAll = false;
            if (r.Result.ModulesSearched > modulesSearchedMax) modulesSearchedMax = r.Result.ModulesSearched;
            foreach (var c in r.Result.Callers)
            {
                if (hits.Count >= maxHits) return false;
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
            HandleSyntax.FormatEvent(module.Mvid, eventMetadataToken),
            hits, modulesSearchedMax, fromCacheAll));
    }

    private FieldAccessIndexData GetOrBuild(ModuleHandle module, CancellationToken cancellationToken, out bool wasCached)
    {
        var stamp = ModuleCacheStamp.TryCapture(module);
        if (_cache.TryGetValue(module.Mvid, out var entry) && entry.Stamp.Equals(stamp))
        {
            wasCached = true;
            return entry.Data;
        }
        wasCached = false;
        var data = BuildFieldAccessIndex(module, cancellationToken);
        _cache[module.Mvid] = new CacheEntry(data, stamp);
        return data;
    }

    private static bool ModeMatches(FieldAccessMode mode, FieldAccessKind kind) => mode switch
    {
        FieldAccessMode.All => true,
        FieldAccessMode.Read => kind == FieldAccessKind.Read || kind == FieldAccessKind.Address,
        FieldAccessMode.Write => kind == FieldAccessKind.Write,
        _ => true,
    };

    private static FieldKey? BuildFieldKey(ModuleHandle module, FieldDefinition fieldDef)
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

    private static FieldAccessIndexData BuildFieldAccessIndex(ModuleHandle module, CancellationToken cancellationToken)
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

    private static void ClassifyFieldToken(ModuleHandle module, int token, int callerToken, int ilOffset,
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

    private sealed record CacheEntry(FieldAccessIndexData Data, ModuleCacheStamp Stamp);
}
