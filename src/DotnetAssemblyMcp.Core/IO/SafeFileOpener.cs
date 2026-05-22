using System.Runtime.InteropServices;
using DotnetAssemblyMcp.Core.Errors;
using Microsoft.Win32.SafeHandles;

namespace DotnetAssemblyMcp.Core.IO;

/// <summary>
/// Bounded, single-handle reader for filesystem inputs. Defends the static-analysis surface
/// against two classes of attack flagged in the v0.18.1 security audit:
/// <list type="bullet">
///   <item><description>OOM-via-huge-file: <see cref="File.ReadAllBytes"/> allocates the
///     entire file before any validation. A caller pointing <c>load_assembly</c> at a 50 GiB
///     sparse file (or a video, or <c>/dev/zero</c> on Linux) trivially crashes the host.</description></item>
///   <item><description>TOCTOU + symlink escape: the historical
///     <c>File.Exists</c> + <c>File.ReadAllBytes</c> sequence dereferences symlinks and
///     races the caller's filesystem mutations. A sibling-PDB lookup that does
///     <c>Path.ChangeExtension(".pdb")</c> followed by an unbounded read can be tricked into
///     reading any file the host process can see.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// All public methods open the file once with <see cref="FileShare.Read"/> + <see cref="FileMode.Open"/>
/// + <see cref="FileAccess.Read"/>, then validate against:
/// <list type="number">
///   <item><description><see cref="FileAttributes.ReparsePoint"/> — rejects Unix symlinks /
///     NTFS junctions / Windows mount points before any byte is read.</description></item>
///   <item><description><see cref="FileStream.Length"/> against the supplied cap, before the
///     allocation.</description></item>
/// </list>
/// Failure modes are surfaced as <see cref="AssemblyError"/> records using the new
/// <see cref="ErrorKinds.PathRejected"/> kind so clients can distinguish a security rejection
/// from a generic IO failure.
/// </remarks>
public static class SafeFileOpener
{
    /// <summary>Default cap for managed PE inputs (64 MiB). Larger than any real assembly we've shipped against.</summary>
    public const long DefaultMaxAssemblyBytes = 64L * 1024 * 1024;

    /// <summary>Default cap for portable PDB inputs (64 MiB). Embedded sources can push past 32 MiB on monorepos.</summary>
    public const long DefaultMaxPdbBytes = 64L * 1024 * 1024;

