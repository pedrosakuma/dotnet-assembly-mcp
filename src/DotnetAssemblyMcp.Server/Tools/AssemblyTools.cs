using System.ComponentModel;
using DotnetAssemblyMcp.Core;
using DotnetAssemblyMcp.Core.Decompilation;
using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Identity;
using DotnetAssemblyMcp.Core.Metadata;
using ModelContextProtocol.Server;

namespace DotnetAssemblyMcp.Server.Tools;

/// <summary>
/// MCP tools that expose the dotnet-assembly-mcp Core navigation primitives. Every tool
/// returns an <see cref="AssemblyResult{T}"/> envelope carrying a short summary, next-action
/// hints, and the typed payload — mirrors the companion dotnet-diagnostics-mcp surface so
/// the agent experience is consistent across both servers.
/// </summary>
[McpServerToolType]
public sealed class AssemblyTools
{
    [McpServerTool(
        Name = "load_assembly",
        Title = "Load a .NET assembly from disk",
        Destructive = false,
        ReadOnly = false,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Opens a managed PE file (.dll / .exe) via System.Reflection.Metadata and caches a " +
        "metadata-only handle keyed by its ModuleVersionId. Idempotent: loading the same MVID " +
        "twice returns the cached handle. Never executes the assembly. Usually the first call.")]
    public static AssemblyResult<ModuleSummary> LoadAssembly(
        IMetadataIndex index,
        [Description("Absolute path to a .NET PE assembly on the local filesystem.")] string path)
    {
        var result = index.Load(path);
        if (!result.IsSuccess)
        {
            return AssemblyResult.Fail<ModuleSummary>(
                $"Failed to load '{path}': {result.Error!.Message}",
                result.Error,
                new NextActionHint("list_assemblies", "List currently loaded modules to confirm what is already available."));
        }

        var m = result.Module!;
        return AssemblyResult.Ok(
            m,
            $"Loaded {m.ModuleName} (mvid={m.ModuleVersionId:D}, {m.MethodCount} methods).",
            new NextActionHint(
                "get_method",
                "Resolve a MethodIdentity from a diagnostic payload against this module.",
                new Dictionary<string, object?>
                {
                    ["moduleVersionId"] = m.ModuleVersionId.ToString("D"),
                    ["metadataToken"] = 0x06000001,
                }));
    }

    [McpServerTool(
        Name = "list_assemblies",
        Title = "List currently loaded assemblies",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Returns the modules currently held by the metadata index (mvid, path, method count). " +
        "Useful to confirm a target assembly is loaded before calling get_method, or to check " +
        "whether two builds with the same name have different MVIDs.")]
    public static AssemblyResult<IReadOnlyList<ModuleSummary>> ListAssemblies(IMetadataIndex index)
    {
        var modules = index.List();
        if (modules.Count == 0)
        {
            return AssemblyResult.Ok(
                modules,
                "No assemblies loaded yet. Call load_assembly with a path to begin.",
                new NextActionHint("load_assembly", "Load the target assembly from disk before resolving identities."));
        }

        var preview = string.Join(", ", modules.Take(3).Select(m => m.ModuleName));
        return AssemblyResult.Ok(
            modules,
            $"{modules.Count} assembly(ies) loaded: {preview}{(modules.Count > 3 ? ", …" : "")}.",
            new NextActionHint("get_method", "Resolve a MethodIdentity emitted by dotnet-diagnostics-mcp against a loaded module."));
    }

