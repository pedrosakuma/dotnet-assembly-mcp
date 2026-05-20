using System.Collections.Immutable;
using System.Reflection.Metadata;

namespace DotnetAssemblyMcp.Core.Metadata;

/// <summary>
/// Thrown by <see cref="StringCustomAttributeTypeProvider"/> when the metadata reader asks
/// for a type ('Type'-typed argument) that doesn't resolve cleanly. The decoder catches it
/// so a single malformed argument doesn't poison the whole attribute walk.
/// </summary>
internal sealed class UnknownTypeException(string message) : Exception(message);

/// <summary>
/// Minimal <see cref="ICustomAttributeTypeProvider{TType}"/> producing readable strings.
/// Mirrors <see cref="StringSignatureProvider"/> but answers the custom-attribute decoder's
/// type-rendering questions (no signature decoding involved).
/// </summary>
internal sealed class StringCustomAttributeTypeProvider : ICustomAttributeTypeProvider<string>
{
    private readonly MetadataReader _md;
    public StringCustomAttributeTypeProvider(MetadataReader md) => _md = md;

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

    public string GetSystemType() => "System.Type";
    public string GetSZArrayType(string elementType) => elementType + "[]";

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

    public string GetTypeFromSerializedName(string name)
    {
        // 'name' is an assembly-qualified type name as it appears in the CustomAttribute blob
        // for typeof(...) arguments. Strip the assembly portion so the value stays consistent
        // with how we render other type references.
        var comma = name.IndexOf(',');
        return comma < 0 ? name : name[..comma].Trim();
    }

    public PrimitiveTypeCode GetUnderlyingEnumType(string type)
    {
        // For most framework enums this is Int32. We don't have a resolver to walk arbitrary
        // user-defined enum types, so fall back to Int32 — the decoder will then surface the
        // raw integer value, which is what consumers actually want.
        return PrimitiveTypeCode.Int32;
    }

    public bool IsSystemType(string type) =>
        type is "System.Type" || type.EndsWith(".Type", StringComparison.Ordinal);
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

/// <summary>
/// Signature provider used by XrefIndex to collect every TypeDef / TypeRef handle reachable
/// from a signature (method, field, property, local). The provider's return type is a dummy
/// unit value; the side-effect is accumulation into an internal sink that the caller drains
/// with <see cref="Drain"/> between uses. <see cref="Reset"/> clears the sink so a single
/// instance can be reused across many signatures.
/// </summary>
internal sealed class TypeTokenCollectorProvider : ISignatureTypeProvider<TypeTokenCollectorProvider.Unit, object?>
{
    public readonly record struct Unit;

    private readonly MetadataReader _md;
    private readonly HashSet<EntityHandle> _seen = new();
    private readonly List<EntityHandle> _ordered = new();

    public TypeTokenCollectorProvider(MetadataReader md) => _md = md;

    public void Reset() { _seen.Clear(); _ordered.Clear(); }

    public IReadOnlyList<EntityHandle> Drain()
    {
        // Caller must consume the snapshot before resetting; we return the live list because
        // the typical caller iterates synchronously and discards the provider afterwards.
        return _ordered;
    }

    private Unit Add(EntityHandle h)
    {
        if (_seen.Add(h)) _ordered.Add(h);
        return default;
    }

    public Unit GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => Add(handle);
    public Unit GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => Add(handle);
    public Unit GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
    {
        // Recurse into the spec so generic args + outer type both surface, but don't record
        // the TypeSpec handle itself — only the leaf TypeDef/TypeRef handles are useful.
        try { reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext); }
        catch (BadImageFormatException) { /* skip */ }
        return default;
    }

    public Unit GetPrimitiveType(PrimitiveTypeCode typeCode) => default;
    public Unit GetSZArrayType(Unit elementType) => default;
    public Unit GetArrayType(Unit elementType, ArrayShape shape) => default;
    public Unit GetByReferenceType(Unit elementType) => default;
    public Unit GetPointerType(Unit elementType) => default;
    public Unit GetPinnedType(Unit elementType) => default;
    public Unit GetGenericInstantiation(Unit genericType, ImmutableArray<Unit> typeArguments) => default;
    public Unit GetGenericMethodParameter(object? genericContext, int index) => default;
    public Unit GetGenericTypeParameter(object? genericContext, int index) => default;
    public Unit GetModifiedType(Unit modifier, Unit unmodifiedType, bool isRequired) => default;
    public Unit GetFunctionPointerType(MethodSignature<Unit> signature) => default;
}
