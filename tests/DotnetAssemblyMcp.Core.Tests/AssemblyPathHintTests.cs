using DotnetAssemblyMcp.Core;
using DotnetAssemblyMcp.Core.Decompilation;
using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Metadata;
using DotnetAssemblyMcp.Server.Tools;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// End-to-end tests for the assemblyPathHint parameter (issue #4 / Phase Z(a)) on the five
/// (mvid, token) tools. Verifies the three documented outcomes: already-loaded ignores the
/// hint, matching-MVID hint loads transparently, mismatched-MVID hint fails with
/// mvid_mismatch and does not silently load the wrong binary.
/// </summary>
public sealed class AssemblyPathHintTests
{
    private static string SampleLibPath => typeof(SampleLib.OrderService).Assembly.Location;
    private static string SampleConsumerPath => typeof(SampleConsumer.ConsumerService).Assembly.Location;

    private static (MetadataIndex Index, Guid Mvid, int Token) ResolveSampleLibCtor()
    {
        var index = new MetadataIndex();
        var load = index.Load(SampleLibPath);
        load.IsSuccess.Should().BeTrue();
        var mvid = load.Module!.ModuleVersionId;
        var find = index.FindMethod(mvid, new FindMethodQuery("^Process$"));
        find.IsSuccess.Should().BeTrue();
        find.Page!.Matches.Should().NotBeEmpty();
        var token = find.Page.Matches[0].MetadataToken;
        return (index, mvid, token);
    }

    [Fact]
    public void GetMethod_with_hint_loads_unloaded_module_transparently()
    {
        // Discover the token from a throwaway index, then call GetMethod with a fresh empty
        // index plus the hint — the single tool call must succeed by loading via the hint.
        var (warmup, mvid, token) = ResolveSampleLibCtor();
        warmup.Dispose();

        var index = new MetadataIndex();
        var result = AssemblyTools.GetMethod(
            index,
            mvid.ToString("D"),
            $"0x{token:X8}",
            assemblyPathHint: SampleLibPath);

        result.IsError.Should().BeFalse(result.Summary);
        result.Data!.ModuleVersionId.Should().Be(mvid);
        index.List().Should().ContainSingle(m => m.ModuleVersionId == mvid);
        index.Dispose();
    }

    [Fact]
    public void GetMethod_with_mismatched_hint_returns_mvid_mismatch_and_does_not_resolve()
    {
        // Discover the SampleLib token, then call GetMethod with a SampleConsumer path —
        // different MVID. Must fail cleanly without silently honoring the wrong binary.
        var (warmup, mvid, token) = ResolveSampleLibCtor();
        warmup.Dispose();

        var index = new MetadataIndex();
        var result = AssemblyTools.GetMethod(
            index,
            mvid.ToString("D"),
            $"0x{token:X8}",
            assemblyPathHint: SampleConsumerPath);

        result.IsError.Should().BeTrue();
        result.Error!.Kind.Should().Be(ErrorKinds.MvidMismatch);
        // Hint is probed before load; mismatch must not pollute the registry with the wrong binary.
        index.List().Should().BeEmpty();
        index.Dispose();
    }

    [Fact]
    public void GetMethod_without_hint_on_unloaded_mvid_still_returns_module_not_found()
    {
        var (warmup, mvid, token) = ResolveSampleLibCtor();
        warmup.Dispose();

        var index = new MetadataIndex();
        var result = AssemblyTools.GetMethod(index, mvid.ToString("D"), $"0x{token:X8}");

        result.IsError.Should().BeTrue();
        result.Error!.Kind.Should().Be(ErrorKinds.ModuleNotFound);
        index.Dispose();
    }

    [Fact]
    public void GetMethod_with_hint_is_idempotent_when_already_loaded()
    {
        var (index, mvid, token) = ResolveSampleLibCtor();
        using var _ = index;

        var before = index.List().Count;
        var result = AssemblyTools.GetMethod(
            index,
            mvid.ToString("D"),
            $"0x{token:X8}",
            assemblyPathHint: "/totally/bogus/path/that/does/not/exist.dll");

        // Hint is ignored because the MVID is already loaded — no error, no second load.
        result.IsError.Should().BeFalse(result.Summary);
        index.List().Should().HaveCount(before);
    }

    [Fact]
    public void DecompileMethod_honors_assemblyPathHint()
    {
        var (warmup, mvid, token) = ResolveSampleLibCtor();
        warmup.Dispose();

        var index = new MetadataIndex();
        var decompiler = new Decompiler(index);
        var result = AssemblyTools.DecompileMethod(
            decompiler,
            index,
            mvid.ToString("D"),
            $"0x{token:X8}",
            assemblyPathHint: SampleLibPath);

        result.IsError.Should().BeFalse(result.Summary);
        result.Data!.SourceLengthChars.Should().BeGreaterThan(0);
        index.Dispose();
    }

    [Fact]
    public void GetMethodIl_honors_assemblyPathHint()
    {
        var (warmup, mvid, token) = ResolveSampleLibCtor();
        warmup.Dispose();

        var index = new MetadataIndex();
        var disassembler = new IlDisassembler(index);
        var result = AssemblyTools.GetMethodIl(
            disassembler, index, mvid.ToString("D"), $"0x{token:X8}",
            format: "raw", assemblyPathHint: SampleLibPath);

        result.IsError.Should().BeFalse(result.Summary);
        result.Data!.Format.Should().Be(MethodIlFormat.Raw);
        result.Data.Raw.Should().NotBeNull();
        result.Data.Raw!.IlSize.Should().BeGreaterThan(0);
        index.Dispose();
    }

    [Fact]
    public void ScanMethodIl_honors_assemblyPathHint()
    {
        var (warmup, mvid, token) = ResolveSampleLibCtor();
        warmup.Dispose();

        var index = new MetadataIndex();
        var disassembler = new IlDisassembler(index);
        var result = AssemblyTools.GetMethodIl(
            disassembler, index, mvid.ToString("D"), $"0x{token:X8}",
            format: "scan", assemblyPathHint: SampleLibPath);

        result.IsError.Should().BeFalse(result.Summary);
        result.Data!.Format.Should().Be(MethodIlFormat.Scan);
        result.Data.Scan.Should().NotBeNull();
        result.Data.Scan!.InstructionCount.Should().BeGreaterThan(0);
        index.Dispose();
    }

    [Fact]
    public void FindCallers_honors_assemblyPathHint()
    {
        var (warmup, mvid, token) = ResolveSampleLibCtor();
        warmup.Dispose();

        var index = new MetadataIndex();
        var result = AssemblyTools.FindCallers(
            index, mvid.ToString("D"), $"0x{token:X8}",
            assemblyPathHint: SampleLibPath);

        result.IsError.Should().BeFalse(result.Summary);
        index.Dispose();
    }
}
