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

/// <summary>
/// A transitive caller tree produced by <see cref="AssemblyAnalysisOperations.BuildCallGraph"/>.
/// This is a <em>call-path</em> tree over MethodDef-level IL call sites across all loaded modules:
/// each root is a matched overload and its children are its (transitive) callers. The same method
/// may appear under several branches when it is reached by distinct call paths.
/// </summary>
public sealed record CallGraph(
    string TargetDisplay,
    int Depth,
    IReadOnlyList<CallGraphNode> Roots,
    int NodeCount,
    bool Truncated = false,
    IReadOnlyList<string>? Warnings = null);

/// <summary>
/// One method in a <see cref="CallGraph"/>. <see cref="Callers"/> are the methods that call this
/// one. <see cref="Cycle"/> marks a node whose identity repeats an ancestor on the current path
/// (recursion stopped). <see cref="DepthLimited"/> marks a node that has callers which were not
/// expanded because the depth limit was reached ("more callers not shown").
/// </summary>
public sealed record CallGraphNode(
    Guid ModuleVersionId,
    int MetadataToken,
    string Handle,
    string Display,
    IReadOnlyList<CallGraphNode> Callers,
    bool Cycle = false,
    bool DepthLimited = false);

/// <summary>
/// Public-surface diff between two assemblies, produced by
/// <see cref="AssemblyAnalysisOperations.DiffAssemblies"/>. Covers externally-visible types and
/// their public / protected members (methods — including property and event accessors — and
/// fields). <see cref="Incomplete"/> is set when at least one visible type's surface could not be
/// read and was excluded from comparison (see <see cref="Warnings"/>).
/// </summary>
public sealed record AssemblyDiff(
    string LeftDisplay,
    string RightDisplay,
    IReadOnlyList<TypeDiff> AddedTypes,
    IReadOnlyList<TypeDiff> RemovedTypes,
    IReadOnlyList<TypeDiff> ChangedTypes,
    int LeftTypeCount,
    int RightTypeCount,
    bool Incomplete = false,
    IReadOnlyList<string>? Warnings = null);

/// <summary>
/// A single type entry in an <see cref="AssemblyDiff"/>. For added / removed types only
/// <see cref="TypeFullName"/>, <see cref="Kind"/> and <see cref="MemberCount"/> are populated. For
/// changed types the member-level deltas and <see cref="ShapeChanges"/> (kind / base / interface
/// changes) are filled in.
/// </summary>
public sealed record TypeDiff(
    string TypeFullName,
    string Kind,
    int MemberCount = 0,
    IReadOnlyList<MemberChange>? AddedMembers = null,
    IReadOnlyList<MemberChange>? RemovedMembers = null,
    IReadOnlyList<MemberSignatureChange>? ChangedMembers = null,
    IReadOnlyList<string>? ShapeChanges = null);

/// <summary>A member added to or removed from a type's public surface.</summary>
public sealed record MemberChange(
    string Name,
    string Category,
    string Signature);

/// <summary>
/// A member kept (same identity: name + generic arity + parameter list) but whose surface
/// fingerprint changed — e.g. a return-type, visibility or modifier (static / virtual / abstract /
/// sealed / readonly / const) change.
/// </summary>
public sealed record MemberSignatureChange(
    string Name,
    string Category,
    string Before,
    string After);
