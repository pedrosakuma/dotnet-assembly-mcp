using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Metadata;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

public class GenericTypeNameTests
{
    [Theory]
    [InlineData("System.Int32")]
    [InlineData("System.Collections.Generic.List`1")]
    [InlineData("System.Collections.Generic.Dictionary`2[System.Int32,System.String]")]
    [InlineData("System.Collections.Generic.Dictionary`2[System.Int32,System.Collections.Generic.List`1[System.String]]")]
    [InlineData("System.Int32[]")]
    [InlineData("System.Int32[,]")]
    [InlineData("System.Int32[,,]")]
    [InlineData("System.Int32[*]")]
    [InlineData("System.Int32&")]
    [InlineData("System.Int32*")]
    [InlineData("SampleLib.NestingHost+Inner")]
    [InlineData("System.Collections.Generic.List`1[SampleLib.NestingHost+Inner]")]
    [InlineData("System.Collections.Generic.List`1[System.Int32][]")]
    public void Round_trips_canonical_format(string input)
    {
        Assert.True(GenericTypeName.TryParse(input, out var node, out var errKind, out var errMsg),
            $"parse failed for {input}: {errKind}/{errMsg}");
        Assert.Equal(input, node!.Format());
    }

    [Fact]
    public void Nested_type_chain_separates_namespace_from_nested_names()
    {
        Assert.True(GenericTypeName.TryParse("Outer.NS.Top+Middle+Inner", out var node, out _, out _));
        var named = Assert.IsType<GenericTypeName.Named>(node);
        Assert.Equal(["Outer", "NS"], (IEnumerable<string>)named.NamespaceSegments);
        Assert.Equal(["Top", "Middle", "Inner"], (IEnumerable<string>)named.NameChain);
        Assert.Equal("Outer.NS.Top+Middle+Inner", named.ClrFullName);
    }

    [Fact]
    public void Generic_args_are_parsed_recursively()
    {
        Assert.True(GenericTypeName.TryParse(
            "System.Collections.Generic.Dictionary`2[System.Int32,System.String]",
            out var node, out _, out _));
        var named = Assert.IsType<GenericTypeName.Named>(node);
        Assert.Equal(2, named.TypeArguments.Length);
        Assert.IsType<GenericTypeName.Named>(named.TypeArguments[0]);
    }

    [Fact]
    public void Open_type_parameter_is_rejected()
    {
        Assert.False(GenericTypeName.TryParse("!0", out _, out var errKind, out _));
        Assert.Equal(ErrorKinds.GenericInstantiationOpen, errKind);
        Assert.False(GenericTypeName.TryParse("!!0", out _, out errKind, out _));
        Assert.Equal(ErrorKinds.GenericInstantiationOpen, errKind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("List`1[")]
    [InlineData("List`1[System.Int32")]
    [InlineData("List`1[System.Int32,]")]
    [InlineData("Foo[a,b,c")]
    [InlineData("Foo.")]
    [InlineData("Foo+")]
    public void Malformed_input_is_rejected(string input)
    {
        Assert.False(GenericTypeName.TryParse(input, out _, out var kind, out _));
        Assert.NotNull(kind);
    }

    [Fact]
    public void Trailing_garbage_is_rejected()
    {
        Assert.False(GenericTypeName.TryParse("System.Int32[]extra", out _, out var kind, out _));
        Assert.Equal(ErrorKinds.InvalidArgument, kind);
    }

    [Fact]
    public void Array_of_generic_keeps_element_distinct_from_args()
    {
        Assert.True(GenericTypeName.TryParse("System.Collections.Generic.List`1[System.Int32][]",
            out var node, out _, out _));
        var sz = Assert.IsType<GenericTypeName.SzArray>(node);
        var element = Assert.IsType<GenericTypeName.Named>(sz.Element);
        Assert.Single(element.TypeArguments);
    }

    [Fact]
    public void Md_array_rank_is_preserved()
    {
        Assert.True(GenericTypeName.TryParse("System.Int32[,,,]", out var node, out _, out _));
        var md = Assert.IsType<GenericTypeName.MdArray>(node);
        Assert.Equal(4, md.Rank);
        Assert.False(md.LowerBoundNonZero);
    }

    [Fact]
    public void Md_array_rank_one_with_non_zero_lower_bound()
    {
        Assert.True(GenericTypeName.TryParse("System.Int32[*]", out var node, out _, out _));
        var md = Assert.IsType<GenericTypeName.MdArray>(node);
        Assert.Equal(1, md.Rank);
        Assert.True(md.LowerBoundNonZero);
    }

    [Fact]
    public void ClrFullName_normalizes_root_namespaceless_type()
    {
        Assert.True(GenericTypeName.TryParse("Outer+Inner", out var node, out _, out _));
        var named = Assert.IsType<GenericTypeName.Named>(node);
        Assert.Empty(named.NamespaceSegments);
        Assert.Equal("Outer+Inner", named.ClrFullName);
    }
}
