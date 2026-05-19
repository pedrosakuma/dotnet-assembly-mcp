using System.Reflection;
using DotnetAssemblyMcp.Core.Identity;
using DotnetAssemblyMcp.Core.Metadata;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Cross-module Tier-4 tests. SampleConsumer references SampleLib; loading both modules into
/// the same index should let <c>find_callers(OrderService.Process(int))</c> discover
/// <c>ConsumerService.RunInt</c> via the outbound MemberRef recorded in SampleConsumer's xref.
/// </summary>
public sealed class CrossModuleXrefTests
{
    private static string SampleLibPath => typeof(SampleLib.OrderService).Assembly.Location;
    private static string SampleConsumerPath => typeof(SampleConsumer.ConsumerService).Assembly.Location;

    private static MethodInfo MethodOf(Type t, string name, params Type[] args) =>
        t.GetMethod(
            name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static,
            binder: null,
            types: args,
            modifiers: null)!;

    private static MethodIdentity IdentityOf(MethodInfo mi) =>
        new(mi.Module.ModuleVersionId, mi.MetadataToken);

    [Fact]
    public void FindCallers_discovers_consumer_calling_into_lib()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);
        index.Load(SampleConsumerPath);

        var processInt = MethodOf(typeof(SampleLib.OrderService), "Process", typeof(int));
        var runInt = MethodOf(typeof(SampleConsumer.ConsumerService), "RunInt", typeof(int));

        var result = index.FindCallers(IdentityOf(processInt));

        result.IsSuccess.Should().BeTrue();
        result.Result!.ModulesSearched.Should().Be(2);
        result.Result.Callers.Should().Contain(c =>
            c.ModuleVersionId == runInt.Module.ModuleVersionId &&
            c.MetadataToken == runInt.MetadataToken);
    }

    [Fact]
    public void FindCallers_cross_module_respects_overloads()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);
        index.Load(SampleConsumerPath);

        var processString = MethodOf(typeof(SampleLib.OrderService), "Process", typeof(string));
        var runString = MethodOf(typeof(SampleConsumer.ConsumerService), "RunString", typeof(string));
        var runInt = MethodOf(typeof(SampleConsumer.ConsumerService), "RunInt", typeof(int));

        var result = index.FindCallers(IdentityOf(processString));

        result.IsSuccess.Should().BeTrue();
        result.Result!.Callers.Select(c => c.MetadataToken)
            .Should().Contain(runString.MetadataToken)
            .And.NotContain(runInt.MetadataToken);
    }

    [Fact]
    public void FindCallers_without_consumer_loaded_finds_no_cross_module_caller()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);

        var processInt = MethodOf(typeof(SampleLib.OrderService), "Process", typeof(int));
        var result = index.FindCallers(IdentityOf(processInt));

        result.IsSuccess.Should().BeTrue();
        result.Result!.ModulesSearched.Should().Be(1);
        var consumerMvid = typeof(SampleConsumer.ConsumerService).Module.ModuleVersionId;
        result.Result.Callers.Should().NotContain(c => c.ModuleVersionId == consumerMvid);
    }
}
