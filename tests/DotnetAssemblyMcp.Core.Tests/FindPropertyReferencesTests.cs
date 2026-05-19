using System.Reflection;
using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Metadata;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Tier-4 reverse property-access lookup tests. Validates that
/// <see cref="MetadataIndex.FindPropertyReferences"/> resolves the property's getter / setter
/// and reuses the existing call-xref index to list every caller, tagged by accessor.
/// </summary>
public sealed class FindPropertyReferencesTests
{
    private static string SampleLibPath => typeof(SampleLib.OrderService).Assembly.Location;
    private static string SampleConsumerPath => typeof(SampleConsumer.ConsumerService).Assembly.Location;
    private static Guid SampleLibMvid => typeof(SampleLib.OrderService).Assembly.ManifestModule.ModuleVersionId;
    private static Guid SampleConsumerMvid => typeof(SampleConsumer.ConsumerService).Assembly.ManifestModule.ModuleVersionId;

    private static int EmailPropertyToken =>
        typeof(SampleLib.CustomerDto).GetProperty("Email")!.MetadataToken;

    private static MetadataIndex LoadBoth()
    {
        var index = new MetadataIndex();
        index.Load(SampleLibPath);
        index.Load(SampleConsumerPath);
        return index;
    }

    [Fact]
    public void Finds_both_getter_and_setter_calls_across_modules()
    {
        using var index = LoadBoth();

        var result = index.FindPropertyReferences(SampleLibMvid, EmailPropertyToken);

        result.IsSuccess.Should().BeTrue();
        var r = result.Result!;
        r.TargetModuleVersionId.Should().Be(SampleLibMvid);

        // SampleConsumer.RoundTripEmail calls both accessors.
        r.References.Should().Contain(h =>
            h.ModuleVersionId == SampleConsumerMvid
            && h.CallerDisplay.Contains("RoundTripEmail", StringComparison.Ordinal)
            && h.Accessor == PropertyAccessor.Getter);
        r.References.Should().Contain(h =>
            h.ModuleVersionId == SampleConsumerMvid
            && h.CallerDisplay.Contains("RoundTripEmail", StringComparison.Ordinal)
            && h.Accessor == PropertyAccessor.Setter);
    }

    [Fact]
    public void GetterOnly_filter_excludes_setter_hits()
    {
        using var index = LoadBoth();

        var result = index.FindPropertyReferences(SampleLibMvid, EmailPropertyToken,
            PropertyAccessorFilter.GetterOnly);

        result.IsSuccess.Should().BeTrue();
        result.Result!.References.Should().OnlyContain(h => h.Accessor == PropertyAccessor.Getter);
    }

    [Fact]
    public void SetterOnly_filter_excludes_getter_hits()
    {
        using var index = LoadBoth();

        var result = index.FindPropertyReferences(SampleLibMvid, EmailPropertyToken,
            PropertyAccessorFilter.SetterOnly);

        result.IsSuccess.Should().BeTrue();
        result.Result!.References.Should().OnlyContain(h => h.Accessor == PropertyAccessor.Setter);
    }

    [Fact]
    public void Bad_token_kind_returns_invalid_argument()
    {
        using var index = LoadBoth();

        // Pass a field token instead of a property token.
        var fieldToken = typeof(SampleLib.CounterFixture)
            .GetField("Count", BindingFlags.Public | BindingFlags.Static)!.MetadataToken;

        var result = index.FindPropertyReferences(SampleLibMvid, fieldToken);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.InvalidArgument);
    }
}
