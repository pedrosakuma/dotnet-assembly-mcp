using System.Reflection;
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
        //
        // The Kind discriminator (added in #86) is the source of truth for classification:
        // MemberReference (table 0x0A) handles can carry either a method or field signature,
        // and the wire grammar has no first-class prefix for either MemberRef flavor, so the
        // Handle string is a synthetic carrier and Kind tells the consumer what was actually
        // pointed at.
        string handleStr;
        string display;
        IlSymbolKind kind;
        try
        {
            var h = MetadataTokens.Handle(token);
            (handleStr, display, kind) = h.Kind switch
            {
                HandleKind.MethodDefinition =>
                    (HandleSyntax.FormatMethod(m.Mvid, token),
                     RenderMethodDef(m, (MethodDefinitionHandle)h),
                     IlSymbolKind.MethodDef),
                HandleKind.MemberReference =>
                    RenderMemberRefWithKind(m, (MemberReferenceHandle)h, token),
                HandleKind.MethodSpecification =>
                    (HandleSyntax.FormatMethod(m.Mvid, token),
                     RenderMethodSpec(m, (MethodSpecificationHandle)h),
                     IlSymbolKind.MethodSpec),
                HandleKind.FieldDefinition =>
                    (HandleSyntax.FormatField(m.Mvid, token),
                     RenderFieldDef(m, (FieldDefinitionHandle)h),
                     IlSymbolKind.FieldDef),
                HandleKind.TypeDefinition =>
                    (HandleSyntax.FormatType(m.Mvid, token),
                     RenderTypeDef(m, (TypeDefinitionHandle)h),
                     IlSymbolKind.TypeDef),
                HandleKind.TypeReference =>
                    (HandleSyntax.FormatType(m.Mvid, token),
                     RenderTypeRef(m, (TypeReferenceHandle)h),
                     IlSymbolKind.TypeRef),
                HandleKind.TypeSpecification =>
                    (HandleSyntax.FormatType(m.Mvid, token),
                     RenderTypeSpec(m, (TypeSpecificationHandle)h),
                     IlSymbolKind.TypeSpec),
                _ => (HandleSyntax.FormatMethod(m.Mvid, token), IlSymbolRef.UnresolvedDisplay, IlSymbolKind.Unknown),
            };
        }
        catch (BadImageFormatException)
        {
            handleStr = HandleSyntax.FormatMethod(m.Mvid, token);
            display = IlSymbolRef.UnresolvedDisplay;
            kind = IlSymbolKind.Unknown;
        }
        catch (InvalidCastException)
        {
            handleStr = HandleSyntax.FormatMethod(m.Mvid, token);
            display = IlSymbolRef.UnresolvedDisplay;
            kind = IlSymbolKind.Unknown;
        }
        return new IlSymbolRef(token, handleStr, display, kind);
    }

    // MemberReference rows (table 0x0A) carry either a method or a field signature; the
    // metadata reader exposes this via MemberReferenceKind. We use it to bucket the symbol
    // correctly downstream (issue #86 — pre-fix, field MemberRefs leaked into the calls bucket).
    private static (string Handle, string Display, IlSymbolKind Kind) RenderMemberRefWithKind(
        ModuleHandle m, MemberReferenceHandle h, int token)
    {
        var display = RenderMemberRef(m, h);
        IlSymbolKind kind;
        try
        {
            kind = m.MD.GetMemberReference(h).GetKind() switch
            {
                MemberReferenceKind.Field => IlSymbolKind.FieldMemberRef,
                MemberReferenceKind.Method => IlSymbolKind.MethodMemberRef,
                _ => IlSymbolKind.Unknown,
            };
        }
        catch (BadImageFormatException)
        {
            kind = IlSymbolKind.Unknown;
        }

        // The wire grammar formally addresses MethodDef (m:) and FieldDef (f:) — neither
        // strictly covers a MemberRef token. We keep the m: carrier shape for backward
        // compatibility (pre-#86 every MemberRef shipped as m:); Kind is the source of truth
        // for consumers that need to distinguish method MemberRefs from field MemberRefs and
        // for tooling that may later introduce a dedicated MemberRef wire prefix.
        return (HandleSyntax.FormatMethod(m.Mvid, token), display, kind);
    }

    public static MethodIdentity BuildMethodIdentity(ModuleHandle module, MethodDefinitionHandle h) =>
        new(module.Mvid, MetadataTokens.GetToken(h));

    public static MethodSummary SummarizeMethod(ModuleHandle m, MethodDefinitionHandle h, int token)
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
        var handle = HandleSyntax.FormatMethod(m.Mvid, token);

        return new MethodSummary(
            m.Mvid, token, handle, fullType, methodName, signature,
            ilSize, def.GetGenericParameters().Count, attrs);
    }

    public static List<string> FormatAttributes(MethodAttributes a)
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
}
