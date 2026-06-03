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

    /// <summary>Hard cap on pages walked while enumerating an assembly's types / a type's members.</summary>
    private const int MaxSurfacePages = 10_000;

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

        OverloadResolution resolution = ResolveOverloads(index, mvidOrPath, typeFullName, methodName, contains);
        if (resolution.Selected is not { } selected)
        {
            return AssemblyResult.Fail<MethodExplanation>(resolution.FailSummary!, resolution.FailError!);
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

    /// <summary>
    /// Builds a transitive caller tree for a method resolved by name: each matched overload is a
    /// root and its children are the methods that call it, recursively, across all loaded modules.
    /// Recursion stops at <paramref name="depth"/> levels, at <paramref name="maxNodes"/> rendered
    /// nodes (a budget shared across all roots), or when a caller repeats an ancestor on the
    /// current path (a recursion cycle). A <see cref="FindCallersResult"/> failure on any node is
    /// recorded as a warning and that node is treated as a leaf.
    /// </summary>
    /// <param name="index">The metadata index.</param>
    /// <param name="mvidOrPath">Module MVID, or a filesystem path that is auto-loaded.</param>
    /// <param name="typeFullName">Full declaring type name ('+'-joined for nested types).</param>
    /// <param name="methodName">Method name to resolve (exact by default).</param>
    /// <param name="depth">Maximum number of caller levels below each root (root = level 0).</param>
    /// <param name="maxNodes">Hard cap on total rendered nodes across the whole forest.</param>
    /// <param name="contains">When true, match the name by case-insensitive substring instead of exact.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static AssemblyResult<CallGraph> BuildCallGraph(
        IMetadataIndex index,
        string mvidOrPath,
        string typeFullName,
        string methodName,
        int depth = 3,
        int maxNodes = 200,
        bool contains = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (string.IsNullOrWhiteSpace(methodName))
        {
            return AssemblyResult.Fail<CallGraph>(
                "A method name is required.",
                new AssemblyError(ErrorKinds.InvalidArgument, "methodName is required."));
        }

        if (depth < 0)
        {
            return AssemblyResult.Fail<CallGraph>(
                "depth must be >= 0.",
                new AssemblyError(ErrorKinds.InvalidArgument, "depth must be >= 0."));
        }

        if (maxNodes < 1)
        {
            return AssemblyResult.Fail<CallGraph>(
                "maxNodes must be >= 1.",
                new AssemblyError(ErrorKinds.InvalidArgument, "maxNodes must be >= 1."));
        }

        OverloadResolution resolution = ResolveOverloads(index, mvidOrPath, typeFullName, methodName, contains);
        if (resolution.Selected is not { } selected)
        {
            return AssemblyResult.Fail<CallGraph>(resolution.FailSummary!, resolution.FailError!);
        }

        var warnings = new List<string>();
        var builder = new CallGraphBuilder(index, depth, maxNodes, warnings, cancellationToken);
        var roots = new List<CallGraphNode>(selected.Count);
        var omittedRoots = 0;

        foreach (MethodSummary method in selected.OrderBy(m => m.Signature, StringComparer.Ordinal))
        {
            string display = $"{typeFullName}.{method.MethodName} : {method.Signature}";
            CallGraphNode? root = builder.AddRoot(method.ModuleVersionId, method.MetadataToken, method.Handle, display);
            if (root is null)
            {
                omittedRoots++;
                continue;
            }

            roots.Add(root);
        }

        if (omittedRoots > 0)
        {
            warnings.Add($"{omittedRoots} overload root(s) omitted because the --max-nodes budget ({maxNodes}) was exhausted.");
        }

        var graph = new CallGraph(
            $"{typeFullName}.{methodName}",
            depth,
            roots,
            builder.NodeCount,
            builder.Truncated,
            warnings.Count > 0 ? warnings : null);

        var rootWord = roots.Count == 1 ? "root" : "roots";
        var summary = $"{typeFullName}.{methodName}: {roots.Count} {rootWord}, {builder.NodeCount} node(s){(builder.Truncated ? ", truncated" : string.Empty)}.";
        return AssemblyResult.Ok(graph, summary);
    }

    /// <summary>
    /// Collects every overload of <paramref name="methodName"/> on <paramref name="typeFullName"/>
    /// across all pages, then selects exact (default) or substring (<paramref name="contains"/>)
    /// matches over the complete candidate set. Returns a populated <see cref="OverloadResolution.Selected"/>
    /// on success, or a failure summary + error otherwise. Shared by <see cref="ExplainMethod"/>
    /// and <see cref="BuildCallGraph"/> so the paging, page-cap guard and exact-vs-substring
    /// semantics stay identical.
    /// </summary>
    private static OverloadResolution ResolveOverloads(
        IMetadataIndex index,
        string mvidOrPath,
        string typeFullName,
        string methodName,
        bool contains)
    {
        // The server-side namePattern filter is itself a substring match; we re-filter for exact
        // mode below so the choice between exact / substring is made on the complete candidate set.
        var candidates = new List<MethodSummary>();
        int? cursor = 0;
        var paginationExhausted = false;
        for (var page = 0; page < MaxOverloadPages; page++)
        {
            AssemblyResult<ListMethodsPage> methodsResult = AssemblyOperations.ListMethods(
                index, typeHandle: null, mvidOrPath, typeFullName,
                namePattern: methodName, cursor: cursor ?? 0, pageSize: ListMethodsQuery.MaxPageSize);
            if (methodsResult.IsError)
            {
                return OverloadResolution.Fail(methodsResult.Summary, methodsResult.Error!);
            }

            ListMethodsPage methodsPage = methodsResult.Data!;
            candidates.AddRange(methodsPage.Methods);
            if (methodsPage.NextCursor is null)
            {
                paginationExhausted = true;
                break;
            }

            cursor = methodsPage.NextCursor;
        }

        if (!paginationExhausted)
        {
            // We hit the page cap with more matches still pending. Returning here would risk
            // reporting an exact miss (or a partial --contains set) over an incomplete candidate
            // list, so fail loudly and ask the caller to narrow the query instead.
            return OverloadResolution.Fail(
                $"Too many methods matching '{methodName}' on '{typeFullName}' to enumerate ({candidates.Count}+ across {MaxOverloadPages} pages). Narrow the method name.",
                new AssemblyError(ErrorKinds.PatternTooBroad, $"Overload enumeration exceeded {MaxOverloadPages} pages for '{methodName}' on '{typeFullName}'."));
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
                return OverloadResolution.Fail(
                    $"No method named '{methodName}' on '{typeFullName}'. Did you mean: {string.Join(", ", nearby)}? Use --contains to match substrings.",
                    new AssemblyError(ErrorKinds.InvalidArgument, $"No exact method '{methodName}' on '{typeFullName}'."));
            }

            return OverloadResolution.Fail(
                $"No method matching '{methodName}' on '{typeFullName}'.",
                new AssemblyError(ErrorKinds.InvalidArgument, $"No method matching '{methodName}' on '{typeFullName}'."));
        }

        return OverloadResolution.Ok(selected);
    }

    /// <summary>Outcome of <see cref="ResolveOverloads"/>: either the selected overloads or a failure.</summary>
    private readonly record struct OverloadResolution(
        List<MethodSummary>? Selected,
        string? FailSummary,
        AssemblyError? FailError)
    {
        public static OverloadResolution Ok(List<MethodSummary> selected) => new(selected, null, null);
        public static OverloadResolution Fail(string summary, AssemblyError error) => new(null, summary, error);
    }

    /// <summary>
    /// Stateful recursive caller-tree builder. Holds the shared node budget, depth limit and
    /// warning sink so <see cref="BuildCallGraph"/> can grow a forest of roots against one budget.
    /// </summary>
    private sealed class CallGraphBuilder
    {
        private readonly IMetadataIndex _index;
        private readonly int _depth;
        private readonly int _maxNodes;
        private readonly List<string> _warnings;
        private readonly CancellationToken _cancellationToken;

        public CallGraphBuilder(IMetadataIndex index, int depth, int maxNodes, List<string> warnings, CancellationToken cancellationToken)
        {
            _index = index;
            _depth = depth;
            _maxNodes = maxNodes;
            _warnings = warnings;
            _cancellationToken = cancellationToken;
        }

        public int NodeCount { get; private set; }

        public bool Truncated { get; private set; }

        /// <summary>Adds a root node, or returns null (and flags truncation) when the budget is exhausted.</summary>
        public CallGraphNode? AddRoot(Guid moduleVersionId, int metadataToken, string handle, string display)
        {
            if (NodeCount >= _maxNodes)
            {
                Truncated = true;
                return null;
            }

            NodeCount++;
            var ancestors = new HashSet<(Guid, int)> { (moduleVersionId, metadataToken) };
            return Expand(moduleVersionId, metadataToken, handle, display, level: 0, ancestors);
        }

        private CallGraphNode Expand(Guid mvid, int token, string handle, string display, int level, HashSet<(Guid, int)> ancestors)
        {
            _cancellationToken.ThrowIfCancellationRequested();

            AssemblyResult<FindCallersResult> result = AssemblyOperations.FindCallers(
                _index, mvid.ToString("D", CultureInfo.InvariantCulture), token.ToString(CultureInfo.InvariantCulture));
            if (result.IsError)
            {
                _warnings.Add($"callers of '{display}' unavailable: {result.Summary}");
                return new CallGraphNode(mvid, token, handle, display, Array.Empty<CallGraphNode>());
            }

            IReadOnlyList<CallerRef> callers = result.Data!.Callers;

            // An oversized loaded module can be skipped while building the xref index, which
            // would otherwise silently hide cross-module callers. Surface that the result for
            // this node is partial and flag the whole graph as incomplete.
            if (result.Data!.SkippedOverBudgetModules is { Count: > 0 } skipped)
            {
                _warnings.Add($"callers of '{display}' are partial: {skipped.Count} oversized module(s) skipped.");
                Truncated = true;
            }

            if (callers.Count == 0)
            {
                return new CallGraphNode(mvid, token, handle, display, Array.Empty<CallGraphNode>());
            }

            if (level >= _depth)
            {
                // There are callers but we've hit the depth limit; surface that rather than
                // silently presenting this as a leaf.
                return new CallGraphNode(mvid, token, handle, display, Array.Empty<CallGraphNode>(), DepthLimited: true);
            }

            var children = new List<CallGraphNode>();
            foreach (CallerRef caller in callers.OrderBy(c => c.Display, StringComparer.Ordinal))
            {
                var id = (caller.ModuleVersionId, caller.MetadataToken);
                if (ancestors.Contains(id))
                {
                    if (NodeCount >= _maxNodes)
                    {
                        Truncated = true;
                        break;
                    }

                    NodeCount++;
                    children.Add(new CallGraphNode(caller.ModuleVersionId, caller.MetadataToken, caller.Handle, caller.Display, Array.Empty<CallGraphNode>(), Cycle: true));
                    continue;
                }

                if (NodeCount >= _maxNodes)
                {
                    Truncated = true;
                    break;
                }

                NodeCount++;
                ancestors.Add(id);
                children.Add(Expand(caller.ModuleVersionId, caller.MetadataToken, caller.Handle, caller.Display, level + 1, ancestors));
                ancestors.Remove(id);
            }

            return new CallGraphNode(mvid, token, handle, display, children);
        }
    }

    // ---- diff-assemblies ----------------------------------------------------------------------

    /// <summary>Surface modifiers that participate in a member's change fingerprint.</summary>
    private static readonly HashSet<string> RelevantModifiers = new(StringComparer.Ordinal)
    {
        "public", "protected", "protected internal", "static", "virtual", "abstract", "sealed", "readonly", "const",
    };

    /// <summary>
    /// Diffs the public surface of two assemblies: externally-visible types added / removed, and,
    /// for types present in both, their public / protected members (methods — including property
    /// and event accessors — and fields) added, removed or signature-changed, plus type-shape
    /// changes (kind / base type / interfaces). Member identity is name + generic arity + parameter
    /// list, so a return-type / visibility / modifier change on the same signature is reported as a
    /// change rather than an add+remove. A visible type whose surface cannot be read is excluded
    /// and flagged via <see cref="AssemblyDiff.Incomplete"/> + a warning rather than diffed against
    /// an empty surface.
    /// <para>
    /// Known limitation: type references are compared by full name only (signatures render type
    /// references without assembly identity), so a type that keeps its full name but moves to a
    /// different assembly — e.g. a dependency version swap or a type forward — is not flagged.
    /// </para>
    /// </summary>
    /// <param name="index">The metadata index.</param>
    /// <param name="left">Baseline module MVID or filesystem path (auto-loaded).</param>
    /// <param name="right">Comparand module MVID or filesystem path (auto-loaded).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static AssemblyResult<AssemblyDiff> DiffAssemblies(
        IMetadataIndex index,
        string left,
        string right,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(index);

        var warnings = new List<string>();

        Surface? leftSurface = BuildSurface(index, left, warnings, cancellationToken, out AssemblyError? leftError);
        if (leftSurface is null)
        {
            return AssemblyResult.Fail<AssemblyDiff>($"Could not read left assembly '{left}': {leftError!.Message}", leftError);
        }

        Surface? rightSurface = BuildSurface(index, right, warnings, cancellationToken, out AssemblyError? rightError);
        if (rightSurface is null)
        {
            return AssemblyResult.Fail<AssemblyDiff>($"Could not read right assembly '{right}': {rightError!.Message}", rightError);
        }

        var added = new List<TypeDiff>();
        foreach (string name in rightSurface.VisibleTypes.Keys.Where(k => !leftSurface.VisibleTypes.ContainsKey(k)))
        {
            TypeSummary t = rightSurface.VisibleTypes[name];
            int count = rightSurface.Members.TryGetValue(name, out var m) ? m.Count : 0;
            added.Add(new TypeDiff(name, t.Kind.ToString(), count));
        }

        var removed = new List<TypeDiff>();
        foreach (string name in leftSurface.VisibleTypes.Keys.Where(k => !rightSurface.VisibleTypes.ContainsKey(k)))
        {
            TypeSummary t = leftSurface.VisibleTypes[name];
            int count = leftSurface.Members.TryGetValue(name, out var m) ? m.Count : 0;
            removed.Add(new TypeDiff(name, t.Kind.ToString(), count));
        }

        var changed = new List<TypeDiff>();
        foreach (string name in leftSurface.VisibleTypes.Keys.Where(rightSurface.VisibleTypes.ContainsKey))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A type whose surface could not be read on either side is excluded entirely so we
            // never fabricate diffs from a partial read.
            if (!leftSurface.Members.TryGetValue(name, out var leftMembers) ||
                !rightSurface.Members.TryGetValue(name, out var rightMembers))
            {
                continue;
            }

            var addedMembers = new List<MemberChange>();
            var removedMembers = new List<MemberChange>();
            var changedMembers = new List<MemberSignatureChange>();

            foreach (string key in leftMembers.Keys.Union(rightMembers.Keys, StringComparer.Ordinal))
            {
                bool inLeft = leftMembers.TryGetValue(key, out MemberEntry l);
                bool inRight = rightMembers.TryGetValue(key, out MemberEntry r);
                if (inLeft && !inRight)
                {
                    removedMembers.Add(new MemberChange(l.Name, l.Category, l.Display));
                }
                else if (!inLeft && inRight)
                {
                    addedMembers.Add(new MemberChange(r.Name, r.Category, r.Display));
                }
                else if (!string.Equals(l.Fingerprint, r.Fingerprint, StringComparison.Ordinal))
                {
                    changedMembers.Add(new MemberSignatureChange(l.Name, l.Category, l.Display, r.Display));
                }
            }

            List<string> shape = ShapeChanges(leftSurface.VisibleTypes[name], rightSurface.VisibleTypes[name]);

            if (addedMembers.Count == 0 && removedMembers.Count == 0 && changedMembers.Count == 0 && shape.Count == 0)
            {
                continue;
            }

            addedMembers.Sort((a, b) => string.CompareOrdinal(a.Signature, b.Signature));
            removedMembers.Sort((a, b) => string.CompareOrdinal(a.Signature, b.Signature));
            changedMembers.Sort((a, b) => string.CompareOrdinal(a.Before, b.Before));

            changed.Add(new TypeDiff(
                name,
                leftSurface.VisibleTypes[name].Kind.ToString(),
                MemberCount: 0,
                addedMembers.Count > 0 ? addedMembers : null,
                removedMembers.Count > 0 ? removedMembers : null,
                changedMembers.Count > 0 ? changedMembers : null,
                shape.Count > 0 ? shape : null));
        }

        added.Sort((a, b) => string.CompareOrdinal(a.TypeFullName, b.TypeFullName));
        removed.Sort((a, b) => string.CompareOrdinal(a.TypeFullName, b.TypeFullName));
        changed.Sort((a, b) => string.CompareOrdinal(a.TypeFullName, b.TypeFullName));

        bool incomplete = leftSurface.Unreadable.Count > 0 || rightSurface.Unreadable.Count > 0;

        var diff = new AssemblyDiff(
            left,
            right,
            added,
            removed,
            changed,
            leftSurface.VisibleTypes.Count,
            rightSurface.VisibleTypes.Count,
            incomplete,
            warnings.Count > 0 ? warnings : null);

        var summary = $"+{added.Count} type(s), -{removed.Count} type(s), ~{changed.Count} type(s){(incomplete ? " (incomplete)" : string.Empty)}.";
        return AssemblyResult.Ok(diff, summary);
    }

    private static List<string> ShapeChanges(TypeSummary a, TypeSummary b)
    {
        var changes = new List<string>();
        if (a.Kind != b.Kind)
        {
            changes.Add($"kind: {a.Kind} -> {b.Kind}");
        }

        string? baseA = a.BaseType?.FullName;
        string? baseB = b.BaseType?.FullName;
        if (!string.Equals(baseA, baseB, StringComparison.Ordinal))
        {
            changes.Add($"base: {baseA ?? "(none)"} -> {baseB ?? "(none)"}");
        }

        var ifA = new HashSet<string>((a.Interfaces ?? Array.Empty<TypeReferenceSummary>()).Select(i => i.FullName), StringComparer.Ordinal);
        var ifB = new HashSet<string>((b.Interfaces ?? Array.Empty<TypeReferenceSummary>()).Select(i => i.FullName), StringComparer.Ordinal);
        foreach (string gone in ifA.Except(ifB).OrderBy(x => x, StringComparer.Ordinal))
        {
            changes.Add($"interface -{gone}");
        }

        foreach (string add in ifB.Except(ifA).OrderBy(x => x, StringComparer.Ordinal))
        {
            changes.Add($"interface +{add}");
        }

        return changes;
    }

    /// <summary>
    /// Reads the externally-visible public surface of one assembly. Returns null (with
    /// <paramref name="error"/> set) only when the assembly itself cannot be enumerated; per-type
    /// read failures are recorded in <see cref="Surface.Unreadable"/> + a warning.
    /// </summary>
    private static Surface? BuildSurface(
        IMetadataIndex index,
        string mvidOrPath,
        List<string> warnings,
        CancellationToken cancellationToken,
        out AssemblyError? error)
    {
        error = null;
        var allTypes = new Dictionary<string, TypeSummary>(StringComparer.Ordinal);
        int? cursor = 0;
        for (var page = 0; page < MaxSurfacePages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AssemblyResult<ListTypesPage> typesResult = AssemblyOperations.ListTypes(
                index, mvidOrPath, namespacePrefix: null, nameContains: null, kind: null,
                cursor: cursor ?? 0, pageSize: ListTypesQuery.MaxPageSize);
            if (typesResult.IsError)
            {
                error = typesResult.Error;
                return null;
            }

            ListTypesPage typesPage = typesResult.Data!;
            foreach (TypeSummary t in typesPage.Types)
            {
                allTypes[t.FullName] = t;
            }

            if (typesPage.NextCursor is null)
            {
                break;
            }

            cursor = typesPage.NextCursor;
        }

        var visible = new Dictionary<string, TypeSummary>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, TypeSummary> kv in allTypes)
        {
            if (IsExternallyVisible(kv.Key, allTypes))
            {
                visible[kv.Key] = kv.Value;
            }
        }

        var members = new Dictionary<string, Dictionary<string, MemberEntry>>(StringComparer.Ordinal);
        var unreadable = new HashSet<string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, TypeSummary> kv in visible)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryReadTypeSurface(index, kv.Value, cancellationToken, out Dictionary<string, MemberEntry> typeMembers, out string? failure))
            {
                members[kv.Key] = typeMembers;
            }
            else
            {
                unreadable.Add(kv.Key);
                warnings.Add($"surface of '{kv.Key}' unavailable: {failure}");
            }
        }

        return new Surface(visible, members, unreadable);
    }

    /// <summary>
    /// A type is externally visible only when every type in its declaring chain (split on '+') is
    /// itself public / nested-public. A missing ancestor is treated conservatively as not visible.
    /// </summary>
    private static bool IsExternallyVisible(string fullName, Dictionary<string, TypeSummary> allTypes)
    {
        string[] segments = fullName.Split('+');
        string cumulative = segments[0];
        for (var i = 0; i < segments.Length; i++)
        {
            if (i > 0)
            {
                cumulative = cumulative + "+" + segments[i];
            }

            if (!allTypes.TryGetValue(cumulative, out TypeSummary? t) || !t.IsPublic)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryReadTypeSurface(
        IMetadataIndex index,
        TypeSummary type,
        CancellationToken cancellationToken,
        out Dictionary<string, MemberEntry> members,
        out string? failure)
    {
        members = new Dictionary<string, MemberEntry>(StringComparer.Ordinal);
        failure = null;

        int? cursor = 0;
        for (var page = 0; page < MaxSurfacePages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AssemblyResult<ListMethodsPage> methodsResult = AssemblyOperations.ListMethods(
                index, type.Handle, mvidOrPath: null, typeFullName: null,
                namePattern: null, cursor: cursor ?? 0, pageSize: ListMethodsQuery.MaxPageSize);
            if (methodsResult.IsError)
            {
                failure = methodsResult.Summary;
                return false;
            }

            foreach (MethodSummary m in methodsResult.Data!.Methods)
            {
                if (!IsVisible(m.Attributes))
                {
                    continue;
                }

                string key = $"method:{m.MethodName}`{m.GenericArity.ToString(CultureInfo.InvariantCulture)}{ParamSuffix(m.Signature)}";
                members[key] = new MemberEntry(m.MethodName, "method", Display(m.Attributes, m.Signature), Fingerprint(m.Attributes, m.Signature));
            }

            if (methodsResult.Data!.NextCursor is null)
            {
                break;
            }

            cursor = methodsResult.Data!.NextCursor;
        }

        cursor = 0;
        for (var page = 0; page < MaxSurfacePages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AssemblyResult<ListMembersPage> fieldsResult = AssemblyOperations.ListMembers(
                index, type.Handle, mvidOrPath: null, typeFullName: null, kind: MemberKind.Field,
                namePattern: null, signatureContains: null, cursor: cursor ?? 0, pageSize: ListMembersQuery.MaxPageSize);
            if (fieldsResult.IsError)
            {
                failure = fieldsResult.Summary;
                return false;
            }

            foreach (MemberSummary f in fieldsResult.Data!.Members)
            {
                if (!IsVisible(f.Attributes))
                {
                    continue;
                }

                members[$"field:{f.Name}"] = new MemberEntry(f.Name, "field", Display(f.Attributes, f.Signature), Fingerprint(f.Attributes, f.Signature));
            }

            if (fieldsResult.Data!.NextCursor is null)
            {
                break;
            }

            cursor = fieldsResult.Data!.NextCursor;
        }

        return true;
    }

    /// <summary>True for members that form the externally-callable surface (public / protected).</summary>
    private static bool IsVisible(IReadOnlyList<string> attributes) =>
        attributes.Contains("public") || attributes.Contains("protected") || attributes.Contains("protected internal");

    /// <summary>The parameter-list substring of a method signature, used as part of its identity key.</summary>
    private static string ParamSuffix(string signature)
    {
        int open = signature.IndexOf('(', StringComparison.Ordinal);
        int close = signature.LastIndexOf(')');
        return open >= 0 && close > open ? signature.Substring(open, close - open + 1) : "()";
    }

    /// <summary>
    /// A normalized fingerprint that captures surface-affecting modifiers plus the full signature,
    /// so a visibility / static / virtual / abstract / sealed / readonly / const change on an
    /// otherwise identical member is detected as a change.
    /// </summary>
    private static string Fingerprint(IReadOnlyList<string> attributes, string signature)
    {
        IEnumerable<string> relevant = attributes
            .Where(RelevantModifiers.Contains)
            .OrderBy(a => a, StringComparer.Ordinal);
        return string.Join(" ", relevant) + " | " + signature;
    }

    /// <summary>
    /// A human-facing member display: surface-affecting modifiers prefixed to the signature, so a
    /// modifier-only change (e.g. <c>virtual</c> dropped) is visible in the rendered before / after
    /// even when the bare signature string is unchanged.
    /// </summary>
    private static string Display(IReadOnlyList<string> attributes, string signature)
    {
        IEnumerable<string> relevant = attributes
            .Where(RelevantModifiers.Contains)
            .OrderBy(a => a, StringComparer.Ordinal);
        string prefix = string.Join(" ", relevant);
        return prefix.Length > 0 ? prefix + " " + signature : signature;
    }

    private sealed record Surface(
        Dictionary<string, TypeSummary> VisibleTypes,
        Dictionary<string, Dictionary<string, MemberEntry>> Members,
        HashSet<string> Unreadable);

    private readonly record struct MemberEntry(string Name, string Category, string Display, string Fingerprint);
}
