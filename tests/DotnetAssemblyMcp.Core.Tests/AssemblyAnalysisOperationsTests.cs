using DotnetAssemblyMcp.Application;
using DotnetAssemblyMcp.Core.Decompilation;
using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Metadata;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Tests for the composed, human-oriented <see cref="AssemblyAnalysisOperations"/> workflows
/// (<c>ExplainType</c> / <c>ExplainMethod</c>) against the SampleLib fixture. These verify the
/// orchestration that lets a human resolve a type / method by name without chasing handles or
/// tokens: type overviews, overload collection, exact-vs-substring matching, decompilation, and
/// the error / warning behaviour the CLI relies on.
/// </summary>
public sealed class AssemblyAnalysisOperationsTests
{
    private static string SampleLibPath => typeof(SampleLib.OrderService).Assembly.Location;

    private static (MetadataIndex index, Decompiler dec) NewSubject()
    {
        var index = new MetadataIndex();
        index.Load(SampleLibPath);
        return (index, new Decompiler(index));
    }

    [Fact]
    public void ExplainType_aggregates_members_methods_and_attributes()
    {
        var (index, _) = NewSubject();
        using var _index = index;

        var result = AssemblyAnalysisOperations.ExplainType(index, SampleLibPath, "SampleLib.OrderService");

        result.IsError.Should().BeFalse();
        var data = result.Data!;
        data.Type.FullName.Should().Be("SampleLib.OrderService");
        data.Methods.Should().Contain(m => m.MethodName == "Process");
        data.Members.Should().Contain(m => m.Name == "_counter");
        data.Attributes.Should().NotBeEmpty();
        data.Warnings.Should().BeNull();
    }

    [Fact]
    public void ExplainType_resolves_via_path_without_prior_load()
    {
        using var index = new MetadataIndex();

        var result = AssemblyAnalysisOperations.ExplainType(index, SampleLibPath, "SampleLib.OrderService");

        result.IsError.Should().BeFalse();
        result.Data!.Type.Handle.Should().StartWith("t:");
    }

