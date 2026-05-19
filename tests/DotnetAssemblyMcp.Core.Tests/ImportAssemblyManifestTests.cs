using DotnetAssemblyMcp.Core;
using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Metadata;
using DotnetAssemblyMcp.Server.Tools;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// End-to-end tests for the <c>import_assembly_manifest</c> tool (issue #6 / Phase Z(b)).
/// Exercises both modes (<c>lazy</c> default + <c>tier1</c>), MVID-mismatch rejection, the
/// idempotency contract on re-import, and the downstream behavior where lazy hints make
/// later <c>get_method</c> calls succeed without an explicit <c>assemblyPathHint</c>.
/// </summary>
public sealed class ImportAssemblyManifestTests
{
    private static string SampleLibPath => typeof(SampleLib.OrderService).Assembly.Location;
    private static string SampleConsumerPath => typeof(SampleConsumer.ConsumerService).Assembly.Location;

    private static Guid ProbeMvid(string path)
    {
        using var probe = new MetadataIndex();
        var result = probe.Probe(path);
        result.IsSuccess.Should().BeTrue();
        return result.Mvid;
    }

    [Fact]
    public void Tier1_mode_loads_every_entry_into_the_index()
    {
        var libMvid = ProbeMvid(SampleLibPath);
        var consumerMvid = ProbeMvid(SampleConsumerPath);

        using var index = new MetadataIndex();
        var entries = new[]
        {
            new ManifestEntry(libMvid, SampleLibPath, "SampleLib.dll"),
            new ManifestEntry(consumerMvid, SampleConsumerPath, "SampleConsumer.dll"),
        };

        var result = AssemblyTools.ImportAssemblyManifest(index, entries, ManifestImportMode.Tier1);

        result.IsError.Should().BeFalse(result.Summary);
        result.Data!.Loaded.Should().HaveCount(2);
        result.Data.Loaded.Should().OnlyContain(l => l.Status == "loaded" && l.MethodCount > 0);
        result.Data.Registered.Should().BeEmpty();
        result.Data.Skipped.Should().BeEmpty();
        index.List().Select(m => m.ModuleVersionId)
            .Should().Contain(new[] { libMvid, consumerMvid });
    }

    [Fact]
    public void Lazy_mode_registers_path_hints_without_opening_the_pe()
    {
        var libMvid = ProbeMvid(SampleLibPath);

        using var index = new MetadataIndex();
        var entries = new[] { new ManifestEntry(libMvid, SampleLibPath, "SampleLib.dll") };

        var result = AssemblyTools.ImportAssemblyManifest(index, entries, ManifestImportMode.Lazy);

        result.IsError.Should().BeFalse(result.Summary);
        result.Data!.Registered.Should().ContainSingle(r => r.ModuleVersionId == libMvid);
        result.Data.Loaded.Should().BeEmpty();
        result.Data.Skipped.Should().BeEmpty();
        index.List().Should().BeEmpty();
        index.TryGetPathHint(libMvid, out var path).Should().BeTrue();
        path.Should().Be(Path.GetFullPath(SampleLibPath));
    }

    [Fact]
    public void Lazy_default_mode_is_lazy()
    {
        var libMvid = ProbeMvid(SampleLibPath);
        using var index = new MetadataIndex();
        var result = AssemblyTools.ImportAssemblyManifest(
            index, new[] { new ManifestEntry(libMvid, SampleLibPath) });
        result.Data!.Mode.Should().Be(ManifestImportMode.Lazy);
        index.List().Should().BeEmpty();
        index.PathHints.Should().ContainKey(libMvid);
    }

    [Fact]
    public void Lazy_hint_lets_get_method_resolve_without_an_explicit_path_hint()
    {
        // 1) Discover a token from a throwaway index. 2) On a fresh index, import the
        // manifest in lazy mode. 3) Call GetMethod with only (mvid, token) — no path hint —
        // and verify it resolves by consulting the lazy mapping.
        Guid mvid;
        int token;
        using (var warmup = new MetadataIndex())
        {
            var load = warmup.Load(SampleLibPath);
            mvid = load.Module!.ModuleVersionId;
            var find = warmup.FindMethod(mvid, new FindMethodQuery("^Process$"));
            token = find.Page!.Matches[0].MetadataToken;
        }

        using var index = new MetadataIndex();
        AssemblyTools.ImportAssemblyManifest(
            index, new[] { new ManifestEntry(mvid, SampleLibPath) });

        var result = AssemblyTools.GetMethod(index, mvid.ToString("D"), $"0x{token:X8}");
        result.IsError.Should().BeFalse(result.Summary);
        result.Data!.ModuleVersionId.Should().Be(mvid);
        index.List().Should().ContainSingle(m => m.ModuleVersionId == mvid);
    }

    [Fact]
    public void Mvid_mismatch_with_path_is_rejected_and_does_not_touch_the_index()
    {
        var libMvid = ProbeMvid(SampleLibPath);
        // Wrong manifest: claims libMvid lives at the consumer file.
        using var index = new MetadataIndex();
        var result = AssemblyTools.ImportAssemblyManifest(
            index,
            new[] { new ManifestEntry(libMvid, SampleConsumerPath) },
            ManifestImportMode.Tier1);

        result.Data!.Skipped.Should().ContainSingle(s =>
            s.ModuleVersionId == libMvid
            && s.Reason == "mvid_mismatch_with_path");
        result.Data.Loaded.Should().BeEmpty();
        index.List().Should().BeEmpty();
        index.PathHints.Should().NotContainKey(libMvid);
    }

    [Fact]
    public void Missing_file_is_skipped_with_file_not_found()
    {
        using var index = new MetadataIndex();
        var bogus = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}.dll");
        var result = AssemblyTools.ImportAssemblyManifest(
            index,
            new[] { new ManifestEntry(Guid.NewGuid(), bogus) });

        result.Data!.Skipped.Should().ContainSingle(s => s.Reason == "file_not_found");
        result.Data.Loaded.Should().BeEmpty();
        result.Data.Registered.Should().BeEmpty();
    }

    [Fact]
    public void Reimport_of_loaded_mvid_reports_already_loaded()
    {
        var libMvid = ProbeMvid(SampleLibPath);
        using var index = new MetadataIndex();
        index.Load(SampleLibPath).IsSuccess.Should().BeTrue();

        var result = AssemblyTools.ImportAssemblyManifest(
            index,
            new[] { new ManifestEntry(libMvid, SampleLibPath) },
            ManifestImportMode.Tier1);

        result.Data!.Loaded.Should().ContainSingle(l => l.Status == "already_loaded");
        result.Data.Skipped.Should().BeEmpty();
        // Still only one module — idempotent.
        index.List().Should().ContainSingle(m => m.ModuleVersionId == libMvid);
    }

    [Fact]
    public void Empty_manifest_returns_empty_buckets()
    {
        using var index = new MetadataIndex();
        var result = AssemblyTools.ImportAssemblyManifest(index, Array.Empty<ManifestEntry>());
        result.IsError.Should().BeFalse();
        result.Data!.Loaded.Should().BeEmpty();
        result.Data.Registered.Should().BeEmpty();
        result.Data.Skipped.Should().BeEmpty();
    }

    [Fact]
    public void Invalid_entry_with_empty_mvid_is_skipped_with_invalid_argument()
    {
        using var index = new MetadataIndex();
        var result = AssemblyTools.ImportAssemblyManifest(
            index,
            new[] { new ManifestEntry(Guid.Empty, SampleLibPath) });
        result.Data!.Skipped.Should().ContainSingle(s => s.Reason == ErrorKinds.InvalidArgument);
    }
}
