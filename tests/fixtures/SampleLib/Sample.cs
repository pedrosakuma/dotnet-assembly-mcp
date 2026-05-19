using System.ComponentModel;
using SampleLib;

[assembly: FixtureMarker("sample-assembly", Category = "fixture")]

namespace SampleLib;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Parameter | AttributeTargets.Assembly,
    AllowMultiple = true)]
public sealed class FixtureMarkerAttribute : Attribute
{
    public FixtureMarkerAttribute(string name) { Name = name; }
    public FixtureMarkerAttribute(string name, int order) { Name = name; Order = order; }
    public FixtureMarkerAttribute(string[] tags) { Name = "tagged"; Tags = tags; }
    public string Name { get; }
    public int Order { get; init; }
    public string Category { get; set; } = "default";
    public string[]? Tags { get; }
}

[FixtureMarker("hello", 1)]
[FixtureMarker("world", Category = "greeting")]
public class AnnotatedService
{
    [FixtureMarker(new[] { "tagA", "tagB" })]
    [Description("Marked-up method for the list_attributes fixture.")]
    public int Annotated([FixtureMarker("p0")] int x) => x;
}

public class OrderService
{
    private readonly ILogger _logger;
    private static int _counter;

    public OrderService(ILogger logger) => _logger = logger;

    public int Process(int orderId)
    {
        _counter++;
        _logger.Log($"processing order {orderId}");
        try
        {
            return Compute(orderId);
        }
        catch (InvalidOperationException ex)
        {
            _logger.Log($"failed: {ex.Message}");
            throw;
        }
    }

    public string Process(string orderId) => $"order:{orderId}";

    private int Compute(int x)
    {
        var box = new Box<int>(x);
        return box.Value * 2;
    }

    public async Task<int> ProcessAsync(int orderId)
    {
        await Task.Yield();
        return Process(orderId);
    }

    public IEnumerable<int> Enumerate(int n)
    {
        for (int i = 0; i < n; i++)
            yield return i * i;
    }

    public Func<int, int> MakeAdder(int delta) => x => x + delta + _counter;

    public T Echo<T>(T value) => value;

    public TOut Map<TIn, TOut>(TIn input, Func<TIn, TOut> selector) => selector(input);
}

public sealed class Box<T>
{
    public T Value { get; }
    public Box(T value) => Value = value;
}

public interface ILogger
{
    void Log(string message);
}

/// <summary>Hosts a nested type used by xref tests to exercise the Outer+Inner naming path.</summary>
public class NestingHost
{
    public class Inner
    {
        public int Ping(int x) => x + 1;
    }
}
