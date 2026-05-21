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

        var typeNames = subscribers.Select(c => c.GetType().Name).ToList();
        typeNames.Should().Contain(new[]
        {
            nameof(XrefIndex),
            nameof(StringIndex),
            nameof(AttributeIndex),
            nameof(FieldAccessIndex),
            nameof(TypeNavigationIndex),
        });
        typeNames.Should().Contain(n => n.Contains("R2R", StringComparison.Ordinal));
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

        // Fan out invalidation as OnStoreModuleReloaded would.
        var subscribers = GetSubscribers(index);
        foreach (var sub in subscribers) sub.Invalidate(mvid);

        xrefIdx.HasCacheEntry(mvid).Should().BeFalse();
        stringIdx.HasCacheEntry(mvid).Should().BeFalse();
        attrIdx.HasCacheEntry(mvid).Should().BeFalse();
        fieldIdx.HasCacheEntry(mvid).Should().BeFalse();
        typeNavIdx.HasNameCacheEntry(mvid).Should().BeFalse();
        typeNavIdx.HasParentMapsEntry.Should().BeFalse();
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
