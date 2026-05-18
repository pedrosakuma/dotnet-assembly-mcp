using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Spike.Adapters;

public sealed class CecilAdapter : IMetadataAdapter
{
    private readonly ModuleDefinition _module;

    public CecilAdapter(string path)
    {
        // ReadingMode.Deferred = metadata-only-ish, body read on demand.
        _module = ModuleDefinition.ReadModule(path, new ReaderParameters(ReadingMode.Deferred));
    }

    public string Name => "Mono.Cecil";
    public Guid Mvid => _module.Mvid;

    public MethodSummary Resolve(int token)
    {
        var m = (MethodDefinition)_module.LookupToken(new MetadataToken((uint)token));
        return new MethodSummary(
            m.DeclaringType.FullName,
            m.Name,
            m.FullName,
            m.HasBody ? m.Body.CodeSize : 0,
            m.GenericParameters.Count,
            token);
    }

    public IReadOnlyList<MethodSummary> ListMethods()
    {
        var list = new List<MethodSummary>();
        foreach (var t in _module.GetTypes())
            foreach (var m in t.Methods)
                list.Add(new MethodSummary(
                    t.FullName, m.Name, m.FullName,
                    m.HasBody ? m.Body.CodeSize : 0,
                    m.GenericParameters.Count,
                    m.MetadataToken.ToInt32()));
        return list;
    }

    public ReadOnlyMemory<byte> GetIlBytes(int token)
    {
        var m = (MethodDefinition)_module.LookupToken(new MetadataToken((uint)token));
        if (!m.HasBody) return ReadOnlyMemory<byte>.Empty;
        // Cecil doesn't expose raw IL bytes directly; re-encode from Instructions for parity.
        // For benchmark fairness this is acceptable — most tools never need raw bytes.
        var ms = new MemoryStream();
        foreach (var ins in m.Body.Instructions)
        {
            // Just write the opcode byte(s) — operand re-encoding is non-trivial and
            // not needed for the spike (we measure decoding, not round-trip).
            var op = ins.OpCode;
            if (op.Size == 1) ms.WriteByte(op.Op2);
            else { ms.WriteByte(op.Op1); ms.WriteByte(op.Op2); }
        }
        return ms.ToArray();
    }

    public IlSummary ScanIl(int token)
    {
        var m = (MethodDefinition)_module.LookupToken(new MetadataToken((uint)token));
        var calls = new List<int>();
        var fields = new List<int>();
        var types = new List<int>();
        var strings = new List<string>();
        if (!m.HasBody) return new IlSummary(calls, fields, types, strings);

        foreach (var ins in m.Body.Instructions)
        {
            switch (ins.OpCode.Code)
            {
                case Code.Call:
                case Code.Callvirt:
                case Code.Newobj:
                case Code.Ldftn:
                case Code.Ldvirtftn:
                    if (ins.Operand is MethodReference mr)
                        calls.Add(mr.MetadataToken.ToInt32());
                    break;
                case Code.Ldfld:
                case Code.Stfld:
                case Code.Ldsfld:
                case Code.Stsfld:
                case Code.Ldflda:
                case Code.Ldsflda:
                    if (ins.Operand is FieldReference fr)
                        fields.Add(fr.MetadataToken.ToInt32());
                    break;
                case Code.Newarr:
                case Code.Castclass:
                case Code.Isinst:
                case Code.Box:
                case Code.Unbox:
                case Code.Unbox_Any:
                case Code.Ldtoken:
                    if (ins.Operand is TypeReference tr)
                        types.Add(tr.MetadataToken.ToInt32());
                    break;
                case Code.Ldstr:
                    if (ins.Operand is string s) strings.Add(s);
                    break;
            }
        }
        return new IlSummary(calls, fields, types, strings);
    }

    public void Dispose() => _module.Dispose();
}
