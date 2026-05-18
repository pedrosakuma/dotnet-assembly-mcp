namespace DotnetAssemblyMcp.Core.Metadata;

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
    IReadOnlyList<string> Attributes);
