using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Metadata;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Validates <see cref="MetadataIndex.ListAssemblyReferences"/> against the SampleLib fixture.
/// Every .NET assembly references at least System.Runtime / System.Private.CoreLib, so the
/// table is never empty for our fixtures.
/// </summary>
public sealed class ListAssemblyReferencesTests
{
    private static string SampleLibPath => typeof(SampleLib.OrderService).Assembly.Location;
    private static Guid SampleLibMvid => typeof(SampleLib.OrderService).Assembly.ManifestModule.ModuleVersionId;

    [Fact]
    public void Returns_non_empty_list_with_system_runtime()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);

        var result = index.ListAssemblyReferences(SampleLibMvid);

        result.IsSuccess.Should().BeTrue();
        var page = result.Page!;
        page.References.Should().NotBeEmpty();
        page.References.Should().Contain(r => r.Name == "System.Runtime" || r.Name == "System.Private.CoreLib");
    }

    [Fact]
    public void Reference_summary_fields_are_populated()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);

        var page = index.ListAssemblyReferences(SampleLibMvid).Page!;
        var first = page.References[0];

        first.MetadataToken.Should().BeGreaterThan(0);
        first.Handle.Should().StartWith("a:").And.Contain($"{SampleLibMvid:D}");
        first.Name.Should().NotBeNullOrWhiteSpace();
        first.Version.Should().MatchRegex(@"^\d+\.\d+\.\d+\.\d+$");
        // System.Runtime is signed → public key token present and 16-hex-char (8 bytes).
        var systemRuntime = page.References.FirstOrDefault(r => r.Name == "System.Runtime");
        if (systemRuntime is not null)
        {
            systemRuntime.PublicKeyTokenHex.Should().NotBeNullOrEmpty();
            systemRuntime.PublicKeyTokenHex!.Length.Should().Be(16);
        }
    }

    [Fact]
    public void Returns_ModuleNotFound_for_unknown_mvid()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);

        var result = index.ListAssemblyReferences(Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.ModuleNotFound);
    }

    [Fact]
    public void Returns_IdentityMalformed_for_empty_mvid()
    {
        using var index = new MetadataIndex();
        var result = index.ListAssemblyReferences(Guid.Empty);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.IdentityMalformed);
    }
}
