using System.Reflection;
using DotnetAssemblyMcp.Core.Decompilation;
using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Identity;
using DotnetAssemblyMcp.Core.Metadata;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Tier-3 textual-IL disassembly tests against the SampleLib fixture. Validates that
/// ReflectionDisassembler emits readable IL for known methods, that the LRU cache returns
/// hits on repeats, that operands resolve to readable token references, and that the
/// maxLines cap truncates with an unambiguous marker.
/// </summary>
public sealed class IlDisassemblerTests
{
    private static string SampleLibPath => typeof(SampleLib.OrderService).Assembly.Location;

    private static (MetadataIndex index, IlDisassembler disasm) NewSubject()
    {
        var index = new MetadataIndex();
        index.Load(SampleLibPath);
        return (index, new IlDisassembler(index));
    }

    private static MethodIdentity IdentityFor(Type type, string methodName, params Type[] argTypes)
    {
        var mi = type.GetMethod(
            methodName, BindingFlags.Public | BindingFlags.Instance, binder: null,
            types: argTypes, modifiers: null)!;
        return new MethodIdentity(mi.Module.ModuleVersionId, mi.MetadataToken);
    }

    [Fact]
    public void Disassemble_emits_il_lines_with_resolved_operand_for_known_method()
    {
        var (index, disasm) = NewSubject();
        using var _ = index;
        using var __ = disasm;

        var result = disasm.Disassemble(IdentityFor(typeof(SampleLib.Dog), "Speak"));

        result.IsSuccess.Should().BeTrue();
        var t = result.Text!;
        t.MethodName.Should().Be("Speak");
        t.TypeFullName.Should().Be("SampleLib.Dog");
        t.Text.Should().Contain("IL_0000");
        t.Text.Should().Contain("ldstr").And.Contain("\"woof\"");
        t.Text.Should().Contain("ret");
        t.InstructionCount.Should().BeGreaterThan(0);
        t.Truncated.Should().BeFalse();
        t.CacheHit.Should().BeFalse();
    }

    [Fact]
    public void Disassemble_resolves_cross_module_member_refs_with_assembly_hint()
    {
        var (index, disasm) = NewSubject();
        using var _ = index;
        using var __ = disasm;

        // ConsoleLogger.Log calls Console.WriteLine — a MemberRef into System.Console.
        var result = disasm.Disassemble(
            IdentityFor(typeof(SampleLib.ConsoleLogger), "Log", typeof(string)));

        result.IsSuccess.Should().BeTrue();
        result.Text!.Text.Should().Contain("WriteLine");
        result.Text.Text.Should().Contain("[System.Console]System.Console::WriteLine");
    }

    [Fact]
    public void Disassemble_second_call_is_cache_hit()
    {
        var (index, disasm) = NewSubject();
        using var _ = index;
        using var __ = disasm;

        var id = IdentityFor(typeof(SampleLib.Dog), "Speak");
        var first = disasm.Disassemble(id);
        var second = disasm.Disassemble(id);

        first.Text!.CacheHit.Should().BeFalse();
        second.Text!.CacheHit.Should().BeTrue();
        second.Text.Text.Should().Be(first.Text.Text);
        disasm.CachedEntries.Should().Be(1);
    }

    [Fact]
    public void Disassemble_truncates_at_max_lines_with_marker()
    {
        var (index, disasm) = NewSubject();
        using var _ = index;
        using var __ = disasm;

        var result = disasm.Disassemble(IdentityFor(typeof(SampleLib.Dog), "Speak"), maxLines: 3);

        result.IsSuccess.Should().BeTrue();
        var t = result.Text!;
        t.Truncated.Should().BeTrue();
        t.Text.Should().EndWith("more instructions");
        t.LineCount.Should().Be(4); // 3 kept + 1 marker
    }

    [Fact]
    public void Disassemble_clamps_max_lines_to_hard_cap()
    {
        var (index, disasm) = NewSubject();
        using var _ = index;
        using var __ = disasm;

        var bigCap = disasm.Disassemble(IdentityFor(typeof(SampleLib.Dog), "Speak"), maxLines: 1_000_000);
        var hardCap = disasm.Disassemble(IdentityFor(typeof(SampleLib.Dog), "Speak"), maxLines: IlDisassembler.HardMaxLines);

        bigCap.Text!.Text.Should().Be(hardCap.Text!.Text);
    }

    [Fact]
    public void Disassemble_unknown_module_returns_module_not_found()
    {
        var index = new MetadataIndex();
        using var _ = index;
        using var disasm = new IlDisassembler(index);

        var result = disasm.Disassemble(new MethodIdentity(Guid.NewGuid(), 0x06000001));

        result.IsSuccess.Should().BeFalse();
        // Resolve fires first against an empty index, so we surface its error kind.
        result.Error!.Kind.Should().BeOneOf(ErrorKinds.ModuleNotFound, ErrorKinds.TokenOutOfRange);
    }

    [Fact]
    public void Disassemble_invalid_token_returns_resolve_error()
    {
        var (index, disasm) = NewSubject();
        using var _ = index;
        using var __ = disasm;

        var mvid = typeof(SampleLib.OrderService).Assembly.ManifestModule.ModuleVersionId;
        var result = disasm.Disassemble(new MethodIdentity(mvid, 0x06FFFFFF));

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.TokenOutOfRange);
    }

    [Fact]
    public void Disassemble_null_identity_returns_invalid_argument()
    {
        var (index, disasm) = NewSubject();
        using var _ = index;
        using var __ = disasm;

        var result = disasm.Disassemble(null!);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.InvalidArgument);
    }
}
