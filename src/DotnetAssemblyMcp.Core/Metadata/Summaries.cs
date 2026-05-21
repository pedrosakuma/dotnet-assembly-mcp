namespace DotnetAssemblyMcp.Core.Metadata;

using DotnetAssemblyMcp.Core;

/// <summary>
/// Compact summary of a loaded module suitable for <c>list_assemblies</c>. Contains only
/// Tier-1 (metadata-only) data; full PE parsing happens lazily inside <see cref="IMetadataIndex"/>.
/// </summary>
public sealed record ModuleSummary(
    Guid ModuleVersionId,
    string ModuleName,
    string ModulePath,
    int MethodCount);

/// <summary>
/// Structural summary of a single method. Returned by <c>get_method</c> after resolving a
/// <see cref="Identity.MethodIdentity"/>.
/// </summary>
public sealed record MethodSummary(
    Guid ModuleVersionId,
    int MetadataToken,
    string Handle,
    string TypeFullName,
    string MethodName,
    string Signature,
    int IlSize,
    int GenericArity,
    IReadOnlyList<string> Attributes,
    NativeBodyRef? NativeBody = null,
    PInvokeInfo? PInvoke = null,
    IReadOnlyList<GenericParameterSummary>? GenericParameters = null)
{
    // Binary-compat overload: preserves the 10-arg constructor signature that shipped before
    // the PInvoke discriminator was added (issue #104). Existing compiled consumers calling
    // MethodSummary(..., NativeBodyRef?) keep working without recompilation.
    public MethodSummary(
        Guid moduleVersionId,
        int metadataToken,
        string handle,
        string typeFullName,
        string methodName,
        string signature,
        int ilSize,
        int genericArity,
        IReadOnlyList<string> attributes,
        NativeBodyRef? nativeBody)
        : this(moduleVersionId, metadataToken, handle, typeFullName, methodName, signature,
               ilSize, genericArity, attributes, nativeBody, PInvoke: null, GenericParameters: null) { }

    // Binary-compat overload: preserves the 10-out Deconstruct signature that records
    // synthesise from a 10-param primary constructor (issue #104). Existing consumers using
    // the pre-PInvoke deconstruction keep working without recompilation.
    public void Deconstruct(
        out Guid moduleVersionId,
        out int metadataToken,
        out string handle,
        out string typeFullName,
        out string methodName,
        out string signature,
        out int ilSize,
        out int genericArity,
        out IReadOnlyList<string> attributes,
        out NativeBodyRef? nativeBody)
    {
        moduleVersionId = ModuleVersionId;
        metadataToken = MetadataToken;
        handle = Handle;
        typeFullName = TypeFullName;
        methodName = MethodName;
        signature = Signature;
        ilSize = IlSize;
        genericArity = GenericArity;
        attributes = Attributes;
        nativeBody = NativeBody;
    }
}

/// <summary>
/// Decoded PInvoke binding for methods carrying <see cref="System.Reflection.MethodAttributes.PinvokeImpl"/>.
/// Surfaced on <see cref="MethodSummary.PInvoke"/> so interop audits do not need to call
/// <c>decompile_method</c>. Populated from <see cref="System.Reflection.Metadata.MethodImport"/>
/// plus the method's <see cref="System.Reflection.MethodImplAttributes"/>.
/// </summary>
public sealed record PInvokeInfo(
    string ModuleName,
    string EntryPoint,
    string CharSet,
    string CallingConvention,
    bool ExactSpelling = false,
    bool SetLastError = false,
    bool PreserveSig = false,
    bool? BestFitMapping = null,
    bool? ThrowOnUnmappableChar = null);

/// <summary>
/// Coarse-grained kind of a type definition. Mirrors the buckets a user-facing client cares
/// about; computed from <see cref="System.Reflection.TypeAttributes"/> + base-type heuristics
/// rather than reflected raw attributes so the tool surface stays stable.
/// </summary>
public enum TypeKind
{
    Class,
    Struct,
    Interface,
    Enum,
    Delegate,
}

/// <summary>
/// Lightweight reference to another type — used by <see cref="TypeSummary.BaseType"/> and
/// <see cref="TypeSummary.Interfaces"/>. <see cref="AssemblyName"/> is the simple name of
/// the assembly that owns the referenced type and is null when the reference resolves to a
/// type defined in the same module.
/// </summary>
public sealed record TypeReferenceSummary(
    string FullName,
    string? AssemblyName = null);

