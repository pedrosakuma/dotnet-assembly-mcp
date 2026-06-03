using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetAssemblyMcp.Core;

namespace DotnetAssemblyMcp.Cli;

/// <summary>
/// Renders an <see cref="AssemblyResult{T}"/> envelope to the console. In <c>--json</c> mode the
/// full envelope (Summary + Data + Hints + Error) is serialized verbatim so the output stays
/// scriptable and identical to what the MCP server would return. In the default text mode only
/// the human-relevant parts are printed: the <see cref="AssemblyResult{T}.Summary"/>, a
/// reflection-driven pretty-print of <see cref="AssemblyResult{T}.Data"/>, and the error detail
/// on failure. <see cref="AssemblyResult{T}.Hints"/> are suppressed in text mode because they name
/// MCP tools and are meaningless to a human at a terminal.
/// </summary>
internal static class CliRenderer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Writes <paramref name="result"/> to stdout (or stderr on error) and returns the process
    /// exit code: <c>0</c> on success, <c>1</c> when <see cref="AssemblyResult{T}.IsError"/>.
    /// </summary>
    public static int Render<T>(AssemblyResult<T> result, bool json)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            return result.IsError ? 1 : 0;
        }

        if (result.IsError)
        {
            var error = result.Error!;
            Console.Error.WriteLine(result.Summary);
            Console.Error.WriteLine($"error: {error.Kind} - {error.Message}");
            if (!string.IsNullOrEmpty(error.Detail))
            {
                Console.Error.WriteLine($"detail: {error.Detail}");
            }

            return 1;
        }

        Console.WriteLine(result.Summary);
        if (result.Data is not null)
        {
            WriteValue(Console.Out, name: null, value: result.Data, indent: 0, depth: 0);
        }

        return 0;
    }

    private const int MaxDepth = 16;

    private static void WriteValue(TextWriter writer, string? name, object? value, int indent, int depth)
    {
        if (value is null || depth > MaxDepth)
        {
            return;
        }

        string pad = new(' ', indent * 2);
        string label = name is null ? string.Empty : $"{name}: ";

        switch (value)
        {
            case string s:
                if (s.Contains('\n', StringComparison.Ordinal))
                {
                    if (name is not null)
                    {
                        writer.WriteLine($"{pad}{name}:");
                    }

                    foreach (var line in s.Split('\n'))
                    {
                        writer.WriteLine($"{pad}  {line.TrimEnd('\r')}");
                    }
                }
                else
                {
                    writer.WriteLine($"{pad}{label}{s}");
                }

                break;

            case bool or Enum or Guid or int or long or short or byte or double or float or decimal:
                writer.WriteLine($"{pad}{label}{FormatScalar(value)}");
                break;

            case IEnumerable enumerable:
                var items = enumerable.Cast<object?>().ToList();
                writer.WriteLine($"{pad}{label}({items.Count.ToString(CultureInfo.InvariantCulture)})");
                foreach (var item in items)
                {
                    if (item is null)
                    {
                        continue;
                    }

                    if (IsScalar(item))
                    {
                        writer.WriteLine($"{pad}  - {FormatScalar(item)}");
                    }
                    else
                    {
                        writer.WriteLine($"{pad}  -");
                        WriteObject(writer, item, indent + 2, depth + 1);
                    }
                }

                break;

            default:
                if (name is not null)
                {
                    writer.WriteLine($"{pad}{name}:");
                    WriteObject(writer, value, indent + 1, depth + 1);
                }
                else
                {
                    WriteObject(writer, value, indent, depth + 1);
                }

                break;
        }
    }

    private static void WriteObject(TextWriter writer, object value, int indent, int depth)
    {
        if (depth > MaxDepth)
        {
            return;
        }

        var properties = value.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        foreach (var property in properties)
        {
            if (property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            object? propertyValue = property.GetValue(value);
            if (propertyValue is null)
            {
                continue;
            }

            WriteValue(writer, property.Name, propertyValue, indent, depth);
        }
    }

    private static bool IsScalar(object value) =>
        value is string or bool or Enum or Guid or int or long or short or byte or double or float or decimal;

    private static string FormatScalar(object value) =>
        value switch
        {
            string s => s,
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
}
