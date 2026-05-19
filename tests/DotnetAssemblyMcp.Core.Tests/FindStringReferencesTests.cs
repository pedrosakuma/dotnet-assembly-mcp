using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Metadata;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Tier-4 reverse string-literal lookup tests. Validates that <see cref="MetadataIndex.FindStringReferences"/>
/// discovers <c>ldstr</c> sites across exact/contains/regex match modes, handles MVID scoping,
/// truncates gracefully, and rebuilds its in-memory cache when the watcher invalidates a module.
/// </summary>
public sealed class FindStringReferencesTests
{
    private static string SampleLibPath => typeof(SampleLib.OrderService).Assembly.Location;
    private static string SampleConsumerPath => typeof(SampleConsumer.ConsumerService).Assembly.Location;
    private static Guid SampleLibMvid => typeof(SampleLib.OrderService).Assembly.ManifestModule.ModuleVersionId;
    private static Guid SampleConsumerMvid => typeof(SampleConsumer.ConsumerService).Assembly.ManifestModule.ModuleVersionId;

    private static MetadataIndex LoadBoth()
    {
        var index = new MetadataIndex();
        index.Load(SampleLibPath);
        index.Load(SampleConsumerPath);
        return index;
    }

    [Fact]
    public void Exact_match_finds_dog_speak_literal()
    {
        using var index = LoadBoth();

        var result = index.FindStringReferences("woof", StringMatchMode.Exact, SampleLibMvid);

        result.IsSuccess.Should().BeTrue();
        result.Result!.Hits.Should().NotBeEmpty();
        result.Result.Hits.Should().OnlyContain(h => h.Literal == "woof");
        result.Result.Hits.Should().Contain(h => h.MethodDisplay.Contains("Speak", StringComparison.Ordinal));
        result.Result.MatchMode.Should().Be(StringMatchMode.Exact);
        result.Result.Truncated.Should().BeFalse();
    }

    [Fact]
    public void Exact_match_is_case_sensitive()
    {
        using var index = LoadBoth();

        var hit = index.FindStringReferences("woof", StringMatchMode.Exact, SampleLibMvid);
        var miss = index.FindStringReferences("WOOF", StringMatchMode.Exact, SampleLibMvid);

        hit.Result!.Hits.Should().NotBeEmpty();
        miss.Result!.Hits.Should().BeEmpty();
    }

    [Fact]
    public void Contains_match_finds_substring_in_interpolated_holes()
    {
        using var index = LoadBoth();

        // OrderService.Process emits two ldstr literals from interpolated handlers
        // ("processing order " and "failed: "). The substring "order" hits the first.
        var result = index.FindStringReferences("order", StringMatchMode.Contains, SampleLibMvid);

        result.IsSuccess.Should().BeTrue();
        result.Result!.Hits.Should().Contain(h => h.Literal.Contains("order", StringComparison.Ordinal));
        result.Result.MatchMode.Should().Be(StringMatchMode.Contains);
    }

    [Fact]
    public void Regex_match_anchors_apply()
    {
        using var index = LoadBoth();

        var result = index.FindStringReferences("^woof$", StringMatchMode.Regex, SampleLibMvid);

        result.IsSuccess.Should().BeTrue();
        result.Result!.Hits.Should().NotBeEmpty();
        result.Result.Hits.Should().OnlyContain(h => h.Literal == "woof");
    }

    [Fact]
    public void Regex_invalid_pattern_returns_invalid_argument()
    {
        using var index = LoadBoth();

        var result = index.FindStringReferences("(unbalanced", StringMatchMode.Regex, SampleLibMvid);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.InvalidArgument);
    }

    [Fact]
    public void Exact_empty_query_returns_invalid_argument()
    {
        using var index = LoadBoth();

        var result = index.FindStringReferences(string.Empty, StringMatchMode.Exact, SampleLibMvid);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.InvalidArgument);
    }

    [Fact]
    public void Mvid_filter_scopes_to_one_module()
    {
        using var index = LoadBoth();

        var libOnly = index.FindStringReferences("consumer-banner", StringMatchMode.Exact, SampleLibMvid);
        libOnly.Result!.Hits.Should().BeEmpty();
        libOnly.Result.ModulesSearched.Should().Be(1);

        var consumerOnly = index.FindStringReferences("consumer-banner", StringMatchMode.Exact, SampleConsumerMvid);
        consumerOnly.Result!.Hits.Should().NotBeEmpty();
        consumerOnly.Result.Hits.Should().OnlyContain(h => h.ModuleVersionId == SampleConsumerMvid);
    }

    [Fact]
    public void No_mvid_filter_searches_every_loaded_module()
    {
        using var index = LoadBoth();

        var result = index.FindStringReferences("consumer-banner", StringMatchMode.Exact);

        result.IsSuccess.Should().BeTrue();
        result.Result!.ModulesSearched.Should().Be(2);
        result.Result.Hits.Should().Contain(h => h.ModuleVersionId == SampleConsumerMvid);
    }

    [Fact]
    public void Truncation_flag_set_when_max_hits_reached()
    {
        using var index = LoadBoth();

        // Contains-match on a single char will match many literals across the fixture; cap at 1.
        var result = index.FindStringReferences("o", StringMatchMode.Contains, SampleLibMvid, maxHits: 1);

        result.IsSuccess.Should().BeTrue();
        result.Result!.Hits.Should().HaveCount(1);
        result.Result.Truncated.Should().BeTrue();
    }

    [Fact]
    public void Module_not_loaded_filter_returns_module_not_found()
    {
        using var index = LoadBoth();

        var result = index.FindStringReferences("anything", StringMatchMode.Exact, Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.ModuleNotFound);
    }

    [Fact]
    public void Second_call_serves_from_cache()
    {
        using var index = LoadBoth();

        var first = index.FindStringReferences("woof", StringMatchMode.Exact, SampleLibMvid);
        var second = index.FindStringReferences("woof", StringMatchMode.Exact, SampleLibMvid);

        first.Result!.FromCache.Should().BeFalse();
        second.Result!.FromCache.Should().BeTrue();
    }

    [Fact]
    public void Hits_carry_il_offset_and_method_handle()
    {
        using var index = LoadBoth();

        var result = index.FindStringReferences("woof", StringMatchMode.Exact, SampleLibMvid);

        var hit = result.Result!.Hits.Should().ContainSingle().Subject;
        hit.IlOffset.Should().BeGreaterThanOrEqualTo(0);
        hit.MethodHandle.Should().StartWith("m:");
        hit.MethodHandle.Should().Contain(SampleLibMvid.ToString("D"));
    }
}
