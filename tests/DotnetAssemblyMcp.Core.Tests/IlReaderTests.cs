using System.Reflection;
using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Identity;
using DotnetAssemblyMcp.Core.Metadata;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Tier-2 / Tier-2.5 IL surface tests: validate that <see cref="MetadataIndex.GetIlBody"/>
/// returns body metadata that matches reality for the SampleLib fixture, and that
/// <see cref="MetadataIndex.ScanIl"/> recognises calls/strings emitted by the C# compiler.
/// </summary>
public sealed class IlReaderTests
{
    private static string SampleLibPath => typeof(SampleLib.OrderService).Assembly.Location;

    private static MethodIdentity IdentityOf(MethodInfo mi) =>
        new(mi.Module.ModuleVersionId, mi.MetadataToken);

    private static MethodInfo IntProcess() =>
        typeof(SampleLib.OrderService).GetMethod(
            "Process",
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: new[] { typeof(int) },
            modifiers: null)!;

    private static MethodInfo StringProcess() =>
        typeof(SampleLib.OrderService).GetMethod(
            "Process",
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: new[] { typeof(string) },
            modifiers: null)!;

    [Fact]
    public void GetIlBody_returns_non_empty_hex_for_int_process()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);

        var result = index.GetIlBody(IdentityOf(IntProcess()));

        result.IsSuccess.Should().BeTrue();
        var body = result.Body!;
        body.IlSize.Should().BeGreaterThan(0);
        body.InstructionCount.Should().BeGreaterThan(0);
        body.MaxStack.Should().BeGreaterThan(0);
        body.ExceptionRegionCount.Should().BeGreaterThan(0);
        body.IlHex.Should().NotBeNullOrEmpty();
        body.IlHex.Length.Should().Be(body.IlSize * 2);
        body.IlTruncated.Should().BeFalse();
    }

    [Fact]
    public void GetIlBody_truncates_hex_when_max_bytes_is_small()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);

        var result = index.GetIlBody(IdentityOf(IntProcess()), maxBytes: 4);

        result.IsSuccess.Should().BeTrue();
        var body = result.Body!;
        body.IlTruncated.Should().BeTrue();
        body.IlHex.Length.Should().Be(8);
        body.IlSize.Should().BeGreaterThan(4);
    }

    [Fact]
    public void GetIlBody_on_abstract_method_returns_empty_body()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);

        var log = typeof(SampleLib.ILogger).GetMethod("Log")!;
        var result = index.GetIlBody(IdentityOf(log));

        result.IsSuccess.Should().BeTrue();
        var body = result.Body!;
        body.IlSize.Should().Be(0);
        body.IlHex.Should().BeEmpty();
        body.InstructionCount.Should().Be(0);
    }

    [Fact]
    public void GetIlBody_unknown_mvid_returns_module_not_found()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);

        var result = index.GetIlBody(new MethodIdentity(Guid.NewGuid(), IntProcess().MetadataToken));

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.ModuleNotFound);
    }

    [Fact]
    public void GetIlBody_wrong_token_table_returns_token_wrong_table()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);

        var typeToken = typeof(SampleLib.OrderService).MetadataToken;
        var result = index.GetIlBody(new MethodIdentity(IntProcess().Module.ModuleVersionId, typeToken));

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.TokenWrongTable);
    }

    [Fact]
    public void ScanIl_detects_logger_call_compute_call_and_string_literal()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);

        var result = index.ScanIl(IdentityOf(IntProcess()));

        result.IsSuccess.Should().BeTrue();
        var scan = result.Scan!;
        scan.InstructionCount.Should().BeGreaterThan(0);
        scan.Calls.Should().Contain(c => c.Display.Contains("ILogger.Log"));
        scan.Calls.Should().Contain(c => c.Display.Contains("Compute"));
        scan.Strings.Should().Contain(s => s.Contains("processing order"));
        scan.Fields.Should().Contain(f => f.Display.Contains("_counter") || f.Display.Contains("_logger"));
    }

    [Fact]
    public void ScanIl_is_independent_across_overloads()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);

        var intScan = index.ScanIl(IdentityOf(IntProcess())).Scan!;
        var stringScan = index.ScanIl(IdentityOf(StringProcess())).Scan!;

        intScan.MetadataToken.Should().NotBe(stringScan.MetadataToken);
        intScan.Calls.Should().Contain(c => c.Display.Contains("Compute"));
        stringScan.Calls.Should().NotContain(c => c.Display.Contains("Compute"));
    }

    [Fact]
    public void ScanIl_unknown_mvid_returns_module_not_found()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);

        var result = index.ScanIl(new MethodIdentity(Guid.NewGuid(), IntProcess().MetadataToken));

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ErrorKinds.ModuleNotFound);
    }
}
