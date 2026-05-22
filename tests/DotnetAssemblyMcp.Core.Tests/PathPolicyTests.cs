using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.IO;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

public sealed class PathPolicyTests
{
    [Fact]
    public void RequireAbsolute_rejects_null_with_invalid_argument()
    {
        var err = PathPolicy.RequireAbsolute(null);
        err.Should().NotBeNull();
        err!.Kind.Should().Be(ErrorKinds.InvalidArgument);
    }

    [Fact]
    public void RequireAbsolute_rejects_empty_with_invalid_argument()
    {
        var err = PathPolicy.RequireAbsolute("   ");
        err.Should().NotBeNull();
        err!.Kind.Should().Be(ErrorKinds.InvalidArgument);
    }

    [Theory]
    [InlineData("foo.dll")]
    [InlineData("./foo.dll")]
    [InlineData("../foo.dll")]
    [InlineData("relative/sub/foo.dll")]
    public void RequireAbsolute_rejects_relative_paths(string path)
    {
        var err = PathPolicy.RequireAbsolute(path);
        err.Should().NotBeNull();
        err!.Kind.Should().Be(ErrorKinds.PathMustBeAbsolute);
        err.Message.Should().Contain("<file:");
    }

    [Fact]
    public void RequireAbsolute_accepts_unix_absolute_path()
    {
        if (OperatingSystem.IsWindows()) return;
        PathPolicy.RequireAbsolute("/tmp/foo.dll").Should().BeNull();
    }

    [Fact]
    public void RequireAbsolute_accepts_windows_absolute_path()
    {
        if (!OperatingSystem.IsWindows()) return;
        PathPolicy.RequireAbsolute(@"C:\tmp\foo.dll").Should().BeNull();
    }
}
