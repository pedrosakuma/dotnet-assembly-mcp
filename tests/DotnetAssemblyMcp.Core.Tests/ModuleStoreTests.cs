using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Metadata;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Direct unit tests for <see cref="ModuleStore"/>, the lifecycle owner extracted from
/// <see cref="MetadataIndex"/> as part of #79. <see cref="MetadataIndexWatcherTests"/>
/// continues to cover the public watcher behaviour end-to-end; the tests here exercise
/// the store's own surface so a regression localises immediately.
/// </summary>
public sealed class ModuleStoreTests : IDisposable
{
    private readonly string _tempDir;

    public ModuleStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "asm-mcp-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Load_returns_invalid_argument_for_empty_path()
    {
        using var store = new ModuleStore(watchForChanges: false);
        var result = store.Load("   ");
        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.InvalidArgument);
    }

    [Fact]
    public void Load_returns_module_load_failed_for_missing_file()
    {
        using var store = new ModuleStore(watchForChanges: false);
        var result = store.Load(Path.Combine(_tempDir, "does-not-exist.dll"));
        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.ModuleLoadFailed);
    }

    [Fact]
    public void Load_and_List_expose_loaded_module()
    {
        using var store = new ModuleStore(watchForChanges: false);
        var loaded = store.Load(typeof(SampleLib.OrderService).Assembly.Location);
        loaded.IsSuccess.Should().BeTrue();

        store.Count.Should().Be(1);
        store.List().Should().ContainSingle()
            .Which.ModuleVersionId.Should().Be(loaded.Module!.ModuleVersionId);
    }

    [Fact]
    public void Load_is_idempotent_for_same_path_when_mvid_matches()
    {
        using var store = new ModuleStore(watchForChanges: false);
        var path = typeof(SampleLib.OrderService).Assembly.Location;
        var a = store.Load(path);
        var b = store.Load(path);
        a.IsSuccess.Should().BeTrue();
        b.IsSuccess.Should().BeTrue();
        b.Module!.ModuleVersionId.Should().Be(a.Module!.ModuleVersionId);
        store.Count.Should().Be(1);
    }

    [Fact]
    public void TryGet_returns_handle_for_loaded_mvid()
    {
        using var store = new ModuleStore(watchForChanges: false);
        var loaded = store.Load(typeof(SampleLib.OrderService).Assembly.Location);
        store.TryGet(loaded.Module!.ModuleVersionId, out var handle).Should().BeTrue();
        handle.Should().NotBeNull();
        handle!.MD.Should().NotBeNull();
    }

    [Fact]
    public void Probe_returns_mvid_without_registering_module()
    {
        using var store = new ModuleStore(watchForChanges: false);
        var result = store.Probe(typeof(SampleLib.OrderService).Assembly.Location);
        result.IsSuccess.Should().BeTrue();
        result.Mvid.Should().NotBe(Guid.Empty);
        store.Count.Should().Be(0);
    }

    [Fact]
    public void RegisterPathHint_round_trips_through_TryGetPathHint()
    {
        using var store = new ModuleStore(watchForChanges: false);
        var mvid = Guid.NewGuid();
        var path = typeof(SampleLib.OrderService).Assembly.Location;
        store.RegisterPathHint(mvid, path);
        store.TryGetPathHint(mvid, out var got).Should().BeTrue();
        got.Should().Be(Path.GetFullPath(path));
    }

    [Fact]
    public void Same_mvid_reload_raises_event_and_swaps_pe()
    {
        var target = Path.Combine(_tempDir, "SameMvid.dll");
        File.Copy(typeof(SampleLib.OrderService).Assembly.Location, target);

        using var store = new ModuleStore(watchForChanges: false);
        var first = store.Load(target);
        first.IsSuccess.Should().BeTrue();
        var mvid = first.Module!.ModuleVersionId;

        var events = new List<ModuleReloadedEventArgs>();
        store.ModuleReloaded += (_, args) => events.Add(args);

        // Same path, same bytes — second Load triggers the same-MVID swap path.
        var second = store.Load(target);
        second.IsSuccess.Should().BeTrue();
        second.Module!.ModuleVersionId.Should().Be(mvid);

        events.Should().ContainSingle(e => e.OldMvid == mvid && e.NewMvid == mvid && e.Error == null);
        store.Count.Should().Be(1);
    }
}
