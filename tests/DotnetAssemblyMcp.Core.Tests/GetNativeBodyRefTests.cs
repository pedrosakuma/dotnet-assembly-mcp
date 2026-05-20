using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Metadata;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// R2R native-body lookup tests for issue #74. Hits the real shared-framework
/// <c>System.Private.CoreLib.dll</c> as the positive fixture (always R2R-compiled on x64)
/// and <c>SampleLib</c> as the negative (JIT-only).
/// </summary>
public sealed class GetNativeBodyRefTests
{
    // Latest installed shared framework. SDK 10.0.201 ships runtime 10.0.5.
    private static readonly string? SpcPath = FindSharedCoreLib();

    private static string SampleLibPath => typeof(SampleLib.OrderService).Assembly.Location;
    private static Guid SampleLibMvid => typeof(SampleLib.OrderService).Assembly.ManifestModule.ModuleVersionId;

    [SkippableFact]
    public void Returns_native_body_for_R2R_precompiled_method()
    {
        Skip.If(SpcPath is null, "Could not locate shared framework System.Private.CoreLib.dll.");

        using var index = new MetadataIndex();
        var loaded = index.Load(SpcPath!);
        loaded.IsSuccess.Should().BeTrue();
        var mvid = loaded.Module!.ModuleVersionId;

        // String.get_Length is a guaranteed R2R hit on x64 SPCorLib.
        var lengthToken = typeof(string).GetProperty("Length")!.GetMethod!.MetadataToken;

        var result = index.GetNativeBodyRef(mvid, lengthToken);

        result.IsSuccess.Should().BeTrue();
        result.Found.Should().BeTrue("String.get_Length is always R2R-precompiled in shared SPCorLib");
        var body = result.Body!;
        body.Source.Should().Be(NativeBodySource.R2R);
        body.PePath.Should().Be(SpcPath);
        body.Architecture.Should().Be(NativeArchitecture.X64);
        body.HotRegion.Should().NotBeNull();
        body.HotRegion.Rva.Should().BeGreaterThan(0);
        body.HotRegion.Size.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Returns_not_found_for_JIT_only_assembly()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);

        var anyMethod = typeof(SampleLib.OrderService).GetMethods()[0];
        var result = index.GetNativeBodyRef(SampleLibMvid, anyMethod.MetadataToken);

        result.IsSuccess.Should().BeTrue();
        result.Found.Should().BeFalse("user-built SampleLib has no ManagedNativeHeader");
        result.Body.Should().BeNull();
        result.Error.Should().BeNull();
    }

    [Fact]
    public void Fails_for_unknown_mvid()
    {
        using var index = new MetadataIndex();
        var unknown = Guid.Parse("00000000-0000-0000-0000-DEADBEEFDEAD");

        var result = index.GetNativeBodyRef(unknown, 0x06000001);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.ModuleNotFound);
    }

    [Fact]
    public void Fails_for_non_methoddef_token()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);

        // 0x01000001 is a TypeRef token, not MethodDef.
        var result = index.GetNativeBodyRef(SampleLibMvid, 0x01000001);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.IdentityMalformed);
    }

    [SkippableFact]
    public void High_percentage_of_SPCorLib_methods_have_native_bodies()
    {
        Skip.If(SpcPath is null, "Could not locate shared framework System.Private.CoreLib.dll.");

        using var index = new MetadataIndex();
        var loaded = index.Load(SpcPath!);
        var mvid = loaded.Module!.ModuleVersionId;

        // Walk MethodDef table via reflection.
        using var fs = File.OpenRead(SpcPath!);
        using var pe = new PEReader(fs);
        var md = pe.GetMetadataReader();

        int total = 0, withBody = 0;
        foreach (var h in md.MethodDefinitions)
        {
            total++;
            int tok = MetadataTokens.GetToken(h);
            if (index.GetNativeBodyRef(mvid, tok).Found) withBody++;
        }

        total.Should().BeGreaterThan(1000);
        // We've empirically measured ~50% precompilation on .NET 10 SPCorLib.
        ((double)withBody / total).Should().BeGreaterThan(0.40,
            "expected substantial R2R coverage in shared-framework SPCorLib");
    }

    private static string? FindSharedCoreLib()
    {
        // Probe both Linux and Windows dotnet roots; fail soft so the test simply skips
        // when nobody installed a shared runtime on this machine.
        string[] dotnetRoots =
        {
            Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? string.Empty,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet"),
            "/usr/share/dotnet",
            "/usr/local/share/dotnet",
            @"C:\Program Files\dotnet",
        };
        foreach (var root in dotnetRoots.Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r)))
        {
            var sharedAppDir = Path.Combine(root, "shared", "Microsoft.NETCore.App");
            if (!Directory.Exists(sharedAppDir)) continue;
            // Pick the highest version directory that actually contains SPC.
            var hit = Directory.EnumerateDirectories(sharedAppDir)
                .OrderByDescending(d => d, StringComparer.Ordinal)
                .Select(d => Path.Combine(d, "System.Private.CoreLib.dll"))
                .FirstOrDefault(File.Exists);
            if (hit is not null) return hit;
        }
        return null;
    }
}