/// <summary>
/// Per-generic-parameter constraint summary (#103). Surfaces what a caller would write as
/// <c>where T : class, IDisposable, new()</c> without forcing a decompile. <see cref="Variance"/>
/// is decoded from <see cref="System.Reflection.GenericParameterAttributes.VarianceMask"/>;
/// the boolean flags decode <see cref="System.Reflection.GenericParameterAttributes.SpecialConstraintMask"/>;
/// <see cref="TypeConstraints"/> carries the base-type / interface constraints from the
/// <c>GenericParamConstraint</c> table.
/// </summary>
public sealed record GenericParameterSummary(
    int Index,
    string Name,
    string Variance,
    bool IsReferenceType = false,
    bool IsValueType = false,
    bool HasDefaultConstructor = false,
    IReadOnlyList<TypeReferenceSummary>? TypeConstraints = null);

/// <summary>
/// Tier-1 summary of a type definition. Returned by <c>list_types</c>.
/// </summary>
public sealed record TypeSummary(
    Guid ModuleVersionId,
    int MetadataToken,
    string Handle,
    string FullName,
    TypeKind Kind,
    int MethodCount,
    bool IsPublic,
    TypeReferenceSummary? BaseType = null,
    IReadOnlyList<TypeReferenceSummary>? Interfaces = null,
    IReadOnlyList<string>? Instantiation = null,
    IReadOnlyList<GenericParameterSummary>? GenericParameters = null)
{
    // Binary-compat overload: preserves the 10-arg constructor signature that shipped before
    // the GenericParameters field was added (issue #103). Mirrors the IlSymbolRef / MethodSummary
    // pattern from #86 / #104.
    public TypeSummary(
        Guid moduleVersionId,
        int metadataToken,
        string handle,
        string fullName,
        TypeKind kind,
        int methodCount,
        bool isPublic,
        TypeReferenceSummary? baseType,
        IReadOnlyList<TypeReferenceSummary>? interfaces,
        IReadOnlyList<string>? instantiation)
        : this(moduleVersionId, metadataToken, handle, fullName, kind, methodCount, isPublic,
               baseType, interfaces, instantiation, GenericParameters: null) { }

    // Binary-compat overload: preserves the 10-out Deconstruct signature synthesised from the
    // pre-#103 primary constructor.
    public void Deconstruct(
        out Guid moduleVersionId,
        out int metadataToken,
        out string handle,
        out string fullName,
        out TypeKind kind,
        out int methodCount,
        out bool isPublic,
        out TypeReferenceSummary? baseType,
        out IReadOnlyList<TypeReferenceSummary>? interfaces,
        out IReadOnlyList<string>? instantiation)
    {
        moduleVersionId = ModuleVersionId;
        metadataToken = MetadataToken;
        handle = Handle;
        fullName = FullName;
        kind = Kind;
        methodCount = MethodCount;
        isPublic = IsPublic;
        baseType = BaseType;
        interfaces = Interfaces;
        instantiation = Instantiation;
    }
}

/// <summary>
/// Filter / paging knobs accepted by <see cref="IMetadataIndex.ListTypes"/>. All fields are
/// optional; the defaults return up to <see cref="PageSize"/> non-synthetic types in metadata order.
/// </summary>
public sealed record ListTypesQuery(
    string? NamespacePrefix = null,
    string? NameContains = null,
    TypeKind? Kind = null,
    int? Cursor = null,
    int PageSize = ListTypesQuery.DefaultPageSize)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 500;
}

/// <summary>Paginated result of <see cref="IMetadataIndex.ListTypes"/>.</summary>
public sealed record ListTypesPage(
    Guid ModuleVersionId,
    IReadOnlyList<TypeSummary> Types,
    int? NextCursor = null,
    bool Truncated = false);

/// <summary>Result of <see cref="IMetadataIndex.ListTypes"/>.</summary>
public readonly record struct ListTypesResult(ListTypesPage? Page, AssemblyError? Error)
{
    public bool IsSuccess => Page is not null;
    public static ListTypesResult Ok(ListTypesPage p) => new(p, null);
    public static ListTypesResult Fail(AssemblyError e) => new(null, e);
}

