using System.CommandLine;
using DotnetAssemblyMcp.Application;
using DotnetAssemblyMcp.Core;
using DotnetAssemblyMcp.Core.Metadata;

namespace DotnetAssemblyMcp.Cli;

/// <summary>
/// Per-process state shared by every subcommand: the Core engine (built once via
/// <see cref="AssemblyEngineFactory"/>) plus the two global options every command honours.
/// The CLI is one-shot, so a single engine instance lives for the duration of the invocation.
/// </summary>
internal sealed class CliContext
{
    public required AssemblyEngine Engine { get; init; }

    /// <summary>Global <c>--json</c> flag: emit the full envelope instead of human text.</summary>
    public required Option<bool> JsonOption { get; init; }

    /// <summary>
    /// Global repeatable <c>--load &lt;path&gt;</c> option. Because the CLI is one-shot, a handle
    /// returned by an earlier invocation is meaningless unless the owning assembly is reloaded.
    /// Every path supplied here is loaded into the metadata index before the subcommand runs,
    /// which also primes cross-module reference queries.
    /// </summary>
    public required Option<string[]> LoadOption { get; init; }
}

/// <summary>Shared plumbing for binding subcommand actions to <see cref="AssemblyOperations"/>.</summary>
internal static class CliRun
{
    /// <summary>
    /// Loads every <c>--load</c> path into the index (best-effort; failures are warned to stderr),
    /// invokes <paramref name="operation"/>, and renders the result honouring <c>--json</c>.
    /// </summary>
    public static int Execute<T>(CliContext context, ParseResult parseResult, Func<AssemblyEngine, AssemblyResult<T>> operation)
    {
        Preload(context, parseResult);
        AssemblyResult<T> result = operation(context.Engine);
        return CliRenderer.Render(result, parseResult.GetValue(context.JsonOption));
    }

    /// <summary>
    /// Loads every <c>--load</c> path into the index (best-effort; failures are warned to stderr).
    /// Exposed so composed commands that bypass <see cref="Execute{T}"/> still honour the global option.
    /// </summary>
    public static void Preload(CliContext context, ParseResult parseResult)
    {
        string[]? paths = parseResult.GetValue(context.LoadOption);
        if (paths is null)
        {
            return;
        }

        foreach (var path in paths)
        {
            AssemblyResult<ModuleSummary> loaded = AssemblyOperations.LoadAssembly(context.Engine.Index, path);
            if (loaded.IsError)
            {
                Console.Error.WriteLine($"warning: --load '{path}': {loaded.Summary}");
            }
        }
    }

    /// <summary>Collapses an empty / missing repeatable option to <c>null</c> to preserve op semantics.</summary>
    public static string[]? NullIfEmpty(string[]? values) => values is { Length: > 0 } ? values : null;
}
