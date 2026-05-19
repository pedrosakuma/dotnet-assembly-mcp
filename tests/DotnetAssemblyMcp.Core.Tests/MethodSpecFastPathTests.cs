using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Identity;
using DotnetAssemblyMcp.Core.Metadata;
using DotnetAssemblyMcp.Server.Tools;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// §3.5 MethodSpec fast-path: supplying a <c>(mvid, token)</c> pair pointing at a
/// <c>MethodSpec</c> row in a caller's module lets the resolver derive the closed
/// instantiation directly from metadata, without the producer having to render
/// type-arg strings. When both forms are supplied, they are cross-checked.
/// </summary>
public sealed class MethodSpecFastPathTests
{
    private static string SampleLibPath => typeof(SampleLib.OrderService).Assembly.Location;
    private static string SampleConsumerPath => typeof(SampleConsumer.ConsumerService).Assembly.Location;

    private static MethodInfo MethodOf(Type t, string name) =>
        t.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)!;

    /// <summary>
    /// Scans the caller's IL for MethodSpec tokens, returning the first one whose decoded
    /// Instantiation matches the requested type argument names (Ordinal compare on the
    /// wire-format rendering).
    /// </summary>
    private static int FindMethodSpecToken(MetadataIndex index, MethodInfo caller, params string[] expectedArgs)
    {
        var callerId = new MethodIdentity(caller.Module.ModuleVersionId, caller.MetadataToken);
        var scan = index.ScanIl(callerId);
        scan.IsSuccess.Should().BeTrue();

        foreach (var c in scan.Scan!.Calls)
        {
            var h = MetadataTokens.Handle(c.Token);
            if (h.Kind != HandleKind.MethodSpecification) continue;

            // Open the caller's metadata reader to decode the spec.
            // We can do this via reflection on the loaded assembly since we have the path.
            using var pe = new System.Reflection.PortableExecutable.PEReader(File.OpenRead(caller.Module.Assembly.Location));
            var md = pe.GetMetadataReader();
            var spec = md.GetMethodSpecification((MethodSpecificationHandle)h);
            var decoded = spec.DecodeSignature(new WireFormatSignatureProvider(), genericContext: (object?)null);
            if (decoded.Length == expectedArgs.Length
                && decoded.Zip(expectedArgs, (a, b) => string.Equals(a, b, StringComparison.Ordinal)).All(x => x))
            {
                return c.Token;
            }
        }
        throw new InvalidOperationException($"No MethodSpec with [{string.Join(",", expectedArgs)}] in {caller.Name}.");
    }

    [Fact]
    public void Only_method_spec_yields_closed_signature()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);
        index.Load(SampleConsumerPath);

        var echoOpen = MethodOf(typeof(SampleLib.OrderService), "Echo");
        var callInt = MethodOf(typeof(SampleConsumer.ConsumerService), "CallEchoOfInt");
        var specToken = FindMethodSpecToken(index, callInt, "System.Int32");

        var result = AssemblyTools.GetMethod(
            index,
            echoOpen.Module.ModuleVersionId.ToString("D"),
            $"0x{echoOpen.MetadataToken:X8}",
            methodSpecModuleVersionId: callInt.Module.ModuleVersionId.ToString("D"),
            methodSpecMetadataToken: $"0x{specToken:X8}");

        result.IsError.Should().BeFalse(result.Summary);
        result.Data!.Signature.Should().Contain("System.Int32");
        result.Data.Signature.Should().NotContain("!!0");
    }

    [Fact]
    public void Method_spec_plus_matching_explicit_args_succeeds()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);
        index.Load(SampleConsumerPath);

        var echoOpen = MethodOf(typeof(SampleLib.OrderService), "Echo");
        var callInt = MethodOf(typeof(SampleConsumer.ConsumerService), "CallEchoOfInt");
        var specToken = FindMethodSpecToken(index, callInt, "System.Int32");

        var result = AssemblyTools.GetMethod(
            index,
            echoOpen.Module.ModuleVersionId.ToString("D"),
            $"0x{echoOpen.MetadataToken:X8}",
            genericMethodArguments: ["System.Int32"],
            methodSpecModuleVersionId: callInt.Module.ModuleVersionId.ToString("D"),
            methodSpecMetadataToken: $"0x{specToken:X8}");

        result.IsError.Should().BeFalse(result.Summary);
        result.Data!.Signature.Should().Contain("System.Int32");
    }

    [Fact]
    public void Method_spec_plus_mismatched_explicit_args_yields_generic_instantiation_mismatch()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);
        index.Load(SampleConsumerPath);

        var echoOpen = MethodOf(typeof(SampleLib.OrderService), "Echo");
        var callInt = MethodOf(typeof(SampleConsumer.ConsumerService), "CallEchoOfInt");
        var specToken = FindMethodSpecToken(index, callInt, "System.Int32");

        var result = AssemblyTools.GetMethod(
            index,
            echoOpen.Module.ModuleVersionId.ToString("D"),
            $"0x{echoOpen.MetadataToken:X8}",
            genericMethodArguments: ["System.String"],
            methodSpecModuleVersionId: callInt.Module.ModuleVersionId.ToString("D"),
            methodSpecMetadataToken: $"0x{specToken:X8}");

        result.IsError.Should().BeTrue();
        result.Error!.Kind.Should().Be(ErrorKinds.GenericInstantiationMismatch);
    }

    [Fact]
    public void Method_spec_with_invalid_token_yields_invalid_argument()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);
        index.Load(SampleConsumerPath);

        var echoOpen = MethodOf(typeof(SampleLib.OrderService), "Echo");
        var callInt = MethodOf(typeof(SampleConsumer.ConsumerService), "CallEchoOfInt");

        var result = AssemblyTools.GetMethod(
            index,
            echoOpen.Module.ModuleVersionId.ToString("D"),
            $"0x{echoOpen.MetadataToken:X8}",
            methodSpecModuleVersionId: callInt.Module.ModuleVersionId.ToString("D"),
            methodSpecMetadataToken: "0xFFFFFFFF");

        result.IsError.Should().BeTrue();
        result.Error!.Kind.Should().BeOneOf(ErrorKinds.InvalidArgument, ErrorKinds.TokenWrongTable);
    }

    [Fact]
    public void Method_spec_with_unloaded_module_yields_module_not_found()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);

        var echoOpen = MethodOf(typeof(SampleLib.OrderService), "Echo");
        var bogusMvid = Guid.NewGuid();

        var result = AssemblyTools.GetMethod(
            index,
            echoOpen.Module.ModuleVersionId.ToString("D"),
            $"0x{echoOpen.MetadataToken:X8}",
            methodSpecModuleVersionId: bogusMvid.ToString("D"),
            methodSpecMetadataToken: "0x2B000001");

        result.IsError.Should().BeTrue();
        result.Error!.Kind.Should().Be(ErrorKinds.ModuleNotFound);
    }

    [Fact]
    public void Method_spec_token_with_wrong_table_yields_token_wrong_table()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);

        var echoOpen = MethodOf(typeof(SampleLib.OrderService), "Echo");

        // 0x06xxxxxx is MethodDef, not MethodSpec.
        var result = AssemblyTools.GetMethod(
            index,
            echoOpen.Module.ModuleVersionId.ToString("D"),
            $"0x{echoOpen.MetadataToken:X8}",
            methodSpecModuleVersionId: echoOpen.Module.ModuleVersionId.ToString("D"),
            methodSpecMetadataToken: $"0x{echoOpen.MetadataToken:X8}");

        result.IsError.Should().BeTrue();
        result.Error!.Kind.Should().Be(ErrorKinds.TokenWrongTable);
    }

    [Fact]
    public void Get_method_with_method_spec_renders_closed_signature()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);
        index.Load(SampleConsumerPath);

        var echoOpen = MethodOf(typeof(SampleLib.OrderService), "Echo");
        var callInt = MethodOf(typeof(SampleConsumer.ConsumerService), "CallEchoOfInt");
        var specToken = FindMethodSpecToken(index, callInt, "System.Int32");

        var result = AssemblyTools.GetMethod(index,
            echoOpen.Module.ModuleVersionId.ToString("D"),
            $"0x{echoOpen.MetadataToken:X8}",
            methodSpecModuleVersionId: callInt.Module.ModuleVersionId.ToString("D"),
            methodSpecMetadataToken: $"0x{specToken:X8}");

        result.IsError.Should().BeFalse(result.Summary);
        result.Data!.Signature.Should().Contain("System.Int32");
    }
}
