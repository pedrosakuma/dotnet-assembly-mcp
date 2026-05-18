namespace DotnetAssemblyMcp.Core;

/// <summary>
/// Discoverability-aware envelope for every assembly tool response. Provides a short
/// human-readable <see cref="Summary"/>, a list of suggested <see cref="Hints"/> that the
/// LLM should follow next, and the typed <see cref="Data"/> payload. Mirrors the
/// <c>DiagnosticResult&lt;T&gt;</c> envelope used by the companion dotnet-diagnostics-mcp
/// so the two servers feel identical to the agent.
/// </summary>
/// <typeparam name="T">Type of the underlying payload.</typeparam>
public sealed record AssemblyResult<T>(
    string Summary,
    T? Data,
    IReadOnlyList<NextActionHint> Hints,
    AssemblyError? Error = null)
{
    /// <summary>True when the call failed and <see cref="Error"/> is populated.</summary>
    public bool IsError => Error is not null;
}

/// <summary>
/// Non-generic factory helpers for <see cref="AssemblyResult{T}"/>. Kept separate so the
/// generic type satisfies CA1000 (no static members on generic types).
/// </summary>
public static class AssemblyResult
{
    /// <summary>Successful response.</summary>
    public static AssemblyResult<T> Ok<T>(T data, string summary, params NextActionHint[] hints)
        => new(summary, data, hints);

    /// <summary>Error response with a structured error and at least one recovery hint.</summary>
    public static AssemblyResult<T> Fail<T>(string summary, AssemblyError error, params NextActionHint[] hints)
        => new(summary, default, hints, error);
}

/// <summary>
/// A suggestion to the agent for the next call to make. Surfaced verbatim in
/// <see cref="AssemblyResult{T}.Hints"/> so a low-context LLM can keep drilling without
/// having to re-read the server instructions on every turn.
/// </summary>
public sealed record NextActionHint(
    string NextTool,
    string Reason,
    IReadOnlyDictionary<string, object?>? SuggestedArguments = null);

/// <summary>
/// Structured representation of a tool failure. Always carries a <see cref="Kind"/> (machine
/// classification) and an optional <see cref="Detail"/>. Hints describe the recommended recovery.
/// </summary>
public sealed record AssemblyError(
    string Kind,
    string Message,
    string? Detail = null);
