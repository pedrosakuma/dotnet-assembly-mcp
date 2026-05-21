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
