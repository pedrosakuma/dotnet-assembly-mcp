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
