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
        [Description("Optional absolute path the producer observed for this assembly. Used only when the MVID is not yet loaded: if the file at the path has a matching MVID it is loaded transparently; if it has a different MVID the call fails with mvid_mismatch (the path is a hint, never an override).")] string? assemblyPathHint = null)
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

        var identity = new MethodIdentity(mvid, token, TypeFullName: typeFullName, MethodName: methodName, GenericArity: genericArity);
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
        "confirm the identity exists, then call this for the body.")]
    public static AssemblyResult<DecompiledMethod> DecompileMethod(
        IDecompiler decompiler,
        IMetadataIndex index,
        [Description("ModuleVersionId GUID of the assembly the method belongs to, as a string ('D' format).")] string moduleVersionId,
        [Description("Method definition metadata token (table 0x06). Accepts decimal or hex (0x06000001).")] string metadataToken,
        [Description("Optional cap on returned characters. Pass 0 to use the server default (16 KiB).")] int maxChars = 0,
        [Description("Optional absolute path the producer observed for this assembly (see get_method for semantics).")] string? assemblyPathHint = null)
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
        var result = decompiler.Decompile(identity, maxChars);
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
        [Description("Optional absolute path the producer observed for this assembly (see get_method for semantics).")] string? assemblyPathHint = null)
    {
        if (!TryParseIdentity(moduleVersionId, metadataToken, out var identity, out var err))
            return AssemblyResult.Fail<IlMethodBody>(err!.Message, err, ErrorRecoveryHint(err));

        if (TryEnsureModuleLoaded(index, identity.ModuleVersionId, assemblyPathHint) is { } loadErr)
            return AssemblyResult.Fail<IlMethodBody>(loadErr.Message, loadErr, ErrorRecoveryHint(loadErr));

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
        [Description("Method definition metadata token (table 0x06). Accepts decimal or hex (0x06000001).")] string metadataToken,
        [Description("Optional absolute path the producer observed for this assembly (see get_method for semantics).")] string? assemblyPathHint = null)
    {
        if (!TryParseIdentity(moduleVersionId, metadataToken, out var identity, out var err))
            return AssemblyResult.Fail<IlScanResult>(err!.Message, err, ErrorRecoveryHint(err));

        if (TryEnsureModuleLoaded(index, identity.ModuleVersionId, assemblyPathHint) is { } loadErr)
            return AssemblyResult.Fail<IlScanResult>(loadErr.Message, loadErr, ErrorRecoveryHint(loadErr));

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
        [Description("Max matches per page (default 20, capped at 200).")] int pageSize = FindMethodQuery.DefaultPageSize)
    {
        if (!TryResolveModuleId(index, mvidOrPath, out var mvid, out var resolveErr))
            return AssemblyResult.Fail<FindMethodPage>(resolveErr!.Message, resolveErr, ErrorRecoveryHint(resolveErr));

        var query = new FindMethodQuery(namePattern, signatureContains, cursor, pageSize);
        var result = index.FindMethod(mvid, query);
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
        [Description("Optional absolute path the producer observed for this assembly (see get_method for semantics).")] string? assemblyPathHint = null)
    {
        if (!TryParseIdentity(moduleVersionId, metadataToken, out var identity, out var err))
            return AssemblyResult.Fail<FindCallersResult>(err!.Message, err, ErrorRecoveryHint(err));

        if (TryEnsureModuleLoaded(index, identity.ModuleVersionId, assemblyPathHint) is { } loadErr)
            return AssemblyResult.Fail<FindCallersResult>(loadErr.Message, loadErr, ErrorRecoveryHint(loadErr));

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
        _ => new NextActionHint(
            "list_assemblies",
            "Inspect loaded modules and retry the call."),
    };

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
        var load = index.Load(hint);
        if (!load.IsSuccess) return load.Error;
        if (load.Module!.ModuleVersionId != mvid)
        {
            return new AssemblyError(
                ErrorKinds.MvidMismatch,
                $"assemblyPathHint '{hint}' has MVID {load.Module.ModuleVersionId:D} but the caller requested {mvid:D}.");
        }
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