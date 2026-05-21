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
}
