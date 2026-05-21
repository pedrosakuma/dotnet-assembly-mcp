using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Metadata;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Validates <see cref="MetadataIndex.ListResources"/> against the SampleLib fixture, which
/// embeds a single text resource (<c>SampleLib.Strings.txt</c>) via the csproj.
/// </summary>
public sealed class ListResourcesTests
{
    private static string SampleLibPath => typeof(SampleLib.OrderService).Assembly.Location;
    private static Guid SampleLibMvid => typeof(SampleLib.OrderService).Assembly.ManifestModule.ModuleVersionId;

    [Fact]
    public void Surfaces_the_embedded_strings_resource_with_offset_and_length()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);

        var result = index.ListResources(SampleLibMvid);

        result.IsSuccess.Should().BeTrue();
        var page = result.Page!;
        page.ModuleVersionId.Should().Be(SampleLibMvid);
        page.Resources.Should().ContainSingle(r => r.Name == "SampleLib.Strings.txt");

        var res = page.Resources.Single(r => r.Name == "SampleLib.Strings.txt");
        res.IsPublic.Should().BeTrue("EmbeddedResource defaults to Public visibility");
        res.Implementation.Should().Be(ResourceImplementationKind.InPe);
        res.Offset.Should().NotBeNull();
        res.Offset!.Value.Should().BeGreaterThanOrEqualTo(0);
        res.Length.Should().NotBeNull("the in-PE payload length must decode from the section");
        res.Length!.Value.Should().BeGreaterThan(0);
        res.LinkedFileName.Should().BeNull();
        res.LinkedAssemblyName.Should().BeNull();
        // Token must be a ManifestResource row (table 0x28).
        ((res.MetadataToken >> 24) & 0xFF).Should().Be(0x28);
    }

    [Fact]
    public void Length_matches_the_original_file_size_plus_no_padding()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);

        var page = index.ListResources(SampleLibMvid).Page!;
        var res = page.Resources.Single(r => r.Name == "SampleLib.Strings.txt");

        // Cross-check with what System.Reflection materializes from the same resource — guards
        // against off-by-four / wrong-section-base regressions in the offset decoder.
        var asm = typeof(SampleLib.OrderService).Assembly;
        using var stream = asm.GetManifestResourceStream("SampleLib.Strings.txt")!;
        stream.Length.Should().Be(res.Length!.Value);
    }

    [Fact]
    public void Empty_mvid_is_rejected_with_identity_malformed()
    {
        using var index = new MetadataIndex();
        var result = index.ListResources(Guid.Empty);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.IdentityMalformed);
    }

    [Fact]
    public void Unknown_mvid_surfaces_module_not_found()
    {
        using var index = new MetadataIndex();
        var result = index.ListResources(Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.ModuleNotFound);
    }
}
