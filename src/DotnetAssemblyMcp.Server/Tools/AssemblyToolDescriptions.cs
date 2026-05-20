namespace DotnetAssemblyMcp.Server.Tools;

/// <summary>
/// Description strings for MCP tool attributes, extracted from AssemblyTools.cs per issue #93
/// to keep the tool file readable.
/// </summary>
internal static class AssemblyToolDescriptions
{
    // Common shared descriptions.
    internal const string Common_MvidOrPathModule = """MVID GUID or absolute path of the module; only used when typeHandle is omitted.""";
    internal const string Common_PaginationCursor = """Pagination cursor returned by the previous call. Pass 0 or omit for the first page.""";
    internal const string Common_TypeFullNameDescription = """Full type name (case-sensitive, '+'-joined for nested types); only used when typeHandle is omitted.""";
    internal const string Common_MetadataToken = """Method definition metadata token (table 0x06). Accepts decimal or hex (0x06000001).""";
    internal const string Common_ModuleVersionId = """ModuleVersionId GUID of the assembly the method belongs to, as a string ('D' format).""";
    internal const string Common_AssemblyPathHint = """Optional absolute path the producer observed for this assembly (see get_method for semantics).""";
    internal const string Common_MaxHitsDescription = """Optional cap on returned hits (default 1000, hard cap 10000). Pass 0 for default.""";
    internal const string Common_TypeHandle = """Type handle 't:<mvid>:0x<typeToken>' as returned by list_types. Pass null/empty if using mvidOrPath+typeFullName instead.""";
    internal const string Common_MvidOrPathAssembly = """Either the MVID GUID (D format) of a loaded module, or an absolute path to a .NET PE assembly (auto-loaded).""";
    internal const string Common_MaxTypesPerPage = """Max types per page (default 50, capped at 500).""";
    internal const string Common_ScopeMvidOrPath = """Optional scope. MVID GUID or absolute path of a single module. Omit / pass null to search every loaded module.""";

    // LoadAssembly.
    internal const string LoadAssembly_Summary = """Opens a managed PE file (.dll / .exe) via System.Reflection.Metadata and caches a metadata-only handle keyed by its ModuleVersionId. Idempotent: loading the same MVID twice returns the cached handle. Never executes the assembly. Usually the first call.""";
    internal const string LoadAssembly_Path = """Absolute path to a .NET PE assembly on the local filesystem.""";

    // ListAssemblies.
    internal const string ListAssemblies_Summary = """Returns the modules currently held by the metadata index (mvid, path, method count). Useful to confirm a target assembly is loaded before calling get_method, or to check whether two builds with the same name have different MVIDs.""";

    // ImportAssemblyManifest.
    internal const string ImportAssemblyManifest_Summary = """Bulk handshake intended for sidecar scenarios: a producer (typically dotnet-diagnostics-mcp) supplies a manifest of (moduleVersionId, path) pairs observed in a running process. In 'lazy' mode (default) each entry is recorded as a (mvid → path) hint without opening the PE — later get_method calls for that MVID use the hint automatically. In 'tier1' mode each entry is opened eagerly and added to the metadata index. Every entry's on-disk MVID is verified before use; an entry whose actual MVID differs from the manifest is rejected with reason 'mvid_mismatch_with_path' (the path is never silently re-mapped). Re-importing the same MVID is idempotent.""";
    internal const string ImportAssemblyManifest_Entries = """Manifest entries. Each must carry moduleVersionId (GUID 'D' format) and an absolute path; name is optional.""";
    internal const string ImportAssemblyManifest_Mode = """'lazy' (default) records (mvid → path) hints without opening the PEs; 'tier1' eagerly loads every entry into the metadata index.""";

