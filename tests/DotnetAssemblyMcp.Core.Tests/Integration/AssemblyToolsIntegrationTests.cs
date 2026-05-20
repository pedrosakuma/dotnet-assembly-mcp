using DotnetAssemblyMcp.Core;
using DotnetAssemblyMcp.Core.Decompilation;
using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Metadata;
using DotnetAssemblyMcp.Server.Tools;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests.Integration;

/// <summary>
/// End-to-end integration test for issue #81. Constructs the same DI graph
/// Program.cs builds (IMetadataIndex + IDecompiler + IIlDisassembler), then
/// drives three representative <see cref="AssemblyTools"/> entry points
/// (<c>load_assembly</c> → <c>get_method</c> → <c>get_method_il_text</c>) plus
/// one error path (<c>get_method</c> with an unknown MVID).
///
/// Catches regressions that the schema-contract tests miss: argument parsing,
/// error envelopes, NextActionHint shape, and the DI registrations themselves.
/// </summary>
public sealed class AssemblyToolsIntegrationTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly IMetadataIndex _index;
    private readonly IDecompiler _decompiler;
    private readonly IIlDisassembler _disassembler;

    public AssemblyToolsIntegrationTests()
    {
        // Mirror Program.cs.RegisterCoreServices.
        var services = new ServiceCollection();
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AssemblyMcp:WatchForChanges"] = "false",
            })
            .Build();

        services.AddSingleton<IConfiguration>(cfg);
        services.AddSingleton<IMetadataIndex>(_ =>
            new MetadataIndex(watchForChanges:
                cfg.GetValue("AssemblyMcp:WatchForChanges", defaultValue: true)));
        services.AddSingleton<IDecompiler, Decompiler>();
        services.AddSingleton<IIlDisassembler, IlDisassembler>();

        _sp = services.BuildServiceProvider();
        _index = _sp.GetRequiredService<IMetadataIndex>();
        _decompiler = _sp.GetRequiredService<IDecompiler>();
        _disassembler = _sp.GetRequiredService<IIlDisassembler>();
    }

    [Fact]
    public void LoadAssembly_then_GetMethod_then_GetMethodIlText_succeed_end_to_end()
    {
        var samplePath = typeof(SampleLib.OrderService).Assembly.Location;

        // 1. load_assembly
        var load = AssemblyTools.LoadAssembly(_index, samplePath);
        load.IsError.Should().BeFalse();
        load.Data.Should().NotBeNull();
        load.Error.Should().BeNull();
        load.Summary.Should().Contain("Loaded");
        load.Hints.Should().NotBeNullOrEmpty()
            .And.OnlyContain(h => h.NextTool == "get_method");

        var mvid = load.Data!.ModuleVersionId.ToString("D");

        // Pick an instance method we know exists on OrderService.
        var methodInfo = typeof(SampleLib.OrderService)
            .GetMethod(
                "Process",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                binder: null,
                types: new[] { typeof(int) },
                modifiers: null)
            ?? throw new InvalidOperationException("SampleLib.OrderService.Process(int) not found");
        var tokenHex = $"0x{methodInfo.MetadataToken:X8}";

        // 2. get_method
        var get = AssemblyTools.GetMethod(_index, mvid, tokenHex);
        get.IsError.Should().BeFalse();
        get.Data.Should().NotBeNull();
        get.Data!.MethodName.Should().Be("Process");
        get.Data.TypeFullName.Should().Be("SampleLib.OrderService");
        get.Hints.Should().NotBeNullOrEmpty("get_method always points to follow-up tools");

        // 3. get_method_il_text
        var il = AssemblyTools.GetMethodIlText(_disassembler, _index, mvid, tokenHex);
        il.IsError.Should().BeFalse();
        il.Data.Should().NotBeNull();
        il.Data!.MethodName.Should().Be("Process");
        il.Data.LineCount.Should().BeGreaterThan(0);
        il.Data.InstructionCount.Should().BeGreaterThan(0);
        il.Summary.Should().Contain("IL instruction");
    }

    [Fact]
    public void GetMethod_with_unknown_mvid_returns_typed_error_envelope()
    {
        // Use a fresh, never-loaded GUID and a syntactically valid MethodDef token.
        var unknown = Guid.Parse("00000000-0000-0000-0000-DEADBEEFDEAD").ToString("D");

        var result = AssemblyTools.GetMethod(_index, unknown, "0x06000001");

        result.IsError.Should().BeTrue();
        result.Data.Should().BeNull();
        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be(ErrorKinds.ModuleNotFound);
        // Recovery hint must point the agent at the producer side.
        result.Hints.Should().NotBeNullOrEmpty(
            "every error envelope must surface a NextActionHint so the agent can self-recover");
    }

    public void Dispose() => _sp.Dispose();
}
