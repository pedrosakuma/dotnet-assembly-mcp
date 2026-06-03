using DotnetAssemblyMcp.Core;
using DotnetAssemblyMcp.Core.Decompilation;
using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Handles;
using DotnetAssemblyMcp.Core.Identity;
using DotnetAssemblyMcp.Core.Metadata;

namespace DotnetAssemblyMcp.Application;

public static partial class AssemblyOperations
{
    public static AssemblyResult<ListTypesPage> ListTypes(
        IMetadataIndex index,
        string mvidOrPath,
        string? namespacePrefix = null,
        string? nameContains = null,
        string? kind = null,
        int cursor = 0,
        int pageSize = ListTypesQuery.DefaultPageSize)
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

    public static AssemblyResult<ListAssemblyReferencesPage> ListAssemblyReferences(
        IMetadataIndex index,
        string mvidOrPath)
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

    public static AssemblyResult<ListResourcesPage> ListResources(
        IMetadataIndex index,
        string mvidOrPath)
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

        // Resource bytes are out of scope for this server, so the useful next step is either to
        // load a satellite assembly that owns forwarded (localized) resources, or to keep
        // exploring the current module's types.
        var forwarded = p.Resources.FirstOrDefault(r =>
            r.Implementation == ResourceImplementationKind.ForwardedToAssembly);
        NextActionHint hint = forwarded is not null
            ? new NextActionHint("load_assembly",
                $"Resource(s) are forwarded to '{forwarded.LinkedAssemblyName}' (e.g. a satellite "
                + ".resources.dll); locate that assembly on disk and load_assembly it to inspect them.")
            : new NextActionHint("list_types", "Enumerate the module's types to keep exploring.",
                new Dictionary<string, object?> { ["mvidOrPath"] = mvid.ToString("D") });
        return AssemblyResult.Ok(p, summary, hint);
    }
    public static AssemblyResult<ListAttributesPage> ListAttributes(
        IMetadataIndex index,
        string target,
        string? nameContains = null,
        int cursor = 0,
        int pageSize = ListAttributesQuery.DefaultPageSize)
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

    public static AssemblyResult<TypeSummary> GetType(
        IMetadataIndex index,
        string? typeHandle = null,
        string? mvidOrPath = null,
        string? typeFullName = null)
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

    public static AssemblyResult<ListDerivedTypesPage> ListDerivedTypes(
        IMetadataIndex index,
        string? typeHandle = null,
        string? mvidOrPath = null,
        string? typeFullName = null,
        bool directOnly = true,
        int cursor = 0,
        int pageSize = ListDerivedTypesQuery.DefaultPageSize,
        string[]? matchInstantiation = null)
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

    public static AssemblyResult<ListMembersPage> ListMembers(
        IMetadataIndex index,
        string? typeHandle = null,
        string? mvidOrPath = null,
        string? typeFullName = null,
        MemberKind? kind = null,
        string? namePattern = null,
        string? signatureContains = null,
        int cursor = 0,
        int pageSize = ListMembersQuery.DefaultPageSize)
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
}
