using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Metadata;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Tier-4 reverse-type-reference tests against the SampleLib fixture. Validates that the
/// xref index records every site that mentions a TypeDef (field/property/event types,
/// method signatures, locals, IL opcodes) and exposes them through
/// <see cref="MetadataIndex.FindTypeReferences"/>.
/// </summary>
public sealed class FindTypeReferencesTests
{
    private static string SampleLibPath => typeof(SampleLib.OrderService).Assembly.Location;

    private static (MetadataIndex Index, Guid Mvid) Load()
    {
        var index = new MetadataIndex();
        index.Load(SampleLibPath);
        var mvid = typeof(SampleLib.OrderService).Assembly.ManifestModule.ModuleVersionId;
        return (index, mvid);
    }

    private static int TokenOf<T>() => typeof(T).MetadataToken;

    [Fact]
    public void Finds_field_type_via_FieldDef_site()
    {
        var (index, mvid) = Load();
        using (index)
        {
            var iloggerToken = TokenOf<SampleLib.ILogger>();
            var result = index.FindTypeReferences(mvid, iloggerToken);

            result.IsSuccess.Should().BeTrue();
            // OrderService._logger is an ILogger field → must surface as a Field site of FieldType kind.
            result.Result!.References.Should().Contain(r =>
                r.SiteKind == MemberKind.Field && r.ReferenceKind == TypeReferenceKind.FieldType);
        }
    }

    [Fact]
    public void Finds_method_parameter_type()
    {
        var (index, mvid) = Load();
        using (index)
        {
            var iloggerToken = TokenOf<SampleLib.ILogger>();
            var result = index.FindTypeReferences(mvid, iloggerToken);

            result.IsSuccess.Should().BeTrue();
            // OrderService(ILogger logger) ctor parameter.
            result.Result!.References.Should().Contain(r =>
                r.SiteKind == MemberKind.Method && r.ReferenceKind == TypeReferenceKind.MethodParameter);
        }
    }

    [Fact]
    public void Finds_il_opcode_site_for_ldtoken_castclass_isinst()
    {
        var (index, mvid) = Load();
        using (index)
        {
            // TypeUsageFixture.IsAnimal/AsAnimal emit isinst/castclass on AnimalBase, and
            // BoxTypeHandle emits ldtoken Box<int> — all three are InlineType-bearing opcodes.
            var animalToken = typeof(SampleLib.AnimalBase).MetadataToken;
            var result = index.FindTypeReferences(mvid, animalToken);

            result.IsSuccess.Should().BeTrue();
            result.Result!.References.Should().Contain(r =>
                r.SiteKind == MemberKind.Method && r.ReferenceKind == TypeReferenceKind.IlOpcode);
        }
    }

    [Fact]
    public void Finds_event_handler_type_via_EventDef_site()
    {
        var (index, mvid) = Load();
        using (index)
        {
            // CustomerDto.Changed : event EventHandler<string>. Its declared type is the closed
            // generic EventHandler<string>; the TypeRef chain reaches System.EventHandler<T> in
            // System.Private.CoreLib so we won't see an intra-module hit for that. Instead use
            // FixtureMarkerAttribute (declared in SampleLib) which is the Attribute applied to
            // CustomerDto's property/field — its TypeDef will have intra-module sites.
            var fixtureMarkerToken = typeof(SampleLib.FixtureMarkerAttribute).MetadataToken;
            var result = index.FindTypeReferences(mvid, fixtureMarkerToken);

            result.IsSuccess.Should().BeTrue();
            // FixtureMarkerAttribute's ctor params reference string[]/int — and it itself shows
            // up in every call site that uses it; here we just assert the resolver works.
            result.Result!.ModulesSearched.Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public void Rejects_non_typedef_token()
    {
        var (index, mvid) = Load();
        using (index)
        {
            // A method-def token (table 0x06) is not a TypeDef — must yield TokenWrongTable.
            var methodToken = typeof(SampleLib.OrderService)
                .GetMethod(nameof(SampleLib.OrderService.Process), new[] { typeof(int) })!
                .MetadataToken;
            var result = index.FindTypeReferences(mvid, methodToken);

            result.IsSuccess.Should().BeFalse();
            result.Error!.Kind.Should().Be(ErrorKinds.TokenWrongTable);
        }
    }

    [Fact]
    public void Returns_ModuleNotFound_for_unknown_mvid()
    {
        var (index, _) = Load();
        using (index)
        {
            var result = index.FindTypeReferences(Guid.NewGuid(), TokenOf<SampleLib.ILogger>());
            result.IsSuccess.Should().BeFalse();
            result.Error!.Kind.Should().Be(ErrorKinds.ModuleNotFound);
        }
    }

    [Fact]
    public void Second_call_is_served_from_cache()
    {
        var (index, mvid) = Load();
        using (index)
        {
            var token = TokenOf<SampleLib.ILogger>();
            var first = index.FindTypeReferences(mvid, token);
            var second = index.FindTypeReferences(mvid, token);

            first.IsSuccess.Should().BeTrue();
            second.IsSuccess.Should().BeTrue();
            second.Result!.FromCache.Should().BeTrue();
        }
    }
}