/// <summary>
/// Filter / paging knobs accepted by <see cref="IMetadataIndex.ListMethods"/>. All fields
/// except the type identity are optional; the defaults return up to <see cref="PageSize"/>
/// methods of the type in metadata order.
/// </summary>
public sealed record ListMethodsQuery(
    string? NamePattern = null,
    int? Cursor = null,
    int PageSize = ListMethodsQuery.DefaultPageSize)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 500;
}

/// <summary>Paginated result of <see cref="IMetadataIndex.ListMethods"/>.</summary>
public sealed record ListMethodsPage(
    Guid ModuleVersionId,
    int TypeMetadataToken,
    string TypeFullName,
    IReadOnlyList<MethodSummary> Methods,
    int? NextCursor = null,
    bool Truncated = false);

/// <summary>Result of <see cref="IMetadataIndex.ListMethods"/>.</summary>
public readonly record struct ListMethodsResult(ListMethodsPage? Page, AssemblyError? Error)
{
    public bool IsSuccess => Page is not null;
    public static ListMethodsResult Ok(ListMethodsPage p) => new(p, null);
    public static ListMethodsResult Fail(AssemblyError e) => new(null, e);
}

/// <summary>
/// Filter / paging knobs accepted by <see cref="IMetadataIndex.FindMethod"/>. The name pattern
/// is treated as a regular expression matched against the method's short name (not the full
/// signature). <see cref="SignatureContains"/> applies a case-insensitive substring filter on
/// the decoded signature ('void NS.Type.Method(int)' format).
/// </summary>
public sealed record FindMethodQuery(
    string NamePattern,
    string? SignatureContains = null,
    int? Cursor = null,
    int PageSize = FindMethodQuery.DefaultPageSize)
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 200;
}

/// <summary>A single hit returned by <see cref="IMetadataIndex.FindMethod"/>.</summary>
public sealed record MethodMatch(
    Guid ModuleVersionId,
    int MetadataToken,
    string Handle,
    string TypeFullName,
    string MethodName,
    string Signature);

/// <summary>Paginated result of <see cref="IMetadataIndex.FindMethod"/>.</summary>
public sealed record FindMethodPage(
    Guid ModuleVersionId,
    string NamePattern,
    IReadOnlyList<MethodMatch> Matches,
    int? NextCursor = null,
    bool Truncated = false);

/// <summary>Result of <see cref="IMetadataIndex.FindMethod"/>.</summary>
public readonly record struct FindMethodResult(FindMethodPage? Page, AssemblyError? Error)
{
    public bool IsSuccess => Page is not null;
    public static FindMethodResult Ok(FindMethodPage p) => new(p, null);
    public static FindMethodResult Fail(AssemblyError e) => new(null, e);
}

/// <summary>
/// Filter / paging knobs accepted by <see cref="IMetadataIndex.ListDerivedTypes"/>. The
/// query walks every loaded module so subclasses and interface implementers defined in
/// other assemblies are returned alongside same-module hits (see issue #61).
/// </summary>
public sealed record ListDerivedTypesQuery(
    int? Cursor = null,
    int PageSize = ListDerivedTypesQuery.DefaultPageSize,
    bool DirectOnly = true,
    IReadOnlyList<GenericTypeName>? MatchInstantiation = null)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 500;
}

/// <summary>Paginated result of <see cref="IMetadataIndex.ListDerivedTypes"/>.</summary>
public sealed record ListDerivedTypesPage(
    Guid ModuleVersionId,
    int BaseTypeMetadataToken,
    string BaseTypeFullName,
    IReadOnlyList<TypeSummary> Types,
    int? NextCursor = null,
    bool Truncated = false);

/// <summary>Result of <see cref="IMetadataIndex.ListDerivedTypes"/>.</summary>
public readonly record struct ListDerivedTypesResult(ListDerivedTypesPage? Page, AssemblyError? Error)
{
    public bool IsSuccess => Page is not null;
    public static ListDerivedTypesResult Ok(ListDerivedTypesPage p) => new(p, null);
    public static ListDerivedTypesResult Fail(AssemblyError e) => new(null, e);
}

/// <summary>Result of <see cref="IMetadataIndex.GetTypeDefinition"/>.</summary>
public readonly record struct GetTypeResult(TypeSummary? Type, AssemblyError? Error)
{
    public bool IsSuccess => Type is not null;
    public static GetTypeResult Ok(TypeSummary t) => new(t, null);
    public static GetTypeResult Fail(AssemblyError e) => new(null, e);
}
