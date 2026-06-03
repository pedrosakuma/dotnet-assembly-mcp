using System.CommandLine;
using DotnetAssemblyMcp.Application;
using DotnetAssemblyMcp.Core.Metadata;

namespace DotnetAssemblyMcp.Cli;

/// <summary>
/// Type-centric subcommands: <c>list-types</c>, <c>list-assembly-references</c>,
/// <c>list-resources</c>, <c>list-attributes</c>, <c>get-type</c>, <c>list-derived-types</c>,
/// <c>list-members</c>.
/// </summary>
internal static class TypeCommands
{
    public static void Register(RootCommand root, CliContext context)
    {
        root.Subcommands.Add(BuildListTypes(context));
        root.Subcommands.Add(BuildListAssemblyReferences(context));
        root.Subcommands.Add(BuildListResources(context));
        root.Subcommands.Add(BuildListAttributes(context));
        root.Subcommands.Add(BuildGetType(context));
        root.Subcommands.Add(BuildListDerivedTypes(context));
        root.Subcommands.Add(BuildListMembers(context));
    }

    private static Command BuildListTypes(CliContext context)
    {
        var mvidOrPath = new Argument<string>("mvid-or-path") { Description = "Module MVID or absolute path to enumerate." };
        var namespacePrefix = new Option<string?>("--namespace") { Description = "Namespace prefix filter." };
        var nameContains = new Option<string?>("--name-contains") { Description = "Case-insensitive substring on the full type name." };
        var kind = new Option<string?>("--kind") { Description = "Kind filter: class | struct | interface | enum | delegate." };
        var cursor = new Option<int>("--cursor") { Description = "Pagination cursor (0 = first page)." };
        var pageSize = new Option<int>("--page-size") { Description = "Max types per page.", DefaultValueFactory = _ => ListTypesQuery.DefaultPageSize };

        var command = new Command("list-types", "Enumerate the type definitions of a module.");
        command.Arguments.Add(mvidOrPath);
        foreach (var opt in new Option[] { namespacePrefix, nameContains, kind, cursor, pageSize })
        {
            command.Options.Add(opt);
        }

        command.SetAction(pr => CliRun.Execute(context, pr, engine =>
            AssemblyOperations.ListTypes(engine.Index, CliPaths.ResolveMvidOrPath(pr.GetValue(mvidOrPath))!, pr.GetValue(namespacePrefix), pr.GetValue(nameContains), pr.GetValue(kind), pr.GetValue(cursor), pr.GetValue(pageSize))));
        return command;
    }

    private static Command BuildListAssemblyReferences(CliContext context)
    {
        var mvidOrPath = new Argument<string>("mvid-or-path") { Description = "Module MVID or absolute path." };
        var command = new Command("list-assembly-references", "Enumerate the AssemblyRef table of a single module.");
        command.Arguments.Add(mvidOrPath);
        command.SetAction(pr => CliRun.Execute(context, pr, engine =>
            AssemblyOperations.ListAssemblyReferences(engine.Index, CliPaths.ResolveMvidOrPath(pr.GetValue(mvidOrPath))!)));
        return command;
    }

    private static Command BuildListResources(CliContext context)
    {
        var mvidOrPath = new Argument<string>("mvid-or-path") { Description = "Module MVID or absolute path." };
        var command = new Command("list-resources", "Enumerate the ManifestResource table of a single module.");
        command.Arguments.Add(mvidOrPath);
        command.SetAction(pr => CliRun.Execute(context, pr, engine =>
            AssemblyOperations.ListResources(engine.Index, CliPaths.ResolveMvidOrPath(pr.GetValue(mvidOrPath))!)));
        return command;
    }

    private static Command BuildListAttributes(CliContext context)
    {
        var target = new Argument<string>("target") { Description = "Target handle: 'a:<mvid>', 't:..', 'm:..', 'pa:..', 'f:..', 'p:..', 'e:..'." };
        var nameContains = new Option<string?>("--name-contains") { Description = "Case-insensitive substring on the attribute type full name." };
        var cursor = new Option<int>("--cursor") { Description = "Pagination cursor (0 = first page)." };
        var pageSize = new Option<int>("--page-size") { Description = "Max attributes per page.", DefaultValueFactory = _ => ListAttributesQuery.DefaultPageSize };

        var command = new Command("list-attributes", "Enumerate the CustomAttribute rows attached to an entity.");
        command.Arguments.Add(target);
        command.Options.Add(nameContains);
        command.Options.Add(cursor);
        command.Options.Add(pageSize);
        command.SetAction(pr => CliRun.Execute(context, pr, engine =>
            AssemblyOperations.ListAttributes(engine.Index, pr.GetValue(target)!, pr.GetValue(nameContains), pr.GetValue(cursor), pr.GetValue(pageSize))));
        return command;
    }

