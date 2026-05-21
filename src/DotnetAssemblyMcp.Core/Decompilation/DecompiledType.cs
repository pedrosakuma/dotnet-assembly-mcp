namespace DotnetAssemblyMcp.Core.Decompilation;

/// <summary>
/// Result of <see cref="IDecompiler.DecompileType"/>: the C# source for a whole type
/// (members in declaration order, nested types, attributes, properties, events).
/// </summary>
public sealed record DecompiledType(
    Guid ModuleVersionId,
    int MetadataToken,
    string Handle,
    string TypeFullName,
    string Source,
    int SourceLengthChars,
    bool Truncated,
    bool CacheHit);
