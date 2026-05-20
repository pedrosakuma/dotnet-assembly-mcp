// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Adapted from src/coreclr/tools/aot/ILCompiler.Reflection.ReadyToRun/NibbleReader.cs
// — trimmed to the subset asm-mcp's R2R DebugInfo parser actually uses.

namespace DotnetAssemblyMcp.Core.Metadata.R2R;

/// <summary>
/// Reads variable-length unsigned integers nibble-by-nibble. CoreCLR encodes R2R
/// DebugInfo header fields with <c>NibbleWriter::WriteEncodedU32</c> — each nibble
/// contributes 3 bits, high bit of the nibble indicates 'more follows'.
/// </summary>
internal sealed class NibbleReader
{
    private const byte NoNextNibble = 0xFF;

    private readonly NativeReader _imageReader;
    private int _offset;
    private byte _nextNibble = NoNextNibble;

    public NibbleReader(NativeReader imageReader, int offset)
    {
        _imageReader = imageReader;
        _offset = offset;
    }

    private byte ReadNibble()
    {
        byte result;
        if (_nextNibble != NoNextNibble)
        {
            result = _nextNibble;
            _nextNibble = NoNextNibble;
        }
        else
        {
            byte raw = _imageReader.ReadByte(ref _offset);
            result = (byte)(raw & 0x0F);
            _nextNibble = (byte)(raw >> 4);
        }
        return result;
    }

    public uint ReadUInt()
    {
        uint value = 0;
        uint nibble;
        do
        {
            nibble = ReadNibble();
            value = (value << 3) + (nibble & 0x7);
        }
        while ((nibble & 0x8) != 0);
        return value;
    }

    /// <summary>
    /// Next byte the bit-packed bounds payload starts at. The encoded-header section is
    /// nibble-aligned; the payload section is byte-aligned, so any half-consumed nibble
    /// is discarded by re-aligning to the next whole byte (mirrors CoreCLR semantics).
    /// </summary>
    public int GetNextByteOffset() => _offset;
}
