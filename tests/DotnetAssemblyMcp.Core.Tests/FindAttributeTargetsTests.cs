using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Metadata;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Tier-4 reverse-attribute-lookup tests. Validates that
/// <see cref="MetadataIndex.FindAttributeTargets"/> discovers every CustomAttribute target —
/// assembly / type / method / parameter / field / property / event — for a same-module
/// attribute (<c>SampleLib.FixtureMarkerAttribute</c>) and a cross-module one
/// (<c>System.ObsoleteAttribute</c>), and that the targetKinds filter scopes correctly.
/// </summary>
public sealed class FindAttributeTargetsTests
{
    private static string SampleLibPath => typeof(SampleLib.OrderService).Assembly.Location;
    private static Guid SampleLibMvid => typeof(SampleLib.OrderService).Assembly.ManifestModule.ModuleVersionId;

    private const string FixtureMarker = "SampleLib.FixtureMarkerAttribute";
    private const string Obsolete = "System.ObsoleteAttribute";

    private static MetadataIndex Load()
    {
        var index = new MetadataIndex();
        index.Load(SampleLibPath);
        return index;
    }

    [Fact]
    public void Finds_assembly_type_method_parameter_field_property_event_targets()
    {
        using var index = Load();

        var result = index.FindAttributeTargets(FixtureMarker, SampleLibMvid);

        result.IsSuccess.Should().BeTrue();
        var kinds = result.Result!.Hits.Select(h => h.Kind).Distinct().ToHashSet();
        kinds.Should().BeEquivalentTo(new[]
        {
            AttributeTargetKind.Assembly,
            AttributeTargetKind.Type,
            AttributeTargetKind.Method,
            AttributeTargetKind.Parameter,
            AttributeTargetKind.Field,
            AttributeTargetKind.Property,
            AttributeTargetKind.Event,
        });
    }

    [Fact]
    public void Finds_cross_assembly_attribute_obsolete()
    {
        using var index = Load();

        var result = index.FindAttributeTargets(Obsolete, SampleLibMvid);

        result.IsSuccess.Should().BeTrue();
        result.Result!.Hits.Should().Contain(h =>
            h.Kind == AttributeTargetKind.Method && h.Display.Contains("LegacyTouch", StringComparison.Ordinal));
    }

    [Fact]
    public void Target_kinds_filter_restricts_result()
    {
        using var index = Load();

        var result = index.FindAttributeTargets(
            FixtureMarker, SampleLibMvid,
            targetKindsFilter: new[] { AttributeTargetKind.Method, AttributeTargetKind.Parameter });

        result.IsSuccess.Should().BeTrue();
        result.Result!.Hits.Should().NotBeEmpty();
        result.Result.Hits.Should().OnlyContain(h =>
            h.Kind == AttributeTargetKind.Method || h.Kind == AttributeTargetKind.Parameter);
    }

    [Fact]
    public void Unknown_attribute_returns_no_hits()
    {
        using var index = Load();

        var result = index.FindAttributeTargets("NoSuch.Namespace.NopeAttribute", SampleLibMvid);

        result.IsSuccess.Should().BeTrue();
        result.Result!.Hits.Should().BeEmpty();
        result.Result.ModulesSearched.Should().Be(1);
    }

    [Fact]
    public void Empty_attribute_name_returns_invalid_argument()
    {
        using var index = Load();

        var result = index.FindAttributeTargets(string.Empty, SampleLibMvid);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.InvalidArgument);
    }

    [Fact]
    public void Unknown_mvid_filter_returns_module_not_found()
    {
        using var index = Load();

        var result = index.FindAttributeTargets(FixtureMarker, Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.ModuleNotFound);
    }

    [Fact]
    public void Second_call_serves_from_cache()
    {
        using var index = Load();

        var first = index.FindAttributeTargets(FixtureMarker, SampleLibMvid);
        var second = index.FindAttributeTargets(FixtureMarker, SampleLibMvid);

        first.Result!.FromCache.Should().BeFalse();
        second.Result!.FromCache.Should().BeTrue();
    }

    [Fact]
    public void Hits_carry_typed_handles_and_displays()
    {
        using var index = Load();

        var result = index.FindAttributeTargets(FixtureMarker, SampleLibMvid);

        var typeHit = result.Result!.Hits.First(h => h.Kind == AttributeTargetKind.Type);
        typeHit.Handle.Should().StartWith("t:").And.Contain(SampleLibMvid.ToString("D"));
        typeHit.Display.Should().Contain("AnnotatedService");

        var paramHit = result.Result.Hits.First(h => h.Kind == AttributeTargetKind.Parameter);
        paramHit.Handle.Should().StartWith("m:").And.Contain("#param=");
        paramHit.ParameterSequence.Should().BeGreaterThan(0);

        var fieldHit = result.Result.Hits.First(h => h.Kind == AttributeTargetKind.Field);
        fieldHit.Handle.Should().StartWith("f:");
        fieldHit.Display.Should().Contain("Age");

        var propHit = result.Result.Hits.First(h => h.Kind == AttributeTargetKind.Property);
        propHit.Handle.Should().StartWith("p:");
        propHit.Display.Should().Contain("Email");

        var evtHit = result.Result.Hits.First(h => h.Kind == AttributeTargetKind.Event);
        evtHit.Handle.Should().StartWith("e:");
        evtHit.Display.Should().Contain("Changed");
    }

    [Fact]
    public void Truncation_flag_set_when_max_hits_reached()
    {
        using var index = Load();

        var result = index.FindAttributeTargets(FixtureMarker, SampleLibMvid, maxHits: 1);

        result.IsSuccess.Should().BeTrue();
        result.Result!.Hits.Should().HaveCount(1);
        result.Result.Truncated.Should().BeTrue();
    }
}
