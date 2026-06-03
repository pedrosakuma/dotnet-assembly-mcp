using System.Globalization;
using DotnetAssemblyMcp.Core;
using DotnetAssemblyMcp.Core.Decompilation;
using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Metadata;

namespace DotnetAssemblyMcp.Application;

/// <summary>
/// Higher-level, human-oriented composed workflows that chain several
/// <see cref="AssemblyOperations"/> calls into a single answer. These exist to close the
/// "user-side analysis gap": a human at a terminal should be able to inspect or decompile a
/// type / method by <em>name</em> without first chasing MVIDs, type handles and metadata tokens
/// across four separate calls.
/// <para>
/// Deliberately NOT exposed as MCP tools — the MCP surface stays at ~22 primitives and the
/// agent composes them itself. These live in <c>Application</c> (not the CLI) so the
/// orchestration is reusable and unit-testable independently of console rendering.
/// </para>
/// </summary>
public static class AssemblyAnalysisOperations
{
    /// <summary>Hard cap on pages walked while collecting overloads, to bound pathological inputs.</summary>
    private const int MaxOverloadPages = 200;

    /// <summary>
    /// Resolves a type by name and gathers a one-shot overview: the <see cref="TypeSummary"/>
    /// plus its attributes, members (fields / properties / events) and methods. Sub-lookups
    /// degrade to empty lists with a recorded warning rather than failing the whole call, so a
    /// resolvable type always yields a useful overview.
    /// </summary>
    /// <param name="index">The metadata index.</param>
    /// <param name="mvidOrPath">Module MVID, or a filesystem path that is auto-loaded.</param>
    /// <param name="typeFullName">Full type name ('+'-joined for nested types).</param>
    public static AssemblyResult<TypeExplanation> ExplainType(
        IMetadataIndex index,
        string mvidOrPath,
        string typeFullName)
    {
        ArgumentNullException.ThrowIfNull(index);

        AssemblyResult<TypeSummary> typeResult = AssemblyOperations.GetType(index, typeHandle: null, mvidOrPath, typeFullName);
        if (typeResult.IsError)
        {
            return AssemblyResult.Fail<TypeExplanation>(typeResult.Summary, typeResult.Error!);
        }

        TypeSummary type = typeResult.Data!;
        var warnings = new List<string>();

        IReadOnlyList<AttributeSummary> attributes = Array.Empty<AttributeSummary>();
        bool attributesTruncated = false;
        AssemblyResult<ListAttributesPage> attrResult = AssemblyOperations.ListAttributes(
            index, type.Handle, nameContains: null, cursor: 0, pageSize: ListAttributesQuery.MaxPageSize);
        if (attrResult.IsError)
        {
            warnings.Add($"attributes unavailable: {attrResult.Summary}");
        }
        else
        {
            attributes = attrResult.Data!.Attributes;
            attributesTruncated = attrResult.Data!.Truncated;
        }

        IReadOnlyList<MemberSummary> members = Array.Empty<MemberSummary>();
        bool membersTruncated = false;
        AssemblyResult<ListMembersPage> memberResult = AssemblyOperations.ListMembers(
            index, type.Handle, mvidOrPath: null, typeFullName: null, kind: null,
            namePattern: null, signatureContains: null, cursor: 0, pageSize: ListMembersQuery.MaxPageSize);
        if (memberResult.IsError)
        {
            warnings.Add($"members unavailable: {memberResult.Summary}");
        }
        else
        {
            members = memberResult.Data!.Members;
            membersTruncated = memberResult.Data!.Truncated;
        }

        IReadOnlyList<MethodSummary> methods = Array.Empty<MethodSummary>();
        bool methodsTruncated = false;
        AssemblyResult<ListMethodsPage> methodResult = AssemblyOperations.ListMethods(
            index, type.Handle, mvidOrPath: null, typeFullName: null,
            namePattern: null, cursor: 0, pageSize: ListMethodsQuery.MaxPageSize);
        if (methodResult.IsError)
        {
            warnings.Add($"methods unavailable: {methodResult.Summary}");
        }
        else
        {
            methods = methodResult.Data!.Methods;
            methodsTruncated = methodResult.Data!.Truncated;
        }

        var explanation = new TypeExplanation(
            type,
            attributes,
            members,
            methods,
            membersTruncated,
            methodsTruncated,
            attributesTruncated,
            warnings.Count > 0 ? warnings : null);

        var summary = $"{type.FullName}: {members.Count} member(s), {methods.Count} method(s), {attributes.Count} attribute(s).";
        return AssemblyResult.Ok(explanation, summary);
    }

