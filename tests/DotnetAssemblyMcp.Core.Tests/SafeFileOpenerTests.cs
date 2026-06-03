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

        var result = SafeFileOpener.ReadAllBytes(
            fileInB, maxBytes: 1024, expectedParentDirectory: PathPolicy.CanonicalizeRealPath(dirA));
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

        var result = SafeFileOpener.ReadAllBytes(
            sibling, maxBytes: 1024, expectedParentDirectory: PathPolicy.CanonicalizeRealPath(dir));
        result.IsSuccess.Should().BeTrue();
        result.Bytes.Should().Equal(0xCA, 0xFE);
    }

    [Fact]
    public void ReadAllBytes_returns_descriptor_real_path_for_load_anchor()
    {
        // OpenedRealPath is the load-time anchor sibling reads are later checked against.
        var path = Path.Combine(_tempDir, "anchor.bin");
        File.WriteAllBytes(path, [0x01]);

        var result = SafeFileOpener.ReadAllBytes(path, maxBytes: 1024);

        result.IsSuccess.Should().BeTrue();
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            result.OpenedRealPath.Should().Be(PathPolicy.CanonicalizeRealPath(path));
        }
    }

    [Fact]
    public void ReadAllBytes_rejects_sibling_whose_ancestor_is_swapped_for_an_out_of_tree_symlink()
    {
        if (OperatingSystem.IsWindows()) return; // directory symlink creation is privilege-gated

        // Models the residual sibling-PDB ancestor-TOCTOU with the allow-list DISABLED (no verifier):
        // 'anchor' is the assembly's real directory captured at load. The pdb is then read at the
        // lexical path anchor/foo.pdb, but 'anchor' has since been swapped for a symlink to 'evil'.
        // O_NOFOLLOW permits the ancestor traversal (it only guards the leaf), so the open succeeds
        // and the descriptor's real parent resolves to 'evil' — the load-anchored containment check
        // must reject it without needing the allow-list.
        var realDir = Path.Combine(_tempDir, "asm");
        Directory.CreateDirectory(realDir);
        var anchor = PathPolicy.CanonicalizeRealPath(realDir); // captured at "load" time

        var evil = Path.Combine(_tempDir, "evil");
        Directory.CreateDirectory(evil);
        File.WriteAllBytes(Path.Combine(evil, "foo.pdb"), [0xBA, 0xD0]);

        Directory.Delete(realDir);
        Directory.CreateSymbolicLink(realDir, evil);
        var lexicalSibling = Path.Combine(realDir, "foo.pdb"); // realDir -> evil

        var result = SafeFileOpener.ReadAllBytes(
            lexicalSibling, maxBytes: 1024, expectedParentDirectory: anchor);

        result.IsSuccess.Should().BeFalse("the descriptor's real parent is 'evil', not the load anchor");
        result.Error!.Kind.Should().Be(ErrorKinds.PathRejected);
        result.Error.Message.Should().Contain("out-of-tree");
    }

    [Fact]
    public void ReadAllBytes_sibling_check_fails_closed_when_descriptor_real_path_unresolvable()
    {
        var dir = Path.Combine(_tempDir, "asm");
        Directory.CreateDirectory(dir);
        var sibling = Path.Combine(dir, "foo.pdb");
        File.WriteAllBytes(sibling, [0x00]);

        var prev = SafeFileOpener.DescriptorRealPathResolverOverride;
        SafeFileOpener.DescriptorRealPathResolverOverride = _ => null;
        try
        {
            var result = SafeFileOpener.ReadAllBytes(
                sibling, maxBytes: 1024, expectedParentDirectory: PathPolicy.CanonicalizeRealPath(dir));
            result.IsSuccess.Should().BeFalse();
            result.Error!.Kind.Should().Be(ErrorKinds.PathRejected);
        }
        finally
        {
            SafeFileOpener.DescriptorRealPathResolverOverride = prev;
        }
    }
}