    [McpServerTool(
        Name = "get_method",
        Title = "Resolve a MethodIdentity to a method summary",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Implements the consumer side of the MethodIdentity handoff contract: given a " +
        "moduleVersionId + metadataToken (typically copied from a dotnet-diagnostics-mcp " +
        "response), returns the declaring type, method name, signature, IL size and method " +
        "attributes. See docs/handoff-contract.md for the full semantics and error kinds.")]
    public static AssemblyResult<MethodSummary> GetMethod(
        IMetadataIndex index,
        [Description("ModuleVersionId GUID of the assembly the method belongs to, as a string ('D' format).")] string moduleVersionId,
        [Description("Method definition metadata token (table 0x06). Accepts decimal or hex (0x06000001).")] string metadataToken,
        [Description("Optional declaring type full name; used only as a sanity-check display label.")] string? typeFullName = null,
        [Description("Optional method name; used only as a sanity-check display label.")] string? methodName = null,
        [Description("Optional generic arity from the producer payload. Defaults to 0.")] int genericArity = 0)
    {
        if (!Guid.TryParse(moduleVersionId, out var mvid))
        {
            return AssemblyResult.Fail<MethodSummary>(
                "moduleVersionId is not a valid GUID.",
                new AssemblyError(ErrorKinds.InvalidArgument, $"could not parse '{moduleVersionId}' as a GUID."),
                new NextActionHint("list_assemblies", "Inspect loaded MVIDs in the expected format."));
        }

        if (!TryParseToken(metadataToken, out var token))
        {
            return AssemblyResult.Fail<MethodSummary>(
                "metadataToken is not a valid integer.",
                new AssemblyError(ErrorKinds.InvalidArgument, $"could not parse '{metadataToken}' as a 32-bit metadata token."),
                new NextActionHint("get_method", "Pass the token as decimal (100663297) or hex (0x06000001)."));
        }

        var identity = new MethodIdentity(mvid, token, TypeFullName: typeFullName, MethodName: methodName, GenericArity: genericArity);
        var result = index.Resolve(identity);
        if (!result.IsSuccess)
        {
            var hint = result.Error!.Kind == ErrorKinds.ModuleNotFound
                ? new NextActionHint("load_assembly", "Load the assembly whose MVID matches the diagnostic payload.")
                : new NextActionHint("list_assemblies", "Confirm the loaded modules and tokens by listing the metadata index.");
            return AssemblyResult.Fail<MethodSummary>(result.Error.Message, result.Error, hint);
        }

        var m = result.Method!;
        return AssemblyResult.Ok(
            m,
            $"{m.TypeFullName}.{m.MethodName} — {m.Signature} (IL size {m.IlSize} bytes).",
            new NextActionHint("get_method", "Resolve the next frame from the diagnostic payload.",
                new Dictionary<string, object?>
                {
                    ["moduleVersionId"] = m.ModuleVersionId.ToString("D"),
                }));
    }

    [McpServerTool(
        Name = "decompile_method",
        Title = "Decompile a method to C# source",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Returns the C# source of a single method via ICSharpCode.Decompiler. Output is " +
        "hard-capped by maxChars (default 16 KiB) and LRU-cached keyed by (mvid, token, " +
        "maxChars) so repeated calls on the same hotspot are cheap. Use get_method first to " +
        "confirm the identity exists, then call this for the body.")]
    public static AssemblyResult<DecompiledMethod> DecompileMethod(
        IDecompiler decompiler,
        [Description("ModuleVersionId GUID of the assembly the method belongs to, as a string ('D' format).")] string moduleVersionId,
        [Description("Method definition metadata token (table 0x06). Accepts decimal or hex (0x06000001).")] string metadataToken,
        [Description("Optional cap on returned characters. Pass 0 to use the server default (16 KiB).")] int maxChars = 0)
    {
        if (!Guid.TryParse(moduleVersionId, out var mvid))
        {
            return AssemblyResult.Fail<DecompiledMethod>(
                "moduleVersionId is not a valid GUID.",
                new AssemblyError(ErrorKinds.InvalidArgument, $"could not parse '{moduleVersionId}' as a GUID."),
                new NextActionHint("list_assemblies", "Inspect loaded MVIDs in the expected format."));
        }
        if (!TryParseToken(metadataToken, out var token))
        {
            return AssemblyResult.Fail<DecompiledMethod>(
                "metadataToken is not a valid integer.",
                new AssemblyError(ErrorKinds.InvalidArgument, $"could not parse '{metadataToken}' as a 32-bit metadata token."),
                new NextActionHint("decompile_method", "Pass the token as decimal (100663297) or hex (0x06000001)."));
        }

        var identity = new MethodIdentity(mvid, token);
        var result = decompiler.Decompile(identity, maxChars);
        if (!result.IsSuccess)
        {
            var hint = result.Error!.Kind == ErrorKinds.ModuleNotFound
                ? new NextActionHint("load_assembly", "Load the assembly whose MVID matches the requested method.")
                : new NextActionHint("get_method", "Confirm the identity resolves with get_method before retrying.");
            return AssemblyResult.Fail<DecompiledMethod>(result.Error.Message, result.Error, hint);
        }

        var d = result.Source!;
        var prefix = d.CacheHit ? "[cache hit] " : string.Empty;
        var suffix = d.Truncated ? $" — truncated at {d.SourceLengthChars} chars" : string.Empty;
        return AssemblyResult.Ok(
            d,
            $"{prefix}{d.TypeFullName}.{d.MethodName} — {d.SourceLengthChars} chars of C#{suffix}.",
            new NextActionHint("get_method", "Look up another method in the same module.",
                new Dictionary<string, object?> { ["moduleVersionId"] = d.ModuleVersionId.ToString("D") }));
    }

