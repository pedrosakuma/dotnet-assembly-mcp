namespace DotnetAssemblyMcp.Core.Decompilation;

/// <summary>
/// Result of <see cref="IDecompiler.Decompile"/>: the C# source for the requested method
/// plus a tiny <see cref="DecompiledMethod.Source"/> body. Designed to be the typed payload
/// of a <c>decompile_method</c> tool response.
/// </summary>
public sealed record DecompiledMethod(
    Guid ModuleVersionId,
    int MetadataToken,
    string Handle,
    string TypeFullName,
    string MethodName,
    string Source,
    int SourceLengthChars,
    bool Truncated,
    bool CacheHit);
