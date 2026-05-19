using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Metadata;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Behavioural tests for <see cref="IMetadataIndex.ListMethods"/> against the SampleLib fixture.
/// Validates the Phase Y method-enumeration tool: per-type scoping, name filtering, paging and
/// the structured error path for tokens that don't belong to the TypeDef table.
/// </summary>
public sealed class ListMethodsTests
{
    private static string SampleLibPath => typeof(SampleLib.OrderService).Assembly.Location;

    private static (MetadataIndex Index, Guid Mvid, int OrderServiceToken) LoadOrderService()
    {
        var index = new MetadataIndex();
        var load = index.Load(SampleLibPath);
        load.IsSuccess.Should().BeTrue();
        var mvid = load.Module!.ModuleVersionId;
        var types = index.ListTypes(mvid, new ListTypesQuery(PageSize: 100));
        types.IsSuccess.Should().BeTrue();
        var orderService = types.Page!.Types.Single(t => t.FullName == "SampleLib.OrderService");
        return (index, mvid, orderService.MetadataToken);
    }

    [Fact]
    public void Returns_methods_of_the_type_with_handle_and_signature()
    {
        var (index, mvid, token) = LoadOrderService();
        using var _ = index;

        var result = index.ListMethods(mvid, token, new ListMethodsQuery(PageSize: 100));

        result.IsSuccess.Should().BeTrue();
        var page = result.Page!;
        page.TypeFullName.Should().Be("SampleLib.OrderService");
        page.Methods.Should().NotBeEmpty();
        page.Methods.Should().Contain(m => m.MethodName == ".ctor");
        page.Methods.Should().Contain(m => m.MethodName == "Process");
        page.Methods.Should().Contain(m => m.MethodName == "ProcessAsync");

        var process = page.Methods.First(m => m.MethodName == "Process");
        process.Handle.Should().StartWith($"m:{mvid:D}:0x06");
        process.Signature.Should().Contain("SampleLib.OrderService.Process");
    }

    private static readonly string[] ProcessMethodNames = ["Process", "ProcessAsync"];

    [Fact]
    public void Name_pattern_is_case_insensitive_substring()
    {
        var (index, mvid, token) = LoadOrderService();
        using var _ = index;

        var result = index.ListMethods(mvid, token, new ListMethodsQuery(NamePattern: "process"));

        result.IsSuccess.Should().BeTrue();
        result.Page!.Methods.Select(m => m.MethodName).Distinct()
            .Should().BeSubsetOf(ProcessMethodNames);
        result.Page.Methods.Should().Contain(m => m.MethodName == "Process");
        result.Page.Methods.Should().Contain(m => m.MethodName == "ProcessAsync");
    }

    [Fact]
    public void Paging_with_cursor_returns_disjoint_pages_in_metadata_order()
    {
        var (index, mvid, token) = LoadOrderService();
        using var _ = index;

        var first = index.ListMethods(mvid, token, new ListMethodsQuery(PageSize: 2));
        first.IsSuccess.Should().BeTrue();
        first.Page!.Methods.Should().HaveCount(2);
        first.Page.Truncated.Should().BeTrue();
        first.Page.NextCursor.Should().NotBeNull();

        var collected = new List<MethodSummary>(first.Page.Methods);
        int? cursor = first.Page.NextCursor;
        int guard = 20;
        while (cursor is not null && guard-- > 0)
        {
            var next = index.ListMethods(mvid, token, new ListMethodsQuery(PageSize: 2, Cursor: cursor));
            next.IsSuccess.Should().BeTrue();
            collected.AddRange(next.Page!.Methods);
            cursor = next.Page.NextCursor;
        }

        var all = index.ListMethods(mvid, token, new ListMethodsQuery(PageSize: 500));
        collected.Select(m => m.MetadataToken).Should().BeEquivalentTo(
            all.Page!.Methods.Select(m => m.MetadataToken),
            opts => opts.WithStrictOrdering(),
            "paged enumeration must preserve metadata-table order and not duplicate rows");
    }

    [Fact]
    public void Token_pointing_at_method_def_fails_with_wrong_table()
    {
        var (index, mvid, _) = LoadOrderService();
        using var _ = index;

        // 0x06000001 is the first MethodDef — pass it where a TypeDef (0x02……) is required.
        var result = index.ListMethods(mvid, 0x06000001, new ListMethodsQuery());

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.TokenWrongTable);
    }

    [Fact]
    public void Unknown_mvid_fails_with_module_not_found()
    {
        using var index = new MetadataIndex();

        var result = index.ListMethods(Guid.NewGuid(), 0x02000002, new ListMethodsQuery());

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.ModuleNotFound);
    }
}
