using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Identity;

namespace DotnetAssemblyMcp.Core.Metadata;

/// <summary>
/// Process-wide registry of loaded module handles. Handles are keyed by MVID; loading the
/// same physical file twice returns the same handle. All methods are thread-safe.
/// </summary>
/// <remarks>
/// Tier 1 from <c>docs/mcp-conventions.md §2.1</c>: metadata-only, resident, cheap to query.
/// Tier 2+ (IL bytes, decompile, xref) will compose on top of this — they do not own their
/// own cache of <c>PEReader</c> instances.
/// </remarks>
public interface IMetadataIndex
{
    /// <summary>Loads an assembly from disk (or returns the cached handle if its MVID is already known).</summary>
    /// <param name="path">Absolute path to a .NET PE assembly.</param>
    /// <returns>The module summary on success, or a load error.</returns>
    LoadResult Load(string path);

    /// <summary>Snapshot of currently loaded modules.</summary>
    IReadOnlyList<ModuleSummary> List();

    /// <summary>
    /// Reads the MVID of the PE at <paramref name="path"/> without registering it. Used by
    /// <c>import_assembly_manifest</c> to confirm an entry's claimed MVID before loading.
    /// </summary>
    ProbeResult Probe(string path);

    /// <summary>
    /// Registers a lazy <c>(mvid → path)</c> mapping without opening the PE. Consumed by
    /// <see cref="TryGetPathHint"/> so subsequent <c>get_method</c> calls that supply no
    /// explicit <c>assemblyPathHint</c> can still resolve the module on demand. Re-registering
    /// the same MVID with a different path overwrites the previous hint; resolution will
    /// still cross-check the on-disk MVID at load time.
    /// </summary>
    void RegisterPathHint(Guid moduleVersionId, string path);

    /// <summary>Returns the lazily-registered path for an MVID, if any.</summary>
    bool TryGetPathHint(Guid moduleVersionId, out string? path);

    /// <summary>
    /// Snapshot of every lazily-registered <c>(mvid → path)</c> mapping. Read-only; mutations
    /// must go through <see cref="RegisterPathHint"/>.
    /// </summary>
    IReadOnlyDictionary<Guid, string> PathHints { get; }

    /// <summary>
    /// Operator-configured trusted roots for the untrusted-path-hint contract (#150), already
    /// canonicalised, or <c>null</c> when allow-list enforcement is disabled. Exposed so the
    /// Tier-2+ reopen paths (decompile, IL, sibling-PDB) can build the same post-open
    /// descriptor verifier (#156) that <c>ModuleStore</c> applies at load time, closing the
    /// ancestor-directory TOCTOU window on every file descriptor rather than just the first.
    /// </summary>
    IReadOnlyList<string>? AllowedRoots { get; }

    /// <summary>
    /// Installs the per-directory <see cref="System.IO.FileSystemWatcher"/> for the path's
    /// directory without opening the PE. No-op when the index was constructed without
    /// <c>watchForChanges</c>. Used by <c>import_assembly_manifest</c> in <c>lazy</c> mode
    /// so registered paths participate in watcher events the moment they exist on disk.
    /// </summary>
    void WatchPath(string path);

    /// <summary>
    /// Pushes an out-of-band "this MVID is now stale" signal through the index. Three
    /// outcomes are possible, all of which raise <see cref="ModuleReloaded"/> exactly once
    /// per call:
    /// <list type="bullet">
    /// <item><description>
    /// <b>Same-MVID reload</b> (file on disk has the same MVID — typical hot-rebuild):
    /// the underlying <see cref="ModuleStore"/> handle is atomically swapped against the
    /// fresh PE. Event args: <c>OldMvid == NewMvid == moduleVersionId, Error == null</c>.
    /// </description></item>
    /// <item><description>
    /// <b>Different-MVID reload</b> (file rebuilt with a new MVID since the producer's
    /// observation): the old MVID entry is evicted from the store; the new MVID is
    /// registered. Event args: <c>OldMvid == moduleVersionId, NewMvid == new on-disk MVID,
    /// Error == null</c>.
    /// </description></item>
    /// <item><description>
    /// <b>Reload failure</b> (file vanished, I/O error, corrupt PE, or the MVID was never
    /// loaded and no path hint exists): the stale handle (if any) is evicted from the
    /// store so subsequent queries don't repopulate caches from the in-memory PE, and the
    /// per-module caches are cleared. Event args: <c>OldMvid == moduleVersionId,
    /// NewMvid == null, Error != null</c> in the I/O case; <c>OldMvid == NewMvid ==
    /// moduleVersionId, Error == null, Path == ""</c> in the unknown-MVID case.
    /// </description></item>
    /// </list>
    /// Downstream subscribers (Decompiler, IlDisassembler) listen on this event for their
    /// own cache invalidation, so the explicit signal travels through the same channel as
    /// file-watcher reloads. Idempotent. Intended for producers (e.g.
    /// <c>dotnet-diagnostics-mcp</c> over the handoff contract) that observe a reload
    /// independently of the file watcher and want to push staleness through this API.
    /// </summary>
    void Invalidate(Guid moduleVersionId);

