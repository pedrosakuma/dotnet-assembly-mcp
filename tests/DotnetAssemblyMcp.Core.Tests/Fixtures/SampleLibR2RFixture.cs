namespace DotnetAssemblyMcp.Core.Tests.Fixtures;

/// <summary>
/// Locates the deterministic R2R-published fixture (<c>tests/fixtures/SampleLibR2R</c>).
/// The fixture is rebuilt as part of the normal solution build (see the test csproj's
/// <c>BuildSampleLibR2R</c> target), so any machine that runs the test suite is
/// guaranteed to find a fresh, host-SDK-built R2R binary at a stable path.
///
/// Returns <c>null</c> when the fixture is missing — callers should treat that as a
/// hard test failure rather than a soft skip, because the build target should always
/// produce it (the old SPCorLib-probing skip pattern was the bug we're closing).
/// </summary>
public static class SampleLibR2RFixture
{
    /// <summary>Absolute path to the published R2R SampleLib binary, or <c>null</c> if missing.</summary>
    public static string? Path { get; } = ResolvePath();

    private static string? ResolvePath()
    {
        // Walk up from the test bin directory to the repo root, then into the fixture's publish dir.
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && dir is not null; i++)
        {
            if (Directory.Exists(System.IO.Path.Combine(dir, ".git")))
            {
                var candidate = System.IO.Path.Combine(
                    dir, "tests", "fixtures", "SampleLibR2R", "publish", "SampleLibR2R.dll");
                return File.Exists(candidate) ? candidate : null;
            }
            dir = System.IO.Path.GetDirectoryName(dir);
        }
        return null;
    }
}
