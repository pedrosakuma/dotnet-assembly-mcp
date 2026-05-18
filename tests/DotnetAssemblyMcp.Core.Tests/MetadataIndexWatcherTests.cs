using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using DotnetAssemblyMcp.Core.Metadata;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Tests for the Tier-1 <see cref="FileSystemWatcher"/> wiring on <see cref="MetadataIndex"/>.
/// Uses real on-disk files in a temp directory so the watcher fires actual OS events.
/// </summary>
public sealed class MetadataIndexWatcherTests : IDisposable
{
    private readonly string _tempDir;

    public MetadataIndexWatcherTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "asm-mcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task File_change_evicts_old_mvid_and_loads_new()
    {
        var samplePath = typeof(SampleLib.OrderService).Assembly.Location;
        var target = Path.Combine(_tempDir, "Watched.dll");
        File.Copy(samplePath, target);

        using var index = new MetadataIndex(watchForChanges: true);
        var first = index.Load(target);
        first.IsSuccess.Should().BeTrue();
        var oldMvid = first.Module!.ModuleVersionId;

        var reloadedTcs = new TaskCompletionSource<ModuleReloadedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        index.ModuleReloaded += (_, args) =>
        {
            if (args.Error is null && args.NewMvid != oldMvid)
                reloadedTcs.TrySetResult(args);
        };

        // Rewrite the file with a new MVID by patching the GUID heap entry.
        await Task.Delay(50);
        RewriteWithFreshMvid(target);

        var completed = await Task.WhenAny(reloadedTcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.Should().Be(reloadedTcs.Task, "the watcher should have fired a reload within 5s");

        var ev = await reloadedTcs.Task;
        ev.OldMvid.Should().Be(oldMvid);
        ev.NewMvid.Should().NotBe(oldMvid);

        var modules = index.List();
        modules.Should().ContainSingle();
        modules[0].ModuleVersionId.Should().Be(ev.NewMvid!.Value);
    }

    [Fact]
    public void Watcher_is_off_by_default()
    {
        var samplePath = typeof(SampleLib.OrderService).Assembly.Location;
        var target = Path.Combine(_tempDir, "NoWatch.dll");
        File.Copy(samplePath, target);

        using var index = new MetadataIndex();
        var fired = 0;
        index.ModuleReloaded += (_, _) => Interlocked.Increment(ref fired);

        index.Load(target).IsSuccess.Should().BeTrue();

        // Touch the file — the default index must not react.
        RewriteWithFreshMvid(target);
        Thread.Sleep(MetadataIndex.WatchDebounce + TimeSpan.FromMilliseconds(400));

        fired.Should().Be(0);
    }

    /// <summary>
    /// Rewrites the PE in place, mutating exactly one byte of the MVID GUID so the resulting
    /// module has a new MVID without touching anything else. The file is rewritten via
    /// File.Copy to a temp + Move so the watcher sees a single coalesced event.
    /// </summary>
    private static void RewriteWithFreshMvid(string path)
    {
        var bytes = File.ReadAllBytes(path);

        // Locate the MVID by re-reading the metadata. We mutate the bytes that back the
        // GUID heap entry pointed to by the Module table row 1.
        int mvidOffset;
        Guid originalMvid;
        using (var ms = new MemoryStream(bytes, writable: false))
        using (var pe = new PEReader(ms))
        {
            var md = pe.GetMetadataReader();
            originalMvid = md.GetGuid(md.GetModuleDefinition().Mvid);

            // The GUID heap is contiguous; find the first match.
            var span = bytes.AsSpan();
            mvidOffset = span.IndexOf(originalMvid.ToByteArray());
            if (mvidOffset < 0)
                throw new InvalidOperationException("could not locate MVID bytes in the PE image");
        }

        // Flip the last byte of the GUID to guarantee a different MVID.
        bytes[mvidOffset + 15] ^= 0xFF;

        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }
}
