using DotnetAssemblyMcp.Core.Identity;

namespace DotnetAssemblyMcp.Core.Decompilation;

/// <summary>
/// Tier-3 decompilation surface: produces C# source for a single method on demand and
/// keeps a bounded LRU cache of results. Backed by ICSharpCode.Decompiler.
/// </summary>
/// <remarks>
/// Tier 3 from <c>docs/mcp-conventions.md §2.1</c>: heavy, cached, eviction-bounded. Output
/// is hard-capped via <c>maxChars</c> so a runaway decompilation can never blow the MCP
/// response envelope.
/// </remarks>
public interface IDecompiler
{
    /// <summary>
    /// Returns the C# source for the method identified by <paramref name="identity"/>.
    /// </summary>
    /// <param name="identity">A resolved method identity (typically returned by <c>get_method</c>).</param>
    /// <param name="maxChars">
    /// Hard upper bound on the source length returned. Output longer than this is truncated
    /// and <see cref="DecompiledMethod.Truncated"/> is set. Pass 0 to use the implementation
    /// default (16 KiB).
    /// </param>
    /// <param name="cancellationToken">Cancels the call cooperatively. Checked before invoking the decompiler engine.</param>
    DecompileResult Decompile(MethodIdentity identity, int maxChars = 0, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the C# source for the type identified by <paramref name="moduleVersionId"/> +
    /// <paramref name="typeMetadataToken"/> (TypeDef row, table 0x02). Members are emitted in
    /// declaration order with attributes, properties, events, and nested types — equivalent to
    /// the per-type panel ILSpy renders for a TypeDef. Replaces N <c>decompile_method</c> calls
    /// when the user wants a whole-class audit.
    /// </summary>
    /// <param name="moduleVersionId">MVID of the module that owns the TypeDef.</param>
    /// <param name="typeMetadataToken">TypeDef metadata token (table 0x02).</param>
    /// <param name="maxChars">
    /// Hard upper bound on the source length returned. Output longer than this is truncated and
    /// <see cref="DecompiledType.Truncated"/> is set. Pass 0 to use the implementation default
    /// (64 KiB — four times <c>decompile_method</c>'s default since a whole type is naturally larger).
    /// </param>
    /// <param name="cancellationToken">Cancels the call cooperatively. Checked before invoking the decompiler engine.</param>
    DecompileTypeResult DecompileType(Guid moduleVersionId, int typeMetadataToken, int maxChars = 0, CancellationToken cancellationToken = default);

    /// <summary>Number of entries currently cached. Exposed for diagnostics/tests.</summary>
    int CachedEntries { get; }
}

/// <summary>Result of <see cref="IDecompiler.Decompile"/>.</summary>
public readonly record struct DecompileResult(DecompiledMethod? Source, AssemblyError? Error)
{
    public bool IsSuccess => Source is not null;
    public static DecompileResult Ok(DecompiledMethod s) => new(s, null);
    public static DecompileResult Fail(AssemblyError e) => new(null, e);
}

/// <summary>Result of <see cref="IDecompiler.DecompileType"/>.</summary>
public readonly record struct DecompileTypeResult(DecompiledType? Source, AssemblyError? Error)
{
    public bool IsSuccess => Source is not null;
    public static DecompileTypeResult Ok(DecompiledType s) => new(s, null);
    public static DecompileTypeResult Fail(AssemblyError e) => new(null, e);
}
