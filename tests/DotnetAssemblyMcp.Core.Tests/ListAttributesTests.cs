using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Metadata;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Behavioural tests for <see cref="IMetadataIndex.ListAttributes"/> against the SampleLib
/// fixture (issue #38). Covers each target kind plus the substring filter and cursor paging.
/// </summary>
public sealed class ListAttributesTests
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

    private static int FindMethodToken(MetadataIndex index, Guid mvid, int typeToken, string methodName)
    {
        var page = index.ListMethods(mvid, typeToken, new ListMethodsQuery(PageSize: 500)).Page!;
        return page.Methods.Single(m => m.MethodName == methodName).MetadataToken;
    }

    [Fact]
    public void Lists_assembly_level_attributes()
    {
        using var index = new MetadataIndex(); var mvid = LoadSampleLib(index);

        var result = index.ListAttributes(AttributeTarget.Assembly(mvid), new ListAttributesQuery(PageSize: 100));

        result.IsSuccess.Should().BeTrue();
        var page = result.Page!;
        page.TargetKind.Should().Be(AttributeTargetKind.Assembly);
        page.Attributes.Should().Contain(a => a.AttributeTypeFullName == "SampleLib.FixtureMarkerAttribute");
        var marker = page.Attributes.Single(a => a.AttributeTypeFullName == "SampleLib.FixtureMarkerAttribute");
        marker.FixedArguments.Should().ContainSingle()
            .Which.Value.Should().Be("sample-assembly");
        marker.NamedArguments.Should().Contain(na => na.Name == "Category" && (string)na.Value! == "fixture");
    }

    [Fact]
    public void Lists_type_attributes_with_multiple_instances()
    {
        using var index = new MetadataIndex(); var mvid = LoadSampleLib(index);
        var typeToken = FindTypeToken(index, mvid, "SampleLib.AnnotatedService");

        var result = index.ListAttributes(AttributeTarget.Type(mvid, typeToken), new ListAttributesQuery(PageSize: 100));

        result.IsSuccess.Should().BeTrue();
        var attrs = result.Page!.Attributes.Where(a => a.AttributeTypeFullName == "SampleLib.FixtureMarkerAttribute").ToList();
        attrs.Should().HaveCount(2);
        attrs.Should().Contain(a => a.FixedArguments.Count == 2
            && (string)a.FixedArguments[0].Value! == "hello"
            && (int)a.FixedArguments[1].Value! == 1);
        attrs.Should().Contain(a => a.FixedArguments.Count == 1
            && (string)a.FixedArguments[0].Value! == "world"
            && a.NamedArguments.Any(n => n.Name == "Category" && (string)n.Value! == "greeting"));
    }

    [Fact]
    public void Lists_method_attributes_including_array_argument()
    {
        using var index = new MetadataIndex(); var mvid = LoadSampleLib(index);
        var typeToken = FindTypeToken(index, mvid, "SampleLib.AnnotatedService");
        var methodToken = FindMethodToken(index, mvid, typeToken, "Annotated");

        var result = index.ListAttributes(AttributeTarget.Method(mvid, methodToken), new ListAttributesQuery(PageSize: 100));

        result.IsSuccess.Should().BeTrue();
        var attrs = result.Page!.Attributes;

        var marker = attrs.Single(a => a.AttributeTypeFullName == "SampleLib.FixtureMarkerAttribute");
        // The string[] constructor argument should round-trip as an IReadOnlyList<object?>.
        marker.FixedArguments.Should().ContainSingle();
        var arrayArg = marker.FixedArguments[0].Value;
        arrayArg.Should().BeAssignableTo<System.Collections.Generic.IReadOnlyList<object?>>();
        ((System.Collections.Generic.IReadOnlyList<object?>)arrayArg!).Should().Equal("tagA", "tagB");

        attrs.Should().Contain(a => a.AttributeTypeFullName == "System.ComponentModel.DescriptionAttribute");
    }

    [Fact]
    public void Lists_parameter_attributes_by_sequence()
    {
        using var index = new MetadataIndex(); var mvid = LoadSampleLib(index);
        var typeToken = FindTypeToken(index, mvid, "SampleLib.AnnotatedService");
        var methodToken = FindMethodToken(index, mvid, typeToken, "Annotated");

        var result = index.ListAttributes(AttributeTarget.Parameter(mvid, methodToken, parameterSequence: 1),
            new ListAttributesQuery(PageSize: 100));

        result.IsSuccess.Should().BeTrue();
        result.Page!.Attributes.Should().ContainSingle()
            .Which.FixedArguments[0].Value.Should().Be("p0");
    }

    [Fact]
    public void Name_filter_narrows_results()
    {
        using var index = new MetadataIndex(); var mvid = LoadSampleLib(index);
        var typeToken = FindTypeToken(index, mvid, "SampleLib.AnnotatedService");
        var methodToken = FindMethodToken(index, mvid, typeToken, "Annotated");

        var result = index.ListAttributes(AttributeTarget.Method(mvid, methodToken),
            new ListAttributesQuery(NameContains: "Description"));

        result.Page!.Attributes.Should().OnlyContain(a =>
            a.AttributeTypeFullName.Contains("Description", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Cursor_paging_walks_results_without_duplicates()
    {
        using var index = new MetadataIndex(); var mvid = LoadSampleLib(index);
        var typeToken = FindTypeToken(index, mvid, "SampleLib.AnnotatedService");

        // Page size 1 forces multiple iterations.
        var seen = new List<int>();
        int? cursor = null;
        for (int i = 0; i < 10; i++)
        {
            var page = index.ListAttributes(AttributeTarget.Type(mvid, typeToken),
                new ListAttributesQuery(Cursor: cursor, PageSize: 1)).Page!;
            seen.AddRange(page.Attributes.Select(a => a.MetadataToken));
            if (!page.Truncated) break;
            cursor = page.NextCursor;
            cursor.Should().NotBeNull();
        }

        seen.Should().BeInAscendingOrder();
        seen.Should().OnlyHaveUniqueItems();
        seen.Count.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void Unknown_mvid_returns_module_not_found()
    {
        using var index = new MetadataIndex();

        var result = index.ListAttributes(AttributeTarget.Assembly(Guid.NewGuid()), new ListAttributesQuery());

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.ModuleNotFound);
    }

    [Fact]
    public void Wrong_table_token_returns_token_wrong_table()
    {
        using var index = new MetadataIndex(); var mvid = LoadSampleLib(index);

        // Pass a TypeDef token where a MethodDef is expected.
        var typeToken = FindTypeToken(index, mvid, "SampleLib.AnnotatedService");

        var result = index.ListAttributes(AttributeTarget.Method(mvid, typeToken), new ListAttributesQuery());

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.TokenWrongTable);
    }
}
