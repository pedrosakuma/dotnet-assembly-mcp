using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Spike.Adapters;

public sealed class AsmResolverAdapter : IMetadataAdapter
{
    private readonly ModuleDefinition _module;

    public AsmResolverAdapter(string path)
    {
        _module = ModuleDefinition.FromFile(path);
    }

    public string Name => "AsmResolver";
    public Guid Mvid => _module.Mvid;

    public MethodSummary Resolve(int token)
    {
        var m = (MethodDefinition)_module.LookupMember(new MetadataToken((uint)token));
        return new MethodSummary(
            m.DeclaringType?.FullName ?? "",
            m.Name ?? "",
            m.ToString() ?? "",
            m.CilMethodBody?.Instructions.Sum(i => i.Size) ?? 0,
            m.GenericParameters.Count,
            token);
    }

    public IReadOnlyList<MethodSummary> ListMethods()
    {
        var list = new List<MethodSummary>();
        foreach (var t in _module.GetAllTypes())
            foreach (var m in t.Methods)
                list.Add(new MethodSummary(
                    t.FullName, m.Name ?? "", m.ToString() ?? "",
                    m.CilMethodBody?.Instructions.Sum(i => i.Size) ?? 0,
                    m.GenericParameters.Count,
                    m.MetadataToken.ToInt32()));
        return list;
    }

    public ReadOnlyMemory<byte> GetIlBytes(int token)
    {
        var m = (MethodDefinition)_module.LookupMember(new MetadataToken((uint)token));
        var body = m.CilMethodBody;
        if (body is null) return ReadOnlyMemory<byte>.Empty;
        var ms = new MemoryStream();
        foreach (var ins in body.Instructions)
        {
            var code = ins.OpCode.Code;
            ms.WriteByte((byte)((ushort)code & 0xFF));
        }
        return ms.ToArray();
    }

    public IlSummary ScanIl(int token)
    {
        var m = (MethodDefinition)_module.LookupMember(new MetadataToken((uint)token));
        var calls = new List<int>();
        var fields = new List<int>();
        var types = new List<int>();
        var strings = new List<string>();
        var body = m.CilMethodBody;
        if (body is null) return new IlSummary(calls, fields, types, strings);

        foreach (var ins in body.Instructions)
        {
            var code = ins.OpCode.Code;
            switch (code)
            {
                case CilCode.Call:
                case CilCode.Callvirt:
                case CilCode.Newobj:
                case CilCode.Ldftn:
                case CilCode.Ldvirtftn:
                    if (ins.Operand is IMethodDescriptor mr)
                        calls.Add(mr.MetadataToken.ToInt32());
                    break;
                case CilCode.Ldfld:
                case CilCode.Stfld:
                case CilCode.Ldsfld:
                case CilCode.Stsfld:
                case CilCode.Ldflda:
                case CilCode.Ldsflda:
                    if (ins.Operand is IFieldDescriptor fr)
                        fields.Add(fr.MetadataToken.ToInt32());
                    break;
                case CilCode.Newarr:
                case CilCode.Castclass:
                case CilCode.Isinst:
                case CilCode.Box:
                case CilCode.Unbox:
                case CilCode.Unbox_Any:
                case CilCode.Ldtoken:
                    if (ins.Operand is IMetadataMember tr)
                        types.Add(tr.MetadataToken.ToInt32());
                    break;
                case CilCode.Ldstr:
                    if (ins.Operand is string s) strings.Add(s);
                    break;
            }
        }
        return new IlSummary(calls, fields, types, strings);
    }

    public void Dispose() { /* AsmResolver ModuleDefinition has no IDisposable */ }
}
