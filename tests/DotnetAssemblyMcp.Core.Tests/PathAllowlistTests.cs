using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.IO;
using DotnetAssemblyMcp.Core.Metadata;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Enforcement of the untrusted-path-hint contract (#150): the opt-in allow-list of trusted load
/// roots. Covers the <see cref="PathPolicy"/> primitives and the end-to-end behaviour through
/// <see cref="MetadataIndex.Load"/> / <see cref="IMetadataIndex.Probe"/>, which are the public faces
/// of the single <c>ModuleStore.OpenModule</c> chokepoint.
/// </summary>
public sealed class PathAllowlistTests : IDisposable
{
    private static string SampleLibPath => typeof(SampleLib.OrderService).Assembly.Location;

    private readonly string _tempDir;

    public PathAllowlistTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "asm-mcp-allow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    // ---- PathPolicy.RequireWithinRoots unit semantics -------------------------------------------

    [Fact]
    public void RequireWithinRoots_null_roots_allows_everything()
    {
        PathPolicy.RequireWithinRoots("/any/where/foo.dll", allowedRoots: null).Should().BeNull();
    }

    [Fact]
    public void RequireWithinRoots_empty_roots_denies_everything()
    {
        var err = PathPolicy.RequireWithinRoots(SampleLibPath, allowedRoots: Array.Empty<string>());
        err.Should().NotBeNull();
        err!.Kind.Should().Be(ErrorKinds.PathNotAllowed);
    }

    [Fact]
    public void RequireWithinRoots_path_inside_root_is_allowed()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(SampleLibPath))!;
        PathPolicy.RequireWithinRoots(SampleLibPath, new[] { dir }).Should().BeNull();
    }

    [Fact]
    public void RequireWithinRoots_path_outside_root_is_denied()
    {
        var err = PathPolicy.RequireWithinRoots(SampleLibPath, new[] { _tempDir });
        err.Should().NotBeNull();
        err!.Kind.Should().Be(ErrorKinds.PathNotAllowed);
        // The candidate path is surfaced but redacted (no raw sensitive prefix).
        err.Message.Should().Contain("<file:");
    }

    [Fact]
    public void RequireWithinRoots_sibling_prefix_is_not_treated_as_contained()
    {
        // '/root/appfoo' must NOT be considered inside the root '/root/app'.
        var root = Path.Combine(_tempDir, "app");
        var sibling = Path.Combine(_tempDir, "appfoo", "x.dll");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.GetDirectoryName(sibling)!);
        File.WriteAllText(sibling, "x");

        var err = PathPolicy.RequireWithinRoots(sibling, new[] { root });
        err.Should().NotBeNull();
        err!.Kind.Should().Be(ErrorKinds.PathNotAllowed);
    }

    [Fact]
    public void CanonicalizeRealPath_normalises_dotdot_traversal()
    {
        var sub = Path.Combine(_tempDir, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(_tempDir, "leaf.dll"), "x");
        var traversal = Path.Combine(sub, "..", "leaf.dll");
        var real = PathPolicy.CanonicalizeRealPath(traversal);
        real.Should().Be(Path.Combine(_tempDir, "leaf.dll"));
    }

    // ---- End-to-end through MetadataIndex -------------------------------------------------------

    [Fact]
    public void Load_outside_allowed_root_is_rejected_with_path_not_allowed()
    {
        using var index = new MetadataIndex(watchForChanges: false, allowedRoots: new[] { _tempDir });
        var result = index.Load(SampleLibPath);
        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.PathNotAllowed);
    }

    [Fact]
    public void Load_inside_allowed_root_succeeds()
    {
        var copied = Path.Combine(_tempDir, Path.GetFileName(SampleLibPath));
        File.Copy(SampleLibPath, copied);

        using var index = new MetadataIndex(watchForChanges: false, allowedRoots: new[] { _tempDir });
        var result = index.Load(copied);
        result.IsSuccess.Should().BeTrue();
        result.Module!.ModuleVersionId.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void Load_with_null_roots_allows_arbitrary_absolute_path()
    {
        using var index = new MetadataIndex(watchForChanges: false, allowedRoots: null);
        index.Load(SampleLibPath).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Probe_outside_allowed_root_is_rejected_with_path_not_allowed()
    {
        using var index = new MetadataIndex(watchForChanges: false, allowedRoots: new[] { _tempDir });
        var probe = index.Probe(SampleLibPath);
        probe.IsSuccess.Should().BeFalse();
        probe.Error!.Kind.Should().Be(ErrorKinds.PathNotAllowed);
    }

    [Fact]
    public void IdentityFirst_already_loaded_mvid_bypasses_allowlist_on_path_hint()
    {
        var copied = Path.Combine(_tempDir, Path.GetFileName(SampleLibPath));
        File.Copy(SampleLibPath, copied);

        // Load while enforcement covers _tempDir so the MVID becomes known.
        using var index = new MetadataIndex(watchForChanges: false, allowedRoots: new[] { _tempDir });
        var load = index.Load(copied);
        load.IsSuccess.Should().BeTrue();
        var mvid = load.Module!.ModuleVersionId;

        // A subsequent identity-addressed call carrying an OUT-OF-ROOT path hint must resolve via the
        // already-loaded MVID and never touch the filesystem / allow-list — EnsureLoaded returns null.
        index.EnsureLoaded(mvid, assemblyPathHint: "/etc/definitely-out-of-root.dll").Should().BeNull();
    }

    [Fact]
    public void Configured_root_that_is_a_symlinked_directory_resolves_to_real_target()
    {
        if (OperatingSystem.IsWindows()) return; // symlink creation requires elevation on Windows

        // realRoot/<lib>, and a sibling symlink 'linkRoot' -> realRoot. A load via the link path
        // canonicalises to realRoot, which must be contained by a root configured as realRoot.
        var realRoot = Path.Combine(_tempDir, "real");
        Directory.CreateDirectory(realRoot);
        var copied = Path.Combine(realRoot, Path.GetFileName(SampleLibPath));
        File.Copy(SampleLibPath, copied);

        var linkRoot = Path.Combine(_tempDir, "link");
        Directory.CreateSymbolicLink(linkRoot, realRoot);
        var viaLink = Path.Combine(linkRoot, Path.GetFileName(SampleLibPath));

        using var index = new MetadataIndex(watchForChanges: false, allowedRoots: new[] { realRoot });
        var load = index.Load(viaLink);
        load.IsSuccess.Should().BeTrue();
        // Under enforcement the module is registered under its canonical real path, not the
        // symlinked path the caller supplied — so the opened path cannot diverge from what was
        // validated for containment.
        load.Module!.ModulePath.Should().Be(copied);
    }

    [Fact]
    public void Ancestor_symlink_escaping_the_root_is_denied()
    {
        if (OperatingSystem.IsWindows()) return;

        // outside/<lib> lives outside the allowed root. Inside the root we plant a directory symlink
        // 'escape' -> outside. Loading root/escape/<lib> must canonicalise to outside and be denied,
        // even though the lexical path is under the root.
        var outside = Path.Combine(_tempDir, "outside");
        Directory.CreateDirectory(outside);
        var copied = Path.Combine(outside, Path.GetFileName(SampleLibPath));
        File.Copy(SampleLibPath, copied);

        var root = Path.Combine(_tempDir, "root");
        Directory.CreateDirectory(root);
        var escape = Path.Combine(root, "escape");
        Directory.CreateSymbolicLink(escape, outside);
        var viaEscape = Path.Combine(escape, Path.GetFileName(SampleLibPath));

        using var index = new MetadataIndex(watchForChanges: false, allowedRoots: new[] { root });
        var result = index.Load(viaEscape);
        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.PathNotAllowed);
    }

    // ---- Post-open ancestor-directory TOCTOU verification (#156) --------------------------------

    [Fact]
    public void PostOpen_fd_realpath_escaping_root_via_ancestor_symlink_is_rejected()
    {
        if (OperatingSystem.IsWindows()) return; // directory symlink creation is privilege-gated

        // outside/<lib> is a REAL file outside the root. Inside the root, 'link' is a directory
        // symlink -> outside. Opening root/link/<lib> succeeds (O_NOFOLLOW only guards the LEAF, not
        // the 'link' ancestor), so this models the post-canonicalisation ancestor swap: the leaf is
        // real, the ancestor redirects out of the root. The post-open fd-real-path check must reject.
        var outside = Path.Combine(_tempDir, "outside");
        Directory.CreateDirectory(outside);
        var realFile = Path.Combine(outside, Path.GetFileName(SampleLibPath));
        File.Copy(SampleLibPath, realFile);

        var root = Path.Combine(_tempDir, "root");
        Directory.CreateDirectory(root);
        var link = Path.Combine(root, "link");
        Directory.CreateSymbolicLink(link, outside);
        var viaAncestor = Path.Combine(link, Path.GetFileName(SampleLibPath));

        var canonicalRoots = new[] { PathPolicy.CanonicalizeRealPath(root)! };
        var result = SafeFileOpener.ReadAllBytes(
            viaAncestor, SafeFileOpener.DefaultMaxAssemblyBytes,
            verifyOpenedRealPath: rp => PathPolicy.RequireRealPathWithinRoots(rp, canonicalRoots));

        result.IsSuccess.Should().BeFalse("the opened descriptor's real path resolves outside the root");
        result.Error!.Kind.Should().Be(ErrorKinds.PathNotAllowed);
    }

    [Fact]
    public void PostOpen_fd_realpath_inside_root_passes_verification()
    {
        var copied = Path.Combine(_tempDir, Path.GetFileName(SampleLibPath));
        File.Copy(SampleLibPath, copied);

        var canonicalRoots = new[] { PathPolicy.CanonicalizeRealPath(_tempDir)! };
        var result = SafeFileOpener.ReadAllBytes(
            copied, SafeFileOpener.DefaultMaxAssemblyBytes,
            verifyOpenedRealPath: rp => PathPolicy.RequireRealPathWithinRoots(rp, canonicalRoots));

        result.IsSuccess.Should().BeTrue();
        result.Bytes.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void PostOpen_unresolvable_fd_realpath_fails_closed()
    {
        var copied = Path.Combine(_tempDir, Path.GetFileName(SampleLibPath));
        File.Copy(SampleLibPath, copied);

        // Simulate a platform/configuration where the descriptor's real path cannot be resolved
        // (e.g. /proc not mounted): enforcement must deny rather than read an unverifiable file.
        var prev = SafeFileOpener.DescriptorRealPathResolverOverride;
        SafeFileOpener.DescriptorRealPathResolverOverride = _ => null;
        try
        {
            var result = SafeFileOpener.ReadAllBytes(
                copied, SafeFileOpener.DefaultMaxAssemblyBytes,
                verifyOpenedRealPath: _ => null);
            result.IsSuccess.Should().BeFalse();
            result.Error!.Kind.Should().Be(ErrorKinds.PathRejected);
        }
        finally
        {
            SafeFileOpener.DescriptorRealPathResolverOverride = prev;
        }
    }

    [Fact]
    public void PostOpen_verification_uses_containment_not_exact_equality()
    {
        // The fd real path differs from the opened path but is still inside an allowed root — the
        // contract is containment, not byte-equality, so this must succeed (guards against an
        // over-strict equality check that the rubber-duck review flagged as false-positive prone).
        var copied = Path.Combine(_tempDir, Path.GetFileName(SampleLibPath));
        File.Copy(SampleLibPath, copied);
        var inRootButDifferent = Path.Combine(_tempDir, "sibling.dll");

        var canonicalRoots = new[] { PathPolicy.CanonicalizeRealPath(_tempDir)! };
        var prev = SafeFileOpener.DescriptorRealPathResolverOverride;
        SafeFileOpener.DescriptorRealPathResolverOverride = _ => inRootButDifferent;
        try
        {
            var result = SafeFileOpener.ReadAllBytes(
                copied, SafeFileOpener.DefaultMaxAssemblyBytes,
                verifyOpenedRealPath: rp => PathPolicy.RequireRealPathWithinRoots(rp, canonicalRoots));
            result.IsSuccess.Should().BeTrue();
        }
        finally
        {
            SafeFileOpener.DescriptorRealPathResolverOverride = prev;
        }
    }

    [Fact]
    public void MetadataIndex_exposes_canonical_allowed_roots_for_reopen_paths()
    {
        using var index = new MetadataIndex(watchForChanges: false, allowedRoots: new[] { _tempDir });
        index.AllowedRoots.Should().NotBeNull();
        index.AllowedRoots!.Should().ContainSingle()
            .Which.Should().Be(PathPolicy.CanonicalizeRealPath(_tempDir));
    }

    [Fact]
    public void MetadataIndex_exposes_null_allowed_roots_when_enforcement_disabled()
    {
        using var index = new MetadataIndex();
        index.AllowedRoots.Should().BeNull();
    }

    [Fact]
    public void Reopen_for_disassembly_re_checks_post_open_fd_realpath_after_ancestor_swap()
    {
        if (OperatingSystem.IsWindows()) return; // directory symlink creation is privilege-gated

        // The Tier-2+ reopen paths (IL / decompile) must apply the same post-open fd verification
        // as the initial load (#156). Load succeeds while the ancestor dir is real; then the ancestor
        // is swapped for an out-of-root symlink. The first IL open reopens the stored canonical path,
        // O_NOFOLLOW traverses the swapped ancestor, and the fd-real-path check must reject the read.
        var root = Path.Combine(_tempDir, "root");
        var realDir = Path.Combine(root, "sub");
        Directory.CreateDirectory(realDir);
        var libName = Path.GetFileName(SampleLibPath);
        var loadedPath = Path.Combine(realDir, libName);
        File.Copy(SampleLibPath, loadedPath);

        using var index = new MetadataIndex(
            watchForChanges: false,
            allowedRoots: new[] { PathPolicy.CanonicalizeRealPath(root)! });
        var load = index.Load(loadedPath);
        load.IsSuccess.Should().BeTrue("the module is inside the allowed root at load time");

        using var disasm = new DotnetAssemblyMcp.Core.Decompilation.IlDisassembler(index);

        // Swap the 'sub' ancestor for a symlink pointing outside the root, keeping the leaf real.
        var outside = Path.Combine(_tempDir, "outside");
        Directory.CreateDirectory(outside);
        File.Copy(SampleLibPath, Path.Combine(outside, libName));
        Directory.Delete(realDir, recursive: true);
        Directory.CreateSymbolicLink(realDir, outside);

        var mi = typeof(SampleLib.Dog).GetMethod(
            "Speak", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!;
        var result = disasm.Disassemble(
            new DotnetAssemblyMcp.Core.Identity.MethodIdentity(mi.Module.ModuleVersionId, mi.MetadataToken));

        result.IsSuccess.Should().BeFalse("the reopened descriptor resolves outside the allowed root");
    }

    // ---- Host wiring: the allow-list must reach the engine the hosts actually build -------------

    [Fact]
    public void AssemblyEngineFactory_threads_allowedRoots_into_the_index()
    {
        // Regression guard (#150 / PR #153 review): both the MCP Server and the CLI construct their
        // engine through AssemblyEngineFactory.Create. If Create stops forwarding allowedRoots the
        // enforcement becomes dead code and every load is silently permitted. A factory built with a
        // single unrelated root must therefore deny a load from outside that root.
        var engine = DotnetAssemblyMcp.Application.AssemblyEngineFactory.Create(
            watchForChanges: false, allowedRoots: new[] { _tempDir });
        try
        {
            var result = engine.Index.Load(SampleLibPath);
            result.IsSuccess.Should().BeFalse();
            result.Error!.Kind.Should().Be(ErrorKinds.PathNotAllowed);
        }
        finally
        {
            (engine.Index as IDisposable)?.Dispose();
        }
    }
}
