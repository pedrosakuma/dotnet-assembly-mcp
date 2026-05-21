using System.Reflection;
using DotnetAssemblyMcp.Core.Metadata;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Issue #82: every <see cref="IModuleScopedCache"/> subscriber must drop its per-MVID
/// entry when <see cref="MetadataIndex"/> observes a module reload. These tests use
/// reflection to:
///   (1) assert the subscriber list contains at least the six known caches
///       (XrefIndex, StringIndex, AttributeIndex, FieldAccessIndex, R2R adapter, PDB adapter),
///   (2) populate each extracted index's cache against the SampleLib fixture, then
///       invoke <c>Invalidate</c> on every subscriber and assert <c>HasCacheEntry</c>
///       returns false on the four extracted classes.
/// The actual file-watch path is already covered by <see cref="MetadataIndexWatcherTests"/>;
/// this test pins the contract that new caches must implement to be reload-safe.
/// </summary>
public sealed class ModuleScopedCacheContractTests
{
    private static string SampleLibPath => typeof(SampleLib.OrderService).Assembly.Location;

    [Fact]
    public void Subscriber_list_contains_all_six_known_caches()
    {
        using var index = new MetadataIndex();
        var subscribers = GetSubscribers(index);

        subscribers.Should().HaveCountGreaterThanOrEqualTo(7,
            "XrefIndex, StringIndex, AttributeIndex, FieldAccessIndex, R2RCacheAdapter, PdbCacheAdapter, TypeNavigationIndex are mandatory");

        var typeNames = subscribers
            .Select(c =>
            {
                var t = c.GetType();
                // Include generic arg names so ModuleScopedCache<R2RReaderBox> surfaces "R2RReaderBox".
                return t.IsGenericType
                    ? $"{t.Name}<{string.Join(",", t.GetGenericArguments().Select(a => a.Name))}>"
                    : t.Name;
            })
            .ToList();
        typeNames.Should().Contain(new[]
        {
            nameof(XrefIndex),
            nameof(StringIndex),
            nameof(AttributeIndex),
            nameof(FieldAccessIndex),
            nameof(TypeNavigationIndex),
        });
        typeNames.Should().Contain(n => n.Contains("R2R", StringComparison.Ordinal),
            "the R2R cache (now a ModuleScopedCache<R2RReaderBox>) is identified by its generic argument's name");
        typeNames.Should().Contain(n => n.Contains("Pdb", StringComparison.Ordinal));
    }

    [Fact]
    public void Invalidate_clears_every_extracted_index_for_the_mvid()
    {
        using var index = new MetadataIndex();
        var load = index.Load(SampleLibPath);
        load.IsSuccess.Should().BeTrue();
        var mvid = load.Module!.ModuleVersionId;

        var stringIdx = GetField<StringIndex>(index, "_stringIndex");
        var attrIdx = GetField<AttributeIndex>(index, "_attributeIndex");
        var fieldIdx = GetField<FieldAccessIndex>(index, "_fieldAccessIndex");
        var xrefIdx = GetField<XrefIndex>(index, "_xrefIndex");
        var typeNavIdx = GetField<TypeNavigationIndex>(index, "_typeNavigation");

        // Populate every extracted cache by issuing one query that the index handles internally.
        index.FindStringReferences("does-not-exist-xyz", StringMatchMode.Contains, mvid);
        index.FindAttributeTargets("System.SerializableAttribute", mvid);
        // FindFieldReferences builds the field-access index even when the field token is unknown.
        index.FindFieldReferences(mvid, fieldMetadataToken: 0x04000001);
        // FindTypeReferences forces the xref index to build for the module.
        // TypeDef table row 1 is always the <Module> pseudo-type; works for triggering the build.
        index.FindTypeReferences(mvid, typeMetadataToken: 0x02000002);
        // FindTypeByFullName + ListDerivedTypes populate the type-navigation caches.
        index.FindTypeByFullName(mvid, "SampleLib.OrderService");
        index.ListDerivedTypes(mvid, baseTypeMetadataToken: 0x02000002, new ListDerivedTypesQuery());

        stringIdx.HasCacheEntry(mvid).Should().BeTrue();
        attrIdx.HasCacheEntry(mvid).Should().BeTrue();
        fieldIdx.HasCacheEntry(mvid).Should().BeTrue();
        xrefIdx.HasCacheEntry(mvid).Should().BeTrue();
        typeNavIdx.HasNameCacheEntry(mvid).Should().BeTrue();
        typeNavIdx.HasParentMapsEntry.Should().BeTrue();

        // Fan out invalidation via the public API (was private reflection before #127).
        index.Invalidate(mvid);

        xrefIdx.HasCacheEntry(mvid).Should().BeFalse();
        stringIdx.HasCacheEntry(mvid).Should().BeFalse();
        attrIdx.HasCacheEntry(mvid).Should().BeFalse();
        fieldIdx.HasCacheEntry(mvid).Should().BeFalse();
        typeNavIdx.HasNameCacheEntry(mvid).Should().BeFalse();
        typeNavIdx.HasParentMapsEntry.Should().BeFalse();
    }

