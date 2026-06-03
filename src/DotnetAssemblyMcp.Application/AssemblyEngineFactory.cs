using DotnetAssemblyMcp.Core.Decompilation;
using DotnetAssemblyMcp.Core.Metadata;

namespace DotnetAssemblyMcp.Application;

/// <summary>
/// A coherent set of Core engine services — the metadata index plus the decompiler and IL
/// disassembler that compose on top of it. Both the MCP <c>Server</c> and the <c>Cli</c>
/// front-end construct one of these via <see cref="AssemblyEngineFactory"/> so the two hosts
/// never drift in how they wire Core together.
/// </summary>
public sealed record AssemblyEngine(
    IMetadataIndex Index,
    IDecompiler Decompiler,
    IIlDisassembler Disassembler);

/// <summary>
/// Single construction site for the Core engine. Keeps the <c>(MetadataIndex, Decompiler,
/// IlDisassembler)</c> wiring in one place shared by every host.
/// </summary>
public static class AssemblyEngineFactory
{
    /// <summary>
    /// Builds an <see cref="AssemblyEngine"/>. <paramref name="watchForChanges"/> installs the
    /// per-directory file watchers that reload modules on disk changes — appropriate for a
    /// long-lived server, and harmless (just unused) for a one-shot CLI invocation.
    /// </summary>
    public static AssemblyEngine Create(bool watchForChanges = false)
    {
        var index = new MetadataIndex(watchForChanges);
        return new AssemblyEngine(index, new Decompiler(index), new IlDisassembler(index));
    }
}