    // GetMethod.
    internal const string GetMethod_Summary = """Implements the consumer side of the MethodIdentity handoff contract: given a moduleVersionId + metadataToken (typically copied from a dotnet-diagnostics-mcp response), returns the declaring type, method name, signature, IL size and method attributes. See docs/handoff-contract.md for the full semantics and error kinds.""";
    internal const string GetMethod_TypeFullName = """Optional declaring type full name; used only as a sanity-check display label.""";
    internal const string GetMethod_MethodName = """Optional method name; used only as a sanity-check display label.""";
    internal const string GetMethod_GenericArity = """Optional generic arity from the producer payload. Defaults to 0.""";
    internal const string GetMethod_AssemblyPathHint = """Optional absolute path the producer observed for this assembly. Used only when the MVID is not yet loaded: if the file at the path has a matching MVID it is loaded transparently; if it has a different MVID the call fails with mvid_mismatch (the path is a hint, never an override).""";
    internal const string GetMethod_GenericTypeArguments = """Optional CLR reflection-style full names for the declaring type's generic arguments (e.g. ['System.Int32']). When supplied alongside genericMethodArguments produces a closed signature view per docs/handoff-contract.md §3.5. No assembly qualification; nested types use '+'.""";
    internal const string GetMethod_GenericMethodArguments = """Optional CLR reflection-style full names for the method's generic arguments. See genericTypeArguments for the format.""";
    internal const string GetMethod_MethodSpecModuleVersionId = """Optional fast-path (§3.5): ModuleVersionId of a MethodSpec row that natively encodes the closed instantiation. Must be paired with methodSpecMetadataToken.""";
    internal const string GetMethod_MethodSpecMetadataToken = """Optional fast-path (§3.5): MethodSpec metadata token (table 0x2B) inside methodSpecModuleVersionId. When supplied alongside genericTypeArguments, the two are cross-checked; a mismatch yields generic_instantiation_mismatch.""";
    internal const string GetMethod_IncludeNativeBody = """When true, additionally probes the module for a precompiled native body (ReadyToRun) for this method and populates MethodSummary.NativeBody with (PE path, RVA, size) for handoff to dotnet-native-mcp.disassemble. No-op (NativeBody stays null) for JIT-only assemblies. See docs/handoff-contract.md §3.6.""";

    // DecompileMethod.
    internal const string DecompileMethod_Summary = """Returns the C# source of a single method via ICSharpCode.Decompiler. Output is hard-capped by maxChars (default 16 KiB) and LRU-cached keyed by (mvid, token, maxChars) so repeated calls on the same hotspot are cheap. Use get_method first to confirm the identity exists, then call this for the body. Note: generic methods are always returned in their open form (e.g. 'T Echo(T value)') — the decompiler operates on MethodDef, not on closed instantiations. For a closed signature view, use get_method with genericTypeArguments / genericMethodArguments (or the methodSpec fast-path); see docs/handoff-contract.md §3.5.""";
    internal const string DecompileMethod_MaxChars = """Optional cap on returned characters. Pass 0 to use the server default (16 KiB).""";

    // GetMethodIl.
    internal const string GetMethodIl_Summary = """Collapsed IL reader — replaces v0.13's get_method_il + get_method_il_text + scan_method_il. The 'format' argument selects the projection: 'raw' (default) returns hex-encoded IL bytes plus max-stack / EH-region / instruction counts (cheap; pair with maxBytes); 'text' returns an ildasm-style textual dump via ICSharpCode.Decompiler's ReflectionDisassembler with operand tokens resolved to readable names — useful when prefixes (tail./volatile./unaligned.), box/unbox.any placement, or call-vs-callvirt dispatch matters (pair with maxLines; cached); 'scan' walks the IL and returns structural outbound references (called methods, accessed fields, used types, string literals) — the building block for cross-reference queries without paying decompilation cost. The returned envelope carries the chosen format plus exactly one populated payload field (raw / text / scan); the other two are null. Generic methods are rendered in their open form for 'text'; IL token references in 'scan' are invariant across closed instantiations.""";
    internal const string GetMethodIl_Format = """Projection: 'raw' (default) for hex IL bytes, 'text' for an ildasm-style textual dump, or 'scan' for outbound-reference extraction.""";
    internal const string GetMethodIl_MaxBytes = """Used by format='raw' only. Optional cap on raw IL bytes encoded in the response. Pass 0 for the server default (4 KiB).""";
    internal const string GetMethodIl_MaxLines = """Used by format='text' only. Optional cap on output lines. Pass 0 for the server default (256). Hard cap 4096.""";

    // ListTypes.
    internal const string ListTypes_Summary = """Enumerates the type definitions of a module. Accepts either an MVID of an already loaded module or an absolute path (auto-loads on first call). Supports filtering by namespace prefix, name substring (case-insensitive) and kind (class/struct/interface/enum/delegate); results are paginated via cursor (pass nextCursor from the previous response). Each entry includes a type handle 't:<mvid>:0x<token>' suitable for the follow-up list_methods tool.""";
    internal const string ListTypes_NamespacePrefix = """Optional namespace prefix filter, matched as a dot-segmented prefix (e.g. 'MyApp' matches 'MyApp.Foo' but not 'MyAppExt.Foo').""";
    internal const string ListTypes_NameContains = """Optional case-insensitive substring matched against the full type name (including '+' for nested types).""";
    internal const string ListTypes_Kind = """Optional kind filter. One of: class, struct, interface, enum, delegate.""";

