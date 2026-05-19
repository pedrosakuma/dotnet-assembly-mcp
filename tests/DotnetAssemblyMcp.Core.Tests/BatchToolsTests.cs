using DotnetAssemblyMcp.Core;
using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Metadata;
using DotnetAssemblyMcp.Server.Tools;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Tests for the batch tools (issue #5 / Phase Z(c)): get_methods, scan_methods_il and
/// find_callers_batch. Covers per-item ok/error semantics, the BatchTooLarge cap, and the
/// composition with assemblyPathHint (Z(a)) and the lazy hint map (Z(b)).
/// </summary>
public sealed class BatchToolsTests
{
    private static string SampleLibPath => typeof(SampleLib.OrderService).Assembly.Location;

    private static (MetadataIndex Index, Guid Mvid, IReadOnlyList<int> Tokens) LoadAndPickFiveMethods()
    {
        var index = new MetadataIndex();
        var load = index.Load(SampleLibPath);
        load.IsSuccess.Should().BeTrue();
        var mvid = load.Module!.ModuleVersionId;
        var find = index.FindMethod(mvid, new FindMethodQuery(".", PageSize: 5));
        find.IsSuccess.Should().BeTrue();
        var tokens = find.Page!.Matches.Take(5).Select(m => m.MetadataToken).ToList();
        tokens.Should().HaveCountGreaterThan(0);
        return (index, mvid, tokens);
    }

    [Fact]
    public void Get_methods_resolves_every_item_per_position()
    {
        var (index, mvid, tokens) = LoadAndPickFiveMethods();
        using (index)
        {
            var items = tokens
                .Select(t => new MethodBatchItem(mvid.ToString("D"), $"0x{t:X8}"))
                .ToList();

            var result = AssemblyTools.GetMethods(index, items);

            result.IsError.Should().BeFalse(result.Summary);
            result.Data!.Results.Should().HaveCount(items.Count);
            result.Data.OkCount.Should().Be(items.Count);
            result.Data.ErrorCount.Should().Be(0);
            for (int i = 0; i < items.Count; i++)
            {
                result.Data.Results[i].Index.Should().Be(i);
                result.Data.Results[i].Ok.Should().BeTrue();
                result.Data.Results[i].Data!.MetadataToken.Should().Be(tokens[i]);
            }
        }
    }

    [Fact]
    public void Get_methods_with_one_bad_token_returns_per_item_error_and_keeps_the_others()
    {
        var (index, mvid, tokens) = LoadAndPickFiveMethods();
        using (index)
        {
            var items = new List<MethodBatchItem>
            {
                new(mvid.ToString("D"), $"0x{tokens[0]:X8}"),
                new(mvid.ToString("D"), "0x06FFFFFF"),                 // out of range
                new(mvid.ToString("D"), "not-a-number"),               // invalid_argument
                new(mvid.ToString("D"), $"0x{tokens[1]:X8}"),
            };

            var result = AssemblyTools.GetMethods(index, items);

            result.IsError.Should().BeFalse(result.Summary);
            result.Data!.OkCount.Should().Be(2);
            result.Data.ErrorCount.Should().Be(2);
            result.Data.Results[0].Ok.Should().BeTrue();
            result.Data.Results[1].Ok.Should().BeFalse();
            result.Data.Results[1].Error!.Kind.Should().Be(ErrorKinds.TokenOutOfRange);
            result.Data.Results[2].Ok.Should().BeFalse();
            result.Data.Results[2].Error!.Kind.Should().Be(ErrorKinds.InvalidArgument);
            result.Data.Results[3].Ok.Should().BeTrue();
        }
    }

    [Fact]
    public void Get_methods_honors_per_item_assembly_path_hint()
    {
        // Discover identity from a throwaway index, then issue the batch against a fresh
        // empty index — the hint must transparently load the module.
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
        var result = AssemblyTools.GetMethods(index, new[]
        {
            new MethodBatchItem(mvid.ToString("D"), $"0x{token:X8}", SampleLibPath),
        });

        result.IsError.Should().BeFalse(result.Summary);
        result.Data!.OkCount.Should().Be(1);
        index.List().Should().ContainSingle(m => m.ModuleVersionId == mvid);
    }

    [Fact]
    public void Get_methods_over_cap_returns_batch_too_large()
    {
        using var index = new MetadataIndex();
        var items = Enumerable.Range(0, AssemblyTools.BatchCap + 1)
            .Select(_ => new MethodBatchItem(Guid.NewGuid().ToString("D"), "0x06000001"))
            .ToList();

        var result = AssemblyTools.GetMethods(index, items);

        result.IsError.Should().BeTrue();
        result.Error!.Kind.Should().Be(ErrorKinds.BatchTooLarge);
        result.Data.Should().BeNull();
    }

    [Fact]
    public void Get_methods_at_cap_runs_normally()
    {
        var (index, mvid, tokens) = LoadAndPickFiveMethods();
        using (index)
        {
            // Repeat the first valid token to fill exactly to the cap.
            var items = Enumerable.Range(0, AssemblyTools.BatchCap)
                .Select(_ => new MethodBatchItem(mvid.ToString("D"), $"0x{tokens[0]:X8}"))
                .ToList();

            var result = AssemblyTools.GetMethods(index, items);

            result.IsError.Should().BeFalse(result.Summary);
            result.Data!.OkCount.Should().Be(AssemblyTools.BatchCap);
        }
    }

    [Fact]
    public void Get_methods_empty_returns_empty_batch_without_error()
    {
        using var index = new MetadataIndex();
        var result = AssemblyTools.GetMethods(index, Array.Empty<MethodBatchItem>());
        result.IsError.Should().BeFalse();
        result.Data!.Results.Should().BeEmpty();
    }

    [Fact]
    public void Scan_methods_il_returns_one_entry_per_input()
    {
        var (index, mvid, tokens) = LoadAndPickFiveMethods();
        using (index)
        {
            var items = tokens.Take(3)
                .Select(t => new MethodBatchItem(mvid.ToString("D"), $"0x{t:X8}"))
                .ToList();

            var result = AssemblyTools.ScanMethodsIl(index, items);

            result.IsError.Should().BeFalse(result.Summary);
            result.Data!.Results.Should().HaveCount(3);
            result.Data.OkCount.Should().Be(3);
        }
    }

    [Fact]
    public void Find_callers_batch_resolves_every_item()
    {
        var (index, mvid, tokens) = LoadAndPickFiveMethods();
        using (index)
        {
            var items = tokens.Take(2)
                .Select(t => new MethodBatchItem(mvid.ToString("D"), $"0x{t:X8}"))
                .ToList();

            var result = AssemblyTools.FindCallersBatch(index, items);

            result.IsError.Should().BeFalse(result.Summary);
            result.Data!.Results.Should().HaveCount(2);
            result.Data.OkCount.Should().Be(2);
            result.Data.Results.Should().OnlyContain(r => r.Data!.CalleeModuleVersionId == mvid);
        }
    }

    [Fact]
    public void Scan_methods_il_rejects_generic_args_with_invalid_argument()
    {
        var (index, mvid, tokens) = LoadAndPickFiveMethods();
        using (index)
        {
            IReadOnlyList<string> typeArgs = new[] { "System.Int32" };
            var items = new[]
            {
                new MethodBatchItem(
                    mvid.ToString("D"),
                    $"0x{tokens[0]:X8}",
                    GenericTypeArguments: typeArgs),
            };

            var result = AssemblyTools.ScanMethodsIl(index, items);

            result.IsError.Should().BeFalse(result.Summary);
            result.Data!.ErrorCount.Should().Be(1);
            result.Data.Results[0].Ok.Should().BeFalse();
            result.Data.Results[0].Error!.Kind.Should().Be(ErrorKinds.InvalidArgument);
        }
    }

    [Fact]
    public void Get_methods_source_resolves_every_item_per_position()
    {
        var (index, mvid, tokens) = LoadAndPickFiveMethods();
        using (index)
        {
            var items = tokens.Take(3)
                .Select(t => new MethodBatchItem(mvid.ToString("D"), $"0x{t:X8}"))
                .ToList();

            var result = AssemblyTools.GetMethodsSource(index, items);

            result.IsError.Should().BeFalse(result.Summary);
            result.Data!.Results.Should().HaveCount(3);
            // Each item is either a successful lookup or a documented found=false result;
            // a hard error must come through ErrorCount, not be silently swallowed.
            result.Data.ErrorCount.Should().Be(0);
        }
    }

    [Fact]
    public void Get_methods_source_rejects_generic_args_with_invalid_argument()
    {
        var (index, mvid, tokens) = LoadAndPickFiveMethods();
        using (index)
        {
            var items = new[]
            {
                new MethodBatchItem(
                    mvid.ToString("D"),
                    $"0x{tokens[0]:X8}",
                    MethodSpecModuleVersionId: mvid.ToString("D"),
                    MethodSpecMetadataToken: "0x2B000001"),
            };

            var result = AssemblyTools.GetMethodsSource(index, items);

            result.IsError.Should().BeFalse(result.Summary);
            result.Data!.ErrorCount.Should().Be(1);
            result.Data.Results[0].Error!.Kind.Should().Be(ErrorKinds.InvalidArgument);
        }
    }
}
