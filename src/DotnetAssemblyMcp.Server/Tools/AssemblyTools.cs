using System.ComponentModel;
using DotnetAssemblyMcp.Core;
using DotnetAssemblyMcp.Core.Decompilation;
using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Handles;
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
                AssemblyErrorRecovery.For(result.Error));
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
        [Description("Optional fast-path (§3.5): MethodSpec metadata token (table 0x2B) inside methodSpecModuleVersionId. When supplied alongside genericTypeArguments, the two are cross-checked; a mismatch yields generic_instantiation_mismatch.")] string? methodSpecMetadataToken = null,
        [Description("When true, additionally probes the module for a precompiled native body (ReadyToRun) for this method and populates MethodSummary.NativeBody with (PE path, RVA, size) for handoff to dotnet-native-mcp.disassemble. No-op (NativeBody stays null) for JIT-only assemblies. See docs/handoff-contract.md §3.6.")] bool includeNativeBody = false)
    {
        if (!Guid.TryParse(moduleVersionId, out var mvid))
        {
            var err = new AssemblyError(ErrorKinds.InvalidArgument, $"could not parse '{moduleVersionId}' as a GUID.");
            return AssemblyResult.Fail<MethodSummary>(
                "moduleVersionId is not a valid GUID.",
                err,
                AssemblyErrorRecovery.For(err));
        }

        if (!TryParseToken(metadataToken, out var token))
        {
            var err = new AssemblyError(ErrorKinds.InvalidArgument, $"could not parse '{metadataToken}' as a 32-bit metadata token.");
            return AssemblyResult.Fail<MethodSummary>(
                "metadataToken is not a valid integer.",
                err,
                AssemblyErrorRecovery.For(err));
        }

        if (index.EnsureLoaded(mvid, assemblyPathHint) is { } loadErr)
            return AssemblyResult.Fail<MethodSummary>(loadErr.Message, loadErr, AssemblyErrorRecovery.For(loadErr));

        if (!TryParseGenericArgs(genericTypeArguments, nameof(genericTypeArguments), out var typeArgs, out var parseErr))
            return AssemblyResult.Fail<MethodSummary>(parseErr!.Message, parseErr, AssemblyErrorRecovery.For(parseErr));
        if (!TryParseGenericArgs(genericMethodArguments, nameof(genericMethodArguments), out var methodArgs, out parseErr))
            return AssemblyResult.Fail<MethodSummary>(parseErr!.Message, parseErr, AssemblyErrorRecovery.For(parseErr));

        if (!TryParseMethodSpec(methodSpecModuleVersionId, methodSpecMetadataToken, out var methodSpec, out parseErr))
            return AssemblyResult.Fail<MethodSummary>(parseErr!.Message, parseErr, AssemblyErrorRecovery.For(parseErr));

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
            return AssemblyResult.Fail<MethodSummary>(result.Error!.Message, result.Error, AssemblyErrorRecovery.For(result.Error));
        }

        var m = result.Method!;
        var hints = new List<NextActionHint>
        {
            new("get_method", "Resolve the next frame from the diagnostic payload.",
                new Dictionary<string, object?>
                {
                    ["moduleVersionId"] = m.ModuleVersionId.ToString("D"),
                }),
        };

        if (includeNativeBody)
        {
            var nbResult = index.GetNativeBodyRef(m.ModuleVersionId, m.MetadataToken);
            if (nbResult.IsSuccess && nbResult.Body is { } nb)
            {
                m = m with { NativeBody = nb };
                hints.Add(new NextActionHint(
                    "dotnet-native-mcp.disassemble",
                    $"Method has a {nb.Source} precompiled body — hand off (imagePath, rva, size) to dotnet-native-mcp.disassemble for the actual Iced decode (see docs/handoff-contract.md §3.6).",
                    new Dictionary<string, object?>
                    {
                        ["imagePath"] = nb.PePath,
                        ["rva"] = nb.HotRegion.Rva,
                        ["size"] = nb.HotRegion.Size,
                        ["architecture"] = nb.Architecture.ToString(),
                    }));
            }
            else if (nbResult.IsSuccess)
            {
                hints.Add(new NextActionHint(
                    "dotnet-diagnostics-mcp.capture_method_disasm",
                    "No precompiled native body found in the PE (the method is JIT-only, generic-open, or this module is not R2R-compiled). To inspect actual generated code, attach to a running process with dotnet-diagnostics-mcp.",
                    new Dictionary<string, object?>
                    {
                        ["moduleVersionId"] = m.ModuleVersionId.ToString("D"),
                        ["metadataToken"] = $"0x{m.MetadataToken:X8}",
                    }));
            }
        }

        return AssemblyResult.Ok(
            m,
            $"{m.TypeFullName}.{m.MethodName} — {m.Signature} (IL size {m.IlSize} bytes).",
            [.. hints]);
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
                AssemblyErrorRecovery.For(err));
        }
        if (!TryParseToken(metadataToken, out var token))
        {
            var err = new AssemblyError(ErrorKinds.InvalidArgument, $"could not parse '{metadataToken}' as a 32-bit metadata token.");
            return AssemblyResult.Fail<DecompiledMethod>(
                "metadataToken is not a valid integer.",
                err,
                AssemblyErrorRecovery.For(err));
        }

        if (index.EnsureLoaded(mvid, assemblyPathHint) is { } loadErr)
            return AssemblyResult.Fail<DecompiledMethod>(loadErr.Message, loadErr, AssemblyErrorRecovery.For(loadErr));

        var identity = new MethodIdentity(mvid, token);
        var result = decompiler.Decompile(identity, maxChars, cancellationToken);
        if (!result.IsSuccess)
        {
            return AssemblyResult.Fail<DecompiledMethod>(result.Error!.Message, result.Error, AssemblyErrorRecovery.For(result.Error));
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
        Title = "Read a method's IL (raw bytes, ildasm-style text, or outbound-reference scan)",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Collapsed IL reader — replaces v0.13's get_method_il + get_method_il_text + " +
        "scan_method_il. The 'format' argument selects the projection: " +
        "'raw' (default) returns hex-encoded IL bytes plus max-stack / EH-region / instruction " +
        "counts (cheap; pair with maxBytes); " +
        "'text' returns an ildasm-style textual dump via ICSharpCode.Decompiler's " +
        "ReflectionDisassembler with operand tokens resolved to readable names — useful when " +
        "prefixes (tail./volatile./unaligned.), box/unbox.any placement, or call-vs-callvirt " +
        "dispatch matters (pair with maxLines; cached); " +
        "'scan' walks the IL and returns structural outbound references (called methods, " +
        "accessed fields, used types, string literals) — the building block for cross-reference " +
        "queries without paying decompilation cost. The returned envelope carries the chosen " +
        "format plus exactly one populated payload field (raw / text / scan); the other two " +
        "are null. Generic methods are rendered in their open form for 'text'; IL token " +
        "references in 'scan' are invariant across closed instantiations.")]
    public static AssemblyResult<MethodIlResult> GetMethodIl(
        IIlDisassembler disassembler,
        IMetadataIndex index,
        [Description("ModuleVersionId GUID of the assembly the method belongs to, as a string ('D' format).")] string moduleVersionId,
        [Description("Method definition metadata token (table 0x06). Accepts decimal or hex (0x06000001).")] string metadataToken,
        [Description("Projection: 'raw' (default) for hex IL bytes, 'text' for an ildasm-style textual dump, or 'scan' for outbound-reference extraction.")] string format = "raw",
        [Description("Used by format='raw' only. Optional cap on raw IL bytes encoded in the response. Pass 0 for the server default (4 KiB).")] int maxBytes = 0,
        [Description("Used by format='text' only. Optional cap on output lines. Pass 0 for the server default (256). Hard cap 4096.")] int maxLines = 0,
        [Description("Optional absolute path the producer observed for this assembly (see get_method for semantics).")] string? assemblyPathHint = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseIdentity(moduleVersionId, metadataToken, out var identity, out var err))
            return AssemblyResult.Fail<MethodIlResult>(err!.Message, err, AssemblyErrorRecovery.For(err));

        MethodIlFormat fmt;
        if (string.IsNullOrEmpty(format) || string.Equals(format, "raw", StringComparison.OrdinalIgnoreCase)) fmt = MethodIlFormat.Raw;
        else if (string.Equals(format, "text", StringComparison.OrdinalIgnoreCase)) fmt = MethodIlFormat.Text;
        else if (string.Equals(format, "scan", StringComparison.OrdinalIgnoreCase)) fmt = MethodIlFormat.Scan;
        else
        {
            var argErr = new AssemblyError(ErrorKinds.InvalidArgument,
                $"format must be 'raw', 'text', or 'scan' (got '{format}').");
            return AssemblyResult.Fail<MethodIlResult>(argErr.Message, argErr);
        }

        if (index.EnsureLoaded(identity.ModuleVersionId, assemblyPathHint) is { } loadErr)
            return AssemblyResult.Fail<MethodIlResult>(loadErr.Message, loadErr, AssemblyErrorRecovery.For(loadErr));

        switch (fmt)
        {
            case MethodIlFormat.Raw:
            {
                var result = index.GetIlBody(identity, maxBytes, cancellationToken);
                if (!result.IsSuccess)
                    return AssemblyResult.Fail<MethodIlResult>(result.Error!.Message, result.Error,
                        AssemblyErrorRecovery.For(result.Error));
                var b = result.Body!;
                var suffix = b.IlTruncated ? $" (hex truncated at {b.IlHex.Length / 2} bytes)" : string.Empty;
                return AssemblyResult.Ok(
                    new MethodIlResult(MethodIlFormat.Raw, Raw: b),
                    $"IL body: {b.IlSize} bytes, {b.InstructionCount} instructions, maxStack={b.MaxStack}, {b.ExceptionRegionCount} EH region(s){suffix}.",
                    new NextActionHint("get_method_il", "Switch to format='scan' to extract outbound calls / fields / types from the same method.",
                        new Dictionary<string, object?>
                        {
                            ["moduleVersionId"] = b.ModuleVersionId.ToString("D"),
                            ["metadataToken"] = $"0x{b.MetadataToken:X8}",
                            ["format"] = "scan",
                        }));
            }
            case MethodIlFormat.Text:
            {
                var result = disassembler.Disassemble(identity, maxLines, cancellationToken);
                if (!result.IsSuccess)
                    return AssemblyResult.Fail<MethodIlResult>(result.Error!.Message, result.Error,
                        AssemblyErrorRecovery.For(result.Error));
                var t = result.Text!;
                var prefix = t.CacheHit ? "[cache hit] " : string.Empty;
                var suffix = t.Truncated ? $" — truncated at {t.LineCount} lines" : string.Empty;
                return AssemblyResult.Ok(
                    new MethodIlResult(MethodIlFormat.Text, Text: t),
                    $"{prefix}{t.TypeFullName}.{t.MethodName} — {t.InstructionCount} IL instruction(s), {t.LineCount} line(s){suffix}.",
                    new NextActionHint("decompile_method", "Read the reconstructed C# if the IL is hard to follow.",
                        new Dictionary<string, object?>
                        {
                            ["moduleVersionId"] = t.ModuleVersionId.ToString("D"),
                            ["metadataToken"] = $"0x{t.MetadataToken:X8}",
                        }));
            }
            case MethodIlFormat.Scan:
            default:
            {
                var result = index.ScanIl(identity, cancellationToken);
                if (!result.IsSuccess)
                    return AssemblyResult.Fail<MethodIlResult>(result.Error!.Message, result.Error,
                        AssemblyErrorRecovery.For(result.Error));
                var s = result.Scan!;
                return AssemblyResult.Ok(
                    new MethodIlResult(MethodIlFormat.Scan, Scan: s),
                    $"{s.InstructionCount} instructions: {s.Calls.Count} call(s), {s.Fields.Count} field ref(s), {s.Types.Count} type ref(s), {s.Strings.Count} string literal(s).",
                    new NextActionHint("decompile_method", "Read the C# source if the call list is ambiguous.",
                        new Dictionary<string, object?>
                        {
                            ["moduleVersionId"] = s.ModuleVersionId.ToString("D"),
                            ["metadataToken"] = $"0x{s.MetadataToken:X8}",
                        }));
            }
        }
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
            return AssemblyResult.Fail<ListTypesPage>(loadErr!.Message, loadErr, AssemblyErrorRecovery.For(loadErr));

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
                AssemblyErrorRecovery.For(result.Error));

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
            return AssemblyResult.Fail<ListAssemblyReferencesPage>(loadErr!.Message, loadErr, AssemblyErrorRecovery.For(loadErr));

        var result = index.ListAssemblyReferences(mvid);
        if (!result.IsSuccess)
            return AssemblyResult.Fail<ListAssemblyReferencesPage>(result.Error!.Message, result.Error,
                AssemblyErrorRecovery.For(result.Error));

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
                return AssemblyResult.Fail<FindStringReferencesResult>(loadErr!.Message, loadErr, AssemblyErrorRecovery.For(loadErr));
        }

        var result = index.FindStringReferences(query, mode, mvidFilter, maxHits, cancellationToken);
        if (!result.IsSuccess)
            return AssemblyResult.Fail<FindStringReferencesResult>(result.Error!.Message, result.Error,
                AssemblyErrorRecovery.For(result.Error));

        var r = result.Result!;
        var truncTag = r.Truncated ? " (truncated)" : "";
        var summary = r.Hits.Count == 0
            ? $"No hits across {r.ModulesSearched} module(s)."
            : $"{r.Hits.Count} hit(s) across {r.ModulesSearched} module(s){truncTag}.";
        return AssemblyResult.Ok(r, summary,
            new NextActionHint("get_method", "Inspect a specific caller for context around the literal."));
    }

    [McpServerTool(
        Name = "find_attribute_targets",
        Title = "Find every API decorated with a given custom attribute",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Reverse attribute lookup: returns every assembly / type / method / parameter / field / " +
        "property / event decorated with an attribute whose constructor's declaring type matches " +
        "'attributeTypeFullName' (case-sensitive full name, '+' for nested types — e.g. " +
        "'System.ObsoleteAttribute' or 'Xunit.FactAttribute'). Match is by attribute type identity, " +
        "not by IL spelling, so using-aliases are irrelevant. Scope is every loaded module unless " +
        "'mvidOrPath' is supplied. Optional 'targetKinds' filters the result (comma-separated " +
        "subset of assembly,type,method,parameter,field,property,event). Per-module reverse " +
        "attribute index is built lazily and invalidated with the xref cache on file change. " +
        "Result is capped at 'maxHits' (default 1000, hard cap 10000); 'truncated' = true when hit. " +
        "Typical use: 'find every [Obsolete] API' / 'every [Authorize] controller method'.")]
    public static AssemblyResult<FindAttributeTargetsResult> FindAttributeTargets(
        IMetadataIndex index,
        [Description("Full name of the attribute type, including '+' for nested types (e.g. 'System.ObsoleteAttribute'). Required.")] string attributeTypeFullName,
        [Description("Optional scope. MVID GUID or absolute path of a single module. Omit / pass null to search every loaded module.")] string? mvidOrPath = null,
        [Description("Optional comma-separated subset of {assembly, type, method, parameter, field, property, event}. Omit for all kinds.")] string? targetKinds = null,
        [Description("Optional cap on returned hits (default 1000, hard cap 10000). Pass 0 for default.")] int maxHits = 0,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(attributeTypeFullName))
        {
            var err = new AssemblyError(ErrorKinds.InvalidArgument, "attributeTypeFullName is required.");
            return AssemblyResult.Fail<FindAttributeTargetsResult>(err.Message, err);
        }

        HashSet<AttributeTargetKind>? kindFilter = null;
        if (!string.IsNullOrWhiteSpace(targetKinds))
        {
            kindFilter = new HashSet<AttributeTargetKind>();
            foreach (var raw in targetKinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Enum.TryParse<AttributeTargetKind>(raw, ignoreCase: true, out var k))
                {
                    var err = new AssemblyError(ErrorKinds.InvalidArgument,
                        $"unknown targetKind '{raw}'. Allowed: assembly, type, method, parameter, field, property, event.");
                    return AssemblyResult.Fail<FindAttributeTargetsResult>(err.Message, err);
                }
                kindFilter.Add(k);
            }
        }

        var mvidFilter = Guid.Empty;
        if (!string.IsNullOrEmpty(mvidOrPath))
        {
            if (!TryResolveModuleId(index, mvidOrPath, out mvidFilter, out var loadErr))
                return AssemblyResult.Fail<FindAttributeTargetsResult>(loadErr!.Message, loadErr, AssemblyErrorRecovery.For(loadErr));
        }

        var result = index.FindAttributeTargets(attributeTypeFullName, mvidFilter, kindFilter, maxHits, cancellationToken);
        if (!result.IsSuccess)
            return AssemblyResult.Fail<FindAttributeTargetsResult>(result.Error!.Message, result.Error,
                AssemblyErrorRecovery.For(result.Error));

        var r = result.Result!;
        var truncTag = r.Truncated ? " (truncated)" : "";
        var summary = r.Hits.Count == 0
            ? $"No targets of {attributeTypeFullName} found across {r.ModulesSearched} module(s)."
            : $"{r.Hits.Count} target(s) of {attributeTypeFullName} across {r.ModulesSearched} module(s){truncTag}.";
        return AssemblyResult.Ok(r, summary,
            new NextActionHint("list_attributes", "Inspect the decoded arguments of a specific attribute occurrence."));
    }


    [McpServerTool(
        Name = "find_member_references",
        Title = "Find references to a field, property, or event (collapsed; dispatched by handle prefix)",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Reverse member-access lookup, collapsed from the v0.13 trio of " +
        "find_field_references / find_property_references / find_event_references. The kind " +
        "is dispatched from the handle prefix: 'f:<mvid>:0x<fieldToken>' (field — six opcodes " +
        "ldfld/ldsfld/stfld/stsfld/ldflda/ldsflda), 'p:<mvid>:0x<propertyToken>' (property — " +
        "every call to its getter/setter), 'e:<mvid>:0x<eventToken>' (event — every call to " +
        "its add/remove/raise accessor). The 'accessor' filter applies to properties and " +
        "events only: 'all' (default) / 'getter' / 'setter' for properties, 'all' (default) / " +
        "'add' / 'remove' / 'raise' for events, and 'all' (default) / 'read' / 'write' for " +
        "fields (preserves the v0.13 find_field_references mode= filter). Same-module hits " +
        "use metadata tokens; cross-module hits use the existing call/field-access xref " +
        "indices. Result is capped at 'maxHits' (default 1000, hard cap 10000). The returned " +
        "envelope carries a 'kind' discriminator plus exactly one populated payload field " +
        "(field / property / event); the other two are null.")]
    public static AssemblyResult<FindMemberReferencesResult> FindMemberReferences(
        IMetadataIndex index,
        [Description("Member handle: 'f:<mvid>:0x<fieldToken>' for a field, 'p:<mvid>:0x<propertyToken>' for a property, or 'e:<mvid>:0x<eventToken>' for an event.")] string memberHandle,
        [Description("Optional accessor / mode filter. Field handles: 'all' (default) / 'read' (ldfld/ldsfld + ldflda/ldsflda) / 'write' (stfld/stsfld). Property handles: 'all' (default) / 'getter' / 'setter'. Event handles: 'all' (default) / 'add' / 'remove' / 'raise'.")] string? accessor = null,
        [Description("Optional cap on returned hits (default 1000, hard cap 10000). Pass 0 for default.")] int maxHits = 0,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(memberHandle))
        {
            var err = new AssemblyError(ErrorKinds.InvalidArgument, "memberHandle is required.");
            return AssemblyResult.Fail<FindMemberReferencesResult>(err.Message, err);
        }
        if (!HandleSyntax.TryParseAny(memberHandle, out var kind, out var mvid, out var token, out _))
        {
            var err = new AssemblyError(ErrorKinds.InvalidArgument,
                $"could not parse memberHandle '{memberHandle}'. Expected 'f:<mvid>:0x<fieldToken>', "
                + "'p:<mvid>:0x<propertyToken>', or 'e:<mvid>:0x<eventToken>'.");
            return AssemblyResult.Fail<FindMemberReferencesResult>(err.Message, err);
        }

        switch (kind)
        {
            case HandleKind.Field:
                return DispatchField(index, mvid, token, accessor, memberHandle, maxHits, cancellationToken);
            case HandleKind.Property:
                return DispatchProperty(index, mvid, token, accessor, memberHandle, maxHits, cancellationToken);
            case HandleKind.Event:
                return DispatchEvent(index, mvid, token, accessor, memberHandle, maxHits, cancellationToken);
            default:
                var err = new AssemblyError(ErrorKinds.InvalidArgument,
                    $"memberHandle '{memberHandle}' is a {kind} handle; find_member_references accepts only field (f:), property (p:), or event (e:) handles.");
                return AssemblyResult.Fail<FindMemberReferencesResult>(err.Message, err);
        }
    }

    private static AssemblyResult<FindMemberReferencesResult> DispatchField(
        IMetadataIndex index, Guid mvid, int token, string? accessor, string memberHandle,
        int maxHits, CancellationToken ct)
    {
        var mode = FieldAccessMode.All;
        if (!string.IsNullOrEmpty(accessor))
        {
            if (string.Equals(accessor, "all", StringComparison.OrdinalIgnoreCase)) mode = FieldAccessMode.All;
            else if (string.Equals(accessor, "read", StringComparison.OrdinalIgnoreCase)) mode = FieldAccessMode.Read;
            else if (string.Equals(accessor, "write", StringComparison.OrdinalIgnoreCase)) mode = FieldAccessMode.Write;
            else
            {
                var err = new AssemblyError(ErrorKinds.InvalidArgument,
                    $"accessor must be 'all', 'read', or 'write' for a field handle (got '{accessor}').");
                return AssemblyResult.Fail<FindMemberReferencesResult>(err.Message, err);
            }
        }

        var result = index.FindFieldReferences(mvid, token, mode, maxHits, ct);
        if (!result.IsSuccess)
            return AssemblyResult.Fail<FindMemberReferencesResult>(result.Error!.Message, result.Error,
                AssemblyErrorRecovery.For(result.Error));

        var r = result.Result!;
        var envelope = new FindMemberReferencesResult(MemberHandleKind.Field, Field: r);
        var summary = r.References.Count == 0
            ? $"No references to {r.TargetHandle} across {r.ModulesSearched} module(s)."
            : $"{r.References.Count} reference(s) to {r.TargetHandle} across {r.ModulesSearched} module(s).";
        return AssemblyResult.Ok(envelope, summary,
            new NextActionHint("get_method", "Inspect a specific caller around the field-access offset."));
    }

    private static AssemblyResult<FindMemberReferencesResult> DispatchProperty(
        IMetadataIndex index, Guid mvid, int token, string? accessor, string memberHandle,
        int maxHits, CancellationToken ct)
    {
        var filter = PropertyAccessorFilter.All;
        if (!string.IsNullOrEmpty(accessor))
        {
            if (string.Equals(accessor, "all", StringComparison.OrdinalIgnoreCase)) filter = PropertyAccessorFilter.All;
            else if (string.Equals(accessor, "getter", StringComparison.OrdinalIgnoreCase)) filter = PropertyAccessorFilter.GetterOnly;
            else if (string.Equals(accessor, "setter", StringComparison.OrdinalIgnoreCase)) filter = PropertyAccessorFilter.SetterOnly;
            else
            {
                var err = new AssemblyError(ErrorKinds.InvalidArgument,
                    $"accessor must be 'all', 'getter', or 'setter' for a property handle (got '{accessor}').");
                return AssemblyResult.Fail<FindMemberReferencesResult>(err.Message, err);
            }
        }

        var result = index.FindPropertyReferences(mvid, token, filter, maxHits, ct);
        if (!result.IsSuccess)
            return AssemblyResult.Fail<FindMemberReferencesResult>(result.Error!.Message, result.Error,
                AssemblyErrorRecovery.For(result.Error));

        var r = result.Result!;
        var envelope = new FindMemberReferencesResult(MemberHandleKind.Property, Property: r);
        var summary = r.References.Count == 0
            ? $"No references to {r.TargetHandle} across {r.ModulesSearched} module(s)."
            : $"{r.References.Count} reference(s) to {r.TargetHandle} across {r.ModulesSearched} module(s).";
        return AssemblyResult.Ok(envelope, summary,
            new NextActionHint("get_method", "Inspect a specific caller for context."));
    }

    private static AssemblyResult<FindMemberReferencesResult> DispatchEvent(
        IMetadataIndex index, Guid mvid, int token, string? accessor, string memberHandle,
        int maxHits, CancellationToken ct)
    {
        var filter = EventAccessorFilter.All;
        if (!string.IsNullOrEmpty(accessor))
        {
            if (string.Equals(accessor, "all", StringComparison.OrdinalIgnoreCase)) filter = EventAccessorFilter.All;
            else if (string.Equals(accessor, "add", StringComparison.OrdinalIgnoreCase)) filter = EventAccessorFilter.AdderOnly;
            else if (string.Equals(accessor, "remove", StringComparison.OrdinalIgnoreCase)) filter = EventAccessorFilter.RemoverOnly;
            else if (string.Equals(accessor, "raise", StringComparison.OrdinalIgnoreCase)) filter = EventAccessorFilter.RaiserOnly;
            else
            {
                var err = new AssemblyError(ErrorKinds.InvalidArgument,
                    $"accessor must be 'all', 'add', 'remove', or 'raise' for an event handle (got '{accessor}').");
                return AssemblyResult.Fail<FindMemberReferencesResult>(err.Message, err);
            }
        }

        var result = index.FindEventReferences(mvid, token, filter, maxHits, ct);
        if (!result.IsSuccess)
            return AssemblyResult.Fail<FindMemberReferencesResult>(result.Error!.Message, result.Error,
                AssemblyErrorRecovery.For(result.Error));

        var r = result.Result!;
        var envelope = new FindMemberReferencesResult(MemberHandleKind.Event, Event: r);
        var summary = r.References.Count == 0
            ? $"No references to {r.TargetHandle} across {r.ModulesSearched} module(s)."
            : $"{r.References.Count} reference(s) to {r.TargetHandle} across {r.ModulesSearched} module(s).";
        return AssemblyResult.Ok(envelope, summary,
            new NextActionHint("get_method", "Inspect a specific subscriber for context."));
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
                : AssemblyErrorRecovery.For(resolveErr);
            return AssemblyResult.Fail<ListMethodsPage>(resolveErr.Message, resolveErr, resolveHint);
        }

        var query = new ListMethodsQuery(
            NamePattern: string.IsNullOrEmpty(namePattern) ? null : namePattern,
            Cursor: cursor > 0 ? cursor : null,
            PageSize: pageSize);

        var result = index.ListMethods(mvid, typeToken, query);
        if (!result.IsSuccess)
            return AssemblyResult.Fail<ListMethodsPage>(result.Error!.Message, result.Error,
                AssemblyErrorRecovery.For(result.Error));

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
                    ["typeHandle"] = HandleSyntax.FormatType(p.ModuleVersionId, p.TypeMetadataToken),
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
                    ["typeHandle"] = HandleSyntax.FormatType(p.ModuleVersionId, p.TypeMetadataToken),
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
            return AssemblyResult.Fail<FindMethodPage>(resolveErr!.Message, resolveErr, AssemblyErrorRecovery.For(resolveErr));

        var query = new FindMethodQuery(namePattern, signatureContains, cursor, pageSize);
        var result = index.FindMethod(mvid, query, cancellationToken);
        if (!result.IsSuccess)
            return AssemblyResult.Fail<FindMethodPage>(result.Error!.Message, result.Error,
                AssemblyErrorRecovery.For(result.Error));

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
            return AssemblyResult.Fail<FindCallersResult>(err!.Message, err, AssemblyErrorRecovery.For(err));

        if (index.EnsureLoaded(identity.ModuleVersionId, assemblyPathHint) is { } loadErr)
            return AssemblyResult.Fail<FindCallersResult>(loadErr.Message, loadErr, AssemblyErrorRecovery.For(loadErr));

        if (!TryParseGenericArgs(genericTypeArguments, nameof(genericTypeArguments), out var typeArgs, out var parseErr))
            return AssemblyResult.Fail<FindCallersResult>(parseErr!.Message, parseErr, AssemblyErrorRecovery.For(parseErr));
        if (!TryParseGenericArgs(genericMethodArguments, nameof(genericMethodArguments), out var methodArgs, out parseErr))
            return AssemblyResult.Fail<FindCallersResult>(parseErr!.Message, parseErr, AssemblyErrorRecovery.For(parseErr));
        if (!TryParseMethodSpec(methodSpecModuleVersionId, methodSpecMetadataToken, out var methodSpec, out parseErr))
            return AssemblyResult.Fail<FindCallersResult>(parseErr!.Message, parseErr, AssemblyErrorRecovery.For(parseErr));

        identity = identity with
        {
            TypeGenericArguments = typeArgs,
            MethodGenericArguments = methodArgs,
            MethodSpec = methodSpec,
        };

        var result = index.FindCallers(identity, cancellationToken);
        if (!result.IsSuccess)
            return AssemblyResult.Fail<FindCallersResult>(result.Error!.Message, result.Error,
                AssemblyErrorRecovery.For(result.Error));

        var r = result.Result!;
        var cacheTag = r.FromCache ? " (cached)" : " (built)";
        return AssemblyResult.Ok(
            r,
            $"{r.Callers.Count} caller(s) in {r.ModulesSearched} module{cacheTag}.",
            new NextActionHint("get_method_il", "Inspect a specific caller's outbound references.",
                new Dictionary<string, object?>
                {
                    ["moduleVersionId"] = r.CalleeModuleVersionId.ToString("D"),
                    ["metadataToken"] = $"0x{r.CalleeMetadataToken:X8}",
                    ["format"] = "scan",
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
        "types, method parameters / return types / locals, IL opcodes that bake in a type " +
        "token (newobj, castclass, isinst, box, unbox, ldtoken, generic args, ...), and " +
        "type-hierarchy edges (BaseType + InterfaceImplementation per TypeDef, including " +
        "TypeSpec closures of the target — e.g. 'class C : IRequestHandler<int,string>' " +
        "registers as an InterfaceImplementation site of IRequestHandler`2). Same-module " +
        "hits come from TypeDef tokens; cross-module hits come from TypeRef matching " +
        "(assembly simple name + type full name). Uses the same lazily-built per-module " +
        "xref cache as find_callers; the cache file format version was bumped so the first " +
        "call after upgrade rebuilds.")]
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
                : AssemblyErrorRecovery.For(resolveErr);
            return AssemblyResult.Fail<FindTypeReferencesResult>(resolveErr.Message, resolveErr, resolveHint);
        }

        if (index.EnsureLoaded(mvid, assemblyPathHint) is { } loadErr)
            return AssemblyResult.Fail<FindTypeReferencesResult>(loadErr.Message, loadErr, AssemblyErrorRecovery.For(loadErr));

        var result = index.FindTypeReferences(mvid, typeToken, cancellationToken);
        if (!result.IsSuccess)
            return AssemblyResult.Fail<FindTypeReferencesResult>(result.Error!.Message, result.Error,
                AssemblyErrorRecovery.For(result.Error));

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
            return AssemblyResult.Fail<MethodSourceLocation>(err!.Message, err, AssemblyErrorRecovery.For(err));

        if (index.EnsureLoaded(identity.ModuleVersionId, assemblyPathHint) is { } loadErr)
            return AssemblyResult.Fail<MethodSourceLocation>(loadErr.Message, loadErr, AssemblyErrorRecovery.For(loadErr));

        cancellationToken.ThrowIfCancellationRequested();
        var result = index.GetMethodSource(identity);
        if (!result.IsSuccess)
            return AssemblyResult.Fail<MethodSourceLocation>(result.Error!.Message, result.Error,
                AssemblyErrorRecovery.For(result.Error));

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
            return AssemblyResult.Fail<ListAttributesPage>(parseErr!.Message, parseErr, AssemblyErrorRecovery.For(parseErr));

        if (index.EnsureLoaded(parsed.ModuleVersionId, assemblyPathHint: null) is { } loadErr)
            return AssemblyResult.Fail<ListAttributesPage>(loadErr.Message, loadErr, AssemblyErrorRecovery.For(loadErr));

        var query = new ListAttributesQuery(
            NameContains: string.IsNullOrEmpty(nameContains) ? null : nameContains,
            Cursor: cursor > 0 ? cursor : null,
            PageSize: pageSize);

        var result = index.ListAttributes(parsed, query);
        if (!result.IsSuccess)
            return AssemblyResult.Fail<ListAttributesPage>(result.Error!.Message, result.Error,
                AssemblyErrorRecovery.For(result.Error));

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
                : AssemblyErrorRecovery.For(resolveErr);
            return AssemblyResult.Fail<TypeSummary>(resolveErr.Message, resolveErr, resolveHint);
        }

        var result = index.GetTypeDefinition(mvid, typeToken);
        if (!result.IsSuccess)
            return AssemblyResult.Fail<TypeSummary>(result.Error!.Message, result.Error,
                AssemblyErrorRecovery.For(result.Error));

        var t = result.Type!;
        var baseSummary = t.BaseType is null ? "no base type" : $"base = {t.BaseType.FullName}";
        var ifaceCount = t.Interfaces?.Count ?? 0;
        var summary = $"{t.FullName} ({t.Kind}); {baseSummary}; {ifaceCount} interface(s).";
        NextActionHint hint = new("list_derived_types", "Walk the descendants and implementers of this type across every loaded module.",
            new Dictionary<string, object?>
            {
                ["typeHandle"] = HandleSyntax.FormatType(t.ModuleVersionId, t.MetadataToken),
            });
        return AssemblyResult.Ok(t, summary, hint);
    }

    [McpServerTool(
        Name = "list_derived_types",
        Title = "List subclasses and interface implementers of a type across every loaded module",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Enumerates TypeDef rows across every loaded module whose base-class chain or " +
        "InterfaceImplementation chain reaches the supplied base type. Use it to answer " +
        "'who derives from / implements this type?' refactor questions — same-module hits " +
        "use TypeDef tokens, cross-module hits match by (assembly simple name, type full " +
        "name) against the child module's TypeRef rows. With directOnly=true (default) only " +
        "immediate subclasses / implementers are returned; with directOnly=false the full " +
        "transitive set is returned. Generic-instantiation parents are also matched: a " +
        "query against the open base (e.g. `IRequestHandler\u00602`) finds every closed-arg " +
        "implementer, and the matched closed args are surfaced on `TypeSummary.Instantiation`. " +
        "Pass `matchInstantiation` to narrow the result to a specific closed shape (e.g. " +
        "`['System.Int32','System.String']` returns only `OrderHandler : IRequestHandler<int,string>`). " +
        "Identify the base type via 'typeHandle' or via mvidOrPath + typeFullName, exactly " +
        "like get_type / list_methods.")]
    public static AssemblyResult<ListDerivedTypesPage> ListDerivedTypes(
        IMetadataIndex index,
        [Description("Type handle 't:<mvid>:0x<typeToken>' of the base type, as returned by list_types or get_type.")] string? typeHandle = null,
        [Description("MVID GUID or absolute path of the module; only used when typeHandle is omitted.")] string? mvidOrPath = null,
        [Description("Full base-type name (case-sensitive, '+'-joined for nested types); only used when typeHandle is omitted.")] string? typeFullName = null,
        [Description("When true (default) only immediate subclasses are returned; when false, the full transitive descendant set is returned.")] bool directOnly = true,
        [Description("Pagination cursor returned by the previous call. Pass 0 or omit for the first page.")] int cursor = 0,
        [Description("Max types per page (default 50, capped at 500).")] int pageSize = ListDerivedTypesQuery.DefaultPageSize,
        [Description("Optional CLR reflection-style full names for the base type's generic arguments (e.g. ['System.Int32','System.String']) per docs/handoff-contract.md \u00A73.5. When supplied, only TypeSpec parent edges whose closed args match element-wise are returned; non-generic parents are excluded. Omit for open match (default).")] string[]? matchInstantiation = null)
    {
        if (!TryResolveTypeIdentity(index, typeHandle, mvidOrPath, typeFullName,
            out var mvid, out var typeToken, out var resolveErr))
        {
            var resolveHint = resolveErr!.Kind == ErrorKinds.IdentityMalformed
                ? new NextActionHint("list_types", "Use list_types first to discover a valid type handle or full name.")
                : AssemblyErrorRecovery.For(resolveErr);
            return AssemblyResult.Fail<ListDerivedTypesPage>(resolveErr.Message, resolveErr, resolveHint);
        }

        if (!TryParseGenericArgs(matchInstantiation, nameof(matchInstantiation), out var matchArgs, out var matchErr))
            return AssemblyResult.Fail<ListDerivedTypesPage>(matchErr!.Message, matchErr, AssemblyErrorRecovery.For(matchErr));

        var query = new ListDerivedTypesQuery(
            DirectOnly: directOnly,
            Cursor: cursor > 0 ? cursor : null,
            PageSize: pageSize,
            MatchInstantiation: matchArgs);

        var result = index.ListDerivedTypes(mvid, typeToken, query);
        if (!result.IsSuccess)
            return AssemblyResult.Fail<ListDerivedTypesPage>(result.Error!.Message, result.Error,
                AssemblyErrorRecovery.For(result.Error));

        var p = result.Page!;
        var summary = p.Types.Count == 0
            ? $"No derived types found for {p.BaseTypeFullName} in this module."
            : $"{p.Types.Count} derived type(s) of {p.BaseTypeFullName}{(p.Truncated ? $", more available (nextCursor={p.NextCursor})" : "")}.";
        NextActionHint hint = p.Truncated
            ? new NextActionHint("list_derived_types", "Fetch the next page using the returned cursor.",
                new Dictionary<string, object?>
                {
                    ["typeHandle"] = HandleSyntax.FormatType(p.ModuleVersionId, p.BaseTypeMetadataToken),
                    ["cursor"] = p.NextCursor,
                    ["directOnly"] = directOnly,
                    ["matchInstantiation"] = matchInstantiation,
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
                : AssemblyErrorRecovery.For(resolveErr);
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
                AssemblyErrorRecovery.For(result.Error));

        var p = result.Page!;
        var summary = p.Members.Count == 0
            ? $"No members in {p.TypeFullName} matched the filter."
            : $"{p.Members.Count} member(s) in {p.TypeFullName}{(p.Truncated ? $", more available (nextCursor={p.NextCursor})" : "")}.";
        NextActionHint hint = p.Truncated
            ? new NextActionHint("list_members", "Fetch the next page using the returned cursor.",
                new Dictionary<string, object?>
                {
                    ["typeHandle"] = HandleSyntax.FormatType(p.ModuleVersionId, p.TypeMetadataToken),
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
        if (!HandleSyntax.TryParseAny(target, out var kind, out var mvid, out var token, out var sequence))
        {
            error = new AssemblyError(ErrorKinds.InvalidArgument,
                $"could not parse '{target}'. Expected one of: 'a:<mvid>', 't:<mvid>:0x<token>', "
                + "'m:<mvid>:0x<token>', 'pa:<mvid>:0x<methodToken>:<sequence>', 'f:<mvid>:0x<token>', "
                + "'p:<mvid>:0x<token>', 'e:<mvid>:0x<token>'.");
            return false;
        }
        parsed = kind switch
        {
            HandleKind.Assembly => AttributeTarget.Assembly(mvid),
            HandleKind.Type => AttributeTarget.Type(mvid, token),
            HandleKind.Method => AttributeTarget.Method(mvid, token),
            HandleKind.Parameter => AttributeTarget.Parameter(mvid, token, sequence),
            HandleKind.Field => AttributeTarget.Field(mvid, token),
            HandleKind.Property => AttributeTarget.Property(mvid, token),
            HandleKind.Event => AttributeTarget.Event(mvid, token),
            _ => null!,
        };
        if (parsed is null)
        {
            error = new AssemblyError(ErrorKinds.InvalidArgument, $"unsupported handle kind '{kind}' in '{target}'.");
            return false;
        }
        error = null;
        return true;
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


    /// <summary>
    /// Parses an array of canonical CLR-style type names (see <c>docs/handoff-contract.md §3.5</c>)
    /// into <see cref="GenericTypeName"/> nodes for forwarding through <see cref="MethodIdentity"/>.
    /// Returns <c>true</c> with a non-null list (possibly empty) on success, or <c>false</c> with
    /// the first parser error and a null list. Null/empty input yields <c>(true, null)</c> so the
    /// caller can distinguish "absent" from "empty".
    /// </summary>
    private static bool TryParseGenericArgs(string[]? raw, string paramName,
        out IReadOnlyList<GenericTypeName>? parsed, out AssemblyError? error)
    {
        parsed = null;
        error = null;
        if (raw is null || raw.Length == 0) return true;
        var list = new List<GenericTypeName>(raw.Length);
        for (int i = 0; i < raw.Length; i++)
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

    private static bool TryResolveTypeIdentity(IMetadataIndex index, string? typeHandle,
        string? mvidOrPath, string? typeFullName,
        out Guid mvid, out int typeToken, out AssemblyError? error)
    {
        mvid = Guid.Empty;
        typeToken = 0;

        if (!string.IsNullOrWhiteSpace(typeHandle))
        {
            if (!HandleSyntax.TryParseType(typeHandle!, out mvid, out typeToken))
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

        var find = index.FindTypeByFullName(mvid, typeFullName!);
        if (!find.IsSuccess)
        {
            error = find.Error;
            return false;
        }
        typeToken = find.Type!.MetadataToken;
        error = null;
        return true;
    }

    private static bool TryParseToken(string raw, out int token) => HandleSyntax.TryParseToken(raw, out token);
}