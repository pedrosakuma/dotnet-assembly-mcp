using System.CommandLine;
using DotnetAssemblyMcp.Application;
using DotnetAssemblyMcp.Cli;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// End-to-end smoke tests for the <c>dotnet-assembly-cli</c> front-end. They drive the full
/// System.CommandLine pipeline in-process (parse → bind → AssemblyOperations → render) over a
/// real engine and the SampleLib fixture, asserting on captured console output and exit codes.
/// </summary>
public sealed class CliSmokeTests
{
    private static string SampleLibPath => typeof(SampleLib.OrderService).Assembly.Location;

    private static (int ExitCode, string Out, string Error) Invoke(params string[] args)
    {
        AssemblyEngine engine = AssemblyEngineFactory.Create(watchForChanges: false);
        RootCommand root = CliApplication.Build(engine);

        var originalOut = Console.Out;
        var originalError = Console.Error;
        var outWriter = new StringWriter();
        var errorWriter = new StringWriter();
        try
        {
            Console.SetOut(outWriter);
            Console.SetError(errorWriter);
            int exit = root.Parse(args).Invoke();
            return (exit, outWriter.ToString(), errorWriter.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            CliApplication.DisposeEngine(engine);
        }
    }

    private static (Guid Mvid, string TokenHex) DiscoverMethod(string namePattern)
    {
        AssemblyEngine engine = AssemblyEngineFactory.Create(watchForChanges: false);
        try
        {
            AssemblyOperations.LoadAssembly(engine.Index, SampleLibPath);
            var find = AssemblyOperations.FindMethod(engine.Index, SampleLibPath, namePattern);
            find.IsError.Should().BeFalse();
            var match = find.Data!.Matches[0];
            return (match.ModuleVersionId, "0x" + match.MetadataToken.ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            CliApplication.DisposeEngine(engine);
        }
    }

    [Fact]
    public void ListTypes_Text_ListsTypesAndSucceeds()
    {
        var (exit, output, _) = Invoke("list-types", SampleLibPath);

        exit.Should().Be(0);
        output.Should().Contain("SampleLib.OrderService");
        output.Should().Contain("Handle: t:");
    }

    [Fact]
    public void ListTypes_Json_EmitsFullEnvelope()
    {
        var (exit, output, _) = Invoke("--json", "list-types", SampleLibPath, "--page-size", "1");

        exit.Should().Be(0);
        output.TrimStart().Should().StartWith("{");
        output.Should().Contain("\"Summary\"");
        output.Should().Contain("\"Data\"");
        output.Should().Contain("\"Hints\"");
    }

    [Fact]
    public void ListMethods_Text_ResolvesTypeByPath()
    {
        var (exit, output, _) = Invoke(
            "list-methods",
            "--mvid-or-path", SampleLibPath,
            "--type-full-name", "SampleLib.OrderService");

        exit.Should().Be(0);
        output.Should().Contain("Process");
        output.Should().Contain("Handle: m:");
    }

    [Fact]
    public void DecompileMethod_Text_PrintsCSharpSource()
    {
        // First discover a method token via find-method, then decompile it.
        var (mvid, tokenHex) = DiscoverMethod("Process");

        var (exit, output, _) = Invoke(
            "decompile-method",
            mvid.ToString(),
            tokenHex,
            "--assembly", SampleLibPath);

        exit.Should().Be(0);
        output.Should().Contain("Source:");
        output.Should().Contain("Process");
    }

    [Fact]
    public void GetType_NotFound_ReturnsErrorExitCodeAndStderr()
    {
        var (exit, output, error) = Invoke(
            "get-type",
            "--mvid-or-path", SampleLibPath,
            "--type-full-name", "Nope.DoesNotExist");

        exit.Should().Be(1);
        output.Should().BeEmpty();
        error.Should().Contain("error:");
    }

    [Fact]
    public void Load_GlobalOption_PrimesIndexForHandleCommands()
    {
        var (mvid, tokenHex) = DiscoverMethod("Process");

        var (exit, output, _) = Invoke(
            "--load", SampleLibPath,
            "find-callers",
            mvid.ToString(),
            tokenHex);

        exit.Should().Be(0);
        output.Should().Contain("Callers:");
    }

    [Fact]
    public void Load_AfterSubcommand_PrimesIndex()
    {
        var (mvid, tokenHex) = DiscoverMethod("Process");

        // --load is a recursive global option, so it is honoured when placed after the subcommand.
        var (exit, output, _) = Invoke(
            "find-callers",
            mvid.ToString(),
            tokenHex,
            "--load", SampleLibPath);

        exit.Should().Be(0);
        output.Should().Contain("Callers:");
    }

    [Fact]
    public void ListTypes_RelativePath_Resolves()
    {
        string relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), SampleLibPath);
        relative.Should().NotBe(SampleLibPath, "the fixture must be reachable by a relative path for this test");

        var (exit, output, _) = Invoke("list-types", relative);

        exit.Should().Be(0);
        output.Should().Contain("SampleLib.OrderService");
    }