    [Fact]
    public void Invalidate_is_idempotent_and_silent_on_unknown_mvids()
    {
        // Public API (#127): pushes the staleness signal explicitly for producers (e.g.
        // dotnet-diagnostics-mcp over the handoff contract). Must be a no-op when nothing
        // was cached and harmless when called repeatedly.
        using var index = new MetadataIndex();

        var unknown = Guid.NewGuid();
        Action invalidateUnknown = () => index.Invalidate(unknown);
        invalidateUnknown.Should().NotThrow("unknown MVIDs are silently dropped");

        var load = index.Load(SampleLibPath);
        load.IsSuccess.Should().BeTrue();
        var mvid = load.Module!.ModuleVersionId;
        index.FindStringReferences("noop", StringMatchMode.Contains, mvid);

        var stringIdx = GetField<StringIndex>(index, "_stringIndex");
        stringIdx.HasCacheEntry(mvid).Should().BeTrue();

        index.Invalidate(mvid);
        stringIdx.HasCacheEntry(mvid).Should().BeFalse();

        // Idempotent — second call against an already-cleared MVID is a no-op.
        Action invalidateAgain = () => index.Invalidate(mvid);
        invalidateAgain.Should().NotThrow();
        stringIdx.HasCacheEntry(mvid).Should().BeFalse();
    }

    [Fact]
    public void Invalidate_raises_ModuleReloaded_so_downstream_caches_drop_stale_entries()
    {
        // Decompiler and IlDisassembler subscribe ONLY to ModuleReloaded for their own cache
        // invalidation (see Decompiler.OnModuleReloaded). Explicit Invalidate must therefore
        // raise the event with (OldMvid == NewMvid == mvid, Error == null) so those
        // singletons don't keep serving stale results after an out-of-band reload signal
        // pushed through this API. When the path is reachable Invalidate goes through
        // ModuleStore.Load, which swaps the ModuleHandle and raises the event naturally.
        using var index = new MetadataIndex();
        var load = index.Load(SampleLibPath);
        load.IsSuccess.Should().BeTrue();
        var mvid = load.Module!.ModuleVersionId;

        var observed = new List<ModuleReloadedEventArgs>();
        index.ModuleReloaded += (_, e) => observed.Add(e);

        index.Invalidate(mvid);

        observed.Should().HaveCount(1, "Invalidate contract: exactly one ModuleReloaded per call");
        observed.Should().Contain(e =>
            e.OldMvid == mvid && e.NewMvid == mvid && e.Error == null && e.Path == load.Module!.ModulePath,
            "the synthetic same-MVID reload event carries the resolved on-disk path");
    }

