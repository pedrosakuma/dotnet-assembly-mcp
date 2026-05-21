using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Handles;
using DotnetAssemblyMcp.Core.Identity;
using DotnetAssemblyMcp.Core.Metadata.Resolvers;
using HandleKind = System.Reflection.Metadata.HandleKind;
using static DotnetAssemblyMcp.Core.Metadata.MetadataDisplay;
using static DotnetAssemblyMcp.Core.Metadata.MetadataResolver;

namespace DotnetAssemblyMcp.Core.Metadata;

public sealed partial class MetadataIndex
{
    /// <inheritdoc />
    public ListTypesResult ListTypes(Guid moduleVersionId, ListTypesQuery query)
    {
        query ??= new ListTypesQuery();
        if (moduleVersionId == Guid.Empty)
            return ListTypesResult.Fail(new AssemblyError(ErrorKinds.IdentityMalformed, "moduleVersionId is required."));
        if (!_store.TryGet(moduleVersionId, out var module))
            return ListTypesResult.Fail(new AssemblyError(ErrorKinds.ModuleNotFound,
                $"no loaded module has MVID {moduleVersionId}."));

        var pageSize = query.PageSize <= 0 ? ListTypesQuery.DefaultPageSize
            : Math.Min(query.PageSize, ListTypesQuery.MaxPageSize);
        var startRow = query.Cursor is { } c && c > 0 ? c : 1;
        var nsFilter = query.NamespacePrefix;
        var nameFilter = query.NameContains;
        var kindFilter = query.Kind;

        var results = new List<TypeSummary>(pageSize);
        int? nextCursor = null;
        bool truncated = false;

        var totalRows = module.MD.TypeDefinitions.Count;
        for (int row = startRow; row <= totalRows; row++)
        {
            // Defensive: bad metadata shouldn't take the whole enumeration down.
            TypeSummary? summary;
            try { summary = TrySummarizeType(module, row); }
            catch (BadImageFormatException) { continue; }
            if (summary is null) continue;

            if (nsFilter is not null && !MatchesNamespace(summary.FullName, nsFilter)) continue;
            if (nameFilter is not null
                && summary.FullName.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (kindFilter is { } k && summary.Kind != k) continue;

            if (results.Count == pageSize)
            {
                // We already filled the page — this match becomes the next cursor.
                nextCursor = row;
                truncated = true;
                break;
            }
            results.Add(summary);
        }

        return ListTypesResult.Ok(new ListTypesPage(moduleVersionId, results, nextCursor, truncated));
    }
    private static bool MatchesNamespace(string fullName, string nsPrefix)
    {
        // `fullName` is "NS.Outer+Inner" or just "Name". Match the bare namespace portion
        // so "MyApp" matches both "MyApp.Foo" and "MyApp.Bar.Baz" but not "MyAppExt.Foo".
        var plus = fullName.IndexOf('+');
        var head = plus >= 0 ? fullName[..plus] : fullName;
        var dot = head.LastIndexOf('.');
        var ns = dot >= 0 ? head[..dot] : string.Empty;
        if (ns.Length == 0) return nsPrefix.Length == 0;
        if (!ns.StartsWith(nsPrefix, StringComparison.Ordinal)) return false;
        return ns.Length == nsPrefix.Length || ns[nsPrefix.Length] == '.';
    }

    private static TypeSummary? TrySummarizeType(ModuleHandle module, int row)
    {
        var handle = MetadataTokens.TypeDefinitionHandle(row);
        var td = module.MD.GetTypeDefinition(handle);
        var name = module.MD.GetString(td.Name);
        // Skip the synthetic <Module> row (always row 1 in well-formed assemblies).
        if (name == "<Module>") return null;

        var fullName = TypeName(module, td);
        var token = MetadataTokens.GetToken(handle);
        var kind = ClassifyTypeKind(module, td);
        var methodCount = td.GetMethods().Count;
        var vis = td.Attributes & TypeAttributes.VisibilityMask;
        var isPublic = vis == TypeAttributes.Public || vis == TypeAttributes.NestedPublic;
        var baseType = TryRenderTypeReferenceSummary(module, td.BaseType);
        var interfaces = ReadInterfaceImplementations(module, td);
        var generics = MetadataDisplay.DecodeGenericParameters(module, td.GetGenericParameters());
        return new TypeSummary(module.Mvid, token, HandleSyntax.FormatType(module.Mvid, token),
            fullName, kind, methodCount, isPublic, baseType, interfaces,
            Instantiation: null, GenericParameters: generics);
    }

    private static TypeReferenceSummary? TryRenderTypeReferenceSummary(ModuleHandle module, EntityHandle handle)
        => MetadataDisplay.TryRenderTypeReferenceSummary(module, handle);

    private static IReadOnlyList<TypeReferenceSummary> ReadInterfaceImplementations(ModuleHandle module, TypeDefinition td)
    {
        var impls = td.GetInterfaceImplementations();
        if (impls.Count == 0) return Array.Empty<TypeReferenceSummary>();
        var list = new List<TypeReferenceSummary>(impls.Count);
        foreach (var ih in impls)
        {
            try
            {
                var imp = module.MD.GetInterfaceImplementation(ih);
                var summary = TryRenderTypeReferenceSummary(module, imp.Interface);
                if (summary is not null) list.Add(summary);
            }
            catch (BadImageFormatException) { /* skip malformed row */ }
        }
        return list;
    }

    private static TypeKind ClassifyTypeKind(ModuleHandle module, TypeDefinition td)
    {
        if ((td.Attributes & TypeAttributes.Interface) != 0) return TypeKind.Interface;

        // Resolve the base type's full name through a TypeRef or TypeDef. Anything we can't
        // resolve safely defaults to Class — that's the right answer for object-rooted types.
        var baseHandle = td.BaseType;
        if (baseHandle.IsNil) return TypeKind.Class;

        string? baseFullName = baseHandle.Kind switch
        {
            HandleKind.TypeReference => RenderTypeRef(module, (TypeReferenceHandle)baseHandle),
            HandleKind.TypeDefinition => RenderTypeDef(module, (TypeDefinitionHandle)baseHandle),
            _ => null,
        };
        return baseFullName switch
        {
            "System.Enum" => TypeKind.Enum,
            "System.ValueType" => TypeKind.Struct,
            "System.MulticastDelegate" or "System.Delegate" => TypeKind.Delegate,
            _ => TypeKind.Class,
        };
    }
    /// <inheritdoc />
    public GetTypeResult GetTypeDefinition(Guid moduleVersionId, int typeMetadataToken)
    {
        if (moduleVersionId == Guid.Empty)
            return GetTypeResult.Fail(new AssemblyError(ErrorKinds.IdentityMalformed, "moduleVersionId is required."));
        if (!_store.TryGet(moduleVersionId, out var module))
            return GetTypeResult.Fail(new AssemblyError(ErrorKinds.ModuleNotFound,
                $"no loaded module has MVID {moduleVersionId:D}."));

        EntityHandle handle;
        try { handle = (EntityHandle)MetadataTokens.Handle(typeMetadataToken); }
        catch (ArgumentException ex)
        {
            return GetTypeResult.Fail(new AssemblyError(ErrorKinds.IdentityMalformed,
                $"could not interpret token 0x{typeMetadataToken:X8} as a metadata handle.", ex.Message));
        }
        if (handle.Kind != HandleKind.TypeDefinition)
        {
            return GetTypeResult.Fail(new AssemblyError(ErrorKinds.TokenWrongTable,
                $"token 0x{typeMetadataToken:X8} is in table {handle.Kind}, expected TypeDefinition (0x02)."));
        }

        var row = MetadataTokens.GetRowNumber((TypeDefinitionHandle)handle);
        if (row <= 0 || row > module.MD.TypeDefinitions.Count)
            return GetTypeResult.Fail(new AssemblyError(ErrorKinds.TokenOutOfRange,
                $"TypeDef token 0x{typeMetadataToken:X8} is not present in this module."));

        TypeSummary? summary;
        try { summary = TrySummarizeType(module, row); }
        catch (BadImageFormatException ex)
        {
            return GetTypeResult.Fail(new AssemblyError(ErrorKinds.TokenOutOfRange,
                $"could not read TypeDef row {row}: {ex.Message}"));
        }
        if (summary is null)
            return GetTypeResult.Fail(new AssemblyError(ErrorKinds.TokenOutOfRange,
                $"TypeDef row {row} is the synthetic <Module> row."));
        return GetTypeResult.Ok(summary);
    }
    /// <inheritdoc />
    public ListDerivedTypesResult ListDerivedTypes(Guid moduleVersionId, int baseTypeMetadataToken, ListDerivedTypesQuery query)
    {
        query ??= new ListDerivedTypesQuery();
        if (moduleVersionId == Guid.Empty)
            return ListDerivedTypesResult.Fail(new AssemblyError(ErrorKinds.IdentityMalformed, "moduleVersionId is required."));
        if (!_store.TryGet(moduleVersionId, out var module))
            return ListDerivedTypesResult.Fail(new AssemblyError(ErrorKinds.ModuleNotFound,
                $"no loaded module has MVID {moduleVersionId:D}."));

        EntityHandle baseHandle;
        try { baseHandle = (EntityHandle)MetadataTokens.Handle(baseTypeMetadataToken); }
        catch (ArgumentException ex)
        {
            return ListDerivedTypesResult.Fail(new AssemblyError(ErrorKinds.IdentityMalformed,
                $"could not interpret token 0x{baseTypeMetadataToken:X8} as a metadata handle.", ex.Message));
        }
        if (baseHandle.Kind != HandleKind.TypeDefinition)
        {
            return ListDerivedTypesResult.Fail(new AssemblyError(ErrorKinds.TokenWrongTable,
                $"token 0x{baseTypeMetadataToken:X8} is in table {baseHandle.Kind}, expected TypeDefinition (0x02)."));
        }
        TypeDefinition baseTd;
        try { baseTd = module.MD.GetTypeDefinition((TypeDefinitionHandle)baseHandle); }
        catch (Exception ex) when (ex is BadImageFormatException or ArgumentException)
        {
            return ListDerivedTypesResult.Fail(new AssemblyError(ErrorKinds.TokenOutOfRange,
                $"TypeDef token 0x{baseTypeMetadataToken:X8} is not present in this module."));
        }
        var baseFullName = TypeName(module, baseTd);
        var baseAsmName = module.MD.GetString(module.MD.GetAssemblyDefinition().Name);
        var baseKey = (Asm: baseAsmName, Full: baseFullName);

        var pageSize = query.PageSize <= 0 ? ListDerivedTypesQuery.DefaultPageSize
            : Math.Min(query.PageSize, ListDerivedTypesQuery.MaxPageSize);
        var startOffset = query.Cursor is { } c && c > 0 ? c : 0;

        // Build the parent relation across every loaded module (or reuse the cached one):
        //  - localParents: (childMvid, childToken) -> [(parentMvid, parentToken, args?)] (same module
        //    via TypeDef, or via TypeSpec whose CLASS token resolves to a same-module TypeDef).
        //  - crossParents: (childMvid, childToken) -> [(parentAsm, parentFull, args?)] (TypeRef or
        //    TypeSpec whose CLASS token resolves to a cross-module TypeRef).
        // BaseType and every InterfaceImplementation are captured so a single walk covers both
        // class derivation and interface implementation. TypeSpec parents (e.g.
        // `class OrderHandler : IRequestHandler<int,string>`) populate `args` with the closed
        // instantiation in CLR wire format ([ "System.Int32", "System.String" ]); non-spec edges
        // carry null args. Per issue #67 the `args` payload feeds both the MatchInstantiation
        // filter and the TypeSummary.Instantiation stamp on the returned page.
        //
        // The walk visits every TypeDef in every loaded module, so we cache the resulting maps
        // in TypeNavigationIndex. The cache is invalidated when a module is reloaded (via
        // IModuleScopedCache fan-out) and rebuilt when the loaded-module set changes (new
        // load), so repeated ListDerivedTypes calls against a stable set pay the O(N·M) walk
        // only once.
        var (localParents, crossParents) = _typeNavigation.GetParentMaps(builder =>
        {
            foreach (var m in _store.Modules)
            {
                var childMvid = m.Mvid;
                var md = m.MD;
                int n;
                try { n = md.TypeDefinitions.Count; }
                catch (BadImageFormatException) { continue; }

                for (int row = 1; row <= n; row++)
                {
                    TypeDefinition td;
                    try { td = md.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(row)); }
                    catch (BadImageFormatException) { continue; }

                    int childToken = MetadataTokens.GetToken(MetadataTokens.TypeDefinitionHandle(row));
                    var childKey = (childMvid, childToken);

                    AddParentEdge(m, childMvid, childKey, td.BaseType, builder.Local, builder.Cross);

                    InterfaceImplementationHandleCollection iis;
                    try { iis = td.GetInterfaceImplementations(); }
                    catch (BadImageFormatException) { continue; }
                    foreach (var iih in iis)
                    {
                        EntityHandle ih;
                        try { ih = md.GetInterfaceImplementation(iih).Interface; }
                        catch (BadImageFormatException) { continue; }
                        AddParentEdge(m, childMvid, childKey, ih, builder.Local, builder.Cross);
                    }
                }
            }
        });

        // Pre-render the MatchInstantiation filter into CLR wire-format strings so per-edge
        // comparison is a flat string equality check. Null/empty => open match.
        string[]? expectedArgs = null;
        if (query.MatchInstantiation is { Count: > 0 } mi)
        {
            expectedArgs = new string[mi.Count];
            for (int i = 0; i < mi.Count; i++) expectedArgs[i] = mi[i].Format();
        }

        bool MatchesInstantiationFilter(IReadOnlyList<string>? candidate)
        {
            if (expectedArgs is null) return true;
            if (candidate is null) return false;
            if (candidate.Count != expectedArgs.Length) return false;
            for (int i = 0; i < expectedArgs.Length; i++)
                if (!string.Equals(candidate[i], expectedArgs[i], StringComparison.Ordinal))
                    return false;
            return true;
        }

        // Edge test: does (childMvid, childToken) have `baseKey` as one of its direct parents,
        // and does the parent edge satisfy the MatchInstantiation filter? On success returns
        // the matched edge's args (may be null for non-spec edges) so the caller can stamp
        // them on the resulting TypeSummary.
        bool IsDirectChild((Guid mvid, int token) child, out IReadOnlyList<string>? matchedArgs)
        {
            matchedArgs = null;
            if (child.mvid == moduleVersionId
                && localParents.TryGetValue(child, out var lps))
            {
                foreach (var p in lps)
                {
                    if (p.mvid == moduleVersionId && p.token == baseTypeMetadataToken
                        && MatchesInstantiationFilter(p.args))
                    {
                        matchedArgs = p.args;
                        return true;
                    }
                }
            }
            if (crossParents.TryGetValue(child, out var cps))
            {
                foreach (var p in cps)
                {
                    if (p.asm == baseKey.Asm && p.full == baseKey.Full
                        && MatchesInstantiationFilter(p.args))
                    {
                        matchedArgs = p.args;
                        return true;
                    }
                }
            }
            return false;
        }

        // Direct children of the root, used both as the answer in DirectOnly mode and as the
        // BFS seed in transitive mode. directInstantiation captures the matched-edge args so
        // the page can stamp TypeSummary.Instantiation on the closed-generic hits.
        var direct = new HashSet<(Guid, int)>();
        var directInstantiation = new Dictionary<(Guid, int), IReadOnlyList<string>?>();
        foreach (var child in localParents.Keys)
        {
            if (IsDirectChild(child, out var args) && direct.Add(child))
                directInstantiation[child] = args;
        }
        foreach (var child in crossParents.Keys)
        {
            if (IsDirectChild(child, out var args) && direct.Add(child))
                directInstantiation[child] = args;
        }

        HashSet<(Guid, int)> hits;
        if (query.DirectOnly)
        {
            hits = direct;
        }
        else
        {
            // Reverse parent edges so we can BFS downwards. Local edges produce
            // (parentMvid, parentToken) -> child; cross edges produce (asm, full) -> child.
            var childrenByLocal = new Dictionary<(Guid, int), List<(Guid, int)>>();
            var childrenByCross = new Dictionary<(string, string), List<(Guid, int)>>();
            foreach (var kv in localParents)
                foreach (var p in kv.Value)
                {
                    var key = (p.mvid, p.token);
                    if (!childrenByLocal.TryGetValue(key, out var list))
                        childrenByLocal[key] = list = new List<(Guid, int)>();
                    list.Add(kv.Key);
                }
            foreach (var kv in crossParents)
                foreach (var p in kv.Value)
                {
                    var key = (p.asm, p.full);
                    if (!childrenByCross.TryGetValue(key, out var list))
                        childrenByCross[key] = list = new List<(Guid, int)>();
                    list.Add(kv.Key);
                }

            hits = new HashSet<(Guid, int)>();
            var queue = new Queue<(Guid, int)>();
            foreach (var d in direct) { hits.Add(d); queue.Enqueue(d); }

            // Cross-module edges that match the root by (asm, full). Note: when the user
            // supplied MatchInstantiation, these transitive root-cross edges are also gated
            // by the same filter so descendants behind a non-matching closed parent aren't
            // accidentally pulled in.
            if (expectedArgs is null && childrenByCross.TryGetValue(baseKey, out var rootCross))
                foreach (var crossChild in rootCross)
                    if (hits.Add(crossChild)) queue.Enqueue(crossChild);

            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                // Each descendant's own (asm, full) lets other modules' TypeRefs reach it.
                if (!_store.TryGet(node.Item1, out var nodeModule)) continue;
                string nodeAsm;
                string nodeFull;
                try
                {
                    var td = nodeModule.MD.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(
                        MetadataTokens.GetRowNumber((EntityHandle)MetadataTokens.Handle(node.Item2))));
                    nodeFull = TypeName(nodeModule, td);
                    nodeAsm = nodeModule.MD.GetString(nodeModule.MD.GetAssemblyDefinition().Name);
                }
                catch (Exception ex) when (ex is BadImageFormatException or ArgumentException) { continue; }

                if (childrenByLocal.TryGetValue(node, out var localKids))
                    foreach (var k in localKids)
                        if (hits.Add(k)) queue.Enqueue(k);
                if (childrenByCross.TryGetValue((nodeAsm, nodeFull), out var crossKids))
                    foreach (var k in crossKids)
                        if (hits.Add(k)) queue.Enqueue(k);
            }
        }

        // Stable order: by module path, then by token within the module.
        var ordered = hits
            .Select(h => (Hit: h, Path: _store.TryGet(h.Item1, out var mm) ? mm.Path : string.Empty))
            .OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Hit.Item2)
            .Select(t => t.Hit)
            .ToList();

        var results = new List<TypeSummary>(Math.Min(pageSize, 16));
        int? nextCursor = null;
        bool truncated = false;

        for (int i = startOffset; i < ordered.Count; i++)
        {
            var (hitMvid, hitToken) = ordered[i];
            if (!_store.TryGet(hitMvid, out var hitModule)) continue;
            int hitRow;
            try { hitRow = MetadataTokens.GetRowNumber((EntityHandle)MetadataTokens.Handle(hitToken)); }
            catch (ArgumentException) { continue; }

            TypeSummary? summary;
            try { summary = TrySummarizeType(hitModule, hitRow); }
            catch (BadImageFormatException) { continue; }
            if (summary is null) continue;

            // Stamp the closed instantiation that satisfied the match on direct hits reached
            // via a TypeSpec parent (issue #67). Transitive descendants and non-spec direct
            // hits leave Instantiation null.
            if (directInstantiation.TryGetValue((hitMvid, hitToken), out var stamp) && stamp is not null)
                summary = summary with { Instantiation = stamp };

            if (results.Count == pageSize)
            {
                nextCursor = i;
                truncated = true;
                break;
            }
            results.Add(summary);
        }

        return ListDerivedTypesResult.Ok(new ListDerivedTypesPage(
            moduleVersionId, baseTypeMetadataToken, baseFullName, results, nextCursor, truncated));
    }
    private static void AddParentEdge(
        ModuleHandle childModule,
        Guid childMvid,
        (Guid, int) childKey,
        EntityHandle parentHandle,
        Dictionary<(Guid, int), List<(Guid mvid, int token, IReadOnlyList<string>? args)>> localParents,
        Dictionary<(Guid, int), List<(string asm, string full, IReadOnlyList<string>? args)>> crossParents)
    {
        if (parentHandle.IsNil) return;
        switch (parentHandle.Kind)
        {
            case HandleKind.TypeDefinition:
                if (!localParents.TryGetValue(childKey, out var lps))
                    localParents[childKey] = lps = new List<(Guid, int, IReadOnlyList<string>?)>();
                lps.Add((childMvid, MetadataTokens.GetToken(parentHandle), null));
                break;
            case HandleKind.TypeReference:
                var full = ResolveTypeRefName(childModule, (TypeReferenceHandle)parentHandle, out var asm);
                if (full is null || asm is null) return;
                if (!crossParents.TryGetValue(childKey, out var cps))
                    crossParents[childKey] = cps = new List<(string, string, IReadOnlyList<string>?)>();
                cps.Add((asm, full, null));
                break;
            case HandleKind.TypeSpecification:
                AddSpecParentEdge(childModule, childMvid, childKey,
                    (TypeSpecificationHandle)parentHandle, localParents, crossParents);
                break;
        }
    }
    /// <summary>
    /// Decodes a TypeSpec parent edge (issue #67). Generic-instantiation base/interface
    /// signatures (e.g. <c>: IRequestHandler&lt;int,string&gt;</c>) materialize as a
    /// <c>0x15 GENERICINST</c> blob: byte stream is
    /// <c>0x15 (CLASS|VALUETYPE) &lt;coded TypeDefOrRefOrSpec&gt; &lt;argCount&gt; &lt;arg sigs&gt;</c>.
    /// We peek the underlying TypeDef/TypeRef to identify the OPEN parent (so the matcher
    /// can answer queries against the open form, e.g. <c>IRequestHandler`2</c>), and decode
    /// the full spec via <see cref="WireFormatSignatureProvider"/> to capture the closed
    /// args in CLR wire format. Non-GENERICINST specs (rare for base types) are skipped.
    /// </summary>
    private static void AddSpecParentEdge(
        ModuleHandle childModule,
        Guid childMvid,
        (Guid, int) childKey,
        TypeSpecificationHandle handle,
        Dictionary<(Guid, int), List<(Guid mvid, int token, IReadOnlyList<string>? args)>> localParents,
        Dictionary<(Guid, int), List<(string asm, string full, IReadOnlyList<string>? args)>> crossParents)
    {
        try
        {
            var ts = childModule.MD.GetTypeSpecification(handle);
            var sigReader = childModule.MD.GetBlobReader(ts.Signature);
            if (sigReader.RemainingBytes < 2) return;
            if (sigReader.ReadByte() != 0x15 /* GENERICINST */) return;
            var classOrValue = sigReader.ReadByte();
            if (classOrValue != 0x12 /* CLASS */ && classOrValue != 0x11 /* VALUETYPE */) return;
            var encoded = sigReader.ReadCompressedInteger();
            var openHandle = EntityHandleFromCoded(encoded);

            // Decode the closed args via the typed provider then re-parse to extract each
            // arg as a canonical wire-format string ("System.Int32", "List`1[System.Int32]", …).
            var decoded = ts.DecodeSignature(new WireFormatSignatureProvider(), genericContext: (object?)null);
            if (!GenericTypeName.TryParse(decoded, out var node, out _, out _)) return;
            if (node is not GenericTypeName.Named named) return;
            if (named.TypeArguments.IsDefaultOrEmpty) return;
            var args = new string[named.TypeArguments.Length];
            for (int i = 0; i < args.Length; i++) args[i] = named.TypeArguments[i].Format();

            switch (openHandle.Kind)
            {
                case HandleKind.TypeDefinition:
                    if (!localParents.TryGetValue(childKey, out var lps))
                        localParents[childKey] = lps = new List<(Guid, int, IReadOnlyList<string>?)>();
                    lps.Add((childMvid, MetadataTokens.GetToken(openHandle), args));
                    break;
                case HandleKind.TypeReference:
                    var full = ResolveTypeRefName(childModule, (TypeReferenceHandle)openHandle, out var asm);
                    if (full is null || asm is null) return;
                    if (!crossParents.TryGetValue(childKey, out var cps))
                        crossParents[childKey] = cps = new List<(string, string, IReadOnlyList<string>?)>();
                    cps.Add((asm, full, args));
                    break;
            }
        }
        catch (BadImageFormatException) { /* skip malformed spec */ }
    }
    /// <inheritdoc />
    public FindTypeByNameResult FindTypeByFullName(Guid moduleVersionId, string typeFullName)
    {
        if (moduleVersionId == Guid.Empty)
            return FindTypeByNameResult.Fail(new AssemblyError(ErrorKinds.IdentityMalformed, "moduleVersionId is required."));
        if (string.IsNullOrWhiteSpace(typeFullName))
            return FindTypeByNameResult.Fail(new AssemblyError(ErrorKinds.InvalidArgument, "typeFullName is required."));
        if (!_store.TryGet(moduleVersionId, out var module))
            return FindTypeByNameResult.Fail(new AssemblyError(ErrorKinds.ModuleNotFound,
                $"no loaded module has MVID {moduleVersionId:D}."));

        var token = _typeNavigation.TryFindTypeToken(module, typeFullName);
        if (token is null)
            return FindTypeByNameResult.Fail(new AssemblyError(ErrorKinds.IdentityMalformed,
                $"type '{typeFullName}' not found in module {moduleVersionId:D}."));

        int row;
        try { row = MetadataTokens.GetRowNumber((EntityHandle)MetadataTokens.Handle(token.Value)); }
        catch (ArgumentException)
        {
            return FindTypeByNameResult.Fail(new AssemblyError(ErrorKinds.IdentityMalformed,
                $"type '{typeFullName}' not found in module {moduleVersionId:D}."));
        }
        TypeSummary? summary;
        try { summary = TrySummarizeType(module, row); }
        catch (BadImageFormatException) { summary = null; }
        return summary is not null
            ? FindTypeByNameResult.Ok(summary)
            : FindTypeByNameResult.Fail(new AssemblyError(ErrorKinds.IdentityMalformed,
                $"type '{typeFullName}' not found in module {moduleVersionId:D}."));
    }
}
