namespace DotnetAssemblyMcp.Core.Decompilation;

/// <summary>
/// Result of <see cref="IIlDisassembler.Disassemble"/>: an ildasm-style textual dump of a
/// single method's IL, plus shape metadata. Designed to be the typed payload of a
/// <c>get_method_il_text</c> tool response.
/// </summary>
public sealed record MethodIlText(
    Guid ModuleVersionId,
    int MetadataToken,
    string Handle,
    string TypeFullName,
    string MethodName,
    string Text,
    int LineCount,
    int InstructionCount,
    bool Truncated,
    bool CacheHit);
