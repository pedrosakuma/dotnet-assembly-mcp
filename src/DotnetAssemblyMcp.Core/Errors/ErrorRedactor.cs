using System.Text.RegularExpressions;

namespace DotnetAssemblyMcp.Core.Errors;

/// <summary>
/// Scrubs filesystem paths out of client-facing error text. The MCP wire format leaks
/// <see cref="AssemblyError.Message"/> and <see cref="AssemblyError.Detail"/> verbatim to
/// whichever LLM/agent issued the call; full filesystem paths give an attacker who has
/// landed prompt-injection on that agent a free directory map of the host. Path basenames
/// stay useful for debugging without revealing parent directories or usernames.
/// </summary>
/// <remarks>
/// Scope is intentionally narrow: replace absolute Unix paths (<c>/a/b/c.dll</c>) and
/// Windows paths (<c>C:\a\b\c.dll</c>, <c>\\server\share\file</c>) with their basename
/// wrapped in <c>&lt;file:NAME&gt;</c>. Relative segments are left alone — they don't
/// disclose host structure. Exception messages from the BCL frequently embed full paths
/// (<c>FileNotFoundException</c>, <c>UnauthorizedAccessException</c>, <c>IOException</c>),
/// so callers should run <see cref="Redact"/> over <c>ex.Message</c> before stashing it
/// into <see cref="AssemblyError.Detail"/>.
/// </remarks>
public static partial class ErrorRedactor
{
    /// <summary>
    /// Returns just the filename portion of <paramref name="path"/>, wrapped as
    /// <c>&lt;file:NAME&gt;</c>. Use at every site that interpolates a user-supplied
    /// path into client-facing error text.
    /// </summary>
    public static string RedactPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "<file:?>";
        // Cross-platform basename extraction. Path.GetFileName only treats the host OS's
        // separators as splitters, so on Linux it would return the entire Windows path
        // verbatim. Split on both '/' and '\' to keep redaction independent of host OS.
        var trimmed = path.TrimEnd('/', '\\');
        var lastSlash = trimmed.LastIndexOfAny(['/', '\\']);
        var name = lastSlash < 0 ? trimmed : trimmed[(lastSlash + 1)..];
        return string.IsNullOrEmpty(name) ? "<file:?>" : $"<file:{name}>";
    }

    /// <summary>
    /// Replaces absolute filesystem paths embedded in <paramref name="message"/> with
    /// <c>&lt;file:NAME&gt;</c> placeholders. Safe to call on arbitrary text including
    /// exception messages.
    /// </summary>
    public static string Redact(string? message)
    {
        if (string.IsNullOrEmpty(message)) return string.Empty;

        // First pass: paths the BCL wraps in single or double quotes
        // (FileNotFoundException, IOException, UnauthorizedAccessException all do this).
        // Matching on the whole quoted span handles paths that contain spaces — the
        // unquoted regexes below stop at whitespace and would otherwise leak the tail.
        var redacted = QuotedPath().Replace(message, m =>
        {
            var inner = m.Groups[1].Value;
            return ContainsPathSeparator(inner) ? RedactPath(inner) : m.Value;
        });

        redacted = UnixPath().Replace(redacted, m => RedactPath(m.Value));
        redacted = WindowsPath().Replace(redacted, m => RedactPath(m.Value));
        redacted = UncPath().Replace(redacted, m => RedactPath(m.Value));
        return redacted;
    }

    private static bool ContainsPathSeparator(string s)
    {
        for (var i = 0; i < s.Length; i++)
            if (s[i] == '/' || s[i] == '\\') return true;
        return false;
    }

    /// <summary>
    /// Returns a copy of <paramref name="error"/> with path strings scrubbed from
    /// <see cref="AssemblyError.Message"/> and <see cref="AssemblyError.Detail"/>.
    /// </summary>
    public static AssemblyError Sanitize(AssemblyError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new AssemblyError(error.Kind, Redact(error.Message), Redact(error.Detail));
    }

    // Unix absolute path: leading '/', then at least one segment of non-whitespace/quote/bracket chars,
    // optionally with further segments. We deliberately require at least two characters after the
    // leading slash so we don't mangle root references like '/'.
    [GeneratedRegex(@"(?<![\w/])/(?:[^\s'""<>():;,]+/)*[^\s'""<>():;,]+", RegexOptions.CultureInvariant)]
    private static partial Regex UnixPath();

    // Windows drive-letter path: 'C:\', then any number of non-quote/bracket/whitespace chars.
    [GeneratedRegex(@"[A-Za-z]:[\\/](?:[^\s'""<>():;,]+[\\/])*[^\s'""<>():;,]+", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPath();

    // UNC path: '\\server\share\...'.
    [GeneratedRegex(@"\\\\[^\s'""<>():;,]+\\[^\s'""<>():;,]+(?:\\[^\s'""<>():;,]+)*", RegexOptions.CultureInvariant)]
    private static partial Regex UncPath();

    // Anything single- or double-quoted; the callback only redacts when the captured span
    // contains a path separator so we don't molest quoted human-readable strings.
    [GeneratedRegex(@"['""]([^'""\r\n]+)['""]", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedPath();
}
