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
}
