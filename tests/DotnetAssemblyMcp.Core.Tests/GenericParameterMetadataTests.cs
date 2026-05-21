using DotnetAssemblyMcp.Core.Metadata;
using DotnetAssemblyMcp.Server.Tools;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Issue #103 — verifies <c>TypeSummary.GenericParameters</c> and <c>MethodSummary.GenericParameters</c>
/// are populated with name, index, variance, special-constraint flags (class / struct / new()) and
/// base-type / interface type constraints. Non-generic summaries leave the field null.
/// </summary>
public sealed class GenericParameterMetadataTests
{
    private static (MetadataIndex Index, System.Guid Mvid) LoadSampleLib()
    {
        var index = new MetadataIndex();
        var loaded = index.Load(typeof(SampleLib.OrderService).Assembly.Location);
        return (index, loaded.Module!.ModuleVersionId);
    }

    [Fact]
    public void TypeSummary_decodes_class_new_and_struct_constraints_with_interface_constraint()
    {
        var (index, mvid) = LoadSampleLib();
        using (index)
        {
            int token = typeof(SampleLib.ConstrainedRepository<,>).MetadataToken;
            var result = AssemblyTools.GetType(index, typeHandle: $"t:{mvid:D}:0x{token:X8}");
            result.IsError.Should().BeFalse(result.Summary);

            var gps = result.Data!.GenericParameters;
            gps.Should().NotBeNull();
            gps.Should().HaveCount(2);

            var t = gps![0];
            t.Index.Should().Be(0);
            t.Name.Should().Be("T");
            t.Variance.Should().Be("None");
            t.IsReferenceType.Should().BeTrue("where T : class");
            t.IsValueType.Should().BeFalse();
            t.HasDefaultConstructor.Should().BeTrue("where T : new()");
            t.TypeConstraints.Should().NotBeNull();
            t.TypeConstraints!.Should().Contain(c => c.FullName == "System.IDisposable",
                because: "where T : ..., IDisposable, ...");
            t.TypeConstraints!.Single(c => c.FullName == "System.IDisposable")
                .AssemblyName.Should().NotBeNullOrEmpty(
                    "TypeRef constraints must surface their declaring assembly");

            var tkey = gps![1];
            tkey.Index.Should().Be(1);
            tkey.Name.Should().Be("TKey");
            tkey.IsValueType.Should().BeTrue("where TKey : struct");
            tkey.HasDefaultConstructor.Should().BeTrue(
                "the struct constraint implicitly carries DefaultConstructorConstraint at the IL level");
        }
    }

    [Fact]
    public void MethodSummary_decodes_method_level_generic_constraints()
    {
        var (index, mvid) = LoadSampleLib();
        using (index)
        {
            int token = typeof(SampleLib.ConstrainedRepository<,>)
                .GetMethod("Echo")!.MetadataToken;
            var result = AssemblyTools.GetMethod(index, mvid.ToString("D"), $"0x{token:X8}");
            result.IsError.Should().BeFalse(result.Summary);

            var gps = result.Data!.GenericParameters;
            gps.Should().NotBeNull();
            gps.Should().HaveCount(1);

            var tItem = gps![0];
            tItem.Index.Should().Be(0);
            tItem.Name.Should().Be("TItem");
            tItem.TypeConstraints.Should().NotBeNull();
            tItem.TypeConstraints!.Should().Contain(c => c.FullName.Contains("IEquatable"),
                because: "where TItem : IEquatable<TItem>");
            tItem.TypeConstraints!.Single(c => c.FullName.Contains("IEquatable"))
                .AssemblyName.Should().NotBeNullOrEmpty(
                    "TypeSpec generic-instantiation constraints must surface the underlying assembly");
        }
    }

    [Fact]
    public void TypeSummary_decodes_variance_on_interface_generic_parameters()
    {
        var (index, mvid) = LoadSampleLib();
        using (index)
        {
            int token = typeof(SampleLib.IVariantPipe<,>).MetadataToken;
            var result = AssemblyTools.GetType(index, typeHandle: $"t:{mvid:D}:0x{token:X8}");
            result.IsError.Should().BeFalse(result.Summary);

            var gps = result.Data!.GenericParameters;
            gps.Should().NotBeNull();
            gps.Should().HaveCount(2);
            gps![0].Variance.Should().Be("Contravariant", "in TIn");
            gps![1].Variance.Should().Be("Covariant", "out TOut");
        }
    }

    [Fact]
    public void NonGeneric_type_leaves_GenericParameters_null()
    {
        var (index, mvid) = LoadSampleLib();
        using (index)
        {
            int token = typeof(SampleLib.OrderService).MetadataToken;
            var result = AssemblyTools.GetType(index, typeHandle: $"t:{mvid:D}:0x{token:X8}");
            result.IsError.Should().BeFalse(result.Summary);
            result.Data!.GenericParameters.Should().BeNull();
        }
    }

    [Fact]
    public void NonGeneric_method_leaves_GenericParameters_null()
    {
        var (index, mvid) = LoadSampleLib();
        using (index)
        {
            int token = typeof(SampleLib.OrderService).GetMethod("Process", new[] { typeof(int) })!.MetadataToken;
            var result = AssemblyTools.GetMethod(index, mvid.ToString("D"), $"0x{token:X8}");
            result.IsError.Should().BeFalse(result.Summary);
            result.Data!.GenericParameters.Should().BeNull();
        }
    }
}
