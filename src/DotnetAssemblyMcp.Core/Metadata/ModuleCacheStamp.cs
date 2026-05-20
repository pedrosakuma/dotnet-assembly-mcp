using DotnetAssemblyMcp.Core.Handles;

namespace DotnetAssemblyMcp.Core.Metadata;

/// <summary>
/// Compact (length, last-write-time) fingerprint of the file backing a loaded module —
/// stamped on every in-memory cache entry built by an <see cref="IModuleScopedCache"/> so
/// a silent file change between explicit reloads is caught on the next lookup.
/// </summary>
/// <remarks>
/// Stat failures (file deleted, permission denied) collapse to <see cref="Unknown"/>. Two
/// unknown stamps compare equal — the cache still serves the entry. This matches the
/// pre-#82 behaviour where the explicit <c>InvalidateXref</c> reload path was the only
/// invalidation signal; the staleness check is additive.
/// </remarks>
internal readonly record struct ModuleCacheStamp(long Length, DateTime LastWriteUtc)
{
    public static ModuleCacheStamp Unknown { get; } = new(-1, default);

    public static ModuleCacheStamp TryCapture(ModuleHandle module)
    {
        try
        {
            var fi = new FileInfo(module.Path);
            if (!fi.Exists) return Unknown;
            return new ModuleCacheStamp(fi.Length, fi.LastWriteTimeUtc);
        }
        catch (IOException) { return Unknown; }
        catch (UnauthorizedAccessException) { return Unknown; }
    }
}
