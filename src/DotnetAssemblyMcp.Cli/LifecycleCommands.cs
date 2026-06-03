using System.CommandLine;
using System.Globalization;
using DotnetAssemblyMcp.Application;
using DotnetAssemblyMcp.Core.Metadata;

namespace DotnetAssemblyMcp.Cli;

/// <summary>Lifecycle subcommands: <c>import-manifest</c>.</summary>
/// <remarks>
/// The stateful MCP <c>load_assembly</c> / <c>list_assemblies</c> tools have no standalone meaning
/// in a one-shot CLI (each invocation builds and discards its own index), so they are intentionally
/// not exposed as subcommands. Prime the index with the global <c>--load &lt;path&gt;</c> option, or
/// pass a path directly to a path-taking command instead.
/// </remarks>
internal static class LifecycleCommands
{
    public static void Register(RootCommand root, CliContext context)
    {
        root.Subcommands.Add(BuildImportManifest(context));
    }

    private static Command BuildImportManifest(CliContext context)
    {
        var entryOption = new Option<string[]>("--entry")
        {
            Description = "Manifest entry in '<mvid-guid>=<path>' form. Repeat for multiple modules.",
            AllowMultipleArgumentsPerToken = false,
        };
        var modeOption = new Option<string>("--mode")
        {
            Description = "Import mode: 'lazy' (default; record mvid->path) or 'tier1' (open eagerly).",
            DefaultValueFactory = _ => "lazy",
        };

        var command = new Command("import-manifest", "Bulk-register (mvid, path) pairs observed in a running process.");
        command.Options.Add(entryOption);
        command.Options.Add(modeOption);
        command.SetAction(pr =>
        {
            if (!TryParseMode(pr.GetValue(modeOption), out ManifestImportMode mode))
            {
                Console.Error.WriteLine($"error: invalid --mode '{pr.GetValue(modeOption)}' (expected 'lazy' or 'tier1').");
                return 2;
            }

            if (!TryParseEntries(pr.GetValue(entryOption), out var entries))
            {
                return 2;
            }

            return CliRun.Execute(context, pr, engine =>
                AssemblyOperations.ImportAssemblyManifest(engine.Index, entries, mode));
        });
        return command;
    }

    private static bool TryParseMode(string? value, out ManifestImportMode mode)
    {
        mode = ManifestImportMode.Lazy;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        return Enum.TryParse(value, ignoreCase: true, out mode);
    }

    private static bool TryParseEntries(string[]? raw, out IReadOnlyList<ManifestEntry> entries)
    {
        var list = new List<ManifestEntry>();
        entries = list;
        if (raw is null)
        {
            return true;
        }

        foreach (var item in raw)
        {
            int separator = item.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0 || separator == item.Length - 1)
            {
                Console.Error.WriteLine($"error: invalid --entry '{item}' (expected '<mvid-guid>=<path>').");
                return false;
            }

            string mvidText = item[..separator];
            string path = item[(separator + 1)..];
            if (!Guid.TryParse(mvidText, CultureInfo.InvariantCulture, out Guid mvid))
            {
                Console.Error.WriteLine($"error: invalid mvid in --entry '{item}'.");
                return false;
            }

            list.Add(new ManifestEntry(mvid, CliPaths.ResolvePathOnly(path)!));
        }

        return true;
    }
}
