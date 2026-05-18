using System.Reflection;
using System.Reflection.Emit;

namespace DotnetAssemblyMcp.Core.Metadata;

/// <summary>
/// Static lookup table mapping every IL opcode value (1-byte and FE-prefixed 2-byte) to its
/// operand kind. Built once at type-init time from <see cref="System.Reflection.Emit.OpCodes"/>
/// reflection — the runtime is the source of truth, so we cannot get the per-opcode operand
/// size wrong.
/// </summary>
internal static class IlOpcodeTable
{
    public enum Op
    {
        Unknown,
        InlineNone,
        InlineI,        // 4-byte int
        InlineI8,       // 8-byte long
        InlineR,        // 8-byte double
        ShortInlineI,   // 1-byte
        ShortInlineR,   // 4-byte float
        ShortInlineVar, // 1-byte
        InlineVar,      // 2-byte
        ShortInlineBrTarget, // 1-byte
        InlineBrTarget, // 4-byte
        InlineSwitch,   // variable
        InlineMethod,   // 4-byte token
        InlineField,    // 4-byte token
        InlineType,     // 4-byte token
        InlineTok,      // 4-byte token (typeDef/Ref, methodDef/Ref, fieldDef/Ref)
        InlineString,   // 4-byte userstring token
        InlineSig,      // 4-byte token
        InlinePhi,
    }

    private static readonly Op[] OneByte = new Op[256];
    private static readonly Op[] TwoByte = new Op[256];

    static IlOpcodeTable()
    {
        for (var i = 0; i < 256; i++) { OneByte[i] = Op.Unknown; TwoByte[i] = Op.Unknown; }
        foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (f.GetValue(null) is not OpCode oc) continue;
            var op = Classify(oc.OperandType);
            var v = (ushort)oc.Value;
            if ((v & 0xFF00) == 0xFE00) TwoByte[v & 0xFF] = op;
            else OneByte[v & 0xFF] = op;
        }
    }

    public static Op Classify(OperandType t) => t switch
    {
        OperandType.InlineNone => Op.InlineNone,
        OperandType.InlineI => Op.InlineI,
        OperandType.InlineI8 => Op.InlineI8,
        OperandType.InlineR => Op.InlineR,
        OperandType.ShortInlineI => Op.ShortInlineI,
        OperandType.ShortInlineR => Op.ShortInlineR,
        OperandType.ShortInlineVar => Op.ShortInlineVar,
        OperandType.InlineVar => Op.InlineVar,
        OperandType.ShortInlineBrTarget => Op.ShortInlineBrTarget,
        OperandType.InlineBrTarget => Op.InlineBrTarget,
        OperandType.InlineSwitch => Op.InlineSwitch,
        OperandType.InlineMethod => Op.InlineMethod,
        OperandType.InlineField => Op.InlineField,
        OperandType.InlineType => Op.InlineType,
        OperandType.InlineTok => Op.InlineTok,
        OperandType.InlineString => Op.InlineString,
        OperandType.InlineSig => Op.InlineSig,
#pragma warning disable CS0618
        OperandType.InlinePhi => Op.InlinePhi,
#pragma warning restore CS0618
        _ => Op.Unknown,
    };

    /// <summary>Resolves a 1-byte opcode value to its operand kind. Returns Unknown for unassigned slots.</summary>
    public static Op OneByteOp(byte b) => OneByte[b];
    /// <summary>Resolves a 2-byte (FE-prefixed) opcode value to its operand kind.</summary>
    public static Op TwoByteOp(byte b) => TwoByte[b];

    /// <summary>Byte size of the operand for fixed-size operand kinds. Returns -1 for <see cref="Op.InlineSwitch"/>.</summary>
    public static int OperandSize(Op op) => op switch
    {
        Op.InlineNone => 0,
        Op.ShortInlineI => 1,
        Op.ShortInlineVar => 1,
        Op.ShortInlineBrTarget => 1,
        Op.InlineVar => 2,
        Op.InlineI => 4,
        Op.ShortInlineR => 4,
        Op.InlineBrTarget => 4,
        Op.InlineMethod => 4,
        Op.InlineField => 4,
        Op.InlineType => 4,
        Op.InlineTok => 4,
        Op.InlineString => 4,
        Op.InlineSig => 4,
        Op.InlineI8 => 8,
        Op.InlineR => 8,
        Op.InlineSwitch => -1,
        _ => 0,
    };
}
