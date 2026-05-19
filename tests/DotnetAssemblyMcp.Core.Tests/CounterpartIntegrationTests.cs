using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using DotnetAssemblyMcp.Core.Identity;
using DotnetAssemblyMcp.Core.Metadata;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// End-to-end handoff tests against the dotnet-diagnostics-mcp counterpart's CoreClrSample,
/// pulled in via the git submodule at <c>external/dotnet-diagnostics-mcp</c>. They exercise
/// the full chain (Load → Resolve → GetIlBody → ScanIl → FindCallers) on a real ASP.NET
/// Minimal API DLL — the canonical producer-side payload these tools are designed to consume.
///
/// When the submodule is not initialised, every test is skipped with a clear message instead
/// of failing — `git submodule update --init` will turn them on.
/// </summary>
public sealed class CounterpartIntegrationTests
{
    private static readonly string RepoRoot = LocateRepoRoot();
    private static readonly string CoreClrSampleDll = Path.Combine(
        RepoRoot, "external", "dotnet-diagnostics-mcp", "samples", "CoreClrSample",
        "bin", "Release", "net10.0", "CoreClrSample.dll");

    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    private static bool SampleAvailable => File.Exists(CoreClrSampleDll);

    private const string SkipReason =
        "external/dotnet-diagnostics-mcp submodule not initialised — run `git submodule update --init`.";

    [SkippableFact]
    public void Load_resolves_and_walks_a_real_aspnet_assembly()
    {
        Skip.IfNot(SampleAvailable, SkipReason);

        using var index = new MetadataIndex();
        var load = index.Load(CoreClrSampleDll);
        load.IsSuccess.Should().BeTrue();
        var mvid = load.Module!.ModuleVersionId;
        load.Module.MethodCount.Should().BeGreaterThan(0);

        // Pick any method with a non-trivial body — the compiler-generated <Program>$.<Main>$
        // is a reliable fixture in Minimal API templates.
        var (methodToken, methodName) = PickMethodWithBody(CoreClrSampleDll);
        methodToken.Should().NotBe(0, "the sample assembly should contain at least one method with IL");

        var identity = new MethodIdentity(mvid, methodToken);

        var resolve = index.Resolve(identity);
        resolve.IsSuccess.Should().BeTrue();
        resolve.Method!.MethodName.Should().Be(methodName);
        resolve.Method.IlSize.Should().BeGreaterThan(0);

        var body = index.GetIlBody(identity);
        body.IsSuccess.Should().BeTrue();
        body.Body!.IlSize.Should().Be(resolve.Method.IlSize);
        body.Body.InstructionCount.Should().BeGreaterThan(0);

        var scan = index.ScanIl(identity);
        scan.IsSuccess.Should().BeTrue();
        scan.Scan!.InstructionCount.Should().Be(body.Body.InstructionCount);

        var callers = index.FindCallers(identity);
        callers.IsSuccess.Should().BeTrue();
        callers.Result!.ModulesSearched.Should().Be(1);
    }

    [SkippableFact]
    public void FindCallers_picks_up_cross_module_when_sample_loaded_alongside_lib()
    {
        Skip.IfNot(SampleAvailable, SkipReason);

        // Load both the counterpart sample and our own test fixtures; FindCallers on any
        // method in the sample must still report ModulesSearched == 3 (CoreClrSample + the
        // two fixture DLLs).
        using var index = new MetadataIndex();
        index.Load(CoreClrSampleDll);
        index.Load(typeof(SampleLib.OrderService).Assembly.Location);
        index.Load(typeof(SampleConsumer.ConsumerService).Assembly.Location);

        var (sampleToken, _) = PickMethodWithBody(CoreClrSampleDll);
        var sampleMvid = LoadMvid(CoreClrSampleDll);

        var result = index.FindCallers(new MethodIdentity(sampleMvid, sampleToken));
        result.IsSuccess.Should().BeTrue();
        result.Result!.ModulesSearched.Should().Be(3);
    }

    private static Guid LoadMvid(string path)
    {
        using var pe = new PEReader(new MemoryStream(File.ReadAllBytes(path), writable: false));
        var md = pe.GetMetadataReader();
        return md.GetGuid(md.GetModuleDefinition().Mvid);
    }

    /// <summary>
    /// Walks the assembly's MethodDef table and returns the first method definition that has
    /// a non-zero RVA (i.e. an actual IL body), plus its name. Equivalent to what a producer
    /// would hand us via the MethodIdentity contract.
    /// </summary>
    private static (int Token, string Name) PickMethodWithBody(string path)
    {
        using var pe = new PEReader(new MemoryStream(File.ReadAllBytes(path), writable: false));
        var md = pe.GetMetadataReader();
        foreach (var h in md.MethodDefinitions)
        {
            var def = md.GetMethodDefinition(h);
            if (def.RelativeVirtualAddress == 0) continue;
            return (System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(h),
                md.GetString(def.Name));
        }
        return (0, string.Empty);
    }
}
