using DotnetAssemblyMcp.Core.Handles;

namespace DotnetAssemblyMcp.Cli;

/// <summary>
/// Normalizes user-supplied path arguments to absolute paths before they reach the shared
/// Application / Core layers. Core enforces <c>PathPolicy.RequireAbsolute</c> as a security
/// invariant — the MCP server has no stable working directory, so a relative path there is
/// ambiguous. The CLI, by contrast, runs in the operator's real shell, so it resolves relative
/// paths (and a leading <c>~</c>) against the current directory here, keeping Core untouched.
/// </summary>
internal static class CliPaths
{
    /// <summary>
    /// Resolves an argument that is strictly a filesystem path (<c>--load</c>, <c>--assembly</c>,
    /// the <c>load</c> positional, an <c>import-manifest</c> entry path). Always absolutized — no
    /// GUID / handle passthrough — so a file that happens to be named like a GUID still works.
    /// </summary>
    public static string? ResolvePathOnly(string? value) =>
        string.IsNullOrWhiteSpace(value) ? value : ToAbsolute(value);

    /// <summary>
    /// Resolves an <c>mvid-or-path</c> argument: an MVID GUID or a structured handle
    /// (<c>m:</c> / <c>t:</c> / <c>a:</c> / …) is passed through unchanged; anything else is treated
    /// as a filesystem path and absolutized. Uses <see cref="HandleSyntax.TryParseAny"/> (not a bare
    /// prefix check) so a relative path that merely starts with <c>t:</c> is still absolutized.
    /// </summary>
    public static string? ResolveMvidOrPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        if (Guid.TryParse(value, out _))
        {
            return value;
        }

        if (HandleSyntax.TryParseAny(value, out _, out _, out _, out _))
        {
            return value;
        }

        return ToAbsolute(value);
    }

    private static string ToAbsolute(string value) => Path.GetFullPath(ExpandHome(value));

    private static string ExpandHome(string value)
    {
        if (value.Length == 0 || value[0] != '~')
        {
            return value;
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (value.Length == 1)
        {
            return home;
        }

        if (value[1] == '/' || value[1] == '\\')
        {
            return Path.Combine(home, value[2..]);
        }

        // '~user' form is intentionally not expanded — leave it for Path.GetFullPath to handle.
        return value;
    }
}