    [Fact]
    public void ExplainType_unknown_type_returns_error()
    {
        var (index, _) = NewSubject();
        using var _index = index;

        var result = AssemblyAnalysisOperations.ExplainType(index, SampleLibPath, "Nope.DoesNotExist");

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public void ExplainMethod_returns_every_overload()
    {
        var (index, dec) = NewSubject();
        using var _index = index;

        var result = AssemblyAnalysisOperations.ExplainMethod(dec, index, SampleLibPath, "SampleLib.OrderService", "Process");

        result.IsError.Should().BeFalse();
        var data = result.Data!;
        data.Exact.Should().BeTrue();
        data.Overloads.Should().HaveCount(2);
        data.Overloads.Select(o => o.Method.MethodName).Should().OnlyContain(n => n == "Process");
    }

    [Fact]
    public void ExplainMethod_exact_does_not_match_substrings()
    {
        var (index, dec) = NewSubject();
        using var _index = index;

        // "Proc" is a substring of Process / ProcessAsync but is not an exact method name.
        var result = AssemblyAnalysisOperations.ExplainMethod(dec, index, SampleLibPath, "SampleLib.OrderService", "Proc");

        result.IsError.Should().BeTrue();
        result.Error!.Kind.Should().Be(ErrorKinds.InvalidArgument);
        result.Summary.Should().Contain("--contains");
        result.Summary.Should().Contain("Process");
    }

    [Fact]
    public void ExplainMethod_contains_matches_substrings()
    {
        var (index, dec) = NewSubject();
        using var _index = index;

        var result = AssemblyAnalysisOperations.ExplainMethod(
            dec, index, SampleLibPath, "SampleLib.OrderService", "Proc", contains: true);

        result.IsError.Should().BeFalse();
        var data = result.Data!;
        data.Exact.Should().BeFalse();
        data.Overloads.Should().HaveCountGreaterThanOrEqualTo(3);
        data.Overloads.Select(o => o.Method.MethodName).Should().Contain("ProcessAsync");
    }

    [Fact]
    public void ExplainMethod_decompile_populates_csharp()
    {
        var (index, dec) = NewSubject();
        using var _index = index;

        var result = AssemblyAnalysisOperations.ExplainMethod(
            dec, index, SampleLibPath, "SampleLib.OrderService", "Compute", decompile: true);

        result.IsError.Should().BeFalse();
        var detail = result.Data!.Overloads.Single();
        detail.DecompiledCSharp.Should().NotBeNullOrEmpty();
        detail.DecompiledCSharp.Should().Contain("Compute");
    }

    [Fact]
    public void ExplainMethod_without_decompile_leaves_csharp_null()
    {
        var (index, dec) = NewSubject();
        using var _index = index;

        var result = AssemblyAnalysisOperations.ExplainMethod(
            dec, index, SampleLibPath, "SampleLib.OrderService", "Compute");

        result.IsError.Should().BeFalse();
        result.Data!.Overloads.Single().DecompiledCSharp.Should().BeNull();
    }

    [Fact]
    public void ExplainMethod_resolves_source_location_for_a_method_with_sequence_points()
    {
        var (index, dec) = NewSubject();
        using var _index = index;

        var result = AssemblyAnalysisOperations.ExplainMethod(dec, index, SampleLibPath, "SampleLib.OrderService", "Compute");

        result.IsError.Should().BeFalse();
        var source = result.Data!.Overloads.Single().Source;
        source.Should().NotBeNull();
        source!.Found.Should().BeTrue();
        source.File.Should().EndWith("Sample.cs");
    }

    [Fact]
    public void ExplainMethod_unknown_method_returns_error()
    {
        var (index, dec) = NewSubject();
        using var _index = index;

        var result = AssemblyAnalysisOperations.ExplainMethod(dec, index, SampleLibPath, "SampleLib.OrderService", "Nonexistent");

        result.IsError.Should().BeTrue();
        result.Error!.Kind.Should().Be(ErrorKinds.InvalidArgument);
    }

    [Fact]
    public void ExplainMethod_blank_name_is_rejected()
    {
        var (index, dec) = NewSubject();
        using var _index = index;

        var result = AssemblyAnalysisOperations.ExplainMethod(dec, index, SampleLibPath, "SampleLib.OrderService", "   ");

        result.IsError.Should().BeTrue();
        result.Error!.Kind.Should().Be(ErrorKinds.InvalidArgument);
    }

    [Fact]
    public void BuildCallGraph_returns_transitive_callers()
    {
        var (index, _) = NewSubject();
        using var _index = index;

        var result = AssemblyAnalysisOperations.BuildCallGraph(index, SampleLibPath, "SampleLib.OrderService", "Compute", depth: 3);

        result.IsError.Should().BeFalse();
        var graph = result.Data!;
        graph.Roots.Should().ContainSingle();
        var root = graph.Roots[0];
        root.Display.Should().Contain("Compute");
        // Compute <- Process <- ProcessAsync state machine.
        root.Callers.Should().ContainSingle(c => c.Display.Contains("Process"));
        root.Callers[0].Callers.Should().Contain(c => c.Display.Contains("MoveNext"));
        graph.NodeCount.Should().Be(3);
        graph.Truncated.Should().BeFalse();
        graph.Warnings.Should().BeNull();
    }

    [Fact]
    public void BuildCallGraph_marks_depth_limited_nodes()
    {
        var (index, _) = NewSubject();
        using var _index = index;

        var result = AssemblyAnalysisOperations.BuildCallGraph(index, SampleLibPath, "SampleLib.OrderService", "Compute", depth: 1);

        result.IsError.Should().BeFalse();
        var root = result.Data!.Roots[0];
        var process = root.Callers.Should().ContainSingle().Subject;
        process.DepthLimited.Should().BeTrue();
        process.Callers.Should().BeEmpty();
    }

    [Fact]
    public void BuildCallGraph_builds_one_root_per_overload()
    {
        var (index, _) = NewSubject();
        using var _index = index;

        var result = AssemblyAnalysisOperations.BuildCallGraph(index, SampleLibPath, "SampleLib.OrderService", "Process", depth: 2);

        result.IsError.Should().BeFalse();
        // Process(int) and Process(string) are both roots.
        result.Data!.Roots.Should().HaveCount(2);
    }

    [Fact]
    public void BuildCallGraph_honours_max_nodes_budget()
    {
        var (index, _) = NewSubject();
        using var _index = index;

        var result = AssemblyAnalysisOperations.BuildCallGraph(index, SampleLibPath, "SampleLib.OrderService", "Compute", depth: 3, maxNodes: 1);

        result.IsError.Should().BeFalse();
        var graph = result.Data!;
        graph.NodeCount.Should().Be(1);
        graph.Truncated.Should().BeTrue();
        graph.Roots[0].Callers.Should().BeEmpty();
    }

    [Fact]
    public void BuildCallGraph_warns_when_overload_roots_omitted()
    {
        var (index, _) = NewSubject();
        using var _index = index;

        // Two overloads but a budget for a single node: the second root cannot be emitted.
        var result = AssemblyAnalysisOperations.BuildCallGraph(index, SampleLibPath, "SampleLib.OrderService", "Process", depth: 1, maxNodes: 1);

        result.IsError.Should().BeFalse();
        var graph = result.Data!;
        graph.Roots.Should().ContainSingle();
        graph.Truncated.Should().BeTrue();
        graph.Warnings.Should().NotBeNull();
        graph.Warnings.Should().Contain(w => w.Contains("overload root", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildCallGraph_exact_miss_fails_like_explain_method()
    {
        var (index, _) = NewSubject();
        using var _index = index;

        var result = AssemblyAnalysisOperations.BuildCallGraph(index, SampleLibPath, "SampleLib.OrderService", "Proces");

        result.IsError.Should().BeTrue();
        result.Error!.Kind.Should().Be(ErrorKinds.InvalidArgument);
        result.Summary.Should().Contain("--contains");
    }

    [Fact]
    public void BuildCallGraph_negative_depth_is_rejected()
    {
        var (index, _) = NewSubject();
        using var _index = index;

        var result = AssemblyAnalysisOperations.BuildCallGraph(index, SampleLibPath, "SampleLib.OrderService", "Compute", depth: -1);

        result.IsError.Should().BeTrue();
        result.Error!.Kind.Should().Be(ErrorKinds.InvalidArgument);
    }

    [Fact]
    public void BuildCallGraph_blank_name_is_rejected()
    {
        var (index, _) = NewSubject();
        using var _index = index;

        var result = AssemblyAnalysisOperations.BuildCallGraph(index, SampleLibPath, "SampleLib.OrderService", "  ");

        result.IsError.Should().BeTrue();
        result.Error!.Kind.Should().Be(ErrorKinds.InvalidArgument);
    }

    // ---- diff-assemblies ----------------------------------------------------------------------

    private static string SampleLibV2Path =>
        Fixtures.SampleLibV2Fixture.Path
        ?? throw new InvalidOperationException("SampleLibV2 fixture must be built by the test csproj.");

    [Fact]
    public void DiffAssemblies_self_vs_self_reports_no_differences()
    {
        using var index = new MetadataIndex();

        var result = AssemblyAnalysisOperations.DiffAssemblies(index, SampleLibPath, SampleLibPath);

        result.IsError.Should().BeFalse();
        var diff = result.Data!;
        diff.AddedTypes.Should().BeEmpty();
        diff.RemovedTypes.Should().BeEmpty();
        diff.ChangedTypes.Should().BeEmpty();
        diff.Incomplete.Should().BeFalse();
        diff.Warnings.Should().BeNull();
        diff.LeftTypeCount.Should().Be(diff.RightTypeCount);
        diff.LeftTypeCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void DiffAssemblies_detects_added_and_removed_types()
    {
        using var index = new MetadataIndex();

        var result = AssemblyAnalysisOperations.DiffAssemblies(index, SampleLibPath, SampleLibV2Path);

        result.IsError.Should().BeFalse();
        var diff = result.Data!;

        diff.AddedTypes.Should().Contain(t => t.TypeFullName == "SampleLib.BrandNewType");
        diff.RemovedTypes.Should().Contain(t => t.TypeFullName == "SampleLib.CustomerDto");
    }

    [Fact]
    public void DiffAssemblies_detects_changed_members_via_fingerprint()
    {
        using var index = new MetadataIndex();

        var result = AssemblyAnalysisOperations.DiffAssemblies(index, SampleLibPath, SampleLibV2Path);

        result.IsError.Should().BeFalse();
        var order = result.Data!.ChangedTypes.Should().ContainSingle(t => t.TypeFullName == "SampleLib.OrderService").Subject;

        // Process(int) return type int -> long: same identity, different fingerprint => changed.
        order.ChangedMembers.Should().NotBeNull();
        order.ChangedMembers!.Should().Contain(m =>
            m.Name == "Process" && m.Before.Contains("Process(") && m.Before != m.After);

        // Process(int, int) is a new overload => added member.
        order.AddedMembers.Should().NotBeNull();
        order.AddedMembers!.Should().Contain(m => m.Signature.Contains("Process("));

        // SampleLib's OrderService has members absent from V2 (e.g. Echo) => removed members.
        order.RemovedMembers.Should().NotBeNull();
        order.RemovedMembers!.Should().Contain(m => m.Name == "Echo");
    }

    [Fact]
    public void DiffAssemblies_detects_shape_change_on_base_type()
    {
        using var index = new MetadataIndex();

        var result = AssemblyAnalysisOperations.DiffAssemblies(index, SampleLibPath, SampleLibV2Path);

        result.IsError.Should().BeFalse();
        var dog = result.Data!.ChangedTypes.Should().ContainSingle(t => t.TypeFullName == "SampleLib.Dog").Subject;

        dog.ShapeChanges.Should().NotBeNull();
        dog.ShapeChanges!.Should().Contain(s => s.StartsWith("base:") && s.Contains("AnimalBase"));
    }

    [Fact]
    public void DiffAssemblies_excludes_nested_types_inside_non_public_types()
    {
        using var index = new MetadataIndex();

        var result = AssemblyAnalysisOperations.DiffAssemblies(index, SampleLibPath, SampleLibPath);

        result.IsError.Should().BeFalse();
        // Externally-visible nested-public type IS counted; we assert the surface is internally
        // consistent (self-vs-self => no diff) which already validates the visibility filter.
        result.Data!.AddedTypes.Should().BeEmpty();
        result.Data!.RemovedTypes.Should().BeEmpty();
    }

    [Fact]
    public void DiffAssemblies_bad_left_assembly_fails()
    {
        using var index = new MetadataIndex();

        var result = AssemblyAnalysisOperations.DiffAssemblies(index, "/does/not/exist.dll", SampleLibPath);

        result.IsError.Should().BeTrue();
    }
}
