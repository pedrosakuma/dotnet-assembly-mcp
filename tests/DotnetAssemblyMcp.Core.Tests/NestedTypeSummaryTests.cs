using System.Reflection;
using DotnetAssemblyMcp.Core.Identity;
using DotnetAssemblyMcp.Core.Metadata;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Regression tests for issue #23: <see cref="MethodSummary.TypeFullName"/> must include the
/// declaring chain for nested types (e.g. "SampleLib.NestingHost+Inner"), not just the leaf
/// name. Affects <c>get_method</c>, <c>list_methods</c>, <c>find_method</c>, etc.
/// </summary>
public sealed class NestedTypeSummaryTests
{
    private static string SampleLibPath => typeof(SampleLib.OrderService).Assembly.Location;

    [Fact]
    public void GetMethod_returns_OuterPlusInner_for_nested_type_method()
    {
        using var index = new MetadataIndex();
        var load = index.Load(SampleLibPath);
        load.IsSuccess.Should().BeTrue();
        var mvid = load.Module!.ModuleVersionId;

        var pingMethod = typeof(SampleLib.NestingHost.Inner)
            .GetMethod("Ping", BindingFlags.Public | BindingFlags.Instance)!;
        var token = pingMethod.MetadataToken;

        var identity = new MethodIdentity(mvid, token, GenericArity: 0);
        var resolved = index.Resolve(identity);

        resolved.IsSuccess.Should().BeTrue(resolved.Error?.Message);
        var summary = resolved.Method!;
        summary.TypeFullName.Should().Be("SampleLib.NestingHost+Inner");
        summary.MethodName.Should().Be("Ping");
        summary.Signature.Should().Contain("SampleLib.NestingHost+Inner.Ping");
    }

    [Fact]
    public void FindMethod_reports_nested_TypeFullName()
    {
        using var index = new MetadataIndex();
        var load = index.Load(SampleLibPath);
        load.IsSuccess.Should().BeTrue();
        var mvid = load.Module!.ModuleVersionId;

        var page = index.FindMethod(mvid, new FindMethodQuery("^Ping$"));
        page.IsSuccess.Should().BeTrue();
        var hit = page.Page!.Matches.Single(m => m.MethodName == "Ping");
        hit.TypeFullName.Should().Be("SampleLib.NestingHost+Inner");
    }
}
