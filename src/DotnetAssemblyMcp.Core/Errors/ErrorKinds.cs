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
}