    /// <summary>
    /// Resolves a method identity to a <see cref="MethodSummary"/>. Implements the resolution
    /// algorithm from <c>docs/handoff-contract.md §3</c>.
    /// </summary>
    ResolveResult Resolve(MethodIdentity identity);

    /// <summary>
    /// Returns the raw IL of the method as a hex string plus body metadata. The hex output
    /// is capped at <paramref name="maxBytes"/> bytes of IL; the actual <see cref="IlMethodBody.IlSize"/>
    /// reports the full size.
    /// </summary>
    /// <param name="identity">A resolved method identity.</param>
    /// <param name="maxBytes">Hard upper bound on the bytes encoded in the response. Pass 0 for the default (4 KiB).</param>
    /// <param name="cancellationToken">Cancels the call cooperatively. Long-running operations check this periodically.</param>
    IlBodyResult GetIlBody(MethodIdentity identity, int maxBytes = 0, CancellationToken cancellationToken = default);

    /// <summary>
    /// Walks the IL of the method and returns the structural references it contains:
    /// outbound calls, field accesses, type uses and string literals. No decompilation.
    /// </summary>
    IlScanReadResult ScanIl(MethodIdentity identity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tier-4 reverse-call lookup: returns every method in the callee's own module whose IL
    /// emits a call/callvirt/newobj/ldftn/ldvirtftn to <paramref name="callee"/>. The index
    /// is built lazily per module and cached both in memory and at
    /// <c>~/.cache/dotnet-assembly-mcp/&lt;mvid&gt;.xref</c> (rebuilt when the underlying file
    /// changes). Cross-module callers are out of scope for this iteration and tracked
    /// separately.
    /// </summary>
    FindCallersReadResult FindCallers(MethodIdentity callee, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tier-1 type enumeration: walks the module's <c>TypeDef</c> table, filters by namespace,
    /// name substring and coarse <see cref="TypeKind"/>, and returns a paginated slice.
    /// Synthetic types (the global <c>&lt;Module&gt;</c> placeholder) are skipped.
    /// </summary>
    /// <param name="moduleVersionId">MVID of a loaded module.</param>
    /// <param name="query">Filter and paging options. Defaults yield 50 types in metadata order.</param>
    ListTypesResult ListTypes(Guid moduleVersionId, ListTypesQuery query);

    /// <summary>
    /// Tier-1 method enumeration scoped to one type: walks the type's method list, optionally
    /// filtering by case-insensitive name substring, and returns a paginated slice.
    /// </summary>
    /// <param name="moduleVersionId">MVID of a loaded module.</param>
    /// <param name="typeMetadataToken">TypeDef metadata token (table 0x02) of the declaring type.</param>
    /// <param name="query">Filter and paging options.</param>
    ListMethodsResult ListMethods(Guid moduleVersionId, int typeMetadataToken, ListMethodsQuery query);

    /// <summary>
    /// Module-wide method search. Matches every <c>MethodDef</c> whose short name matches the
    /// supplied regex (and optionally whose signature contains a substring). Iterates the
    /// <c>MethodDef</c> table directly so it is O(n) per call; pagination uses the metadata
    /// token as an exclusive cursor.
    /// </summary>
    /// <param name="moduleVersionId">MVID of a loaded module.</param>
    /// <param name="query">Required name pattern plus optional signature substring, cursor and page size.</param>
    /// <param name="cancellationToken">Cooperative cancellation; checked once per <c>MethodDef</c> row.</param>
    FindMethodResult FindMethod(Guid moduleVersionId, FindMethodQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a method to its source-location triple using the module's PDB (embedded
    /// portable PDB first, then a sibling <c>.pdb</c> file). Returns a
    /// <see cref="MethodSourceLocation"/> whose <c>Found</c> flag indicates whether a non-
    /// hidden sequence point was found; the <c>SourceLink</c> URL is constructed from the
    /// PDB's SourceLink CustomDebugInformation document when present. No HTTP. See
    /// <c>docs/handoff-contract.md §3.4</c>.
    /// </summary>
    MethodSourceResult GetMethodSource(MethodIdentity identity);

    /// <summary>
    /// Tier-1 custom-attribute enumeration: walks the <c>CustomAttribute</c> rows attached to
    /// the entity identified by <paramref name="target"/> (assembly, type, method, or
    /// parameter) and returns decoded <see cref="AttributeSummary"/> entries. No IL is
    /// touched; this is pure metadata.
    /// </summary>
    ListAttributesResult ListAttributes(AttributeTarget target, ListAttributesQuery query);

    /// <summary>
    /// Returns a single <see cref="TypeSummary"/> for the requested TypeDef, fully populated
    /// with base type and implemented interfaces (the same shape <see cref="ListTypes"/>
    /// emits). Cheaper than paginating <see cref="ListTypes"/> when you already know the
    /// target token.
    /// </summary>
    GetTypeResult GetTypeDefinition(Guid moduleVersionId, int typeMetadataToken);

    /// <summary>
    /// Tier-1 type-hierarchy walk: returns every <c>TypeDef</c> across every loaded module
    /// whose <c>BaseType</c> chain or <c>InterfaceImplementation</c> chain reaches
    /// <paramref name="baseTypeMetadataToken"/>. Same-module hits are matched by TypeDef
    /// token; cross-module hits are matched by (assembly simple name, type full name) via
    /// the child module's <c>TypeRef</c> rows. When <see cref="ListDerivedTypesQuery.DirectOnly"/>
    /// is <c>false</c> the walk is transitive (every descendant / implementer, not just
    /// immediate children). TypeSpec parents (generic instantiations such as
    /// <c>class Dog : AnimalBase&lt;int&gt;</c>) are not yet matched.
    /// </summary>
    ListDerivedTypesResult ListDerivedTypes(Guid moduleVersionId, int baseTypeMetadataToken, ListDerivedTypesQuery query);

    /// <summary>
    /// Enumerates the structural members (fields, properties, events) of a single type. Optional
    /// <see cref="ListMembersQuery.Kind"/> narrows the kind; <see cref="ListMembersQuery.NamePattern"/>
    /// and <see cref="ListMembersQuery.SignatureContains"/> apply case-insensitive substring filters
    /// (no regex, mirroring the existing <c>list_methods</c> ergonomics). Methods are intentionally
    /// excluded — they have their own <see cref="ListMethods"/> surface.
    /// </summary>
    ListMembersResult ListMembers(Guid moduleVersionId, int typeMetadataToken, ListMembersQuery query);

    /// <summary>
    /// Reverse-resolves "who uses this type?" — returns every site in any loaded module that
    /// references the target TypeDef through a field type, property type, event type, method
    /// parameter / return type, method body local, or type-bearing IL opcode (newobj,
    /// castclass, isinst, box, unbox, ldtoken, newarr, etc.). Cross-module sites match the
    /// target by (assembly simple name, type full name) so consumers don't need to pre-resolve
    /// TypeRef identities. Reuses the per-module xref cache that <see cref="FindCallers"/>
    /// already builds.
    /// </summary>
    FindTypeReferencesReadResult FindTypeReferences(Guid moduleVersionId, int typeMetadataToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enumerates the AssemblyRef rows of a single loaded module: every external assembly the
    /// module references at metadata level (name, version, culture, public key token, flags).
    /// One-shot; not paginated — AssemblyRef tables are typically &lt; 100 entries.
    /// </summary>
    ListAssemblyReferencesResult ListAssemblyReferences(Guid moduleVersionId);

    /// <summary>
    /// Enumerates the ManifestResource rows of a single loaded module: every embedded resource
    /// (in-PE, linked external file, or forwarded to a satellite assembly) with name, visibility,
    /// implementation kind, and — for in-PE resources — payload offset and decoded length.
    /// One-shot; not paginated — ManifestResource tables are typically &lt; 100 entries.
    /// Reading the resource bytes themselves is intentionally out of scope; this tool stays
    /// metadata-only.
    /// </summary>
    ListResourcesResult ListResources(Guid moduleVersionId);

    /// <summary>
    /// Reverse string-literal lookup: returns every method that emits an <c>ldstr</c> opcode
    /// whose decoded user-string matches <paramref name="query"/>. The match semantics depend
    /// on <paramref name="matchMode"/> (exact / contains / regex). The per-module string index
    /// is built lazily on first call and cached in memory; rebuilds when the module is reloaded.
    /// When <paramref name="moduleVersionIdFilter"/> is non-empty the search is scoped to that
    /// one module; otherwise every loaded module is searched. <paramref name="maxHits"/> is a
    /// server-side cap (defaults to 1000); hitting it returns <see cref="FindStringReferencesResult.Truncated"/> = true.
    /// </summary>
    FindStringReferencesReadResult FindStringReferences(
        string query,
        StringMatchMode matchMode,
        Guid moduleVersionIdFilter = default,
        int maxHits = 0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reverse attribute lookup: returns every target (assembly / type / method / parameter /
    /// field / property / event) decorated with an attribute whose constructor's declaring
    /// type full name matches <paramref name="attributeTypeFullName"/>. Match is by the
    /// attribute type's identity (case-sensitive full name including '+' for nested types),
    /// not by IL spelling, so using-aliases are irrelevant. <paramref name="targetKindsFilter"/>
    /// (when non-null) restricts the result to the listed kinds. <paramref name="moduleVersionIdFilter"/>
    /// scopes to a single loaded module when set; otherwise every loaded module is searched.
    /// The per-module index is built lazily on first call and invalidated together with the
    /// xref cache when the underlying file changes.
    /// </summary>
    FindAttributeTargetsReadResult FindAttributeTargets(
        string attributeTypeFullName,
        Guid moduleVersionIdFilter = default,
        IReadOnlyCollection<AttributeTargetKind>? targetKindsFilter = null,
        int maxHits = 0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reverse field-access lookup: returns every method whose IL touches the field identified
    /// by (<paramref name="moduleVersionId"/>, <paramref name="fieldMetadataToken"/>) via one
    /// of the six field opcodes (<c>ldfld</c> / <c>stfld</c> / <c>ldflda</c> / <c>ldsfld</c> /
    /// <c>stsfld</c> / <c>ldsflda</c>). Same-module hits resolve through FieldDef tokens;
    /// cross-module hits match via assembly + declaring-type-full-name + field-name. The
    /// per-module field-access index is built lazily on first call and invalidated together
    /// with the xref cache when the underlying file changes.
    /// </summary>
    FindFieldReferencesReadResult FindFieldReferences(
        Guid moduleVersionId,
        int fieldMetadataToken,
        FieldAccessMode mode = FieldAccessMode.All,
        int maxHits = 0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reverse property-access lookup: resolves the property identified by
    /// (<paramref name="moduleVersionId"/>, <paramref name="propertyMetadataToken"/>) to its
    /// getter / setter MethodDefs and reuses the existing call-xref index to list every
    /// invocation, tagged by which accessor was hit. <paramref name="accessor"/> filters the
    /// result to a single accessor when desired.
    /// </summary>
    FindPropertyReferencesReadResult FindPropertyReferences(
        Guid moduleVersionId,
        int propertyMetadataToken,
        PropertyAccessorFilter accessor = PropertyAccessorFilter.All,
        int maxHits = 0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reverse event-accessor lookup: resolves the event identified by
    /// (<paramref name="moduleVersionId"/>, <paramref name="eventMetadataToken"/>) to its
    /// adder / remover / raiser MethodDefs and reuses the existing call-xref index to list
    /// every invocation, tagged by which accessor was hit. <paramref name="accessor"/> filters
    /// the result to a single accessor when desired.
    /// </summary>
    FindEventReferencesReadResult FindEventReferences(
        Guid moduleVersionId,
        int eventMetadataToken,
        EventAccessorFilter accessor = EventAccessorFilter.All,
        int maxHits = 0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks up the R2R-precompiled native body for a method in its own managed PE. Returns
    /// <see cref="NativeBodyResult.NotFound"/> when the module has no <c>ManagedNativeHeader</c>
    /// (most user assemblies) or the requested method has no entrypoint in the R2R
    /// <c>METHODDEF_ENTRYPOINTS</c> table (e.g. generic methods that were not pre-instantiated).
    /// </summary>
    /// <remarks>
    /// Pure metadata: this method reads the R2R header + tables and returns RVA / size pointers
    /// into the PE. It never decodes instructions — disassembly is delegated to
    /// <c>dotnet-native-mcp.disassemble</c> via the handoff documented in
    /// <c>docs/handoff-contract.md</c>.
    /// </remarks>
    NativeBodyResult GetNativeBodyRef(Guid moduleVersionId, int methodMetadataToken);

    /// <summary>
    /// Ensures the module identified by <paramref name="moduleVersionId"/> is loaded, using
    /// <paramref name="assemblyPathHint"/> (or any previously registered path hint) when the
    /// MVID is not yet known. Implements the §3.1 hint-with-MVID-check protocol: a hinted
    /// path whose on-disk MVID differs from the request is rejected with
    /// <see cref="ErrorKinds"/>.<c>MvidMismatch</c> — the path is a hint, never an override. Returns
    /// <c>null</c> on success.
    /// </summary>
    AssemblyError? EnsureLoaded(Guid moduleVersionId, string? assemblyPathHint);

    /// <summary>
    /// Resolves a type by its case-sensitive full name (with <c>+</c> separating nested types)
    /// to a <see cref="TypeSummary"/>. Direct lookup — does not page the TypeDef table from
    /// the caller side. The module must be loaded; callers can pair this with
    /// <see cref="EnsureLoaded"/> when starting from an (mvid, path) pair.
    /// </summary>
    FindTypeByNameResult FindTypeByFullName(Guid moduleVersionId, string typeFullName);

    /// <summary>
    /// Raised after a module reload (file-on-disk MVID changed, or an explicit
    /// re-<see cref="Load"/> with the same MVID) so downstream caches (decompilation, IL
    /// disassembly, xref) can invalidate entries keyed by the affected MVID. Subscribers
    /// must be idempotent — the event also fires for reload failures (with
    /// <see cref="ModuleReloadedEventArgs.Error"/> non-null) so they can drop stale state.
    /// </summary>
    event EventHandler<ModuleReloadedEventArgs>? ModuleReloaded;
}

/// <summary>Result of <see cref="IMetadataIndex.Load"/>.</summary>
public readonly record struct LoadResult(ModuleSummary? Module, AssemblyError? Error)
{
    public bool IsSuccess => Module is not null;
    public static LoadResult Ok(ModuleSummary m) => new(m, null);
    public static LoadResult Fail(AssemblyError e) => new(null, e);
}

/// <summary>Result of <see cref="IMetadataIndex.Resolve"/>.</summary>
public readonly record struct ResolveResult(MethodSummary? Method, AssemblyError? Error)
{
    public bool IsSuccess => Method is not null;
    public static ResolveResult Ok(MethodSummary m) => new(m, null);
    public static ResolveResult Fail(AssemblyError e) => new(null, e);
}

/// <summary>Result of <see cref="IMetadataIndex.Probe"/>.</summary>
public readonly record struct ProbeResult(Guid Mvid, AssemblyError? Error)
{
    public bool IsSuccess => Error is null;
    public static ProbeResult Ok(Guid mvid) => new(mvid, null);
    public static ProbeResult Fail(AssemblyError e) => new(Guid.Empty, e);
}

/// <summary>Result of <see cref="IMetadataIndex.FindTypeByFullName"/>.</summary>
public readonly record struct FindTypeByNameResult(TypeSummary? Type, AssemblyError? Error)
{
    public bool IsSuccess => Type is not null;
    public static FindTypeByNameResult Ok(TypeSummary t) => new(t, null);
    public static FindTypeByNameResult Fail(AssemblyError e) => new(null, e);
}
