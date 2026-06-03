using System.CommandLine;
using DotnetAssemblyMcp.Application;

namespace DotnetAssemblyMcp.Cli;

/// <summary>
/// Composition root for the CLI. Builds the <see cref="RootCommand"/> with the global
/// <c>--json</c> / <c>--load</c> options and all subcommands, wiring each to the shared
/// <see cref="AssemblyEngine"/>. Kept separate from <c>Program</c> so tests can drive the
/// full command pipeline in-process.
/// </summary>
internal static class CliApplication
{
    /// <summary>Parses <paramref name="args"/> against a freshly built root command and invokes it.</summary>
    public static int Run(string[] args)
    {
        AssemblyEngine engine = AssemblyEngineFactory.Create(watchForChanges: false);
        try
        {
            return Build(engine).Parse(args).Invoke();
        }
        finally
        {
            DisposeEngine(engine);
        }
    }

    /// <summary>
    /// Disposes an engine's underlying Core services. The CLI is a one-shot process with no DI
    /// host to own their lifetime, so it releases the metadata index (PE readers) and the IL
    /// disassembler (cached <c>PEFile</c>s) itself. Each Core <c>Dispose</c> is idempotent.
    /// </summary>
    internal static void DisposeEngine(AssemblyEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        (engine.Disassembler as IDisposable)?.Dispose();
        (engine.Decompiler as IDisposable)?.Dispose();
        (engine.Index as IDisposable)?.Dispose();
    }

    /// <summary>
    /// Builds the root command over a supplied <paramref name="engine"/> (tests pass their own so
    /// they can pre-load fixtures and assert on a deterministic index).
    /// </summary>
    public static RootCommand Build(AssemblyEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);

        var root = new RootCommand(
            "Static navigation of compiled .NET assemblies — types, methods, attributes, references, " +
            "and on-demand decompilation, straight from the terminal.");

        var jsonOption = new Option<bool>("--json")
        {
            Description = "Emit the full AssemblyResult envelope as indented JSON instead of human text.",
            Recursive = true,
        };
        var loadOption = new Option<string[]>("--load")
        {
            Description = "Load an assembly into the index before the command runs. Repeatable.",
            Recursive = true,
        };

        root.Options.Add(jsonOption);
        root.Options.Add(loadOption);

        var context = new CliContext
        {
            Engine = engine,
            JsonOption = jsonOption,
            LoadOption = loadOption,
        };

        LifecycleCommands.Register(root, context);
        MethodCommands.Register(root, context);
        TypeCommands.Register(root, context);
        ReferenceCommands.Register(root, context);
        AnalyzeCommands.Register(root, context);

        return root;
    }
}
