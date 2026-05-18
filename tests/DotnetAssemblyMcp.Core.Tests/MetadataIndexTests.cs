using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Identity;
using DotnetAssemblyMcp.Core.Metadata;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Round-trip tests for <see cref="MetadataIndex"/> against the SampleLib fixture. Validates
/// the consumer side of the MethodIdentity handoff contract: <c>(MVID, MethodDef token)</c>
/// must resolve to the same method whose token was looked up via the producer-side reflection API.
/// </summary>
public sealed class MetadataIndexTests
{
    private static string SampleLibPath => typeof(SampleLib.OrderService).Assembly.Location;

    [Fact]
    public void Load_returns_module_summary_with_mvid()
    {
        using var index = new MetadataIndex();
        var result = index.Load(SampleLibPath);

        result.IsSuccess.Should().BeTrue();
        result.Module!.ModuleName.Should().Be("SampleLib.dll");
        result.Module.ModuleVersionId.Should().Be(typeof(SampleLib.OrderService).Assembly.ManifestModule.ModuleVersionId);
        result.Module.MethodCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Load_is_idempotent_by_mvid()
    {
        using var index = new MetadataIndex();
        var a = index.Load(SampleLibPath);
        var b = index.Load(SampleLibPath);

        a.IsSuccess.Should().BeTrue();
        b.IsSuccess.Should().BeTrue();
        index.List().Should().HaveCount(1);
    }

    [Fact]
    public void Load_missing_file_returns_module_load_failed()
    {
        using var index = new MetadataIndex();
        var result = index.Load(Path.Combine(Path.GetTempPath(), "does-not-exist.dll"));

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.ModuleLoadFailed);
    }

    [Fact]
    public void Load_non_managed_file_returns_module_load_failed()
    {
        using var index = new MetadataIndex();
        var temp = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(temp, new byte[] { 0x4D, 0x5A, 0x00, 0x01, 0x02 });
            var result = index.Load(temp);
            result.IsSuccess.Should().BeFalse();
            result.Error!.Kind.Should().Be(ErrorKinds.ModuleLoadFailed);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void Resolve_round_trips_a_real_method_token()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);

        var mi = typeof(SampleLib.OrderService).GetMethod(
            nameof(SampleLib.OrderService.Process),
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: new[] { typeof(int) },
            modifiers: null)!;
        var mvid = mi.Module.ModuleVersionId;
        var token = mi.MetadataToken;

        var result = index.Resolve(new MethodIdentity(mvid, token));

        result.IsSuccess.Should().BeTrue();
        result.Method!.TypeFullName.Should().Be("SampleLib.OrderService");
        result.Method.MethodName.Should().Be("Process");
        result.Method.MetadataToken.Should().Be(token);
        result.Method.ModuleVersionId.Should().Be(mvid);
        result.Method.Signature.Should().Contain("int").And.Contain("Process");
        result.Method.Handle.Should().StartWith("m:");
        result.Method.IlSize.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Resolve_distinguishes_overloads_by_token()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);

        var intOverload = typeof(SampleLib.OrderService).GetMethod(
            "Process", new[] { typeof(int) })!;
        var stringOverload = typeof(SampleLib.OrderService).GetMethod(
            "Process", new[] { typeof(string) })!;
        var mvid = intOverload.Module.ModuleVersionId;

        var ri = index.Resolve(new MethodIdentity(mvid, intOverload.MetadataToken));
        var rs = index.Resolve(new MethodIdentity(mvid, stringOverload.MetadataToken));

        ri.IsSuccess.Should().BeTrue();
        rs.IsSuccess.Should().BeTrue();
        ri.Method!.Signature.Should().Contain("int");
        rs.Method!.Signature.Should().Contain("string");
    }

    [Fact]
    public void Resolve_unknown_mvid_returns_module_not_found()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);

        var result = index.Resolve(new MethodIdentity(Guid.NewGuid(), 0x06000001));

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.ModuleNotFound);
    }

    [Fact]
    public void Resolve_wrong_table_returns_token_wrong_table()
    {
        using var index = new MetadataIndex();
        var loaded = index.Load(SampleLibPath);
        var mvid = loaded.Module!.ModuleVersionId;

        // TypeDef (table 0x02) instead of MethodDef (0x06).
        var typeDefToken = 0x02000002;
        var result = index.Resolve(new MethodIdentity(mvid, typeDefToken));

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.TokenWrongTable);
    }

    [Fact]
    public void Resolve_out_of_range_methoddef_returns_token_out_of_range()
    {
        using var index = new MetadataIndex();
        var loaded = index.Load(SampleLibPath);
        var mvid = loaded.Module!.ModuleVersionId;

        var bogus = (0x06 << 24) | 0x00FFFFFF;
        var result = index.Resolve(new MethodIdentity(mvid, bogus));

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.TokenOutOfRange);
    }

    [Fact]
    public void Resolve_empty_mvid_returns_identity_malformed()
    {
        using var index = new MetadataIndex();
        var result = index.Resolve(new MethodIdentity(Guid.Empty, 0x06000001));

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.IdentityMalformed);
    }
}
