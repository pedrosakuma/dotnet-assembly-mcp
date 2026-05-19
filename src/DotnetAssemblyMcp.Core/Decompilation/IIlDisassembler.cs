using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Identity;

namespace DotnetAssemblyMcp.Core.Decompilation;

/// <summary>
/// Tier-3 IL-text surface: produces an ildasm-like textual dump for a single method on
/// demand and keeps a bounded LRU cache of results. Backed by
/// <c>ICSharpCode.Decompiler.Disassembler.ReflectionDisassembler</c>.
/// </summary>
/// <remarks>
/// Sits between <c>get_method_il</c> (raw hex bytes) and <c>decompile_method</c> (C# source):
/// preserves <c>tail.</c>/<c>volatile.</c>/<c>unaligned.</c> prefixes, exact <c>box</c>/<c>unbox.any</c>
/// placement, and <c>callvirt</c>-vs-<c>call</c> dispatch — information that decompilation
/// erases.
/// </remarks>
public interface IIlDisassembler
{
    /// <summary>
    /// Returns the IL text for the method identified by <paramref name="identity"/>.
    /// </summary>
    /// <param name="identity">A resolved method identity (typically returned by <c>get_method</c>).</param>
    /// <param name="maxLines">
    /// Hard upper bound on the number of output lines. Output longer than this is truncated
    /// with a <c>// ... truncated, N more instructions</c> marker and
    /// <see cref="MethodIlText.Truncated"/> is set. Pass 0 to use the implementation default.
    /// </param>
    /// <param name="cancellationToken">Cancels the call cooperatively. Forwarded to the disassembler.</param>
    DisassembleResult Disassemble(MethodIdentity identity, int maxLines = 0, CancellationToken cancellationToken = default);

    /// <summary>Number of entries currently cached. Exposed for diagnostics/tests.</summary>
    int CachedEntries { get; }
}

/// <summary>Result of <see cref="IIlDisassembler.Disassemble"/>.</summary>
public readonly record struct DisassembleResult(MethodIlText? Text, AssemblyError? Error)
{
    public bool IsSuccess => Text is not null;
    public static DisassembleResult Ok(MethodIlText t) => new(t, null);
    public static DisassembleResult Fail(AssemblyError e) => new(null, e);
}
