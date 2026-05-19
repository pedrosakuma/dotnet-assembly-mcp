using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Metadata;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Behavioural tests for <see cref="IMetadataIndex.ListMembers"/> (issue #40) and the
/// Field / Property / Event targets added to <see cref="IMetadataIndex.ListAttributes"/>.
/// Fixture type: <c>SampleLib.CustomerDto</c>, which exercises every member kind.
/// </summary>
public sealed class ListMembersTests
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
    public void Lists_every_member_kind_for_a_poco()
    {
        using var index = new MetadataIndex(); var mvid = LoadSampleLib(index);
        var token = FindTypeToken(index, mvid, "SampleLib.CustomerDto");

        var page = index.ListMembers(mvid, token, new ListMembersQuery(PageSize: 100)).Page!;
        var byKind = page.Members.GroupBy(m => m.Kind).ToDictionary(g => g.Key, g => g.Select(m => m.Name).ToList());

        byKind[MemberKind.Field].Should().Contain("DefaultRegion")
            .And.Contain("Schema").And.Contain("_id").And.Contain("Age");
        byKind[MemberKind.Property].Should().Contain("Name")
            .And.Contain("Email").And.Contain("Id");
        byKind[MemberKind.Event].Should().Contain("Changed");
    }

    [Fact]
    public void Filters_to_a_single_kind()
    {
        using var index = new MetadataIndex(); var mvid = LoadSampleLib(index);
        var token = FindTypeToken(index, mvid, "SampleLib.CustomerDto");

        var page = index.ListMembers(mvid, token, new ListMembersQuery(Kind: MemberKind.Property, PageSize: 100)).Page!;
        page.Members.Should().OnlyContain(m => m.Kind == MemberKind.Property);
    }

    [Fact]
    public void Field_attributes_capture_const_and_readonly_modifiers()
    {
        using var index = new MetadataIndex(); var mvid = LoadSampleLib(index);
        var token = FindTypeToken(index, mvid, "SampleLib.CustomerDto");

        var fields = index.ListMembers(mvid, token,
            new ListMembersQuery(Kind: MemberKind.Field, PageSize: 100)).Page!.Members;

        fields.Single(m => m.Name == "DefaultRegion").Attributes
            .Should().Contain("const");
        fields.Single(m => m.Name == "Schema").Attributes
            .Should().Contain("static").And.Contain("readonly");
        fields.Single(m => m.Name == "_id").Attributes
            .Should().Contain("private").And.Contain("readonly");
    }

    [Fact]
    public void Property_signature_renders_accessors()
    {
        using var index = new MetadataIndex(); var mvid = LoadSampleLib(index);
        var token = FindTypeToken(index, mvid, "SampleLib.CustomerDto");

        var props = index.ListMembers(mvid, token,
            new ListMembersQuery(Kind: MemberKind.Property, PageSize: 100)).Page!.Members;

        props.Single(m => m.Name == "Name").Signature.Should().Contain("{ get; set; }");
        props.Single(m => m.Name == "Id").Signature.Should().Contain("{ get; }");
    }

    [Fact]
    public void Name_and_signature_filters_compose()
    {
        using var index = new MetadataIndex(); var mvid = LoadSampleLib(index);
        var token = FindTypeToken(index, mvid, "SampleLib.CustomerDto");

        var page = index.ListMembers(mvid, token,
            new ListMembersQuery(Kind: MemberKind.Property, NamePattern: "name", SignatureContains: "string", PageSize: 100)).Page!;

        page.Members.Should().ContainSingle().Which.Name.Should().Be("Name");
    }

    [Fact]
    public void Pagination_walks_members_without_duplicates()
    {
        using var index = new MetadataIndex(); var mvid = LoadSampleLib(index);
        var token = FindTypeToken(index, mvid, "SampleLib.CustomerDto");

        var seen = new List<string>();
        int? cursor = null;
        while (true)
        {
            var page = index.ListMembers(mvid, token,
                new ListMembersQuery(Cursor: cursor, PageSize: 2)).Page!;
            seen.AddRange(page.Members.Select(m => $"{m.Kind}:{m.Name}"));
            if (!page.Truncated) break;
            cursor = page.NextCursor;
        }

        seen.Should().OnlyHaveUniqueItems();
        seen.Should().Contain("Field:Age");
        seen.Should().Contain("Event:Changed");
    }

    [Fact]
    public void Returns_TokenWrongTable_for_method_token()
    {
        using var index = new MetadataIndex(); var mvid = LoadSampleLib(index);
        var result = index.ListMembers(mvid, unchecked((int)0x06000001), new ListMembersQuery());
        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.TokenWrongTable);
    }

    [Fact]
    public void List_attributes_accepts_field_property_event_handles()
    {
        using var index = new MetadataIndex(); var mvid = LoadSampleLib(index);
        var typeToken = FindTypeToken(index, mvid, "SampleLib.CustomerDto");
        var members = index.ListMembers(mvid, typeToken, new ListMembersQuery(PageSize: 100)).Page!.Members;

        // Event 'Changed' carries [FixtureMarker("on-changed")].
        var changed = members.Single(m => m.Kind == MemberKind.Event && m.Name == "Changed");
        var eventAttrs = index.ListAttributes(
            AttributeTarget.Event(mvid, changed.MetadataToken),
            new ListAttributesQuery(PageSize: 50)).Page!;
        eventAttrs.Attributes.Should().Contain(a => a.AttributeTypeFullName == "SampleLib.FixtureMarkerAttribute");

        // Field / property targets resolve without throwing even when empty.
        var ageField = members.Single(m => m.Kind == MemberKind.Field && m.Name == "Age");
        var fieldRes = index.ListAttributes(AttributeTarget.Field(mvid, ageField.MetadataToken), new ListAttributesQuery());
        fieldRes.IsSuccess.Should().BeTrue();

        var nameProp = members.Single(m => m.Kind == MemberKind.Property && m.Name == "Name");
        var propRes = index.ListAttributes(AttributeTarget.Property(mvid, nameProp.MetadataToken), new ListAttributesQuery());
        propRes.IsSuccess.Should().BeTrue();
    }
}
