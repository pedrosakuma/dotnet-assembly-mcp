using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.IO;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Validates the size-cap + symlink-rejection guarantees of <see cref="SafeFileOpener"/>.
/// Closes #135 — the historical <c>File.ReadAllBytes</c> path had no defense against either.
/// </summary>
public sealed class SafeFileOpenerTests : IDisposable
{
    private readonly string _tempDir;

    public SafeFileOpenerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "asm-mcp-sfo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void ReadAllBytes_returns_contents_for_small_file()
    {
        var path = Path.Combine(_tempDir, "small.bin");
        File.WriteAllBytes(path, [0xDE, 0xAD, 0xBE, 0xEF]);
        var result = SafeFileOpener.ReadAllBytes(path, maxBytes: 1024);
        result.IsSuccess.Should().BeTrue();
        result.Bytes.Should().Equal(0xDE, 0xAD, 0xBE, 0xEF);
    }

    [Fact]
    public void ReadAllBytes_rejects_file_larger_than_cap_before_alloc()
    {
        var path = Path.Combine(_tempDir, "big.bin");
        File.WriteAllBytes(path, new byte[16 * 1024]);
        var result = SafeFileOpener.ReadAllBytes(path, maxBytes: 1024);
        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.PathRejected);
        result.Error.Message.Should().Contain("exceeds cap");
    }

    [Fact]
    public void ReadAllBytes_returns_module_load_failed_for_missing_file()
    {
        var result = SafeFileOpener.ReadAllBytes(Path.Combine(_tempDir, "nope.bin"), maxBytes: 1024);
        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.ModuleLoadFailed);
        result.Error.Message.Should().Contain("<file:nope.bin>");
    }

    [Fact]
    public void ReadAllBytes_rejects_directory_target()
    {
        // Directory is reported as "not a file" via File.Exists — surfaces as ModuleLoadFailed
        // (we don't synthesize a path_rejected, since this is indistinguishable from a missing file
        // and reading more state would be a TOCTOU window). Either rejection is fine — the
        // contract is "you do not get bytes back".
        var result = SafeFileOpener.ReadAllBytes(_tempDir, maxBytes: 1024);
        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().BeOneOf(ErrorKinds.PathRejected, ErrorKinds.ModuleLoadFailed);
    }

    [Fact]
    public void ReadAllBytes_rejects_symlink_on_unix()
    {
        if (OperatingSystem.IsWindows()) return; // CreateSymbolicLink needs SeCreateSymbolicLink on Windows
        var target = Path.Combine(_tempDir, "target.bin");
        File.WriteAllBytes(target, [0x01, 0x02]);
        var link = Path.Combine(_tempDir, "link.bin");
        File.CreateSymbolicLink(link, target);

        var result = SafeFileOpener.ReadAllBytes(link, maxBytes: 1024);
        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.PathRejected);
        result.Error.Message.Should().Contain("symlink");
    }

    [Fact]
    public void ReadAllBytes_rejects_out_of_tree_sibling()
    {
        // Build two siblings in different directories. The 'expected parent' is dirA, but
        // the file we try to read lives in dirB — exactly the shape of a sibling-PDB attack
        // where Path.ChangeExtension(asm, ".pdb") could resolve to a different directory
        // when the assembly path contains a traversal segment.
        var dirA = Path.Combine(_tempDir, "asm");
        var dirB = Path.Combine(_tempDir, "elsewhere");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        var fileInB = Path.Combine(dirB, "foo.pdb");
        File.WriteAllBytes(fileInB, [0x00]);

        var result = SafeFileOpener.ReadAllBytes(fileInB, maxBytes: 1024, expectedParentDirectory: dirA);
        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.PathRejected);
        result.Error.Message.Should().Contain("out-of-tree");
    }

    [Fact]
    public void ReadAllBytes_accepts_in_tree_sibling()
    {
        var dir = Path.Combine(_tempDir, "asm");
        Directory.CreateDirectory(dir);
        var sibling = Path.Combine(dir, "foo.pdb");
        File.WriteAllBytes(sibling, [0xCA, 0xFE]);

        var result = SafeFileOpener.ReadAllBytes(sibling, maxBytes: 1024, expectedParentDirectory: dir);
        result.IsSuccess.Should().BeTrue();
        result.Bytes.Should().Equal(0xCA, 0xFE);
    }
}
