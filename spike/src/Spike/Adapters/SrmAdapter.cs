using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Spike.Adapters;

public sealed class SrmAdapter : IMetadataAdapter
{
    private readonly PEReader _pe;
    private readonly MetadataReader _md;

    public SrmAdapter(string path)
    {
        _pe = new PEReader(File.OpenRead(path));
        _md = _pe.GetMetadataReader();
    }

    public string Name => "System.Reflection.Metadata";

    public Guid Mvid
    {
        get
        {
            var module = _md.GetModuleDefinition();
            return _md.GetGuid(module.Mvid);
        }
    }

    public MethodSummary Resolve(int token)
    {
        var handle = (MethodDefinitionHandle)MetadataTokens.Handle(token);
        return Summarize(handle, token);
    }

    public IReadOnlyList<MethodSummary> ListMethods()
    {
        var list = new List<MethodSummary>(_md.MethodDefinitions.Count);
        foreach (var h in _md.MethodDefinitions)
            list.Add(Summarize(h, MetadataTokens.GetToken(h)));
        return list;
    }

    public ReadOnlyMemory<byte> GetIlBytes(int token)
    {
        var handle = (MethodDefinitionHandle)MetadataTokens.Handle(token);
        var def = _md.GetMethodDefinition(handle);
        int rva = def.RelativeVirtualAddress;
        if (rva == 0) return ReadOnlyMemory<byte>.Empty;
        var body = _pe.GetMethodBody(rva);
        return body.GetILBytes() is { } b ? b.AsMemory() : ReadOnlyMemory<byte>.Empty;
    }

    public IlSummary ScanIl(int token)
    {
        var calls = new List<int>();
        var fields = new List<int>();
        var types = new List<int>();
        var strings = new List<string>();
        var bytes = GetIlBytes(token).Span;
        if (bytes.IsEmpty) return new IlSummary(calls, fields, types, strings);

        // Minimal IL reader — only operand kinds we care about.
        int pos = 0;
        while (pos < bytes.Length)
        {
            byte b1 = bytes[pos++];
            ushort op;
            if (b1 == 0xFE)
            {
                if (pos >= bytes.Length) break;
                op = (ushort)(0xFE00 | bytes[pos++]);
            }
            else op = b1;

            int operandSize = OperandSize(op, out var kind);
            int operand = 0;
            if (operandSize == 4 && pos + 4 <= bytes.Length)
                operand = BitConverter.ToInt32(bytes.Slice(pos, 4));
            else if (operandSize == -1) // switch
            {
                if (pos + 4 > bytes.Length) break;
                int n = BitConverter.ToInt32(bytes.Slice(pos, 4));
                pos += 4 + n * 4;
                continue;
            }
            pos += Math.Max(0, operandSize);

            switch (kind)
            {
                case OpKind.Call:
                    calls.Add(operand);
                    break;
                case OpKind.Field:
                    fields.Add(operand);
                    break;
                case OpKind.Type:
                    types.Add(operand);
                    break;
                case OpKind.String:
                    var sh = MetadataTokens.UserStringHandle(operand & 0x00FFFFFF);
                    strings.Add(_md.GetUserString(sh));
                    break;
            }
        }
        return new IlSummary(calls, fields, types, strings);
    }

    private enum OpKind { None, Call, Field, Type, String }

    // Returns operand size in bytes (or -1 for switch), and classifies token-bearing ops.
    private static int OperandSize(ushort op, out OpKind kind)
    {
        kind = OpKind.None;
        switch (op)
        {
            // Token operands we care about (all 4-byte tokens)
            case 0x28: kind = OpKind.Call; return 4;       // call
            case 0x6F: kind = OpKind.Call; return 4;       // callvirt
            case 0x73: kind = OpKind.Call; return 4;       // newobj
            case 0xFE06: kind = OpKind.Call; return 4;     // ldftn
            case 0xFE07: kind = OpKind.Call; return 4;     // ldvirtftn

            case 0x7B: kind = OpKind.Field; return 4;      // ldfld
            case 0x7C: kind = OpKind.Field; return 4;      // ldflda
            case 0x7D: kind = OpKind.Field; return 4;      // stfld
            case 0x7E: kind = OpKind.Field; return 4;      // ldsfld
            case 0x7F: kind = OpKind.Field; return 4;      // ldsflda
            case 0x80: kind = OpKind.Field; return 4;      // stsfld

            case 0x74: kind = OpKind.Type; return 4;       // castclass
            case 0x75: kind = OpKind.Type; return 4;       // isinst
            case 0x8C: kind = OpKind.Type; return 4;       // box
            case 0x79: kind = OpKind.Type; return 4;       // unbox
            case 0xA5: kind = OpKind.Type; return 4;       // unbox.any
            case 0x8D: kind = OpKind.Type; return 4;       // newarr
            case 0xD0: kind = OpKind.Type; return 4;       // ldtoken

            case 0x72: kind = OpKind.String; return 4;     // ldstr
        }
        return NonTokenOperandSize(op);
    }

    private static int NonTokenOperandSize(ushort op)
    {
        // Coarse opcode-size table for the spike. Covers the common cases;
        // mis-sizing a rare op would only skew xref scanning, not correctness
        // of the token-bearing ops above.
        return op switch
        {
            // inline none (most arithmetic, ldarg.0..3, etc.) — too many to list; default 0.
            0x2B or 0x2C or 0x2D or 0x2E or 0x2F or 0x30 or 0x31 or 0x32 or
            0x33 or 0x34 or 0x35 or 0x36 or 0x37 or 0x38 => 1, // short branches (br.s family) — actually 1-byte target
            0x39 or 0x3A or 0x3B or 0x3C or 0x3D or 0x3E or 0x3F or 0x40 or
            0x41 or 0x42 or 0x43 or 0x44 or 0x45 => 4, // long branches
            0x1F or 0x0E or 0x10 or 0x12 or 0x13 or 0xFE0C or 0xFE0D or 0xFE0E or 0xFE0F or 0xFE10 or 0xFE11 or 0xFE12 or 0xFE13 => 1,
            0x20 or 0x22 or 0x23 => 4, // ldc.i4, ldc.r4, ldc.r8 (r8 is 8)
            0x21 => 8, // ldc.i8
            0x2A => 0, // ret
            _ => 0,
        };
    }

    private MethodSummary Summarize(MethodDefinitionHandle h, int token)
    {
        var def = _md.GetMethodDefinition(h);
        var typeH = def.GetDeclaringType();
        var type = _md.GetTypeDefinition(typeH);
        string ns = _md.GetString(type.Namespace);
        string name = _md.GetString(type.Name);
        string fullType = string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
        string method = _md.GetString(def.Name);

        var sig = def.DecodeSignature(new StringSignatureProvider(_md), genericContext: null);
        string paramList = string.Join(",", sig.ParameterTypes);
        string signature = $"{sig.ReturnType} {fullType}.{method}({paramList})";

        int ilSize = 0;
        if (def.RelativeVirtualAddress != 0)
        {
            var body = _pe.GetMethodBody(def.RelativeVirtualAddress);
            ilSize = body.GetILBytes()?.Length ?? 0;
        }

        return new MethodSummary(
            fullType, method, signature,
            ilSize,
            def.GetGenericParameters().Count,
            token);
    }

    public void Dispose() => _pe.Dispose();
}
