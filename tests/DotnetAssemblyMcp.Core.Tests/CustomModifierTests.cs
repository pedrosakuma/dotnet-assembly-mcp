using System.Linq;
using DotnetAssemblyMcp.Core.Metadata;
using DotnetAssemblyMcp.Server.Tools;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Issue #101 — verifies that custom modifiers (modreq / modopt) are surfaced inline on
/// rendered signatures. Without these, <c>in T</c> is indistinguishable from <c>ref T</c>,
/// <c>init</c>-only setters look like ordinary setters, and <c>volatile</c> fields look
/// like ordinary fields.
/// </summary>
public sealed class CustomModifierTests
{
    private static (MetadataIndex Index, System.Guid Mvid) LoadSampleLib()
    {
        var index = new MetadataIndex();
        var loaded = index.Load(typeof(SampleLib.OrderService).Assembly.Location);
        return (index, loaded.Module!.ModuleVersionId);
    }

    [Fact]
    public void In_parameter_signature_carries_InAttribute_modreq()
    {
        var (index, mvid) = LoadSampleLib();
        using (index)
        {
            int token = typeof(SampleLib.ModifierFixture)
                .GetMethod(nameof(SampleLib.ModifierFixture.FirstByIn))!.MetadataToken;
            var result = AssemblyTools.GetMethod(index, mvid.ToString("D"), $"0x{token:X8}");
            result.IsError.Should().BeFalse(result.Summary);

            var sig = result.Data!.Signature;
            sig.Should().Contain("modreq",
                "in / ref readonly parameters and returns must surface the InAttribute modreq");
            sig.Should().Contain("InAttribute",
                "the InAttribute modreq is what distinguishes 'in T' from 'ref T'");
        }
    }

    [Fact]
    public void Init_only_setter_carries_IsExternalInit_modreq()
    {
        var (index, mvid) = LoadSampleLib();
        using (index)
        {
            int token = typeof(SampleLib.ModifierFixture)
                .GetProperty(nameof(SampleLib.ModifierFixture.InitOnly))!
                .GetSetMethod(nonPublic: true)!.MetadataToken;
            var result = AssemblyTools.GetMethod(index, mvid.ToString("D"), $"0x{token:X8}");
            result.IsError.Should().BeFalse(result.Summary);

            var sig = result.Data!.Signature;
            sig.Should().Contain("modreq");
            sig.Should().Contain("IsExternalInit",
                "init-only setters carry the IsExternalInit modreq on their return type");
        }
    }

    [Fact]
    public void Volatile_field_signature_carries_IsVolatile_modreq()
    {
        var (index, mvid) = LoadSampleLib();
        using (index)
        {
            var typeHandle = $"t:{mvid:D}:0x{typeof(SampleLib.ModifierFixture).MetadataToken:X8}";
            var members = AssemblyTools.ListMembers(index, typeHandle: typeHandle, kind: MemberKind.Field);
            members.IsError.Should().BeFalse(members.Summary);

            var volField = members.Data!.Members.Single(m => m.Name == "VolatileCounter");
            volField.Signature.Should().Contain("modreq");
            volField.Signature.Should().Contain("IsVolatile",
                "volatile fields carry the IsVolatile modreq on their type");
        }
    }

    [Fact]
    public void Plain_signatures_do_not_emit_modifiers()
    {
        var (index, mvid) = LoadSampleLib();
        using (index)
        {
            int token = typeof(SampleLib.OrderService)
                .GetMethod(nameof(SampleLib.OrderService.Process), new[] { typeof(int) })!.MetadataToken;
            var result = AssemblyTools.GetMethod(index, mvid.ToString("D"), $"0x{token:X8}");
            result.IsError.Should().BeFalse(result.Summary);

            result.Data!.Signature.Should().NotContain("modreq");
            result.Data!.Signature.Should().NotContain("modopt");
        }
    }

    [Fact]
    public void Cross_module_FindCallers_resolves_modreq_bearing_callsite()
    {
        using var index = new MetadataIndex();
        index.Load(typeof(SampleLib.OrderService).Assembly.Location);
        index.Load(typeof(SampleConsumer.ConsumerService).Assembly.Location);

        var firstByIn = typeof(SampleLib.ModifierFixture)
            .GetMethod(nameof(SampleLib.ModifierFixture.FirstByIn))!;
        var caller = typeof(SampleConsumer.CrossModuleModifierConsumer)
            .GetMethod(nameof(SampleConsumer.CrossModuleModifierConsumer.CallFirstByIn))!;

        var result = index.FindCallers(new DotnetAssemblyMcp.Core.Identity.MethodIdentity(
            firstByIn.Module.ModuleVersionId, firstByIn.MetadataToken));

        result.IsSuccess.Should().BeTrue();
        result.Result!.Callers.Should().Contain(c =>
            c.ModuleVersionId == caller.Module.ModuleVersionId &&
            c.MetadataToken == caller.MetadataToken,
            because: "xref signature keys must strip modreq / modopt so the matcher pairs an " +
                "'in T' callsite with its 'in T' MethodDef regardless of consumer-facing rendering");
    }
}
