using System.CommandLine;
using DotnetAssemblyMcp.Application;
using DotnetAssemblyMcp.Core.Metadata;

namespace DotnetAssemblyMcp.Cli;

/// <summary>
/// Method-centric subcommands: <c>get-method</c>, <c>decompile-method</c>, <c>decompile-type</c>,
/// <c>get-method-il</c>, <c>list-methods</c>, <c>find-method</c>, <c>find-callers</c>,
/// <c>get-method-source</c>.
/// </summary>
internal static class MethodCommands
{
    public static void Register(RootCommand root, CliContext context)
    {
        root.Subcommands.Add(BuildGetMethod(context));
        root.Subcommands.Add(BuildDecompileMethod(context));
        root.Subcommands.Add(BuildDecompileType(context));
        root.Subcommands.Add(BuildGetMethodIl(context));
        root.Subcommands.Add(BuildListMethods(context));
        root.Subcommands.Add(BuildFindMethod(context));
        root.Subcommands.Add(BuildFindCallers(context));
        root.Subcommands.Add(BuildGetMethodSource(context));
    }

    private static Argument<string> MvidArg() =>
        new("module-version-id") { Description = "MVID GUID (or 'm:<mvid>:0x<token>' handle) of the declaring assembly." };

    private static Argument<string?> TokenArg() =>
        new("metadata-token") { Description = "Method definition metadata token (decimal or 0x hex).", Arity = ArgumentArity.ZeroOrOne };

    private static Option<string?> AssemblyOption() =>
        new("--assembly") { Description = "Path hint to the assembly so it can be auto-loaded if not already in the index." };

    private static Command BuildGetMethod(CliContext context)
    {
        var mvid = MvidArg();
        var token = TokenArg();
        var assembly = AssemblyOption();
        var typeFullName = new Option<string?>("--type-full-name") { Description = "Declaring type full name (display sanity-check)." };
        var methodName = new Option<string?>("--method-name") { Description = "Method name (display sanity-check)." };
        var genericArity = new Option<int>("--generic-arity") { Description = "Generic arity from the producer payload." };
        var genericTypeArgs = new Option<string[]>("--generic-type-arg") { Description = "Declaring-type generic argument (repeatable)." };
        var genericMethodArgs = new Option<string[]>("--generic-method-arg") { Description = "Method generic argument (repeatable)." };
        var specMvid = new Option<string?>("--method-spec-mvid") { Description = "MethodSpec module MVID (closed-instantiation fast-path)." };
        var specToken = new Option<string?>("--method-spec-token") { Description = "MethodSpec metadata token (closed-instantiation fast-path)." };
        var includeNative = new Option<bool>("--include-native-body") { Description = "Probe for a precompiled ReadyToRun native body." };

        var command = new Command("get-method", "Resolve a method identity to its declaring type, name, signature and attributes.");
        command.Arguments.Add(mvid);
        command.Arguments.Add(token);
        foreach (var opt in new Option[] { assembly, typeFullName, methodName, genericArity, genericTypeArgs, genericMethodArgs, specMvid, specToken, includeNative })
        {
            command.Options.Add(opt);
        }

        command.SetAction(pr => CliRun.Execute(context, pr, engine =>
            AssemblyOperations.GetMethod(
                engine.Index,
                pr.GetValue(mvid)!,
                pr.GetValue(token),
                pr.GetValue(typeFullName),
                pr.GetValue(methodName),
                pr.GetValue(genericArity),
                pr.GetValue(assembly),
                CliRun.NullIfEmpty(pr.GetValue(genericTypeArgs)),
                CliRun.NullIfEmpty(pr.GetValue(genericMethodArgs)),
                pr.GetValue(specMvid),
                pr.GetValue(specToken),
                pr.GetValue(includeNative))));
        return command;
    }

