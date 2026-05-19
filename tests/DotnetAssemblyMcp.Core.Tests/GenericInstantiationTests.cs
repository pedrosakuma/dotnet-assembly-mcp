using DotnetAssemblyMcp.Core;
using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Identity;
using DotnetAssemblyMcp.Core.Metadata;
using DotnetAssemblyMcp.Server.Tools;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// End-to-end tests for the §3.5 generic instantiation handoff: <c>get_method</c> with the
/// optional <c>genericTypeArguments</c> / <c>genericMethodArguments</c> parameters returns
/// a <em>closed</em> signature view, validating that each type-arg leaf resolves in some
/// loaded module and substituting the canonical name for the open generic parameter.
/// </summary>
public sealed class GenericInstantiationTests
{
    private static string SampleLibPath => typeof(SampleLib.OrderService).Assembly.Location;

    private static (MetadataIndex Index, Guid Mvid, int Token, int TypeArity, int MethodArity) FindOpen(string namePattern)
    {
        var index = new MetadataIndex();
        var load = index.Load(SampleLibPath);
        load.IsSuccess.Should().BeTrue();
        var mvid = load.Module!.ModuleVersionId;
        var find = index.FindMethod(mvid, new FindMethodQuery(namePattern));
        find.IsSuccess.Should().BeTrue();
        find.Page!.Matches.Should().NotBeEmpty();
        var token = find.Page.Matches[0].MetadataToken;
        // Open signature (no substitution) should still contain a generic param marker.
        var open = index.Resolve(new MethodIdentity(mvid, token));
        open.IsSuccess.Should().BeTrue();
        // Read arities from metadata via a second resolve trick: pass a single arg and check the error.
        return (index, mvid, token, TypeArityOf(index, mvid, token), MethodArityOf(index, mvid, token));
    }

    private static int MethodArityOf(MetadataIndex index, Guid mvid, int token)
    {
        // Probe by feeding one method arg and reading the InvalidArgument count from the error message,
        // OR by parsing the open signature — but we have direct access via index.Resolve only. Easier:
        // attempt with no args and let the caller drive the arities separately. For these tests we know
        // them by construction (Echo<T> -> 1; Map<TIn,TOut> -> 2; Box<T>.ctor -> 0).
        return 0;
    }

    private static int TypeArityOf(MetadataIndex index, Guid mvid, int token) => 0;

    [Fact]
    public void Method_level_generic_substitutes_type_argument_in_signature()
    {
        var (index, mvid, token, _, _) = FindOpen("^Echo$");

        var result = AssemblyTools.GetMethod(
            index,
            mvid.ToString("D"),
            $"0x{token:X8}",
            genericMethodArguments: ["System.Int32"]);

        result.IsError.Should().BeFalse(result.Summary);
        result.Data!.Signature.Should().Contain("System.Int32");
        result.Data.Signature.Should().NotContain("!!0");
        index.Dispose();
    }

    [Fact]
    public void Type_level_generic_substitutes_type_argument_on_ctor()
    {
        // Box<T>(T value) — ctor has type-level generic but no method-level generics.
        var (index, mvid, _, _, _) = FindOpen("^.ctor$");
        // Locate Box<T>.ctor specifically (there are multiple .ctors across types).
        var find = index.FindMethod(mvid, new FindMethodQuery("^.ctor$"));
        find.IsSuccess.Should().BeTrue();
        var boxCtor = find.Page!.Matches
            .First(m => m.TypeFullName == "SampleLib.Box`1");

        var result = AssemblyTools.GetMethod(
            index,
            mvid.ToString("D"),
            $"0x{boxCtor.MetadataToken:X8}",
            genericTypeArguments: ["System.String"]);

        result.IsError.Should().BeFalse(result.Summary);
        result.Data!.Signature.Should().Contain("System.String");
        result.Data.Signature.Should().NotContain("!0");
        index.Dispose();
    }

    [Fact]
    public void Unresolvable_type_argument_returns_generic_instantiation_unresolvable()
    {
        var (index, mvid, token, _, _) = FindOpen("^Echo$");

        var result = AssemblyTools.GetMethod(
            index,
            mvid.ToString("D"),
            $"0x{token:X8}",
            genericMethodArguments: ["Imaginary.Type.Nope"]);

        result.IsError.Should().BeTrue();
        result.Error!.Kind.Should().Be(ErrorKinds.GenericInstantiationUnresolvable);
        index.Dispose();
    }

    [Fact]
    public void Wrong_arity_returns_invalid_argument()
    {
        var (index, mvid, token, _, _) = FindOpen("^Echo$");

        var result = AssemblyTools.GetMethod(
            index,
            mvid.ToString("D"),
            $"0x{token:X8}",
            genericMethodArguments: ["System.Int32", "System.String"]); // Echo<T> expects 1

        result.IsError.Should().BeTrue();
        result.Error!.Kind.Should().Be(ErrorKinds.InvalidArgument);
        index.Dispose();
    }

    [Fact]
    public void Open_type_parameter_in_args_is_rejected()
    {
        var (index, mvid, token, _, _) = FindOpen("^Echo$");

        var result = AssemblyTools.GetMethod(
            index,
            mvid.ToString("D"),
            $"0x{token:X8}",
            genericMethodArguments: ["!0"]);

        result.IsError.Should().BeTrue();
        result.Error!.Kind.Should().Be(ErrorKinds.GenericInstantiationOpen);
        index.Dispose();
    }

    [Fact]
    public void Open_signature_is_returned_when_no_generic_args_supplied()
    {
        var (index, mvid, token, _, _) = FindOpen("^Echo$");

        var result = AssemblyTools.GetMethod(
            index,
            mvid.ToString("D"),
            $"0x{token:X8}");

        result.IsError.Should().BeFalse(result.Summary);
        // Open Echo<T>(T) signature uses !!0 for the method-level generic parameter.
        result.Data!.Signature.Should().Contain("!!0");
        index.Dispose();
    }

    [Fact]
    public void Method_with_two_type_parameters_substitutes_both()
    {
        var (index, mvid, token, _, _) = FindOpen("^Map$");

        var result = AssemblyTools.GetMethod(
            index,
            mvid.ToString("D"),
            $"0x{token:X8}",
            genericMethodArguments: ["System.Int32", "System.String"]);

        result.IsError.Should().BeFalse(result.Summary);
        result.Data!.Signature.Should().Contain("System.Int32");
        result.Data.Signature.Should().Contain("System.String");
        result.Data.Signature.Should().NotContain("!!0");
        result.Data.Signature.Should().NotContain("!!1");
        index.Dispose();
    }
}