    /// <summary>Reads <paramref name="path"/> in full as a byte buffer, enforcing the size cap and rejecting symlinks.</summary>
    /// <param name="path">Absolute path to the file. Caller is expected to have validated <see cref="Path.IsPathFullyQualified(string)"/>.</param>
    /// <param name="maxBytes">Hard cap on the file size in bytes. Reads above this throw before allocation.</param>
    /// <param name="expectedParentDirectory">
    /// Optional canonical directory that the resolved real path must reside in. Used by the
    /// sibling-PDB lookup to prevent an attacker-controlled directory layout from escaping
    /// the assembly's directory via a symlink to e.g. <c>/etc/shadow.pdb</c>.
    /// </param>
    /// <returns>The raw bytes on success; an <see cref="AssemblyError"/> on rejection.</returns>
    public static ReadResult ReadAllBytes(string path, long maxBytes, string? expectedParentDirectory = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        // Reject reparse points BEFORE opening the file: on Linux opening a symlink to
        // /dev/zero would still succeed and the post-open Attributes check would not catch it.
        // FileInfo.Attributes reflects the link metadata when the path itself is a symlink.
        try
        {
            var pre = new FileInfo(path);
            if (pre.Exists && (pre.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return ReadResult.Fail(new AssemblyError(
                    ErrorKinds.PathRejected,
                    $"refusing to read symlink / reparse point: {ErrorRedactor.RedactPath(path)}"));
            }
            if (pre.Exists && (pre.Attributes & FileAttributes.Directory) != 0)
            {
                return ReadResult.Fail(new AssemblyError(
                    ErrorKinds.PathRejected,
                    $"refusing to read directory: {ErrorRedactor.RedactPath(path)}"));
            }
        }
        catch (IOException ex)
        {
            return ReadResult.Fail(new AssemblyError(
                ErrorKinds.ModuleLoadFailed, "i/o error stat'ing file.", ErrorRedactor.Redact(ex.Message)));
        }
        catch (UnauthorizedAccessException ex)
        {
            return ReadResult.Fail(new AssemblyError(
                ErrorKinds.ModuleLoadFailed, "permission denied.", ErrorRedactor.Redact(ex.Message)));
        }

        FileStream fs;
        try
        {
            fs = OpenNoFollow(path);
        }
        catch (PathRejectedException pre2)
        {
            return ReadResult.Fail(new AssemblyError(
                ErrorKinds.PathRejected,
                $"refusing to read symlink / reparse point: {ErrorRedactor.RedactPath(path)}",
                ErrorRedactor.Redact(pre2.Message)));
        }
        catch (FileNotFoundException ex)
        {
            return ReadResult.Fail(new AssemblyError(
                ErrorKinds.ModuleLoadFailed, $"file not found: {ErrorRedactor.RedactPath(path)}",
                ErrorRedactor.Redact(ex.Message)));
        }
        catch (DirectoryNotFoundException ex)
        {
            return ReadResult.Fail(new AssemblyError(
                ErrorKinds.ModuleLoadFailed, $"directory not found: {ErrorRedactor.RedactPath(path)}",
                ErrorRedactor.Redact(ex.Message)));
        }
        catch (UnauthorizedAccessException ex)
        {
            return ReadResult.Fail(new AssemblyError(
                ErrorKinds.ModuleLoadFailed, "permission denied.", ErrorRedactor.Redact(ex.Message)));
        }
        catch (IOException ex)
        {
            return ReadResult.Fail(new AssemblyError(
                ErrorKinds.ModuleLoadFailed, "i/o error opening file.", ErrorRedactor.Redact(ex.Message)));
        }

        using (fs)
        {
            // Sibling-PDB / containment check. Verifies the OS-canonical parent of the open
            // file is the directory the caller expected — defeats a 'pdb is a symlink that
            // escapes the assembly directory' attack.
            if (expectedParentDirectory is not null)
            {
                var actualParent = Path.GetDirectoryName(Path.GetFullPath(path));
                var expectedFull = Path.GetFullPath(expectedParentDirectory);
                var comp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (!string.Equals(
                        actualParent?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        expectedFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        comp))
                {
                    return ReadResult.Fail(new AssemblyError(
                        ErrorKinds.PathRejected,
                        $"refusing to read out-of-tree file: {ErrorRedactor.RedactPath(path)}"));
                }
            }

            long length;
            try { length = fs.Length; }
            catch (NotSupportedException ex)
            {
                return ReadResult.Fail(new AssemblyError(
                    ErrorKinds.PathRejected, "refusing to read non-seekable stream.",
                    ErrorRedactor.Redact(ex.Message)));
            }
            if (length > maxBytes)
            {
                return ReadResult.Fail(new AssemblyError(
                    ErrorKinds.PathRejected,
                    $"file size {length} bytes exceeds cap of {maxBytes} bytes: {ErrorRedactor.RedactPath(path)}"));
            }

            var buffer = new byte[length];
            var read = 0;
            try
            {
                while (read < buffer.Length)
                {
                    var n = fs.Read(buffer, read, buffer.Length - read);
                    if (n == 0) break;
                    read += n;
                }
            }
            catch (IOException ex)
            {
                return ReadResult.Fail(new AssemblyError(
                    ErrorKinds.ModuleLoadFailed, "i/o error reading file.", ErrorRedactor.Redact(ex.Message)));
            }
            if (read != buffer.Length)
            {
                return ReadResult.Fail(new AssemblyError(
                    ErrorKinds.ModuleLoadFailed,
                    $"short read: expected {buffer.Length} bytes, got {read}: {ErrorRedactor.RedactPath(path)}"));
            }
            return ReadResult.Ok(buffer);
        }
    }

    /// <summary>Result envelope mirroring the shape of <c>ProbeResult</c> / <c>LoadResult</c>.</summary>
    public readonly record struct ReadResult(bool IsSuccess, byte[]? Bytes, AssemblyError? Error)
    {
        public static ReadResult Ok(byte[] bytes) => new(true, bytes, null);
        public static ReadResult Fail(AssemblyError error) => new(false, null, error);
    }

    /// <summary>
    /// Reads <paramref name="path"/> through the same TOCTOU-safe pipeline as
    /// <see cref="ReadAllBytes(string,long,string?)"/> but returns the result as a
    /// non-writable <see cref="MemoryStream"/> ready to hand off to libraries (e.g.
    /// ICSharpCode.Decompiler's <c>PEFile</c>) that insist on a <see cref="Stream"/> input.
    /// </summary>
    /// <remarks>
    /// Returning a <see cref="MemoryStream"/> instead of the live <see cref="FileStream"/>
    /// is intentional: it severs the consumer from the on-disk handle so the open file
    /// descriptor is released the moment <see cref="ReadAllBytes"/> returns, matching the
    /// memory profile of <c>ModuleStore</c>. Callers retain the bytes only for as
    /// long as they hold the returned stream.
    /// </remarks>
    public static StreamResult OpenReadAsStream(string path, long maxBytes, string? expectedParentDirectory = null)
    {
        var read = ReadAllBytes(path, maxBytes, expectedParentDirectory);
        if (!read.IsSuccess) return StreamResult.Fail(read.Error!);
        return StreamResult.Ok(new MemoryStream(read.Bytes!, writable: false));
    }

    /// <summary>Result envelope for <see cref="OpenReadAsStream(string,long,string?)"/>.</summary>
    public readonly record struct StreamResult(bool IsSuccess, MemoryStream? Stream, AssemblyError? Error)
    {
        public static StreamResult Ok(MemoryStream stream) => new(true, stream, null);
        public static StreamResult Fail(AssemblyError error) => new(false, null, error);
    }

    /// <summary>
    /// Opens <paramref name="path"/> for reading with kernel-level no-follow semantics and
    /// hands the open <see cref="FileStream"/> back to the caller. Use this when the caller
    /// needs to keep the file descriptor open across additional fd-based syscalls
    /// (e.g. <c>fstat</c>-based permission checks) — the absence of a path-based recheck is
    /// what closes the TOCTOU window between <c>stat</c> and <c>open</c>.
    /// </summary>
    /// <remarks>
    /// Caller owns the returned <see cref="FileStream"/> and must dispose it. No size cap is
    /// enforced here — these helpers are designed for fixed-format cache files where the
    /// caller decodes a header before deciding how much to read.
    /// </remarks>
    public static OpenResult OpenReadNoFollow(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        try
        {
            return OpenResult.Ok(OpenNoFollow(path));
        }
        catch (PathRejectedException pre)
        {
            return OpenResult.Fail(new AssemblyError(
                ErrorKinds.PathRejected,
                $"refusing to read symlink / reparse point: {ErrorRedactor.RedactPath(path)}",
                ErrorRedactor.Redact(pre.Message)));
        }
        catch (FileNotFoundException ex)
        {
            return OpenResult.Fail(new AssemblyError(
                ErrorKinds.ModuleLoadFailed, $"file not found: {ErrorRedactor.RedactPath(path)}",
                ErrorRedactor.Redact(ex.Message)));
        }
        catch (DirectoryNotFoundException ex)
        {
            return OpenResult.Fail(new AssemblyError(
                ErrorKinds.ModuleLoadFailed, $"directory not found: {ErrorRedactor.RedactPath(path)}",
                ErrorRedactor.Redact(ex.Message)));
        }
        catch (UnauthorizedAccessException ex)
        {
            return OpenResult.Fail(new AssemblyError(
                ErrorKinds.ModuleLoadFailed, "permission denied.", ErrorRedactor.Redact(ex.Message)));
        }
        catch (IOException ex)
        {
            return OpenResult.Fail(new AssemblyError(
                ErrorKinds.ModuleLoadFailed, "i/o error opening file.", ErrorRedactor.Redact(ex.Message)));
        }
    }

    /// <summary>Result envelope for <see cref="OpenReadNoFollow(string)"/>.</summary>
    public readonly record struct OpenResult(bool IsSuccess, FileStream? Stream, AssemblyError? Error)
    {
        public static OpenResult Ok(FileStream stream) => new(true, stream, null);
        public static OpenResult Fail(AssemblyError error) => new(false, null, error);
    }

    // ---- TOCTOU-safe open ---------------------------------------------------
    //
    // Opens the file using kernel-level no-follow semantics so a symlink that races into
    // place between the pre-open stat and FileStream construction cannot trick us into
    // reading an out-of-tree target.
    //
    //   * Unix (Linux/macOS): P/Invoke open(2) with O_RDONLY | O_NOFOLLOW; if the path's
    //     leaf component is a symlink the kernel itself fails the call with ELOOP (40
    //     on Linux, 62 on macOS) — no FileStream object is ever built. We probe both
    //     values for the O_NOFOLLOW flag so the same binary works on both platforms.
    //   * Windows: reparse-point creation requires SeCreateSymbolicLinkPrivilege which
    //     is admin-only by default, so the race window is dramatically narrower. We open
    //     normally then immediately re-stat via FileInfo.LinkTarget; a non-null target
    //     after open means a junction/symlink slipped in and we throw.

    private sealed class PathRejectedException(string message) : Exception(message);

    private static FileStream OpenNoFollow(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            // Fail closed on unknown Unix platforms: if we don't have a verified O_NOFOLLOW
            // value for this OS we will not open the file at all, rather than guess a value
            // that might mean something else on that kernel and silently bypass the defense.
            if (NoFollowFlag is null)
            {
                throw new PathRejectedException(
                    "refusing file IO on unsupported Unix platform (no verified O_NOFOLLOW value).");
            }

            // O_NOFOLLOW's numeric value is platform-specific (Linux: 0x20000, macOS/*BSD: 0x0100).
            // We pin to the running OS at static init — retrying on failure would defeat the
            // defense, because a real ELOOP on Linux is indistinguishable from an EINVAL caused
            // by passing the BSD flag value, and a retry without the no-follow bit would happily
            // follow the symlink the kernel just rejected.
            var handle = TryOpen(path, O_RDONLY | NoFollowFlag.Value);
            if (handle is null || handle.IsInvalid)
            {
                handle?.Dispose();
                var errno = Marshal.GetLastPInvokeError();
                throw errno switch
                {
                    ELOOP_LINUX or ELOOP_BSD => new PathRejectedException("kernel rejected symlink (ELOOP)."),
                    ENOENT => new FileNotFoundException(),
                    EACCES => new UnauthorizedAccessException(),
                    EISDIR => new UnauthorizedAccessException("target is a directory."),
                    _ => new IOException($"open() failed: errno={errno}"),
                };
            }
            return new FileStream(handle, FileAccess.Read);
        }

        // Windows path: standard open + post-open reparse-point recheck.
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, options: FileOptions.SequentialScan);
        try
        {
            var post = new FileInfo(path);
            if (post.LinkTarget is not null || (post.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                fs.Dispose();
                throw new PathRejectedException("post-open reparse-point check failed.");
            }
        }
        catch
        {
            fs.Dispose();
            throw;
        }
        return fs;
    }

