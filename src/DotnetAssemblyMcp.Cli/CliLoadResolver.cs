using System.Text;
using System.Text.RegularExpressions;

namespace DotnetAssemblyMcp.Cli;

/// <summary>
/// Expands the raw <c>--load</c> values supplied on the command line into a flat, deduplicated
/// list of file paths before they reach <see cref="DotnetAssemblyMcp.Application.AssemblyOperations.LoadAssembly"/>.
/// </summary>
/// <remarks>
/// Real .NET repos routinely produce far more file *paths* than distinct assemblies: MSBuild
/// copies every transitive dependency into each project's own <c>bin/</c> output, so a naive
/// <c>--load</c> list (or a glob over <c>bin/</c>) can contain dozens of byte-identical copies of
/// the same DLL. Loading each copy independently is pure overhead — every copy pays the full
/// PE-open + metadata-parse cost even though only the first occurrence is ever kept by the
/// underlying module store. This resolver removes that overhead up front:
/// <list type="number">
/// <item>Each raw value is expanded: a glob pattern (containing <c>*</c>, <c>?</c> or <c>[</c>)
/// is matched against disk, an existing directory is walked recursively for <c>*.dll</c>/<c>*.exe</c>
/// files, and anything else is treated as a literal path (unchanged — including paths that don't
/// exist, so the existing per-path load error/warning still surfaces downstream).</item>
/// <item>The flattened list is deduplicated by (file name, file length) — a cheap, read-free
/// heuristic that reliably collapses MSBuild's redundant same-content copies without opening a
/// single PE. Skips are reported to the caller-supplied writer so the operator can see what was
/// dropped and why (see #173).</item>
/// </list>
/// </remarks>
internal static class CliLoadResolver
{
    /// <summary>Glob/wildcard metacharacters that mark a raw <c>--load</c> value as a pattern rather than a literal path.</summary>
    private static readonly char[] GlobChars = ['*', '?', '['];

    public static IReadOnlyList<string> Resolve(IEnumerable<string> rawValues, TextWriter warnings)
    {
        var expanded = new List<(string Raw, string Path)>();
        foreach (var raw in rawValues)
        {
            foreach (var path in ExpandOne(raw))
            {
                expanded.Add((raw, path));
            }
        }

        return Dedupe(expanded, warnings);
    }

    private static IEnumerable<string> ExpandOne(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            yield return raw;
            yield break;
        }

        if (raw.IndexOfAny(GlobChars) >= 0)
        {
            foreach (var match in ExpandGlob(raw))
            {
                yield return match;
            }
            yield break;
        }

        string resolved = CliPaths.ResolvePathOnly(raw)!;
        if (Directory.Exists(resolved))
        {
            foreach (var file in EnumerateAssemblies(resolved))
            {
                yield return file;
            }
            yield break;
        }

        // Literal file path (possibly non-existent) — pass through unchanged so the existing
        // per-path Load error surfaces exactly as before.
        yield return resolved;
    }

    private static IEnumerable<string> EnumerateAssemblies(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> ExpandGlob(string rawPattern)
    {
        // Path.GetFullPath collapses "**" segments (it doesn't understand glob syntax), so the
        // root/pattern split must happen against the *original*, unresolved value — only the
        // literal root portion (before the first wildcard) is safe to absolutize.
        string rawNormalized = rawPattern.Replace('\\', '/');
        int firstWildcard = rawNormalized.IndexOfAny(GlobChars);
        string rawRoot = firstWildcard < 0 ? rawNormalized : rawNormalized[..firstWildcard];
        int lastSlashInRawRoot = rawRoot.LastIndexOf('/');
        string rootPortion = lastSlashInRawRoot >= 0 ? rawNormalized[..lastSlashInRawRoot] : ".";
        string patternPortion = lastSlashInRawRoot >= 0 ? rawNormalized[(lastSlashInRawRoot + 1)..] : rawNormalized;

        string root = Path.GetFullPath(string.IsNullOrEmpty(rootPortion) ? "/" : rootPortion);
        if (!Directory.Exists(root))
        {
            yield break;
        }

        var regex = GlobToRegex(patternPortion);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (regex.IsMatch(relative))
            {
                yield return file;
            }
        }
    }

    private static Regex GlobToRegex(string glob)
    {
        var sb = new StringBuilder("^");
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                sb.Append(".*");
                i++;
                if (i + 1 < glob.Length && glob[i + 1] == '/')
                {
                    i++;
                }
            }
            else if (c == '*')
            {
                sb.Append("[^/]*");
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }

        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase);
    }

    private static List<string> Dedupe(List<(string Raw, string Path)> expanded, TextWriter warnings)
    {
        var seen = new Dictionary<(string Name, long Length), string>();
        var result = new List<string>(expanded.Count);
        foreach (var (raw, path) in expanded)
        {
            if (!File.Exists(path))
            {
                // Let the existing load path surface the "file not found" error verbatim.
                result.Add(path);
                continue;
            }

            var key = (Name: Path.GetFileName(path).ToLowerInvariant(), Length: new FileInfo(path).Length);
            if (seen.TryGetValue(key, out var firstPath))
            {
                if (!string.Equals(firstPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    warnings.WriteLine(
                        $"warning: --load '{raw}': skipping '{path}' — duplicate of '{firstPath}' (same file name and size).");
                }
                continue;
            }

            seen[key] = path;
            result.Add(path);
        }

        return result;
    }
}
