using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Metadata;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Behavioural tests for <see cref="IMetadataIndex.FindMethod"/> against the SampleLib fixture.
/// Validates the Phase Y module-wide method search: regex name matching, signature substring
/// filter, cursor-based pagination, and the structured error path for invalid regex patterns.
/// </summary>
public sealed class FindMethodTests
{
    private static string SampleLibPath => typeof(SampleLib.OrderService).Assembly.Location;

    private static (MetadataIndex Index, Guid Mvid) LoadSampleLib()
    {
        var index = new MetadataIndex();
        var load = index.Load(SampleLibPath);
        load.IsSuccess.Should().BeTrue();
        return (index, load.Module!.ModuleVersionId);
    }

    [Fact]
    public void Regex_matches_method_names_across_types()
    {
        var (index, mvid) = LoadSampleLib();
        using var _ = index;

        var result = index.FindMethod(mvid, new FindMethodQuery("^Process"));

        result.IsSuccess.Should().BeTrue();
        var page = result.Page!;
        page.Matches.Should().NotBeEmpty();
        page.Matches.Should().OnlyContain(m => m.MethodName.StartsWith("Process", StringComparison.OrdinalIgnoreCase));
        page.Matches.Should().Contain(m => m.MethodName == "Process" && m.TypeFullName == "SampleLib.OrderService");
        page.Matches.First(m => m.MethodName == "Process").Handle
            .Should().StartWith($"m:{mvid:D}:0x06");
    }

    [Fact]
    public void Signature_filter_narrows_results()
    {
        var (index, mvid) = LoadSampleLib();
        using var _ = index;

        var all = index.FindMethod(mvid, new FindMethodQuery(".*", PageSize: 200));
        all.IsSuccess.Should().BeTrue();
        var anyAsync = all.Page!.Matches.Any(m => m.Signature.Contains("Task", StringComparison.Ordinal));
        anyAsync.Should().BeTrue("the fixture should expose at least one Task-returning method");

        var filtered = index.FindMethod(mvid, new FindMethodQuery(".*", SignatureContains: "Task", PageSize: 200));

        filtered.IsSuccess.Should().BeTrue();
        filtered.Page!.Matches.Should().NotBeEmpty();
        filtered.Page.Matches.Should().OnlyContain(m => m.Signature.Contains("Task", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Cursor_round_trip_yields_disjoint_pages_in_token_order()
    {
        var (index, mvid) = LoadSampleLib();
        using var _ = index;

        var first = index.FindMethod(mvid, new FindMethodQuery(".*", PageSize: 3));
        first.IsSuccess.Should().BeTrue();
        first.Page!.Matches.Should().HaveCount(3);
        first.Page.Truncated.Should().BeTrue();
        first.Page.NextCursor.Should().NotBeNull();

        var second = index.FindMethod(mvid, new FindMethodQuery(".*", Cursor: first.Page.NextCursor, PageSize: 3));
        second.IsSuccess.Should().BeTrue();
        second.Page!.Matches.Should().NotBeEmpty();
        second.Page.Matches.Select(m => m.MetadataToken)
            .Should().OnlyContain(t => t > first.Page.NextCursor!.Value);
        second.Page.Matches.Select(m => m.MetadataToken)
            .Should().NotIntersectWith(first.Page.Matches.Select(m => m.MetadataToken));
    }

    [Fact]
    public void Invalid_regex_returns_structured_error()
    {
        var (index, mvid) = LoadSampleLib();
        using var _ = index;

        var result = index.FindMethod(mvid, new FindMethodQuery("(unclosed"));

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.InvalidArgument);
    }

    [Fact]
    public void Unknown_mvid_returns_module_not_found()
    {
        var (index, _) = LoadSampleLib();
        using var _ = index;

        var result = index.FindMethod(Guid.NewGuid(), new FindMethodQuery(".*"));

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.ModuleNotFound);
    }
}
