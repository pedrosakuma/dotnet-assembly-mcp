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
/// HIGH-severity audit follow-ups (#11–#13, #16): MethodSpec fast-path semantics for
/// <c>find_callers</c>, target validation in <c>Resolve</c>, type-level filtering, and
/// fallback to explicit args when the methodSpec module is not loaded.
/// </summary>
public sealed class MethodSpecAuditTests
{
    private static string SampleLibPath => typeof(SampleLib.OrderService).Assembly.Location;
    private static string SampleConsumerPath => typeof(SampleConsumer.ConsumerService).Assembly.Location;

    private static MethodInfo MethodOf(Type t, string name) =>
        t.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)!;

    /// <summary>Finds the first MethodSpec token in <paramref name="caller"/> whose decoded
    /// Instantiation matches <paramref name="expectedArgs"/>.</summary>
    private static int FindMethodSpecToken(MetadataIndex index, MethodInfo caller, params string[] expectedArgs)
    {
        var callerId = new MethodIdentity(caller.Module.ModuleVersionId, caller.MetadataToken);
        var scan = index.ScanIl(callerId);
        scan.IsSuccess.Should().BeTrue();
        foreach (var c in scan.Scan!.Calls)
        {
            var h = MetadataTokens.Handle(c.Token);
            if (h.Kind != HandleKind.MethodSpecification) continue;
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

    // ---- #11: find_callers honors methodSpec fast-path (without explicit args) -----------

    [Fact]
    public void Find_callers_with_methodSpec_only_narrows_to_int_callers()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);
        index.Load(SampleConsumerPath);

        var echoOpen = MethodOf(typeof(SampleLib.OrderService), "Echo");
        var callInt = MethodOf(typeof(SampleConsumer.ConsumerService), "CallEchoOfInt");
        var callStr = MethodOf(typeof(SampleConsumer.ConsumerService), "CallEchoOfString");
        var specToken = FindMethodSpecToken(index, callInt, "System.Int32");

        var result = AssemblyTools.FindCallers(
            index,
            echoOpen.Module.ModuleVersionId.ToString("D"),
            $"0x{echoOpen.MetadataToken:X8}",
            methodSpecModuleVersionId: callInt.Module.ModuleVersionId.ToString("D"),
            methodSpecMetadataToken: $"0x{specToken:X8}");

        result.IsError.Should().BeFalse(result.Summary);
        var tokens = result.Data!.Callers.Select(c => c.MetadataToken).ToList();
        tokens.Should().Contain(callInt.MetadataToken);
        tokens.Should().NotContain(callStr.MetadataToken);
    }

    // ---- #13: type-level generic filtering in find_callers -------------------------------

    [Fact]
    public void Find_callers_with_type_arg_int_narrows_to_box_int_callers()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);
        index.Load(SampleConsumerPath);

        // Box<T>.ctor
        var libMvid = typeof(SampleLib.Box<int>).Assembly.ManifestModule.ModuleVersionId;
        var find = index.FindMethod(libMvid, new FindMethodQuery("^.ctor$"));
        find.IsSuccess.Should().BeTrue();
        var boxCtor = find.Page!.Matches.First(m => m.TypeFullName == "SampleLib.Box`1");

        var runBoxInt = MethodOf(typeof(SampleConsumer.ConsumerService), "RunBox");
        var runBoxString = MethodOf(typeof(SampleConsumer.ConsumerService), "RunBoxString");

        var result = AssemblyTools.FindCallers(
            index,
            libMvid.ToString("D"),
            $"0x{boxCtor.MetadataToken:X8}",
            genericTypeArguments: ["System.Int32"]);

        result.IsError.Should().BeFalse(result.Summary);
        var tokens = result.Data!.Callers.Select(c => c.MetadataToken).ToList();
        tokens.Should().Contain(runBoxInt.MetadataToken);
        tokens.Should().NotContain(runBoxString.MetadataToken);
    }

    [Fact]
    public void Find_callers_with_type_arg_string_narrows_to_box_string_callers()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);
        index.Load(SampleConsumerPath);

        var libMvid = typeof(SampleLib.Box<int>).Assembly.ManifestModule.ModuleVersionId;
        var find = index.FindMethod(libMvid, new FindMethodQuery("^.ctor$"));
        var boxCtor = find.Page!.Matches.First(m => m.TypeFullName == "SampleLib.Box`1");

        var runBoxInt = MethodOf(typeof(SampleConsumer.ConsumerService), "RunBox");
        var runBoxString = MethodOf(typeof(SampleConsumer.ConsumerService), "RunBoxString");

        var result = AssemblyTools.FindCallers(
            index,
            libMvid.ToString("D"),
            $"0x{boxCtor.MetadataToken:X8}",
            genericTypeArguments: ["System.String"]);

        result.IsError.Should().BeFalse(result.Summary);
        var tokens = result.Data!.Callers.Select(c => c.MetadataToken).ToList();
        tokens.Should().Contain(runBoxString.MetadataToken);
        tokens.Should().NotContain(runBoxInt.MetadataToken);
    }

    // ---- #12: Resolve validates methodSpec.Method targets the requested MethodDef --------

    [Fact]
    public void Get_method_with_methodSpec_pointing_at_wrong_method_yields_mismatch()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);
        index.Load(SampleConsumerPath);

        // Spec found in CallEchoOfInt points at Echo<int>; request Map<TIn,TOut> identity instead.
        var callInt = MethodOf(typeof(SampleConsumer.ConsumerService), "CallEchoOfInt");
        var specToken = FindMethodSpecToken(index, callInt, "System.Int32");
        var mapOpen = MethodOf(typeof(SampleLib.OrderService), "Map");

        var result = AssemblyTools.GetMethod(
            index,
            mapOpen.Module.ModuleVersionId.ToString("D"),
            $"0x{mapOpen.MetadataToken:X8}",
            methodSpecModuleVersionId: callInt.Module.ModuleVersionId.ToString("D"),
            methodSpecMetadataToken: $"0x{specToken:X8}");

        result.IsError.Should().BeTrue();
        result.Error!.Kind.Should().Be(ErrorKinds.GenericInstantiationMismatch);
    }

    // ---- #16: methodSpec module not loaded + explicit args ⇒ fallback to explicit -------

    [Fact]
    public void Get_method_with_unloaded_methodSpec_module_and_explicit_args_falls_back()
    {
        using var index = new MetadataIndex();
        index.Load(SampleLibPath);
        // Intentionally do NOT load SampleConsumer.

        var echoOpen = MethodOf(typeof(SampleLib.OrderService), "Echo");
        var bogusMvid = Guid.NewGuid();

        var result = AssemblyTools.GetMethod(
            index,
            echoOpen.Module.ModuleVersionId.ToString("D"),
            $"0x{echoOpen.MetadataToken:X8}",
            genericMethodArguments: ["System.Int32"],
            methodSpecModuleVersionId: bogusMvid.ToString("D"),
            methodSpecMetadataToken: "0x2B000001");

        result.IsError.Should().BeFalse(result.Summary);
        result.Data!.Signature.Should().Contain("System.Int32");
    }

    [Fact]
    public void Get_method_with_unloaded_methodSpec_module_and_no_explicit_args_yields_module_not_found()
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
}