    [Fact]
    public void Load_Subcommand_IsNotRegistered()
    {
        // The stateful 'load' MCP tool has no standalone meaning in a one-shot CLI; it must not
        // be exposed as a subcommand (the global '--load' option is the priming mechanism instead).
        var (exit, _, error) = Invoke("load", SampleLibPath);

        exit.Should().NotBe(0);
        error.Should().Contain("'load'");
    }

    [Fact]
    public void ListAssemblies_Subcommand_IsNotRegistered()
    {
        // 'list-assemblies' would only ever echo back what was primed on the same command line,
        // so it is intentionally not a CLI subcommand.
        var (exit, _, error) = Invoke("list-assemblies");

        exit.Should().NotBe(0);
        error.Should().Contain("'list-assemblies'");
    }

    [Fact]
    public void FindCallers_UnloadedModule_EmitsModuleNotFoundCliHint()
    {
        var (exit, _, error) = Invoke("find-callers", Guid.NewGuid().ToString(), "0x06000001");

        exit.Should().Be(1);
        error.Should().Contain("module_not_found");
        error.Should().Contain("hint:");
    }

    [Fact]
    public void ExplainType_Text_RendersGroupedOverview()
    {
        var (exit, output, _) = Invoke("explain-type", SampleLibPath, "SampleLib.OrderService");

        exit.Should().Be(0);
        output.Should().Contain("Type: SampleLib.OrderService");
        output.Should().Contain("Kind: Class");
        output.Should().Contain("Methods (");
        output.Should().Contain("Process");
    }

    [Fact]
    public void ExplainType_Json_EmitsCompositeEnvelope()
    {
        var (exit, output, _) = Invoke("--json", "explain-type", SampleLibPath, "SampleLib.OrderService");

        exit.Should().Be(0);
        output.TrimStart().Should().StartWith("{");
        output.Should().Contain("\"Type\"");
        output.Should().Contain("\"Methods\"");
    }

    [Fact]
    public void ExplainType_UnknownType_ErrorsToStderr()
    {
        var (exit, output, error) = Invoke("explain-type", SampleLibPath, "Nope.Missing");

        exit.Should().Be(1);
        output.Should().BeEmpty();
        error.Should().Contain("error:");
    }

    [Fact]
    public void ExplainMethod_Text_ListsOverloads()
    {
        var (exit, output, _) = Invoke("explain-method", SampleLibPath, "SampleLib.OrderService", "Process");

        exit.Should().Be(0);
        output.Should().Contain("Method: SampleLib.OrderService.Process");
        output.Should().Contain("(2 overload(s))");
        output.Should().Contain("Source:");
    }

    [Fact]
    public void ExplainMethod_Decompile_PrintsCSharpBody()
    {
        var (exit, output, _) = Invoke("explain-method", SampleLibPath, "SampleLib.OrderService", "Compute", "--decompile");

        exit.Should().Be(0);
        output.Should().Contain("--- C# ---");
        output.Should().Contain("Compute");
    }

