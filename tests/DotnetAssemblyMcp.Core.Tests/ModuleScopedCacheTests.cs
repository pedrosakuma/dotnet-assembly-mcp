using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using DotnetAssemblyMcp.Core.Metadata;
using ModuleHandle = DotnetAssemblyMcp.Core.Metadata.ModuleHandle;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Direct tests for the shared <see cref="ModuleScopedCache{TData}"/> helper. The four module-
/// scoped indices in the codebase all delegate to this helper, so any race / freshness bug
/// here would compound across them. Covers:
///   - Same handle → cache hit (no rebuild).
///   - Different <see cref="ModuleHandle"/> reference with the same MVID → cache miss
///     (defeats the publish-after-invalidate race on same-MVID reload, as flagged in PR #117
///     code review).
///   - <see cref="ModuleScopedCache{TData}.Invalidate"/> → next lookup rebuilds.
///   - <c>wasCached</c> overload reports the correct flag.
/// </summary>
public sealed class ModuleScopedCacheTests
{
    private static string SampleLibPath => typeof(SampleLib.OrderService).Assembly.Location;

    [Fact]
    public void GetOrBuild_returns_cached_data_for_same_handle()
    {
        using var holder = ModuleHandleHolder.Open(SampleLibPath);
        var cache = new ModuleScopedCache<Box>();
        int builds = 0;

        var first = cache.GetOrBuild(holder.Handle, _ => { builds++; return new Box(42); }, out var firstCached);
        var second = cache.GetOrBuild(holder.Handle, _ => { builds++; return new Box(99); }, out var secondCached);

        builds.Should().Be(1, "the build delegate must run only once per handle");
        first.Value.Should().Be(42);
        second.Value.Should().Be(42);
        firstCached.Should().BeFalse();
        secondCached.Should().BeTrue();
    }

    [Fact]
    public void GetOrBuild_rejects_cache_when_handle_reference_differs_for_same_mvid()
    {
        // Open the same DLL twice → two distinct ModuleHandle instances with identical MVID.
        // This simulates the same-MVID reload race the helper is designed to defeat.
        using var first = ModuleHandleHolder.Open(SampleLibPath);
        using var second = ModuleHandleHolder.Open(SampleLibPath);
        first.Handle.Mvid.Should().Be(second.Handle.Mvid, "fixture invariant: same DLL = same MVID");
        ReferenceEquals(first.Handle, second.Handle).Should().BeFalse();

        var cache = new ModuleScopedCache<Box>();
        int builds = 0;

        cache.GetOrBuild(first.Handle, _ => { builds++; return new Box(1); });
        cache.GetOrBuild(second.Handle, _ => { builds++; return new Box(2); }, out var secondCached);

        builds.Should().Be(2, "a new ModuleHandle reference must force a rebuild even when MVID matches");
        secondCached.Should().BeFalse();
    }

    [Fact]
    public void Invalidate_forces_rebuild_on_next_lookup()
    {
        using var holder = ModuleHandleHolder.Open(SampleLibPath);
        var cache = new ModuleScopedCache<Box>();
        int builds = 0;

        cache.GetOrBuild(holder.Handle, _ => { builds++; return new Box(1); });
        cache.HasEntry(holder.Handle.Mvid).Should().BeTrue();

        cache.Invalidate(holder.Handle.Mvid);
        cache.HasEntry(holder.Handle.Mvid).Should().BeFalse();

        cache.GetOrBuild(holder.Handle, _ => { builds++; return new Box(2); }, out var cached);
        builds.Should().Be(2);
        cached.Should().BeFalse();
    }

    [Fact]
    public void OnEvict_fires_on_invalidate_and_on_rebuild_replacement()
    {
        using var first = ModuleHandleHolder.Open(SampleLibPath);
        using var second = ModuleHandleHolder.Open(SampleLibPath);

        var evicted = new List<int>();
        var cache = new ModuleScopedCache<Box>(onEvict: box => evicted.Add(box.Value));

        cache.GetOrBuild(first.Handle, _ => new Box(1));
        // Same-MVID reload (distinct handle) → entry replaced; old payload must be evicted.
        cache.GetOrBuild(second.Handle, _ => new Box(2));
        evicted.Should().ContainInOrder(1).And.HaveCount(1, "rebuild on a different handle must dispose the prior entry");

        cache.Invalidate(second.Handle.Mvid);
        evicted.Should().ContainInOrder(1, 2).And.HaveCount(2, "Invalidate must dispose the live entry");
    }

    [Fact]
    public void Clear_evicts_every_entry()
    {
        using var holder = ModuleHandleHolder.Open(SampleLibPath);
        var evicted = new List<int>();
        var cache = new ModuleScopedCache<Box>(onEvict: box => evicted.Add(box.Value));

        cache.GetOrBuild(holder.Handle, _ => new Box(7));
        cache.Clear();

        evicted.Should().ContainSingle().Which.Should().Be(7);
        cache.HasEntry(holder.Handle.Mvid).Should().BeFalse();
    }

    [Fact]
    public void OnEvict_failure_does_not_pin_stale_entry()
    {
        using var holder = ModuleHandleHolder.Open(SampleLibPath);
        var cache = new ModuleScopedCache<Box>(onEvict: _ => throw new InvalidOperationException("boom"));

        cache.GetOrBuild(holder.Handle, _ => new Box(1));
        // Must not throw and must clear the slot even though the disposer blew up.
        cache.Invalidate(holder.Handle.Mvid);
        cache.HasEntry(holder.Handle.Mvid).Should().BeFalse();
    }

