namespace DotnetAssemblyMcp.Core.Metadata;

using DotnetAssemblyMcp.Core;

/// <summary>
/// Source of a precompiled native body discovered alongside a method's IL. Currently only
/// <see cref="R2R"/> is surfaced by asm-mcp — NativeAOT binaries don't carry ECMA-335
/// metadata and are out of scope for this server (see <c>dotnet-native-mcp</c>).
/// </summary>
public enum NativeBodySource
{
    R2R,
}

/// <summary>
/// CPU architecture of a native body, derived from the PE COFF header
/// (<see cref="System.Reflection.PortableExecutable.Machine"/>). Returned as part of
/// <see cref="NativeBodyRef"/> so consumers can pick the right Iced decoder.
/// </summary>
public enum NativeArchitecture
{
    Unknown,
    X64,
    Arm64,
    X86,
}

/// <summary>
/// Pointer-into-PE for one contiguous range of native code. Hot regions are always present;
/// cold regions (R2R hot/cold split) are optional and will be populated by a future iteration.
/// </summary>
public sealed record NativeRegion(
    int Rva,
    int Size);

/// <summary>
/// Handoff payload that asm-mcp emits when a method has a precompiled native body inside
/// the same managed PE (currently R2R only). The agent is expected to feed
/// (<see cref="PePath"/>, <see cref="HotRegion"/>) into
/// <c>dotnet-native-mcp.disassemble(imagePath, rva, size)</c> for the actual Iced decode.
/// </summary>
/// <remarks>
/// asm-mcp deliberately does NOT decode bytes itself — that is native-mcp's charter. This
/// record is metadata-only: header detection + RuntimeFunction lookup.
/// </remarks>
public sealed record NativeBodyRef(
    NativeBodySource Source,
    string PePath,
    NativeArchitecture Architecture,
    NativeRegion HotRegion,
    NativeRegion? ColdRegion = null,
    IReadOnlyList<NativeIlMapEntry>? IlMap = null);

/// <summary>
/// Single mapping entry from R2R DebugInfo: a native-code offset (inside the hot region)
/// paired with the IL offset it originated from. Populated only when the R2R DebugInfo
/// section is present and decoded.
/// </summary>
public sealed record NativeIlMapEntry(
    int NativeOffset,
    int IlOffset);

/// <summary>Result of <see cref="IMetadataIndex.GetNativeBodyRef"/>.</summary>
public readonly record struct NativeBodyResult(NativeBodyRef? Body, AssemblyError? Error)
{
    /// <summary>True when the lookup ran cleanly (regardless of whether a body was found).</summary>
    public bool IsSuccess => Error is null;

    /// <summary>True when the method has a precompiled native body in the module.</summary>
    public bool Found => Body is not null;

    public static NativeBodyResult Ok(NativeBodyRef body) => new(body, null);
    public static NativeBodyResult NotFound() => new(null, null);
    public static NativeBodyResult Fail(AssemblyError e) => new(null, e);
}
