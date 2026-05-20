using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Handles;
using DotnetAssemblyMcp.Core.Identity;
using HandleKind = System.Reflection.Metadata.HandleKind;
using static DotnetAssemblyMcp.Core.Metadata.MetadataDisplay;

namespace DotnetAssemblyMcp.Core.Metadata.Resolvers;

/// <summary>
/// IL body extraction + IL walking. Extracted from <see cref="MetadataIndex"/> in issue #92.
/// Owns <see cref="GetIlBody"/>, <see cref="ScanIl"/>, the instruction-count helper, and the
/// MemberRef-aware <see cref="AddTokenRef"/> bucketing (issue #86).
/// </summary>
/// <remarks>
/// Stateless — there is no per-module cache to invalidate. Pre-decoded IL would buy little
/// over the underlying <c>PEReader</c> body lookups; if a cache is ever introduced this class
/// would also implement <see cref="IModuleScopedCache"/>.
/// </remarks>
internal sealed class IlBodyReader
{
    /// <summary>Default cap on raw IL bytes encoded by <see cref="GetIlBody"/>. 4 KiB.</summary>
    public const int DefaultIlMaxBytes = 4 * 1024;

    private readonly MethodResolver _methods;

    public IlBodyReader(MethodResolver methods) => _methods = methods;

    public IlBodyResult GetIlBody(MethodIdentity identity, int maxBytes = 0, CancellationToken cancellationToken = default)
    {
        var common = _methods.TryResolveMethod(identity);
        if (common.Error is not null) return IlBodyResult.Fail(common.Error);
        var (module, methodHandle) = (common.Module!, common.Handle);

        var def = module.MD.GetMethodDefinition(methodHandle);
        if (def.RelativeVirtualAddress == 0)
        {
            // Abstract / extern / trimmed body — emit an empty body rather than failing.
            var handleStr = HandleSyntax.FormatMethod(module.Mvid, identity.MetadataToken);
            return IlBodyResult.Ok(new IlMethodBody(
                module.Mvid, identity.MetadataToken, handleStr,
                IlSize: 0, MaxStack: 0, ExceptionRegionCount: 0, InstructionCount: 0,
                IlHex: string.Empty, IlTruncated: false));
        }

        try
        {
            var body = module.PE.GetMethodBody(def.RelativeVirtualAddress);
            var ilBytes = body.GetILBytes() ?? Array.Empty<byte>();
            var cap = maxBytes > 0 ? maxBytes : DefaultIlMaxBytes;
            var hexLen = Math.Min(ilBytes.Length, cap);
            var hex = Convert.ToHexString(ilBytes.AsSpan(0, hexLen));
            var truncated = hexLen < ilBytes.Length;
            var instructions = CountInstructions(ilBytes, cancellationToken);

            return IlBodyResult.Ok(new IlMethodBody(
                module.Mvid, identity.MetadataToken,
                HandleSyntax.FormatMethod(module.Mvid, identity.MetadataToken),
                IlSize: ilBytes.Length,
                MaxStack: body.MaxStack,
                ExceptionRegionCount: body.ExceptionRegions.Length,
                InstructionCount: instructions,
                IlHex: hex,
                IlTruncated: truncated));
        }
        catch (BadImageFormatException ex)
        {
            return IlBodyResult.Fail(new AssemblyError(ErrorKinds.ModuleLoadFailed,
                "method body is malformed.", ex.Message));
        }
    }

