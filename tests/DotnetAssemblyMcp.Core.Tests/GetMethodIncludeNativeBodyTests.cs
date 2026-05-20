using DotnetAssemblyMcp.Core.Metadata;
using DotnetAssemblyMcp.Server.Tools;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Issue #74 — tool-layer coverage of <c>get_method(includeNativeBody:true)</c>: confirms
/// the payload is populated for R2R modules, omitted for JIT-only modules, and that a
/// hand-off hint pointing at <c>dotnet-native-mcp.disassemble</c> is appended.
/// </summary>
public sealed class GetMethodIncludeNativeBodyTests
{
    private static readonly string? SpcPath = FindSharedCoreLib();

    [SkippableFact]
    public void IncludeNativeBody_emits_NativeBody_and_disassemble_hint()
    {
        Skip.If(SpcPath is null, "Shared framework System.Private.CoreLib.dll not found.");

        using var index = new MetadataIndex();
        var loaded = index.Load(SpcPath!);
        var mvid = loaded.Module!.ModuleVersionId;
        int token = typeof(string).GetProperty("Length")!.GetMethod!.MetadataToken;

        var result = AssemblyTools.GetMethod(
            index,
            mvid.ToString("D"),
            $"0x{token:X8}",
            includeNativeBody: true);

        result.IsError.Should().BeFalse(result.Summary);
        result.Data!.NativeBody.Should().NotBeNull();
        result.Data.NativeBody!.Source.Should().Be(NativeBodySource.R2R);
        result.Data.NativeBody.HotRegion.Size.Should().BeGreaterThan(0);

        result.Hints.Should().Contain(h => h.NextTool == "dotnet-native-mcp.disassemble");
        var hint = result.Hints!.First(h => h.NextTool == "dotnet-native-mcp.disassemble");
        hint.SuggestedArguments.Should().NotBeNull();
        hint.SuggestedArguments!["imagePath"].Should().Be(SpcPath);
        hint.SuggestedArguments["rva"].Should().Be(result.Data.NativeBody.HotRegion.Rva);
        hint.SuggestedArguments["size"].Should().Be(result.Data.NativeBody.HotRegion.Size);
    }

    [Fact]
    public void IncludeNativeBody_on_JIT_only_assembly_leaves_NativeBody_null_and_suggests_diag()
    {
        using var index = new MetadataIndex();
        index.Load(typeof(SampleLib.OrderService).Assembly.Location);
        var mvid = typeof(SampleLib.OrderService).Assembly.ManifestModule.ModuleVersionId;
        int token = typeof(SampleLib.OrderService).GetMethods()[0].MetadataToken;

        var result = AssemblyTools.GetMethod(
            index,
            mvid.ToString("D"),
            $"0x{token:X8}",
            includeNativeBody: true);

        result.IsError.Should().BeFalse(result.Summary);
        result.Data!.NativeBody.Should().BeNull();
        result.Hints.Should().Contain(h => h.NextTool == "dotnet-diagnostics-mcp.capture_method_disasm");
    }

    [Fact]
    public void Default_includeNativeBody_false_omits_NativeBody_payload()
    {
        using var index = new MetadataIndex();
        index.Load(typeof(SampleLib.OrderService).Assembly.Location);
        var mvid = typeof(SampleLib.OrderService).Assembly.ManifestModule.ModuleVersionId;
        int token = typeof(SampleLib.OrderService).GetMethods()[0].MetadataToken;

        var result = AssemblyTools.GetMethod(index, mvid.ToString("D"), $"0x{token:X8}");

        result.IsError.Should().BeFalse(result.Summary);
        result.Data!.NativeBody.Should().BeNull();
        result.Hints.Should().NotContain(h => h.NextTool == "dotnet-native-mcp.disassemble");
        result.Hints.Should().NotContain(h => h.NextTool == "dotnet-diagnostics-mcp.capture_method_disasm");
    }

    private static string? FindSharedCoreLib()
    {
        string[] roots =
        {
            Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? string.Empty,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet"),
            "/usr/share/dotnet",
            "/usr/local/share/dotnet",
            @"C:\Program Files\dotnet",
        };
        foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r)))
        {
            var sharedAppDir = Path.Combine(root, "shared", "Microsoft.NETCore.App");
            if (!Directory.Exists(sharedAppDir)) continue;
            var hit = Directory.EnumerateDirectories(sharedAppDir)
                .OrderByDescending(d => d, StringComparer.Ordinal)
                .Select(d => Path.Combine(d, "System.Private.CoreLib.dll"))
                .FirstOrDefault(File.Exists);
            if (hit is not null) return hit;
        }
        return null;
    }
}
