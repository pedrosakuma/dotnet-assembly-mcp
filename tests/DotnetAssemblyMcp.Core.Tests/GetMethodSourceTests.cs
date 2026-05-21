using DotnetAssemblyMcp.Core;
using DotnetAssemblyMcp.Core.Metadata;
using DotnetAssemblyMcp.Server.Tools;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Tests for the get_method_source tool (issue #8 / Phase Z(e)). Verifies that the SampleLib
/// fixture (built with DebugType=portable, sibling .pdb) resolves to file/line coordinates,
/// that absent PDBs surface a non-error found=false, and that SourceLink fields are absent
/// when no SourceLink CustomDebugInformation is present.
/// </summary>
public sealed class GetMethodSourceTests
{
    private static string SampleLibPath => typeof(SampleLib.OrderService).Assembly.Location;

    private static (MetadataIndex Index, Guid Mvid, int Token) ResolveProcessToken()
    {
        var index = new MetadataIndex();
        var load = index.Load(SampleLibPath);
        load.IsSuccess.Should().BeTrue();
        var mvid = load.Module!.ModuleVersionId;
        var find = index.FindMethod(mvid, new FindMethodQuery("^Process$"));
        find.IsSuccess.Should().BeTrue();
        find.Page!.Matches.Should().NotBeEmpty();
        return (index, mvid, find.Page.Matches[0].MetadataToken);
    }

    [Fact]
    public void Sibling_pdb_resolves_to_file_and_lines()
    {
        var (index, mvid, token) = ResolveProcessToken();
        using (index)
        {
            var result = AssemblyTools.GetMethodSource(index, mvid.ToString("D"), $"0x{token:X8}");

            result.IsError.Should().BeFalse(result.Summary);
            var loc = result.Data!;
            loc.Found.Should().BeTrue($"reason={loc.Reason}, pdb={loc.PdbKind}");
            loc.File.Should().NotBeNullOrEmpty();
            loc.StartLine.Should().BeGreaterThan(0);
            loc.EndLine.Should().BeGreaterThanOrEqualTo(loc.StartLine!.Value);
            loc.PdbKind.Should().BeOneOf(PdbKind.Portable, PdbKind.Embedded);
        }
    }

    [Fact]
    public void Source_link_is_resolved_when_pdb_carries_a_source_link_document()
    {
        // The repo's Directory.Build.props enables Microsoft.SourceLink.GitHub, so the
        // SampleLib PDB ships SourceLink CustomDebugInformation. Verify the resolver walks
        // the JSON, substitutes the document path into the URL pattern, and returns a usable
        // https URL — satisfies the integration acceptance from issue #8.
        var (index, mvid, token) = ResolveProcessToken();
        using (index)
        {
            var result = AssemblyTools.GetMethodSource(index, mvid.ToString("D"), $"0x{token:X8}");
            var sl = result.Data!.SourceLink;
            sl.Should().NotBeNullOrEmpty();
            sl!.Should().StartWith("https://");
            sl.Should().EndWith(Path.GetFileName(result.Data.File)!);
        }
    }

    [Fact]
    public void Absent_pdb_surfaces_found_false_with_pdb_kind_none()
    {
        // Copy the DLL alone (no .pdb) into a temp directory. Probe + load + lookup must
        // return Found=false / PdbKind=None instead of crashing.
        var tmpDir = Directory.CreateTempSubdirectory("dotnet-assembly-mcp-source-test");
        try
        {
            var orphan = Path.Combine(tmpDir.FullName, "OrphanedLib.dll");
            File.Copy(SampleLibPath, orphan);

            using var index = new MetadataIndex();
            var load = index.Load(orphan);
            load.IsSuccess.Should().BeTrue();
            var mvid = load.Module!.ModuleVersionId;
            var find = index.FindMethod(mvid, new FindMethodQuery("^Process$"));
            var token = find.Page!.Matches[0].MetadataToken;

            var result = AssemblyTools.GetMethodSource(index, mvid.ToString("D"), $"0x{token:X8}");
            result.IsError.Should().BeFalse(result.Summary);
            result.Data!.Found.Should().BeFalse();
            result.Data.PdbKind.Should().Be(PdbKind.None);
            result.Data.Reason.Should().NotBeNullOrEmpty();
        }
        finally { tmpDir.Delete(recursive: true); }
    }

    [Fact]
    public void Get_method_source_honors_assembly_path_hint()
    {
        // Discover token from a throwaway index, then invoke on a fresh empty index with
        // only the hint — composes with Z(a).
        Guid mvid;
        int token;
        using (var warmup = new MetadataIndex())
        {
            var load = warmup.Load(SampleLibPath);
            mvid = load.Module!.ModuleVersionId;
            var find = warmup.FindMethod(mvid, new FindMethodQuery("^Process$"));
            token = find.Page!.Matches[0].MetadataToken;
        }

        using var index = new MetadataIndex();
        var result = AssemblyTools.GetMethodSource(
            index, mvid.ToString("D"), $"0x{token:X8}", assemblyPathHint: SampleLibPath);

        result.IsError.Should().BeFalse(result.Summary);
        result.Data!.Found.Should().BeTrue();
        index.List().Should().ContainSingle(m => m.ModuleVersionId == mvid);
    }

    [Fact]
    public void Repeat_calls_reuse_the_cached_pdb_reader()
    {
        // No public counter; just verify the second call gives identical output without
        // throwing (smoke test of the _sourceCache idempotency).
        var (index, mvid, token) = ResolveProcessToken();
        using (index)
        {
            var first = AssemblyTools.GetMethodSource(index, mvid.ToString("D"), $"0x{token:X8}").Data!;
            var second = AssemblyTools.GetMethodSource(index, mvid.ToString("D"), $"0x{token:X8}").Data!;
            second.File.Should().Be(first.File);
            second.StartLine.Should().Be(first.StartLine);
            second.PdbKind.Should().Be(first.PdbKind);
        }
    }

    [Fact]
    public void Embedded_source_text_is_surfaced_when_pdb_carries_EmbedAllSources()
    {
        // SampleLib is built with <DebugType>embedded</DebugType> + <EmbedAllSources>true</EmbedAllSources>
        // (#105) so the portable PDB carries the embedded-source CDI for every Document.
        var (index, mvid, token) = ResolveProcessToken();
        using (index)
        {
            var result = AssemblyTools.GetMethodSource(index, mvid.ToString("D"), $"0x{token:X8}");
            result.IsError.Should().BeFalse(result.Summary);

            var loc = result.Data!;
            loc.Found.Should().BeTrue($"reason={loc.Reason}, pdb={loc.PdbKind}");
            loc.PdbKind.Should().BeOneOf(PdbKind.Portable, PdbKind.Embedded);
            loc.EmbeddedSource.Should().NotBeNull(
                "EmbedAllSources=true must surface the embedded-source CDI on every Document");
            loc.EmbeddedSource!.Path.Should().NotBeNullOrEmpty();
            loc.EmbeddedSource.Length.Should().BeGreaterThan(0);
            loc.EmbeddedSource.Content.Should().Contain("namespace SampleLib",
                "the embedded source must round-trip the original Sample.cs text");
            loc.EmbeddedSource.Content.Should().Contain("class OrderService",
                "OrderService.Process is declared in the same file");
        }
    }
}
