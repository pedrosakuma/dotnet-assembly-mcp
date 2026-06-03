using DotnetAssemblyMcp.Core;
using DotnetAssemblyMcp.Core.Decompilation;
using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Handles;
using DotnetAssemblyMcp.Core.Metadata;
using DotnetAssemblyMcp.Server.Tools;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Covers the additive intra-server convenience (Path A): every (MVID, token)-addressed tool
/// also accepts the matching opaque handle — 'm:&lt;mvid&gt;:0x&lt;token&gt;' for method tools,
/// 't:&lt;mvid&gt;:0x&lt;typeToken&gt;' for decompile_type — while the canonical handoff pair keeps
/// working unchanged. Guards the wording/behaviour contract described in docs/handoff-contract.md §2.1.
/// </summary>
public sealed class MethodHandleAcceptanceTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly IMetadataIndex _index;
    private readonly IDecompiler _decompiler;
    private readonly IIlDisassembler _disassembler;
    private readonly Guid _mvid;
    private readonly int _methodToken;
    private readonly string _methodHandle;
    private readonly int _typeToken;
    private readonly string _typeHandle;

    public MethodHandleAcceptanceTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMetadataIndex>(_ => new MetadataIndex(watchForChanges: false));
        services.AddSingleton<IDecompiler, Decompiler>();
        services.AddSingleton<IIlDisassembler, IlDisassembler>();
        _sp = services.BuildServiceProvider();
        _index = _sp.GetRequiredService<IMetadataIndex>();
        _decompiler = _sp.GetRequiredService<IDecompiler>();
        _disassembler = _sp.GetRequiredService<IIlDisassembler>();

        var samplePath = typeof(SampleLib.OrderService).Assembly.Location;
        var load = AssemblyTools.LoadAssembly(_index, samplePath);
        load.IsError.Should().BeFalse();
        _mvid = load.Data!.ModuleVersionId;

        var methodInfo = typeof(SampleLib.OrderService)
            .GetMethod("Process",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                binder: null, types: new[] { typeof(int) }, modifiers: null)
            ?? throw new InvalidOperationException("SampleLib.OrderService.Process(int) not found");
        _methodToken = methodInfo.MetadataToken;
        _methodHandle = HandleSyntax.FormatMethod(_mvid, _methodToken);

        _typeToken = typeof(SampleLib.OrderService).MetadataToken;
        _typeHandle = HandleSyntax.FormatType(_mvid, _typeToken);
    }

    [Fact]
    public void GetMethod_accepts_method_handle_with_no_token()
    {
        var result = AssemblyTools.GetMethod(_index, _methodHandle);
        result.IsError.Should().BeFalse();
        result.Data!.MethodName.Should().Be("Process");
        result.Data.TypeFullName.Should().Be("SampleLib.OrderService");
    }

    [Fact]
    public void DecompileMethod_accepts_method_handle_with_no_token()
    {
        var result = AssemblyTools.DecompileMethod(_decompiler, _index, _methodHandle);
        result.IsError.Should().BeFalse();
        result.Data!.MethodName.Should().Be("Process");
    }

    [Fact]
    public void GetMethodIl_accepts_method_handle_with_no_token()
    {
        var result = AssemblyTools.GetMethodIl(_disassembler, _index, _methodHandle, format: "text");
        result.IsError.Should().BeFalse();
        result.Data!.Text!.MethodName.Should().Be("Process");
    }

    [Fact]
    public void FindCallers_accepts_method_handle_with_no_token()
    {
        var result = AssemblyTools.FindCallers(_index, _methodHandle);
        result.IsError.Should().BeFalse();
        result.Summary.Should().Contain("caller");
    }

    [Fact]
    public void GetMethodSource_accepts_method_handle_with_no_token()
    {
        // Source resolution may report found=false depending on the PDB; the point is that the
        // handle parses and the call is not an error envelope.
        var result = AssemblyTools.GetMethodSource(_index, _methodHandle);
        result.IsError.Should().BeFalse();
    }

    [Fact]
    public void GetMethod_handle_with_matching_explicit_token_succeeds()
    {
        var result = AssemblyTools.GetMethod(_index, _methodHandle, $"0x{_methodToken:X8}");
        result.IsError.Should().BeFalse();
        result.Data!.MethodName.Should().Be("Process");
    }

    [Fact]
    public void GetMethod_handle_with_mismatched_explicit_token_fails()
    {
        var wrong = $"0x{_methodToken + 1:X8}";
        var result = AssemblyTools.GetMethod(_index, _methodHandle, wrong);
        result.IsError.Should().BeTrue();
        result.Error!.Kind.Should().Be(ErrorKinds.InvalidArgument);
        result.Error.Message.Should().Contain("does not match");
    }

    [Fact]
    public void GetMethod_bare_guid_without_token_fails_with_clear_error()
    {
        var result = AssemblyTools.GetMethod(_index, _mvid.ToString("D"));
        result.IsError.Should().BeTrue();
        result.Error!.Kind.Should().Be(ErrorKinds.IdentityMalformed);
        result.Error.Message.Should().Contain("metadataToken is required");
    }

    [Fact]
    public void GetMethod_with_type_handle_fails_with_wrong_kind_message()
    {
        var result = AssemblyTools.GetMethod(_index, _typeHandle);
        result.IsError.Should().BeTrue();
        result.Error!.Kind.Should().Be(ErrorKinds.InvalidArgument);
        result.Error.Message.Should().Contain("Type handle");
        result.Error.Message.Should().Contain("m:<mvid>:0x<methodToken>");
    }

    [Fact]
    public void DecompileType_accepts_type_handle_with_no_token()
    {
        var result = AssemblyTools.DecompileType(_decompiler, _index, _typeHandle);
        result.IsError.Should().BeFalse();
        result.Data!.TypeFullName.Should().Be("SampleLib.OrderService");
    }

    [Fact]
    public void DecompileType_with_method_handle_fails_with_wrong_kind_message()
    {
        var result = AssemblyTools.DecompileType(_decompiler, _index, _methodHandle);
        result.IsError.Should().BeTrue();
        result.Error!.Kind.Should().Be(ErrorKinds.InvalidArgument);
        result.Error.Message.Should().Contain("Method handle");
        result.Error.Message.Should().Contain("t:<mvid>:0x<typeToken>");
    }

    [Fact]
    public void GetMethod_canonical_pair_still_works()
    {
        var result = AssemblyTools.GetMethod(_index, _mvid.ToString("D"), $"0x{_methodToken:X8}");
        result.IsError.Should().BeFalse();
        result.Data!.MethodName.Should().Be("Process");
    }

    public void Dispose() => _sp.Dispose();
}
