// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Vendored from src/coreclr/tools/aot/ILCompiler.Reflection.ReadyToRun/NativeArray.cs
// (commit c8810a6). Verbatim algorithm — only namespace / accessibility tweaked for asm-mcp.

namespace DotnetAssemblyMcp.Core.Metadata.R2R;

/// <summary>
/// Sparse-array container used by R2R section 103 (METHODDEF_ENTRYPOINTS) to map
/// MethodDef rid -&gt; entrypoint blob offset. See
/// <see href="https://github.com/dotnet/runtime/blob/main/src/coreclr/vm/nativeformatreader.h">NativeFormat::NativeArray</see>.
/// </summary>
internal sealed class NativeArray
{
    private const int BlockSize = 16;

    private readonly NativeReader _reader;
    private readonly uint _baseOffset;
    private readonly uint _nElements;
    private readonly byte _entryIndexSize;

    public NativeArray(NativeReader reader, uint offset)
    {
        _reader = reader;
        uint val = 0;
        _baseOffset = _reader.DecodeUnsigned(offset, ref val);
        _nElements = val >> 2;
        _entryIndexSize = (byte)(val & 3);
    }

    public uint GetCount() => _nElements;

    public bool TryGetAt(uint index, ref int pOffset)
    {
        if (index >= _nElements)
            return false;

        uint offset;
        if (_entryIndexSize == 0)
        {
            int i = (int)(_baseOffset + (index / BlockSize));
            offset = _reader.ReadByte(ref i);
        }
        else if (_entryIndexSize == 1)
        {
            int i = (int)(_baseOffset + 2 * (index / BlockSize));
            offset = _reader.ReadUInt16(ref i);
        }
        else
        {
            int i = (int)(_baseOffset + 4 * (index / BlockSize));
            offset = _reader.ReadUInt32(ref i);
        }
        offset += _baseOffset;

        for (uint bit = BlockSize >> 1; bit > 0; bit >>= 1)
        {
            uint val = 0;
            uint offset2 = _reader.DecodeUnsigned(offset, ref val);
            if ((index & bit) != 0)
            {
                if ((val & 2) != 0)
                {
                    offset += val >> 2;
                    continue;
                }
            }
            else
            {
                if ((val & 1) != 0)
                {
                    offset = offset2;
                    continue;
                }
            }

            // Not found
            if ((val & 3) == 0)
            {
                // Matching special leaf node?
                if ((val >> 2) == (index & (BlockSize - 1)))
                {
                    offset = offset2;
                    break;
                }
            }
            return false;
        }
        pOffset = (int)offset;
        return true;
    }
}
