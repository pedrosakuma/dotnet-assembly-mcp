namespace SampleLib;

// A deliberately divergent variant of a subset of SampleLib's public surface, used only by the
// diff-assemblies fixtures. Each divergence below targets a specific diff branch:
//   * OrderService.Process(int)        -> return type int  changed to long  (changed member)
//   * OrderService.Process(int,int)    -> new overload                       (added member)
//   * OrderService.Process(string)     -> identical                          (no diff)
//   * (SampleLib's other OrderService members are absent here)               (removed members)
//   * Dog                              -> base AnimalBase changed to object  (shape change)
//   * BrandNewType                     -> only exists here                   (added type)

public class OrderService
{
    public long Process(int orderId) => orderId;

    public string Process(string orderId) => $"order:{orderId}";

    public int Process(int orderId, int retry) => orderId + retry;
}

public class Dog
{
    public string Speak() => "woof";
}

public sealed class BrandNewType
{
    public int Value { get; set; }
}
