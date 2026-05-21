using System.ComponentModel;
using DotnetAssemblyMcp.Core;
using DotnetAssemblyMcp.Core.Decompilation;
using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Handles;
using DotnetAssemblyMcp.Core.Identity;
using DotnetAssemblyMcp.Core.Metadata;
using ModelContextProtocol.Server;

namespace DotnetAssemblyMcp.Server.Tools;

public sealed partial class AssemblyTools
{
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
}