    public IlScanReadResult ScanIl(MethodIdentity identity, CancellationToken cancellationToken = default)
    {
        var common = _methods.TryResolveMethod(identity);
        if (common.Error is not null) return IlScanReadResult.Fail(common.Error);
        var (module, methodHandle) = (common.Module!, common.Handle);

        var def = module.MD.GetMethodDefinition(methodHandle);
        var handleStr = HandleSyntax.FormatMethod(module.Mvid, identity.MetadataToken);

        if (def.RelativeVirtualAddress == 0)
        {
            return IlScanReadResult.Ok(new IlScanResult(
                module.Mvid, identity.MetadataToken, handleStr,
                InstructionCount: 0,
                Calls: Array.Empty<IlSymbolRef>(),
                Fields: Array.Empty<IlSymbolRef>(),
                Types: Array.Empty<IlSymbolRef>(),
                Strings: Array.Empty<string>()));
        }

        byte[] ilBytes;
        try
        {
            var body = module.PE.GetMethodBody(def.RelativeVirtualAddress);
            ilBytes = body.GetILBytes() ?? Array.Empty<byte>();
        }
        catch (BadImageFormatException ex)
        {
            return IlScanReadResult.Fail(new AssemblyError(ErrorKinds.ModuleLoadFailed,
                "method body is malformed.", ex.Message));
        }

        var calls = new List<IlSymbolRef>();
        var fields = new List<IlSymbolRef>();
        var types = new List<IlSymbolRef>();
        var strings = new List<string>();
        int instructions = 0;

        var span = ilBytes.AsSpan();
        int pos = 0;
        int sinceCancelCheck = 0;
        while (pos < span.Length)
        {
            // Cancellation is checked every 256 instructions to keep the hot loop cheap; an
            // unbounded malformed body otherwise spins until the whole IL is walked.
            if ((sinceCancelCheck++ & 0xFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            instructions++;
            var b1 = span[pos++];
            IlOpcodeTable.Op op;
            if (b1 == 0xFE)
            {
                if (pos >= span.Length) break;
                op = IlOpcodeTable.TwoByteOp(span[pos++]);
            }
            else
            {
                op = IlOpcodeTable.OneByteOp(b1);
            }

            var size = IlOpcodeTable.OperandSize(op);
            if (size == -1) // switch: 4-byte N followed by N 4-byte offsets
            {
                if (pos + 4 > span.Length) break;
                var n = BitConverter.ToInt32(span.Slice(pos, 4));
                // Validate without overflow: n quads must fit in the remaining IL.
                if (n < 0 || n > (span.Length - pos - 4) / 4) break;
                pos += 4 + n * 4;
                continue;
            }

            int token = 0;
            if (size == 4 && pos + 4 <= span.Length)
                token = BitConverter.ToInt32(span.Slice(pos, 4));

            switch (op)
            {
                case IlOpcodeTable.Op.InlineMethod:
                    calls.Add(BuildSymbolRef(module, token));
                    break;
                case IlOpcodeTable.Op.InlineField:
                    fields.Add(BuildSymbolRef(module, token));
                    break;
                case IlOpcodeTable.Op.InlineType:
                    types.Add(BuildSymbolRef(module, token));
                    break;
                case IlOpcodeTable.Op.InlineTok:
                    // Could be method/field/type — classify by handle kind.
                    AddTokenRef(module, token, calls, fields, types);
                    break;
                case IlOpcodeTable.Op.InlineString:
                    var s = TryReadUserString(module, token);
                    if (s is not null) strings.Add(s);
                    break;
            }

            pos += Math.Max(0, size);
        }

        return IlScanReadResult.Ok(new IlScanResult(
            module.Mvid, identity.MetadataToken, handleStr,
            instructions, calls, fields, types, strings));
    }

    internal static int CountInstructions(byte[] il, CancellationToken cancellationToken = default)
    {
        int n = 0, pos = 0;
        int sinceCancelCheck = 0;
        var span = il.AsSpan();
        while (pos < span.Length)
        {
            if ((sinceCancelCheck++ & 0xFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            n++;
            var b1 = span[pos++];
            IlOpcodeTable.Op op;
            if (b1 == 0xFE)
            {
                if (pos >= span.Length) break;
                op = IlOpcodeTable.TwoByteOp(span[pos++]);
            }
            else op = IlOpcodeTable.OneByteOp(b1);

            var size = IlOpcodeTable.OperandSize(op);
            if (size == -1)
            {
                if (pos + 4 > span.Length) break;
                var count = BitConverter.ToInt32(span.Slice(pos, 4));
                if (count < 0 || count > (span.Length - pos - 4) / 4) break;
                pos += 4 + count * 4;
                continue;
            }
            pos += Math.Max(0, size);
        }
        return n;
    }

    internal static void AddTokenRef(ModuleHandle m, int token, List<IlSymbolRef> calls,
        List<IlSymbolRef> fields, List<IlSymbolRef> types)
    {
        try
        {
            var h = MetadataTokens.Handle(token);
            // MemberReference rows (table 0x0A) can carry either a method or a field signature;
            // route them to the correct bucket using the symbol Kind populated by
            // BuildSymbolRef (issue #86 — pre-fix every MemberRef leaked into `calls`).
            if (h.Kind == HandleKind.MemberReference)
            {
                var symbol = BuildSymbolRef(m, token);
                var bucket = symbol.Kind == IlSymbolKind.FieldMemberRef ? fields : calls;
                bucket.Add(symbol);
                return;
            }

            var target = h.Kind switch
            {
                HandleKind.MethodDefinition or HandleKind.MethodSpecification => calls,
                HandleKind.FieldDefinition => fields,
                HandleKind.TypeDefinition or HandleKind.TypeReference or HandleKind.TypeSpecification => types,
                _ => (List<IlSymbolRef>?)null,
            };
            target?.Add(BuildSymbolRef(m, token));
        }
        catch (BadImageFormatException) { /* ignore malformed token */ }
    }
}
