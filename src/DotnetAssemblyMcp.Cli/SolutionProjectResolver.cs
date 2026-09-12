using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DotnetAssemblyMcp.Cli;

/// <summary>
/// Resolves a <c>.sln</c>/<c>.slnx</c> solution file into the set of glob patterns that locate
/// each referenced project's build output, so <see cref="CliLoadResolver"/> can expand them the
/// same way it expands any other glob (directory walk + name/size dedup).
/// </summary>
/// <remarks>
/// This deliberately does not invoke MSBuild — the project graph is read directly from the
/// solution/project XML, matching the "never Assembly.Load, never execute the target" posture
/// of the rest of the tool. Because a <c>.csproj</c> alone doesn't reliably tell you the final
/// output path (TargetFramework is frequently set only in a repo-wide Directory.Build.props, as
/// in this very repo), the resolver doesn't try to compute one analytic path: it derives the
/// assembly's expected file name (AssemblyName, defaulting to the project file's base name) and
/// globs for it under the project's own <c>bin/</c> tree — optionally anchored to a
/// <c>--configuration</c> — reusing the same glob-expansion/dedup pipeline as every other
/// <c>--load</c> value.
/// </remarks>
internal static class SolutionProjectResolver
{
    private static readonly Regex ClassicSlnProjectLine = new(
        """^Project\("\{[0-9A-Fa-f-]+\}"\)\s*=\s*"[^"]*"\s*,\s*"(?<path>[^"]+)"\s*,\s*"\{[0-9A-Fa-f-]+\}"\s*$""",
        RegexOptions.Multiline);

    /// <summary>
    /// Returns one glob pattern per project referenced by <paramref name="solutionPath"/>, each
    /// matching that project's expected build-output assembly under its own <c>bin/</c> tree.
    /// </summary>
    public static IEnumerable<string> ResolveOutputGlobs(string solutionPath, string? configuration, TextWriter warnings)
    {
        IReadOnlyList<string> projectPaths = Path.GetExtension(solutionPath).Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            ? ParseSlnx(solutionPath)
            : ParseClassicSln(solutionPath);

        foreach (var projectPath in projectPaths)
        {
            if (!File.Exists(projectPath))
            {
                warnings.WriteLine($"warning: --load '{solutionPath}': referenced project '{projectPath}' not found, skipping.");
                continue;
            }

            string assemblyName = GetAssemblyName(projectPath);
            string? projectDir = Path.GetDirectoryName(projectPath);
            if (string.IsNullOrEmpty(projectDir))
            {
                continue;
            }

            string configSegment = string.IsNullOrWhiteSpace(configuration) ? "**" : configuration;
            yield return Path.Combine(projectDir, "bin", configSegment, "**", assemblyName + ".dll");
            yield return Path.Combine(projectDir, "bin", configSegment, "**", assemblyName + ".exe");
        }
    }

    private static List<string> ParseClassicSln(string slnPath)
    {
        string text = File.ReadAllText(slnPath);
        string? baseDir = Path.GetDirectoryName(Path.GetFullPath(slnPath));
        var result = new List<string>();
        foreach (Match match in ClassicSlnProjectLine.Matches(text))
        {
            string relative = match.Groups["path"].Value.Replace('\\', Path.DirectorySeparatorChar);
            if (!IsProjectFile(relative))
            {
                continue; // solution folders and other non-project entries share the same grammar.
            }
            result.Add(baseDir is null ? relative : Path.GetFullPath(Path.Combine(baseDir, relative)));
        }
        return result;
    }

    private static List<string> ParseSlnx(string slnxPath)
    {
        string? baseDir = Path.GetDirectoryName(Path.GetFullPath(slnxPath));
        var doc = XDocument.Load(slnxPath);
        var result = new List<string>();
        foreach (var project in doc.Descendants().Where(e => e.Name.LocalName == "Project"))
        {
            string? relative = project.Attribute("Path")?.Value;
            if (string.IsNullOrWhiteSpace(relative) || !IsProjectFile(relative))
            {
                continue;
            }
            string normalized = relative.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            result.Add(baseDir is null ? normalized : Path.GetFullPath(Path.Combine(baseDir, normalized)));
        }
        return result;
    }

    private static bool IsProjectFile(string path) =>
        path.EndsWith("proj", StringComparison.OrdinalIgnoreCase) && Path.HasExtension(path);

    private static string GetAssemblyName(string projectPath)
    {
        try
        {
            var doc = XDocument.Load(projectPath);
            string? assemblyName = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "AssemblyName")?.Value;
            if (!string.IsNullOrWhiteSpace(assemblyName))
            {
                return assemblyName.Trim();
            }
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            // Fall through to the file-name default — a malformed/unreadable csproj shouldn't
            // abort the whole solution resolution.
        }

        return Path.GetFileNameWithoutExtension(projectPath);
    }
}
