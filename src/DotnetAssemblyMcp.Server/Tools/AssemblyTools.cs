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
    [Description(AssemblyToolDescriptions.LoadAssembly_Summary)]
    public static AssemblyResult<ModuleSummary> LoadAssembly(
        IMetadataIndex index,
        [Description(AssemblyToolDescriptions.LoadAssembly_Path)] string path)
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
    [Description(AssemblyToolDescriptions.ListAssemblies_Summary)]
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
    [Description(AssemblyToolDescriptions.ImportAssemblyManifest_Summary)]
    public static AssemblyResult<ManifestImportResult> ImportAssemblyManifest(
        IMetadataIndex index,
        [Description(AssemblyToolDescriptions.ImportAssemblyManifest_Entries)] IReadOnlyList<ManifestEntry> entries,
        [Description(AssemblyToolDescriptions.ImportAssemblyManifest_Mode)] ManifestImportMode mode = ManifestImportMode.Lazy)
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
    [Description(AssemblyToolDescriptions.GetMethod_Summary)]
    public static AssemblyResult<MethodSummary> GetMethod(
        IMetadataIndex index,
        [Description(AssemblyToolDescriptions.Common_ModuleVersionId)] string moduleVersionId,
        [Description(AssemblyToolDescriptions.Common_MetadataToken)] string metadataToken,
        [Description(AssemblyToolDescriptions.GetMethod_TypeFullName)] string? typeFullName = null,
        [Description(AssemblyToolDescriptions.GetMethod_MethodName)] string? methodName = null,
        [Description(AssemblyToolDescriptions.GetMethod_GenericArity)] int genericArity = 0,
        [Description(AssemblyToolDescriptions.GetMethod_AssemblyPathHint)] string? assemblyPathHint = null,
        [Description(AssemblyToolDescriptions.GetMethod_GenericTypeArguments)] string[]? genericTypeArguments = null,
        [Description(AssemblyToolDescriptions.GetMethod_GenericMethodArguments)] string[]? genericMethodArguments = null,
        [Description(AssemblyToolDescriptions.GetMethod_MethodSpecModuleVersionId)] string? methodSpecModuleVersionId = null,
        [Description(AssemblyToolDescriptions.GetMethod_MethodSpecMetadataToken)] string? methodSpecMetadataToken = null,
        [Description(AssemblyToolDescriptions.GetMethod_IncludeNativeBody)] bool includeNativeBody = false)
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
    [Description(AssemblyToolDescriptions.DecompileMethod_Summary)]
    public static AssemblyResult<DecompiledMethod> DecompileMethod(
        IDecompiler decompiler,
        IMetadataIndex index,
        [Description(AssemblyToolDescriptions.Common_ModuleVersionId)] string moduleVersionId,
        [Description(AssemblyToolDescriptions.Common_MetadataToken)] string metadataToken,
        [Description(AssemblyToolDescriptions.DecompileMethod_MaxChars)] int maxChars = 0,
        [Description(AssemblyToolDescriptions.Common_AssemblyPathHint)] string? assemblyPathHint = null,
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
        Name = "decompile_type",
        Title = "Decompile a whole type (declarations + members + nested types) to C# source",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(AssemblyToolDescriptions.DecompileType_Summary)]
    public static AssemblyResult<DecompiledType> DecompileType(
        IDecompiler decompiler,
        IMetadataIndex index,
        [Description(AssemblyToolDescriptions.Common_ModuleVersionId)] string moduleVersionId,
        [Description(AssemblyToolDescriptions.Common_MetadataToken)] string metadataToken,
        [Description(AssemblyToolDescriptions.DecompileType_MaxChars)] int maxChars = 0,
        [Description(AssemblyToolDescriptions.Common_AssemblyPathHint)] string? assemblyPathHint = null,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(moduleVersionId, out var mvid))
        {
            var err = new AssemblyError(ErrorKinds.InvalidArgument, $"could not parse '{moduleVersionId}' as a GUID.");
            return AssemblyResult.Fail<DecompiledType>(
                "moduleVersionId is not a valid GUID.",
                err,
                AssemblyErrorRecovery.For(err));
        }
        if (!TryParseToken(metadataToken, out var token))
        {
            var err = new AssemblyError(ErrorKinds.InvalidArgument, $"could not parse '{metadataToken}' as a 32-bit metadata token.");
            return AssemblyResult.Fail<DecompiledType>(
                "metadataToken is not a valid integer.",
                err,
                AssemblyErrorRecovery.For(err));
        }

        if (index.EnsureLoaded(mvid, assemblyPathHint) is { } loadErr)
            return AssemblyResult.Fail<DecompiledType>(loadErr.Message, loadErr, AssemblyErrorRecovery.For(loadErr));

        var result = decompiler.DecompileType(mvid, token, maxChars, cancellationToken);
        if (!result.IsSuccess)
        {
            return AssemblyResult.Fail<DecompiledType>(result.Error!.Message, result.Error, AssemblyErrorRecovery.For(result.Error));
        }

        var d = result.Source!;
        var prefix = d.CacheHit ? "[cache hit] " : string.Empty;
        var suffix = d.Truncated ? $" — truncated at {d.SourceLengthChars} chars" : string.Empty;
        return AssemblyResult.Ok(
            d,
            $"{prefix}{d.TypeFullName} — {d.SourceLengthChars} chars of C#{suffix}.",
            new NextActionHint("list_methods", "Drill into a single member of this type after reading the whole-class view.",
                new Dictionary<string, object?>
                {
                    ["mvidOrPath"] = d.ModuleVersionId.ToString("D"),
                    ["typeHandle"] = d.Handle,
                }));
    }


    [McpServerTool(
        Name = "get_method_il",
        Title = "Read a method's IL (raw bytes, ildasm-style text, or outbound-reference scan)",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(AssemblyToolDescriptions.GetMethodIl_Summary)]
    public static AssemblyResult<MethodIlResult> GetMethodIl(
        IIlDisassembler disassembler,
        IMetadataIndex index,
        [Description(AssemblyToolDescriptions.Common_ModuleVersionId)] string moduleVersionId,
        [Description(AssemblyToolDescriptions.Common_MetadataToken)] string metadataToken,
        [Description(AssemblyToolDescriptions.GetMethodIl_Format)] string format = "raw",
        [Description(AssemblyToolDescriptions.GetMethodIl_MaxBytes)] int maxBytes = 0,
        [Description(AssemblyToolDescriptions.GetMethodIl_MaxLines)] int maxLines = 0,
        [Description(AssemblyToolDescriptions.Common_AssemblyPathHint)] string? assemblyPathHint = null,
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
    [Description(AssemblyToolDescriptions.ListTypes_Summary)]
    public static AssemblyResult<ListTypesPage> ListTypes(
        IMetadataIndex index,
        [Description(AssemblyToolDescriptions.Common_MvidOrPathAssembly)] string mvidOrPath,
        [Description(AssemblyToolDescriptions.ListTypes_NamespacePrefix)] string? namespacePrefix = null,
        [Description(AssemblyToolDescriptions.ListTypes_NameContains)] string? nameContains = null,
        [Description(AssemblyToolDescriptions.ListTypes_Kind)] string? kind = null,
        [Description(AssemblyToolDescriptions.Common_PaginationCursor)] int cursor = 0,
        [Description(AssemblyToolDescriptions.Common_MaxTypesPerPage)] int pageSize = ListTypesQuery.DefaultPageSize)
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
    [Description(AssemblyToolDescriptions.ListAssemblyReferences_Summary)]
    public static AssemblyResult<ListAssemblyReferencesPage> ListAssemblyReferences(
        IMetadataIndex index,
        [Description(AssemblyToolDescriptions.Common_MvidOrPathAssembly)] string mvidOrPath)
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
        Name = "list_resources",
        Title = "List ManifestResource rows (embedded resources) of a module",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(AssemblyToolDescriptions.ListResources_Summary)]
    public static AssemblyResult<ListResourcesPage> ListResources(
        IMetadataIndex index,
        [Description(AssemblyToolDescriptions.Common_MvidOrPathAssembly)] string mvidOrPath)
    {
        if (!TryResolveModuleId(index, mvidOrPath, out var mvid, out var loadErr))
            return AssemblyResult.Fail<ListResourcesPage>(loadErr!.Message, loadErr, AssemblyErrorRecovery.For(loadErr));

        var result = index.ListResources(mvid);
        if (!result.IsSuccess)
            return AssemblyResult.Fail<ListResourcesPage>(result.Error!.Message, result.Error,
                AssemblyErrorRecovery.For(result.Error));

        var p = result.Page!;
        var summary = p.Resources.Count == 0
            ? "Module declares no ManifestResource rows."
            : $"{p.Resources.Count} resource(s).";
        return AssemblyResult.Ok(p, summary);
    }

    [McpServerTool(
        Name = "find_string_references",
        Title = "Find every method that emits a given string literal",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(AssemblyToolDescriptions.FindStringReferences_Summary)]
    public static AssemblyResult<FindStringReferencesResult> FindStringReferences(
        IMetadataIndex index,
        [Description(AssemblyToolDescriptions.FindStringReferences_Query)] string query,
        [Description(AssemblyToolDescriptions.FindStringReferences_MatchMode)] string? matchMode = null,
        [Description(AssemblyToolDescriptions.Common_ScopeMvidOrPath)] string? mvidOrPath = null,
        [Description(AssemblyToolDescriptions.Common_MaxHitsDescription)] int maxHits = 0,
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
    [Description(AssemblyToolDescriptions.FindAttributeTargets_Summary)]
    public static AssemblyResult<FindAttributeTargetsResult> FindAttributeTargets(
        IMetadataIndex index,
        [Description(AssemblyToolDescriptions.FindAttributeTargets_AttributeTypeFullName)] string attributeTypeFullName,
        [Description(AssemblyToolDescriptions.Common_ScopeMvidOrPath)] string? mvidOrPath = null,
        [Description(AssemblyToolDescriptions.FindAttributeTargets_TargetKinds)] string? targetKinds = null,
        [Description(AssemblyToolDescriptions.Common_MaxHitsDescription)] int maxHits = 0,
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
    [Description(AssemblyToolDescriptions.FindMemberReferences_Summary)]
    public static AssemblyResult<FindMemberReferencesResult> FindMemberReferences(
        IMetadataIndex index,
        [Description(AssemblyToolDescriptions.FindMemberReferences_MemberHandle)] string memberHandle,
        [Description(AssemblyToolDescriptions.FindMemberReferences_Accessor)] string? accessor = null,
        [Description(AssemblyToolDescriptions.Common_MaxHitsDescription)] int maxHits = 0,
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
    [Description(AssemblyToolDescriptions.ListMethods_Summary)]
    public static AssemblyResult<ListMethodsPage> ListMethods(
        IMetadataIndex index,
        [Description(AssemblyToolDescriptions.Common_TypeHandle)] string? typeHandle = null,
        [Description(AssemblyToolDescriptions.Common_MvidOrPathModule)] string? mvidOrPath = null,
        [Description(AssemblyToolDescriptions.Common_TypeFullNameDescription)] string? typeFullName = null,
        [Description(AssemblyToolDescriptions.ListMethods_NamePattern)] string? namePattern = null,
        [Description(AssemblyToolDescriptions.Common_PaginationCursor)] int cursor = 0,
        [Description(AssemblyToolDescriptions.ListMethods_PageSize)] int pageSize = ListMethodsQuery.DefaultPageSize)
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
    [Description(AssemblyToolDescriptions.FindMethod_Summary)]
    public static AssemblyResult<FindMethodPage> FindMethod(
        IMetadataIndex index,
        [Description(AssemblyToolDescriptions.FindMethod_MvidOrPath)] string mvidOrPath,
        [Description(AssemblyToolDescriptions.FindMethod_NamePattern)] string namePattern,
        [Description(AssemblyToolDescriptions.FindMethod_SignatureContains)] string? signatureContains = null,
        [Description(AssemblyToolDescriptions.FindMethod_Cursor)] int? cursor = null,
        [Description(AssemblyToolDescriptions.FindMethod_PageSize)] int pageSize = FindMethodQuery.DefaultPageSize,
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
    [Description(AssemblyToolDescriptions.FindCallers_Summary)]
    public static AssemblyResult<FindCallersResult> FindCallers(
        IMetadataIndex index,
        [Description(AssemblyToolDescriptions.FindCallers_ModuleVersionId)] string moduleVersionId,
        [Description(AssemblyToolDescriptions.FindCallers_MetadataToken)] string metadataToken,
        [Description(AssemblyToolDescriptions.Common_AssemblyPathHint)] string? assemblyPathHint = null,
        [Description(AssemblyToolDescriptions.FindCallers_GenericTypeArguments)] string[]? genericTypeArguments = null,
        [Description(AssemblyToolDescriptions.FindCallers_GenericMethodArguments)] string[]? genericMethodArguments = null,
        [Description(AssemblyToolDescriptions.FindCallers_MethodSpecModuleVersionId)] string? methodSpecModuleVersionId = null,
        [Description(AssemblyToolDescriptions.FindCallers_MethodSpecMetadataToken)] string? methodSpecMetadataToken = null,
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
    [Description(AssemblyToolDescriptions.FindTypeReferences_Summary)]
    public static AssemblyResult<FindTypeReferencesResult> FindTypeReferences(
        IMetadataIndex index,
        [Description(AssemblyToolDescriptions.FindTypeReferences_TypeHandle)] string? typeHandle = null,
        [Description(AssemblyToolDescriptions.Common_MvidOrPathModule)] string? mvidOrPath = null,
        [Description(AssemblyToolDescriptions.Common_TypeFullNameDescription)] string? typeFullName = null,
        [Description(AssemblyToolDescriptions.FindTypeReferences_AssemblyPathHint)] string? assemblyPathHint = null,
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
    [Description(AssemblyToolDescriptions.GetMethodSource_Summary)]
    public static AssemblyResult<MethodSourceLocation> GetMethodSource(
        IMetadataIndex index,
        [Description(AssemblyToolDescriptions.Common_ModuleVersionId)] string moduleVersionId,
        [Description(AssemblyToolDescriptions.Common_MetadataToken)] string metadataToken,
        [Description(AssemblyToolDescriptions.Common_AssemblyPathHint)] string? assemblyPathHint = null,
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
    [Description(AssemblyToolDescriptions.ListAttributes_Summary)]
    public static AssemblyResult<ListAttributesPage> ListAttributes(
        IMetadataIndex index,
        [Description(AssemblyToolDescriptions.ListAttributes_Target)] string target,
        [Description(AssemblyToolDescriptions.ListAttributes_NameContains)] string? nameContains = null,
        [Description(AssemblyToolDescriptions.Common_PaginationCursor)] int cursor = 0,
        [Description(AssemblyToolDescriptions.ListAttributes_PageSize)] int pageSize = ListAttributesQuery.DefaultPageSize)
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
    [Description(AssemblyToolDescriptions.GetType_Summary)]
    public static AssemblyResult<TypeSummary> GetType(
        IMetadataIndex index,
        [Description(AssemblyToolDescriptions.Common_TypeHandle)] string? typeHandle = null,
        [Description(AssemblyToolDescriptions.Common_MvidOrPathModule)] string? mvidOrPath = null,
        [Description(AssemblyToolDescriptions.Common_TypeFullNameDescription)] string? typeFullName = null)
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
    [Description(AssemblyToolDescriptions.ListDerivedTypes_Summary)]
    public static AssemblyResult<ListDerivedTypesPage> ListDerivedTypes(
        IMetadataIndex index,
        [Description(AssemblyToolDescriptions.ListDerivedTypes_TypeHandle)] string? typeHandle = null,
        [Description(AssemblyToolDescriptions.Common_MvidOrPathModule)] string? mvidOrPath = null,
        [Description(AssemblyToolDescriptions.ListDerivedTypes_TypeFullName)] string? typeFullName = null,
        [Description(AssemblyToolDescriptions.ListDerivedTypes_DirectOnly)] bool directOnly = true,
        [Description(AssemblyToolDescriptions.Common_PaginationCursor)] int cursor = 0,
        [Description(AssemblyToolDescriptions.Common_MaxTypesPerPage)] int pageSize = ListDerivedTypesQuery.DefaultPageSize,
        [Description(AssemblyToolDescriptions.ListDerivedTypes_MatchInstantiation)] string[]? matchInstantiation = null)
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
    [Description(AssemblyToolDescriptions.ListMembers_Summary)]
    public static AssemblyResult<ListMembersPage> ListMembers(
        IMetadataIndex index,
        [Description(AssemblyToolDescriptions.Common_TypeHandle)] string? typeHandle = null,
        [Description(AssemblyToolDescriptions.Common_MvidOrPathModule)] string? mvidOrPath = null,
        [Description(AssemblyToolDescriptions.Common_TypeFullNameDescription)] string? typeFullName = null,
        [Description(AssemblyToolDescriptions.ListMembers_Kind)] MemberKind? kind = null,
        [Description(AssemblyToolDescriptions.ListMembers_NamePattern)] string? namePattern = null,
        [Description(AssemblyToolDescriptions.ListMembers_SignatureContains)] string? signatureContains = null,
        [Description(AssemblyToolDescriptions.Common_PaginationCursor)] int cursor = 0,
        [Description(AssemblyToolDescriptions.ListMembers_PageSize)] int pageSize = ListMembersQuery.DefaultPageSize)
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