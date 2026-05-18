using System.Diagnostics;
using Spike;
using Spike.Adapters;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: Spike <path-to-SampleLib.dll>");
    return 1;
}

string path = args[0];
if (!File.Exists(path))
{
    Console.Error.WriteLine($"file not found: {path}");
    return 1;
}

const int warmup = 3;
const int iters = 50;

Console.WriteLine($"# Metadata library spike\n");
Console.WriteLine($"Fixture: `{path}`  ({new FileInfo(path).Length} bytes)\n");

var rows = new List<Row>();
foreach (var make in new Func<IMetadataAdapter>[]
{
    () => new CecilAdapter(path),
    () => new AsmResolverAdapter(path),
    () => new SrmAdapter(path),
})
{
    using var probe = make();
    Console.WriteLine($"## {probe.Name}\n");
    Console.WriteLine($"- MVID: `{probe.Mvid}`");

    // Warmup
    for (int i = 0; i < warmup; i++) using (var a = make()) { _ = a.ListMethods(); }

    // Cold open + list
    long openTicks = 0, listTicks = 0;
    int methodCount = 0;
    long allocStart = GC.GetTotalAllocatedBytes(true);
    for (int i = 0; i < iters; i++)
    {
        var sw = Stopwatch.StartNew();
        var a = make();
        sw.Stop(); openTicks += sw.ElapsedTicks;

        sw.Restart();
        var ms = a.ListMethods();
        sw.Stop(); listTicks += sw.ElapsedTicks;
        methodCount = ms.Count;
        a.Dispose();
    }
    long allocEnd = GC.GetTotalAllocatedBytes(true);

    double openMs = TicksToMs(openTicks) / iters;
    double listMs = TicksToMs(listTicks) / iters;
    long allocPerRun = (allocEnd - allocStart) / iters;
    Console.WriteLine($"- Methods listed: **{methodCount}**");
    Console.WriteLine($"- Open avg: **{openMs:F3} ms**");
    Console.WriteLine($"- ListMethods avg: **{listMs:F3} ms**");
    Console.WriteLine($"- Alloc per (open+list): **{allocPerRun:N0} bytes**");

    // Resident footprint (10 handles)
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    long memBefore = GC.GetTotalMemory(true);
    var keep = new List<IMetadataAdapter>();
    for (int i = 0; i < 10; i++) { var a = make(); _ = a.ListMethods(); keep.Add(a); }
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    long memAfter = GC.GetTotalMemory(true);
    Console.WriteLine($"- Resident (10 handles, after GC): **{(memAfter - memBefore):N0} bytes**");
    foreach (var a in keep) a.Dispose();

    // IL summary on the Process(int) method — find by name+arity hint
    using var probe2 = make();
    var processToken = FindProcessIntToken(probe2);
    if (processToken != 0)
    {
        var il = probe2.GetIlBytes(processToken);
        var sum = probe2.ScanIl(processToken);
        Console.WriteLine($"- `Process(int)` token: `0x{processToken:X8}`");
        Console.WriteLine($"  - IL pseudo-size (opcode bytes): {il.Length}");
        Console.WriteLine($"  - Outbound calls: {sum.OutboundCallTokens.Count} → [{string.Join(", ", sum.OutboundCallTokens.Select(t => $"0x{t:X8}"))}]");
        Console.WriteLine($"  - Field refs:     {sum.FieldRefTokens.Count} → [{string.Join(", ", sum.FieldRefTokens.Select(t => $"0x{t:X8}"))}]");
        Console.WriteLine($"  - Type refs:      {sum.TypeRefTokens.Count} → [{string.Join(", ", sum.TypeRefTokens.Select(t => $"0x{t:X8}"))}]");
        Console.WriteLine($"  - String lits:    {sum.StringLiterals.Count} → [{string.Join(" | ", sum.StringLiterals)}]");
    }
    Console.WriteLine();

    rows.Add(new Row(probe.Name, methodCount, openMs, listMs, allocPerRun, memAfter - memBefore));
}

Console.WriteLine("## Summary\n");
Console.WriteLine("| Library | Open (ms) | List (ms) | Alloc (B) | Resident×10 (B) |");
Console.WriteLine("|---|---:|---:|---:|---:|");
foreach (var r in rows)
    Console.WriteLine($"| {r.Name} | {r.OpenMs:F3} | {r.ListMs:F3} | {r.AllocPerRun:N0} | {r.Resident:N0} |");

return 0;

static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

static int FindProcessIntToken(IMetadataAdapter a)
{
    foreach (var m in a.ListMethods())
        if (m.TypeFullName == "SampleLib.OrderService" && m.MethodName == "Process" &&
            (m.Signature.Contains("Int32") || m.Signature.Contains("(int)")))
            return m.MetadataToken;
    return 0;
}

internal readonly record struct Row(string Name, int MethodCount, double OpenMs, double ListMs, long AllocPerRun, long Resident);