    [Fact]
    public void Invalidate_falls_back_to_cache_only_invalidation_when_path_is_unknown()
    {
        // When the MVID is unknown (never loaded, no path hint) the synthetic reload is the
        // only option — there is no PE on disk we can re-read. Subscribers still see the
        // event with an empty Path so any stale state they happened to hold can be dropped.
        using var index = new MetadataIndex();
        var unknown = Guid.NewGuid();

        ModuleReloadedEventArgs? observed = null;
        index.ModuleReloaded += (_, e) => observed = e;

        index.Invalidate(unknown);

        observed.Should().NotBeNull();
        observed!.OldMvid.Should().Be(unknown);
        observed.NewMvid.Should().Be(unknown);
        observed.Error.Should().BeNull();
        observed.Path.Should().BeEmpty("no path is known for an MVID that was never loaded or hinted");
    }

    [Fact]
    public void Invalidate_swaps_the_ModuleStore_handle_so_caches_rebuild_from_fresh_PE()
    {
        // Regression test for the "synthetic event but stale handle" hazard flagged in the
        // gpt-5.5 review of #127: after Invalidate, the next cache rebuild MUST run against
        // a fresh ModuleHandle (re-read from disk) — NOT against the same handle that
        // populated the now-evicted entry. Verified by comparing handle reference identity
        // before/after.
        using var index = new MetadataIndex();
        var load = index.Load(SampleLibPath);
        load.IsSuccess.Should().BeTrue();
        var mvid = load.Module!.ModuleVersionId;

        var handleField = typeof(ModuleStore).GetField("_modules", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var storeField = typeof(MetadataIndex).GetField("_store", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var store = storeField.GetValue(index)!;
        var modules = (System.Collections.IDictionary)handleField.GetValue(store)!;
        var before = modules[mvid];
        before.Should().NotBeNull();

        index.Invalidate(mvid);

        var after = modules[mvid];
        after.Should().NotBeNull("the module must still be loaded after Invalidate");
        ReferenceEquals(before, after).Should().BeFalse(
            "Invalidate goes through ModuleStore.Load which atomically swaps the ModuleHandle; " +
            "the next cache rebuild must see the FRESH handle");
    }

    [Fact]
    public void Invalidate_evicts_old_mvid_when_on_disk_rebuild_changes_the_mvid()
    {
        // Regression test for the gpt-5.5 v3 finding: when the file at `path` has been
        // rebuilt with a different MVID since the producer's last observation, Invalidate
        // must remove the old MVID entry from ModuleStore (not just leave it sitting next
        // to the new MVID) AND raise ModuleReloaded(oldMvid, newMvid) so downstream caches
        // keyed on the old MVID can drop their state.
        var tempDir = Path.Combine(Path.GetTempPath(), "amcp-invalidate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var copyPath = Path.Combine(tempDir, "SampleLib.dll");
            File.Copy(SampleLibPath, copyPath, overwrite: true);

            using var index = new MetadataIndex();
            var load = index.Load(copyPath);
            load.IsSuccess.Should().BeTrue();
            var oldMvid = load.Module!.ModuleVersionId;

            // Overwrite the on-disk file with content whose MVID differs (the original PE,
            // before being patched, has a different MVID than the round-tripped copy. We
            // forge a new MVID by patching a few bytes of the MVID guid in the metadata
            // root — simpler: write a SECOND assembly with a different MVID over the path.)
            // Use SampleConsumer.dll which is a separate fixture with its own MVID.
            var otherFixture = Path.Combine(
                Path.GetDirectoryName(typeof(SampleLib.OrderService).Assembly.Location)!,
                "SampleConsumer.dll");
            if (!File.Exists(otherFixture))
            {
                // Skip-soft: fixture not built, can't drive the different-MVID branch.
                return;
            }
            File.Copy(otherFixture, copyPath, overwrite: true);

            var observed = new List<ModuleReloadedEventArgs>();
            index.ModuleReloaded += (_, e) => observed.Add(e);

            index.Invalidate(oldMvid);

            // The old MVID entry must be gone from ModuleStore.
            var storeField = typeof(MetadataIndex).GetField("_store", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var modulesField = typeof(ModuleStore).GetField("_modules", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var modules = (System.Collections.IDictionary)modulesField.GetValue(storeField.GetValue(index)!)!;
            modules.Contains(oldMvid).Should().BeFalse("old MVID must be evicted when the on-disk MVID has drifted");

            // Event must carry (oldMvid, newMvid) with newMvid != oldMvid.
            observed.Should().Contain(e => e.OldMvid == oldMvid && e.NewMvid != null && e.NewMvid != oldMvid,
                "downstream subscribers need both keys to clear oldMvid state and prime newMvid state");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void Invalidate_raises_error_event_when_path_disappears_between_lookup_and_reload()
    {
        // Regression test for the gpt-5.5 v3 Medium finding: if the file vanishes after we
        // resolved its path (TOCTOU), Invalidate must still raise ModuleReloaded — this time
        // with Error populated — so subscribers know they should drop stale state.
        var tempDir = Path.Combine(Path.GetTempPath(), "amcp-invalidate-toctou-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var copyPath = Path.Combine(tempDir, "SampleLib.dll");
            File.Copy(SampleLibPath, copyPath, overwrite: true);

            using var index = new MetadataIndex();
            var load = index.Load(copyPath);
            load.IsSuccess.Should().BeTrue();
            var mvid = load.Module!.ModuleVersionId;

            // Prime an extracted cache via the index's own public surface, then yank the
            // file out from under it. The cache survives because it's held in memory; the
            // explicit Invalidate must drop it even though _store.Reload will fail on the
            // now-deleted file.
            index.FindStringReferences("does-not-exist-xyz", StringMatchMode.Contains, mvid);
            var stringIdx = GetField<StringIndex>(index, "_stringIndex");
            stringIdx.HasCacheEntry(mvid).Should().BeTrue("precondition: cache primed");

            File.Delete(copyPath);

            ModuleReloadedEventArgs? observed = null;
            index.ModuleReloaded += (_, e) => observed = e;

            index.Invalidate(mvid);

            observed.Should().NotBeNull("subscribers must still hear about the invalidation even when reload fails");
            observed!.OldMvid.Should().Be(mvid);
            observed.Error.Should().NotBeNull("the failure mode is visible on the event");
            stringIdx.HasCacheEntry(mvid).Should().BeFalse(
                "explicit Invalidate must clear internal caches even when the on-disk reload fails — " +
                "the caller is asserting the MVID is known-stale");

            // Critical: a follow-up query for the same MVID must NOT silently repopulate
            // the cache from the stale in-memory PE. Reload failure evicts the ModuleHandle
            // from the store so TryGet returns false.
            index.FindStringReferences("does-not-exist-xyz", StringMatchMode.Contains, mvid);
            stringIdx.HasCacheEntry(mvid).Should().BeFalse(
                "follow-up queries after a failed explicit Invalidate must not repopulate from the stale PE — " +
                "the stale ModuleHandle must be evicted from ModuleStore on failure");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    private static List<IModuleScopedCache> GetSubscribers(MetadataIndex index)
    {
        var field = typeof(MetadataIndex).GetField("_moduleScopedCaches",
            BindingFlags.NonPublic | BindingFlags.Instance);
        field.Should().NotBeNull("MetadataIndex must expose the subscriber list to inheritors/tests");
        var list = (System.Collections.IList)field!.GetValue(index)!;
        return list.Cast<IModuleScopedCache>().ToList();
    }

    private static T GetField<T>(MetadataIndex index, string name)
    {
        var f = typeof(MetadataIndex).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        f.Should().NotBeNull($"private field {name} expected on MetadataIndex");
        return (T)f!.GetValue(index)!;
    }
}
