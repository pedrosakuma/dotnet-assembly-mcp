using System.Reflection;
using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Metadata;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Tier-4 reverse field-access lookup tests. Validates that
/// <see cref="MetadataIndex.FindFieldReferences"/> discovers ldfld / stfld / ldflda /
/// ldsfld / stsfld / ldsflda call sites against a target field in the same module and across
/// modules, that the <see cref="FieldAccessMode"/> filter scopes correctly, and that the
/// per-module field-access index is cached.
/// </summary>
public sealed class FindFieldReferencesTests
{
    private static string SampleLibPath => typeof(SampleLib.OrderService).Assembly.Location;
    private static string SampleConsumerPath => typeof(SampleConsumer.ConsumerService).Assembly.Location;
    private static Guid SampleLibMvid => typeof(SampleLib.OrderService).Assembly.ManifestModule.ModuleVersionId;
    private static Guid SampleConsumerMvid => typeof(SampleConsumer.ConsumerService).Assembly.ManifestModule.ModuleVersionId;

    private static int CounterCountToken =>
        typeof(SampleLib.CounterFixture)
            .GetField("Count", BindingFlags.Public | BindingFlags.Static)!
            .MetadataToken;

    private static MetadataIndex LoadBoth()
    {
        var index = new MetadataIndex();
        index.Load(SampleLibPath);
        index.Load(SampleConsumerPath);
        return index;
    }

    [Fact]
    public void Finds_same_module_readers_and_writers_and_address()
    {
        using var index = LoadBoth();

        var result = index.FindFieldReferences(SampleLibMvid, CounterCountToken);

        result.IsSuccess.Should().BeTrue();
        var r = result.Result!;
        r.TargetModuleVersionId.Should().Be(SampleLibMvid);
        r.References.Should().NotBeEmpty();

        var localHits = r.References.Where(h => h.ModuleVersionId == SampleLibMvid).ToList();
        localHits.Should().Contain(h => h.AccessKind == FieldAccessKind.Read
            && h.CallerDisplay.Contains("ReadCount", StringComparison.Ordinal));
        localHits.Should().Contain(h => h.AccessKind == FieldAccessKind.Write
            && h.CallerDisplay.Contains("WriteCount", StringComparison.Ordinal));
        // Bump() emits both a read and a write of the same field.
        localHits.Where(h => h.CallerDisplay.Contains("Bump", StringComparison.Ordinal))
            .Select(h => h.AccessKind).Distinct()
            .Should().BeEquivalentTo(new[] { FieldAccessKind.Read, FieldAccessKind.Write });
    }

    [Fact]
    public void Finds_cross_module_field_access_from_consumer()
    {
        using var index = LoadBoth();

        var result = index.FindFieldReferences(SampleLibMvid, CounterCountToken);

        result.IsSuccess.Should().BeTrue();
        result.Result!.References.Should().Contain(h =>
            h.ModuleVersionId == SampleConsumerMvid
            && h.CallerDisplay.Contains("SnapshotCounter", StringComparison.Ordinal)
            && h.AccessKind == FieldAccessKind.Read);
        result.Result.References.Should().Contain(h =>
            h.ModuleVersionId == SampleConsumerMvid
            && h.CallerDisplay.Contains("BumpCounter", StringComparison.Ordinal)
            && h.AccessKind == FieldAccessKind.Write);
        result.Result.ModulesSearched.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void Mode_filter_read_excludes_writes()
    {
        using var index = LoadBoth();

        var result = index.FindFieldReferences(SampleLibMvid, CounterCountToken, FieldAccessMode.Read);

        result.IsSuccess.Should().BeTrue();
        result.Result!.References.Should().NotBeEmpty();
        result.Result.References.Should().OnlyContain(h =>
            h.AccessKind == FieldAccessKind.Read || h.AccessKind == FieldAccessKind.Address);
    }

    [Fact]
    public void Mode_filter_write_excludes_reads()
    {
        using var index = LoadBoth();

        var result = index.FindFieldReferences(SampleLibMvid, CounterCountToken, FieldAccessMode.Write);

        result.IsSuccess.Should().BeTrue();
        result.Result!.References.Should().NotBeEmpty();
        result.Result.References.Should().OnlyContain(h => h.AccessKind == FieldAccessKind.Write);
    }

    [Fact]
    public void Truncated_when_maxHits_below_total()
    {
        using var index = LoadBoth();

        var unbounded = index.FindFieldReferences(SampleLibMvid, CounterCountToken);
        unbounded.Result!.References.Count.Should().BeGreaterThan(1);

        var capped = index.FindFieldReferences(SampleLibMvid, CounterCountToken, maxHits: 1);
        capped.IsSuccess.Should().BeTrue();
        capped.Result!.References.Should().HaveCount(1);
    }

    [Fact]
    public void Second_call_is_served_from_cache()
    {
        using var index = LoadBoth();

        var first = index.FindFieldReferences(SampleLibMvid, CounterCountToken);
        var second = index.FindFieldReferences(SampleLibMvid, CounterCountToken);

        first.Result!.FromCache.Should().BeFalse();
        second.Result!.FromCache.Should().BeTrue();
    }

    [Fact]
    public void Bad_token_kind_returns_invalid_argument()
    {
        using var index = LoadBoth();

        // Pass a MethodDef token (table 0x06) instead of a FieldDef token (0x04).
        var methodToken = typeof(SampleLib.CounterFixture)
            .GetMethod("ReadCount")!.MetadataToken;

        var result = index.FindFieldReferences(SampleLibMvid, methodToken);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.InvalidArgument);
    }
}
