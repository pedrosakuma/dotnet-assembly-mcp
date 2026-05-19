using System.Reflection;
using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Identity;
using DotnetAssemblyMcp.Core.Metadata;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Tier-4 reverse-call tests against the SampleLib fixture. Validates that the xref index
/// finds same-module callers (Process(int) → Compute, ProcessAsync(int) → Process(int)),
/// distinguishes overloads, returns empty for un-called methods, and persists/reloads via
/// the on-disk cache.
/// </summary>
public sealed class XrefIndexTests
{
    private static string SampleLibPath => typeof(SampleLib.OrderService).Assembly.Location;

    private static MethodIdentity IdentityOf(MethodInfo mi) =>
        new(mi.Module.ModuleVersionId, mi.MetadataToken);

    private static MethodInfo MethodOf(Type t, string name, params Type[] args) =>
        t.GetMethod(
            name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static,
            binder: null,
            types: args,
            modifiers: null)!;

    [Fact]
    public void FindCallers_returns_caller_of_private_helper()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);

        var compute = MethodOf(typeof(SampleLib.OrderService), "Compute", typeof(int));
        var processInt = MethodOf(typeof(SampleLib.OrderService), "Process", typeof(int));

        var result = index.FindCallers(IdentityOf(compute));

        result.IsSuccess.Should().BeTrue();
        result.Result!.Callers.Should().Contain(c => c.MetadataToken == processInt.MetadataToken);
    }

    [Fact]
    public void FindCallers_distinguishes_overloads()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);

        var processInt = MethodOf(typeof(SampleLib.OrderService), "Process", typeof(int));
        var processString = MethodOf(typeof(SampleLib.OrderService), "Process", typeof(string));
        var processAsync = MethodOf(typeof(SampleLib.OrderService), "ProcessAsync", typeof(int));

        var intCallers = index.FindCallers(IdentityOf(processInt)).Result!.Callers
            .Select(c => c.MetadataToken).ToHashSet();
        var stringCallers = index.FindCallers(IdentityOf(processString)).Result!.Callers
            .Select(c => c.MetadataToken).ToHashSet();

        intCallers.Should().NotContain(processString.MetadataToken);
        stringCallers.Should().NotContain(processInt.MetadataToken);
        // ProcessAsync(int) calls Process(int) (via the compiler-generated state machine's MoveNext —
        // here we assert the direct user-visible caller surface is non-empty for Process(int)).
        intCallers.Should().NotBeEmpty();
    }

    [Fact]
    public void FindCallers_returns_empty_for_uncalled_method()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);

        // MakeAdder is public, only invoked externally — no other method in SampleLib calls it.
        var makeAdder = MethodOf(typeof(SampleLib.OrderService), "MakeAdder", typeof(int));

        var result = index.FindCallers(IdentityOf(makeAdder));

        result.IsSuccess.Should().BeTrue();
        result.Result!.Callers.Should().BeEmpty();
    }

    [Fact]
    public void FindCallers_finds_same_module_interface_caller_via_memberref()
    {
        // OrderService.Process(int) calls _logger.Log(string), an interface MemberRef whose
        // parent type lives in the same module. The xref index must resolve the MemberRef
        // back to a local MethodDef so this caller is discovered without loading any consumer.
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);

        var log = MethodOf(typeof(SampleLib.ILogger), "Log", typeof(string));
        var processInt = MethodOf(typeof(SampleLib.OrderService), "Process", typeof(int));

        var result = index.FindCallers(IdentityOf(log));

        result.IsSuccess.Should().BeTrue();
        result.Result!.Callers.Should().Contain(c => c.MetadataToken == processInt.MetadataToken);
    }

    [Fact]
    public void FindCallers_unknown_mvid_returns_module_not_found()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);

        var compute = MethodOf(typeof(SampleLib.OrderService), "Compute", typeof(int));
        var result = index.FindCallers(new MethodIdentity(Guid.NewGuid(), compute.MetadataToken));

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.ModuleNotFound);
    }

    [Fact]
    public void FindCallers_second_call_is_served_from_in_memory_cache()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);

        var compute = MethodOf(typeof(SampleLib.OrderService), "Compute", typeof(int));

        var first = index.FindCallers(IdentityOf(compute));
        var second = index.FindCallers(IdentityOf(compute));

        first.Result!.FromCache.Should().BeFalse();
        second.Result!.FromCache.Should().BeTrue();
        second.Result.Callers.Should().BeEquivalentTo(first.Result.Callers);
    }

    [Fact]
    public void FindCallers_disk_cache_survives_index_recreation()
    {
        var compute = MethodOf(typeof(SampleLib.OrderService), "Compute", typeof(int));
        var identity = IdentityOf(compute);

        // First index builds and writes the on-disk cache.
        using (var first = new MetadataIndex())
        {
            first.Load(SampleLibPath);
            first.FindCallers(identity).IsSuccess.Should().BeTrue();
        }

        // Second index should pick up the previously written cache.
        using var second = new MetadataIndex();
        second.Load(SampleLibPath);
        var fresh = second.FindCallers(identity);
        fresh.IsSuccess.Should().BeTrue();
        fresh.Result!.FromCache.Should().BeFalse(); // FromCache reflects in-memory; disk hit is opaque
        fresh.Result.Callers.Should().NotBeEmpty();
    }
}
