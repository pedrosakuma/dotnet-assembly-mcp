using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using DotnetAssemblyMcp.Core.Metadata;
using FluentAssertions;
using Xunit;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Regression coverage for the v0.7.x bug where the MCP SDK schema generator marked
/// every positional record parameter as <c>required</c>, but System.Text.Json omitted
/// nullable fields (<c>NextCursor</c>) when their value was null. Clients that validate
/// structured tool output against the declared schema (e.g. claude code) then rejected
/// the response with <c>Structured content does not match the tool's output schema:
/// data/data must have required property 'nextCursor'</c>.
///
/// The fix is to give the optional pagination/truncation fields a default value so
/// the JSON schema generator treats them as optional. This test pins the contract by
/// asserting the generated schema for each paginated page record does not require
/// the optional pagination fields.
/// </summary>
public sealed class PaginatedPageSchemaTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    [Theory]
    [InlineData(typeof(ListTypesPage))]
    [InlineData(typeof(ListMethodsPage))]
    [InlineData(typeof(FindMethodPage))]
    public void Schema_does_not_require_optional_pagination_fields(Type pageType)
    {
        var schema = Options.GetJsonSchemaAsNode(pageType);
        var required = schema["required"] as JsonArray;

        var requiredNames = required?.Select(n => n?.GetValue<string>()).ToHashSet() ?? new HashSet<string?>();

        requiredNames.Should().NotContain("nextCursor",
            $"{pageType.Name} omits nextCursor from the JSON payload when null, so the schema must not require it.");
        requiredNames.Should().NotContain("truncated",
            $"{pageType.Name} omits truncated from the JSON payload when false, so the schema must not require it.");
    }
}
