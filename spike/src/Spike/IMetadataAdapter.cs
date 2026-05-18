namespace Spike;

public readonly record struct MethodSummary(
    string TypeFullName,
    string MethodName,
    string Signature,
    int IlSize,
    int GenericArity,
    int MetadataToken);

public readonly record struct IlSummary(
    IReadOnlyList<int> OutboundCallTokens,
    IReadOnlyList<int> FieldRefTokens,
    IReadOnlyList<int> TypeRefTokens,
    IReadOnlyList<string> StringLiterals);

public interface IMetadataAdapter : IDisposable
{
    string Name { get; }
    Guid Mvid { get; }
    MethodSummary Resolve(int methodDefToken);
    IReadOnlyList<MethodSummary> ListMethods();
    ReadOnlyMemory<byte> GetIlBytes(int methodDefToken);
    IlSummary ScanIl(int methodDefToken);
}
