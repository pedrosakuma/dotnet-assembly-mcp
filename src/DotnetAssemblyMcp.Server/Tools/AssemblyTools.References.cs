using System.ComponentModel;
using DotnetAssemblyMcp.Core;
using DotnetAssemblyMcp.Core.Decompilation;
using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Handles;
using DotnetAssemblyMcp.Core.Identity;
using DotnetAssemblyMcp.Core.Metadata;
using DotnetAssemblyMcp.Application;
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
        => AssemblyOperations.FindStringReferences(index, query, matchMode, mvidOrPath, maxHits, cancellationToken);

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
        [Description(AssemblyToolDescriptions.FindAttributeTargets_TargetKinds)] string[]? targetKinds = null,
        [Description(AssemblyToolDescriptions.Common_MaxHitsDescription)] int maxHits = 0,
        CancellationToken cancellationToken = default)
        => AssemblyOperations.FindAttributeTargets(index, attributeTypeFullName, mvidOrPath, targetKinds, maxHits, cancellationToken);


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
        => AssemblyOperations.FindMemberReferences(index, memberHandle, accessor, maxHits, cancellationToken);


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
        => AssemblyOperations.FindTypeReferences(index, typeHandle, mvidOrPath, typeFullName, assemblyPathHint, cancellationToken);
}
