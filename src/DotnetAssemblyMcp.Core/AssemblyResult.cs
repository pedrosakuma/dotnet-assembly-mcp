using DotnetAssemblyMcp.Core.Errors;

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
    T? Data = default,
    IReadOnlyList<NextActionHint>? Hints = null,
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

/// <summary>
/// Maps an <see cref="AssemblyError"/> to a recommended <see cref="NextActionHint"/>. Keeps
/// the mapping co-located with the envelope contract: tools (and any other consumer of
/// <c>AssemblyError</c>) get a uniform recovery suggestion without duplicating the switch.
/// Add a new arm whenever <see cref="ErrorKinds"/> grows a new constant — the default arm
/// keeps responses safe but uninformative.
/// </summary>
public static class AssemblyErrorRecovery
{
    /// <summary>Returns the canonical recovery hint for <paramref name="error"/>.</summary>
    public static NextActionHint For(AssemblyError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Kind switch
        {
            ErrorKinds.ModuleNotFound => new NextActionHint(
                "load_assembly",
                "Load the assembly whose MVID matches the request, then retry."),
            ErrorKinds.ModuleLoadFailed => new NextActionHint(
                "list_assemblies",
                "Verify the path / file is a valid managed PE and confirm what is already loaded."),
            ErrorKinds.MvidMismatch => new NextActionHint(
                "list_assemblies",
                "Inspect loaded MVIDs and reload the build that matches the diagnostic payload."),
            ErrorKinds.TokenWrongTable => new NextActionHint(
                "find_method",
                "The token does not point at a MethodDef. Search by name to locate the right token."),
            ErrorKinds.TokenOutOfRange => new NextActionHint(
                "find_method",
                "The MethodDef row id exceeds the table. Re-discover the token via find_method or list_methods."),
            ErrorKinds.TokenTrimmed => new NextActionHint(
                "get_method",
                "The method has no IL body (trimmed / NativeAOT). Use get_method for the signature-only view."),
            ErrorKinds.IdentityMalformed => new NextActionHint(
                "get_method",
                "Re-issue the call with both moduleVersionId (GUID) and metadataToken populated."),
            ErrorKinds.PathNotAllowed => new NextActionHint(
                "list_assemblies",
                "The path is outside the configured search roots. Inspect loaded modules and use their MVID instead."),
            ErrorKinds.InvalidArgument => new NextActionHint(
                "list_assemblies",
                "Validate the argument shape against the tool description and retry."),
            ErrorKinds.GenericInstantiationUnresolvable => new NextActionHint(
                "import_assembly_manifest",
                "A type-argument name did not resolve in any loaded module. Import the manifest for the dependency or supply assemblyPathHint, then retry."),
            ErrorKinds.GenericInstantiationAmbiguous => new NextActionHint(
                "list_assemblies",
                "A type-argument name resolved in 2+ modules with conflicting MVIDs. Inspect loaded modules and narrow the manifest, or qualify on the producer side."),
            ErrorKinds.GenericInstantiationOpen => new NextActionHint(
                "get_method",
                "Wire instantiations must be closed. Re-emit on the producer side with concrete type arguments instead of open type parameters."),
            ErrorKinds.GenericInstantiationMismatch => new NextActionHint(
                "get_method",
                "methodSpec and genericTypeArguments decode to different instantiations. Re-issue the call with only one of them, or fix the producer to keep them consistent."),
            ErrorKinds.PathMustBeAbsolute => new NextActionHint(
                "load_assembly",
                "Re-issue the call with an absolute path; relative paths are rejected because they resolve against the server's working directory."),
            ErrorKinds.PathRejected => new NextActionHint(
                "list_assemblies",
                "The supplied path was rejected by the file-IO guard (size cap, symlink, or out-of-tree sibling lookup). Inspect already-loaded modules and use their MVID directly."),
            ErrorKinds.ModuleTooLarge => new NextActionHint(
                "list_assemblies",
                "The target module would exceed the per-module index budget. Narrow the call's scope by passing mvidOrPath to a smaller assembly, or run the analysis offline on the on-disk PE."),
            _ => new NextActionHint(
                "list_assemblies",
                "Inspect loaded modules and retry the call."),
        };
    }
}
