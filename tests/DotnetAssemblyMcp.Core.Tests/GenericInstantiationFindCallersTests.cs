using System.Reflection;
using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Identity;
using DotnetAssemblyMcp.Core.Metadata;
using DotnetAssemblyMcp.Server.Tools;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Phase Ω(e): <c>find_callers</c> narrowed by a closed method-level instantiation. The
/// xref index records every call site against the open MethodDef, so when the caller
/// supplies <c>genericMethodArguments</c> the index post-walks each candidate's IL for
/// a <c>MethodSpec</c> whose <c>Instantiation</c> blob matches element-wise.
/// </summary>
public sealed class GenericInstantiationFindCallersTests
{
    private static string SampleLibPath => typeof(SampleLib.OrderService).Assembly.Location;
    private static string SampleConsumerPath => typeof(SampleConsumer.ConsumerService).Assembly.Location;

    private static MethodInfo MethodOf(Type t, string name) =>
        t.GetMethod(
            name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)!;

    private static MethodIdentity IdentityOf(MethodInfo mi) => new(mi.Module.ModuleVersionId, mi.MetadataToken);

    [Fact]
    public void Unfiltered_find_callers_returns_all_echo_call_sites()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);
        index.Load(SampleConsumerPath);

        var echoOpen = MethodOf(typeof(SampleLib.OrderService), "Echo");
        var result = index.FindCallers(IdentityOf(echoOpen));

        result.IsSuccess.Should().BeTrue();
        // SampleConsumer has 3 Echo call sites (CallEchoOfInt, CallEchoOfString, CallEchoOfStringAgain).
        result.Result!.Callers.Should().HaveCountGreaterThanOrEqualTo(3);
    }

    [Fact]
    public void Filtered_by_int_instantiation_returns_only_int_callers()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);
        index.Load(SampleConsumerPath);

        var echoOpen = MethodOf(typeof(SampleLib.OrderService), "Echo");
        var callInt = MethodOf(typeof(SampleConsumer.ConsumerService), "CallEchoOfInt");
        var callStr = MethodOf(typeof(SampleConsumer.ConsumerService), "CallEchoOfString");

        var identity = new MethodIdentity(
            echoOpen.Module.ModuleVersionId, echoOpen.MetadataToken,
            MethodGenericArguments: ParseArgs("System.Int32"));

        var result = index.FindCallers(identity);

        result.IsSuccess.Should().BeTrue();
        var tokens = result.Result!.Callers.Select(c => c.MetadataToken).ToList();
        tokens.Should().Contain(callInt.MetadataToken);
        tokens.Should().NotContain(callStr.MetadataToken);
    }

    [Fact]
    public void Filtered_by_string_instantiation_returns_only_string_callers()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);
        index.Load(SampleConsumerPath);

        var echoOpen = MethodOf(typeof(SampleLib.OrderService), "Echo");
        var callInt = MethodOf(typeof(SampleConsumer.ConsumerService), "CallEchoOfInt");
        var callStr = MethodOf(typeof(SampleConsumer.ConsumerService), "CallEchoOfString");
        var callStrAgain = MethodOf(typeof(SampleConsumer.ConsumerService), "CallEchoOfStringAgain");

        var identity = new MethodIdentity(
            echoOpen.Module.ModuleVersionId, echoOpen.MetadataToken,
            MethodGenericArguments: ParseArgs("System.String"));

        var result = index.FindCallers(identity);

        result.IsSuccess.Should().BeTrue();
        var tokens = result.Result!.Callers.Select(c => c.MetadataToken).ToList();
        tokens.Should().Contain(callStr.MetadataToken);
        tokens.Should().Contain(callStrAgain.MetadataToken);
        tokens.Should().NotContain(callInt.MetadataToken);
    }

    [Fact]
    public void Find_callers_tool_accepts_generic_method_arguments_and_filters()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);
        index.Load(SampleConsumerPath);

        var echoOpen = MethodOf(typeof(SampleLib.OrderService), "Echo");
        var callInt = MethodOf(typeof(SampleConsumer.ConsumerService), "CallEchoOfInt");

        var result = AssemblyTools.FindCallers(
            index,
            echoOpen.Module.ModuleVersionId.ToString("D"),
            $"0x{echoOpen.MetadataToken:X8}",
            genericMethodArguments: ["System.Int32"]);

        result.IsError.Should().BeFalse(result.Summary);
        result.Data!.Callers.Select(c => c.MetadataToken).Should().Contain(callInt.MetadataToken);
    }

    [Fact]
    public void Find_callers_batch_propagates_per_item_generic_args()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);
        index.Load(SampleConsumerPath);

        var echoOpen = MethodOf(typeof(SampleLib.OrderService), "Echo");
        var callInt = MethodOf(typeof(SampleConsumer.ConsumerService), "CallEchoOfInt");
        var callStr = MethodOf(typeof(SampleConsumer.ConsumerService), "CallEchoOfString");

        var batch = new MethodBatchItem[]
        {
            new(
                echoOpen.Module.ModuleVersionId.ToString("D"),
                $"0x{echoOpen.MetadataToken:X8}",
                GenericMethodArguments: ["System.Int32"]),
            new(
                echoOpen.Module.ModuleVersionId.ToString("D"),
                $"0x{echoOpen.MetadataToken:X8}",
                GenericMethodArguments: ["System.String"]),
        };

        var result = AssemblyTools.FindCallersBatch(index, batch);

        result.IsError.Should().BeFalse(result.Summary);
        var intItem = result.Data!.Results[0];
        var strItem = result.Data.Results[1];
        intItem.Ok.Should().BeTrue();
        strItem.Ok.Should().BeTrue();

        intItem.Data!.Callers.Select(c => c.MetadataToken)
            .Should().Contain(callInt.MetadataToken)
            .And.NotContain(callStr.MetadataToken);
        strItem.Data!.Callers.Select(c => c.MetadataToken)
            .Should().Contain(callStr.MetadataToken)
            .And.NotContain(callInt.MetadataToken);
    }

    [Fact]
    public void Get_methods_batch_propagates_per_item_generic_args()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);

        var echoOpen = MethodOf(typeof(SampleLib.OrderService), "Echo");

        var batch = new MethodBatchItem[]
        {
            new(
                echoOpen.Module.ModuleVersionId.ToString("D"),
                $"0x{echoOpen.MetadataToken:X8}",
                GenericMethodArguments: ["System.Int32"]),
        };

        var result = AssemblyTools.GetMethods(index, batch);

        result.IsError.Should().BeFalse(result.Summary);
        result.Data!.Results[0].Ok.Should().BeTrue();
        result.Data.Results[0].Data!.Signature.Should().Contain("System.Int32");
        result.Data.Results[0].Data!.Signature.Should().NotContain("!!0");
    }

    private static List<GenericTypeName> ParseArgs(params string[] raw)
    {
        var list = new List<GenericTypeName>(raw.Length);
        foreach (var r in raw)
        {
            GenericTypeName.TryParse(r, out var node, out _, out _).Should().BeTrue();
            list.Add(node!);
        }
        return list;
    }
}
