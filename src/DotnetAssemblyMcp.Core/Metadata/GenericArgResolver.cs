using System.Collections.Immutable;
using System.Reflection.Metadata;
using DotnetAssemblyMcp.Core.Errors;

namespace DotnetAssemblyMcp.Core.Metadata;

/// <summary>
/// Resolves canonical type-argument names from the handoff wire format (see
/// <c>docs/handoff-contract.md §3.5</c>) into their declaring module + TypeDef/TypeRef handle,
/// and emits substituted signature strings for closed views of generic methods.
/// </summary>
/// <remarks>
/// Resolution order for each <see cref="GenericTypeName.Named"/> leaf:
/// <list type="number">
///   <item>The module that owns the open MethodDef being resolved.</item>
///   <item>Every other loaded module in MVID order.</item>
/// </list>
/// Multiple matches with conflicting MVIDs ⇒ <see cref="ErrorKinds.GenericInstantiationAmbiguous"/>.
/// No match in any loaded module ⇒ <see cref="ErrorKinds.GenericInstantiationUnresolvable"/>.
/// </remarks>
internal static class GenericArgResolver
{
    /// <summary>One resolved type-arg leaf: the module it was found in and the TypeDef handle.</summary>
    public readonly record struct Resolved(Guid Mvid, int TypeDefToken, string DisplayName);

    /// <summary>
    /// Validates that every <see cref="GenericTypeName.Named"/> leaf inside <paramref name="args"/>
    /// resolves in some loaded module. Returns the verbatim list of rendered strings, or an error
    /// on the first unresolvable / ambiguous leaf.
    /// </summary>
    public static (IReadOnlyList<string>? Rendered, AssemblyError? Error) RenderAndValidate(
        IReadOnlyList<GenericTypeName> args,
        Guid ownerMvid,
        IReadOnlyDictionary<Guid, Func<MetadataReader>> readers)
    {
        var rendered = new string[args.Count];
        for (int i = 0; i < args.Count; i++)
        {
            var err = ValidateTree(args[i], ownerMvid, readers);
            if (err is not null) return (null, err);
            rendered[i] = args[i].Format();
        }
        return (rendered, null);
    }

    private static AssemblyError? ValidateTree(
        GenericTypeName node, Guid ownerMvid, IReadOnlyDictionary<Guid, Func<MetadataReader>> readers)
    {
        switch (node)
        {
            case GenericTypeName.Named named:
                var err = ValidateNamed(named, ownerMvid, readers);
                if (err is not null) return err;
                foreach (var arg in named.TypeArguments)
                {
                    var inner = ValidateTree(arg, ownerMvid, readers);
                    if (inner is not null) return inner;
                }
                return null;
            case GenericTypeName.SzArray sz: return ValidateTree(sz.Element, ownerMvid, readers);
            case GenericTypeName.MdArray md: return ValidateTree(md.Element, ownerMvid, readers);
            case GenericTypeName.ByRefType br: return ValidateTree(br.Element, ownerMvid, readers);
            case GenericTypeName.PointerType pt: return ValidateTree(pt.Element, ownerMvid, readers);
            default: return null;
        }
    }

    /// <summary>
    /// CLR full names of BCL primitives and a handful of well-known reference types that the
    /// resolver accepts as "always resolvable" without requiring the BCL to be loaded as a
    /// module. These appear in metadata as <see cref="PrimitiveTypeCode"/> or well-known
    /// TypeRefs that are resolved against the runtime, not the user's manifest.
    /// </summary>
    private static readonly HashSet<string> WellKnownNames = new(StringComparer.Ordinal)
    {
        "System.Void", "System.Boolean", "System.Byte", "System.SByte",
        "System.Char", "System.Int16", "System.UInt16",
        "System.Int32", "System.UInt32", "System.Int64", "System.UInt64",
        "System.Single", "System.Double", "System.String", "System.Object",
        "System.IntPtr", "System.UIntPtr", "System.TypedReference",
        "System.Guid", "System.DateTime", "System.TimeSpan", "System.Decimal",
    };

    private static AssemblyError? ValidateNamed(
        GenericTypeName.Named named, Guid ownerMvid, IReadOnlyDictionary<Guid, Func<MetadataReader>> readers)
    {
        var fullName = named.ClrFullName;

        // BCL primitives are always resolvable — they're a PrimitiveTypeCode in metadata, not a TypeDef
        // the consumer needs to have loaded. Short-circuit so callers don't need to import corelib.
        if (WellKnownNames.Contains(fullName)) return null;

        var hits = new List<Guid>();

        // Owner first.
        if (readers.TryGetValue(ownerMvid, out var ownerFactory) && ContainsTypeDef(ownerFactory(), fullName))
            hits.Add(ownerMvid);

        foreach (var (mvid, factory) in readers)
        {
            if (mvid == ownerMvid) continue;
            if (ContainsTypeDef(factory(), fullName))
                hits.Add(mvid);
        }

        if (hits.Count == 0)
        {
            return new AssemblyError(
                ErrorKinds.GenericInstantiationUnresolvable,
                $"type '{fullName}' did not resolve in any loaded module. Import the manifest for the dependency or supply assemblyPathHint, then retry.");
        }
        if (hits.Count > 1)
        {
            var mvids = string.Join(", ", hits.Select(m => m.ToString("D")));
            return new AssemblyError(
                ErrorKinds.GenericInstantiationAmbiguous,
                $"type '{fullName}' resolved in {hits.Count} modules with conflicting MVIDs: {mvids}. Narrow the manifest or qualify the producer payload.");
        }
        return null;
    }

