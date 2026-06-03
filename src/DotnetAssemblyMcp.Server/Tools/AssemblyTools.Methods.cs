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
        [Description(AssemblyToolDescriptions.Common_MetadataToken)] string? metadataToken = null,
        [Description(AssemblyToolDescriptions.GetMethod_TypeFullName)] string? typeFullName = null,
        [Description(AssemblyToolDescriptions.GetMethod_MethodName)] string? methodName = null,
        [Description(AssemblyToolDescriptions.GetMethod_GenericArity)] int genericArity = 0,
        [Description(AssemblyToolDescriptions.GetMethod_AssemblyPathHint)] string? assemblyPathHint = null,
        [Description(AssemblyToolDescriptions.GetMethod_GenericTypeArguments)] string[]? genericTypeArguments = null,
        [Description(AssemblyToolDescriptions.GetMethod_GenericMethodArguments)] string[]? genericMethodArguments = null,
        [Description(AssemblyToolDescriptions.GetMethod_MethodSpecModuleVersionId)] string? methodSpecModuleVersionId = null,
        [Description(AssemblyToolDescriptions.GetMethod_MethodSpecMetadataToken)] string? methodSpecMetadataToken = null,
        [Description(AssemblyToolDescriptions.GetMethod_IncludeNativeBody)] bool includeNativeBody = false)
        => AssemblyOperations.GetMethod(index, moduleVersionId, metadataToken, typeFullName, methodName, genericArity, assemblyPathHint, genericTypeArguments, genericMethodArguments, methodSpecModuleVersionId, methodSpecMetadataToken, includeNativeBody);

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
        [Description(AssemblyToolDescriptions.Common_MetadataToken)] string? metadataToken = null,
        [Description(AssemblyToolDescriptions.DecompileMethod_MaxChars)] int maxChars = 0,
        [Description(AssemblyToolDescriptions.Common_AssemblyPathHint)] string? assemblyPathHint = null,
        CancellationToken cancellationToken = default)
        => AssemblyOperations.DecompileMethod(decompiler, index, moduleVersionId, metadataToken, maxChars, assemblyPathHint, cancellationToken);

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
        [Description(AssemblyToolDescriptions.DecompileType_ModuleVersionId)] string moduleVersionId,
        [Description(AssemblyToolDescriptions.DecompileType_MetadataToken)] string? metadataToken = null,
        [Description(AssemblyToolDescriptions.DecompileType_MaxChars)] int maxChars = 0,
        [Description(AssemblyToolDescriptions.Common_AssemblyPathHint)] string? assemblyPathHint = null,
        CancellationToken cancellationToken = default)
        => AssemblyOperations.DecompileType(decompiler, index, moduleVersionId, metadataToken, maxChars, assemblyPathHint, cancellationToken);


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
        [Description(AssemblyToolDescriptions.Common_MetadataToken)] string? metadataToken = null,
        [Description(AssemblyToolDescriptions.GetMethodIl_Format)] string format = "raw",
        [Description(AssemblyToolDescriptions.GetMethodIl_MaxBytes)] int maxBytes = 0,
        [Description(AssemblyToolDescriptions.GetMethodIl_MaxLines)] int maxLines = 0,
        [Description(AssemblyToolDescriptions.Common_AssemblyPathHint)] string? assemblyPathHint = null,
        CancellationToken cancellationToken = default)
        => AssemblyOperations.GetMethodIl(disassembler, index, moduleVersionId, metadataToken, format, maxBytes, maxLines, assemblyPathHint, cancellationToken);
    [McpServerTool(
        Name = "list_methods",
        Title = "List methods of a type with paging and name filtering",
        Destructive = false,
        ReadOnly = true,
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
        => AssemblyOperations.ListMethods(index, typeHandle, mvidOrPath, typeFullName, namePattern, cursor, pageSize);

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
        => AssemblyOperations.FindMethod(index, mvidOrPath, namePattern, signatureContains, cursor, pageSize, cancellationToken);

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
        [Description(AssemblyToolDescriptions.FindCallers_MetadataToken)] string? metadataToken = null,
        [Description(AssemblyToolDescriptions.Common_AssemblyPathHint)] string? assemblyPathHint = null,
        [Description(AssemblyToolDescriptions.FindCallers_GenericTypeArguments)] string[]? genericTypeArguments = null,
        [Description(AssemblyToolDescriptions.FindCallers_GenericMethodArguments)] string[]? genericMethodArguments = null,
        [Description(AssemblyToolDescriptions.FindCallers_MethodSpecModuleVersionId)] string? methodSpecModuleVersionId = null,
        [Description(AssemblyToolDescriptions.FindCallers_MethodSpecMetadataToken)] string? methodSpecMetadataToken = null,
        CancellationToken cancellationToken = default)
        => AssemblyOperations.FindCallers(index, moduleVersionId, metadataToken, assemblyPathHint, genericTypeArguments, genericMethodArguments, methodSpecModuleVersionId, methodSpecMetadataToken, cancellationToken);
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
        [Description(AssemblyToolDescriptions.Common_MetadataToken)] string? metadataToken = null,
        [Description(AssemblyToolDescriptions.Common_AssemblyPathHint)] string? assemblyPathHint = null,
        CancellationToken cancellationToken = default)
        => AssemblyOperations.GetMethodSource(index, moduleVersionId, metadataToken, assemblyPathHint, cancellationToken);
}