    [McpServerTool(
        Name = "get_method_il",
        Title = "Get raw IL of a method",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Returns the raw IL bytes of a method (hex-encoded, capped by maxBytes; default " +
        "4 KiB) plus max-stack, exception region count and instruction count. Cheaper than " +
        "decompile_method when you only need to confirm the method exists with a non-empty " +
        "body or to count instructions.")]
    public static AssemblyResult<IlMethodBody> GetMethodIl(
        IMetadataIndex index,
        [Description("ModuleVersionId GUID of the assembly the method belongs to, as a string ('D' format).")] string moduleVersionId,
        [Description("Method definition metadata token (table 0x06). Accepts decimal or hex (0x06000001).")] string metadataToken,
        [Description("Optional cap on raw IL bytes encoded in the response. Pass 0 for the server default (4 KiB).")] int maxBytes = 0)
    {
        if (!TryParseIdentity(moduleVersionId, metadataToken, out var identity, out var err))
            return AssemblyResult.Fail<IlMethodBody>(err!.Message, err, new NextActionHint("list_assemblies", "Inspect loaded modules."));

        var result = index.GetIlBody(identity, maxBytes);
        if (!result.IsSuccess)
            return AssemblyResult.Fail<IlMethodBody>(result.Error!.Message, result.Error, ErrorRecoveryHint(result.Error));

        var b = result.Body!;
        var suffix = b.IlTruncated ? $" (hex truncated at {b.IlHex.Length / 2} bytes)" : string.Empty;
        return AssemblyResult.Ok(
            b,
            $"IL body: {b.IlSize} bytes, {b.InstructionCount} instructions, maxStack={b.MaxStack}, {b.ExceptionRegionCount} EH region(s){suffix}.",
            new NextActionHint("scan_method_il", "Extract outbound calls / fields / types from the same method.",
                new Dictionary<string, object?>
                {
                    ["moduleVersionId"] = b.ModuleVersionId.ToString("D"),
                    ["metadataToken"] = $"0x{b.MetadataToken:X8}",
                }));
    }

    [McpServerTool(
        Name = "scan_method_il",
        Title = "Scan a method's IL for outbound references",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Walks the method's IL and returns its structural outbound references: called methods, " +
        "accessed fields, used types and string literals — each with its raw token and a " +
        "best-effort textual rendering. Designed as the building block for cross-reference " +
        "queries without paying the cost of full decompilation.")]
    public static AssemblyResult<IlScanResult> ScanMethodIl(
        IMetadataIndex index,
        [Description("ModuleVersionId GUID of the assembly the method belongs to, as a string ('D' format).")] string moduleVersionId,
        [Description("Method definition metadata token (table 0x06). Accepts decimal or hex (0x06000001).")] string metadataToken)
    {
        if (!TryParseIdentity(moduleVersionId, metadataToken, out var identity, out var err))
            return AssemblyResult.Fail<IlScanResult>(err!.Message, err, new NextActionHint("list_assemblies", "Inspect loaded modules."));

        var result = index.ScanIl(identity);
        if (!result.IsSuccess)
            return AssemblyResult.Fail<IlScanResult>(result.Error!.Message, result.Error, ErrorRecoveryHint(result.Error));

        var s = result.Scan!;
        return AssemblyResult.Ok(
            s,
            $"{s.InstructionCount} instructions: {s.Calls.Count} call(s), {s.Fields.Count} field ref(s), {s.Types.Count} type ref(s), {s.Strings.Count} string literal(s).",
            new NextActionHint("decompile_method", "Read the C# source if the call list is ambiguous.",
                new Dictionary<string, object?>
                {
                    ["moduleVersionId"] = s.ModuleVersionId.ToString("D"),
                    ["metadataToken"] = $"0x{s.MetadataToken:X8}",
                }));
    }

    [McpServerTool(
        Name = "find_callers",
        Title = "Find callers of a method (same module)",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Returns every method in the callee's own module whose IL emits a direct call to it " +
        "(call / callvirt / newobj / ldftn / ldvirtftn / generic MethodSpec). The reverse index " +
        "is built lazily per module and cached at ~/.cache/dotnet-assembly-mcp/<mvid>.xref so " +
        "subsequent queries are O(callers). Cross-module callers are out of scope for now.")]
    public static AssemblyResult<FindCallersResult> FindCallers(
        IMetadataIndex index,
        [Description("ModuleVersionId GUID of the callee, as a string ('D' format).")] string moduleVersionId,
        [Description("Callee MethodDef metadata token (table 0x06). Accepts decimal or hex.")] string metadataToken)
    {
        if (!TryParseIdentity(moduleVersionId, metadataToken, out var identity, out var err))
            return AssemblyResult.Fail<FindCallersResult>(err!.Message, err,
                new NextActionHint("list_assemblies", "Inspect loaded modules."));

        var result = index.FindCallers(identity);
        if (!result.IsSuccess)
            return AssemblyResult.Fail<FindCallersResult>(result.Error!.Message, result.Error,
                ErrorRecoveryHint(result.Error));

        var r = result.Result!;
        var cacheTag = r.FromCache ? " (cached)" : " (built)";
        return AssemblyResult.Ok(
            r,
            $"{r.Callers.Count} caller(s) in {r.ModulesSearched} module{cacheTag}.",
            new NextActionHint("scan_method_il", "Inspect a specific caller's outbound references.",
                new Dictionary<string, object?>
                {
                    ["moduleVersionId"] = r.CalleeModuleVersionId.ToString("D"),
                    ["metadataToken"] = $"0x{r.CalleeMetadataToken:X8}",
                }));
    }

    private static bool TryParseIdentity(string moduleVersionId, string metadataToken,
        out MethodIdentity identity, out AssemblyError? error)
    {
        if (!Guid.TryParse(moduleVersionId, out var mvid))
        {
            identity = default!;
            error = new AssemblyError(ErrorKinds.InvalidArgument,
                $"could not parse '{moduleVersionId}' as a GUID.");
            return false;
        }
        if (!TryParseToken(metadataToken, out var token))
        {
            identity = default!;
            error = new AssemblyError(ErrorKinds.InvalidArgument,
                $"could not parse '{metadataToken}' as a 32-bit metadata token.");
            return false;
        }
        identity = new MethodIdentity(mvid, token);
        error = null;
        return true;
    }

    private static NextActionHint ErrorRecoveryHint(AssemblyError error) =>
        error.Kind == ErrorKinds.ModuleNotFound
            ? new NextActionHint("load_assembly", "Load the assembly whose MVID matches the requested method.")
            : new NextActionHint("get_method", "Confirm the identity resolves with get_method before retrying.");

    private static bool TryParseToken(string raw, out int token)
    {
        var s = raw?.Trim() ?? string.Empty;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || s.StartsWith("0X", StringComparison.Ordinal))
        {
            return int.TryParse(s.AsSpan(2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out token);
        }
        return int.TryParse(s, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out token);
    }
}