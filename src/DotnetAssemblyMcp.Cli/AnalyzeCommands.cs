using System.CommandLine;
using DotnetAssemblyMcp.Application;
using DotnetAssemblyMcp.Core;

namespace DotnetAssemblyMcp.Cli;

/// <summary>
/// Human-oriented composed subcommands: <c>explain-type</c> and <c>explain-method</c>. Unlike the
/// 1:1 subcommands, these take human-friendly inputs (assembly path / MVID + type name [+ method
/// name]) and internally chain several operations, so a human never has to chase a handle or a
/// metadata token. They render a purpose-built text view; <c>--json</c> still emits the standard
/// <see cref="AssemblyResult{T}"/> envelope so the output stays scriptable.
/// </summary>
internal static class AnalyzeCommands
{
    public static void Register(RootCommand root, CliContext context)
    {
        root.Subcommands.Add(BuildExplainType(context));
        root.Subcommands.Add(BuildExplainMethod(context));
    }

    private static Command BuildExplainType(CliContext context)
    {
        var mvidOrPath = new Argument<string>("mvid-or-path") { Description = "Module MVID or absolute path to the assembly." };
        var typeFullName = new Argument<string>("type-full-name") { Description = "Full type name ('+'-joined for nested types)." };

        var command = new Command("explain-type", "One-shot overview of a type: summary, attributes, members and methods — resolved by name.");
        command.Arguments.Add(mvidOrPath);
        command.Arguments.Add(typeFullName);
        command.SetAction(pr =>
        {
            CliRun.Preload(context, pr);
            AssemblyResult<TypeExplanation> result = AssemblyAnalysisOperations.ExplainType(
                context.Engine.Index, pr.GetValue(mvidOrPath)!, pr.GetValue(typeFullName)!);
            return Render(result, pr.GetValue(context.JsonOption), ExplainRenderer.WriteType);
        });
        return command;
    }

    private static Command BuildExplainMethod(CliContext context)
    {
        var mvidOrPath = new Argument<string>("mvid-or-path") { Description = "Module MVID or absolute path to the assembly." };
        var typeFullName = new Argument<string>("type-full-name") { Description = "Full declaring type name ('+'-joined for nested types)." };
        var methodName = new Argument<string>("method-name") { Description = "Method name to resolve (exact by default)." };
        var contains = new Option<bool>("--contains") { Description = "Match the method name by case-insensitive substring instead of exact." };
        var decompile = new Option<bool>("--decompile") { Description = "Decompile each selected overload to C#." };
        var maxChars = new Option<int>("--max-chars") { Description = "Per-method decompilation character cap (0 = engine default)." };

        var command = new Command("explain-method", "Resolve a method by name and show each overload's signature, source location and (optionally) decompiled C#.");
        command.Arguments.Add(mvidOrPath);
        command.Arguments.Add(typeFullName);
        command.Arguments.Add(methodName);
        command.Options.Add(contains);
        command.Options.Add(decompile);
        command.Options.Add(maxChars);
        command.SetAction(pr =>
        {
            CliRun.Preload(context, pr);
            AssemblyResult<MethodExplanation> result = AssemblyAnalysisOperations.ExplainMethod(
                context.Engine.Decompiler,
                context.Engine.Index,
                pr.GetValue(mvidOrPath)!,
                pr.GetValue(typeFullName)!,
                pr.GetValue(methodName)!,
                pr.GetValue(contains),
                pr.GetValue(decompile),
                pr.GetValue(maxChars),
                CancellationToken.None);
            return Render(result, pr.GetValue(context.JsonOption), ExplainRenderer.WriteMethod);
        });
        return command;
    }

    /// <summary>
    /// Renders a composed result: <c>--json</c> delegates to the shared envelope serializer for
    /// scriptability; text mode uses the supplied bespoke writer (errors still go to stderr with a
    /// non-zero exit code, matching the rest of the CLI).
    /// </summary>
    private static int Render<T>(AssemblyResult<T> result, bool json, Action<TextWriter, T> writeText)
    {
        if (json)
        {
            return CliRenderer.Render(result, json: true);
        }

        if (result.IsError)
        {
            return CliRenderer.Render(result, json: false);
        }

        Console.WriteLine(result.Summary);
        writeText(Console.Out, result.Data!);
        return 0;
    }
}
