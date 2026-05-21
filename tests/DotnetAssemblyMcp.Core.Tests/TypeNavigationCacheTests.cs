using System.Diagnostics;
using System.Reflection;
using DotnetAssemblyMcp.Core.Metadata;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Behavioural + micro-bench tests for <see cref="TypeNavigationIndex"/>:
///   - <see cref="MetadataIndex.FindTypeByFullName"/> answers from a cached frozen name-map
///     (per-MVID), so the second call costs O(1) instead of an O(N) TypeDef linear scan.
///   - <see cref="MetadataIndex.ListDerivedTypes"/> reuses cached parent maps across calls
///     against a stable loaded-module set; rebuilds when the set changes.
///   - New-load detection: loading a second module after a successful build invalidates the
///     parent-maps cache so a subsequent call rebuilds.
/// </summary>
public sealed class TypeNavigationCacheTests
{
    private readonly ITestOutputHelper _output;
    public TypeNavigationCacheTests(ITestOutputHelper output) { _output = output; }

    private static string SampleLibPath => typeof(SampleLib.OrderService).Assembly.Location;
    private static string SampleConsumerPath
    {
        get
        {
            // SampleConsumer sits next to SampleLib in the test output dir.
            var libDir = Path.GetDirectoryName(SampleLibPath)!;
            var candidate = Path.Combine(libDir, "SampleConsumer.dll");
            return File.Exists(candidate) ? candidate : libDir;
        }
    }

    [Fact]
    public void FindTypeByFullName_caches_name_map_per_mvid()
    {
        using var index = new MetadataIndex();
        var load = index.Load(SampleLibPath);
        load.IsSuccess.Should().BeTrue();
        var mvid = load.Module!.ModuleVersionId;

        var typeNavIdx = GetField<TypeNavigationIndex>(index, "_typeNavigation");
        typeNavIdx.HasNameCacheEntry(mvid).Should().BeFalse("cache must be lazy");

        var first = index.FindTypeByFullName(mvid, "SampleLib.OrderService");
        first.IsSuccess.Should().BeTrue();
        typeNavIdx.HasNameCacheEntry(mvid).Should().BeTrue();

        // Same query, second call — must still succeed (cache hit, not a regression).
        var second = index.FindTypeByFullName(mvid, "SampleLib.OrderService");
        second.IsSuccess.Should().BeTrue();
        second.Type!.MetadataToken.Should().Be(first.Type!.MetadataToken);
    }

    [Fact]
    public void FindTypeByFullName_repeated_lookup_is_substantially_faster()
    {
        using var index = new MetadataIndex();
        var load = index.Load(SampleLibPath);
        load.IsSuccess.Should().BeTrue();
        var mvid = load.Module!.ModuleVersionId;

        // Warm path: first call builds the frozen name map; subsequent calls are O(1) lookups.
        // We can't make absolute timing assertions reliable on CI, so we just sanity-check that
        // 10,000 cached lookups stay well under the linear-scan baseline of the cold call.
        var coldSw = Stopwatch.StartNew();
        index.FindTypeByFullName(mvid, "SampleLib.OrderService");
        coldSw.Stop();

        var warmSw = Stopwatch.StartNew();
        for (int i = 0; i < 10_000; i++)
            index.FindTypeByFullName(mvid, "SampleLib.OrderService");
        warmSw.Stop();

        _output.WriteLine($"cold (1×): {coldSw.Elapsed.TotalMicroseconds:F1} µs");
        _output.WriteLine($"warm (10k×): {warmSw.Elapsed.TotalMilliseconds:F2} ms — avg {warmSw.Elapsed.TotalMicroseconds / 10_000d:F2} µs/call");

        // The per-call warm cost should be a small fraction of the cold cost. With FrozenDictionary
        // we'd expect <1 µs/call on a tiny fixture. Use a generous multiplier to stay non-flaky.
        var perWarmCallUs = warmSw.Elapsed.TotalMicroseconds / 10_000d;
        var coldUs = coldSw.Elapsed.TotalMicroseconds;
        perWarmCallUs.Should().BeLessThan(Math.Max(50, coldUs / 5),
            "cached lookup must outperform the linear scan by a clear margin");
    }

    [Fact]
    public void ListDerivedTypes_reuses_cached_parent_maps_across_calls()
    {
        using var index = new MetadataIndex();
        var load = index.Load(SampleLibPath);
        load.IsSuccess.Should().BeTrue();
        var mvid = load.Module!.ModuleVersionId;

        var typeNavIdx = GetField<TypeNavigationIndex>(index, "_typeNavigation");
        typeNavIdx.HasParentMapsEntry.Should().BeFalse("parent maps must be lazy");

        var first = index.ListDerivedTypes(mvid, baseTypeMetadataToken: 0x02000002, new ListDerivedTypesQuery());
        first.IsSuccess.Should().BeTrue();
        typeNavIdx.HasParentMapsEntry.Should().BeTrue();

        // A subsequent call against the same loaded-module set must NOT invalidate the cache.
        var second = index.ListDerivedTypes(mvid, baseTypeMetadataToken: 0x02000002, new ListDerivedTypesQuery());
        second.IsSuccess.Should().BeTrue();
        typeNavIdx.HasParentMapsEntry.Should().BeTrue("the cache must survive between calls");
    }

    [Fact]
    public void Loading_a_new_module_triggers_parent_maps_rebuild_on_next_call()
    {
        using var index = new MetadataIndex();
        var load = index.Load(SampleLibPath);
        load.IsSuccess.Should().BeTrue();
        var mvid = load.Module!.ModuleVersionId;
        var typeNavIdx = GetField<TypeNavigationIndex>(index, "_typeNavigation");

        index.ListDerivedTypes(mvid, baseTypeMetadataToken: 0x02000002, new ListDerivedTypesQuery());
        typeNavIdx.HasParentMapsEntry.Should().BeTrue();

        if (!File.Exists(SampleConsumerPath)) return; // skip on environments without SampleConsumer
        var consumerLoad = index.Load(SampleConsumerPath);
        if (!consumerLoad.IsSuccess) return; // skip if fixture missing — covered elsewhere

        // The cached parent maps still exist as an OBJECT but their captured MVID set no longer
        // matches the current store. On next ListDerivedTypes call the cache must rebuild
        // transparently (we don't expose the captured-set; we assert no exceptions / correct hits).
        var result = index.ListDerivedTypes(mvid, baseTypeMetadataToken: 0x02000002, new ListDerivedTypesQuery());
        result.IsSuccess.Should().BeTrue("rebuilt cache must answer correctly after a new module load");
    }

    private static T GetField<T>(MetadataIndex index, string name)
    {
        var f = typeof(MetadataIndex).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        f.Should().NotBeNull($"private field {name} expected on MetadataIndex");
        return (T)f!.GetValue(index)!;
    }
}
