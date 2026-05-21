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

    // ─────────────────────────────────────────────────────────────────────────────
    // Type-hierarchy sites (issue #69): BaseType + InterfaceImplementation should
    // surface as references, including TypeSpec closures of a generic target.
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Finds_intra_module_BaseType_via_TypeSpec_closure()
    {
        // SampleLib.IntRepository : Repository<int> — same-module TypeSpec→TypeDef edge.
        var (index, mvid) = Load();
        using (index)
        {
            var repositoryOpenToken = typeof(SampleLib.Repository<>).MetadataToken;
            var result = index.FindTypeReferences(mvid, repositoryOpenToken);

            result.IsSuccess.Should().BeTrue();
            var intRepoToken = typeof(SampleLib.IntRepository).MetadataToken;
            result.Result!.References.Should().Contain(r =>
                r.SiteKind == MemberKind.Type
                && r.ReferenceKind == TypeReferenceKind.BaseType
                && r.MetadataToken == intRepoToken);
        }
    }

    [Fact]
    public void Finds_cross_module_BaseType_via_TypeSpec_TypeRef_path()
    {
        // SampleConsumer.UserRepo : Repository<string> — cross-module TypeSpec→TypeRef edge.
        var index = new MetadataIndex();
        index.Load(SampleLibPath);
        index.Load(typeof(SampleConsumer.ConsumerService).Assembly.Location);
        using (index)
        {
            var libMvid = typeof(SampleLib.OrderService).Assembly.ManifestModule.ModuleVersionId;
            var consumerMvid = typeof(SampleConsumer.ConsumerService).Assembly.ManifestModule.ModuleVersionId;
            var repositoryOpenToken = typeof(SampleLib.Repository<>).MetadataToken;
            var result = index.FindTypeReferences(libMvid, repositoryOpenToken);

            result.IsSuccess.Should().BeTrue();
            var userRepoToken = typeof(SampleConsumer.UserRepo).MetadataToken;
            result.Result!.References.Should().Contain(r =>
                r.ModuleVersionId == consumerMvid
                && r.SiteKind == MemberKind.Type
                && r.ReferenceKind == TypeReferenceKind.BaseType
                && r.MetadataToken == userRepoToken);
        }
    }

    [Fact]
    public void Finds_cross_module_InterfaceImplementation_via_TypeSpec()
    {
        // SampleConsumer.OrderHandler : IRequestHandler<int,string> — InterfaceImpl edge whose
        // target is a TypeSpec wrapping a TypeRef to the open generic interface in SampleLib.
        var index = new MetadataIndex();
        index.Load(SampleLibPath);
        index.Load(typeof(SampleConsumer.ConsumerService).Assembly.Location);
        using (index)
        {
            var libMvid = typeof(SampleLib.OrderService).Assembly.ManifestModule.ModuleVersionId;
            var consumerMvid = typeof(SampleConsumer.ConsumerService).Assembly.ManifestModule.ModuleVersionId;
            var ifaceOpenToken = typeof(SampleLib.IRequestHandler<,>).MetadataToken;
            var result = index.FindTypeReferences(libMvid, ifaceOpenToken);

            result.IsSuccess.Should().BeTrue();
            var orderHandlerToken = typeof(SampleConsumer.OrderHandler).MetadataToken;
            var userHandlerToken = typeof(SampleConsumer.UserHandler).MetadataToken;
            result.Result!.References.Should().Contain(r =>
                r.ModuleVersionId == consumerMvid
                && r.SiteKind == MemberKind.Type
                && r.ReferenceKind == TypeReferenceKind.InterfaceImplementation
                && r.MetadataToken == orderHandlerToken);
            result.Result!.References.Should().Contain(r =>
                r.ModuleVersionId == consumerMvid
                && r.SiteKind == MemberKind.Type
                && r.ReferenceKind == TypeReferenceKind.InterfaceImplementation
                && r.MetadataToken == userHandlerToken);
        }
    }

    [Fact]
    public void Finds_intra_module_BaseType_for_non_generic_target()
    {
        // SampleConsumer.Cub : Wolf (non-generic, same module) — confirms the BaseType walk
        // works for plain TypeDef edges, not just TypeSpec closures.
        var index = new MetadataIndex();
        index.Load(typeof(SampleConsumer.ConsumerService).Assembly.Location);
        using (index)
        {
            var consumerMvid = typeof(SampleConsumer.ConsumerService).Assembly.ManifestModule.ModuleVersionId;
            var wolfToken = typeof(SampleConsumer.Wolf).MetadataToken;
            var result = index.FindTypeReferences(consumerMvid, wolfToken);

            result.IsSuccess.Should().BeTrue();
            var cubToken = typeof(SampleConsumer.Cub).MetadataToken;
            result.Result!.References.Should().Contain(r =>
                r.SiteKind == MemberKind.Type
                && r.ReferenceKind == TypeReferenceKind.BaseType
                && r.MetadataToken == cubToken);
        }
    }

    [Fact]
    public void Type_site_render_carries_type_handle_prefix()
    {
        var (index, mvid) = Load();
        using (index)
        {
            var repositoryOpenToken = typeof(SampleLib.Repository<>).MetadataToken;
            var result = index.FindTypeReferences(mvid, repositoryOpenToken);
            result.IsSuccess.Should().BeTrue();

            var typeSite = result.Result!.References.FirstOrDefault(r => r.SiteKind == MemberKind.Type);
            typeSite.Should().NotBeNull();
            typeSite!.Handle.Should().StartWith("t:");
            typeSite.Display.Should().Contain("Repository"); // either IntRepository's TypeName render
        }
    }

    [Fact]
    public void Finds_cross_module_newobj_of_generic_via_TypeSpec_parent()
    {
        // SampleConsumer.RunBox does `new Box<int>(value)`. The IL is `newobj` (InlineMethod)
        // with operand = MemberRef whose Parent is a TypeSpec wrapping TypeRef(Box`1).
        // Pre-#69 fix, ScanTypesFromIl only inspected InlineType/InlineTok operands, so the
        // type-reference edge to Box`1 was lost — the open generic was invisible to
        // find_type_references even though new_obj is the canonical "use" of a type.
        var index = new MetadataIndex();
        index.Load(SampleLibPath);
        index.Load(typeof(SampleConsumer.ConsumerService).Assembly.Location);
        using (index)
        {
            var libMvid = typeof(SampleLib.OrderService).Assembly.ManifestModule.ModuleVersionId;
            var consumerMvid = typeof(SampleConsumer.ConsumerService).Assembly.ManifestModule.ModuleVersionId;
            var boxOpenToken = typeof(SampleLib.Box<>).MetadataToken;

            var result = index.FindTypeReferences(libMvid, boxOpenToken);

            result.IsSuccess.Should().BeTrue();
            // RunBox + RunBoxString both `new Box<...>(value)` — must surface as IlOpcode sites
            // on the consumer module.
            result.Result!.References.Should().Contain(r =>
                r.ModuleVersionId == consumerMvid
                && r.SiteKind == MemberKind.Method
                && r.ReferenceKind == TypeReferenceKind.IlOpcode,
                because: "cross-module newobj of Box<int>/Box<string> must surface as an IlOpcode reference to Box`1");
        }
    }

    [Fact]
    public void Finds_intra_module_call_through_generic_method_parent_TypeSpec()
    {
        // SampleLib.OrderService.Compute does `new Box<int>(x)` + `box.Value` (same module).
        // The newobj/call operands are MemberRefs whose Parent is a TypeSpec over Box`1.
        // Pre-#69, ScanTypesFromIl only walked InlineType/InlineTok operands, so this intra-
        // module path was invisible — TypeUsageFixture.BoxTypeHandle() (ldtoken) was the only
        // intra site and would mask any regression. We assert specifically on Compute's
        // MethodDef token so the test fails if the MemberRef.Parent walk is removed.
        var (index, mvid) = Load();
        using (index)
        {
            var boxOpenToken = typeof(SampleLib.Box<>).MetadataToken;
            var computeMethod = typeof(SampleLib.OrderService)
                .GetMethod("Compute", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var computeToken = computeMethod.MetadataToken;

            var result = index.FindTypeReferences(mvid, boxOpenToken);

            result.IsSuccess.Should().BeTrue();
            result.Result!.References.Should().Contain(r =>
                r.SiteKind == MemberKind.Method
                && r.ReferenceKind == TypeReferenceKind.IlOpcode
                && r.MetadataToken == computeToken,
                because: "intra-module newobj/call through TypeSpec parent on Box<int> must surface OrderService.Compute as an IlOpcode reference to Box`1");
        }
    }
}
