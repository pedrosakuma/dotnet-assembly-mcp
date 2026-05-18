using System.Reflection;
using DotnetAssemblyMcp.Core.Decompilation;
using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Identity;
using DotnetAssemblyMcp.Core.Metadata;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Tier-3 decompilation tests against the SampleLib fixture. Validates that
/// ICSharpCode.Decompiler produces meaningful C# for a known method, that the LRU cache
/// returns hits on repeated identical calls, and that an over-budget result is truncated.
/// </summary>
public sealed class DecompilerTests
{
    private static string SampleLibPath => typeof(SampleLib.OrderService).Assembly.Location;

    private static (MetadataIndex index, Decompiler dec) NewSubject()
    {
        var index = new MetadataIndex();
        index.Load(SampleLibPath);
        return (index, new Decompiler(index));
    }

    private static MethodIdentity IdentityFor(string methodName, params Type[] argTypes)
    {
        var mi = typeof(SampleLib.OrderService).GetMethod(
            methodName, BindingFlags.Public | BindingFlags.Instance, binder: null,
            types: argTypes, modifiers: null)!;
        return new MethodIdentity(mi.Module.ModuleVersionId, mi.MetadataToken);
    }

    [Fact]
    public void Decompile_returns_csharp_for_a_known_method()
    {
        var (index, dec) = NewSubject();
        using var _ = index;

        var result = dec.Decompile(IdentityFor("Process", typeof(int)));

        result.IsSuccess.Should().BeTrue();
        var src = result.Source!;
        src.Source.Should().Contain("Process").And.Contain("Compute");
        src.MethodName.Should().Be("Process");
        src.TypeFullName.Should().Be("SampleLib.OrderService");
        src.Truncated.Should().BeFalse();
        src.CacheHit.Should().BeFalse();
        src.SourceLengthChars.Should().Be(src.Source.Length);
    }

    [Fact]
    public void Decompile_second_call_hits_cache()
    {
        var (index, dec) = NewSubject();
        using var _ = index;

        var id = IdentityFor("Process", typeof(int));
        var first = dec.Decompile(id);
        var second = dec.Decompile(id);

        first.Source!.CacheHit.Should().BeFalse();
        second.Source!.CacheHit.Should().BeTrue();
        second.Source.Source.Should().Be(first.Source.Source);
        dec.CachedEntries.Should().Be(1);
    }

    [Fact]
    public void Decompile_distinguishes_overloads_in_cache()
    {
        var (index, dec) = NewSubject();
        using var _ = index;

        dec.Decompile(IdentityFor("Process", typeof(int)));
        dec.Decompile(IdentityFor("Process", typeof(string)));

        dec.CachedEntries.Should().Be(2);
    }

    [Fact]
    public void Decompile_truncates_output_when_exceeding_max_chars()
    {
        var (index, dec) = NewSubject();
        using var _ = index;

        var result = dec.Decompile(IdentityFor("Process", typeof(int)), maxChars: 32);

        result.IsSuccess.Should().BeTrue();
        result.Source!.Truncated.Should().BeTrue();
        result.Source.Source.Should().Contain("[truncated by server");
        result.Source.SourceLengthChars.Should().BeGreaterThan(32); // includes the marker
    }

    [Fact]
    public void Decompile_unknown_mvid_returns_module_not_found()
    {
        var (index, dec) = NewSubject();
        using var _ = index;

        var result = dec.Decompile(new MethodIdentity(Guid.NewGuid(), 0x06000001));

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.ModuleNotFound);
    }

    [Fact]
    public void Decompile_ttl_expiry_invalidates_cache_entry()
    {
        var index = new MetadataIndex();
        index.Load(SampleLibPath);
        using var _ = index;

        var t = DateTime.UtcNow;
        var dec = TestHelpers.NewDecompilerWithClock(index, () => t);

        var id = IdentityFor("Process", typeof(int));
        dec.Decompile(id);

        // Advance clock past TTL — the next call must miss the cache.
        t = t + Decompiler.EntryTtl + TimeSpan.FromSeconds(1);
        var afterExpiry = dec.Decompile(id);

        afterExpiry.Source!.CacheHit.Should().BeFalse();
    }
}

internal static class TestHelpers
{
    public static Decompiler NewDecompilerWithClock(IMetadataIndex index, Func<DateTime> clock)
    {
        // Access the internal ctor via the same assembly's InternalsVisibleTo or, if absent,
        // via the public ctor + reflection. We expose an InternalsVisibleTo so the public
        // ctor is preferred here.
        var ctor = typeof(Decompiler).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            types: new[] { typeof(IMetadataIndex), typeof(Func<DateTime>) },
            modifiers: null)!;
        return (Decompiler)ctor.Invoke(new object[] { index, clock });
    }
}
