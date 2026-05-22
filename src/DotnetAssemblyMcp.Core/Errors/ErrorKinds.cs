namespace DotnetAssemblyMcp.Core.Errors;

/// <summary>
/// Stable error <c>Kind</c> values used by <see cref="AssemblyError"/>. These are part of
/// the public contract: clients branch on them. Never repurpose a value once published;
/// add a new constant instead. See <c>docs/handoff-contract.md §4</c>.
/// </summary>
public static class ErrorKinds
{
    /// <summary>No loaded module matches the requested moduleVersionId.</summary>
    public const string ModuleNotFound = "module_not_found";

    /// <summary>A module matched by path or name, but its MVID differs.</summary>
    public const string MvidMismatch = "mvid_mismatch";

    /// <summary>MethodDef row id exceeds the module's MethodDef table size.</summary>
    public const string TokenOutOfRange = "token_out_of_range";

    /// <summary>metadataToken decodes to a table other than MethodDef (0x06).</summary>
    public const string TokenWrongTable = "token_wrong_table";

    /// <summary>Method has no body in the target module (trimmed / NativeAOT).</summary>
    public const string TokenTrimmed = "token_trimmed";

    /// <summary>Required field missing or wrong type in the MethodIdentity payload.</summary>
    public const string IdentityMalformed = "identity_malformed";

    /// <summary>load_assembly failed (file missing, bad PE, permission denied).</summary>
    public const string ModuleLoadFailed = "module_load_failed";

    /// <summary>Path is outside the configured search roots and explicit-load is disabled.</summary>
    public const string PathNotAllowed = "path_not_allowed";

    /// <summary>A parameter failed validation before any resolution was attempted.</summary>
    public const string InvalidArgument = "invalid_argument";

    /// <summary>A batch call exceeded the server-side cap on items per request.</summary>
    public const string BatchTooLarge = "batch_too_large"; // retained for ABI compat with older clients; no current tool emits it.

    /// <summary>A type-arg name in <c>genericTypeArguments</c> (§3.5) did not resolve in any loaded module.</summary>
    public const string GenericInstantiationUnresolvable = "generic_instantiation_unresolvable";

    /// <summary>A type-arg name in <c>genericTypeArguments</c> (§3.5) resolved in 2+ modules with conflicting MVIDs.</summary>
    public const string GenericInstantiationAmbiguous = "generic_instantiation_ambiguous";

    /// <summary>A type-arg referenced an open type parameter (<c>!N</c> / <c>!!N</c>). Wire instantiations MUST be closed.</summary>
    public const string GenericInstantiationOpen = "generic_instantiation_open";

    /// <summary>Both <c>methodSpec</c> and <c>genericTypeArguments</c> were supplied and decode to different instantiations.</summary>
    public const string GenericInstantiationMismatch = "generic_instantiation_mismatch";

    /// <summary>A pattern argument (regex / substring) would match more results than the server is willing to return in a single call.</summary>
    public const string PatternTooBroad = "pattern_too_broad";

    /// <summary>
    /// A path supplied to a tool is not absolute (per <see cref="System.IO.Path.IsPathFullyQualified(string)"/>).
    /// Relative paths are rejected because they would resolve against the server's working
    /// directory, which is unrelated to the operator's intent in HTTP / container deployments.
    /// </summary>
    public const string PathMustBeAbsolute = "path_must_be_absolute";

    /// <summary>
    /// A path was rejected by the security-hardening layer before any read: too large,
    /// a symlink / reparse point, or pointing outside an expected containment directory.
    /// </summary>
    public const string PathRejected = "path_rejected";
}
