using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Handles;
using HandleKind = System.Reflection.Metadata.HandleKind;
using static DotnetAssemblyMcp.Core.Metadata.MetadataDisplay;
using static DotnetAssemblyMcp.Core.Metadata.MetadataResolver;

namespace DotnetAssemblyMcp.Core.Metadata;

/// <summary>
/// Per-module reverse index of every <c>CustomAttribute</c> keyed by attribute type full
/// name. Extracted from MetadataIndex (#82). Subscribes to module-reload fan-out via
/// <see cref="IModuleScopedCache"/> and rebuilds when the file's
/// <see cref="ModuleCacheStamp"/> drifts.
/// </summary>
internal sealed class AttributeIndex : IModuleScopedCache
{
    private readonly ModuleStore _store;
    private readonly ModuleScopedCache<AttributeIndexData> _cache = new();

    public AttributeIndex(ModuleStore store) { _store = store; }

    public void Invalidate(Guid mvid) => _cache.Invalidate(mvid);

    internal bool HasCacheEntry(Guid mvid) => _cache.HasEntry(mvid);

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

        IEnumerable<ModuleHandle> targets;
        if (moduleVersionIdFilter != Guid.Empty)
        {
            if (!_store.TryGet(moduleVersionIdFilter, out var only))
            {
                return FindAttributeTargetsReadResult.Fail(new AssemblyError(
                    ErrorKinds.ModuleNotFound,
                    $"no loaded module has MVID {moduleVersionIdFilter:D}."));
            }
            targets = new[] { only };
        }
        else
        {
            targets = _store.Modules;
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
            var index = GetOrBuild(module, cancellationToken, out var wasCached);
            if (!wasCached) fromCache = false;

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

    private AttributeIndexData GetOrBuild(ModuleHandle module, CancellationToken cancellationToken, out bool wasCached) =>
        _cache.GetOrBuild(module, m => BuildAttributeIndex(m, cancellationToken), out wasCached);

    private static (string Handle, string Display) RenderAttributeTarget(
        ModuleHandle module, AttributeTargetKind kind, int targetToken, int paramSeq)
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
                    return (HandleSyntax.FormatAssembly(module.Mvid), name);
                }
                case AttributeTargetKind.Type:
                {
                    var h = (TypeDefinitionHandle)MetadataTokens.Handle(targetToken);
                    return (HandleSyntax.FormatType(module.Mvid, targetToken),
                            TypeName(module, module.MD.GetTypeDefinition(h)));
                }
                case AttributeTargetKind.Method:
                {
                    var h = (MethodDefinitionHandle)MetadataTokens.Handle(targetToken);
                    return (HandleSyntax.FormatMethod(module.Mvid, targetToken),
                            RenderMethodDef(module, h));
                }
                case AttributeTargetKind.Parameter:
                {
                    var h = (MethodDefinitionHandle)MetadataTokens.Handle(targetToken);
                    var methodDisplay = RenderMethodDef(module, h);
                    return (HandleSyntax.FormatParameter(module.Mvid, targetToken, paramSeq),
                            $"{methodDisplay}#param={paramSeq}");
                }
                case AttributeTargetKind.Field:
                {
                    var h = (FieldDefinitionHandle)MetadataTokens.Handle(targetToken);
                    return (HandleSyntax.FormatField(module.Mvid, targetToken),
                            RenderFieldDef(module, h));
                }
                case AttributeTargetKind.Property:
                {
                    var h = (PropertyDefinitionHandle)MetadataTokens.Handle(targetToken);
                    return (HandleSyntax.FormatProperty(module.Mvid, targetToken),
                            RenderPropertyDef(module, h));
                }
                case AttributeTargetKind.Event:
                {
                    var h = (EventDefinitionHandle)MetadataTokens.Handle(targetToken);
                    return (HandleSyntax.FormatEvent(module.Mvid, targetToken),
                            RenderEventDef(module, h));
                }
            }
        }
        catch (BadImageFormatException) { /* fall through to placeholder */ }
        return ($"<{kind} 0x{targetToken:X8}>", $"<{kind} 0x{targetToken:X8}>");
    }

    private static AttributeIndexData BuildAttributeIndex(ModuleHandle module, CancellationToken cancellationToken)
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

    private static string? TryReadAttributeTypeFullName(ModuleHandle module, CustomAttributeHandle handle)
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

}