    [Fact]
    public void ExplainMethod_ExactMiss_SuggestsContains()
    {
        var (exit, _, error) = Invoke("explain-method", SampleLibPath, "SampleLib.OrderService", "Proc");

        exit.Should().Be(1);
        error.Should().Contain("--contains");
    }

    [Fact]
    public void ExplainMethod_Contains_MatchesSubstrings()
    {
        var (exit, output, _) = Invoke("explain-method", SampleLibPath, "SampleLib.OrderService", "Proc", "--contains");

        exit.Should().Be(0);
        output.Should().Contain("[substring match]");
        output.Should().Contain("ProcessAsync");
    }

    [Fact]
    public void CallGraph_Text_RendersTransitiveCallerTree()
    {
        var (exit, output, _) = Invoke("callgraph", SampleLibPath, "SampleLib.OrderService", "Compute", "--depth", "3");

        exit.Should().Be(0);
        output.Should().Contain("Call graph: SampleLib.OrderService.Compute");
        output.Should().Contain("Process");
        output.Should().Contain("MoveNext");
    }

    [Fact]
    public void CallGraph_DepthLimit_MarksUnexpandedCallers()
    {
        var (exit, output, _) = Invoke("callgraph", SampleLibPath, "SampleLib.OrderService", "Compute", "--depth", "1");

        exit.Should().Be(0);
        output.Should().Contain("more callers not shown");
    }

    [Fact]
    public void CallGraph_MaxNodes_FlagsTruncation()
    {
        var (exit, output, _) = Invoke("callgraph", SampleLibPath, "SampleLib.OrderService", "Compute", "--max-nodes", "1");

        exit.Should().Be(0);
        output.Should().Contain("truncated");
    }

    [Fact]
    public void CallGraph_Json_EmitsCompositeEnvelope()
    {
        var (exit, output, _) = Invoke("--json", "callgraph", SampleLibPath, "SampleLib.OrderService", "Compute");

        exit.Should().Be(0);
        output.TrimStart().Should().StartWith("{");
        output.Should().Contain("\"Roots\"");
        output.Should().Contain("\"NodeCount\"");
    }

    [Fact]
    public void CallGraph_ExactMiss_ReturnsErrorExitCode()
    {
        var (exit, _, error) = Invoke("callgraph", SampleLibPath, "SampleLib.OrderService", "Proces");

        exit.Should().Be(1);
        error.Should().Contain("--contains");
    }

    [Fact]
    public void DiffAssemblies_SelfVsSelf_ReportsNoDifferences()
    {
        var (exit, output, _) = Invoke("diff-assemblies", SampleLibPath, SampleLibPath);

        exit.Should().Be(0);
        output.Should().Contain("No public-surface differences.");
    }

    [Fact]
    public void DiffAssemblies_DifferentAssembly_RendersAddedAndRemoved()
    {
        var v2 = Fixtures.SampleLibV2Fixture.Path;
        v2.Should().NotBeNull("SampleLibV2 fixture must be built by the test csproj");

        var (exit, output, _) = Invoke("diff-assemblies", SampleLibPath, v2!);

        exit.Should().Be(0);
        output.Should().Contain("Added types");
        output.Should().Contain("Changed types");
        output.Should().Contain("SampleLib.OrderService");
    }

    [Fact]
    public void DiffAssemblies_Json_EmitsCompositeEnvelope()
    {
        var (exit, output, _) = Invoke("--json", "diff-assemblies", SampleLibPath, SampleLibPath);

        exit.Should().Be(0);
        output.TrimStart().Should().StartWith("{");
        output.Should().Contain("\"AddedTypes\"");
        output.Should().Contain("\"RemovedTypes\"");
        output.Should().Contain("\"ChangedTypes\"");
    }

    [Fact]
    public void DiffAssemblies_BadAssembly_ReturnsErrorExitCode()
    {
        var (exit, _, _) = Invoke("diff-assemblies", "/does/not/exist.dll", SampleLibPath);

        exit.Should().Be(1);
    }
}
