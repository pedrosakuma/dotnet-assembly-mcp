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
