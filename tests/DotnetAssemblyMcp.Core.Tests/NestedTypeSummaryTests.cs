using System.Reflection;
using DotnetAssemblyMcp.Core.Identity;
using DotnetAssemblyMcp.Core.Metadata;
using DotnetAssemblyMcp.Core.Tests.Fixtures;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Regression tests for issue #23: <see cref="MethodSummary.TypeFullName"/> must include the
/// declaring chain for nested types (e.g. "SampleLib.NestingHost+Inner"), not just the leaf
/// name. Affects <c>get_method</c>, <c>list_methods</c>, <c>find_method</c>, etc.
///
/// Uses <see cref="SampleLibFixture"/> to share a single cold-load of SampleLib across
/// every test in this class (issue #81 pattern).
/// </summary>
public sealed class NestedTypeSummaryTests : IClassFixture<SampleLibFixture>
{
    private readonly SampleLibFixture _fixture;

    public NestedTypeSummaryTests(SampleLibFixture fixture) => _fixture = fixture;

    [Fact]
    public void GetMethod_returns_OuterPlusInner_for_nested_type_method()
    {
        var pingMethod = typeof(SampleLib.NestingHost.Inner)
            .GetMethod("Ping", BindingFlags.Public | BindingFlags.Instance)!;
        var token = pingMethod.MetadataToken;

        var identity = new MethodIdentity(_fixture.Mvid, token, GenericArity: 0);
        var resolved = _fixture.Index.Resolve(identity);

        resolved.IsSuccess.Should().BeTrue(resolved.Error?.Message);
        var summary = resolved.Method!;
        summary.TypeFullName.Should().Be("SampleLib.NestingHost+Inner");
        summary.MethodName.Should().Be("Ping");
        summary.Signature.Should().Contain("SampleLib.NestingHost+Inner.Ping");
    }

    [Fact]
    public void FindMethod_reports_nested_TypeFullName()
    {
        var page = _fixture.Index.FindMethod(_fixture.Mvid, new FindMethodQuery("^Ping$"));
        page.IsSuccess.Should().BeTrue();
        var hit = page.Page!.Matches.Single(m => m.MethodName == "Ping");
        hit.TypeFullName.Should().Be("SampleLib.NestingHost+Inner");
    }
}
