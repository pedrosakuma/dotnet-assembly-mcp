using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using DotnetAssemblyMcp.Core.Identity;
using DotnetAssemblyMcp.Core.Metadata;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Issue #86 — <see cref="MetadataIndex.ScanIl"/> must classify <c>MemberReference</c>
/// rows (table 0x0A) by signature kind: a cross-module <c>ldsfld</c> of a field defined
/// in another assembly emits a field MemberRef and must land in <c>Fields</c> with
/// <see cref="IlSymbolKind.FieldMemberRef"/>, not leak into <c>Calls</c>. Likewise a
/// cross-module method <c>call</c> must surface as <see cref="IlSymbolKind.MethodMemberRef"/>.
/// </summary>
public sealed class IlScanMemberRefClassificationTests
{
    private static string SampleConsumerPath =>
        typeof(SampleConsumer.ConsumerService).Assembly.Location;

    private static MethodIdentity IdentityOf(MethodInfo mi) =>
        new(mi.Module.ModuleVersionId, mi.MetadataToken);

    private static MethodInfo MethodOf(Type t, string name) =>
        t.GetMethod(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)!;

    [Fact]
    public void Cross_module_static_field_read_lands_in_fields_bucket_with_FieldMemberRef_kind()
    {
        using var index = new MetadataIndex();
        index.Load(SampleConsumerPath);

        // SnapshotCounter is a one-liner that does `return CounterFixture.Count;` where
        // CounterFixture lives in SampleLib — the compiler emits ldsfld against a field
        // MemberRef. Pre-#86 this row was misclassified as a call (the wire bucket was
        // `Calls`, which made it invisible to find_member_references with the field handle).
        var snapshotCounter = MethodOf(typeof(SampleConsumer.CrossModuleFieldAndPropertyConsumer), "SnapshotCounter");

        var scan = index.ScanIl(IdentityOf(snapshotCounter)).Scan!;

        scan.Fields.Should().Contain(
            f => f.Kind == IlSymbolKind.FieldMemberRef && f.Display.Contains("Count"),
            because: "the cross-module ldsfld of CounterFixture.Count must surface as a FieldMemberRef in the Fields bucket");

        scan.Calls.Should().NotContain(
            c => c.Display.Contains("CounterFixture.Count"),
            because: "field MemberRefs must not leak into the Calls bucket (issue #86)");
    }

    [Fact]
    public void Cross_module_instance_method_call_is_classified_as_MethodMemberRef()
    {
        using var index = new MetadataIndex();
        index.Load(SampleConsumerPath);

        // CallEchoOfInt issues a call to OrderService.Echo<int> in SampleLib — the IL emits
        // a method MemberRef (or MethodSpec wrapping one).
        var callEcho = MethodOf(typeof(SampleConsumer.ConsumerService), "CallEchoOfInt");

        var scan = index.ScanIl(IdentityOf(callEcho)).Scan!;

        scan.Calls.Should().Contain(
            c => (c.Kind == IlSymbolKind.MethodMemberRef || c.Kind == IlSymbolKind.MethodSpec)
                 && c.Display.Contains("Echo"),
            because: "cross-module method calls must surface as MethodMemberRef (or MethodSpec when generic-instantiated) in the Calls bucket");

        scan.Fields.Should().NotContain(
            f => f.Display.Contains("Echo"),
            because: "method MemberRefs must not leak into the Fields bucket");
    }

    [Fact]
    public void Ldtoken_of_cross_module_field_memberref_is_bucketed_into_fields_via_AddTokenRef()
    {
        // This is the test that actually exercises the production fix: `ldtoken` lowers to the
        // `InlineTok` opcode and routes through MetadataIndex.AddTokenRef, which pre-#86
        // mis-bucketed every MemberRef token (table 0x0A) as a call regardless of whether
        // the underlying signature was a method or a field. ldsfld/ldfld go through
        // InlineField and were never affected, so the other tests in this file can't catch
        // a regression in AddTokenRef. We synthesise a tiny assembly via Reflection.Emit
        // whose body is `ldtoken [SampleLib]CounterFixture::Count; pop; ret`.

        var sampleLibPath = typeof(SampleLib.CounterFixture).Assembly.Location;
        var tempPath = Path.Combine(Path.GetTempPath(), $"IlScanMemberRefTok_{Guid.NewGuid():N}.dll");

        try
        {
            EmitAssemblyWithLdtokenOfCrossModuleField(tempPath, sampleLibPath);

            using var index = new MetadataIndex();
            index.Load(tempPath);

            // Look up the synthesised method's MVID + token via PEReader (no managed Assembly
            // load — keeps us aligned with the "never Assembly.Load" rule in AGENTS.md).
            using var pe = new PEReader(File.OpenRead(tempPath));
            var reader = pe.GetMetadataReader();
            var mvid = reader.GetGuid(reader.GetModuleDefinition().Mvid);
            var methodHandle = reader.MethodDefinitions
                .Single(h => reader.GetString(reader.GetMethodDefinition(h).Name) == "TokenField");
            var token = MetadataTokens.GetToken(methodHandle);

            var scan = index.ScanIl(new MethodIdentity(mvid, token)).Scan!;

            scan.Fields.Should().Contain(
                f => f.Kind == IlSymbolKind.FieldMemberRef && f.Display.Contains("Count"),
                because: "ldtoken of a cross-module field MemberRef must route through AddTokenRef into the Fields bucket with FieldMemberRef kind (issue #86)");

            scan.Calls.Should().NotContain(
                f => f.Display.Contains("CounterFixture") && f.Display.Contains("Count"),
                because: "AddTokenRef must not put field MemberRefs into the Calls bucket");
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* best-effort cleanup */ }
        }
    }

    // Emits: public static class Probe { public static void TokenField() {
    //     ldtoken [SampleLib]SampleLib.CounterFixture::Count
    //     pop
    //     ret
    // } }
    private static void EmitAssemblyWithLdtokenOfCrossModuleField(string outputPath, string sampleLibPath)
    {
        // Use PersistedAssemblyBuilder (added in .NET 9) so we can write a real PE to disk.
        var coreAsmName = typeof(object).Assembly.GetName();
        var ab = new PersistedAssemblyBuilder(new AssemblyName("IlScanMemberRefProbe"), typeof(object).Assembly);
        var mod = ab.DefineDynamicModule("IlScanMemberRefProbe");
        var tb = mod.DefineType("Probe", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        var mb = tb.DefineMethod("TokenField", MethodAttributes.Public | MethodAttributes.Static, typeof(void), Type.EmptyTypes);

        // Resolve the cross-module field via the same managed assembly the runtime already
        // loaded for SampleLib (its location is sampleLibPath). Using Assembly.LoadFrom here
        // is acceptable in a test context — it doesn't violate the production rule.
        var sampleLib = Assembly.LoadFrom(sampleLibPath);
        var counterFixtureType = sampleLib.GetType("SampleLib.CounterFixture", throwOnError: true)!;
        var countField = counterFixtureType.GetField("Count", BindingFlags.Public | BindingFlags.Static)!;

        var il = mb.GetILGenerator();
        il.Emit(OpCodes.Ldtoken, countField);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ret);

        tb.CreateType();
        ab.Save(outputPath);
    }
}
