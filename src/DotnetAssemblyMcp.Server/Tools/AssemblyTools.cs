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
                ErrorRecoveryHint(result.Error));
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
        Name = "import_assembly_manifest",
        Title = "Bulk-import an (mvid, path) manifest from a producer",
        Destructive = false,
        ReadOnly = false,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Bulk handshake intended for sidecar scenarios: a producer (typically " +
        "dotnet-diagnostics-mcp) supplies a manifest of (moduleVersionId, path) pairs " +
        "observed in a running process. In 'lazy' mode (default) each entry is recorded as " +
        "a (mvid → path) hint without opening the PE — later get_method calls for that MVID " +
        "use the hint automatically. In 'tier1' mode each entry is opened eagerly and added " +
        "to the metadata index. Every entry's on-disk MVID is verified before use; an entry " +
        "whose actual MVID differs from the manifest is rejected with reason " +
        "'mvid_mismatch_with_path' (the path is never silently re-mapped). Re-importing the " +
        "same MVID is idempotent.")]
    public static AssemblyResult<ManifestImportResult> ImportAssemblyManifest(
        IMetadataIndex index,
        [Description("Manifest entries. Each must carry moduleVersionId (GUID 'D' format) and an absolute path; name is optional.")] IReadOnlyList<ManifestEntry> entries,
        [Description("'lazy' (default) records (mvid → path) hints without opening the PEs; 'tier1' eagerly loads every entry into the metadata index.")] ManifestImportMode mode = ManifestImportMode.Lazy)
    {
        if (entries is null || entries.Count == 0)
        {
            var empty = new ManifestImportResult(mode, Array.Empty<ManifestImportLoaded>(),
                Array.Empty<ManifestImportRegistered>(), Array.Empty<ManifestImportSkipped>());
            return AssemblyResult.Ok(
                empty,
                "Manifest is empty — nothing to import.",
                new NextActionHint("list_assemblies", "Inspect the modules currently loaded."));
        }

        var loaded = new List<ManifestImportLoaded>();
        var registered = new List<ManifestImportRegistered>();
        var skipped = new List<ManifestImportSkipped>();

        var alreadyLoaded = new HashSet<Guid>();
        foreach (var m in index.List()) alreadyLoaded.Add(m.ModuleVersionId);

        foreach (var entry in entries)
        {
            if (entry is null)
            {
                skipped.Add(new ManifestImportSkipped(Guid.Empty, string.Empty,
                    ErrorKinds.InvalidArgument, "entry is null."));
                continue;
            }
            if (entry.ModuleVersionId == Guid.Empty || string.IsNullOrWhiteSpace(entry.Path))
            {
                skipped.Add(new ManifestImportSkipped(entry.ModuleVersionId, entry.Path ?? string.Empty,
                    ErrorKinds.InvalidArgument, "moduleVersionId and path are required."));
                continue;
            }

            if (alreadyLoaded.Contains(entry.ModuleVersionId))
            {
                var existing = index.List().First(m => m.ModuleVersionId == entry.ModuleVersionId);
                loaded.Add(new ManifestImportLoaded(
                    existing.ModuleVersionId, existing.ModuleName, existing.MethodCount, "already_loaded"));
                continue;
            }

            if (!File.Exists(entry.Path))
            {
                skipped.Add(new ManifestImportSkipped(entry.ModuleVersionId, entry.Path,
                    "file_not_found", $"no file exists at '{entry.Path}'."));
                continue;
            }

            var probe = index.Probe(entry.Path);
            if (!probe.IsSuccess)
            {
                skipped.Add(new ManifestImportSkipped(entry.ModuleVersionId, entry.Path,
                    probe.Error!.Kind, probe.Error.Message));
                continue;
            }
            if (probe.Mvid != entry.ModuleVersionId)
            {
                skipped.Add(new ManifestImportSkipped(entry.ModuleVersionId, entry.Path,
                    "mvid_mismatch_with_path",
                    $"file at '{entry.Path}' has MVID {probe.Mvid:D} but the manifest claims {entry.ModuleVersionId:D}."));
                continue;
            }

            if (mode == ManifestImportMode.Tier1)
            {
                var load = index.Load(entry.Path);
                if (!load.IsSuccess)
                {
                    skipped.Add(new ManifestImportSkipped(entry.ModuleVersionId, entry.Path,
                        load.Error!.Kind, load.Error.Message));
                    continue;
                }
                alreadyLoaded.Add(load.Module!.ModuleVersionId);
                loaded.Add(new ManifestImportLoaded(
                    load.Module.ModuleVersionId, load.Module.ModuleName, load.Module.MethodCount, "loaded"));
            }
            else
            {
                index.RegisterPathHint(entry.ModuleVersionId, entry.Path);
                index.WatchPath(entry.Path);
                registered.Add(new ManifestImportRegistered(entry.ModuleVersionId, Path.GetFullPath(entry.Path)));
            }
        }

        var result = new ManifestImportResult(mode, loaded, registered, skipped);
        var summary = mode switch
        {
            ManifestImportMode.Tier1 =>
                $"Imported {loaded.Count} module(s) (tier1); {skipped.Count} skipped.",
            _ =>
                $"Registered {registered.Count} (mvid→path) hint(s); {loaded.Count} already loaded, {skipped.Count} skipped.",
        };

        NextActionHint next = skipped.Count > 0
            ? new NextActionHint(
                "list_assemblies",
                $"{skipped.Count} entry(ies) were skipped — inspect their 'reason' field and re-issue corrected entries.")
            : new NextActionHint(
                "get_method",
                "Resolve a MethodIdentity against an imported module — assemblyPathHint is no longer required for lazy-registered MVIDs.");

        return AssemblyResult.Ok(result, summary, next);
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
        [Description("Optional generic arity from the producer payload. Defaults to 0.")] int genericArity = 0,
        [Description("Optional absolute path the producer observed for this assembly. Used only when the MVID is not yet loaded: if the file at the path has a matching MVID it is loaded transparently; if it has a different MVID the call fails with mvid_mismatch (the path is a hint, never an override).")] string? assemblyPathHint = null,
        [Description("Optional CLR reflection-style full names for the declaring type's generic arguments (e.g. ['System.Int32']). When supplied alongside genericMethodArguments produces a closed signature view per docs/handoff-contract.md §3.5. No assembly qualification; nested types use '+'.")] string[]? genericTypeArguments = null,
        [Description("Optional CLR reflection-style full names for the method's generic arguments. See genericTypeArguments for the format.")] string[]? genericMethodArguments = null,
        [Description("Optional fast-path (§3.5): ModuleVersionId of a MethodSpec row that natively encodes the closed instantiation. Must be paired with methodSpecMetadataToken.")] string? methodSpecModuleVersionId = null,
        [Description("Optional fast-path (§3.5): MethodSpec metadata token (table 0x2B) inside methodSpecModuleVersionId. When supplied alongside genericTypeArguments, the two are cross-checked; a mismatch yields generic_instantiation_mismatch.")] string? methodSpecMetadataToken = null)
    {
        if (!Guid.TryParse(moduleVersionId, out var mvid))
        {
            var err = new AssemblyError(ErrorKinds.InvalidArgument, $"could not parse '{moduleVersionId}' as a GUID.");
            return AssemblyResult.Fail<MethodSummary>(
                "moduleVersionId is not a valid GUID.",
                err,
                ErrorRecoveryHint(err));
        }

        if (!TryParseToken(metadataToken, out var token))
        {
            var err = new AssemblyError(ErrorKinds.InvalidArgument, $"could not parse '{metadataToken}' as a 32-bit metadata token.");
            return AssemblyResult.Fail<MethodSummary>(
                "metadataToken is not a valid integer.",
                err,
                ErrorRecoveryHint(err));
        }

        if (TryEnsureModuleLoaded(index, mvid, assemblyPathHint) is { } loadErr)
            return AssemblyResult.Fail<MethodSummary>(loadErr.Message, loadErr, ErrorRecoveryHint(loadErr));

        if (!TryParseGenericArgs(genericTypeArguments, nameof(genericTypeArguments), out var typeArgs, out var parseErr))
            return AssemblyResult.Fail<MethodSummary>(parseErr!.Message, parseErr, ErrorRecoveryHint(parseErr));
        if (!TryParseGenericArgs(genericMethodArguments, nameof(genericMethodArguments), out var methodArgs, out parseErr))
            return AssemblyResult.Fail<MethodSummary>(parseErr!.Message, parseErr, ErrorRecoveryHint(parseErr));

        if (!TryParseMethodSpec(methodSpecModuleVersionId, methodSpecMetadataToken, out var methodSpec, out parseErr))
            return AssemblyResult.Fail<MethodSummary>(parseErr!.Message, parseErr, ErrorRecoveryHint(parseErr));

        var identity = new MethodIdentity(
            mvid, token,
            TypeFullName: typeFullName,
            MethodName: methodName,
            GenericArity: genericArity,
            TypeGenericArguments: typeArgs,
            MethodGenericArguments: methodArgs,
            MethodSpec: methodSpec);
        var result = index.Resolve(identity);
        if (!result.IsSuccess)
        {
            return AssemblyResult.Fail<MethodSummary>(result.Error!.Message, result.Error, ErrorRecoveryHint(result.Error));
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
        "confirm the identity exists, then call this for the body. " +
        "Note: generic methods are always returned in their open form (e.g. 'T Echo(T value)') — " +
        "the decompiler operates on MethodDef, not on closed instantiations. For a closed " +
        "signature view, use get_method with genericTypeArguments / genericMethodArguments " +
        "(or the methodSpec fast-path); see docs/handoff-contract.md §3.5.")]
    public static AssemblyResult<DecompiledMethod> DecompileMethod(
        IDecompiler decompiler,
        IMetadataIndex index,
        [Description("ModuleVersionId GUID of the assembly the method belongs to, as a string ('D' format).")] string moduleVersionId,
        [Description("Method definition metadata token (table 0x06). Accepts decimal or hex (0x06000001).")] string metadataToken,
        [Description("Optional cap on returned characters. Pass 0 to use the server default (16 KiB).")] int maxChars = 0,
        [Description("Optional absolute path the producer observed for this assembly (see get_method for semantics).")] string? assemblyPathHint = null,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(moduleVersionId, out var mvid))
        {
            var err = new AssemblyError(ErrorKinds.InvalidArgument, $"could not parse '{moduleVersionId}' as a GUID.");
            return AssemblyResult.Fail<DecompiledMethod>(
                "moduleVersionId is not a valid GUID.",
                err,
                ErrorRecoveryHint(err));
        }
        if (!TryParseToken(metadataToken, out var token))
        {
            var err = new AssemblyError(ErrorKinds.InvalidArgument, $"could not parse '{metadataToken}' as a 32-bit metadata token.");
            return AssemblyResult.Fail<DecompiledMethod>(
                "metadataToken is not a valid integer.",
                err,
                ErrorRecoveryHint(err));
        }

        if (TryEnsureModuleLoaded(index, mvid, assemblyPathHint) is { } loadErr)
            return AssemblyResult.Fail<DecompiledMethod>(loadErr.Message, loadErr, ErrorRecoveryHint(loadErr));

        var identity = new MethodIdentity(mvid, token);
        var result = decompiler.Decompile(identity, maxChars, cancellationToken);
        if (!result.IsSuccess)
        {
            return AssemblyResult.Fail<DecompiledMethod>(result.Error!.Message, result.Error, ErrorRecoveryHint(result.Error));
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
        Name = "get_method_il_text",
        Title = "Get ildasm-like textual IL dump for a method",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Returns an ildasm-style textual dump of a single method's IL via " +
        "ICSharpCode.Decompiler's ReflectionDisassembler. Operand tokens are resolved to " +
        "readable names; cross-module MemberRefs render with an assembly hint " +
        "(e.g. '[System.Runtime]System.Object::GetHashCode'). Capped server-side by " +
        "maxLines (default 256, hard cap 4096) and LRU-cached by (mvid, token, maxLines). " +
        "Sits between get_method_il (raw hex bytes) and decompile_method (C#): use this " +
        "when prefixes (tail./volatile./unaligned.), box/unbox.any placement, or " +
        "call-vs-callvirt dispatch matters. Generic methods rendered in open form, like " +
        "decompile_method.")]
    public static AssemblyResult<MethodIlText> GetMethodIlText(
        IIlDisassembler disassembler,
        IMetadataIndex index,
        [Description("ModuleVersionId GUID of the assembly the method belongs to, as a string ('D' format).")] string moduleVersionId,
        [Description("Method definition metadata token (table 0x06). Accepts decimal or hex (0x06000001).")] string metadataToken,
        [Description("Optional cap on output lines. Pass 0 for the server default (256). Hard cap 4096.")] int maxLines = 0,
        [Description("Optional absolute path the producer observed for this assembly (see get_method for semantics).")] string? assemblyPathHint = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseIdentity(moduleVersionId, metadataToken, out var identity, out var err))
            return AssemblyResult.Fail<MethodIlText>(err!.Message, err, ErrorRecoveryHint(err));

        if (TryEnsureModuleLoaded(index, identity.ModuleVersionId, assemblyPathHint) is { } loadErr)
            return AssemblyResult.Fail<MethodIlText>(loadErr.Message, loadErr, ErrorRecoveryHint(loadErr));

        var result = disassembler.Disassemble(identity, maxLines, cancellationToken);
        if (!result.IsSuccess)
            return AssemblyResult.Fail<MethodIlText>(result.Error!.Message, result.Error, ErrorRecoveryHint(result.Error));

        var t = result.Text!;
        var prefix = t.CacheHit ? "[cache hit] " : string.Empty;
        var suffix = t.Truncated ? $" — truncated at {t.LineCount} lines" : string.Empty;
        return AssemblyResult.Ok(
            t,
            $"{prefix}{t.TypeFullName}.{t.MethodName} — {t.InstructionCount} IL instruction(s), {t.LineCount} line(s){suffix}.",
            new NextActionHint("decompile_method", "Read the reconstructed C# if the IL is hard to follow.",
                new Dictionary<string, object?>
                {
                    ["moduleVersionId"] = t.ModuleVersionId.ToString("D"),
                    ["metadataToken"] = $"0x{t.MetadataToken:X8}",
                }));
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
        [Description("Optional cap on raw IL bytes encoded in the response. Pass 0 for the server default (4 KiB).")] int maxBytes = 0,
        [Description("Optional absolute path the producer observed for this assembly (see get_method for semantics).")] string? assemblyPathHint = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseIdentity(moduleVersionId, metadataToken, out var identity, out var err))
            return AssemblyResult.Fail<IlMethodBody>(err!.Message, err, ErrorRecoveryHint(err));

        if (TryEnsureModuleLoaded(index, identity.ModuleVersionId, assemblyPathHint) is { } loadErr)
            return AssemblyResult.Fail<IlMethodBody>(loadErr.Message, loadErr, ErrorRecoveryHint(loadErr));

        var result = index.GetIlBody(identity, maxBytes, cancellationToken);
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
        "queries without paying the cost of full decompilation. " +
        "Note: this tool does not accept §3.5 generic-instantiation arguments — IL token " +
        "references are invariant across instantiations of an open generic method. Pass the " +
        "closed args to get_method if you need a closed signature alongside the IL scan.")]
    public static AssemblyResult<IlScanResult> ScanMethodIl(
        IMetadataIndex index,
        [Description("ModuleVersionId GUID of the assembly the method belongs to, as a string ('D' format).")] string moduleVersionId,
        [Description("Method definition metadata token (table 0x06). Accepts decimal or hex (0x06000001).")] string metadataToken,
        [Description("Optional absolute path the producer observed for this assembly (see get_method for semantics).")] string? assemblyPathHint = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseIdentity(moduleVersionId, metadataToken, out var identity, out var err))
            return AssemblyResult.Fail<IlScanResult>(err!.Message, err, ErrorRecoveryHint(err));

        if (TryEnsureModuleLoaded(index, identity.ModuleVersionId, assemblyPathHint) is { } loadErr)
            return AssemblyResult.Fail<IlScanResult>(loadErr.Message, loadErr, ErrorRecoveryHint(loadErr));

        var result = index.ScanIl(identity, cancellationToken);
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
        Name = "list_types",
        Title = "List types in a loaded assembly with paging and filtering",
        Destructive = false,
        ReadOnly = false,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Enumerates the type definitions of a module. Accepts either an MVID of an already " +
        "loaded module or an absolute path (auto-loads on first call). Supports filtering by " +
        "namespace prefix, name substring (case-insensitive) and kind (class/struct/interface/" +
        "enum/delegate); results are paginated via cursor (pass nextCursor from the previous " +
        "response). Each entry includes a type handle 't:<mvid>:0x<token>' suitable for the " +
        "follow-up list_methods tool.")]
    public static AssemblyResult<ListTypesPage> ListTypes(
        IMetadataIndex index,
        [Description("Either the MVID GUID (D format) of a loaded module, or an absolute path to a .NET PE assembly (auto-loaded).")] string mvidOrPath,
        [Description("Optional namespace prefix filter, matched as a dot-segmented prefix (e.g. 'MyApp' matches 'MyApp.Foo' but not 'MyAppExt.Foo').")] string? namespacePrefix = null,
        [Description("Optional case-insensitive substring matched against the full type name (including '+' for nested types).")] string? nameContains = null,
        [Description("Optional kind filter. One of: class, struct, interface, enum, delegate.")] string? kind = null,
        [Description("Pagination cursor returned by the previous call. Pass 0 or omit for the first page.")] int cursor = 0,
        [Description("Max types per page (default 50, capped at 500).")] int pageSize = ListTypesQuery.DefaultPageSize)
    {
        if (!TryResolveModuleId(index, mvidOrPath, out var mvid, out var loadErr))
            return AssemblyResult.Fail<ListTypesPage>(loadErr!.Message, loadErr, ErrorRecoveryHint(loadErr));

        TypeKind? kindFilter = null;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            if (!Enum.TryParse<TypeKind>(kind, ignoreCase: true, out var parsed))
            {
                return AssemblyResult.Fail<ListTypesPage>(
                    $"unknown kind '{kind}'. Accepted: class, struct, interface, enum, delegate.",
                    new AssemblyError(ErrorKinds.InvalidArgument, $"unknown kind '{kind}'."),
                    new NextActionHint("list_types", "Drop the kind argument or pass one of: class, struct, interface, enum, delegate."));
            }
            kindFilter = parsed;
        }

        var query = new ListTypesQuery(
            NamespacePrefix: string.IsNullOrEmpty(namespacePrefix) ? null : namespacePrefix,
            NameContains: string.IsNullOrEmpty(nameContains) ? null : nameContains,
            Kind: kindFilter,
            Cursor: cursor > 0 ? cursor : null,
            PageSize: pageSize);

        var result = index.ListTypes(mvid, query);
        if (!result.IsSuccess)
            return AssemblyResult.Fail<ListTypesPage>(result.Error!.Message, result.Error,
                ErrorRecoveryHint(result.Error));

        var p = result.Page!;
        var summary = p.Types.Count == 0
            ? "No types matched the filter."
            : $"{p.Types.Count} type(s){(p.Truncated ? $", more available (nextCursor={p.NextCursor})" : "")}.";
        NextActionHint hint;
        if (p.Truncated)
        {
            hint = new NextActionHint("list_types", "Fetch the next page using the returned cursor.",
                new Dictionary<string, object?>
                {
                    ["mvidOrPath"] = mvid.ToString("D"),
                    ["cursor"] = p.NextCursor,
                });
        }
        else if (p.Types.Count > 0)
        {
            var first = p.Types[0];
            hint = new NextActionHint("list_methods", "Drill into a type's methods using its type handle.",
                new Dictionary<string, object?>
                {
                    ["typeHandle"] = first.Handle,
                });
        }
        else
        {
            hint = new NextActionHint("list_types", "Relax the filter (drop namespacePrefix or nameContains) and retry.",
                new Dictionary<string, object?> { ["mvidOrPath"] = mvid.ToString("D") });
        }
        return AssemblyResult.Ok(p, summary, hint);
    }

    [McpServerTool(
        Name = "list_assembly_references",
        Title = "List AssemblyRef rows (external dependencies) of a module",
        Destructive = false,
        ReadOnly = false,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Enumerates the AssemblyRef table of a single module: every external assembly the " +
        "module depends on at metadata level, with name, four-part version, culture, public " +
        "key token (hex), and raw AssemblyFlags. Cheap (single MetadataReader walk, not " +
        "paginated). Use to reconstruct the dependency graph, audit target-framework or " +
        "package versions, or pivot into load_assembly when the referenced assembly is also " +
        "on disk. Accepts an MVID of an already-loaded module or an absolute path (auto-" +
        "loaded on first call).")]
    public static AssemblyResult<ListAssemblyReferencesPage> ListAssemblyReferences(
        IMetadataIndex index,
        [Description("Either the MVID GUID (D format) of a loaded module, or an absolute path to a .NET PE assembly (auto-loaded).")] string mvidOrPath)
    {
        if (!TryResolveModuleId(index, mvidOrPath, out var mvid, out var loadErr))
            return AssemblyResult.Fail<ListAssemblyReferencesPage>(loadErr!.Message, loadErr, ErrorRecoveryHint(loadErr));

        var result = index.ListAssemblyReferences(mvid);
        if (!result.IsSuccess)
            return AssemblyResult.Fail<ListAssemblyReferencesPage>(result.Error!.Message, result.Error,
                ErrorRecoveryHint(result.Error));

        var p = result.Page!;
        var summary = p.References.Count == 0
            ? "Module declares no AssemblyRef rows."
            : $"{p.References.Count} assembly reference(s).";
        return AssemblyResult.Ok(p, summary,
            new NextActionHint("load_assembly", "Load a referenced assembly by absolute path to inspect it."));
    }

    [McpServerTool(
        Name = "find_string_references",
        Title = "Find every method that emits a given string literal",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Reverse string-literal lookup: returns every method whose IL contains an ldstr opcode " +
        "whose decoded user-string matches 'query' under 'matchMode' (exact / contains / regex). " +
        "Scope is all loaded modules unless 'mvidOrPath' is supplied; in that case only the named " +
        "module is searched (auto-loaded from path if needed). Hits include the caller's method " +
        "handle, signature display, IL offset of the ldstr opcode, and the matched literal. " +
        "Per-module string index is built lazily on the first call against that module and held " +
        "in memory; subsequent calls are O(1) for exact / O(unique-literals) for contains+regex. " +
        "Result is capped at 'maxHits' (default 1000, hard cap 10000); 'truncated' = true when hit. " +
        "Typical use: 'a user reported error message X — which method produces it?'.")]
    public static AssemblyResult<FindStringReferencesResult> FindStringReferences(
        IMetadataIndex index,
        [Description("The string to search for. Required.")] string query,
        [Description("Match semantics: 'exact' (default), 'contains', or 'regex'. Regex evaluation has a 1s timeout per literal.")] string? matchMode = null,
        [Description("Optional scope. MVID GUID or absolute path of a single module. Omit / pass null to search every loaded module.")] string? mvidOrPath = null,
        [Description("Optional cap on returned hits (default 1000, hard cap 10000). Pass 0 for default.")] int maxHits = 0,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(query))
        {
            return AssemblyResult.Fail<FindStringReferencesResult>(
                "query is required.",
                new AssemblyError(ErrorKinds.InvalidArgument, "query is required."));
        }

        StringMatchMode mode = StringMatchMode.Exact;
        if (!string.IsNullOrEmpty(matchMode))
        {
            if (string.Equals(matchMode, "exact", StringComparison.OrdinalIgnoreCase)) mode = StringMatchMode.Exact;
            else if (string.Equals(matchMode, "contains", StringComparison.OrdinalIgnoreCase)) mode = StringMatchMode.Contains;
            else if (string.Equals(matchMode, "regex", StringComparison.OrdinalIgnoreCase)) mode = StringMatchMode.Regex;
            else
            {
                var err = new AssemblyError(ErrorKinds.InvalidArgument,
                    $"matchMode must be 'exact', 'contains', or 'regex' (got '{matchMode}').");
                return AssemblyResult.Fail<FindStringReferencesResult>(err.Message, err);
            }
        }

        var mvidFilter = Guid.Empty;
        if (!string.IsNullOrEmpty(mvidOrPath))
        {
            if (!TryResolveModuleId(index, mvidOrPath, out mvidFilter, out var loadErr))
                return AssemblyResult.Fail<FindStringReferencesResult>(loadErr!.Message, loadErr, ErrorRecoveryHint(loadErr));
        }

        var result = index.FindStringReferences(query, mode, mvidFilter, maxHits, cancellationToken);
        if (!result.IsSuccess)
            return AssemblyResult.Fail<FindStringReferencesResult>(result.Error!.Message, result.Error,
                ErrorRecoveryHint(result.Error));

        var r = result.Result!;
        var truncTag = r.Truncated ? " (truncated)" : "";
        var summary = r.Hits.Count == 0
            ? $"No hits across {r.ModulesSearched} module(s)."
            : $"{r.Hits.Count} hit(s) across {r.ModulesSearched} module(s){truncTag}.";
        return AssemblyResult.Ok(r, summary,
            new NextActionHint("get_method", "Inspect a specific caller for context around the literal."));
    }

    [McpServerTool(
        Name = "list_methods",
        Title = "List methods of a type with paging and name filtering",
        Destructive = false,
        ReadOnly = false,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Enumerates the methods of a single type. Identify the type either via a typeHandle " +
        "('t:<mvid>:0x<typeToken>' returned by list_types) or via mvidOrPath + typeFullName " +
        "(case-sensitive, uses '+' for nested types e.g. 'NS.Outer+Inner'). Returns one " +
        "MethodSummary per method (handle, name, signature, ilSize, attributes); use cursor " +
        "for paging. Drill in further with decompile_method, get_method_il or find_callers.")]
    public static AssemblyResult<ListMethodsPage> ListMethods(
        IMetadataIndex index,
        [Description("Type handle 't:<mvid>:0x<typeToken>' as returned by list_types. Pass null/empty if using mvidOrPath+typeFullName instead.")] string? typeHandle = null,
        [Description("MVID GUID or absolute path of the module; only used when typeHandle is omitted.")] string? mvidOrPath = null,
        [Description("Full type name (case-sensitive, '+'-joined for nested types); only used when typeHandle is omitted.")] string? typeFullName = null,
        [Description("Optional case-insensitive substring filter on the method name.")] string? namePattern = null,
        [Description("Pagination cursor returned by the previous call. Pass 0 or omit for the first page.")] int cursor = 0,
        [Description("Max methods per page (default 50, capped at 500).")] int pageSize = ListMethodsQuery.DefaultPageSize)
    {
        if (!TryResolveTypeIdentity(index, typeHandle, mvidOrPath, typeFullName,
            out var mvid, out var typeToken, out var resolveErr))
        {
            // typeFullName misses surface as IdentityMalformed; both share the same recovery
            // (use list_types to discover a real handle) so we still prefer the centralized helper.
            var resolveHint = resolveErr!.Kind == ErrorKinds.IdentityMalformed
                ? new NextActionHint("list_types", "Use list_types first to discover a valid type handle or full name.")
                : ErrorRecoveryHint(resolveErr);
            return AssemblyResult.Fail<ListMethodsPage>(resolveErr.Message, resolveErr, resolveHint);
        }

        var query = new ListMethodsQuery(
            NamePattern: string.IsNullOrEmpty(namePattern) ? null : namePattern,
            Cursor: cursor > 0 ? cursor : null,
            PageSize: pageSize);

        var result = index.ListMethods(mvid, typeToken, query);
        if (!result.IsSuccess)
            return AssemblyResult.Fail<ListMethodsPage>(result.Error!.Message, result.Error,
                ErrorRecoveryHint(result.Error));

        var p = result.Page!;
        var summary = p.Methods.Count == 0
            ? $"No methods in {p.TypeFullName} matched the filter."
            : $"{p.Methods.Count} method(s) in {p.TypeFullName}{(p.Truncated ? $", more available (nextCursor={p.NextCursor})" : "")}.";

        NextActionHint hint;
        if (p.Truncated)
        {
            hint = new NextActionHint("list_methods", "Fetch the next page using the returned cursor.",
                new Dictionary<string, object?>
                {
                    ["typeHandle"] = HandleFormat.FormatType(p.ModuleVersionId, p.TypeMetadataToken),
                    ["cursor"] = p.NextCursor,
                });
        }
        else if (p.Methods.Count > 0)
        {
            var first = p.Methods[0];
            hint = new NextActionHint("decompile_method", "Read the C# source of a method to understand its behaviour.",
                new Dictionary<string, object?>
                {
                    ["moduleVersionId"] = first.ModuleVersionId.ToString("D"),
                    ["metadataToken"] = $"0x{first.MetadataToken:X8}",
                });
        }
        else
        {
            hint = new NextActionHint("list_methods", "Drop the namePattern filter and retry to see all methods of the type.",
                new Dictionary<string, object?>
                {
                    ["typeHandle"] = HandleFormat.FormatType(p.ModuleVersionId, p.TypeMetadataToken),
                });
        }
        return AssemblyResult.Ok(p, summary, hint);
    }

    [McpServerTool(
        Name = "find_method",
        Title = "Search methods across a whole module by name regex",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Module-wide method search. Matches every MethodDef whose short name matches the " +
        "supplied regular expression (case-insensitive) and, optionally, whose signature " +
        "contains a substring. Returns hits with the canonical 'm:<mvid>:0x<token>' handle " +
        "ready to feed into get_method / decompile_method / find_callers. Use this when you " +
        "do not yet have a type in mind; otherwise prefer list_methods which is cheaper.")]
    public static AssemblyResult<FindMethodPage> FindMethod(
        IMetadataIndex index,
        [Description("Either the MVID GUID ('D' format) of a previously loaded module, or an absolute path to a managed PE assembly (will be loaded on demand).")] string mvidOrPath,
        [Description("Regular expression matched (case-insensitive) against each method's short name.")] string namePattern,
        [Description("Optional case-insensitive substring filter on the decoded signature (e.g. 'CancellationToken').")] string? signatureContains = null,
        [Description("Optional pagination cursor returned in a prior call (exclusive lower bound on MethodDef token).")] int? cursor = null,
        [Description("Max matches per page (default 20, capped at 200).")] int pageSize = FindMethodQuery.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveModuleId(index, mvidOrPath, out var mvid, out var resolveErr))
            return AssemblyResult.Fail<FindMethodPage>(resolveErr!.Message, resolveErr, ErrorRecoveryHint(resolveErr));

        var query = new FindMethodQuery(namePattern, signatureContains, cursor, pageSize);
        var result = index.FindMethod(mvid, query, cancellationToken);
        if (!result.IsSuccess)
            return AssemblyResult.Fail<FindMethodPage>(result.Error!.Message, result.Error,
                ErrorRecoveryHint(result.Error));

        var p = result.Page!;
        var summary = p.Matches.Count == 0
            ? $"No method matched /{p.NamePattern}/ in module {p.ModuleVersionId:D}."
            : $"{p.Matches.Count} match(es) for /{p.NamePattern}/{(p.Truncated ? $", more available (nextCursor={p.NextCursor})" : "")}.";

        NextActionHint hint;
        if (p.Truncated)
        {
            hint = new NextActionHint("find_method", "Fetch the next page of matches using the returned cursor.",
                new Dictionary<string, object?>
                {
                    ["mvidOrPath"] = p.ModuleVersionId.ToString("D"),
                    ["namePattern"] = p.NamePattern,
                    ["signatureContains"] = signatureContains,
                    ["cursor"] = p.NextCursor,
                });
        }
        else if (p.Matches.Count > 0)
        {
            var first = p.Matches[0];
            hint = new NextActionHint("decompile_method", "Read the C# source of the top match.",
                new Dictionary<string, object?>
                {
                    ["moduleVersionId"] = first.ModuleVersionId.ToString("D"),
                    ["metadataToken"] = $"0x{first.MetadataToken:X8}",
                });
        }
        else
        {
            hint = new NextActionHint("find_method", "Relax the namePattern or drop signatureContains and retry.",
                new Dictionary<string, object?>
                {
                    ["mvidOrPath"] = p.ModuleVersionId.ToString("D"),
                    ["namePattern"] = ".*",
                });
        }
        return AssemblyResult.Ok(p, summary, hint);
    }

    [McpServerTool(
        Name = "find_callers",
        Title = "Find callers of a method (same- and cross-module)",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Returns every method whose IL emits a direct call to the requested callee — within " +
        "the callee's own module via MethodDef tokens, and across any other loaded module via " +
        "MemberRef signature matching (assembly name + type fullname + method name + " +
        "parameter signature + generic arity). The reverse index is built lazily per module " +
        "and cached at ~/.cache/dotnet-assembly-mcp/<mvid>.xref so subsequent queries are " +
        "O(callers).")]
    public static AssemblyResult<FindCallersResult> FindCallers(
        IMetadataIndex index,
        [Description("ModuleVersionId GUID of the callee, as a string ('D' format).")] string moduleVersionId,
        [Description("Callee MethodDef metadata token (table 0x06). Accepts decimal or hex.")] string metadataToken,
        [Description("Optional absolute path the producer observed for this assembly (see get_method for semantics).")] string? assemblyPathHint = null,
        [Description("Optional CLR reflection-style full names for the declaring type's generic arguments (see get_method).")] string[]? genericTypeArguments = null,
        [Description("Optional CLR reflection-style full names for the method's generic arguments. When supplied, the caller list is narrowed to call sites whose MethodSpec.Instantiation matches element-wise (docs/handoff-contract.md §3.5).")] string[]? genericMethodArguments = null,
        [Description("Optional fast-path (§3.5): ModuleVersionId of a MethodSpec row. Paired with methodSpecMetadataToken.")] string? methodSpecModuleVersionId = null,
        [Description("Optional fast-path (§3.5): MethodSpec metadata token (table 0x2B). When supplied, derives the instantiation directly from metadata.")] string? methodSpecMetadataToken = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseIdentity(moduleVersionId, metadataToken, out var identity, out var err))
            return AssemblyResult.Fail<FindCallersResult>(err!.Message, err, ErrorRecoveryHint(err));

        if (TryEnsureModuleLoaded(index, identity.ModuleVersionId, assemblyPathHint) is { } loadErr)
            return AssemblyResult.Fail<FindCallersResult>(loadErr.Message, loadErr, ErrorRecoveryHint(loadErr));

        if (!TryParseGenericArgs(genericTypeArguments, nameof(genericTypeArguments), out var typeArgs, out var parseErr))
            return AssemblyResult.Fail<FindCallersResult>(parseErr!.Message, parseErr, ErrorRecoveryHint(parseErr));
        if (!TryParseGenericArgs(genericMethodArguments, nameof(genericMethodArguments), out var methodArgs, out parseErr))
            return AssemblyResult.Fail<FindCallersResult>(parseErr!.Message, parseErr, ErrorRecoveryHint(parseErr));
        if (!TryParseMethodSpec(methodSpecModuleVersionId, methodSpecMetadataToken, out var methodSpec, out parseErr))
            return AssemblyResult.Fail<FindCallersResult>(parseErr!.Message, parseErr, ErrorRecoveryHint(parseErr));

        identity = identity with
        {
            TypeGenericArguments = typeArgs,
            MethodGenericArguments = methodArgs,
            MethodSpec = methodSpec,
        };

        var result = index.FindCallers(identity, cancellationToken);
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

    [McpServerTool(
        Name = "find_type_references",
        Title = "Find references to a type (same- and cross-module)",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Returns every site that references the requested TypeDef: field/property/event " +
        "types, method parameters / return types / locals, and IL opcodes that bake in a " +
        "type token (newobj, castclass, isinst, box, unbox, ldtoken, generic args, ...). " +
        "Same-module hits come from TypeDef tokens; cross-module hits come from TypeRef " +
        "matching (assembly simple name + type full name). Uses the same lazily-built per-" +
        "module xref cache as find_callers; the cache file format version was bumped so the " +
        "first call after upgrade rebuilds.")]
    public static AssemblyResult<FindTypeReferencesResult> FindTypeReferences(
        IMetadataIndex index,
        [Description("Type handle 't:<mvid>:0x<typeToken>' as returned by list_types or get_type.")] string? typeHandle = null,
        [Description("MVID GUID or absolute path of the module; only used when typeHandle is omitted.")] string? mvidOrPath = null,
        [Description("Full type name (case-sensitive, '+'-joined for nested types); only used when typeHandle is omitted.")] string? typeFullName = null,
        [Description("Optional absolute path the producer observed for this assembly (used to load the module if it's not yet known).")] string? assemblyPathHint = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveTypeIdentity(index, typeHandle, mvidOrPath, typeFullName,
            out var mvid, out var typeToken, out var resolveErr))
        {
            var resolveHint = resolveErr!.Kind == ErrorKinds.IdentityMalformed
                ? new NextActionHint("list_types", "Use list_types first to discover a valid type handle or full name.")
                : ErrorRecoveryHint(resolveErr);
            return AssemblyResult.Fail<FindTypeReferencesResult>(resolveErr.Message, resolveErr, resolveHint);
        }

        if (TryEnsureModuleLoaded(index, mvid, assemblyPathHint) is { } loadErr)
            return AssemblyResult.Fail<FindTypeReferencesResult>(loadErr.Message, loadErr, ErrorRecoveryHint(loadErr));

        var result = index.FindTypeReferences(mvid, typeToken, cancellationToken);
        if (!result.IsSuccess)
            return AssemblyResult.Fail<FindTypeReferencesResult>(result.Error!.Message, result.Error,
                ErrorRecoveryHint(result.Error));

        var r = result.Result!;
        var cacheTag = r.FromCache ? " (cached)" : " (built)";
        if (r.References.Count > 0)
        {
            return AssemblyResult.Ok(
                r,
                $"{r.References.Count} reference(s) in {r.ModulesSearched} module{cacheTag}.",
                new NextActionHint("get_method", "Drill into the first reference site.",
                    new Dictionary<string, object?>
                    {
                        ["moduleVersionId"] = r.References[0].ModuleVersionId.ToString("D"),
                        ["metadataToken"] = $"0x{r.References[0].MetadataToken:X8}",
                    }));
        }
        return AssemblyResult.Ok(
            r,
            $"{r.References.Count} reference(s) in {r.ModulesSearched} module{cacheTag}.");
    }

    [McpServerTool(
        Title = "Batch: resolve many MethodIdentities in one call",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Batch variant of get_method. Resolves up to " + BatchCapStr + " (moduleVersionId, " +
        "metadataToken) pairs in one round-trip. Per-item success/failure: one bad token does " +
        "not fail the batch. Each item may carry an assemblyPathHint (same semantics as " +
        "get_method), and the §3.5 generic-instantiation fields (genericTypeArguments, " +
        "genericMethodArguments, methodSpecModuleVersionId, methodSpecMetadataToken) so closed " +
        "instantiations are honored per item. Use after a dotnet-diagnostics-mcp top-N hotspot " +
        "dump to enrich the whole table in a single call. Over the cap → batch_too_large.")]
    public static AssemblyResult<BatchResponse<MethodSummary>> GetMethods(
        IMetadataIndex index,
        [Description("Method identities to resolve. At most " + BatchCapStr + " items.")] IReadOnlyList<MethodBatchItem> items,
        CancellationToken cancellationToken = default)
        => RunBatch<MethodSummary>(items, (item, _) =>
        {
            if (!TryParseIdentity(item.ModuleVersionId, item.MetadataToken, out var identity, out var err))
                return (null, err);
            if (TryEnsureModuleLoaded(index, identity.ModuleVersionId, item.AssemblyPathHint) is { } loadErr)
                return (null, loadErr);
            if (!TryParseGenericArgs(item.GenericTypeArguments, "genericTypeArguments", out var typeArgs, out var pErr))
                return (null, pErr);
            if (!TryParseGenericArgs(item.GenericMethodArguments, "genericMethodArguments", out var methodArgs, out pErr))
                return (null, pErr);
            if (!TryParseMethodSpec(item.MethodSpecModuleVersionId, item.MethodSpecMetadataToken, out var methodSpec, out pErr))
                return (null, pErr);
            identity = identity with { TypeGenericArguments = typeArgs, MethodGenericArguments = methodArgs, MethodSpec = methodSpec };
            var r = index.Resolve(identity);
            return r.IsSuccess ? (r.Method, null) : (null, r.Error);
        }, summarize: (ok, total) => $"Resolved {ok}/{total} method identit(ies).",
           overCapToolName: "get_methods", cancellationToken: cancellationToken);

    [McpServerTool(
        Name = "scan_methods_il",
        Title = "Batch: scan IL of many methods in one call",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Batch variant of scan_method_il. Walks the IL of up to " + BatchCapStr + " methods " +
        "and returns each one's outbound calls/fields/types/strings. Per-item success/" +
        "failure; assemblyPathHint honored per item. Like scan_method_il, this batch does not " +
        "accept §3.5 generic-instantiation arguments per item — IL is invariant across " +
        "instantiations of an open generic; supplying them is rejected with invalid_argument. " +
        "Over the cap → batch_too_large.")]
    public static AssemblyResult<BatchResponse<IlScanResult>> ScanMethodsIl(
        IMetadataIndex index,
        [Description("Method identities to scan. At most " + BatchCapStr + " items.")] IReadOnlyList<MethodBatchItem> items,
        CancellationToken cancellationToken = default)
        => RunBatch<IlScanResult>(items, (item, _) =>
        {
            if (!TryParseIdentity(item.ModuleVersionId, item.MetadataToken, out var identity, out var err))
                return (null, err);
            if (HasGenericInstantiationFields(item))
                return (null, new AssemblyError(
                    ErrorKinds.InvalidArgument,
                    "scan_methods_il does not accept genericTypeArguments / genericMethodArguments / methodSpec* — IL token references are invariant across instantiations. Remove the fields and retry."));
            if (TryEnsureModuleLoaded(index, identity.ModuleVersionId, item.AssemblyPathHint) is { } loadErr)
                return (null, loadErr);
            var r = index.ScanIl(identity, cancellationToken);
            return r.IsSuccess ? (r.Scan, null) : (null, r.Error);
        }, summarize: (ok, total) => $"Scanned {ok}/{total} method(s) for outbound references.",
           overCapToolName: "scan_methods_il", cancellationToken: cancellationToken);

    [McpServerTool(
        Name = "find_callers_batch",
        Title = "Batch: find callers of many methods in one call",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Batch variant of find_callers. Reverse-resolves up to " + BatchCapStr + " callees " +
        "in one round-trip; per-item success/failure. The lazily-built xref cache is shared " +
        "across items so repeated calls within the same module pay the build cost once. Each " +
        "item may carry §3.5 generic-instantiation fields (genericTypeArguments, " +
        "genericMethodArguments, methodSpecModuleVersionId, methodSpecMetadataToken) to narrow " +
        "results to a closed instantiation. Over the cap → batch_too_large.")]
    public static AssemblyResult<BatchResponse<FindCallersResult>> FindCallersBatch(
        IMetadataIndex index,
        [Description("Callee identities. At most " + BatchCapStr + " items.")] IReadOnlyList<MethodBatchItem> items,
        CancellationToken cancellationToken = default)
        => RunBatch<FindCallersResult>(items, (item, _) =>
        {
            if (!TryParseIdentity(item.ModuleVersionId, item.MetadataToken, out var identity, out var err))
                return (null, err);
            if (TryEnsureModuleLoaded(index, identity.ModuleVersionId, item.AssemblyPathHint) is { } loadErr)
                return (null, loadErr);
            if (!TryParseGenericArgs(item.GenericTypeArguments, "genericTypeArguments", out var typeArgs, out var pErr))
                return (null, pErr);
            if (!TryParseGenericArgs(item.GenericMethodArguments, "genericMethodArguments", out var methodArgs, out pErr))
                return (null, pErr);
            if (!TryParseMethodSpec(item.MethodSpecModuleVersionId, item.MethodSpecMetadataToken, out var methodSpec, out pErr))
                return (null, pErr);
            identity = identity with { TypeGenericArguments = typeArgs, MethodGenericArguments = methodArgs, MethodSpec = methodSpec };
            var r = index.FindCallers(identity, cancellationToken);
            return r.IsSuccess ? (r.Result, null) : (null, r.Error);
        }, summarize: (ok, total) => $"Resolved callers for {ok}/{total} callee(s).",
           overCapToolName: "find_callers_batch", cancellationToken: cancellationToken);

    /// <summary>Server-wide cap on batch items. See issue #5.</summary>
    public const int BatchCap = 100;
    private const string BatchCapStr = "100";

    [McpServerTool(
        Name = "get_method_source",
        Title = "Resolve a method's source-line coordinates from the PDB",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Reads the module's PDB (embedded portable PDB first, then sibling .pdb) and " +
        "returns the file/startLine/endLine triple plus a resolved SourceLink URL when " +
        "SourceLink CustomDebugInformation is present. Second-chance source resolver: use " +
        "after dotnet-diagnostics-mcp has emitted a hotspot with no SourceLocation. " +
        "Metadata-only (no HTTP). Returns found=false (not an error) when no PDB exists or " +
        "the method has no non-hidden sequence points (compiler-generated bodies). " +
        "Note: this tool does not accept §3.5 generic-instantiation arguments — PDB sequence " +
        "points anchor on the open MethodDef and the source coordinates are the same for " +
        "every closed instantiation.")]
    public static AssemblyResult<MethodSourceLocation> GetMethodSource(
        IMetadataIndex index,
        [Description("ModuleVersionId GUID of the assembly the method belongs to, as a string ('D' format).")] string moduleVersionId,
        [Description("Method definition metadata token (table 0x06). Accepts decimal or hex (0x06000001).")] string metadataToken,
        [Description("Optional absolute path the producer observed for this assembly (see get_method for semantics).")] string? assemblyPathHint = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseIdentity(moduleVersionId, metadataToken, out var identity, out var err))
            return AssemblyResult.Fail<MethodSourceLocation>(err!.Message, err, ErrorRecoveryHint(err));

        if (TryEnsureModuleLoaded(index, identity.ModuleVersionId, assemblyPathHint) is { } loadErr)
            return AssemblyResult.Fail<MethodSourceLocation>(loadErr.Message, loadErr, ErrorRecoveryHint(loadErr));

        cancellationToken.ThrowIfCancellationRequested();
        var result = index.GetMethodSource(identity);
        if (!result.IsSuccess)
            return AssemblyResult.Fail<MethodSourceLocation>(result.Error!.Message, result.Error,
                ErrorRecoveryHint(result.Error));

        var loc = result.Location!;
        if (loc.Found)
        {
            var sl = loc.SourceLink is null ? "" : $" → {loc.SourceLink}";
            return AssemblyResult.Ok(
                loc,
                $"{loc.File}:{loc.StartLine}-{loc.EndLine} (PDB={loc.PdbKind}){sl}.",
                new NextActionHint("decompile_method", "Read the method body if you need the surrounding code.",
                    new Dictionary<string, object?>
                    {
                        ["moduleVersionId"] = loc.ModuleVersionId.ToString("D"),
                        ["metadataToken"] = $"0x{loc.MetadataToken:X8}",
                    }));
        }

        var reason = string.IsNullOrEmpty(loc.Reason) ? "no source coordinates available" : loc.Reason!;
        return AssemblyResult.Ok(
            loc,
            $"Source not found: {reason} (PDB={loc.PdbKind}).",
            new NextActionHint("decompile_method", "Fall back to decompilation for a reconstructed body.",
                new Dictionary<string, object?>
                {
                    ["moduleVersionId"] = loc.ModuleVersionId.ToString("D"),
                    ["metadataToken"] = $"0x{loc.MetadataToken:X8}",
                }));
    }

    [McpServerTool(
        Name = "get_methods_source",
        Title = "Batch: resolve source-line coordinates for many methods",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Batch variant of get_method_source. Reads the PDB for up to " + BatchCapStr + " " +
        "methods in one round-trip and returns each one's file/startLine/endLine plus " +
        "SourceLink URL when available. Per-item success/failure; assemblyPathHint honored " +
        "per item. Like get_method_source, this batch does not accept §3.5 generic-" +
        "instantiation arguments per item — sequence points anchor on the open MethodDef; " +
        "supplying them is rejected with invalid_argument. Over the cap → batch_too_large.")]
    public static AssemblyResult<BatchResponse<MethodSourceLocation>> GetMethodsSource(
        IMetadataIndex index,
        [Description("Method identities to resolve. At most " + BatchCapStr + " items.")] IReadOnlyList<MethodBatchItem> items,
        CancellationToken cancellationToken = default)
        => RunBatch<MethodSourceLocation>(items, (item, _) =>
        {
            if (!TryParseIdentity(item.ModuleVersionId, item.MetadataToken, out var identity, out var err))
                return (null, err);
            if (HasGenericInstantiationFields(item))
                return (null, new AssemblyError(
                    ErrorKinds.InvalidArgument,
                    "get_methods_source does not accept genericTypeArguments / genericMethodArguments / methodSpec* — PDB sequence points anchor on the open MethodDef. Remove the fields and retry."));
            if (TryEnsureModuleLoaded(index, identity.ModuleVersionId, item.AssemblyPathHint) is { } loadErr)
                return (null, loadErr);
            var r = index.GetMethodSource(identity);
            return r.IsSuccess ? (r.Location, null) : (null, r.Error);
        }, summarize: (ok, total) => $"Resolved source for {ok}/{total} method(s).",
           overCapToolName: "get_methods_source", cancellationToken: cancellationToken);

    [McpServerTool(
        Name = "list_attributes",
        Title = "List custom attributes on an assembly, type, method, or parameter",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Enumerates the CustomAttribute rows attached to the entity identified by 'target'. " +
        "Accepts a polymorphic handle: 'a:<mvid>' (assembly), 't:<mvid>:0x<token>' (type), " +
        "'m:<mvid>:0x<token>' (method), 'pa:<mvid>:0x<methodToken>:<sequence>' (parameter; " +
        "sequence 0 = return value), 'f:<mvid>:0x<token>' (field), 'p:<mvid>:0x<token>' " +
        "(property), or 'e:<mvid>:0x<token>' (event — same handles list_members returns). " +
        "Pure metadata — no IL decoded, no decompilation. Each entry includes the " +
        "attribute's full type name, its declaring assembly's simple name (when " +
        "cross-module), the decoded constructor arguments, and the named arguments " +
        "(properties / fields set in the attribute usage).")]
    public static AssemblyResult<ListAttributesPage> ListAttributes(
        IMetadataIndex index,
        [Description("Target handle. One of: 'a:<mvid>', 't:<mvid>:0x<typeToken>', 'm:<mvid>:0x<methodToken>', 'pa:<mvid>:0x<methodToken>:<sequence>' (sequence 0 = return value), 'f:<mvid>:0x<fieldToken>', 'p:<mvid>:0x<propertyToken>', 'e:<mvid>:0x<eventToken>'.")] string target,
        [Description("Optional case-insensitive substring filter on the attribute type's full name (e.g. 'Authorize').")] string? nameContains = null,
        [Description("Pagination cursor returned by the previous call. Pass 0 or omit for the first page.")] int cursor = 0,
        [Description("Max attributes per page (default 50, capped at 500).")] int pageSize = ListAttributesQuery.DefaultPageSize)
    {
        if (!TryParseAttributeTarget(target, out var parsed, out var parseErr))
            return AssemblyResult.Fail<ListAttributesPage>(parseErr!.Message, parseErr, ErrorRecoveryHint(parseErr));

        if (TryEnsureModuleLoaded(index, parsed.ModuleVersionId, assemblyPathHint: null) is { } loadErr)
            return AssemblyResult.Fail<ListAttributesPage>(loadErr.Message, loadErr, ErrorRecoveryHint(loadErr));

        var query = new ListAttributesQuery(
            NameContains: string.IsNullOrEmpty(nameContains) ? null : nameContains,
            Cursor: cursor > 0 ? cursor : null,
            PageSize: pageSize);

        var result = index.ListAttributes(parsed, query);
        if (!result.IsSuccess)
            return AssemblyResult.Fail<ListAttributesPage>(result.Error!.Message, result.Error,
                ErrorRecoveryHint(result.Error));

        var p = result.Page!;
        var summary = p.Attributes.Count == 0
            ? "No custom attributes matched."
            : $"{p.Attributes.Count} attribute(s){(p.Truncated ? $", more available (nextCursor={p.NextCursor})" : "")}.";
        NextActionHint hint = p.Truncated
            ? new NextActionHint("list_attributes", "Fetch the next page using the returned cursor.",
                new Dictionary<string, object?>
                {
                    ["target"] = target,
                    ["cursor"] = p.NextCursor,
                })
            : new NextActionHint("get_method", "Drill into one of the surrounding methods or types to see what the attribute decorates.");
        return AssemblyResult.Ok(p, summary, hint);
    }

    [McpServerTool(
        Name = "get_type",
        Title = "Get a TypeSummary for a single type, including base type and implemented interfaces",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Returns the full TypeSummary (kind, attributes, generic arity, base type, implemented " +
        "interfaces) for a single type. Identify the type via 'typeHandle' " +
        "('t:<mvid>:0x<typeToken>' from list_types) or via mvidOrPath + typeFullName. " +
        "Cross-module base types and interfaces are reported as TypeReferenceSummary " +
        "(FullName + declaring assembly simple name) without forcing the other module to load.")]
    public static AssemblyResult<TypeSummary> GetType(
        IMetadataIndex index,
        [Description("Type handle 't:<mvid>:0x<typeToken>' as returned by list_types. Pass null/empty if using mvidOrPath+typeFullName instead.")] string? typeHandle = null,
        [Description("MVID GUID or absolute path of the module; only used when typeHandle is omitted.")] string? mvidOrPath = null,
        [Description("Full type name (case-sensitive, '+'-joined for nested types); only used when typeHandle is omitted.")] string? typeFullName = null)
    {
        if (!TryResolveTypeIdentity(index, typeHandle, mvidOrPath, typeFullName,
            out var mvid, out var typeToken, out var resolveErr))
        {
            var resolveHint = resolveErr!.Kind == ErrorKinds.IdentityMalformed
                ? new NextActionHint("list_types", "Use list_types first to discover a valid type handle or full name.")
                : ErrorRecoveryHint(resolveErr);
            return AssemblyResult.Fail<TypeSummary>(resolveErr.Message, resolveErr, resolveHint);
        }

        var result = index.GetTypeDefinition(mvid, typeToken);
        if (!result.IsSuccess)
            return AssemblyResult.Fail<TypeSummary>(result.Error!.Message, result.Error,
                ErrorRecoveryHint(result.Error));

        var t = result.Type!;
        var baseSummary = t.BaseType is null ? "no base type" : $"base = {t.BaseType.FullName}";
        var ifaceCount = t.Interfaces?.Count ?? 0;
        var summary = $"{t.FullName} ({t.Kind}); {baseSummary}; {ifaceCount} interface(s).";
        NextActionHint hint = new("list_derived_types", "Walk the descendants of this type within the same module.",
            new Dictionary<string, object?>
            {
                ["typeHandle"] = HandleFormat.FormatType(t.ModuleVersionId, t.MetadataToken),
            });
        return AssemblyResult.Ok(t, summary, hint);
    }

    [McpServerTool(
        Name = "list_derived_types",
        Title = "List types derived from a given base type within a single module",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Enumerates TypeDef rows in the same module whose base type chain reaches the " +
        "supplied base type. With directOnly=true (default) only immediate subclasses are " +
        "returned; with directOnly=false the full transitive descendant set is returned. " +
        "Scope is intentionally intra-module: cross-module derived-type lookup is deferred. " +
        "Identify the base type via 'typeHandle' or via mvidOrPath + typeFullName, exactly " +
        "like get_type / list_methods.")]
    public static AssemblyResult<ListDerivedTypesPage> ListDerivedTypes(
        IMetadataIndex index,
        [Description("Type handle 't:<mvid>:0x<typeToken>' of the base type, as returned by list_types or get_type.")] string? typeHandle = null,
        [Description("MVID GUID or absolute path of the module; only used when typeHandle is omitted.")] string? mvidOrPath = null,
        [Description("Full base-type name (case-sensitive, '+'-joined for nested types); only used when typeHandle is omitted.")] string? typeFullName = null,
        [Description("When true (default) only immediate subclasses are returned; when false, the full transitive descendant set is returned.")] bool directOnly = true,
        [Description("Pagination cursor returned by the previous call. Pass 0 or omit for the first page.")] int cursor = 0,
        [Description("Max types per page (default 50, capped at 500).")] int pageSize = ListDerivedTypesQuery.DefaultPageSize)
    {
        if (!TryResolveTypeIdentity(index, typeHandle, mvidOrPath, typeFullName,
            out var mvid, out var typeToken, out var resolveErr))
        {
            var resolveHint = resolveErr!.Kind == ErrorKinds.IdentityMalformed
                ? new NextActionHint("list_types", "Use list_types first to discover a valid type handle or full name.")
                : ErrorRecoveryHint(resolveErr);
            return AssemblyResult.Fail<ListDerivedTypesPage>(resolveErr.Message, resolveErr, resolveHint);
        }

        var query = new ListDerivedTypesQuery(
            DirectOnly: directOnly,
            Cursor: cursor > 0 ? cursor : null,
            PageSize: pageSize);

        var result = index.ListDerivedTypes(mvid, typeToken, query);
        if (!result.IsSuccess)
            return AssemblyResult.Fail<ListDerivedTypesPage>(result.Error!.Message, result.Error,
                ErrorRecoveryHint(result.Error));

        var p = result.Page!;
        var summary = p.Types.Count == 0
            ? $"No derived types found for {p.BaseTypeFullName} in this module."
            : $"{p.Types.Count} derived type(s) of {p.BaseTypeFullName}{(p.Truncated ? $", more available (nextCursor={p.NextCursor})" : "")}.";
        NextActionHint hint = p.Truncated
            ? new NextActionHint("list_derived_types", "Fetch the next page using the returned cursor.",
                new Dictionary<string, object?>
                {
                    ["typeHandle"] = HandleFormat.FormatType(p.ModuleVersionId, p.BaseTypeMetadataToken),
                    ["cursor"] = p.NextCursor,
                    ["directOnly"] = directOnly,
                })
            : new NextActionHint("list_methods", "Drill into one of the derived types to inspect its methods.");
        return AssemblyResult.Ok(p, summary, hint);
    }

    [McpServerTool(
        Name = "list_members",
        Title = "List fields, properties, and events of a type",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Enumerates the structural members of a single type — fields, properties, and " +
        "events — with paging and optional kind / name / signature filters. Methods are " +
        "intentionally excluded; use list_methods for those (it carries IL-size + generic " +
        "arity which don't apply to fields/properties/events). Each MemberSummary carries a " +
        "prefix-tagged handle ('f:', 'p:', 'e:') accepted by list_attributes as a target.")]
    public static AssemblyResult<ListMembersPage> ListMembers(
        IMetadataIndex index,
        [Description("Type handle 't:<mvid>:0x<typeToken>' as returned by list_types. Pass null/empty if using mvidOrPath+typeFullName instead.")] string? typeHandle = null,
        [Description("MVID GUID or absolute path of the module; only used when typeHandle is omitted.")] string? mvidOrPath = null,
        [Description("Full type name (case-sensitive, '+'-joined for nested types); only used when typeHandle is omitted.")] string? typeFullName = null,
        [Description("Optional kind filter: Field, Property, or Event. Omit to return all kinds in metadata order (fields, then properties, then events).")] MemberKind? kind = null,
        [Description("Optional case-insensitive substring filter on the member name.")] string? namePattern = null,
        [Description("Optional case-insensitive substring filter on the rendered signature (e.g. 'int', 'EventHandler').")] string? signatureContains = null,
        [Description("Pagination cursor returned by the previous call. Pass 0 or omit for the first page.")] int cursor = 0,
        [Description("Max members per page (default 50, capped at 500).")] int pageSize = ListMembersQuery.DefaultPageSize)
    {
        if (!TryResolveTypeIdentity(index, typeHandle, mvidOrPath, typeFullName,
            out var mvid, out var typeToken, out var resolveErr))
        {
            var resolveHint = resolveErr!.Kind == ErrorKinds.IdentityMalformed
                ? new NextActionHint("list_types", "Use list_types first to discover a valid type handle or full name.")
                : ErrorRecoveryHint(resolveErr);
            return AssemblyResult.Fail<ListMembersPage>(resolveErr.Message, resolveErr, resolveHint);
        }

        var query = new ListMembersQuery(
            Kind: kind,
            NamePattern: string.IsNullOrEmpty(namePattern) ? null : namePattern,
            SignatureContains: string.IsNullOrEmpty(signatureContains) ? null : signatureContains,
            Cursor: cursor > 0 ? cursor : null,
            PageSize: pageSize);

        var result = index.ListMembers(mvid, typeToken, query);
        if (!result.IsSuccess)
            return AssemblyResult.Fail<ListMembersPage>(result.Error!.Message, result.Error,
                ErrorRecoveryHint(result.Error));

        var p = result.Page!;
        var summary = p.Members.Count == 0
            ? $"No members in {p.TypeFullName} matched the filter."
            : $"{p.Members.Count} member(s) in {p.TypeFullName}{(p.Truncated ? $", more available (nextCursor={p.NextCursor})" : "")}.";
        NextActionHint hint = p.Truncated
            ? new NextActionHint("list_members", "Fetch the next page using the returned cursor.",
                new Dictionary<string, object?>
                {
                    ["typeHandle"] = HandleFormat.FormatType(p.ModuleVersionId, p.TypeMetadataToken),
                    ["cursor"] = p.NextCursor,
                })
            : new NextActionHint("list_attributes", "Inspect the custom attributes attached to one of the listed members.",
                new Dictionary<string, object?>
                {
                    ["target"] = p.Members.Count > 0 ? p.Members[0].Handle : null,
                });
        return AssemblyResult.Ok(p, summary, hint);
    }

    private static bool TryParseAttributeTarget(string target, out AttributeTarget parsed, out AssemblyError? error)
    {
        parsed = null!;
        if (string.IsNullOrWhiteSpace(target))
        {
            error = new AssemblyError(ErrorKinds.InvalidArgument, "target is required.");
            return false;
        }
        var s = target.Trim();
        // Order matters: 'pa:' must be tested before 'p:' (no 'p:' kind here but be defensive).
        if (s.StartsWith("a:", StringComparison.Ordinal))
        {
            if (!Guid.TryParse(s.AsSpan(2), out var mvid))
            {
                error = new AssemblyError(ErrorKinds.InvalidArgument,
                    $"could not parse '{target}' as 'a:<mvid>'.");
                return false;
            }
            parsed = AttributeTarget.Assembly(mvid);
            error = null;
            return true;
        }
        if (s.StartsWith("t:", StringComparison.Ordinal))
        {
            if (!TryParseTypeHandle(s, out var mvid, out var typeToken))
            {
                error = new AssemblyError(ErrorKinds.InvalidArgument,
                    $"could not parse '{target}' as 't:<mvid>:0x<typeToken>'.");
                return false;
            }
            parsed = AttributeTarget.Type(mvid, typeToken);
            error = null;
            return true;
        }
        if (s.StartsWith("m:", StringComparison.Ordinal))
        {
            var rest = s.AsSpan(2);
            var sep = rest.IndexOf(':');
            if (sep < 0 || !Guid.TryParse(rest[..sep], out var mvid)
                || !TryParseToken(rest[(sep + 1)..].ToString(), out var methodToken))
            {
                error = new AssemblyError(ErrorKinds.InvalidArgument,
                    $"could not parse '{target}' as 'm:<mvid>:0x<methodToken>'.");
                return false;
            }
            parsed = AttributeTarget.Method(mvid, methodToken);
            error = null;
            return true;
        }
        if (s.StartsWith("pa:", StringComparison.Ordinal))
        {
            var rest = s.AsSpan(3);
            var sep1 = rest.IndexOf(':');
            if (sep1 < 0)
            {
                error = new AssemblyError(ErrorKinds.InvalidArgument,
                    $"could not parse '{target}' as 'pa:<mvid>:0x<methodToken>:<sequence>'.");
                return false;
            }
            if (!Guid.TryParse(rest[..sep1], out var mvid))
            {
                error = new AssemblyError(ErrorKinds.InvalidArgument,
                    $"could not parse '{target}' as 'pa:<mvid>:0x<methodToken>:<sequence>'.");
                return false;
            }
            var afterMvid = rest[(sep1 + 1)..];
            var sep2 = afterMvid.IndexOf(':');
            if (sep2 < 0
                || !TryParseToken(afterMvid[..sep2].ToString(), out var methodToken)
                || !int.TryParse(afterMvid[(sep2 + 1)..], System.Globalization.NumberStyles.Integer,
                       System.Globalization.CultureInfo.InvariantCulture, out var seq)
                || seq < 0)
            {
                error = new AssemblyError(ErrorKinds.InvalidArgument,
                    $"could not parse '{target}' as 'pa:<mvid>:0x<methodToken>:<sequence>'.");
                return false;
            }
            parsed = AttributeTarget.Parameter(mvid, methodToken, seq);
            error = null;
            return true;
        }
        // 'pa:' must be tested before 'p:' (parameter takes precedence).
        if (s.StartsWith("f:", StringComparison.Ordinal))
        {
            if (!TryParsePrefixedHandle(s, 2, out var mvid, out var token))
            {
                error = new AssemblyError(ErrorKinds.InvalidArgument,
                    $"could not parse '{target}' as 'f:<mvid>:0x<fieldToken>'.");
                return false;
            }
            parsed = AttributeTarget.Field(mvid, token);
            error = null;
            return true;
        }
        if (s.StartsWith("p:", StringComparison.Ordinal))
        {
            if (!TryParsePrefixedHandle(s, 2, out var mvid, out var token))
            {
                error = new AssemblyError(ErrorKinds.InvalidArgument,
                    $"could not parse '{target}' as 'p:<mvid>:0x<propertyToken>'.");
                return false;
            }
            parsed = AttributeTarget.Property(mvid, token);
            error = null;
            return true;
        }
        if (s.StartsWith("e:", StringComparison.Ordinal))
        {
            if (!TryParsePrefixedHandle(s, 2, out var mvid, out var token))
            {
                error = new AssemblyError(ErrorKinds.InvalidArgument,
                    $"could not parse '{target}' as 'e:<mvid>:0x<eventToken>'.");
                return false;
            }
            parsed = AttributeTarget.Event(mvid, token);
            error = null;
            return true;
        }
        error = new AssemblyError(ErrorKinds.InvalidArgument,
            $"unknown target prefix in '{target}'. Expected one of: 'a:', 't:', 'm:', 'pa:', 'f:', 'p:', 'e:'.");
        return false;
    }

    private static bool TryParsePrefixedHandle(string s, int prefixLen, out Guid mvid, out int token)
    {
        mvid = default;
        token = 0;
        var rest = s.AsSpan(prefixLen);
        var sep = rest.IndexOf(':');
        if (sep < 0) return false;
        if (!Guid.TryParse(rest[..sep], out mvid)) return false;
        return TryParseToken(rest[(sep + 1)..].ToString(), out token);
    }

    private static AssemblyResult<BatchResponse<T>> RunBatch<T>(
        IReadOnlyList<MethodBatchItem> items,
        Func<MethodBatchItem, int, (T? Data, AssemblyError? Error)> handler,
        Func<int, int, string> summarize,
        string overCapToolName,
        CancellationToken cancellationToken = default)
        where T : class
    {
        if (items is null || items.Count == 0)
        {
            var empty = new BatchResponse<T>(Array.Empty<BatchItemResult<T>>(), 0, 0);
            return AssemblyResult.Ok(
                empty,
                "Batch is empty — nothing to do.",
                new NextActionHint("list_assemblies", "Confirm what is loaded before issuing a batch."));
        }
        if (items.Count > BatchCap)
        {
            var err = new AssemblyError(
                ErrorKinds.BatchTooLarge,
                $"batch contains {items.Count} items, max is {BatchCap}.");
            return AssemblyResult.Fail<BatchResponse<T>>(
                err.Message, err,
                new NextActionHint(
                    overCapToolName,
                    $"Split the input into chunks of at most {BatchCap} items and re-issue."));
        }

        var results = new List<BatchItemResult<T>>(items.Count);
        int ok = 0, fail = 0;
        for (int i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = items[i];
            if (item is null)
            {
                var nullErr = new AssemblyError(ErrorKinds.InvalidArgument, "batch item is null.");
                results.Add(new BatchItemResult<T>(i, new MethodBatchItem(string.Empty, string.Empty), false, null, nullErr));
                fail++;
                continue;
            }
            var (data, error) = handler(item, i);
            if (data is not null && error is null)
            {
                results.Add(new BatchItemResult<T>(i, item, true, data, null));
                ok++;
            }
            else
            {
                results.Add(new BatchItemResult<T>(i, item, false,
                    null,
                    error ?? new AssemblyError(ErrorKinds.InvalidArgument, "handler returned no data and no error.")));
                fail++;
            }
        }

        var response = new BatchResponse<T>(results, ok, fail);
        var summary = summarize(ok, items.Count);
        NextActionHint next = fail > 0
            ? new NextActionHint(
                "get_method",
                $"{fail} item(s) failed — inspect each result's 'error.kind' and re-issue affected items individually.")
            : new NextActionHint(
                "decompile_method",
                "Drill into a specific hotspot's source after the batch enrichment.");
        return AssemblyResult.Ok(response, summary, next);
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

    private static NextActionHint ErrorRecoveryHint(AssemblyError error) => error.Kind switch
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
        ErrorKinds.BatchTooLarge => new NextActionHint(
            // Reachable batch-too-large responses are emitted by RunBatch with the actual tool
            // name; this fallback is only used when an out-of-band caller surfaces the kind
            // without context. Keep it tool-agnostic so we never lie about which tool to retry.
            "list_assemblies",
            $"Batch exceeded the per-call cap of {BatchCap}. Split the items into smaller batches and re-issue the same batch tool."),
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
        _ => new NextActionHint(
            "list_assemblies",
            "Inspect loaded modules and retry the call."),
    };

    /// <summary>
    /// True when the batch item carries any §3.5 generic-instantiation field. Used by tools
    /// that do not accept instantiations (IL-only, source-only) to reject them with a clear
    /// invalid_argument instead of silently ignoring.
    /// </summary>
    private static bool HasGenericInstantiationFields(MethodBatchItem item) =>
        (item.GenericTypeArguments is { Count: > 0 }) ||
        (item.GenericMethodArguments is { Count: > 0 }) ||
        !string.IsNullOrWhiteSpace(item.MethodSpecModuleVersionId) ||
        !string.IsNullOrWhiteSpace(item.MethodSpecMetadataToken);

    /// <summary>
    /// Parses an array of canonical CLR-style type names (see <c>docs/handoff-contract.md §3.5</c>)
    /// into <see cref="GenericTypeName"/> nodes for forwarding through <see cref="MethodIdentity"/>.
    /// Returns <c>true</c> with a non-null list (possibly empty) on success, or <c>false</c> with
    /// the first parser error and a null list. Null/empty input yields <c>(true, null)</c> so the
    /// caller can distinguish "absent" from "empty".
    /// </summary>
    private static bool TryParseGenericArgs(string[]? raw, string paramName,
        out IReadOnlyList<GenericTypeName>? parsed, out AssemblyError? error)
        => TryParseGenericArgs((IReadOnlyList<string>?)raw, paramName, out parsed, out error);

    private static bool TryParseGenericArgs(IReadOnlyList<string>? raw, string paramName,
        out IReadOnlyList<GenericTypeName>? parsed, out AssemblyError? error)
    {
        parsed = null;
        error = null;
        if (raw is null || raw.Count == 0) return true;
        var list = new List<GenericTypeName>(raw.Count);
        for (int i = 0; i < raw.Count; i++)
        {
            if (!GenericTypeName.TryParse(raw[i], out var node, out var kind, out var msg))
            {
                error = new AssemblyError(kind ?? ErrorKinds.InvalidArgument,
                    $"{paramName}[{i}] is invalid: {msg}");
                return false;
            }
            list.Add(node!);
        }
        parsed = list;
        return true;
    }

    private static bool TryParseMethodSpec(string? mvidStr, string? tokenStr,
        out MethodSpecHandle? spec, out AssemblyError? error)
    {
        spec = null;
        error = null;
        bool hasMvid = !string.IsNullOrWhiteSpace(mvidStr);
        bool hasToken = !string.IsNullOrWhiteSpace(tokenStr);
        if (!hasMvid && !hasToken) return true;
        if (hasMvid != hasToken)
        {
            error = new AssemblyError(ErrorKinds.InvalidArgument,
                "methodSpecModuleVersionId and methodSpecMetadataToken must be supplied together.");
            return false;
        }
        if (!Guid.TryParse(mvidStr, out var mvid))
        {
            error = new AssemblyError(ErrorKinds.InvalidArgument,
                $"could not parse methodSpecModuleVersionId '{mvidStr}' as a GUID.");
            return false;
        }
        if (!TryParseToken(tokenStr!, out var token))
        {
            error = new AssemblyError(ErrorKinds.InvalidArgument,
                $"could not parse methodSpecMetadataToken '{tokenStr}' as a 32-bit metadata token.");
            return false;
        }
        spec = new MethodSpecHandle(mvid, token);
        return true;
    }

    private static bool TryResolveModuleId(IMetadataIndex index, string mvidOrPath,
        out Guid mvid, out AssemblyError? error)
    {
        mvid = Guid.Empty;
        if (string.IsNullOrWhiteSpace(mvidOrPath))
        {
            error = new AssemblyError(ErrorKinds.InvalidArgument, "mvidOrPath is required.");
            return false;
        }
        if (Guid.TryParse(mvidOrPath, out var parsed))
        {
            mvid = parsed;
            error = null;
            return true;
        }
        // Treat as path — auto-load (idempotent if MVID already known).
        var load = index.Load(mvidOrPath);
        if (!load.IsSuccess)
        {
            error = load.Error;
            return false;
        }
        mvid = load.Module!.ModuleVersionId;
        error = null;
        return true;
    }

    /// <summary>
    /// Ensures the module identified by <paramref name="mvid"/> is loaded in the index. If it
    /// isn't and <paramref name="assemblyPathHint"/> is non-empty, opens the PE at the hint,
    /// confirms the MVID matches, and loads it idempotently. A hinted path whose MVID differs
    /// is rejected with <see cref="ErrorKinds.MvidMismatch"/> — the path is a hint, never an
    /// override. See issue #4 / docs/handoff-contract.md.
    /// </summary>
    private static AssemblyError? TryEnsureModuleLoaded(IMetadataIndex index, Guid mvid, string? assemblyPathHint)
    {
        foreach (var loaded in index.List())
        {
            if (loaded.ModuleVersionId == mvid) return null;
        }
        var hint = assemblyPathHint;
        if (string.IsNullOrWhiteSpace(hint) && index.TryGetPathHint(mvid, out var lazyHint))
        {
            hint = lazyHint;
        }
        if (string.IsNullOrWhiteSpace(hint))
        {
            return new AssemblyError(ErrorKinds.ModuleNotFound,
                $"no loaded module has MVID {mvid:D}.");
        }
        var probe = index.Probe(hint);
        if (probe.Error is not null) return probe.Error;
        if (probe.Mvid != mvid)
        {
            return new AssemblyError(
                ErrorKinds.MvidMismatch,
                $"assemblyPathHint '{hint}' has MVID {probe.Mvid:D} but the caller requested {mvid:D}.");
        }
        var load = index.Load(hint);
        if (!load.IsSuccess) return load.Error;
        return null;
    }

    private static bool TryResolveTypeIdentity(IMetadataIndex index, string? typeHandle,
        string? mvidOrPath, string? typeFullName,
        out Guid mvid, out int typeToken, out AssemblyError? error)
    {
        mvid = Guid.Empty;
        typeToken = 0;

        if (!string.IsNullOrWhiteSpace(typeHandle))
        {
            if (!TryParseTypeHandle(typeHandle!, out mvid, out typeToken))
            {
                error = new AssemblyError(ErrorKinds.InvalidArgument,
                    $"could not parse typeHandle '{typeHandle}'. Expected 't:<mvid>:0x<typeToken>'.");
                return false;
            }
            error = null;
            return true;
        }

        if (string.IsNullOrWhiteSpace(mvidOrPath) || string.IsNullOrWhiteSpace(typeFullName))
        {
            error = new AssemblyError(ErrorKinds.InvalidArgument,
                "either typeHandle, or both mvidOrPath and typeFullName, are required.");
            return false;
        }

        if (!TryResolveModuleId(index, mvidOrPath, out mvid, out var modErr))
        {
            error = modErr;
            return false;
        }

        if (!TryFindTypeByFullName(index, mvid, typeFullName!, out typeToken))
        {
            error = new AssemblyError(ErrorKinds.IdentityMalformed,
                $"type '{typeFullName}' not found in module {mvid:D}.");
            return false;
        }
        error = null;
        return true;
    }

    private static bool TryParseTypeHandle(string handle, out Guid mvid, out int token)
    {
        mvid = Guid.Empty;
        token = 0;
        var s = handle.Trim();
        if (!s.StartsWith("t:", StringComparison.Ordinal)) return false;
        var rest = s.AsSpan(2);
        var sep = rest.IndexOf(':');
        if (sep < 0) return false;
        if (!Guid.TryParse(rest[..sep], out mvid)) return false;
        return TryParseToken(rest[(sep + 1)..].ToString(), out token);
    }

    private static bool TryFindTypeByFullName(IMetadataIndex index, Guid mvid, string typeFullName, out int token)
    {
        token = 0;
        // Paginate through all types until we find an exact-match full name. The page size cap
        // protects giant assemblies; we read the whole table only on misses, which is acceptable
        // for the typeFullName entry-point (callers should prefer typeHandle from list_types).
        int? cursor = null;
        while (true)
        {
            var page = index.ListTypes(mvid, new ListTypesQuery(
                Cursor: cursor, PageSize: ListTypesQuery.MaxPageSize));
            if (!page.IsSuccess) return false;
            foreach (var t in page.Page!.Types)
            {
                if (string.Equals(t.FullName, typeFullName, StringComparison.Ordinal))
                {
                    token = t.MetadataToken;
                    return true;
                }
            }
            if (!page.Page.Truncated) return false;
            cursor = page.Page.NextCursor;
        }
    }

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