    /// <summary>
    /// Platform-pinned <c>O_NOFOLLOW</c> value. <c>null</c> on Unix platforms we have not
    /// verified — callers must treat that as a fail-closed signal and refuse to open. Pinned
    /// once at type init; never derived from a probe-and-retry pattern, because a real
    /// <c>ELOOP</c> on the leaf component would be indistinguishable from "wrong flag value"
    /// and a retry would defeat the defense.
    /// </summary>
    private static readonly int? NoFollowFlag =
        OperatingSystem.IsLinux() ? O_NOFOLLOW_LINUX :
        OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD() ? O_NOFOLLOW_BSD :
        null;

    private static SafeFileHandle? TryOpen(string path, int flags)
    {
        var raw = NativeOpen(path, flags, 0);
        if (raw == -1) return null;
        return new SafeFileHandle((IntPtr)raw, ownsHandle: true);
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int NativeOpen(string pathname, int flags, int mode);

    private const int O_RDONLY = 0;
    private const int O_NOFOLLOW_LINUX = 0x20000;
    private const int O_NOFOLLOW_BSD = 0x0100;
    private const int ELOOP_LINUX = 40;
    private const int ELOOP_BSD = 62;
    private const int ENOENT = 2;
    private const int EACCES = 13;
    private const int EISDIR = 21;
}
