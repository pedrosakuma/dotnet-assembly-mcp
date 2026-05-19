using System.ComponentModel;
using System.Text.Json;
using DotnetAssemblyMcp.Core.Metadata;
using ModelContextProtocol.Server;

namespace DotnetAssemblyMcp.Server.Resources;

/// <summary>
/// Read-only MCP resources that expose the currently loaded-assembly state. Pairs with the
/// <c>import_assembly_manifest</c> tool: the tool mutates the cache, these resources read it
/// without paying the tool-invocation tax. See issue #7 / Phase Z(d).
/// </summary>
[McpServerResourceType]
public sealed class AssemblyManifestResources
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    [McpServerResource(
        UriTemplate = "assembly://manifest/loaded",
        Name = "loaded-assembly-manifest",
        Title = "Loaded-assembly manifest",
        MimeType = "application/json")]
    [Description(
        "JSON list of every assembly currently loaded in the metadata index — one entry per " +
        "MVID with name, path and method count. Read-only view over the same cache backing " +
        "the list_assemblies tool; safe to poll from any client without invoking a tool.")]
    public static string ListLoaded(IMetadataIndex index)
    {
        var modules = index.List();
        var payload = modules
            .Select(m => new
            {
                moduleVersionId = m.ModuleVersionId.ToString("D"),
                name = m.ModuleName,
                path = m.ModulePath,
                methodCount = m.MethodCount,
            })
            .ToArray();
        return JsonSerializer.Serialize(new { count = payload.Length, modules = payload }, JsonOptions);
    }

    [McpServerResource(
        UriTemplate = "assembly://manifest/loaded/{mvid}",
        Name = "loaded-assembly-detail",
        Title = "Loaded-assembly detail",
        MimeType = "application/json")]
    [Description(
        "JSON detail for a single loaded module addressed by its MVID. Returns an error " +
        "payload (kind='module_not_found') when the MVID is unknown or unloaded — clients " +
        "should call the load_assembly or import_assembly_manifest tool to register it.")]
    public static string ReadLoaded(IMetadataIndex index, string mvid)
    {
        if (!Guid.TryParse(mvid, out var parsed))
        {
            return JsonSerializer.Serialize(new
            {
                kind = "invalid_argument",
                error = $"could not parse '{mvid}' as a GUID.",
            }, JsonOptions);
        }
        var module = index.List().FirstOrDefault(m => m.ModuleVersionId == parsed);
        if (module is null)
        {
            return JsonSerializer.Serialize(new
            {
                kind = "module_not_found",
                moduleVersionId = parsed.ToString("D"),
                error = $"no loaded module has MVID {parsed:D}.",
            }, JsonOptions);
        }
        return JsonSerializer.Serialize(new
        {
            moduleVersionId = module.ModuleVersionId.ToString("D"),
            name = module.ModuleName,
            path = module.ModulePath,
            methodCount = module.MethodCount,
        }, JsonOptions);
    }
}
