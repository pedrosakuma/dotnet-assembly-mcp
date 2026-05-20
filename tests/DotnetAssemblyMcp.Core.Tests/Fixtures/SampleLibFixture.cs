using DotnetAssemblyMcp.Core.Metadata;

namespace DotnetAssemblyMcp.Core.Tests.Fixtures;

/// <summary>
/// xUnit <see cref="IClassFixture{TFixture}"/> that loads SampleLib once per test
/// class. Avoids the per-test cold-load that all 38 legacy suites pay individually.
///
/// Tests that mutate the index (e.g. watcher / reload tests) MUST keep using their
/// own <c>new MetadataIndex()</c> — this fixture exposes its index as a shared,
/// read-only navigation target.
/// </summary>
public sealed class SampleLibFixture : IDisposable
{
    public SampleLibFixture()
    {
        Index = new MetadataIndex();
        var loaded = Index.Load(SampleLibPath);
        if (!loaded.IsSuccess)
            throw new InvalidOperationException(
                $"SampleLibFixture failed to load '{SampleLibPath}': {loaded.Error?.Message}");

        Module = loaded.Module!;
        Mvid = Module.ModuleVersionId;
    }

    /// <summary>Shared metadata index. Do not call <see cref="MetadataIndex.Dispose"/>.</summary>
    public MetadataIndex Index { get; }

    /// <summary>The SampleLib module summary, fetched at fixture construction.</summary>
    public ModuleSummary Module { get; }

    /// <summary>Shorthand for <c>Module.ModuleVersionId</c>.</summary>
    public Guid Mvid { get; }

    /// <summary>Resolved absolute path of the SampleLib assembly on disk.</summary>
    public static string SampleLibPath => typeof(SampleLib.OrderService).Assembly.Location;

    public void Dispose() => Index.Dispose();
}