    // ListAssemblyReferences.
    internal const string ListAssemblyReferences_Summary = """Enumerates the AssemblyRef table of a single module: every external assembly the module depends on at metadata level, with name, four-part version, culture, public key token (hex), and raw AssemblyFlags. Cheap (single MetadataReader walk, not paginated). Use to reconstruct the dependency graph, audit target-framework or package versions, or pivot into load_assembly when the referenced assembly is also on disk. Accepts an MVID of an already-loaded module or an absolute path (auto-loaded on first call).""";

    // FindStringReferences.
    internal const string FindStringReferences_Summary = """Reverse string-literal lookup: returns every method whose IL contains an ldstr opcode whose decoded user-string matches 'query' under 'matchMode' (exact / contains / regex). Scope is all loaded modules unless 'mvidOrPath' is supplied; in that case only the named module is searched (auto-loaded from path if needed). Hits include the caller's method handle, signature display, IL offset of the ldstr opcode, and the matched literal. Per-module string index is built lazily on the first call against that module and held in memory; subsequent calls are O(1) for exact / O(unique-literals) for contains+regex. Result is capped at 'maxHits' (default 1000, hard cap 10000); 'truncated' = true when hit. Typical use: 'a user reported error message X — which method produces it?'.""";
    internal const string FindStringReferences_Query = """The string to search for. Required.""";
    internal const string FindStringReferences_MatchMode = """Match semantics: 'exact' (default), 'contains', or 'regex'. Regex evaluation has a 1s timeout per literal.""";

    // FindAttributeTargets.
    internal const string FindAttributeTargets_Summary = """Reverse attribute lookup: returns every assembly / type / method / parameter / field / property / event decorated with an attribute whose constructor's declaring type matches 'attributeTypeFullName' (case-sensitive full name, '+' for nested types — e.g. 'System.ObsoleteAttribute' or 'Xunit.FactAttribute'). Match is by attribute type identity, not by IL spelling, so using-aliases are irrelevant. Scope is every loaded module unless 'mvidOrPath' is supplied. Optional 'targetKinds' filters the result (comma-separated subset of assembly,type,method,parameter,field,property,event). Per-module reverse attribute index is built lazily and invalidated with the xref cache on file change. Result is capped at 'maxHits' (default 1000, hard cap 10000); 'truncated' = true when hit. Typical use: 'find every [Obsolete] API' / 'every [Authorize] controller method'.""";
    internal const string FindAttributeTargets_AttributeTypeFullName = """Full name of the attribute type, including '+' for nested types (e.g. 'System.ObsoleteAttribute'). Required.""";
    internal const string FindAttributeTargets_TargetKinds = """Optional comma-separated subset of {assembly, type, method, parameter, field, property, event}. Omit for all kinds.""";

    // FindMemberReferences.
    internal const string FindMemberReferences_Summary = """Reverse member-access lookup, collapsed from the v0.13 trio of find_field_references / find_property_references / find_event_references. The kind is dispatched from the handle prefix: 'f:<mvid>:0x<fieldToken>' (field — six opcodes ldfld/ldsfld/stfld/stsfld/ldflda/ldsflda), 'p:<mvid>:0x<propertyToken>' (property — every call to its getter/setter), 'e:<mvid>:0x<eventToken>' (event — every call to its add/remove/raise accessor). The 'accessor' filter applies to properties and events only: 'all' (default) / 'getter' / 'setter' for properties, 'all' (default) / 'add' / 'remove' / 'raise' for events, and 'all' (default) / 'read' / 'write' for fields (preserves the v0.13 find_field_references mode= filter). Same-module hits use metadata tokens; cross-module hits use the existing call/field-access xref indices. Result is capped at 'maxHits' (default 1000, hard cap 10000). The returned envelope carries a 'kind' discriminator plus exactly one populated payload field (field / property / event); the other two are null.""";
    internal const string FindMemberReferences_MemberHandle = """Member handle: 'f:<mvid>:0x<fieldToken>' for a field, 'p:<mvid>:0x<propertyToken>' for a property, or 'e:<mvid>:0x<eventToken>' for an event.""";
    internal const string FindMemberReferences_Accessor = """Optional accessor / mode filter. Field handles: 'all' (default) / 'read' (ldfld/ldsfld + ldflda/ldsflda) / 'write' (stfld/stsfld). Property handles: 'all' (default) / 'getter' / 'setter'. Event handles: 'all' (default) / 'add' / 'remove' / 'raise'.""";

