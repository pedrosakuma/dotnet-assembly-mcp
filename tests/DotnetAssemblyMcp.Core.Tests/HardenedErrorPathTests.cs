using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Identity;
using DotnetAssemblyMcp.Core.Metadata;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Audit #78 — hostile / malformed input must never escape the MCP envelope as a raw
/// exception. Every path covered here used to throw <see cref="System.ArgumentException"/>
/// or <see cref="System.BadImageFormatException"/> before the audit fix.
/// </summary>
public sealed class HardenedErrorPathTests
{
    private static string SampleLibPath => typeof(SampleLib.OrderService).Assembly.Location;
    private static System.Guid SampleLibMvid =>
        typeof(SampleLib.OrderService).Assembly.ManifestModule.ModuleVersionId;

    [Fact]
    public void Resolve_returns_identity_malformed_for_negative_token()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath).IsSuccess.Should().BeTrue();

        var result = index.Resolve(new MethodIdentity(SampleLibMvid, -1));

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.IdentityMalformed);
    }

    [Theory]
    [InlineData(unchecked((int)0xFFFFFFFFu))]
    [InlineData(unchecked((int)0xFEFEFEFEu))]
    public void Resolve_returns_clean_error_for_garbage_token(int token)
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath).IsSuccess.Should().BeTrue();

        // Must not throw — must surface as a structured error.
        var result = index.Resolve(new MethodIdentity(SampleLibMvid, token));

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().BeOneOf(
            ErrorKinds.IdentityMalformed,
            ErrorKinds.TokenWrongTable,
            ErrorKinds.TokenOutOfRange);
    }

    [Fact]
    public void GetNativeBodyRef_returns_clean_error_for_negative_token()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath).IsSuccess.Should().BeTrue();

        var result = index.GetNativeBodyRef(SampleLibMvid, -1);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.IdentityMalformed);
    }

    [Fact]
    public async Task Reload_under_concurrent_reads_does_not_throw_on_disposed_PE()
    {
        // Audit #78 finding #3: same-MVID reload used to Dispose() the old PEReader
        // synchronously while other threads could still be reading through it. With the
        // bug present, this test surfaces ObjectDisposedException / AVE intermittently
        // because Resolve() calls into module.MD via the old reader after Dispose ran.
        var tempDir = Path.Combine(Path.GetTempPath(), "asm-mcp-reload-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var copy = Path.Combine(tempDir, "SampleLib.dll");
            File.Copy(SampleLibPath, copy);

            using var index = new MetadataIndex();
            var loaded = index.Load(copy);
            loaded.IsSuccess.Should().BeTrue();
            var mvid = loaded.Module!.ModuleVersionId;
            var methodToken = typeof(SampleLib.OrderService).GetMethods()[0].MetadataToken;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

            // Reader thread: bash on the same method as fast as possible.
            var reader = Task.Run(() =>
            {
                int ok = 0;
                while (!cts.IsCancellationRequested)
                {
                    var r = index.Resolve(new MethodIdentity(mvid, methodToken));
                    if (r.IsSuccess) ok++;
                }
                return ok;
            }, cts.Token);

            // Writer thread: keep triggering same-MVID reloads.
            var writer = Task.Run(() =>
            {
                int reloads = 0;
                while (!cts.IsCancellationRequested)
                {
                    File.SetLastWriteTimeUtc(copy, System.DateTime.UtcNow);
                    var r = index.Load(copy);
                    if (r.IsSuccess) reloads++;
                }
                return reloads;
            }, cts.Token);

            // Either an uncaught exception escapes (failure) or both tasks complete cleanly.
            var resolves = await reader;
            var reloads = await writer;
            resolves.Should().BeGreaterThan(0);
            reloads.Should().BeGreaterThan(0);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }
}
