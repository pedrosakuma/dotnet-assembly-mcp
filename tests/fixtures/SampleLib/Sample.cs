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

/// <summary>
/// Fixture for find_type_references IL-scan coverage. The methods here emit type-bearing IL
/// opcodes (<c>ldtoken</c>, <c>castclass</c>, <c>isinst</c>) whose operand is an InlineType
/// token, so the xref builder can record them as IlOpcode reference sites.
/// </summary>
public static class TypeUsageFixture
{
    public static Type BoxTypeHandle() => typeof(Box<int>);
    public static bool IsAnimal(object o) => o is AnimalBase;
    public static AnimalBase? AsAnimal(object o) => o as AnimalBase;
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
    [FixtureMarker("age-field")]
    public int Age;
#pragma warning restore CA1051

    public CustomerDto(Guid id, string name)
    {
        _id = id;
        Name = name;
    }

    public string Name { get; init; }

    [FixtureMarker("email-property")]
    public string? Email { get; set; }
    public Guid Id => _id;

    [FixtureMarker("on-changed")]
    public event System.EventHandler<string>? Changed;

    public void RaiseChanged(string field) => Changed?.Invoke(this, field);

    [Obsolete("kept for find_attribute_targets fixture")]
    public void LegacyTouch() { }
}

/// <summary>
/// Fixture for find_field_references: a static counter field with deliberate readers, writers,
/// and an address-taker, plus a cross-module-visible reader exercised from SampleConsumer.
/// </summary>
public static class CounterFixture
{
#pragma warning disable CA2211 // public static field is intentional for IL-scan coverage.
    public static int Count;
#pragma warning restore CA2211

    public static int ReadCount() => Count;

    public static void WriteCount(int value) => Count = value;

    public static void Bump()
    {
        Count = Count + 1;
    }

    public static int ReadTwice() => Count + Count;
}

/// <summary>
/// Generic-instantiation fixtures for ListDerivedTypes (issue #67). These exercise the
/// TypeSpec-parent decoding path: a class implementing a closed generic interface or
/// subclassing a closed generic base produces a TypeSpec row that the matcher must walk.
/// </summary>
public interface IRequestHandler<TReq, TResp>
{
    TResp Handle(TReq req);
}

public abstract class Repository<T>
{
    public abstract T? GetById(int id);
}

/// <summary>Same-module TypeSpec → TypeDef edge (CLASS token resolves locally).</summary>
public sealed class IntRepository : Repository<int>
{
    public override int GetById(int id) => id;
}

/// <summary>
/// Fixture for generic-constraints metadata coverage (#103). Combines reference-type, value-type,
/// default-constructor, base-type and interface constraints plus method-level constraints so the
/// decoder can be exercised against every <see cref="System.Reflection.GenericParameterAttributes"/>
/// special flag in a single TypeDef.
/// </summary>
public sealed class ConstrainedRepository<T, TKey>
    where T : class, IDisposable, new()
    where TKey : struct
{
    public T? Find(TKey id) => null;

    public TItem Echo<TItem>(TItem value)
        where TItem : notnull, System.IEquatable<TItem>
        => value;
}

/// <summary>Variance fixture — covariant + contravariant generic parameters on an interface.</summary>
public interface IVariantPipe<in TIn, out TOut>
{
    TOut Transform(TIn input);
}

/// <summary>
/// Fixture for custom-modifier (modreq / modopt) decoding on signatures (#101). Exercises:
/// - <c>in</c> parameter → emits <c>modreq(InAttribute)</c> on a byref.
/// - <c>ref readonly</c> return → emits the same modreq on the return byref.
/// - <c>init</c>-only setter → emits <c>modreq(IsExternalInit)</c> on the setter's return type.
/// - <c>volatile</c> field → emits <c>modreq(IsVolatile)</c> on the field type.
/// </summary>
public sealed class ModifierFixture
{
#pragma warning disable CA1051 // public field intentional — fixture exercises volatile modreq
    public volatile int VolatileCounter;
#pragma warning restore CA1051

    public int InitOnly { get; init; }

    public ref readonly int FirstByIn(in int needle, int[] haystack)
    {
        for (int i = 0; i < haystack.Length; i++)
            if (haystack[i] == needle) return ref haystack[i];
        return ref haystack[0];
    }
}

/// <summary>
/// Fixture for PInvoke metadata coverage on <see cref="MethodSummary.PInvoke"/> (#104).
/// Uses libc / kernel32 entrypoints that exist on the build agent without runtime
/// dispatching — we only need the metadata, the methods are never invoked.
/// </summary>
public static class PInvokeFixture
{
#pragma warning disable CA1401 // P/Invoke method visibility — fixture-only, methods are never invoked
    [System.Runtime.InteropServices.DllImport(
        "libc",
        EntryPoint = "getpid",
        CharSet = System.Runtime.InteropServices.CharSet.Ansi,
        CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl,
        SetLastError = true,
        ExactSpelling = true)]
    public static extern int GetPid();

    [System.Runtime.InteropServices.DllImport(
        "kernel32.dll",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        PreserveSig = false)]
    public static extern int FormatMessageW(uint flags, IntPtr source, uint messageId, uint langId, IntPtr buffer, uint size, IntPtr args);
#pragma warning restore CA1401
}

