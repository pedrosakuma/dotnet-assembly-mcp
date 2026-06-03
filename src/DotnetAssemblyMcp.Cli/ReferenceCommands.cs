using System.CommandLine;
using DotnetAssemblyMcp.Application;

namespace DotnetAssemblyMcp.Cli;

/// <summary>
/// Cross-reference subcommands: <c>find-string-references</c>, <c>find-attribute-targets</c>,
/// <c>find-member-references</c>, <c>find-type-references</c>.
/// </summary>
internal static class ReferenceCommands
{
    public static void Register(RootCommand root, CliContext context)
    {
        root.Subcommands.Add(BuildFindStringReferences(context));
        root.Subcommands.Add(BuildFindAttributeTargets(context));
        root.Subcommands.Add(BuildFindMemberReferences(context));
        root.Subcommands.Add(BuildFindTypeReferences(context));
    }

    private static Command BuildFindStringReferences(CliContext context)
    {
        var query = new Argument<string>("query") { Description = "The string to search for in ldstr literals." };
        var matchMode = new Option<string?>("--match-mode") { Description = "Match semantics: 'exact' (default), 'contains', or 'regex'." };
        var mvidOrPath = new Option<string?>("--mvid-or-path") { Description = "Limit the search to a single module (MVID or path)." };
        var maxHits = new Option<int>("--max-hits") { Description = "Cap on returned hits (0 = default)." };

        var command = new Command("find-string-references", "Find every method whose IL contains a matching string literal.");
        command.Arguments.Add(query);
        command.Options.Add(matchMode);
        command.Options.Add(mvidOrPath);
        command.Options.Add(maxHits);
        command.SetAction(pr => CliRun.Execute(context, pr, engine =>
            AssemblyOperations.FindStringReferences(engine.Index, pr.GetValue(query)!, pr.GetValue(matchMode), pr.GetValue(mvidOrPath), pr.GetValue(maxHits), CancellationToken.None)));
        return command;
    }

    private static Command BuildFindAttributeTargets(CliContext context)
    {
        var attributeTypeFullName = new Argument<string>("attribute-type-full-name") { Description = "Full attribute type name (e.g. 'System.ObsoleteAttribute')." };
        var mvidOrPath = new Option<string?>("--mvid-or-path") { Description = "Limit the search to a single module (MVID or path)." };
        var targetKinds = new Option<string[]>("--target-kind") { Description = "Filter target kind: assembly|type|method|parameter|field|property|event (repeatable)." };
        var maxHits = new Option<int>("--max-hits") { Description = "Cap on returned hits (0 = default)." };

        var command = new Command("find-attribute-targets", "Find every entity decorated with a given attribute type.");
        command.Arguments.Add(attributeTypeFullName);
        command.Options.Add(mvidOrPath);
        command.Options.Add(targetKinds);
        command.Options.Add(maxHits);
        command.SetAction(pr => CliRun.Execute(context, pr, engine =>
            AssemblyOperations.FindAttributeTargets(engine.Index, pr.GetValue(attributeTypeFullName)!, pr.GetValue(mvidOrPath), CliRun.NullIfEmpty(pr.GetValue(targetKinds)), pr.GetValue(maxHits), CancellationToken.None)));
        return command;
    }

    private static Command BuildFindMemberReferences(CliContext context)
    {
        var memberHandle = new Argument<string>("member-handle") { Description = "Member handle: 'f:..' (field), 'p:..' (property), or 'e:..' (event)." };
        var accessor = new Option<string?>("--accessor") { Description = "Accessor / mode filter (see tool docs)." };
        var maxHits = new Option<int>("--max-hits") { Description = "Cap on returned hits (0 = default)." };

        var command = new Command("find-member-references", "Reverse member-access lookup for a field, property, or event.");
        command.Arguments.Add(memberHandle);
        command.Options.Add(accessor);
        command.Options.Add(maxHits);
        command.SetAction(pr => CliRun.Execute(context, pr, engine =>
            AssemblyOperations.FindMemberReferences(engine.Index, pr.GetValue(memberHandle)!, pr.GetValue(accessor), pr.GetValue(maxHits), CancellationToken.None)));
        return command;
    }

    private static Command BuildFindTypeReferences(CliContext context)
    {
        var handle = new Option<string?>("--handle") { Description = "Type handle 't:<mvid>:0x<token>'." };
        var mvidOrPath = new Option<string?>("--mvid-or-path") { Description = "Module MVID or path (with --type-full-name)." };
        var typeFullName = new Option<string?>("--type-full-name") { Description = "Full type name ('+'-joined for nested types)." };
        var assembly = new Option<string?>("--assembly") { Description = "Path hint used to load the module if not yet known." };

        var command = new Command("find-type-references", "Find every site that references a given type.");
        command.Options.Add(handle);
        command.Options.Add(mvidOrPath);
        command.Options.Add(typeFullName);
        command.Options.Add(assembly);
        command.SetAction(pr => CliRun.Execute(context, pr, engine =>
            AssemblyOperations.FindTypeReferences(engine.Index, pr.GetValue(handle), pr.GetValue(mvidOrPath), pr.GetValue(typeFullName), pr.GetValue(assembly), CancellationToken.None)));
        return command;
    }
}
