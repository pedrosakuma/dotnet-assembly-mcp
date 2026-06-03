using System.Globalization;
using DotnetAssemblyMcp.Application;
using DotnetAssemblyMcp.Core.Metadata;

namespace DotnetAssemblyMcp.Cli;

/// <summary>
/// Purpose-built text rendering for the composed <c>explain-*</c> commands. Kept separate from the
/// generic reflection-driven <see cref="CliRenderer"/>: a human overview wants grouped members,
/// compact attributes and clearly separated decompiled bodies, not a recursive property dump with
/// raw GUIDs and tokens.
/// </summary>
internal static class ExplainRenderer
{
    public static void WriteType(TextWriter w, TypeExplanation e)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(e);
        TypeSummary t = e.Type;

        w.WriteLine();
        w.WriteLine($"Type: {t.FullName}");
        w.WriteLine($"  Kind: {t.Kind}   Visibility: {(t.IsPublic ? "public" : "non-public")}");
        w.WriteLine($"  Handle: {t.Handle}");
        if (t.GenericParameters is { Count: > 0 } gp)
        {
            w.WriteLine($"  Generics: <{string.Join(", ", gp.Select(p => p.Name))}>");
        }

        if (t.BaseType is { } baseType)
        {
            w.WriteLine($"  Base: {baseType.FullName}");
        }

        if (t.Interfaces is { Count: > 0 } interfaces)
        {
            w.WriteLine($"  Interfaces: {string.Join(", ", interfaces.Select(i => i.FullName))}");
        }

        if (e.Attributes.Count > 0)
        {
            w.WriteLine();
            w.WriteLine($"Attributes ({Count(e.Attributes.Count)}{Trunc(e.AttributesTruncated)}):");
            foreach (AttributeSummary a in e.Attributes)
            {
                w.WriteLine($"  - {a.AttributeTypeFullName}");
            }
        }

        WriteMemberGroup(w, "Fields", e.Members, MemberKind.Field, e.MembersTruncated);
        WriteMemberGroup(w, "Properties", e.Members, MemberKind.Property, e.MembersTruncated);
        WriteMemberGroup(w, "Events", e.Members, MemberKind.Event, e.MembersTruncated);

        w.WriteLine();
        w.WriteLine($"Methods ({Count(e.Methods.Count)}{Trunc(e.MethodsTruncated)}):");
        foreach (MethodSummary m in e.Methods)
        {
            w.WriteLine($"  - {m.Signature}");
        }