    /// <summary>
    /// Walks the module's TypeDef table looking for a name match against the canonical
    /// CLR full name (namespace.Name+Nested+…). Linear scan; cheap for the small fixtures
    /// we ship and acceptable for production modules (TypeDef counts are bounded).
    /// </summary>
    private static bool ContainsTypeDef(MetadataReader md, string clrFullName)
    {
        foreach (var th in md.TypeDefinitions)
        {
            var t = md.GetTypeDefinition(th);
            if (BuildFullName(md, t) == clrFullName) return true;
        }
        return false;
    }

    private static string BuildFullName(MetadataReader md, TypeDefinition td)
    {
        var ns = md.GetString(td.Namespace);
        var chain = new List<string> { md.GetString(td.Name) };
        var current = td;
        while (current.IsNested)
        {
            var enclosingHandle = current.GetDeclaringType();
            current = md.GetTypeDefinition(enclosingHandle);
            chain.Insert(0, md.GetString(current.Name));
            ns = md.GetString(current.Namespace);
        }
        var nameChain = string.Join("+", chain);
        return string.IsNullOrEmpty(ns) ? nameChain : $"{ns}.{nameChain}";
    }
}

/// <summary>
/// Variant of <see cref="StringSignatureProvider"/> that substitutes generic-type and generic-method
/// parameters using a closed context of pre-rendered type-arg strings (canonical wire form). Used
/// by <c>get_method</c> / <c>decompile_method</c> / <c>find_callers</c> when the caller supplies
/// <c>genericTypeArguments</c> per §3.5 to produce a closed signature view.
/// </summary>
internal sealed class SubstitutingStringSignatureProvider : System.Reflection.Metadata.ISignatureTypeProvider<string, object?>
{
    private readonly MetadataReader _md;
    private readonly ImmutableArray<string> _typeArgs;
    private readonly ImmutableArray<string> _methodArgs;

    public SubstitutingStringSignatureProvider(MetadataReader md, IReadOnlyList<string> typeArgs, IReadOnlyList<string> methodArgs)
    {
        _md = md;
        _typeArgs = typeArgs.ToImmutableArray();
        _methodArgs = methodArgs.ToImmutableArray();
    }

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

    public string GetGenericMethodParameter(object? genericContext, int index) =>
        index < _methodArgs.Length ? _methodArgs[index] : $"!!{index}";

    public string GetGenericTypeParameter(object? genericContext, int index) =>
        index < _typeArgs.Length ? _typeArgs[index] : $"!{index}";

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
/// Signature provider emitting CLR reflection-style FullName strings that match the wire
/// format defined by <see cref="GenericTypeName.Format()"/> (see docs/handoff-contract.md §3.5).
/// Used for comparing decoded MethodSpec/TypeSpec blobs against the strings produced by
/// <see cref="GenericArgResolver.RenderAndValidate"/>.
/// </summary>
public sealed class WireFormatSignatureProvider : System.Reflection.Metadata.ISignatureTypeProvider<string, object?>
{
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.Boolean => "System.Boolean",
        PrimitiveTypeCode.Byte => "System.Byte",
        PrimitiveTypeCode.SByte => "System.SByte",
        PrimitiveTypeCode.Char => "System.Char",
        PrimitiveTypeCode.Int16 => "System.Int16",
        PrimitiveTypeCode.UInt16 => "System.UInt16",
        PrimitiveTypeCode.Int32 => "System.Int32",
        PrimitiveTypeCode.UInt32 => "System.UInt32",
        PrimitiveTypeCode.Int64 => "System.Int64",
        PrimitiveTypeCode.UInt64 => "System.UInt64",
        PrimitiveTypeCode.Single => "System.Single",
        PrimitiveTypeCode.Double => "System.Double",
        PrimitiveTypeCode.String => "System.String",
        PrimitiveTypeCode.Object => "System.Object",
        PrimitiveTypeCode.Void => "System.Void",
        PrimitiveTypeCode.IntPtr => "System.IntPtr",
        PrimitiveTypeCode.UIntPtr => "System.UIntPtr",
        PrimitiveTypeCode.TypedReference => "System.TypedReference",
        _ => typeCode.ToString(),
    };

    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetArrayType(string elementType, ArrayShape shape)
    {
        if (shape.Rank == 1 && shape.LowerBounds.Length > 0 && shape.LowerBounds[0] != 0)
            return elementType + "[*]";
        return elementType + "[" + new string(',', Math.Max(0, shape.Rank - 1)) + "]";
    }
    public string GetByReferenceType(string elementType) => elementType + "&";
    public string GetPointerType(string elementType) => elementType + "*";
    public string GetPinnedType(string elementType) => elementType;
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
        genericType + "[" + string.Join(",", typeArguments) + "]";
    public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
    public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
    public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        => BuildClrFullName(reader, reader.GetTypeDefinition(handle));

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var t = reader.GetTypeReference(handle);
        var n = reader.GetString(t.Name);
        // Walk the ResolutionScope chain for nested TypeRefs to prepend "Outer+".
        if (t.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            var outer = GetTypeFromReference(reader, (TypeReferenceHandle)t.ResolutionScope, rawTypeKind);
            return outer + "+" + n;
        }
        var ns = reader.GetString(t.Namespace);
        return string.IsNullOrEmpty(ns) ? n : ns + "." + n;
    }

    public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    private static string BuildClrFullName(MetadataReader md, TypeDefinition td)
    {
        var chain = new List<string> { md.GetString(td.Name) };
        var ns = md.GetString(td.Namespace);
        var current = td;
        while (current.IsNested)
        {
            var enclosingHandle = current.GetDeclaringType();
            current = md.GetTypeDefinition(enclosingHandle);
            chain.Insert(0, md.GetString(current.Name));
            ns = md.GetString(current.Namespace);
        }
        var nameChain = string.Join("+", chain);
        return string.IsNullOrEmpty(ns) ? nameChain : ns + "." + nameChain;
    }
}
