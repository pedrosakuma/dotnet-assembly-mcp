using DotnetAssemblyMcp.Core;
using DotnetAssemblyMcp.Core.Decompilation;
using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Handles;
using DotnetAssemblyMcp.Core.Identity;
using DotnetAssemblyMcp.Core.Metadata;

namespace DotnetAssemblyMcp.Application;

public static partial class AssemblyOperations
{
    public static AssemblyResult<MethodSummary> GetMethod(
        IMetadataIndex index,
        string moduleVersionId,
        string? metadataToken = null,
        string? typeFullName = null,
        string? methodName = null,
        int genericArity = 0,
        string? assemblyPathHint = null,
        string[]? genericTypeArguments = null,
        string[]? genericMethodArguments = null,
        string? methodSpecModuleVersionId = null,
        string? methodSpecMetadataToken = null,
        bool includeNativeBody = false)
    {
        if (!TryResolveMethodTokens(moduleVersionId, metadataToken, out var mvid, out var token, out var idErr))
            return AssemblyResult.Fail<MethodSummary>(idErr!.Message, idErr, AssemblyErrorRecovery.For(idErr));

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

    public static AssemblyResult<DecompiledMethod> DecompileMethod(
        IDecompiler decompiler,
        IMetadataIndex index,
        string moduleVersionId,
        string? metadataToken = null,
        int maxChars = 0,
        string? assemblyPathHint = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveMethodTokens(moduleVersionId, metadataToken, out var mvid, out var token, out var idErr))
            return AssemblyResult.Fail<DecompiledMethod>(idErr!.Message, idErr, AssemblyErrorRecovery.For(idErr));

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

    public static AssemblyResult<DecompiledType> DecompileType(
        IDecompiler decompiler,
        IMetadataIndex index,
        string moduleVersionId,
        string? metadataToken = null,
        int maxChars = 0,
        string? assemblyPathHint = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveTypeTokens(moduleVersionId, metadataToken, out var mvid, out var token, out var idErr))
            return AssemblyResult.Fail<DecompiledType>(idErr!.Message, idErr, AssemblyErrorRecovery.For(idErr));

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


    public static AssemblyResult<MethodIlResult> GetMethodIl(
        IIlDisassembler disassembler,
        IMetadataIndex index,
        string moduleVersionId,
        string? metadataToken = null,
        string format = "raw",
        int maxBytes = 0,
        int maxLines = 0,
        string? assemblyPathHint = null,
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
    public static AssemblyResult<ListMethodsPage> ListMethods(
        IMetadataIndex index,
        string? typeHandle = null,
        string? mvidOrPath = null,
        string? typeFullName = null,
        string? namePattern = null,
        int cursor = 0,
        int pageSize = ListMethodsQuery.DefaultPageSize)
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

    public static AssemblyResult<FindMethodPage> FindMethod(
        IMetadataIndex index,
        string mvidOrPath,
        string namePattern,
        string? signatureContains = null,
        int? cursor = null,
        int pageSize = FindMethodQuery.DefaultPageSize,
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

    public static AssemblyResult<FindCallersResult> FindCallers(
        IMetadataIndex index,
        string moduleVersionId,
        string? metadataToken = null,
        string? assemblyPathHint = null,
        string[]? genericTypeArguments = null,
        string[]? genericMethodArguments = null,
        string? methodSpecModuleVersionId = null,
        string? methodSpecMetadataToken = null,
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
    public static AssemblyResult<MethodSourceLocation> GetMethodSource(
        IMetadataIndex index,
        string moduleVersionId,
        string? metadataToken = null,
        string? assemblyPathHint = null,
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