    // ListMethods.
    internal const string ListMethods_Summary = """Enumerates the methods of a single type. Identify the type either via a typeHandle ('t:<mvid>:0x<typeToken>' returned by list_types) or via mvidOrPath + typeFullName (case-sensitive, uses '+' for nested types e.g. 'NS.Outer+Inner'). Returns one MethodSummary per method (handle, name, signature, ilSize, attributes); use cursor for paging. Drill in further with decompile_method, get_method_il or find_callers.""";
    internal const string ListMethods_NamePattern = """Optional case-insensitive substring filter on the method name.""";
    internal const string ListMethods_PageSize = """Max methods per page (default 50, capped at 500).""";

    // FindMethod.
    internal const string FindMethod_Summary = """Module-wide method search. Matches every MethodDef whose short name matches the supplied regular expression (case-insensitive) and, optionally, whose signature contains a substring. Returns hits with the canonical 'm:<mvid>:0x<token>' handle ready to feed into get_method / decompile_method / find_callers. Use this when you do not yet have a type in mind; otherwise prefer list_methods which is cheaper.""";
    internal const string FindMethod_MvidOrPath = """Either the MVID GUID ('D' format) of a previously loaded module, or an absolute path to a managed PE assembly (will be loaded on demand).""";
    internal const string FindMethod_NamePattern = """Regular expression matched (case-insensitive) against each method's short name.""";
    internal const string FindMethod_SignatureContains = """Optional case-insensitive substring filter on the decoded signature (e.g. 'CancellationToken').""";
    internal const string FindMethod_Cursor = """Optional pagination cursor returned in a prior call (exclusive lower bound on MethodDef token).""";
    internal const string FindMethod_PageSize = """Max matches per page (default 20, capped at 200).""";

    // FindCallers.
    internal const string FindCallers_Summary = """Returns every method whose IL emits a direct call to the requested callee — within the callee's own module via MethodDef tokens, and across any other loaded module via MemberRef signature matching (assembly name + type fullname + method name + parameter signature + generic arity). The reverse index is built lazily per module and cached at ~/.cache/dotnet-assembly-mcp/<mvid>.xref so subsequent queries are O(callers).""";
    internal const string FindCallers_ModuleVersionId = """ModuleVersionId GUID of the callee, as a string ('D' format).""";
    internal const string FindCallers_MetadataToken = """Callee MethodDef metadata token (table 0x06). Accepts decimal or hex.""";
    internal const string FindCallers_GenericTypeArguments = """Optional CLR reflection-style full names for the declaring type's generic arguments (see get_method).""";
    internal const string FindCallers_GenericMethodArguments = """Optional CLR reflection-style full names for the method's generic arguments. When supplied, the caller list is narrowed to call sites whose MethodSpec.Instantiation matches element-wise (docs/handoff-contract.md §3.5).""";
    internal const string FindCallers_MethodSpecModuleVersionId = """Optional fast-path (§3.5): ModuleVersionId of a MethodSpec row. Paired with methodSpecMetadataToken.""";
    internal const string FindCallers_MethodSpecMetadataToken = """Optional fast-path (§3.5): MethodSpec metadata token (table 0x2B). When supplied, derives the instantiation directly from metadata.""";

    // FindTypeReferences.
    internal const string FindTypeReferences_Summary = """Returns every site that references the requested TypeDef: field/property/event types, method parameters / return types / locals, IL opcodes that bake in a type token (newobj, castclass, isinst, box, unbox, ldtoken, generic args, ...), and type-hierarchy edges (BaseType + InterfaceImplementation per TypeDef, including TypeSpec closures of the target — e.g. 'class C : IRequestHandler<int,string>' registers as an InterfaceImplementation site of IRequestHandler`2). Same-module hits come from TypeDef tokens; cross-module hits come from TypeRef matching (assembly simple name + type full name). Uses the same lazily-built per-module xref cache as find_callers; the cache file format version was bumped so the first call after upgrade rebuilds.""";
    internal const string FindTypeReferences_TypeHandle = """Type handle 't:<mvid>:0x<typeToken>' as returned by list_types or get_type.""";
    internal const string FindTypeReferences_AssemblyPathHint = """Optional absolute path the producer observed for this assembly (used to load the module if it's not yet known).""";

    // GetMethodSource.
    internal const string GetMethodSource_Summary = """Reads the module's PDB (embedded portable PDB first, then sibling .pdb) and returns the file/startLine/endLine triple plus a resolved SourceLink URL when SourceLink CustomDebugInformation is present. Second-chance source resolver: use after dotnet-diagnostics-mcp has emitted a hotspot with no SourceLocation. Metadata-only (no HTTP). Returns found=false (not an error) when no PDB exists or the method has no non-hidden sequence points (compiler-generated bodies). Note: this tool does not accept §3.5 generic-instantiation arguments — PDB sequence points anchor on the open MethodDef and the source coordinates are the same for every closed instantiation.""";

