using DotnetAssemblyMcp.Core;
using DotnetAssemblyMcp.Core.Decompilation;
using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Metadata;
using DotnetAssemblyMcp.Server.Tools;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Server-wrapper coverage for the ergonomics-review consistency fixes:
///   #4 — find_attribute_targets `targetKinds` accepts the string-array shape (and a lenient
///        comma-separated spelling inside a single element), matching the other multi-value params.
///   #5 — list_resources always emits a NextActionHint.
/// These exercise the <see cref="AssemblyTools"/> wrappers (argument parsing + hint shaping),
/// which the Core-level tests do not reach.
/// </summary>
public sealed class ReviewConsistencyFindingsTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly IMetadataIndex _index;

    private const string FixtureMarker = "SampleLib.FixtureMarkerAttribute";

    private static readonly string[] KindsType = { "type" };
    private static readonly string[] KindsTypeMethod = { "type", "method" };
    private static readonly string[] KindsTypeMethodComma = { "type,method" };
    private static readonly string[] KindsBogus = { "bogus" };

    public ReviewConsistencyFindingsTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMetadataIndex>(_ => new MetadataIndex(watchForChanges: false));
        services.AddSingleton<IDecompiler, Decompiler>();
        services.AddSingleton<IIlDisassembler, IlDisassembler>();
        _sp = services.BuildServiceProvider();
        _index = _sp.GetRequiredService<IMetadataIndex>();

        AssemblyTools.LoadAssembly(_index, typeof(SampleLib.OrderService).Assembly.Location)
            .IsError.Should().BeFalse();
    }

    private static string SampleLibPath => typeof(SampleLib.OrderService).Assembly.Location;
    private static Guid SampleLibMvid => typeof(SampleLib.OrderService).Assembly.ManifestModule.ModuleVersionId;

    // ---- #4: targetKinds array shape ----

    [Fact]
    public void FindAttributeTargets_array_shape_scopes_to_requested_kinds()
    {
        var result = AssemblyTools.FindAttributeTargets(
            _index, FixtureMarker, SampleLibPath, targetKinds: KindsType);

        result.IsError.Should().BeFalse();
        result.Data!.Hits.Should().NotBeEmpty();
        result.Data.Hits.Select(h => h.Kind).Should().OnlyContain(k => k == AttributeTargetKind.Type);
    }

    [Fact]
    public void FindAttributeTargets_multi_element_array_unions_kinds()
    {
        var result = AssemblyTools.FindAttributeTargets(
            _index, FixtureMarker, SampleLibPath, targetKinds: KindsTypeMethod);

        result.IsError.Should().BeFalse();
        result.Data!.Hits.Select(h => h.Kind).Distinct()
            .Should().BeSubsetOf(new[] { AttributeTargetKind.Type, AttributeTargetKind.Method });
        result.Data.Hits.Should().Contain(h => h.Kind == AttributeTargetKind.Type);
        result.Data.Hits.Should().Contain(h => h.Kind == AttributeTargetKind.Method);
    }

    [Fact]
    public void FindAttributeTargets_comma_inside_single_element_is_accepted()
    {
        var array = AssemblyTools.FindAttributeTargets(
            _index, FixtureMarker, SampleLibPath, targetKinds: KindsTypeMethod);
        var comma = AssemblyTools.FindAttributeTargets(
            _index, FixtureMarker, SampleLibPath, targetKinds: KindsTypeMethodComma);

        comma.IsError.Should().BeFalse();
        comma.Data!.Hits.Select(h => h.Kind).Distinct().OrderBy(k => k)
            .Should().Equal(array.Data!.Hits.Select(h => h.Kind).Distinct().OrderBy(k => k));
    }

    [Fact]
    public void FindAttributeTargets_unknown_kind_is_rejected()
    {
        var result = AssemblyTools.FindAttributeTargets(
            _index, FixtureMarker, SampleLibPath, targetKinds: KindsBogus);

        result.IsError.Should().BeTrue();
        result.Error!.Kind.Should().Be(ErrorKinds.InvalidArgument);
        result.Error.Message.Should().Contain("unknown targetKind 'bogus'");
    }

    [Fact]
    public void FindAttributeTargets_null_targetKinds_returns_all_kinds()
    {
        var result = AssemblyTools.FindAttributeTargets(_index, FixtureMarker, SampleLibPath);

        result.IsError.Should().BeFalse();
        result.Data!.Hits.Select(h => h.Kind).Distinct().Count().Should().BeGreaterThan(1);
    }

    // ---- #5: list_resources hint ----

    [Fact]
    public void ListResources_emits_hint_for_a_module_with_inpe_resources()
    {
        var result = AssemblyTools.ListResources(_index, SampleLibMvid.ToString("D"));

        result.IsError.Should().BeFalse();
        result.Data!.Resources.Should().Contain(r => r.Name == "SampleLib.Strings.txt");
        result.Hints.Should().NotBeNullOrEmpty();

        var hint = result.Hints!.Single();
        hint.NextTool.Should().Be("list_types");
        hint.SuggestedArguments.Should().ContainKey("mvidOrPath");
        hint.SuggestedArguments!["mvidOrPath"].Should().Be(SampleLibMvid.ToString("D"));
    }

    public void Dispose() => _sp.Dispose();
}
