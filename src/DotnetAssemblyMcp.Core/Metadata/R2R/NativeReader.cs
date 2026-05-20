// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Adapted from src/coreclr/tools/aot/ILCompiler.Reflection.ReadyToRun/NativeReader.cs
// (commit b26a757) — trimmed to the subset asm-mcp's R2R parser actually uses.

using System.Buffers.Binary;

namespace DotnetAssemblyMcp.Core.Metadata.R2R;

/// <summary>
/// Random-access reader over a byte buffer. The CoreCLR NativeFormat decoders take indices
/// by <c>ref int</c>; the surface mirrors that to keep the vendored
/// <see cref="NativeArray"/> code drop-in.
/// </summary>
internal sealed class NativeReader
{
    private readonly byte[] _bytes;

    public NativeReader(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        _bytes = bytes;
    }

    public int Length => _bytes.Length;

    public byte ReadByte(ref int start)
    {
        if ((uint)start >= (uint)_bytes.Length)
            throw new BadImageFormatException("R2R reader: offset out of bounds.");
        return _bytes[start++];
    }

    public ushort ReadUInt16(ref int start)
    {
        if (start < 0 || start + 2 > _bytes.Length)
            throw new BadImageFormatException("R2R reader: offset out of bounds.");
        var v = BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(start, 2));
        start += 2;
        return v;
    }

    public uint ReadUInt32(ref int start)
    {
        if (start < 0 || start + 4 > _bytes.Length)
            throw new BadImageFormatException("R2R reader: offset out of bounds.");
        var v = BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(start, 4));
        start += 4;
        return v;
    }

    public int ReadInt32(ref int start)
    {
        if (start < 0 || start + 4 > _bytes.Length)
            throw new BadImageFormatException("R2R reader: offset out of bounds.");
        var v = BinaryPrimitives.ReadInt32LittleEndian(_bytes.AsSpan(start, 4));
        start += 4;
        return v;
    }

    /// <summary>
    /// Variable-length unsigned decode from CoreCLR's nativeformatreader.h. Returns the
    /// new stream offset (the value is written to <paramref name="pValue"/>).
    /// </summary>
    public uint DecodeUnsigned(uint offset, ref uint pValue)
    {
        if (offset >= (uint)_bytes.Length)
            throw new BadImageFormatException("R2R reader: offset out of bounds.");

        int off = (int)offset;
        uint val = ReadByte(ref off);

        if ((val & 1) == 0)
        {
            pValue = (val >> 1);
            offset += 1;
        }
        else if ((val & 2) == 0)
        {
            if (offset + 1 >= (uint)_bytes.Length)
                throw new BadImageFormatException("R2R reader: offset out of bounds.");
            pValue = (val >> 2) | ((uint)ReadByte(ref off) << 6);
            offset += 2;
        }
        else if ((val & 4) == 0)
        {
            if (offset + 2 >= (uint)_bytes.Length)
                throw new BadImageFormatException("R2R reader: offset out of bounds.");
            pValue = (val >> 3)
                | ((uint)ReadByte(ref off) << 5)
                | ((uint)ReadByte(ref off) << 13);
            offset += 3;
        }
        else if ((val & 8) == 0)
        {
            if (offset + 3 >= (uint)_bytes.Length)
                throw new BadImageFormatException("R2R reader: offset out of bounds.");
            pValue = (val >> 4)
                | ((uint)ReadByte(ref off) << 4)
                | ((uint)ReadByte(ref off) << 12)
                | ((uint)ReadByte(ref off) << 20);
            offset += 4;
        }
        else if ((val & 16) == 0)
        {
            pValue = ReadUInt32(ref off);
            offset += 5;
        }
        else
        {
            throw new BadImageFormatException("R2R reader: DecodeUnsigned overflow.");
        }

        return offset;
    }
}
