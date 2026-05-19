using SampleLib;

namespace SampleConsumer;

/// <summary>
/// Cross-module fixture: every method here emits a MemberRef call into SampleLib so the
/// xref index has something to discover when the two assemblies are both loaded.
/// </summary>
public sealed class ConsumerService
{
    private readonly OrderService _orders;

    public ConsumerService(OrderService orders) => _orders = orders;

    public int RunInt(int id) => _orders.Process(id);

    public string RunString(string id) => _orders.Process(id);

    public int RunBox(int value)
    {
        var box = new Box<int>(value);
        return box.Value;
    }
}

public sealed class NullLogger : ILogger
{
    public void Log(string message) { /* discard */ }
}

/// <summary>
/// Exercises two cross-module xref edge cases:
/// 1) Calling a method on a nested type (LibNS.NestingHost+Inner.Ping).
/// 2) Calling the same target twice from one caller with a non-call statement in between,
///    so the per-method outbound dedup must not collapse only adjacent emissions.
/// </summary>
public sealed class NestedCaller
{
    public int CallInnerPingTwice(int x)
    {
        var inner = new NestingHost.Inner();
        var first = inner.Ping(x);
        var label = first.ToString(System.Globalization.CultureInfo.InvariantCulture); // breaks adjacency
        var second = inner.Ping(first);  // duplicate target, non-adjacent emission
        return second + label.Length;
    }
}
