using DotnetAssemblyMcp.Core.Errors;

namespace DotnetAssemblyMcp.Core.IO;

/// <summary>
/// Absolute-path enforcement for tool-supplied filesystem inputs. The tool descriptions
/// document that every <c>path</c> / <c>assemblyPathHint</c> parameter MUST be absolute,
/// but the historical implementation silently canonicalized relative paths via
/// <see cref="Path.GetFullPath(string)"/> against the server's working directory. In
/// HTTP / container deployments that CWD is unrelated to the operator's intent, so a
/// relative path is almost certainly a client bug and at worst a path-traversal vector.
/// </summary>
/// <remarks>
/// Centralised here (rather than in every tool entry point) so a single update can change
/// the policy. Returns the offending <see cref="AssemblyError"/> or <c>null</c> on success.
/// </remarks>
public static class PathPolicy
{
    /// <summary>
    /// Returns <c>null</c> when <paramref name="path"/> is a non-empty, fully-qualified
    /// absolute path. Otherwise returns an <see cref="AssemblyError"/> describing the
    /// reason for rejection.
    /// </summary>
    public static AssemblyError? RequireAbsolute(string? path, string parameterName = "path")
    {
        if (string.IsNullOrWhiteSpace(path))
            return new AssemblyError(ErrorKinds.InvalidArgument, $"{parameterName} is required.");
        if (!Path.IsPathFullyQualified(path))
            return new AssemblyError(
                ErrorKinds.PathMustBeAbsolute,
                $"{parameterName} must be an absolute path; received {ErrorRedactor.RedactPath(path)}.");
        return null;
    }

    /// <summary>
    /// Opt-in allow-listing of load paths against operator-configured trusted roots, per the
    /// untrusted-path-hint contract (issue #150). Path-shaped tool arguments arrive off an
    /// LLM-driven handoff wire and must not be able to read arbitrary files. Semantics of
    /// <paramref name="allowedRoots"/>:
    /// <list type="bullet">
    /// <item><c>null</c> — enforcement disabled (no operator config). Returns <c>null</c> (allow).
    /// The existing absolute-path, symlink/reparse, size-cap and MVID defenses still apply.</item>
    /// <item>non-null — enforcement active. An <em>empty</em> list denies everything: the operator
    /// configured roots but none survived canonicalisation, so we fail closed rather than silently
    /// revert to allow-all.</item>
    /// </list>
    /// The candidate is canonicalised to its real on-disk path (ancestor symlinks resolved) before
    /// the containment check; a candidate that cannot be canonicalised while enforcement is active
    /// is rejected. The rejected path is surfaced via <see cref="ErrorRedactor.RedactPath"/> so a
    /// sensitive prefix is not leaked.
    /// </summary>
    public static AssemblyError? RequireWithinRoots(
        string fullPath, IReadOnlyList<string>? allowedRoots, string parameterName = "path")
        => ResolveWithinRoots(fullPath, allowedRoots, parameterName).Error;

    /// <summary>
    /// Allow-list variant of <see cref="RequireWithinRoots"/> that also returns the path the caller
    /// should actually open. When enforcement is active the returned <c>Canonical</c> is the
    /// fully symlink-resolved real path that passed containment — callers MUST open <em>that</em>
    /// path (not the original), so an ancestor symlink retargeted between the check and the open
    /// cannot redirect the read outside an allowed root. When enforcement is disabled
    /// (<paramref name="allowedRoots"/> is <c>null</c>) the original <paramref name="fullPath"/> is
    /// returned verbatim and the open path is unchanged (back-compatible).
    /// </summary>
    public static (string? Canonical, AssemblyError? Error) ResolveWithinRoots(
        string fullPath, IReadOnlyList<string>? allowedRoots, string parameterName = "path")
    {
        if (allowedRoots is null) return (fullPath, null); // enforcement disabled (opt-in)

        var canonical = CanonicalizeRealPath(fullPath);
        if (canonical is null) return (null, Denied(fullPath, parameterName));

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        foreach (var root in allowedRoots)
        {
            if (IsWithin(canonical, root, comparison)) return (canonical, null);
        }
        return (null, Denied(fullPath, parameterName));
    }

    private static AssemblyError Denied(string fullPath, string parameterName) =>
        new(ErrorKinds.PathNotAllowed,
            $"{parameterName} is outside the configured allowed roots: {ErrorRedactor.RedactPath(fullPath)}.");

    private static bool IsWithin(string canonicalCandidate, string canonicalRoot, StringComparison comparison)
    {
        var candidate = canonicalCandidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = canonicalRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (root.Length == 0) return false;
        if (string.Equals(candidate, root, comparison)) return true;
        return candidate.Length > root.Length
            && candidate.StartsWith(root, comparison)
            && (candidate[root.Length] == Path.DirectorySeparatorChar
                || candidate[root.Length] == Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// Best-effort resolution of <paramref name="path"/> to its real on-disk location: normalises
    /// <c>.</c>/<c>..</c> via <see cref="Path.GetFullPath(string)"/>, then resolves <em>every</em>
    /// path component that is a symbolic link / reparse point — ancestors included — so a directory
    /// symlink nested inside an allowed root cannot smuggle a target outside it. The walk restarts
    /// from the root after each substitution so chains of nested links are followed, and is bounded
    /// to defeat symlink loops. Returns <c>null</c> when resolution fails (missing component, cycle,
    /// or I/O error) so allow-list callers can fail closed.
    /// </summary>
    public static string? CanonicalizeRealPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var current = Path.GetFullPath(path);
            for (var hop = 0; hop < MaxLinkHops; hop++)
            {
                if (!TryResolveFirstLink(current, out var resolved))
                    return current; // no further links — fully real
                current = resolved!;
            }
            return null; // exceeded hop budget — probable symlink loop, fail closed
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException
            or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private const int MaxLinkHops = 256;

    /// <summary>
    /// Walks the components of <paramref name="fullPath"/> from the filesystem root and, at the
    /// first component that is a reparse point, substitutes a single hop of its link target
    /// (keeping the unresolved tail) into <paramref name="resolved"/>. Returns <c>false</c> when no
    /// component is a link (the path is already real).
    /// </summary>
    private static bool TryResolveFirstLink(string fullPath, out string? resolved)
    {
        resolved = null;
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        var remainder = fullPath.Substring(root.Length);
        var segments = remainder.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        var built = root;
        for (var i = 0; i < segments.Length; i++)
        {
            built = Path.Combine(built, segments[i]);
            FileSystemInfo info = Directory.Exists(built)
                ? new DirectoryInfo(built)
                : new FileInfo(built);
            var target = info.ResolveLinkTarget(returnFinalTarget: false);
            if (target is null) continue;

            var combined = target.FullName;
            for (var t = i + 1; t < segments.Length; t++)
                combined = Path.Combine(combined, segments[t]);
            resolved = Path.GetFullPath(combined);
            return true;
        }
        return false;
    }
}
