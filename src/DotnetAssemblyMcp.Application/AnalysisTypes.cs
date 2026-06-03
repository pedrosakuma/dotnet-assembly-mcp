using DotnetAssemblyMcp.Core.Metadata;

namespace DotnetAssemblyMcp.Application;

/// <summary>
/// One-shot overview of a type, produced by <see cref="AssemblyAnalysisOperations.ExplainType"/>.
/// Aggregates the type summary with its attributes, members and methods so a human can see the
/// whole shape of a type from a single command.
/// </summary>
public sealed record TypeExplanation(
    TypeSummary Type,
    IReadOnlyList<AttributeSummary> Attributes,
    IReadOnlyList<MemberSummary> Members,
    IReadOnlyList<MethodSummary> Methods,
    bool MembersTruncated = false,
    bool MethodsTruncated = false,
    bool AttributesTruncated = false,
    IReadOnlyList<string>? Warnings = null);

/// <summary>
/// Resolved overloads of a method, produced by <see cref="AssemblyAnalysisOperations.ExplainMethod"/>.
/// </summary>
public sealed record MethodExplanation(
    string TypeFullName,
    string MethodName,
    IReadOnlyList<MethodOverloadDetail> Overloads,
    bool Exact = true,
    IReadOnlyList<string>? Warnings = null);

/// <summary>
/// A single method overload with its source location and, optionally, decompiled C#.
/// </summary>
public sealed record MethodOverloadDetail(
    MethodSummary Method,
    MethodSourceLocation? Source = null,
    string? DecompiledCSharp = null);
