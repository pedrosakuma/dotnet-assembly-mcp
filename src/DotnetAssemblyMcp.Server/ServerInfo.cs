using System.Reflection;

namespace DotnetAssemblyMcp.Server;

/// <summary>
/// Server build metadata exposed to MCP clients via <c>serverInfo</c>.
/// </summary>
/// <remarks>
/// The version is harvested from <see cref="AssemblyInformationalVersionAttribute"/>, which
/// MinVer populates at build time from the nearest git tag (e.g. <c>0.15.0</c> on a tagged
/// commit, or <c>0.15.1-alpha.0.3+abcdef</c> on commits past the tag — see issue #94).
/// The build-metadata segment (everything after <c>+</c>) is stripped before reporting so
/// the version stays a clean SemVer 2.0 core+pre-release string.
/// </remarks>
internal static class ServerInfo
{
    public static string Version { get; } = ResolveVersion();

    private static string ResolveVersion()
    {
        var raw = typeof(ServerInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(raw))
        {
            // Fall back to AssemblyVersion when no informational version is baked in
            // (e.g. unit tests that pull this type into a host without MinVer).
            return typeof(ServerInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        var plusIndex = raw.IndexOf('+');
        return plusIndex >= 0 ? raw[..plusIndex] : raw;
    }
}
