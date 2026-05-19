using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Metadata;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Behavioural tests for <see cref="IMetadataIndex.ListTypes"/> against the SampleLib fixture.
/// Verifies the Phase Y type-enumeration tool: kind classification, filters and cursor paging.
/// </summary>
public sealed class ListTypesTests
{
    private static string SampleLibPath => typeof(SampleLib.OrderService).Assembly.Location;

    private static Guid LoadSampleLib(MetadataIndex index)
    {
        var load = index.Load(SampleLibPath);
        load.IsSuccess.Should().BeTrue();
        return load.Module!.ModuleVersionId;
    }

    [Fact]
    public void Returns_every_user_type_with_correct_kind_classification()
    {
        using var index = new MetadataIndex();
        var mvid = LoadSampleLib(index);

        var result = index.ListTypes(mvid, new ListTypesQuery(PageSize: 100));

        result.IsSuccess.Should().BeTrue();
        var page = result.Page!;
        page.Truncated.Should().BeFalse();
        page.NextCursor.Should().BeNull();

        var names = page.Types.Select(t => t.FullName).ToArray();
        names.Should().Contain("SampleLib.OrderService");
        names.Should().Contain("SampleLib.Box`1");
        names.Should().Contain("SampleLib.ILogger");
        names.Should().Contain("SampleLib.NestingHost");
        names.Should().Contain("SampleLib.NestingHost+Inner");
        names.Should().NotContain("<Module>");

        var ilogger = page.Types.Single(t => t.FullName == "SampleLib.ILogger");
        ilogger.Kind.Should().Be(TypeKind.Interface);
        ilogger.IsPublic.Should().BeTrue();
        ilogger.Handle.Should().StartWith($"t:{mvid:D}:0x02");

        var orderService = page.Types.Single(t => t.FullName == "SampleLib.OrderService");
        orderService.Kind.Should().Be(TypeKind.Class);
        orderService.MethodCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Filter_by_kind_interface_returns_only_interfaces()
    {
        using var index = new MetadataIndex();
        var mvid = LoadSampleLib(index);

        var result = index.ListTypes(mvid, new ListTypesQuery(Kind: TypeKind.Interface));

        result.IsSuccess.Should().BeTrue();
        result.Page!.Types.Should().OnlyContain(t => t.Kind == TypeKind.Interface);
        result.Page.Types.Select(t => t.FullName).Should().Contain("SampleLib.ILogger");
    }

    [Fact]
    public void Name_contains_filter_is_case_insensitive_and_matches_nested()
    {
        using var index = new MetadataIndex();
        var mvid = LoadSampleLib(index);

        var result = index.ListTypes(mvid, new ListTypesQuery(NameContains: "inner"));

        result.IsSuccess.Should().BeTrue();
        result.Page!.Types.Should().ContainSingle(t => t.FullName == "SampleLib.NestingHost+Inner");
    }

    [Fact]
    public void Namespace_prefix_matches_dot_segmented_boundary()
    {
        using var index = new MetadataIndex();
        var mvid = LoadSampleLib(index);

        var matching = index.ListTypes(mvid, new ListTypesQuery(NamespacePrefix: "SampleLib"));
        matching.IsSuccess.Should().BeTrue();
        matching.Page!.Types.Should().OnlyContain(t => t.FullName.StartsWith("SampleLib"));

        var nonMatching = index.ListTypes(mvid, new ListTypesQuery(NamespacePrefix: "SampleLi"));
        nonMatching.IsSuccess.Should().BeTrue();
        nonMatching.Page!.Types.Should().BeEmpty(
            "namespace prefix must respect dot boundaries — 'SampleLi' is not a namespace segment");
    }

    [Fact]
    public void Paging_with_cursor_returns_disjoint_pages_that_reassemble_full_result()
    {
        using var index = new MetadataIndex();
        var mvid = LoadSampleLib(index);

        var first = index.ListTypes(mvid, new ListTypesQuery(PageSize: 2));
        first.IsSuccess.Should().BeTrue();
        first.Page!.Types.Should().HaveCount(2);
        first.Page.Truncated.Should().BeTrue();
        first.Page.NextCursor.Should().NotBeNull();

        var collected = new List<TypeSummary>(first.Page.Types);
        int? cursor = first.Page.NextCursor;
        int guard = 10;
        while (cursor is not null && guard-- > 0)
        {
            var next = index.ListTypes(mvid, new ListTypesQuery(PageSize: 2, Cursor: cursor));
            next.IsSuccess.Should().BeTrue();
            collected.AddRange(next.Page!.Types);
            cursor = next.Page.NextCursor;
        }

        var all = index.ListTypes(mvid, new ListTypesQuery(PageSize: 500));
        collected.Select(t => t.MetadataToken).Should().BeEquivalentTo(
            all.Page!.Types.Select(t => t.MetadataToken),
            opts => opts.WithStrictOrdering(),
            "paged enumeration must preserve the metadata-table order and not duplicate rows");
    }

    [Fact]
    public void Unknown_mvid_fails_with_module_not_found()
    {
        using var index = new MetadataIndex();

        var result = index.ListTypes(Guid.NewGuid(), new ListTypesQuery());

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.ModuleNotFound);
    }
}