    [Fact]
    public void OnEvict_disposes_the_orphan_when_a_concurrent_publisher_wins_the_slot()
    {
        // Simulate the publish race by having the build delegate insert a competing fresh
        // entry into the slot before returning. The losing call must (a) not corrupt the
        // winner's entry, (b) evict the orphan it just built, (c) on next iteration serve
        // the winner's value.
        using var holder = ModuleHandleHolder.Open(SampleLibPath);
        var evicted = new List<int>();
        var cache = new ModuleScopedCache<Box>(onEvict: box => evicted.Add(box.Value));

        // Pre-stage a "winner" entry by reflection so the CAS sees a concurrent publisher.
        cache.GetOrBuild(holder.Handle, _ => new Box(99));

        // Now drop just the in-memory key but stage a different entry via a racy GetOrBuild
        // whose build delegate replaces the slot mid-flight.
        cache.Invalidate(holder.Handle.Mvid);
        evicted.Clear();

        var result = cache.GetOrBuild(holder.Handle, _ =>
        {
            // Another thread "wins" the slot while we're building.
            cache.GetOrBuild(holder.Handle, _ => new Box(7));
            return new Box(42); // orphan: never published
        });

        result.Value.Should().Be(7, "the call must observe the winner on its retry iteration");
        evicted.Should().Contain(42, "the orphan built by the losing call must be evicted exactly once");
    }

    [Fact]
    public void Clear_makes_subsequent_GetOrBuild_throw_ObjectDisposedException()
    {
        // Defense-in-depth contract (#128): once the owner has Cleared, stray async
        // continuations that survive past Dispose must fail-fast instead of silently
        // repopulating a cache whose onEvict / graveyard wiring is gone.
        using var holder = ModuleHandleHolder.Open(SampleLibPath);
        var cache = new ModuleScopedCache<Box>();
        cache.GetOrBuild(holder.Handle, _ => new Box(1));

        cache.Clear();

        Action useAfterDispose = () => cache.GetOrBuild(holder.Handle, _ => new Box(2));
        useAfterDispose.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Invalidate_is_no_op_after_Clear()
    {
        using var holder = ModuleHandleHolder.Open(SampleLibPath);
        var evicted = new List<int>();
        var cache = new ModuleScopedCache<Box>(onEvict: box => evicted.Add(box.Value));
        cache.GetOrBuild(holder.Handle, _ => new Box(7));

        cache.Clear();
        evicted.Should().Contain(7, "Clear evicts every entry exactly once");
        evicted.Clear();

        cache.Invalidate(holder.Handle.Mvid);
        evicted.Should().BeEmpty("Invalidate must be a no-op once the cache has been disposed");

        Action clearAgain = () => cache.Clear();
        clearAgain.Should().NotThrow("Clear is idempotent");
    }

    [Fact]
    public void Clear_during_GetOrBuild_orphans_the_in_flight_build_and_throws()
    {
        // Race contract: a GetOrBuild whose build() is still running when Clear() flips
        // the disposed sentinel must NOT publish its orphan into the now-disposed cache.
        // The orphan is routed through onOrphan so any pinned resources release.
        using var holder = ModuleHandleHolder.Open(SampleLibPath);
        var orphaned = new List<int>();
        var cache = new ModuleScopedCache<Box>(
            onEvict: _ => { },
            onOrphan: box => orphaned.Add(box.Value));

        Action raceCall = () => cache.GetOrBuild(holder.Handle, _ =>
        {
            // Simulate Clear() landing mid-build (e.g. another thread / stray continuation).
            cache.Clear();
            return new Box(42);
        });

        raceCall.Should().Throw<ObjectDisposedException>();
        orphaned.Should().Contain(42, "the orphan must be released through onOrphan even when racing Clear");
        cache.HasEntry(holder.Handle.Mvid).Should().BeFalse("nothing must be published into the disposed cache");
    }

    [Fact]
    public void GetOrBuild_cached_hit_throws_when_disposed_races_in()
    {
        // Pins the gpt-5.5 #128 v2 secondary finding: the cached-hit path must re-read
        // _disposed before handing the caller a borrowed reference to data that Clear
        // is about to (or has just) evicted.
        using var holder = ModuleHandleHolder.Open(SampleLibPath);
        var cache = new ModuleScopedCache<Box>();
        cache.GetOrBuild(holder.Handle, _ => new Box(7));

        cache.Clear();

        Action cachedHit = () => cache.GetOrBuild(holder.Handle, _ => new Box(99));
        cachedHit.Should().Throw<ObjectDisposedException>(
            "disposed must be checked at the cached-hit return path too, not just on entry");
    }

    private sealed record Box(int Value);

    /// <summary>Opens a managed PE and exposes a <see cref="ModuleHandle"/> record; disposes the
    /// underlying <see cref="PEReader"/> when the test ends.</summary>
    private sealed class ModuleHandleHolder : IDisposable
    {
        public ModuleHandle Handle { get; }
        private readonly PEReader _pe;
        private readonly FileStream _stream;

        private ModuleHandleHolder(ModuleHandle handle, PEReader pe, FileStream stream)
        { Handle = handle; _pe = pe; _stream = stream; }

        public static ModuleHandleHolder Open(string path)
        {
            var fs = File.OpenRead(path);
            var pe = new PEReader(fs);
            var md = pe.GetMetadataReader();
            var mvid = md.GetGuid(md.GetModuleDefinition().Mvid);
            return new ModuleHandleHolder(new ModuleHandle(mvid, path, pe, md), pe, fs);
        }

        public void Dispose() { _pe.Dispose(); _stream.Dispose(); }
    }
}
