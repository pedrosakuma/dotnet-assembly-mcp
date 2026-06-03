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
        Name = "list_types",
        Title = "List types in a loaded assembly with paging and filtering",
        Destructive = false,
        ReadOnly = true,
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
        => AssemblyOperations.ListTypes(index, mvidOrPath, namespacePrefix, nameContains, kind, cursor, pageSize);

    [McpServerTool(
        Name = "list_assembly_references",
        Title = "List AssemblyRef rows (external dependencies) of a module",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(AssemblyToolDescriptions.ListAssemblyReferences_Summary)]
    public static AssemblyResult<ListAssemblyReferencesPage> ListAssemblyReferences(
        IMetadataIndex index,
        [Description(AssemblyToolDescriptions.Common_MvidOrPathAssembly)] string mvidOrPath)
        => AssemblyOperations.ListAssemblyReferences(index, mvidOrPath);

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
        => AssemblyOperations.ListResources(index, mvidOrPath);
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
        => AssemblyOperations.ListAttributes(index, target, nameContains, cursor, pageSize);

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
        => AssemblyOperations.GetType(index, typeHandle, mvidOrPath, typeFullName);

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
        => AssemblyOperations.ListDerivedTypes(index, typeHandle, mvidOrPath, typeFullName, directOnly, cursor, pageSize, matchInstantiation);

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
        => AssemblyOperations.ListMembers(index, typeHandle, mvidOrPath, typeFullName, kind, namePattern, signatureContains, cursor, pageSize);
}
