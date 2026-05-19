using System.Text.Json;
using DotnetAssemblyMcp.Core.Metadata;
using DotnetAssemblyMcp.Server.Resources;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Tests for the assembly://manifest/loaded MCP resources (issue #7 / Phase Z(d)). Read-only
/// JSON views over the metadata index; no new server state.
/// </summary>
public sealed class AssemblyManifestResourcesTests
{
    private static string SampleLibPath => typeof(SampleLib.OrderService).Assembly.Location;

    [Fact]
    public void ListLoaded_with_empty_index_returns_count_zero_and_empty_modules_array()
    {
        using var index = new MetadataIndex();
        var json = AssemblyManifestResources.ListLoaded(index);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(0);
        doc.RootElement.GetProperty("modules").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void ListLoaded_emits_one_entry_per_loaded_module()
    {
        using var index = new MetadataIndex();
        var load = index.Load(SampleLibPath);
        load.IsSuccess.Should().BeTrue();

        var json = AssemblyManifestResources.ListLoaded(index);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(1);
        var first = doc.RootElement.GetProperty("modules")[0];
        first.GetProperty("moduleVersionId").GetString()
            .Should().Be(load.Module!.ModuleVersionId.ToString("D"));
        first.GetProperty("name").GetString().Should().Be(load.Module.ModuleName);
        first.GetProperty("methodCount").GetInt32().Should().Be(load.Module.MethodCount);
        first.GetProperty("path").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ReadLoaded_with_known_mvid_returns_module_detail()
    {
        using var index = new MetadataIndex();
        var load = index.Load(SampleLibPath);
        var mvid = load.Module!.ModuleVersionId;

        var json = AssemblyManifestResources.ReadLoaded(index, mvid.ToString("D"));

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("moduleVersionId").GetString()
            .Should().Be(mvid.ToString("D"));
        doc.RootElement.GetProperty("methodCount").GetInt32().Should().Be(load.Module.MethodCount);
        doc.RootElement.TryGetProperty("kind", out _).Should().BeFalse();
    }

    [Fact]
    public void ReadLoaded_with_unknown_mvid_returns_structured_module_not_found()
    {
        using var index = new MetadataIndex();
        var json = AssemblyManifestResources.ReadLoaded(index, Guid.NewGuid().ToString("D"));

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("kind").GetString().Should().Be("module_not_found");
        doc.RootElement.GetProperty("error").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ReadLoaded_with_malformed_mvid_returns_invalid_argument()
    {
        using var index = new MetadataIndex();
        var json = AssemblyManifestResources.ReadLoaded(index, "not-a-guid");

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("kind").GetString().Should().Be("invalid_argument");
    }

    [Fact]
    public void ListLoaded_is_a_read_only_view_does_not_mutate_the_index()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);
        var before = index.List().Count;

        _ = AssemblyManifestResources.ListLoaded(index);
        _ = AssemblyManifestResources.ListLoaded(index);

        index.List().Count.Should().Be(before);
    }
}
