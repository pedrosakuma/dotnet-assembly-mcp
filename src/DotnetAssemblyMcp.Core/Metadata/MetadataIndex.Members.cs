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
}
