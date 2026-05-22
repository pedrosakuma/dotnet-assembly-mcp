namespace DotnetAssemblyMcp.Core.Errors;

/// <summary>
/// Thrown by per-module index builders (<c>XrefIndex.BuildXref</c>,
/// <c>StringIndex.BuildStringIndex</c>) when the module's scan would exceed a hard budget
/// (method count, retained refs, retained literals). The caller — invariably
/// <c>ModuleScopedCache.GetOrBuild</c> — propagates the exception, which guarantees no
/// partial index is inserted into the in-memory cache or written to the on-disk xref file.
///
/// Surface representation: translated by callers into
/// <see cref="ErrorKinds.ModuleTooLarge"/>. Never serialized over the wire.
/// </summary>
internal sealed class ModuleTooLargeException : Exception
{
    public string LimitName { get; }
    public long Limit { get; }

    public ModuleTooLargeException(string limitName, long limit)
        : base($"module index build aborted: {limitName} exceeded limit {limit:N0}.")
    {
        LimitName = limitName;
        Limit = limit;
    }
}
