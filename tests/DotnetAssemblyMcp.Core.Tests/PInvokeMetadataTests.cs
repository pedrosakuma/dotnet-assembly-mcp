using DotnetAssemblyMcp.Core.Metadata;
using DotnetAssemblyMcp.Server.Tools;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Issue #104 — verifies <c>MethodSummary.PInvoke</c> is populated for methods carrying the
/// <c>PinvokeImpl</c> bit and decodes ModuleRef name, EntryPoint override, CharSet,
/// CallingConvention, ExactSpelling, SetLastError and PreserveSig from <c>MethodImport</c>
/// + <c>MethodImplAttributes</c>. Non-PInvoke methods must leave the field null.
/// </summary>
public sealed class PInvokeMetadataTests
{
    private static (MetadataIndex Index, System.Guid Mvid) LoadSampleLib()
    {
        var index = new MetadataIndex();
        var loaded = index.Load(typeof(SampleLib.OrderService).Assembly.Location);
        return (index, loaded.Module!.ModuleVersionId);
    }

    [Fact]
    public void Decodes_libc_getpid_with_entry_point_override()
    {
        var (index, mvid) = LoadSampleLib();
        using (index)
        {
            int token = typeof(SampleLib.PInvokeFixture).GetMethod("GetPid")!.MetadataToken;
            var result = AssemblyTools.GetMethod(index, mvid.ToString("D"), $"0x{token:X8}");
            result.IsError.Should().BeFalse(result.Summary);

            var pi = result.Data!.PInvoke;
            pi.Should().NotBeNull("PinvokeImpl bit is set on GetPid — PInvoke metadata must be populated");
            pi!.ModuleName.Should().Be("libc");
            pi.EntryPoint.Should().Be("getpid", "DllImport explicitly overrides EntryPoint");
            pi.CharSet.Should().Be("Ansi");
            pi.CallingConvention.Should().Be("Cdecl");
            pi.ExactSpelling.Should().BeTrue();
            pi.SetLastError.Should().BeTrue();
            pi.PreserveSig.Should().BeTrue("PreserveSig defaults to true on DllImport when not set");
        }
    }

    [Fact]
    public void Decodes_kernel32_FormatMessageW_with_PreserveSig_false()
    {
        var (index, mvid) = LoadSampleLib();
        using (index)
        {
            int token = typeof(SampleLib.PInvokeFixture).GetMethod("FormatMessageW")!.MetadataToken;
            var result = AssemblyTools.GetMethod(index, mvid.ToString("D"), $"0x{token:X8}");
            result.IsError.Should().BeFalse(result.Summary);

            var pi = result.Data!.PInvoke;
            pi.Should().NotBeNull();
            pi!.ModuleName.Should().Be("kernel32.dll");
            pi.EntryPoint.Should().Be("FormatMessageW", "no EntryPoint override — falls back to method name");
            pi.CharSet.Should().Be("Unicode");
            pi.PreserveSig.Should().BeFalse("DllImport sets PreserveSig=false explicitly");
        }
    }

    [Fact]
    public void NonPInvoke_method_leaves_PInvoke_null()
    {
        var (index, mvid) = LoadSampleLib();
        using (index)
        {
            int token = typeof(SampleLib.OrderService).GetMethod("Process", new[] { typeof(int) })!.MetadataToken;
            var result = AssemblyTools.GetMethod(index, mvid.ToString("D"), $"0x{token:X8}");
            result.IsError.Should().BeFalse(result.Summary);
            result.Data!.PInvoke.Should().BeNull("OrderService.Process is a normal managed method");
        }
    }
}
