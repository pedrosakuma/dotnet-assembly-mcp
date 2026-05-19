using System.ComponentModel;
using SampleLib;

[assembly: FixtureMarker("sample-assembly", Category = "fixture")]

namespace SampleLib;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Parameter | AttributeTargets.Assembly | AttributeTargets.Event | AttributeTargets.Property | AttributeTargets.Field,
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

// Three-level hierarchy + interface implementer used by the type-hierarchy tests.
public interface IAnimal
{
    string Speak();
}

public abstract class AnimalBase : IAnimal
{
    public abstract string Speak();
}

public class Dog : AnimalBase
{
    public override string Speak() => "woof";
}

public sealed class Puppy : Dog
{
    public override string Speak() => "yip";
}

public sealed class ConsoleLogger : ILogger
{
    public void Log(string message) => System.Console.WriteLine(message);
}

// POCO / DTO with a mix of field, property, and event for list_members coverage.
public sealed class CustomerDto
{
    public const string DefaultRegion = "us-east";
    public static readonly int Schema = 1;
    private readonly Guid _id;
#pragma warning disable CA1051 // public field is intentional — exercises list_members field-attribute formatting.
    public int Age;
#pragma warning restore CA1051

    public CustomerDto(Guid id, string name)
    {
        _id = id;
        Name = name;
    }

    public string Name { get; init; }
    public string? Email { get; set; }
    public Guid Id => _id;

    [FixtureMarker("on-changed")]
    public event System.EventHandler<string>? Changed;

    public void RaiseChanged(string field) => Changed?.Invoke(this, field);
}
