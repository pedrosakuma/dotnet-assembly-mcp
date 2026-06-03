namespace DotnetAssemblyMcp.Core.Tests.Fixtures;

/// <summary>
/// Locates the SampleLibV2 fixture (<c>tests/fixtures/SampleLibV2</c>) — a deliberately divergent
/// variant of a subset of SampleLib's public surface, in the same <c>SampleLib</c> namespace but a
/// distinct assembly name. It exercises the <c>diff-assemblies</c> changed-member / shape-change
/// paths. The fixture is built as part of the normal solution build (see the test csproj's
/// <c>BuildSampleLibV2Fixture</c> target), so a fresh binary is guaranteed at a stable path.
///
/// Returns <c>null</c> when the fixture is missing — callers should treat that as a hard test
/// failure rather than a soft skip, because the build target should always produce it.
/// </summary>
public static class SampleLibV2Fixture
{
    /// <summary>Absolute path to the built SampleLibV2 binary, or <c>null</c> if missing.</summary>
    public static string? Path { get; } = ResolvePath();

    private static string? ResolvePath()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && dir is not null; i++)
        {
            if (Directory.Exists(System.IO.Path.Combine(dir, ".git")))
            {
                var candidate = System.IO.Path.Combine(
                    dir, "tests", "fixtures", "SampleLibV2", "bin", Configuration, "net10.0", "SampleLibV2.dll");
                return File.Exists(candidate) ? candidate : null;
            }
            dir = System.IO.Path.GetDirectoryName(dir);
        }
        return null;
    }

    private static string Configuration =>
#if DEBUG
        "Debug";
#else
        "Release";
#endif
}
