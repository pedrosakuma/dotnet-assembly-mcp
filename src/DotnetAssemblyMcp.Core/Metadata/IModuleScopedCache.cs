namespace DotnetAssemblyMcp.Core.Metadata;

/// <summary>
/// Per-module cache contract: every component that keys data by <c>ModuleVersionId</c> and
/// must drop its entry when a module is reloaded implements this. <see cref="MetadataIndex"/>
/// keeps a list of subscribers and fans <see cref="Invalidate"/> out on
/// <see cref="ModuleStore.ModuleReloaded"/>, replacing the centralised hardcoded fan-out that
/// used to silently drift stale whenever a new cache was added (#82).
/// </summary>
internal interface IModuleScopedCache
{
    /// <summary>Drop every entry keyed by <paramref name="mvid"/>.</summary>
    void Invalidate(Guid mvid);
}