    /// <summary>
    /// Resolves a method by name on a named type and returns one detail block per overload —
    /// signature, source location (file/line via PDB) and, when <paramref name="decompile"/> is
    /// set, the decompiled C#. Defaults to an exact (case-insensitive) name match; set
    /// <paramref name="contains"/> for substring matching. All matching overloads are collected
    /// across every page so the selection is never silently truncated.
    /// </summary>
    /// <param name="decompiler">The decompiler (only used when <paramref name="decompile"/> is true).</param>
    /// <param name="index">The metadata index.</param>
    /// <param name="mvidOrPath">Module MVID, or a filesystem path that is auto-loaded.</param>
    /// <param name="typeFullName">Full declaring type name ('+'-joined for nested types).</param>
    /// <param name="methodName">Method name to resolve (exact by default).</param>
    /// <param name="contains">When true, match the name by case-insensitive substring instead of exact.</param>
    /// <param name="decompile">When true, decompile each selected overload to C#.</param>
    /// <param name="maxChars">Per-method decompilation character cap (0 = engine default).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static AssemblyResult<MethodExplanation> ExplainMethod(
        IDecompiler decompiler,
        IMetadataIndex index,
        string mvidOrPath,
        string typeFullName,
        string methodName,
        bool contains = false,
        bool decompile = false,
        int maxChars = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (string.IsNullOrWhiteSpace(methodName))
        {
            return AssemblyResult.Fail<MethodExplanation>(
                "A method name is required.",
                new AssemblyError(ErrorKinds.InvalidArgument, "methodName is required."));
        }

        // Collect every method on the type whose short name contains the query, across all pages.
        // The server-side namePattern filter is itself a substring match; we re-filter for exact
        // mode below so the choice between exact / substring is made on the complete candidate set.
        var candidates = new List<MethodSummary>();
        int? cursor = 0;
        for (var page = 0; page < MaxOverloadPages; page++)
        {
            AssemblyResult<ListMethodsPage> methodsResult = AssemblyOperations.ListMethods(
                index, typeHandle: null, mvidOrPath, typeFullName,
                namePattern: methodName, cursor: cursor ?? 0, pageSize: ListMethodsQuery.MaxPageSize);
            if (methodsResult.IsError)
            {
                return AssemblyResult.Fail<MethodExplanation>(methodsResult.Summary, methodsResult.Error!);
            }

            ListMethodsPage methodsPage = methodsResult.Data!;
            candidates.AddRange(methodsPage.Methods);
            if (methodsPage.NextCursor is null)
            {
                break;
            }

            cursor = methodsPage.NextCursor;
        }

        List<MethodSummary> exact = candidates
            .Where(m => string.Equals(m.MethodName, methodName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        List<MethodSummary> selected = contains ? candidates : exact;
        if (selected.Count == 0)
        {
            if (!contains && candidates.Count > 0)
            {
                IEnumerable<string> nearby = candidates.Select(m => m.MethodName).Distinct(StringComparer.OrdinalIgnoreCase).Take(10);
                return AssemblyResult.Fail<MethodExplanation>(
                    $"No method named '{methodName}' on '{typeFullName}'. Did you mean: {string.Join(", ", nearby)}? Use --contains to match substrings.",
                    new AssemblyError(ErrorKinds.InvalidArgument, $"No exact method '{methodName}' on '{typeFullName}'."));
            }

            return AssemblyResult.Fail<MethodExplanation>(
                $"No method matching '{methodName}' on '{typeFullName}'.",
                new AssemblyError(ErrorKinds.InvalidArgument, $"No method matching '{methodName}' on '{typeFullName}'."));
        }

        var warnings = new List<string>();
        string? pathHint = Guid.TryParse(mvidOrPath, out _) ? null : mvidOrPath;
        var details = new List<MethodOverloadDetail>(selected.Count);
        foreach (MethodSummary method in selected.OrderBy(m => m.Signature, StringComparer.Ordinal))
        {
            string mvid = method.ModuleVersionId.ToString("D", CultureInfo.InvariantCulture);
            string token = method.MetadataToken.ToString(CultureInfo.InvariantCulture);

            MethodSourceLocation? source = null;
            AssemblyResult<MethodSourceLocation> sourceResult = AssemblyOperations.GetMethodSource(index, mvid, token, pathHint, cancellationToken);
            if (sourceResult.IsError)
            {
                warnings.Add($"source for '{method.Signature}' unavailable: {sourceResult.Summary}");
            }
            else
            {
                source = sourceResult.Data;
            }

            string? decompiled = null;
            if (decompile)
            {
                AssemblyResult<DecompiledMethod> decompiledResult = AssemblyOperations.DecompileMethod(
                    decompiler, index, mvid, token, maxChars, pathHint, cancellationToken);
                if (decompiledResult.IsError)
                {
                    warnings.Add($"decompilation of '{method.Signature}' failed: {decompiledResult.Summary}");
                }
                else
                {
                    decompiled = decompiledResult.Data!.Source;
                }
            }

            details.Add(new MethodOverloadDetail(method, source, decompiled));
        }

        var explanation = new MethodExplanation(
            typeFullName,
            methodName,
            details,
            Exact: !contains,
            warnings.Count > 0 ? warnings : null);

        var summary = $"{typeFullName}.{methodName}: {details.Count} overload(s).";
        return AssemblyResult.Ok(explanation, summary);
    }
}
