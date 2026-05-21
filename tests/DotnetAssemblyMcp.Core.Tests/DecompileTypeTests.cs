using System.Reflection;
using DotnetAssemblyMcp.Core.Decompilation;
using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Metadata;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Tier-3 whole-type decompilation tests (#106) against the SampleLib fixture. Validates that
/// ICSharpCode.Decompiler renders the entire <c>OrderService</c> class (declaration + members
/// + nested types) in one call, that the type cache returns hits on repeat calls, that
/// non-TypeDef tokens are rejected, and that the type cache is independent of the method cache.
/// </summary>
public sealed class DecompileTypeTests
{
    private static string SampleLibPath => typeof(SampleLib.OrderService).Assembly.Location;

    private static (MetadataIndex index, Decompiler dec) NewSubject()
    {
        var index = new MetadataIndex();
        index.Load(SampleLibPath);
        return (index, new Decompiler(index));
    }

    private static (Guid mvid, int token) TypeTokenFor(Type t)
        => (t.Module.ModuleVersionId, t.MetadataToken);

    [Fact]
    public void DecompileType_returns_csharp_for_a_whole_class()
    {
        var (index, dec) = NewSubject();
        using var _ = index;
        var (mvid, token) = TypeTokenFor(typeof(SampleLib.OrderService));

        var result = dec.DecompileType(mvid, token);

        result.IsSuccess.Should().BeTrue();
        var src = result.Source!;
        src.TypeFullName.Should().Be("SampleLib.OrderService");
        src.MetadataToken.Should().Be(token);
        src.ModuleVersionId.Should().Be(mvid);
        // Whole-type render must include both the declaration AND multiple member bodies — the
        // headline differentiator vs decompile_method. OrderService has Process(int) and
        // Process(string) overloads plus a Compute helper, all in declaration order.
        src.Source.Should().Contain("class OrderService");
        src.Source.Should().Contain("Process");
        src.Source.Should().Contain("Compute");
        src.Truncated.Should().BeFalse();
        src.CacheHit.Should().BeFalse();
        src.SourceLengthChars.Should().Be(src.Source.Length);
    }

    [Fact]
    public void Second_identical_call_returns_a_cache_hit()
    {
        var (index, dec) = NewSubject();
        using var _ = index;
        var (mvid, token) = TypeTokenFor(typeof(SampleLib.OrderService));

        var first = dec.DecompileType(mvid, token);
        var second = dec.DecompileType(mvid, token);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        first.Source!.CacheHit.Should().BeFalse();
        second.Source!.CacheHit.Should().BeTrue();
        second.Source.Source.Should().Be(first.Source.Source);
    }

    [Fact]
    public void Over_budget_response_is_truncated_with_marker()
    {
        var (index, dec) = NewSubject();
        using var _ = index;
        var (mvid, token) = TypeTokenFor(typeof(SampleLib.OrderService));

        var result = dec.DecompileType(mvid, token, maxChars: 64);

        result.IsSuccess.Should().BeTrue();
        var src = result.Source!;
        src.Truncated.Should().BeTrue();
        src.Source.Should().Contain("[truncated by server: exceeded maxChars]");
    }

    [Fact]
    public void Non_typedef_token_is_rejected_with_identity_malformed()
    {
        var (index, dec) = NewSubject();
        using var _ = index;
        var (mvid, _) = TypeTokenFor(typeof(SampleLib.OrderService));

        // Pass a MethodDef token (table 0x06) where a TypeDef (0x02) is required.
        var processMethod = typeof(SampleLib.OrderService).GetMethod(
            "Process", BindingFlags.Public | BindingFlags.Instance, binder: null,
            types: new[] { typeof(int) }, modifiers: null)!;
        var result = dec.DecompileType(mvid, processMethod.MetadataToken);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.IdentityMalformed);
        result.Error.Message.Should().Contain("MethodDefinition");
    }

    [Fact]
    public void Unknown_typedef_token_surfaces_type_not_found_from_index()
    {
        var (index, dec) = NewSubject();
        using var _ = index;
        var (mvid, _) = TypeTokenFor(typeof(SampleLib.OrderService));

        // A TypeDef row id that does not exist in the module. 0x02FFFFFF stays inside the
        // TypeDef table identifier (high byte = 0x02) but its row is well past the end.
        var result = dec.DecompileType(mvid, 0x02FFFFFF);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotBeNull();
    }

    [Fact]
    public void Type_cache_is_independent_of_method_cache()
    {
        var (index, dec) = NewSubject();
        using var _ = index;
        var (mvid, typeToken) = TypeTokenFor(typeof(SampleLib.OrderService));

        // Decompile one method first — populates the method cache.
        var processMethod = typeof(SampleLib.OrderService).GetMethod(
            "Process", BindingFlags.Public | BindingFlags.Instance, binder: null,
            types: new[] { typeof(int) }, modifiers: null)!;
        var methodResult = dec.Decompile(new Identity.MethodIdentity(mvid, processMethod.MetadataToken));
        methodResult.IsSuccess.Should().BeTrue();

        // Now decompile the whole type — it must MISS the cache (different keyspace).
        var typeResult = dec.DecompileType(mvid, typeToken);
        typeResult.IsSuccess.Should().BeTrue();
        typeResult.Source!.CacheHit.Should().BeFalse(
            "type-level results live in a separate LRU from method-level results");

        // CachedEntries aggregates both — at least 2 (one method + one type).
        dec.CachedEntries.Should().BeGreaterThanOrEqualTo(2);
    }
}
