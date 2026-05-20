using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using DotnetAssemblyMcp.Core.Handles;
using DotnetAssemblyMcp.Core.Identity;
using HandleKind = System.Reflection.Metadata.HandleKind;

namespace DotnetAssemblyMcp.Core.Metadata;

/// <summary>
/// Shared metadata-rendering helpers. Extracted from <see cref="MetadataIndex"/> (#82) so the
/// per-index classes (XrefIndex, StringIndex, AttributeIndex, FieldAccessIndex) can reuse the
/// same display strings without forming a circular dependency on the façade.
/// </summary>
internal static class MetadataDisplay
{
    public static string RenderMethodDef(ModuleHandle m, MethodDefinitionHandle h)
    {
        var def = m.MD.GetMethodDefinition(h);
        var type = m.MD.GetTypeDefinition(def.GetDeclaringType());
        return $"{TypeName(m, type)}.{m.MD.GetString(def.Name)}";
    }

    public static string RenderMemberRef(ModuleHandle m, MemberReferenceHandle h)
    {
        var r = m.MD.GetMemberReference(h);
        var parent = RenderParent(m, r.Parent);
        return $"{parent}.{m.MD.GetString(r.Name)}";
    }

    public static string RenderMethodSpec(ModuleHandle m, MethodSpecificationHandle h)
    {
        var spec = m.MD.GetMethodSpecification(h);
        return BuildSymbolRef(m, MetadataTokens.GetToken(spec.Method)).Display + "<…>";
    }

    public static string RenderFieldDef(ModuleHandle m, FieldDefinitionHandle h)
    {
        var f = m.MD.GetFieldDefinition(h);
        var type = m.MD.GetTypeDefinition(f.GetDeclaringType());
        return $"{TypeName(m, type)}.{m.MD.GetString(f.Name)}";
    }

    public static string RenderTypeDef(ModuleHandle m, TypeDefinitionHandle h) =>
        TypeName(m, m.MD.GetTypeDefinition(h));

    public static string RenderTypeRef(ModuleHandle m, TypeReferenceHandle h)
    {
        var r = m.MD.GetTypeReference(h);
        var ns = m.MD.GetString(r.Namespace);
        var n = m.MD.GetString(r.Name);
        return string.IsNullOrEmpty(ns) ? n : $"{ns}.{n}";
    }

    public static string RenderTypeSpec(ModuleHandle m, TypeSpecificationHandle h)
    {
        try
        {
            return m.MD.GetTypeSpecification(h)
                .DecodeSignature(new StringSignatureProvider(m.MD), genericContext: null);
        }
        catch (BadImageFormatException) { return IlSymbolRef.UnresolvedDisplay; }
    }

    public static string RenderParent(ModuleHandle m, EntityHandle parent) => parent.Kind switch
    {
        HandleKind.TypeReference => RenderTypeRef(m, (TypeReferenceHandle)parent),
        HandleKind.TypeDefinition => RenderTypeDef(m, (TypeDefinitionHandle)parent),
        HandleKind.TypeSpecification => RenderTypeSpec(m, (TypeSpecificationHandle)parent),
        _ => IlSymbolRef.UnresolvedDisplay,
    };

    public static string RenderPropertyDef(ModuleHandle module, PropertyDefinitionHandle h)
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

    public static string RenderEventDef(ModuleHandle module, EventDefinitionHandle h)
    {
        try
        {
            var e = module.MD.GetEventDefinition(h);
            return module.MD.GetString(e.Name);
        }
        catch (BadImageFormatException) { return $"<event 0x{MetadataTokens.GetToken(h):X8}>"; }
    }

    public static string TypeName(ModuleHandle m, TypeDefinition t)
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

    public static string? TryReadUserString(ModuleHandle m, int token)
    {
        try
        {
            var h = MetadataTokens.UserStringHandle(token & 0x00FFFFFF);
            return m.MD.GetUserString(h);
        }
        catch (BadImageFormatException) { return null; }
        catch (ArgumentException) { return null; }
    }

    public static IlSymbolRef BuildSymbolRef(ModuleHandle m, int token)
    {
        // Pick the handle prefix that matches the token's metadata table so consumers can feed
        // the returned handle straight back into the corresponding `find_*_references` /
        // `get_method` tool without re-parsing. Pre-#80 every entry was 'm:' prefixed
        // regardless of kind — fields and types could never round-trip.
        string handleStr;
        string display;
        try
        {
            var h = MetadataTokens.Handle(token);
            (handleStr, display) = h.Kind switch
            {
                HandleKind.MethodDefinition =>
                    (HandleSyntax.FormatMethod(m.Mvid, token), RenderMethodDef(m, (MethodDefinitionHandle)h)),
                HandleKind.MemberReference =>
                    // MemberRefs (tokens in table 0x0A) have no first-class wire prefix —
                    // 'm:' formally addresses MethodDef (table 0x06) and 'f:' addresses FieldDef
                    // (table 0x04). We emit a synthetic 'm:' as a stable token-carrier so
                    // existing consumers keep working; precise round-trip into find_*_references
                    // requires the Token field, not the Handle string. See follow-up to #80.
                    (HandleSyntax.FormatMethod(m.Mvid, token), RenderMemberRef(m, (MemberReferenceHandle)h)),
                HandleKind.MethodSpecification =>
                    (HandleSyntax.FormatMethod(m.Mvid, token), RenderMethodSpec(m, (MethodSpecificationHandle)h)),
                HandleKind.FieldDefinition =>
                    (HandleSyntax.FormatField(m.Mvid, token), RenderFieldDef(m, (FieldDefinitionHandle)h)),
                HandleKind.TypeDefinition =>
                    (HandleSyntax.FormatType(m.Mvid, token), RenderTypeDef(m, (TypeDefinitionHandle)h)),
                HandleKind.TypeReference =>
                    (HandleSyntax.FormatType(m.Mvid, token), RenderTypeRef(m, (TypeReferenceHandle)h)),
                HandleKind.TypeSpecification =>
                    (HandleSyntax.FormatType(m.Mvid, token), RenderTypeSpec(m, (TypeSpecificationHandle)h)),
                _ => (HandleSyntax.FormatMethod(m.Mvid, token), IlSymbolRef.UnresolvedDisplay),
            };
        }
        catch (BadImageFormatException)
        {
            handleStr = HandleSyntax.FormatMethod(m.Mvid, token);
            display = IlSymbolRef.UnresolvedDisplay;
        }
        catch (InvalidCastException)
        {
            handleStr = HandleSyntax.FormatMethod(m.Mvid, token);
            display = IlSymbolRef.UnresolvedDisplay;
        }
        return new IlSymbolRef(token, handleStr, display);
    }

    public static MethodIdentity BuildMethodIdentity(ModuleHandle module, MethodDefinitionHandle h) =>
        new(module.Mvid, MetadataTokens.GetToken(h));
}