        WriteWarnings(w, e.Warnings);
    }

    public static void WriteMethod(TextWriter w, MethodExplanation e)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(e);

        w.WriteLine();
        string mode = e.Exact ? string.Empty : " [substring match]";
        w.WriteLine($"Method: {e.TypeFullName}.{e.MethodName}   ({Count(e.Overloads.Count)} overload(s)){mode}");

        foreach (MethodOverloadDetail d in e.Overloads)
        {
            MethodSummary m = d.Method;
            w.WriteLine();
            w.WriteLine($"  {m.Signature}");
            w.Write($"    Handle: {m.Handle}   IL: {m.IlSize.ToString(CultureInfo.InvariantCulture)} bytes");
            if (m.GenericArity > 0)
            {
                w.Write($"   Generic arity: {m.GenericArity.ToString(CultureInfo.InvariantCulture)}");
            }

            w.WriteLine();

            if (m.Attributes.Count > 0)
            {
                w.WriteLine($"    Attributes: {string.Join(", ", m.Attributes)}");
            }

            w.WriteLine($"    Source: {FormatSource(d.Source)}");

            if (d.DecompiledCSharp is { } src)
            {
                w.WriteLine("    --- C# ---");
                foreach (var line in src.Split('\n'))
                {
                    w.WriteLine($"    {line.TrimEnd('\r')}");
                }
            }
        }

        WriteWarnings(w, e.Warnings);
    }

    public static void WriteCallGraph(TextWriter w, CallGraph g)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(g);

        w.WriteLine();
        w.WriteLine($"Call graph: {g.TargetDisplay}   (depth {g.Depth.ToString(CultureInfo.InvariantCulture)}, {Count(g.NodeCount)} node(s){(g.Truncated ? ", truncated" : string.Empty)})");

        if (g.Roots.Count == 0)
        {
            w.WriteLine("  (no matching roots)");
        }

        for (var i = 0; i < g.Roots.Count; i++)
        {
            w.WriteLine();
            WriteCallGraphNode(w, g.Roots[i], prefix: string.Empty, isRoot: true, isLast: i == g.Roots.Count - 1);
        }

        WriteWarnings(w, g.Warnings);
    }

    private static void WriteCallGraphNode(TextWriter w, CallGraphNode node, string prefix, bool isRoot, bool isLast)
    {
        string marker = node.Cycle ? "  [cycle]" : node.DepthLimited ? "  [more callers not shown]" : string.Empty;

        if (isRoot)
        {
            w.WriteLine($"{node.Display}{marker}");
        }
        else
        {
            string connector = isLast ? "└─ " : "├─ ";
            w.WriteLine($"{prefix}{connector}{node.Display}{marker}");
        }

        if (node.Callers.Count == 0)
        {
            return;
        }

        string childPrefix = isRoot ? string.Empty : prefix + (isLast ? "   " : "│  ");
        for (var i = 0; i < node.Callers.Count; i++)
        {
            WriteCallGraphNode(w, node.Callers[i], childPrefix, isRoot: false, isLast: i == node.Callers.Count - 1);
        }
    }

    public static void WriteAssemblyDiff(TextWriter w, AssemblyDiff d)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(d);

        w.WriteLine();
        w.WriteLine($"Left:  {d.LeftDisplay}   ({Count(d.LeftTypeCount)} visible type(s))");
        w.WriteLine($"Right: {d.RightDisplay}   ({Count(d.RightTypeCount)} visible type(s))");
        w.WriteLine("  Note: property / event accessors appear as get_/set_/add_/remove_ methods.");

        if (d.AddedTypes.Count == 0 && d.RemovedTypes.Count == 0 && d.ChangedTypes.Count == 0)
        {
            w.WriteLine();
            w.WriteLine("No public-surface differences.");
        }

        if (d.RemovedTypes.Count > 0)
        {
            w.WriteLine();
            w.WriteLine($"Removed types ({Count(d.RemovedTypes.Count)}):");
            foreach (TypeDiff t in d.RemovedTypes)
            {
                w.WriteLine($"  - {t.TypeFullName}   ({t.Kind}, {Count(t.MemberCount)} member(s))");
            }
        }

        if (d.AddedTypes.Count > 0)
        {
            w.WriteLine();
            w.WriteLine($"Added types ({Count(d.AddedTypes.Count)}):");
            foreach (TypeDiff t in d.AddedTypes)
            {
                w.WriteLine($"  + {t.TypeFullName}   ({t.Kind}, {Count(t.MemberCount)} member(s))");
            }
        }

        if (d.ChangedTypes.Count > 0)
        {
            w.WriteLine();
            w.WriteLine($"Changed types ({Count(d.ChangedTypes.Count)}):");
            foreach (TypeDiff t in d.ChangedTypes)
            {
                w.WriteLine($"  ~ {t.TypeFullName}   ({t.Kind})");
                if (t.ShapeChanges is { Count: > 0 } shape)
                {
                    foreach (string s in shape)
                    {
                        w.WriteLine($"      {s}");
                    }
                }

                if (t.RemovedMembers is { Count: > 0 } rm)
                {
                    foreach (MemberChange m in rm)
                    {
                        w.WriteLine($"      - {m.Signature}");
                    }
                }

                if (t.AddedMembers is { Count: > 0 } am)
                {
                    foreach (MemberChange m in am)
                    {
                        w.WriteLine($"      + {m.Signature}");
                    }
                }

                if (t.ChangedMembers is { Count: > 0 } cm)
                {
                    foreach (MemberSignatureChange m in cm)
                    {
                        w.WriteLine($"      ~ {m.Before}");
                        w.WriteLine($"        -> {m.After}");
                    }
                }
            }
        }

        if (d.Incomplete)
        {
            w.WriteLine();
            w.WriteLine("Note: diff is incomplete — one or more visible types could not be read (see warnings).");
        }

        WriteWarnings(w, d.Warnings);
    }

    private static void WriteMemberGroup(TextWriter w, string label, IReadOnlyList<MemberSummary> members, MemberKind kind, bool truncated)
    {
        var group = members.Where(m => m.Kind == kind).ToList();
        if (group.Count == 0)
        {
            return;
        }

        w.WriteLine();
        w.WriteLine($"{label} ({Count(group.Count)}{Trunc(truncated)}):");
        foreach (MemberSummary m in group)
        {
            w.WriteLine($"  - {m.Signature}");
        }
    }

    private static string FormatSource(MethodSourceLocation? source)
    {
        if (source is null)
        {
            return "unavailable";
        }

        if (!source.Found)
        {
            return source.Reason is { Length: > 0 } reason ? $"not available ({reason})" : "not available";
        }

        string location = source.File ?? "(unknown file)";
        if (source.StartLine is { } start)
        {
            location += source.EndLine is { } end && end != start
                ? $":{start.ToString(CultureInfo.InvariantCulture)}-{end.ToString(CultureInfo.InvariantCulture)}"
                : $":{start.ToString(CultureInfo.InvariantCulture)}";
        }

        if (source.SourceLink is { Length: > 0 } link)
        {
            location += $"   {link}";
        }

        return location;
    }

    private static void WriteWarnings(TextWriter w, IReadOnlyList<string>? warnings)
    {
        if (warnings is not { Count: > 0 })
        {
            return;
        }

        w.WriteLine();
        w.WriteLine("Warnings:");
        foreach (var warning in warnings)
        {
            w.WriteLine($"  ! {warning}");
        }
    }

    private static string Count(int n) => n.ToString(CultureInfo.InvariantCulture);

    private static string Trunc(bool truncated) => truncated ? "+, truncated" : string.Empty;
}
