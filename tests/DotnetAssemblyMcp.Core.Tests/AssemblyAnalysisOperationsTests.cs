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
}
