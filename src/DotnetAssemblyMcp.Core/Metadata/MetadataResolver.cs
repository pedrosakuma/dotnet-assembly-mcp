using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using HandleKind = System.Reflection.Metadata.HandleKind;

namespace DotnetAssemblyMcp.Core.Metadata;

/// <summary>
/// Same-/cross-module entity resolution helpers extracted from <see cref="MetadataIndex"/>
/// (#82). Shared by XrefIndex, FieldAccessIndex and AttributeIndex — every consumer that has
/// to walk a <see cref="MemberReference"/>'s parent or decode a TypeRef chain.
/// </summary>
internal static class MetadataResolver
{
    /// <summary>
    /// Resolves a MemberRef Parent to a TypeDefinitionHandle in the current module, if any.
    /// Returns null when the parent points outside this assembly (handled as outbound) or
    /// cannot be resolved.
    /// </summary>
    public static TypeDefinitionHandle? ResolveLocalParentType(ModuleHandle module, EntityHandle parent)
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

    public static TypeDefinitionHandle? FindTypeDefByName(ModuleHandle module, TypeReference tr)
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

    public static int? TryFindLocalMethod(ModuleHandle module, TypeDefinitionHandle parentType,
        MemberReference mr)
    {
        var methodName = module.MD.GetString(mr.Name);
        MethodSignature<string> mrSig;
        try
        {
            var decoder = new SignatureDecoder<string, object?>(
                StringSignatureProvider.WithoutModifiers(module.MD), module.MD, genericContext: null);
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
                    StringSignatureProvider.WithoutModifiers(module.MD), module.MD, genericContext: null);
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

    public static int? TryFindLocalField(ModuleHandle module, TypeDefinitionHandle parentType, MemberReference mr)
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

    public static string? ResolveOutboundTypeName(ModuleHandle module, EntityHandle parent,
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
                            var handle = EntityHandleFromCoded(encoded);
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

    public static EntityHandle EntityHandleFromCoded(int codedToken)
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

    public static string? ResolveTypeRefName(ModuleHandle module, TypeReferenceHandle trh,
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
}