    private static Command BuildDecompileMethod(CliContext context)
    {
        var mvid = MvidArg();
        var token = TokenArg();
        var assembly = AssemblyOption();
        var maxChars = new Option<int>("--max-chars") { Description = "Cap on returned characters (0 = server default)." };

        var command = new Command("decompile-method", "Decompile a single method to C#.");
        command.Arguments.Add(mvid);
        command.Arguments.Add(token);
        command.Options.Add(assembly);
        command.Options.Add(maxChars);
        command.SetAction(pr => CliRun.Execute(context, pr, engine =>
            AssemblyOperations.DecompileMethod(
                engine.Decompiler, engine.Index, pr.GetValue(mvid)!, pr.GetValue(token), pr.GetValue(maxChars), pr.GetValue(assembly), CancellationToken.None)));
        return command;
    }

    private static Command BuildDecompileType(CliContext context)
    {
        var mvid = MvidArg();
        var token = TokenArg();
        var assembly = AssemblyOption();
        var maxChars = new Option<int>("--max-chars") { Description = "Cap on returned characters (0 = server default)." };

        var command = new Command("decompile-type", "Decompile an entire type to C#.");
        command.Arguments.Add(mvid);
        command.Arguments.Add(token);
        command.Options.Add(assembly);
        command.Options.Add(maxChars);
        command.SetAction(pr => CliRun.Execute(context, pr, engine =>
            AssemblyOperations.DecompileType(
                engine.Decompiler, engine.Index, pr.GetValue(mvid)!, pr.GetValue(token), pr.GetValue(maxChars), pr.GetValue(assembly), CancellationToken.None)));
        return command;
    }

    private static Command BuildGetMethodIl(CliContext context)
    {
        var mvid = MvidArg();
        var token = TokenArg();
        var assembly = AssemblyOption();
        var format = new Option<string>("--format") { Description = "Projection: 'raw' (default), 'text' or 'scan'.", DefaultValueFactory = _ => "raw" };
        var maxBytes = new Option<int>("--max-bytes") { Description = "raw only: cap on IL bytes (0 = default)." };
        var maxLines = new Option<int>("--max-lines") { Description = "text only: cap on output lines (0 = default)." };

        var command = new Command("get-method-il", "Read a method's IL (raw bytes, ildasm text, or outbound-reference scan).");
        command.Arguments.Add(mvid);
        command.Arguments.Add(token);
        command.Options.Add(assembly);
        command.Options.Add(format);
        command.Options.Add(maxBytes);
        command.Options.Add(maxLines);
        command.SetAction(pr => CliRun.Execute(context, pr, engine =>
            AssemblyOperations.GetMethodIl(
                engine.Disassembler, engine.Index, pr.GetValue(mvid)!, pr.GetValue(token), pr.GetValue(format)!, pr.GetValue(maxBytes), pr.GetValue(maxLines), pr.GetValue(assembly), CancellationToken.None)));
        return command;
    }

    private static Command BuildListMethods(CliContext context)
    {
        var handle = new Option<string?>("--handle") { Description = "Type handle 't:<mvid>:0x<token>'." };
        var mvidOrPath = new Option<string?>("--mvid-or-path") { Description = "Module MVID or path (with --type-full-name)." };
        var typeFullName = new Option<string?>("--type-full-name") { Description = "Full type name ('+'-joined for nested types)." };
        var namePattern = new Option<string?>("--name-pattern") { Description = "Case-insensitive substring filter on the method name." };
        var cursor = new Option<int>("--cursor") { Description = "Pagination cursor (0 = first page)." };
        var pageSize = new Option<int>("--page-size") { Description = "Max methods per page.", DefaultValueFactory = _ => ListMethodsQuery.DefaultPageSize };

        var command = new Command("list-methods", "Enumerate the methods of a single type.");
        foreach (var opt in new Option[] { handle, mvidOrPath, typeFullName, namePattern, cursor, pageSize })
        {
            command.Options.Add(opt);
        }

        command.SetAction(pr => CliRun.Execute(context, pr, engine =>
            AssemblyOperations.ListMethods(engine.Index, pr.GetValue(handle), pr.GetValue(mvidOrPath), pr.GetValue(typeFullName), pr.GetValue(namePattern), pr.GetValue(cursor), pr.GetValue(pageSize))));
        return command;
    }

