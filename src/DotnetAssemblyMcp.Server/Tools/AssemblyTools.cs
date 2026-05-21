using DotnetAssemblyMcp.Core;
using ModelContextProtocol.Server;

namespace DotnetAssemblyMcp.Server.Tools;

/// <summary>
/// MCP tools that expose the dotnet-assembly-mcp Core navigation primitives. Every tool
/// returns an <see cref="AssemblyResult{T}"/> envelope carrying a short summary, next-action
/// hints, and the typed payload — mirrors the companion dotnet-diagnostics-mcp surface so
/// the agent experience is consistent across both servers.
/// </summary>
/// <remarks>
/// The tool surface is split across sibling partial files by area to keep each file under
/// ~600 lines and group related responsibilities together:
/// <list type="bullet">
///   <item><c>AssemblyTools.Lifecycle.cs</c> — load_assembly / list_assemblies / import_assembly_manifest.</item>
///   <item><c>AssemblyTools.Methods.cs</c> — get_method / decompile_method / decompile_type / get_method_il / list_methods / find_method / find_callers / get_method_source.</item>
///   <item><c>AssemblyTools.Types.cs</c> — list_types / list_assembly_references / list_resources / list_attributes / get_type / list_derived_types / list_members.</item>
///   <item><c>AssemblyTools.References.cs</c> — find_string_references / find_attribute_targets / find_member_references / find_type_references.</item>
///   <item><c>AssemblyTools.Parsing.cs</c> — private <c>TryParse*</c> / <c>TryResolve*</c> helpers shared by every tool.</item>
/// </list>
/// </remarks>
[McpServerToolType]
public sealed partial class AssemblyTools
{
}
