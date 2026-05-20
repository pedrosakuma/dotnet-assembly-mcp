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
    private const int SectionType_DebugInfo = 105;

    private readonly NativeReader _reader;
    private readonly ImmutableArray<SectionHeader> _peSections;
    private readonly FrozenDictionary<int, (int Rva, int Size)> _r2rSections;
    private readonly NativeArray? _methodEntryPoints;
    private readonly NativeArray? _debugInfo;
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
        NativeArray? debugInfo,
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
        _debugInfo = debugInfo;
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

        NativeArray? debugInfo = null;
        if (frozen.TryGetValue(SectionType_DebugInfo, out var diSec))
        {
            int diFileOffset = RvaToFileOffset(peSections, diSec.Rva);
            if (diFileOffset >= 0)
            {
                debugInfo = new NativeArray(nr, (uint)diFileOffset);
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
            methodEntryPoints, debugInfo, rfFileOffset, rfCount, rfSize);
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
        => TryGetHotRegion(methodDefRid, out region, out _);

    /// <summary>
    /// Same as <see cref="TryGetHotRegion(int, out NativeRegion?)"/> but also surfaces the
    /// <c>runtimeFunctionIndex</c> the entrypoint blob resolved to (needed by
    /// <see cref="TryGetIlMap"/>).
    /// </summary>
    public bool TryGetHotRegion(int methodDefRid, out NativeRegion? region, out int runtimeFunctionIndex)
    {
        region = null;
        runtimeFunctionIndex = -1;
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

        runtimeFunctionIndex = (int)id;
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

    /// <summary>
    /// Resolves the R2R DebugInfo bounds table (section type 105) for one runtime function:
    /// pairs of <c>(NativeOffset, IlOffset, SourceTypes)</c>. Returns <c>false</c> when the
    /// PE has no DebugInfo section, or when the given runtime function has no entry.
    /// </summary>
    /// <param name="runtimeFunctionIndex">Index obtained from <see cref="TryGetHotRegion(int, out NativeRegion?, out int)"/>.</param>
    /// <param name="map">Decoded bounds list. Empty list is possible (no source-mapped instructions).</param>
    public bool TryGetIlMap(int runtimeFunctionIndex, out IReadOnlyList<NativeIlMapEntry>? map)
    {
        map = null;
        if (_debugInfo is null) return false;
        if (runtimeFunctionIndex < 0) return false;

        int rawOffset = 0;
        if (!_debugInfo.TryGetAt((uint)runtimeFunctionIndex, ref rawOffset))
            return false;

        // Back-pointer encoding: DecodeUnsigned reads a `lookback` value out of the blob.
        // If lookback == 0, the actual debug-info bytes start right after the encoded zero;
        // otherwise the bytes start at (rawOffset - lookback) — multiple runtime functions
        // commonly share one DebugInfo blob via this scheme.
        uint lookback = 0;
        uint debugInfoOffset = _reader.DecodeUnsigned((uint)rawOffset, ref lookback);
        if (lookback != 0)
        {
            if (lookback > (uint)rawOffset) return false;
            debugInfoOffset = (uint)rawOffset - lookback;
        }

        NibbleReader hdr = new(_reader, (int)debugInfoOffset);
        uint boundsByteCountOrIndicator;
        try
        {
            boundsByteCountOrIndicator = hdr.ReadUInt();
        }
        catch (BadImageFormatException)
        {
            return false;
        }

        uint boundsByteCount;
        const int DebugInfoFat = 0;
        if (MajorVersion >= 17 && boundsByteCountOrIndicator == DebugInfoFat)
        {
            boundsByteCount = hdr.ReadUInt();
            _ = hdr.ReadUInt(); // variablesByteCount
            _ = hdr.ReadUInt(); // uninstrumented bounds
            _ = hdr.ReadUInt(); // patchpoint info
            _ = hdr.ReadUInt(); // rich debug info
            _ = hdr.ReadUInt(); // async info
        }
        else
        {
            boundsByteCount = boundsByteCountOrIndicator;
            _ = hdr.ReadUInt(); // variablesByteCount
        }

        if (boundsByteCount == 0)
        {
            map = Array.Empty<NativeIlMapEntry>();
            return true;
        }

        int boundsOffset = hdr.GetNextByteOffset();
        try
        {
            map = ParseBounds(boundsOffset, boundsByteCount);
        }
        catch (BadImageFormatException)
        {
            return false;
        }
        return true;
    }

    private const uint DebugInfo_MaxMappingValue = 0xFFFFFFFDu; // Epilog — see DebugInfoTypes.cs

    private List<NativeIlMapEntry> ParseBounds(int offset, uint boundsByteCount)
    {
        // Only the >=16 bit-packed encoding is currently emitted by crossgen2 — older
        // R2R headers are not produced by any supported SDK. We mirror the modern path.
        NibbleReader reader = new(_reader, offset);
        uint count = reader.ReadUInt();
        uint rawNativeDelta = reader.ReadUInt();
        uint rawIlOffsets = reader.ReadUInt();
        // Validate the raw widths BEFORE the `+ 1` adjustment so a payload of 0xFFFFFFFF
        // cannot wrap to zero and bypass the cap below.
        if (rawNativeDelta > 31 || rawIlOffsets > 31)
            throw new BadImageFormatException("R2R DebugInfo: implausible bit width per field.");
        uint bitsForNativeDelta = rawNativeDelta + 1;
        uint bitsForILOffsets = rawIlOffsets + 1;
        uint bitsForSourceType = MajorVersion >= 17 ? 3u : 2u;
        uint bitsPerEntry = bitsForNativeDelta + bitsForILOffsets + bitsForSourceType;
        if (bitsPerEntry == 0 || bitsPerEntry > 60) // sanity — fits in our 64-bit accumulator
            throw new BadImageFormatException("R2R DebugInfo: implausible bitsPerEntry.");

        // Cap `count` against the bytes actually claimed by the bounds payload. An attacker-controlled
        // R2R image cannot make us allocate or scan past the declared bounds blob.
        // Header (count + 2 nibble-encoded width fields) consumed `headerNibbles` nibbles already.
        int bytesOffset = reader.GetNextByteOffset();
        long headerBytes = bytesOffset - offset;
        if (headerBytes < 0 || headerBytes > boundsByteCount)
            throw new BadImageFormatException("R2R DebugInfo: bounds header exceeds declared boundsByteCount.");
        long payloadBytes = boundsByteCount - headerBytes;
        long maxEntries = (payloadBytes * 8) / bitsPerEntry;
        if (count > maxEntries)
            throw new BadImageFormatException("R2R DebugInfo: bounds count exceeds declared payload size.");

        ulong bitsMeaningfulMask = (1UL << (int)bitsPerEntry) - 1;

        List<NativeIlMapEntry> entries = new((int)count);
        uint bitsCollected = 0;
        ulong bitTemp = 0;
        uint previousNativeOffset = 0;
        uint processed = 0;

        while (processed < count)
        {
            byte b = _reader.ReadByte(ref bytesOffset);
            bitTemp |= ((ulong)b) << (int)bitsCollected;
            bitsCollected += 8;

            while (bitsCollected >= bitsPerEntry && processed < count)
            {
                ulong encoded = bitsMeaningfulMask & bitTemp;
                bitTemp >>= (int)bitsPerEntry;
                bitsCollected -= bitsPerEntry;

                string? sourceTypes = DecodeSourceTypes((uint)(encoded & ((1UL << (int)bitsForSourceType) - 1)));
                encoded >>= (int)bitsForSourceType;

                uint nativeDelta = (uint)(encoded & ((1UL << (int)bitsForNativeDelta) - 1));
                try
                {
                    // Checked: a malformed image must not produce a wrong-but-plausible
                    // monotonically-decreasing native offset via uint wrap.
                    previousNativeOffset = checked(previousNativeOffset + nativeDelta);
                }
                catch (OverflowException)
                {
                    throw new BadImageFormatException("R2R DebugInfo: native offset accumulator overflow.");
                }
                if (previousNativeOffset > int.MaxValue)
                    throw new BadImageFormatException("R2R DebugInfo: native offset exceeds int.MaxValue.");
                encoded >>= (int)bitsForNativeDelta;

                uint ilEncoded = (uint)encoded;
                uint il = ilEncoded + DebugInfo_MaxMappingValue;
                int ilOffset = il switch
                {
                    0xFFFFFFFFu => -1, // NoMapping
                    0xFFFFFFFEu => -2, // Prolog
                    0xFFFFFFFDu => -3, // Epilog
                    _ when il > (uint)int.MaxValue =>
                        throw new BadImageFormatException("R2R DebugInfo: IL offset exceeds int.MaxValue."),
                    _ => (int)il,
                };

                entries.Add(new NativeIlMapEntry((int)previousNativeOffset, ilOffset, sourceTypes));
                processed++;
            }
        }

        return entries;
    }

    private static string? DecodeSourceTypes(uint bits)
    {
        // Layout per upstream DebugInfo.ParseBounds (v17+):
        //   bit 0 = CallInstruction, bit 1 = StackEmpty, bit 2 = Async.
        // v16 only has bits 0-1. Higher bits are reserved.
        if (bits == 0) return null;
        List<string> flags = new(3);
        if ((bits & 0x1) != 0) flags.Add("CallInstruction");
        if ((bits & 0x2) != 0) flags.Add("StackEmpty");
        if ((bits & 0x4) != 0) flags.Add("Async");
        return flags.Count == 0 ? null : string.Join('|', flags);
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
