using System.Reflection;
using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Metadata;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Tier-4 reverse event-accessor lookup tests. Validates that
/// <see cref="MetadataIndex.FindEventReferences"/> resolves the event's adder/remover/raiser
/// MethodDefs and reuses the existing call-xref index to list every caller, tagged by accessor.
/// </summary>
public sealed class FindEventReferencesTests
{
    private static string SampleLibPath => typeof(SampleLib.OrderService).Assembly.Location;
    private static string SampleConsumerPath => typeof(SampleConsumer.ConsumerService).Assembly.Location;
    private static Guid SampleLibMvid => typeof(SampleLib.OrderService).Assembly.ManifestModule.ModuleVersionId;
    private static Guid SampleConsumerMvid => typeof(SampleConsumer.ConsumerService).Assembly.ManifestModule.ModuleVersionId;

    private static int ChangedEventToken =>
        typeof(SampleLib.CustomerDto).GetEvent("Changed")!.MetadataToken;

    private static MetadataIndex LoadBoth()
    {
        var index = new MetadataIndex();
        index.Load(SampleLibPath);
        index.Load(SampleConsumerPath);
        return index;
    }

    [Fact]
    public void Finds_subscribe_and_unsubscribe_across_modules()
    {
        using var index = LoadBoth();

        var result = index.FindEventReferences(SampleLibMvid, ChangedEventToken);

        result.IsSuccess.Should().BeTrue();
        var r = result.Result!;
        r.TargetModuleVersionId.Should().Be(SampleLibMvid);

        // SampleConsumer.CrossModuleEventConsumer.SubscribeChanged → adder.
        r.References.Should().Contain(h =>
            h.ModuleVersionId == SampleConsumerMvid
            && h.CallerDisplay.Contains("SubscribeChanged", StringComparison.Ordinal)
            && h.Accessor == EventAccessor.Adder);

        // SampleConsumer.CrossModuleEventConsumer.UnsubscribeChanged → remover.
        r.References.Should().Contain(h =>
            h.ModuleVersionId == SampleConsumerMvid
            && h.CallerDisplay.Contains("UnsubscribeChanged", StringComparison.Ordinal)
            && h.Accessor == EventAccessor.Remover);
    }

    [Fact]
    public void AdderOnly_filter_excludes_remover_hits()
    {
        using var index = LoadBoth();

        var result = index.FindEventReferences(SampleLibMvid, ChangedEventToken,
            EventAccessorFilter.AdderOnly);

        result.IsSuccess.Should().BeTrue();
        result.Result!.References.Should().OnlyContain(h => h.Accessor == EventAccessor.Adder);
    }

    [Fact]
    public void RemoverOnly_filter_excludes_adder_hits()
    {
        using var index = LoadBoth();

        var result = index.FindEventReferences(SampleLibMvid, ChangedEventToken,
            EventAccessorFilter.RemoverOnly);

        result.IsSuccess.Should().BeTrue();
        result.Result!.References.Should().OnlyContain(h => h.Accessor == EventAccessor.Remover);
    }

    [Fact]
    public void Bad_token_kind_returns_invalid_argument()
    {
        using var index = LoadBoth();

        // Pass a property token instead of an event token.
        var propertyToken = typeof(SampleLib.CustomerDto)
            .GetProperty("Email")!.MetadataToken;

        var result = index.FindEventReferences(SampleLibMvid, propertyToken);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.InvalidArgument);
    }
}
