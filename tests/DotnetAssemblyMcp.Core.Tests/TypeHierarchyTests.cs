using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Metadata;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Behavioural tests for the type-hierarchy surface (issue #39): <see cref="TypeSummary.BaseType"/>
/// and <see cref="TypeSummary.Interfaces"/> on every TypeSummary, plus the
/// <see cref="IMetadataIndex.GetTypeDefinition"/> and <see cref="IMetadataIndex.ListDerivedTypes"/>
/// drill-in tools.
/// </summary>
public sealed class TypeHierarchyTests
{
    private static string SampleLibPath => typeof(SampleLib.OrderService).Assembly.Location;

    private static Guid LoadSampleLib(MetadataIndex index)
    {
        var load = index.Load(SampleLibPath);
        load.IsSuccess.Should().BeTrue();
        return load.Module!.ModuleVersionId;
    }

    private static int FindTypeToken(MetadataIndex index, Guid mvid, string fullName)
    {
        var page = index.ListTypes(mvid, new ListTypesQuery(PageSize: 500)).Page!;
        return page.Types.Single(t => t.FullName == fullName).MetadataToken;
    }

    [Fact]
    public void TypeSummary_populates_base_and_interfaces_for_derived_class()
    {
        using var index = new MetadataIndex(); var mvid = LoadSampleLib(index);
        var token = FindTypeToken(index, mvid, "SampleLib.Dog");

        var result = index.GetTypeDefinition(mvid, token);
        result.IsSuccess.Should().BeTrue();
        var t = result.Type!;

        t.BaseType.Should().NotBeNull();
        t.BaseType!.FullName.Should().Be("SampleLib.AnimalBase");
        // Same-module base → no cross-module assembly hint.
        t.BaseType.AssemblyName.Should().BeNull();
        // Dog inherits IAnimal transitively but only direct interface impls are reported.
        t.Interfaces.Should().BeNullOrEmpty();
    }

    [Fact]
    public void TypeSummary_lists_direct_interface_implementations()
    {
        using var index = new MetadataIndex(); var mvid = LoadSampleLib(index);
        var token = FindTypeToken(index, mvid, "SampleLib.ConsoleLogger");

        var t = index.GetTypeDefinition(mvid, token).Type!;
        t.Interfaces.Should().NotBeNull();
        t.Interfaces!.Should().ContainSingle(i => i.FullName == "SampleLib.ILogger");
    }

    [Fact]
    public void TypeSummary_reports_cross_module_base_object()
    {
        using var index = new MetadataIndex(); var mvid = LoadSampleLib(index);
        var token = FindTypeToken(index, mvid, "SampleLib.AnnotatedService");

        var t = index.GetTypeDefinition(mvid, token).Type!;
        t.BaseType.Should().NotBeNull();
        t.BaseType!.FullName.Should().Be("System.Object");
        // System.Object lives in another assembly → must carry the assembly hint.
        t.BaseType.AssemblyName.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GetTypeDefinition_returns_TokenWrongTable_for_method_token()
    {
        using var index = new MetadataIndex(); var mvid = LoadSampleLib(index);
        // Pass a MethodDef token (table 0x06) where a TypeDef (table 0x02) is required.
        var methodToken = unchecked((int)0x06000001);

        var result = index.GetTypeDefinition(mvid, methodToken);
        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().BeOneOf(ErrorKinds.TokenWrongTable, ErrorKinds.TokenOutOfRange);
    }

    [Fact]
    public void GetTypeDefinition_returns_ModuleNotFound_for_unknown_mvid()
    {
        using var index = new MetadataIndex(); LoadSampleLib(index);

        var result = index.GetTypeDefinition(Guid.NewGuid(), unchecked((int)0x02000002));
        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.ModuleNotFound);
    }

    [Fact]
    public void ListDerivedTypes_direct_only_returns_immediate_children()
    {
        using var index = new MetadataIndex(); var mvid = LoadSampleLib(index);
        var dogToken = FindTypeToken(index, mvid, "SampleLib.Dog");

        var result = index.ListDerivedTypes(mvid, dogToken, new ListDerivedTypesQuery(DirectOnly: true, PageSize: 50));
        result.IsSuccess.Should().BeTrue();
        var names = result.Page!.Types.Select(t => t.FullName).ToList();
        names.Should().Contain("SampleLib.Puppy");
    }

    [Fact]
    public void ListDerivedTypes_transitive_returns_all_descendants()
    {
        using var index = new MetadataIndex(); var mvid = LoadSampleLib(index);
        var baseToken = FindTypeToken(index, mvid, "SampleLib.AnimalBase");

        var direct = index.ListDerivedTypes(mvid, baseToken, new ListDerivedTypesQuery(DirectOnly: true, PageSize: 50)).Page!;
        var trans = index.ListDerivedTypes(mvid, baseToken, new ListDerivedTypesQuery(DirectOnly: false, PageSize: 50)).Page!;

        var directNames = direct.Types.Select(t => t.FullName).ToHashSet();
        var transNames = trans.Types.Select(t => t.FullName).ToHashSet();

        directNames.Should().Contain("SampleLib.Dog").And.NotContain("SampleLib.Puppy");
        transNames.Should().Contain("SampleLib.Dog").And.Contain("SampleLib.Puppy");
    }

    [Fact]
    public void ListDerivedTypes_pagination_walks_results_without_duplicates()
    {
        using var index = new MetadataIndex(); var mvid = LoadSampleLib(index);
        var baseToken = FindTypeToken(index, mvid, "SampleLib.AnimalBase");

        // PageSize 1 forces multiple round-trips to enumerate the transitive set.
        var seen = new List<string>();
        int? cursor = null;
        while (true)
        {
            var page = index.ListDerivedTypes(mvid, baseToken,
                new ListDerivedTypesQuery(DirectOnly: false, Cursor: cursor, PageSize: 1)).Page!;
            foreach (var t in page.Types) seen.Add(t.FullName);
            if (!page.Truncated) break;
            cursor = page.NextCursor;
        }

        seen.Should().OnlyHaveUniqueItems();
        seen.Should().Contain("SampleLib.Dog");
        seen.Should().Contain("SampleLib.Puppy");
    }

    [Fact]
    public void ListDerivedTypes_returns_empty_for_leaf_type()
    {
        using var index = new MetadataIndex(); var mvid = LoadSampleLib(index);
        var leaf = FindTypeToken(index, mvid, "SampleLib.Puppy");

        var page = index.ListDerivedTypes(mvid, leaf, new ListDerivedTypesQuery(DirectOnly: false, PageSize: 50)).Page!;
        page.Types.Should().BeEmpty();
        page.Truncated.Should().BeFalse();
    }
}