    private static Command BuildFindMethod(CliContext context)
    {
        var mvidOrPath = new Argument<string>("mvid-or-path") { Description = "Module MVID or absolute path to search." };
        var namePattern = new Argument<string>("name-pattern") { Description = "Regex matched (case-insensitive) against each method's short name." };
        var signatureContains = new Option<string?>("--signature-contains") { Description = "Case-insensitive substring filter on the decoded signature." };
        var cursor = new Option<int?>("--cursor") { Description = "Pagination cursor (omit for first page)." };
        var pageSize = new Option<int>("--page-size") { Description = "Max matches per page.", DefaultValueFactory = _ => FindMethodQuery.DefaultPageSize };

        var command = new Command("find-method", "Module-wide method search by name regex (+ optional signature filter).");
        command.Arguments.Add(mvidOrPath);
        command.Arguments.Add(namePattern);
        command.Options.Add(signatureContains);
        command.Options.Add(cursor);
        command.Options.Add(pageSize);
        command.SetAction(pr => CliRun.Execute(context, pr, engine =>
            AssemblyOperations.FindMethod(engine.Index, pr.GetValue(mvidOrPath)!, pr.GetValue(namePattern)!, pr.GetValue(signatureContains), pr.GetValue(cursor), pr.GetValue(pageSize), CancellationToken.None)));
        return command;
    }

    private static Command BuildFindCallers(CliContext context)
    {
        var mvid = MvidArg();
        var token = TokenArg();
        var assembly = AssemblyOption();
        var genericTypeArgs = new Option<string[]>("--generic-type-arg") { Description = "Declaring-type generic argument (repeatable)." };
        var genericMethodArgs = new Option<string[]>("--generic-method-arg") { Description = "Method generic argument (repeatable)." };
        var specMvid = new Option<string?>("--method-spec-mvid") { Description = "MethodSpec module MVID (closed-instantiation fast-path)." };
        var specToken = new Option<string?>("--method-spec-token") { Description = "MethodSpec metadata token (closed-instantiation fast-path)." };

        var command = new Command("find-callers", "Find every method whose IL directly calls the requested callee.");
        command.Arguments.Add(mvid);
        command.Arguments.Add(token);
        foreach (var opt in new Option[] { assembly, genericTypeArgs, genericMethodArgs, specMvid, specToken })
        {
            command.Options.Add(opt);
        }

        command.SetAction(pr => CliRun.Execute(context, pr, engine =>
            AssemblyOperations.FindCallers(
                engine.Index,
                pr.GetValue(mvid)!,
                pr.GetValue(token),
                pr.GetValue(assembly),
                CliRun.NullIfEmpty(pr.GetValue(genericTypeArgs)),
                CliRun.NullIfEmpty(pr.GetValue(genericMethodArgs)),
                pr.GetValue(specMvid),
                pr.GetValue(specToken),
                CancellationToken.None)));
        return command;
    }

    private static Command BuildGetMethodSource(CliContext context)
    {
        var mvid = MvidArg();
        var token = TokenArg();
        var assembly = AssemblyOption();

        var command = new Command("get-method-source", "Resolve a method to file/line via PDB + SourceLink / embedded source.");
        command.Arguments.Add(mvid);
        command.Arguments.Add(token);
        command.Options.Add(assembly);
        command.SetAction(pr => CliRun.Execute(context, pr, engine =>
            AssemblyOperations.GetMethodSource(engine.Index, pr.GetValue(mvid)!, pr.GetValue(token), pr.GetValue(assembly), CancellationToken.None)));
        return command;
    }
}
