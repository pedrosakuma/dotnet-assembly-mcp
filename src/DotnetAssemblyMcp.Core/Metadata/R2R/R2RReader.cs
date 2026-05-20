// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace DotnetAssemblyMcp.Core.Metadata.R2R;

/// <summary>
/// Lightweight resolver for ReadyToRun pre-compiled native bodies embedded in a managed PE.
/// Implements only the metadata lookup that asm-mcp needs: header parse, section table,
/// <c>MethodDefEntryPoints</c> -&gt; <c>RuntimeFunctions</c> resolution. No disassembly.
/// </summary>
/// <remarks>
/// Format references:
/// <list type="bullet">
/// <item><c>src/coreclr/inc/readytorun.h</c> (header layout, section type enum)</item>
/// <item><c>src/coreclr/tools/aot/ILCompiler.Reflection.ReadyToRun/ReadyToRunReader.cs</c>
///   (entrypoint decoding — see <c>GetRuntimeFunctionIndexFromOffset</c>)</item>
/// </list>
/// </remarks>
internal sealed class R2RReader
{
    private const uint READYTORUN_SIGNATURE = 0x00525452; // 'RTR\0'

    // ReadyToRunSectionType from readytorun.h.
    private const int SectionType_RuntimeFunctions = 102;
    private const int SectionType_MethodDefEntryPoints = 103;

    private readonly NativeReader _reader;
    private readonly ImmutableArray<SectionHeader> _peSections;
    private readonly FrozenDictionary<int, (int Rva, int Size)> _r2rSections;
    private readonly NativeArray? _methodEntryPoints;
    private readonly int _runtimeFunctionsFileOffset;
    private readonly int _runtimeFunctionsCount;
    private readonly int _runtimeFunctionEntrySize;

    public Machine Machine { get; }

    public ushort MajorVersion { get; }

    public ushort MinorVersion { get; }

    private R2RReader(
        NativeReader reader,
        ImmutableArray<SectionHeader> peSections,
        Machine machine,
        ushort major,
        ushort minor,
        FrozenDictionary<int, (int Rva, int Size)> r2rSections,
        NativeArray? methodEntryPoints,
        int runtimeFunctionsFileOffset,
        int runtimeFunctionsCount,
        int runtimeFunctionEntrySize)
    {
        _reader = reader;
        _peSections = peSections;
        Machine = machine;
        MajorVersion = major;
        MinorVersion = minor;
        _r2rSections = r2rSections;
        _methodEntryPoints = methodEntryPoints;
        _runtimeFunctionsFileOffset = runtimeFunctionsFileOffset;
        _runtimeFunctionsCount = runtimeFunctionsCount;
        _runtimeFunctionEntrySize = runtimeFunctionEntrySize;
    }

    /// <summary>
    /// Attempts to build an R2R reader. Returns <c>false</c> for JIT-only managed PEs
    /// (i.e. those without a <c>ManagedNativeHeader</c> or with a non-R2R signature there).
    /// </summary>
    public static bool TryCreate(PEReader pe, out R2RReader? reader)
    {
        reader = null;
        ArgumentNullException.ThrowIfNull(pe);

        CorHeader? cor = pe.PEHeaders.CorHeader;
        if (cor is null) return false;
        DirectoryEntry dir = cor.ManagedNativeHeaderDirectory;
        if (dir.RelativeVirtualAddress == 0 || dir.Size < 16) return false;

        // Confirm signature without materialising the full image.
        PEMemoryBlock probe;
        try
        {
            probe = pe.GetSectionData(dir.RelativeVirtualAddress);
        }
        catch (BadImageFormatException)
        {
            return false;
        }
        if (probe.Length < 4) return false;
        BlobReader br = probe.GetReader();
        uint sig = br.ReadUInt32();
        if (sig != READYTORUN_SIGNATURE) return false;

        // We need a byte[] backing for NativeArray (which jumps around the section).
        // The image is mmap'd by PEReader; this materialises a copy but only once per module.
        ImmutableArray<byte> image = pe.GetEntireImage().GetContent();
        byte[] bytes = ImmutableCollectionsMarshal.AsArray(image) ?? image.ToArray();
        NativeReader nr = new(bytes);

        ImmutableArray<SectionHeader> peSections = pe.PEHeaders.SectionHeaders;
        int headerFileOffset = RvaToFileOffset(peSections, dir.RelativeVirtualAddress);
        if (headerFileOffset < 0) return false;

        // Re-parse the header from the byte buffer (we need MajorVersion + section table).
        int off = headerFileOffset;
        _ = nr.ReadUInt32(ref off); // signature, already validated
        ushort major = nr.ReadUInt16(ref off);
        ushort minor = nr.ReadUInt16(ref off);
        _ = nr.ReadUInt32(ref off); // flags
        int nSections = nr.ReadInt32(ref off);

        // Sanity bounds — R2R headers are tiny; an absurd section count means we mis-parsed.
        if (nSections is < 0 or > 1024) return false;

        Dictionary<int, (int Rva, int Size)> sections = new(nSections);
        for (int i = 0; i < nSections; i++)
        {
            int type = nr.ReadInt32(ref off);
            int rva = nr.ReadInt32(ref off);
            int size = nr.ReadInt32(ref off);
            sections[type] = (rva, size);
        }
        FrozenDictionary<int, (int Rva, int Size)> frozen = sections.ToFrozenDictionary();

        Machine machine = NormalizeR2RMachine(pe.PEHeaders.CoffHeader.Machine);
        int rfSize = machine == Machine.Amd64 ? 3 * sizeof(int) : 2 * sizeof(int);

        NativeArray? methodEntryPoints = null;
        if (frozen.TryGetValue(SectionType_MethodDefEntryPoints, out var meSec))
        {
            int meFileOffset = RvaToFileOffset(peSections, meSec.Rva);
            if (meFileOffset >= 0)
            {
                methodEntryPoints = new NativeArray(nr, (uint)meFileOffset);
            }
        }

        int rfFileOffset = -1;
        int rfCount = 0;
        if (frozen.TryGetValue(SectionType_RuntimeFunctions, out var rfSec))
        {
            rfFileOffset = RvaToFileOffset(peSections, rfSec.Rva);
            rfCount = rfSize > 0 ? rfSec.Size / rfSize : 0;
        }

        reader = new R2RReader(
            nr, peSections, machine, major, minor, frozen,
            methodEntryPoints, rfFileOffset, rfCount, rfSize);
        return true;
    }

