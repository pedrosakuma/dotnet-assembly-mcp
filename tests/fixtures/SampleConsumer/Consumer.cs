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

    public string RunBoxString(string value)
    {
        var box = new Box<string>(value);
        return box.Value;
    }

    public int CallEchoOfInt(int v) => _orders.Echo(v);

    public string CallEchoOfString(string v) => _orders.Echo(v);

    public string CallEchoOfStringAgain(string v) => _orders.Echo(v);
}

public sealed class NullLogger : ILogger
{
    public void Log(string message) { /* discard */ }

    public string Banner() => "consumer-banner";
}

/// <summary>
/// Cross-module fixtures for find_field_references / find_property_references:
/// one method reads SampleLib.CounterFixture.Count, another writes it, and a property method
/// exercises the CustomerDto.Email getter + setter through SampleLib.
/// </summary>
public static class CrossModuleFieldAndPropertyConsumer
{
    public static int SnapshotCounter() => CounterFixture.Count;

    public static void BumpCounter(int by) => CounterFixture.Count = CounterFixture.Count + by;

    public static string? RoundTripEmail(CustomerDto dto, string value)
    {
        dto.Email = value;
        return dto.Email;
    }
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

/// <summary>
/// Cross-module fixture for find_event_references: subscribes to and unsubscribes from
/// CustomerDto.Changed declared in SampleLib, so the test can prove the xref index
/// resolves event accessors across assemblies.
/// </summary>
public static class CrossModuleEventConsumer
{
    public static void SubscribeChanged(CustomerDto dto, System.EventHandler<string> handler)
    {
        dto.Changed += handler;
    }

    public static void UnsubscribeChanged(CustomerDto dto, System.EventHandler<string> handler)
    {
        dto.Changed -= handler;
    }
}

/// <summary>
/// Cross-module type-hierarchy fixtures used by ListDerivedTypes (issue #61): a class
/// that subclasses SampleLib.AnimalBase and another that implements SampleLib.IAnimal
/// directly. Both reach their parents via TypeRef rows in SampleConsumer.
/// </summary>
public class Wolf : AnimalBase
{
    public override string Speak() => "howl";
}

public sealed class Cub : Wolf
{
    public override string Speak() => "yip";
}

public sealed class Hamster : IAnimal
{
    public string Speak() => "squeak";
}

/// <summary>
/// Cross-module TypeSpec fixtures for ListDerivedTypes (issue #67): each parent is a
/// closed generic instantiation whose open form lives in SampleLib. The child types
/// emit TypeSpec rows that decode to (SampleLib, IRequestHandler`2 / Repository`1)
/// plus the closed args, so the matcher can answer both open and closed queries.
/// </summary>
public sealed class OrderHandler : IRequestHandler<int, string>
{
    public string Handle(int req) => req.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed class UserHandler : IRequestHandler<string, int>
{
    public int Handle(string req) => req.Length;
}

public sealed class UserRepo : Repository<string>
{
    public override string? GetById(int id) => null;
}

/// <summary>
/// Cross-module xref fixture for the modreq / modopt path (#101). Calls
/// <see cref="ModifierFixture.FirstByIn"/> through a MemberRef whose signature carries the
/// InAttribute modreq on both the parameter and the return byref, so the xref matcher must
/// recognise the call site even though the consumer-facing renderer now surfaces modifiers.
/// </summary>
public static class CrossModuleModifierConsumer
{
    public static int CallFirstByIn(ModifierFixture fixture, int needle, int[] haystack)
        => fixture.FirstByIn(in needle, haystack);
}