    private static Command BuildGetType(CliContext context)
    {
        var handle = new Option<string?>("--handle") { Description = "Type handle 't:<mvid>:0x<token>'." };
        var mvidOrPath = new Option<string?>("--mvid-or-path") { Description = "Module MVID or path (with --type-full-name)." };
        var typeFullName = new Option<string?>("--type-full-name") { Description = "Full type name ('+'-joined for nested types)." };

        var command = new Command("get-type", "Return the full TypeSummary for a single type.");
        command.Options.Add(handle);
        command.Options.Add(mvidOrPath);
        command.Options.Add(typeFullName);
        command.SetAction(pr => CliRun.Execute(context, pr, engine =>
            AssemblyOperations.GetType(engine.Index, pr.GetValue(handle), CliPaths.ResolveMvidOrPath(pr.GetValue(mvidOrPath)), pr.GetValue(typeFullName))));
        return command;
    }

    private static Command BuildListDerivedTypes(CliContext context)
    {
        var handle = new Option<string?>("--handle") { Description = "Base-type handle 't:<mvid>:0x<token>'." };
        var mvidOrPath = new Option<string?>("--mvid-or-path") { Description = "Module MVID or path (with --type-full-name)." };
        var typeFullName = new Option<string?>("--type-full-name") { Description = "Full base-type name ('+'-joined for nested types)." };
        var transitive = new Option<bool>("--transitive") { Description = "Return the full transitive descendant set (default: direct only)." };
        var cursor = new Option<int>("--cursor") { Description = "Pagination cursor (0 = first page)." };
        var pageSize = new Option<int>("--page-size") { Description = "Max types per page.", DefaultValueFactory = _ => ListDerivedTypesQuery.DefaultPageSize };
        var matchInstantiation = new Option<string[]>("--match-instantiation") { Description = "Base-type generic argument to match (repeatable)." };

        var command = new Command("list-derived-types", "Enumerate types that derive from / implement a base type.");
        command.Options.Add(handle);
        command.Options.Add(mvidOrPath);
        command.Options.Add(typeFullName);
        command.Options.Add(transitive);
        command.Options.Add(cursor);
        command.Options.Add(pageSize);
        command.Options.Add(matchInstantiation);
        command.SetAction(pr => CliRun.Execute(context, pr, engine =>
            AssemblyOperations.ListDerivedTypes(
                engine.Index,
                pr.GetValue(handle),
                CliPaths.ResolveMvidOrPath(pr.GetValue(mvidOrPath)),
                pr.GetValue(typeFullName),
                directOnly: !pr.GetValue(transitive),
                pr.GetValue(cursor),
                pr.GetValue(pageSize),
                CliRun.NullIfEmpty(pr.GetValue(matchInstantiation)))));
        return command;
    }

    private static Command BuildListMembers(CliContext context)
    {
        var handle = new Option<string?>("--handle") { Description = "Type handle 't:<mvid>:0x<token>'." };
        var mvidOrPath = new Option<string?>("--mvid-or-path") { Description = "Module MVID or path (with --type-full-name)." };
        var typeFullName = new Option<string?>("--type-full-name") { Description = "Full type name ('+'-joined for nested types)." };
        var kind = new Option<string?>("--kind") { Description = "Member kind filter: field | property | event." };
        var namePattern = new Option<string?>("--name-pattern") { Description = "Case-insensitive substring on the member name." };
        var signatureContains = new Option<string?>("--signature-contains") { Description = "Case-insensitive substring on the rendered signature." };
        var cursor = new Option<int>("--cursor") { Description = "Pagination cursor (0 = first page)." };
        var pageSize = new Option<int>("--page-size") { Description = "Max members per page.", DefaultValueFactory = _ => ListMembersQuery.DefaultPageSize };

        var command = new Command("list-members", "Enumerate the fields, properties and events of a single type.");
        foreach (var opt in new Option[] { handle, mvidOrPath, typeFullName, kind, namePattern, signatureContains, cursor, pageSize })
        {
            command.Options.Add(opt);
        }

        command.SetAction(pr =>
        {
            if (!TryParseMemberKind(pr.GetValue(kind), out MemberKind? memberKind))
            {
                Console.Error.WriteLine($"error: invalid --kind '{pr.GetValue(kind)}' (expected field | property | event).");
                return 2;
            }

            return CliRun.Execute(context, pr, engine =>
                AssemblyOperations.ListMembers(engine.Index, pr.GetValue(handle), CliPaths.ResolveMvidOrPath(pr.GetValue(mvidOrPath)), pr.GetValue(typeFullName), memberKind, pr.GetValue(namePattern), pr.GetValue(signatureContains), pr.GetValue(cursor), pr.GetValue(pageSize)));
        });
        return command;
    }

    private static bool TryParseMemberKind(string? value, out MemberKind? kind)
    {
        kind = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (Enum.TryParse(value, ignoreCase: true, out MemberKind parsed))
        {
            kind = parsed;
            return true;
        }

        return false;
    }
}