    /// <summary>
    /// Resolves a method's hot region in the PE.
    /// </summary>
    /// <param name="methodDefRid">1-based MethodDef row id (i.e. <c>token &amp; 0x00FFFFFF</c>).</param>
    /// <param name="region">The hot region (RVA + size). Size is 0 on architectures that do
    /// not encode EndAddress in the function table (everything but x64).</param>
    /// <returns><c>true</c> if the method has a precompiled body.</returns>
    public bool TryGetHotRegion(int methodDefRid, out NativeRegion? region)
    {
        region = null;
        if (methodDefRid <= 0) return false;
        if (_methodEntryPoints is null) return false;
        if (_runtimeFunctionsFileOffset < 0) return false;

        int blobOffset = 0;
        if (!_methodEntryPoints.TryGetAt((uint)(methodDefRid - 1), ref blobOffset))
            return false;

        // GetRuntimeFunctionIndexFromOffset — see ReadyToRunReader.cs.
        uint id = 0;
        uint after = _reader.DecodeUnsigned((uint)blobOffset, ref id);
        if ((id & 1) != 0)
        {
            // Followed by a fixup blob; skip-back encoding is irrelevant for our lookup.
            id >>= 2;
        }
        else
        {
            id >>= 1;
        }
        _ = after; // unused — fixups are not needed for hot-region lookup

        int runtimeFunctionIndex = (int)id;
        if (runtimeFunctionIndex < 0 || runtimeFunctionIndex >= _runtimeFunctionsCount)
            return false;

        int entryOffset = _runtimeFunctionsFileOffset + (runtimeFunctionIndex * _runtimeFunctionEntrySize);
        if (entryOffset < 0 || entryOffset + _runtimeFunctionEntrySize > _reader.Length)
            return false;

        int o = entryOffset;
        int startRva = _reader.ReadInt32(ref o);
        int size = 0;
        if (Machine == Machine.Amd64)
        {
            int endRva = _reader.ReadInt32(ref o);
            size = endRva > startRva ? endRva - startRva : 0;
        }
        // UnwindInfo RVA is the trailing field — we don't surface it (yet).

        if (startRva <= 0) return false;
        region = new NativeRegion(startRva, size);
        return true;
    }

    private static int RvaToFileOffset(ImmutableArray<SectionHeader> sections, int rva)
    {
        foreach (SectionHeader s in sections)
        {
            int start = s.VirtualAddress;
            int end = start + s.VirtualSize;
            if (rva >= start && rva < end)
            {
                return s.PointerToRawData + (rva - start);
            }
        }
        return -1;
    }

    /// <summary>
    /// CoreCLR's crossgen2 stamps R2R-only machine codes on the COFF header so the OS PE
    /// loader rejects the file as code. Map them back to their canonical
    /// <see cref="Machine"/> for downstream consumers.
    /// See <c>src/coreclr/inc/pedecoder.h</c> (<c>IMAGE_FILE_MACHINE_NATIVE_*</c>).
    /// </summary>
    private static Machine NormalizeR2RMachine(Machine raw) => (ushort)raw switch
    {
        0xFD1D => Machine.Amd64,
        0xFD66 => Machine.Arm64,
        0xFD32 => Machine.Arm, // R2R Arm32
        0xFD14 => Machine.I386,
        _ => raw,
    };
}

// Small shim — ImmutableCollectionsMarshal lives in System.Runtime.InteropServices in net8+.
// Kept inline here so the rest of the file stays readable.
file static class ImmutableCollectionsMarshal
{
    public static byte[]? AsArray(ImmutableArray<byte> array)
        => System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsArray(array);
}
