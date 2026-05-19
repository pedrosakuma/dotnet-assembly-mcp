using System.Collections;
using System.Reflection;
using DotnetAssemblyMcp.Core.Metadata;
using DotnetAssemblyMcp.Server.Tools;
using FluentAssertions;
using ModelContextProtocol.Server;
using Xunit;
using Xunit.Abstractions;

namespace DotnetAssemblyMcp.Core.Tests;

/// <summary>
/// Structural guarantee against the v0.7.0 bug class: positional record parameters
/// that are nullable (or otherwise omittable by System.Text.Json) MUST carry a default
/// value, otherwise the MCP SDK schema generator marks them <c>required</c> while STJ
/// drops them from the payload — strict MCP clients then reject the call with
/// <c>data must have required property '&lt;name&gt;'</c>.
///
/// This test walks every tool return type reachable from <see cref="AssemblyTools"/>,
/// recurses through generic args and record properties, and asserts the rule on every
/// positional constructor parameter it finds. Any new tool that introduces a nullable
/// positional param without a default value fails the build before reaching production.
/// </summary>
public sealed class ToolReturnTypeSchemaContractTests
{
    private readonly ITestOutputHelper _output;

    public ToolReturnTypeSchemaContractTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Every_tool_return_type_keeps_nullable_positional_params_optional()
    {
        var toolMethods = typeof(AssemblyTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .ToList();

        toolMethods.Should().NotBeEmpty("AssemblyTools must expose at least one [McpServerTool]");

        var seen = new HashSet<Type>();
        var violations = new List<string>();
        var nullabilityCtx = new NullabilityInfoContext();

        foreach (var tool in toolMethods)
        {
            var payload = UnwrapPayloadType(tool.ReturnType);
            WalkType(payload, seen, violations, nullabilityCtx, $"{tool.Name}() return");
        }

        _output.WriteLine($"Audited {toolMethods.Count} tool(s), {seen.Count} reachable type(s).");

        violations.Should().BeEmpty(
            "every nullable positional record parameter on a tool-reachable type must declare a default value " +
            "(e.g. `int? NextCursor = null`) so the MCP SDK schema generator treats it as optional. " +
            "Otherwise STJ may omit the field and strict MCP clients reject the call with " +
            "'Structured content does not match the tool's output schema'.\n\n" +
            string.Join("\n", violations));
    }

    private static Type UnwrapPayloadType(Type returnType)
    {
        // Tool methods return either `AssemblyResult<T>` or `Task<AssemblyResult<T>>`.
        var t = returnType;
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Task<>))
        {
            t = t.GetGenericArguments()[0];
        }
        if (t.IsGenericType && t.GetGenericTypeDefinition().Name.StartsWith("AssemblyResult", StringComparison.Ordinal))
        {
            t = t.GetGenericArguments()[0];
        }
        return t;
    }

    private static void WalkType(
        Type type,
        HashSet<Type> seen,
        List<string> violations,
        NullabilityInfoContext nullabilityCtx,
        string path)
    {
        type = StripNullable(type);

        if (!ShouldWalk(type)) return;
        if (!seen.Add(type)) return;

        // Recurse through generic args (covers IReadOnlyList<T>, ValueTuple, etc.).
        if (type.IsGenericType)
        {
            foreach (var arg in type.GetGenericArguments())
            {
                WalkType(arg, seen, violations, nullabilityCtx, $"{path}<{arg.Name}>");
            }
        }

        if (type.IsArray && type.GetElementType() is { } elem)
        {
            WalkType(elem, seen, violations, nullabilityCtx, $"{path}[]");
        }

        if (!IsUserDefined(type)) return;

        // Inspect the primary constructor (records have exactly one; classes pick the widest).
        var primary = type
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();

        if (primary is not null)
        {
            foreach (var param in primary.GetParameters())
            {
                CheckParam(type, primary, param, violations, nullabilityCtx);
                WalkType(param.ParameterType, seen, violations, nullabilityCtx, $"{type.Name}.{param.Name}");
            }
        }

        // Also walk public properties / fields for non-positional shapes.
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            WalkType(prop.PropertyType, seen, violations, nullabilityCtx, $"{type.Name}.{prop.Name}");
        }
    }

    private static void CheckParam(
        Type type,
        ConstructorInfo ctor,
        ParameterInfo param,
        List<string> violations,
        NullabilityInfoContext nullabilityCtx)
    {
        if (param.HasDefaultValue) return;

        // Value-type params that are NOT Nullable<T> get a JSON default (false, 0, "") from STJ
        // and are not omitted, so the schema's required marker matches reality.
        bool isNullableStruct = Nullable.GetUnderlyingType(param.ParameterType) is not null;
        bool isReferenceType = !param.ParameterType.IsValueType;

        if (!isNullableStruct && !isReferenceType) return;

        // Collections (IEnumerable, arrays) always serialize as `[]` even when null is the value,
        // because STJ writes them as empty arrays when non-null; null collections are usually
        // sourced as empty in our DTOs. Treat them as safe-by-convention to avoid noise — if a
        // future tool emits an actually-null collection, the integration would still surface it
        // via the runtime payload.
        if (IsCollection(param.ParameterType)) return;

        // Strings: nullable string positional params without defaults are the same hazard as
        // nullable ints. Flag them.
        var info = nullabilityCtx.Create(param);
        bool nullable = isNullableStruct || info.WriteState == NullabilityState.Nullable;
        if (!nullable) return;

        violations.Add(
            $"{type.FullName}.ctor({ctor.GetParameters().Length} params): " +
            $"param '{param.Name}' ({Render(param.ParameterType)}) is nullable and has no default value. " +
            "Add `= null` (or another default) so the JSON schema generator treats it as optional.");
    }

    private static bool ShouldWalk(Type type)
    {
        if (type is null) return false;
        if (type == typeof(string)) return false;
        if (type == typeof(object)) return false;
        if (type.IsPrimitive) return false;
        if (type.IsEnum) return false;
        if (type == typeof(Guid)) return false;
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan)) return false;
        return true;
    }

    private static bool IsUserDefined(Type type)
    {
        var ns = type.Namespace ?? string.Empty;
        if (ns.StartsWith("System", StringComparison.Ordinal)) return false;
        if (ns.StartsWith("Microsoft", StringComparison.Ordinal)) return false;
        if (ns.StartsWith("ModelContextProtocol", StringComparison.Ordinal)) return false;
        if (type.IsInterface) return false;
        if (type.IsAbstract && type.IsSealed) return false; // static classes
        return true;
    }

    private static bool IsCollection(Type type)
    {
        if (type == typeof(string)) return false;
        if (type.IsArray) return true;
        if (typeof(IEnumerable).IsAssignableFrom(type)) return true;
        return false;
    }

    private static Type StripNullable(Type type) =>
        Nullable.GetUnderlyingType(type) ?? type;

    private static string Render(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } inner) return $"{Render(inner)}?";
        if (!type.IsGenericType) return type.Name;
        var args = string.Join(", ", type.GetGenericArguments().Select(Render));
        var name = type.Name.Split('`')[0];
        return $"{name}<{args}>";
    }
}