    // ListAttributes.
    internal const string ListAttributes_Summary = """Enumerates the CustomAttribute rows attached to the entity identified by 'target'. Accepts a polymorphic handle: 'a:<mvid>' (assembly), 't:<mvid>:0x<token>' (type), 'm:<mvid>:0x<token>' (method), 'pa:<mvid>:0x<methodToken>:<sequence>' (parameter; sequence 0 = return value), 'f:<mvid>:0x<token>' (field), 'p:<mvid>:0x<token>' (property), or 'e:<mvid>:0x<token>' (event — same handles list_members returns). Pure metadata — no IL decoded, no decompilation. Each entry includes the attribute's full type name, its declaring assembly's simple name (when cross-module), the decoded constructor arguments, and the named arguments (properties / fields set in the attribute usage).""";
    internal const string ListAttributes_Target = """Target handle. One of: 'a:<mvid>', 't:<mvid>:0x<typeToken>', 'm:<mvid>:0x<methodToken>', 'pa:<mvid>:0x<methodToken>:<sequence>' (sequence 0 = return value), 'f:<mvid>:0x<fieldToken>', 'p:<mvid>:0x<propertyToken>', 'e:<mvid>:0x<eventToken>'.""";
    internal const string ListAttributes_NameContains = """Optional case-insensitive substring filter on the attribute type's full name (e.g. 'Authorize').""";
    internal const string ListAttributes_PageSize = """Max attributes per page (default 50, capped at 500).""";

    // GetType.
    internal const string GetType_Summary = """Returns the full TypeSummary (kind, attributes, generic arity, base type, implemented interfaces) for a single type. Identify the type via 'typeHandle' ('t:<mvid>:0x<typeToken>' from list_types) or via mvidOrPath + typeFullName. Cross-module base types and interfaces are reported as TypeReferenceSummary (FullName + declaring assembly simple name) without forcing the other module to load.""";

    // ListDerivedTypes.
    internal const string ListDerivedTypes_Summary = """Enumerates TypeDef rows across every loaded module whose base-class chain or InterfaceImplementation chain reaches the supplied base type. Use it to answer 'who derives from / implements this type?' refactor questions — same-module hits use TypeDef tokens, cross-module hits match by (assembly simple name, type full name) against the child module's TypeRef rows. With directOnly=true (default) only immediate subclasses / implementers are returned; with directOnly=false the full transitive set is returned. Generic-instantiation parents are also matched: a query against the open base (e.g. `IRequestHandler`2`) finds every closed-arg implementer, and the matched closed args are surfaced on `TypeSummary.Instantiation`. Pass `matchInstantiation` to narrow the result to a specific closed shape (e.g. `['System.Int32','System.String']` returns only `OrderHandler : IRequestHandler<int,string>`). Identify the base type via 'typeHandle' or via mvidOrPath + typeFullName, exactly like get_type / list_methods.""";
    internal const string ListDerivedTypes_TypeHandle = """Type handle 't:<mvid>:0x<typeToken>' of the base type, as returned by list_types or get_type.""";
    internal const string ListDerivedTypes_TypeFullName = """Full base-type name (case-sensitive, '+'-joined for nested types); only used when typeHandle is omitted.""";
    internal const string ListDerivedTypes_DirectOnly = """When true (default) only immediate subclasses are returned; when false, the full transitive descendant set is returned.""";
    internal const string ListDerivedTypes_MatchInstantiation = """Optional CLR reflection-style full names for the base type's generic arguments (e.g. ['System.Int32','System.String']) per docs/handoff-contract.md §3.5. When supplied, only TypeSpec parent edges whose closed args match element-wise are returned; non-generic parents are excluded. Omit for open match (default).""";

    // ListMembers.
    internal const string ListMembers_Summary = """Enumerates the structural members of a single type — fields, properties, and events — with paging and optional kind / name / signature filters. Methods are intentionally excluded; use list_methods for those (it carries IL-size + generic arity which don't apply to fields/properties/events). Each MemberSummary carries a prefix-tagged handle ('f:', 'p:', 'e:') accepted by list_attributes as a target.""";
    internal const string ListMembers_Kind = """Optional kind filter: Field, Property, or Event. Omit to return all kinds in metadata order (fields, then properties, then events).""";
    internal const string ListMembers_NamePattern = """Optional case-insensitive substring filter on the member name.""";
    internal const string ListMembers_SignatureContains = """Optional case-insensitive substring filter on the rendered signature (e.g. 'int', 'EventHandler').""";
    internal const string ListMembers_PageSize = """Max members per page (default 50, capped at 500).""";
}
