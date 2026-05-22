using DotnetAssemblyMcp.Core.Errors;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Validates the path-redaction helper that scrubs absolute paths out of client-facing
/// error text. Mirrors the contract pinned in the v0.18.1 security audit (#141).
/// </summary>
public sealed class ErrorRedactorTests
{
    [Theory]
    [InlineData("/var/lib/secret/build.dll", "<file:build.dll>")]
    [InlineData("/home/user/.cache/asm/foo.exe", "<file:foo.exe>")]
    [InlineData("relative/path.dll", "<file:path.dll>")]
    public void RedactPath_keeps_only_basename(string input, string expected)
    {
        ErrorRedactor.RedactPath(input).Should().Be(expected);
    }

    [Fact]
    public void RedactPath_handles_null_and_empty()
    {
        ErrorRedactor.RedactPath(null).Should().Be("<file:?>");
        ErrorRedactor.RedactPath(string.Empty).Should().Be("<file:?>");
        ErrorRedactor.RedactPath("/").Should().Be("<file:?>");
    }

    [Fact]
    public void Redact_scrubs_unix_paths_embedded_in_message()
    {
        var msg = "file not found: /home/alice/build/SampleLib.dll";
        ErrorRedactor.Redact(msg).Should().Be("file not found: <file:SampleLib.dll>");
    }

    [Fact]
    public void Redact_scrubs_windows_paths_embedded_in_message()
    {
        var msg = @"could not find C:\Users\bob\AppData\Local\Temp\foo.dll right now";
        ErrorRedactor.Redact(msg).Should().Contain("<file:foo.dll>").And.NotContain("bob");
    }

    [Fact]
    public void Redact_scrubs_multiple_paths_in_same_message()
    {
        var msg = "could not load /a/b/x.dll because /tmp/y.exe was missing";
        var redacted = ErrorRedactor.Redact(msg);
        redacted.Should().Contain("<file:x.dll>").And.Contain("<file:y.exe>");
        redacted.Should().NotContain("/a/b/").And.NotContain("/tmp/");
    }

    [Fact]
    public void Redact_returns_empty_for_null_or_empty()
    {
        ErrorRedactor.Redact(null).Should().BeEmpty();
        ErrorRedactor.Redact(string.Empty).Should().BeEmpty();
    }

    [Fact]
    public void Redact_does_not_mangle_text_without_paths()
    {
        ErrorRedactor.Redact("invalid argument: name is required.")
            .Should().Be("invalid argument: name is required.");
    }

    [Fact]
    public void Redact_handles_quoted_unix_paths_with_spaces()
    {
        var msg = "Could not find file '/home/alice/My Project/foo.dll'.";
        var redacted = ErrorRedactor.Redact(msg);
        redacted.Should().Contain("<file:foo.dll>");
        redacted.Should().NotContain("alice").And.NotContain("My Project");
    }

    [Fact]
    public void Redact_handles_quoted_windows_paths_with_spaces()
    {
        var msg = @"Could not find file 'C:\Users\Alice Smith\AppData\Local\Temp\foo.dll'.";
        var redacted = ErrorRedactor.Redact(msg);
        redacted.Should().Contain("<file:foo.dll>");
        redacted.Should().NotContain("Alice").And.NotContain("AppData");
    }

    [Fact]
    public void Redact_keeps_non_path_quoted_strings_intact()
    {
        var msg = "expected 'true' but got 'false'";
        ErrorRedactor.Redact(msg).Should().Be(msg);
    }

    [Fact]
    public void Sanitize_redacts_both_message_and_detail_and_preserves_kind()
    {
        var err = new AssemblyError("module_load_failed",
            "could not open /opt/app/x.dll",
            "System.IO.FileNotFoundException: Could not find file '/opt/app/x.dll'.");
        var sanitized = ErrorRedactor.Sanitize(err);
        sanitized.Kind.Should().Be("module_load_failed");
        sanitized.Message.Should().NotContain("/opt").And.Contain("<file:x.dll>");
        sanitized.Detail.Should().NotContain("/opt").And.Contain("<file:x.dll>");
    }
}
