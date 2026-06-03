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
}
