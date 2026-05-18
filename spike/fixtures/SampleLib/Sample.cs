namespace SampleLib;

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
