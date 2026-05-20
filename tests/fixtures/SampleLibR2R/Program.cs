// Trivial entrypoint so SDK accepts OutputType=Exe. We never actually run
// this — the goal is to crossgen2 the published .dll for the R2R test fixture.
namespace SampleLibR2R;

internal static class Program
{
    private static int Main()
    {
        // Touch a type from Sample.cs so trimming (off here, but defensive)
        // doesn't drop methods the R2R tests need. We don't actually need to
        // construct OrderService — instantiating an AnnotatedService is enough.
        var ann = new SampleLib.AnnotatedService();
        return ann.GetHashCode();
    }
}
