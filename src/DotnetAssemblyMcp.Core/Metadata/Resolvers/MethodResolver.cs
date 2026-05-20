using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Handles;
using DotnetAssemblyMcp.Core.Identity;
using HandleKind = System.Reflection.Metadata.HandleKind;
using static DotnetAssemblyMcp.Core.Metadata.MetadataDisplay;

namespace DotnetAssemblyMcp.Core.Metadata.Resolvers;

/// <summary>
/// Handle-to-method lookup + §3.5 closed-signature instantiation. Extracted from
/// <see cref="MetadataIndex"/> in issue #92. Owns the <c>Resolve</c> public API plus
/// the shared <see cref="TryResolveMethod"/> helper consumed by every other resolver
/// (<see cref="IlBodyReader"/>, <see cref="PdbResolver"/>, FindCallers).
/// </summary>
internal sealed class MethodResolver
{
    private readonly ModuleStore _store;

    public MethodResolver(ModuleStore store) => _store = store;

    public ResolveResult Resolve(MethodIdentity identity)
    {
        if (identity.ModuleVersionId == Guid.Empty)
            return ResolveResult.Fail(new AssemblyError(ErrorKinds.IdentityMalformed, "moduleVersionId is required."));
        if (identity.MetadataToken == 0)
            return ResolveResult.Fail(new AssemblyError(ErrorKinds.IdentityMalformed, "metadataToken is required."));

        if (!_store.TryGet(identity.ModuleVersionId, out var module))
        {
            return ResolveResult.Fail(new AssemblyError(
                ErrorKinds.ModuleNotFound,
                $"no loaded module has MVID {identity.ModuleVersionId}.",
                "call load_assembly with the path to the assembly first, or list_assemblies to see what is loaded."));
        }

        HandleKind handleKind;
        try
        {
            handleKind = MetadataTokens.Handle(identity.MetadataToken).Kind;
        }
        catch (ArgumentException)
        {
            return ResolveResult.Fail(new AssemblyError(
                ErrorKinds.IdentityMalformed,
                $"metadataToken 0x{identity.MetadataToken:X8} is not a valid metadata token."));
        }
        if (handleKind != HandleKind.MethodDefinition)
        {
            return ResolveResult.Fail(new AssemblyError(
                ErrorKinds.TokenWrongTable,
                $"metadataToken 0x{identity.MetadataToken:X8} is a {handleKind}, expected MethodDefinition (table 0x06)."));
        }

        var methodHandle = (MethodDefinitionHandle)MetadataTokens.Handle(identity.MetadataToken);
        var rid = MetadataTokens.GetRowNumber(methodHandle);
        if (rid <= 0 || rid > module.MD.MethodDefinitions.Count)
        {
            return ResolveResult.Fail(new AssemblyError(
                ErrorKinds.TokenOutOfRange,
                $"MethodDef row {rid} exceeds the module's table size ({module.MD.MethodDefinitions.Count})."));
        }

        var summary = SummarizeMethod(module, methodHandle, identity.MetadataToken);

        // §3.5: optional closed generic instantiation. Either (or both) of:
        //   - explicit TypeGenericArguments / MethodGenericArguments string lists, validated
        //     against loaded modules.
        //   - MethodSpec fast-path (#9): a (mvid, token) into a MethodSpec row whose
        //     Instantiation blob carries the method-level args, and whose Method.Parent
        //     (if a TypeSpec) carries the type-level args.
        // When both are present they are cross-checked element-wise; a mismatch yields
        // GenericInstantiationMismatch.
        IReadOnlyList<string>? typeRendered = null;
        IReadOnlyList<string>? methodRendered = null;

        if (identity.MethodSpec is { } specRef)
        {
            // §3.5 fallback: if explicit args were supplied AND the methodSpec module is not loaded,
            // skip the fast-path and let the explicit-args branch handle validation/substitution.
            bool hasExplicitArgs = identity.TypeGenericArguments is { Count: > 0 }
                                   || identity.MethodGenericArguments is { Count: > 0 };
            var specDecoded = TryDecodeMethodSpec(specRef, allowMissingModule: hasExplicitArgs);
            if (specDecoded.Error is not null) return ResolveResult.Fail(specDecoded.Error);

            if (specDecoded.SpecModule is not null && specDecoded.SpecRow is { } specRow)
            {
                // §3.5 target validation: the MethodSpec.Method must resolve to the requested MethodDef.
                if (!MethodSpecTargetsMethodDef(
                        specDecoded.SpecModule, specRow,
                        identity.ModuleVersionId, identity.MetadataToken,
                        out var targetErr))
                {
                    return ResolveResult.Fail(targetErr!);
                }

                typeRendered = specDecoded.TypeRendered;
                methodRendered = specDecoded.MethodRendered;
            }
        }

        bool hasTypeArgs = identity.TypeGenericArguments is { Count: > 0 };
        bool hasMethodArgs = identity.MethodGenericArguments is { Count: > 0 };
        if (hasTypeArgs || hasMethodArgs)
        {
            var def0 = module.MD.GetMethodDefinition(methodHandle);
            var typeDef0 = module.MD.GetTypeDefinition(def0.GetDeclaringType());
            int typeArity = typeDef0.GetGenericParameters().Count;
            int methodArity = def0.GetGenericParameters().Count;

            int gotType = identity.TypeGenericArguments?.Count ?? 0;
            int gotMethod = identity.MethodGenericArguments?.Count ?? 0;

            if (gotType != 0 && gotType != typeArity)
                return ResolveResult.Fail(new AssemblyError(
                    ErrorKinds.InvalidArgument,
                    $"genericTypeArguments.type carries {gotType} args but the declaring type has arity {typeArity}."));
            if (gotMethod != 0 && gotMethod != methodArity)
                return ResolveResult.Fail(new AssemblyError(
                    ErrorKinds.InvalidArgument,
                    $"genericTypeArguments.method carries {gotMethod} args but the method has arity {methodArity}."));

            var readers = _store.SnapshotReaders();

            if (hasTypeArgs)
            {
                var (rendered, err) = GenericArgResolver.RenderAndValidate(
                    identity.TypeGenericArguments!, identity.ModuleVersionId, readers);
                if (err is not null) return ResolveResult.Fail(err);
                if (typeRendered is not null && !RenderedSequenceEqual(typeRendered, rendered!))
                    return ResolveResult.Fail(new AssemblyError(
                        ErrorKinds.GenericInstantiationMismatch,
                        $"methodSpec encodes type-args [{string.Join(",", typeRendered)}] but genericTypeArguments has [{string.Join(",", rendered!)}]."));
                typeRendered = rendered!;
            }

            if (hasMethodArgs)
            {
                var (rendered, err) = GenericArgResolver.RenderAndValidate(
                    identity.MethodGenericArguments!, identity.ModuleVersionId, readers);
                if (err is not null) return ResolveResult.Fail(err);
                if (methodRendered is not null && !RenderedSequenceEqual(methodRendered, rendered!))
                    return ResolveResult.Fail(new AssemblyError(
                        ErrorKinds.GenericInstantiationMismatch,
                        $"methodSpec encodes method-args [{string.Join(",", methodRendered)}] but genericMethodArguments has [{string.Join(",", rendered!)}]."));
                methodRendered = rendered!;
            }
        }

        if (typeRendered is not null || methodRendered is not null)
        {
            var def = module.MD.GetMethodDefinition(methodHandle);
            var typeDef = module.MD.GetTypeDefinition(def.GetDeclaringType());

            var provider = new SubstitutingStringSignatureProvider(
                module.MD, typeRendered ?? Array.Empty<string>(), methodRendered ?? Array.Empty<string>());
            var sig = def.DecodeSignature(provider, genericContext: null);
            var paramList = string.Join(", ", sig.ParameterTypes);
            var fullType = TypeName(module, typeDef);
            var methodName = module.MD.GetString(def.Name);
            var closedSig = $"{sig.ReturnType} {fullType}.{methodName}({paramList})";

            summary = summary with { Signature = closedSig };
        }

        return ResolveResult.Ok(summary);
    }

    internal static bool RenderedSequenceEqual(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>
    /// §3.5 fast-path decoder. Returns the type/method instantiation rendered in wire format
    /// from a <c>MethodSpec</c> row. When <paramref name="allowMissingModule"/> is true and the
    /// spec module is not loaded, returns success with all-null payload (caller falls back to
    /// explicit args). Otherwise an unloaded module yields <see cref="ErrorKinds.ModuleNotFound"/>.
    /// </summary>
    internal MethodSpecDecodeResult TryDecodeMethodSpec(MethodSpecHandle specRef, bool allowMissingModule)
    {
        if (!_store.TryGet(specRef.ModuleVersionId, out var specModule))
        {
            if (allowMissingModule) return default;
            return new MethodSpecDecodeResult(null, null, null, null, new AssemblyError(
                ErrorKinds.ModuleNotFound,
                $"methodSpec module {specRef.ModuleVersionId:D} is not loaded; load it first or omit methodSpec."));
        }

        EntityHandle specHandle;
        try { specHandle = (EntityHandle)MetadataTokens.Handle(specRef.MetadataToken); }
        catch (ArgumentException)
        {
            return new MethodSpecDecodeResult(null, null, null, null, new AssemblyError(
                ErrorKinds.InvalidArgument,
                $"methodSpec token 0x{specRef.MetadataToken:X8} is not a valid metadata token."));
        }
        if (specHandle.Kind != HandleKind.MethodSpecification)
        {
            return new MethodSpecDecodeResult(null, null, null, null, new AssemblyError(
                ErrorKinds.TokenWrongTable,
                $"methodSpec token 0x{specRef.MetadataToken:X8} is a {specHandle.Kind}, expected MethodSpecification (table 0x2B)."));
        }

        MethodSpecification specRow;
        try { specRow = specModule.MD.GetMethodSpecification((MethodSpecificationHandle)specHandle); }
        catch (BadImageFormatException)
        {
            return new MethodSpecDecodeResult(null, null, null, null, new AssemblyError(
                ErrorKinds.InvalidArgument,
                $"methodSpec token 0x{specRef.MetadataToken:X8} could not be decoded."));
        }

        var wireProvider = new WireFormatSignatureProvider();
        IReadOnlyList<string> methodRendered;
        try
        {
            methodRendered = specRow.DecodeSignature(wireProvider, genericContext: (object?)null);
        }
        catch (BadImageFormatException)
        {
            return new MethodSpecDecodeResult(null, null, null, null, new AssemblyError(
                ErrorKinds.InvalidArgument,
                "methodSpec Instantiation blob could not be decoded."));
        }

        IReadOnlyList<string>? typeRendered = null;
        if (specRow.Method.Kind == HandleKind.MemberReference)
        {
            try
            {
                var mr = specModule.MD.GetMemberReference((MemberReferenceHandle)specRow.Method);
                if (mr.Parent.Kind == HandleKind.TypeSpecification)
                {
                    var ts = specModule.MD.GetTypeSpecification((TypeSpecificationHandle)mr.Parent);
                    var typeDecoded = ts.DecodeSignature(wireProvider, genericContext: (object?)null);
                    if (GenericTypeName.TryParse(typeDecoded, out var node, out _, out _)
                        && node is GenericTypeName.Named named
                        && !named.TypeArguments.IsDefaultOrEmpty)
                    {
                        typeRendered = named.TypeArguments.Select(a => a.Format()).ToArray();
                    }
                }
            }
            catch (BadImageFormatException) { /* leave typeRendered null */ }
        }

        return new MethodSpecDecodeResult(specModule, specRow, typeRendered, methodRendered, null);
    }

    /// <summary>
    /// §3.5 target validation: verifies that <paramref name="specRow"/>.<c>Method</c> resolves
    /// to the requested <c>(targetMvid, targetMethodDefToken)</c>. Returns false (with a
    /// <see cref="ErrorKinds.GenericInstantiationMismatch"/> error) when the spec was
    /// authored against a different MethodDef.
    /// </summary>
    internal bool MethodSpecTargetsMethodDef(
        ModuleHandle specModule, MethodSpecification specRow,
        Guid targetMvid, int targetMethodDefToken,
        out AssemblyError? validationError)
    {
        validationError = null;
        switch (specRow.Method.Kind)
        {
            case HandleKind.MethodDefinition:
                if (specModule.Mvid != targetMvid)
                {
                    validationError = new AssemblyError(
                        ErrorKinds.GenericInstantiationMismatch,
                        $"methodSpec.Method is a MethodDef in module {specModule.Mvid:D} but the target method lives in {targetMvid:D}.");
                    return false;
                }
                var specMethodToken = MetadataTokens.GetToken(specRow.Method);
                if (specMethodToken != targetMethodDefToken)
                {
                    validationError = new AssemblyError(
                        ErrorKinds.GenericInstantiationMismatch,
                        $"methodSpec.Method targets MethodDef 0x{specMethodToken:X8} but the requested identity is 0x{targetMethodDefToken:X8}.");
                    return false;
                }
                return true;

            case HandleKind.MemberReference:
                if (!_store.TryGet(targetMvid, out var targetMod))
                {
                    validationError = new AssemblyError(
                        ErrorKinds.ModuleNotFound,
                        $"target module {targetMvid:D} is not loaded; cannot cross-check methodSpec target.");
                    return false;
                }
                MethodDefinitionHandle targetHandle;
                try { targetHandle = (MethodDefinitionHandle)MetadataTokens.Handle(targetMethodDefToken); }
                catch (ArgumentException)
                {
                    validationError = new AssemblyError(
                        ErrorKinds.InvalidArgument,
                        $"target token 0x{targetMethodDefToken:X8} is not a valid metadata token.");
                    return false;
                }
                var key = XrefIndex.BuildCalleeKey(targetMod, targetHandle);
                try
                {
                    var mr = specModule.MD.GetMemberReference((MemberReferenceHandle)specRow.Method);
                    if (mr.GetKind() != MemberReferenceKind.Method
                        || !CallerInstantiationMatcher.MemberRefMatchesCalleeKey(specModule, mr, key))
                    {
                        validationError = new AssemblyError(
                            ErrorKinds.GenericInstantiationMismatch,
                            "methodSpec.Method (MemberRef) does not resolve to the requested MethodDef (assembly/type/name/signature mismatch).");
                        return false;
                    }
                }
                catch (BadImageFormatException)
                {
                    validationError = new AssemblyError(
                        ErrorKinds.InvalidArgument,
                        "methodSpec.Method MemberRef row could not be decoded.");
                    return false;
                }
                return true;

            default:
                validationError = new AssemblyError(
                    ErrorKinds.InvalidArgument,
                    $"methodSpec.Method has unsupported kind {specRow.Method.Kind}.");
                return false;
        }
    }

    /// <summary>
    /// Shared resolver consumed by IL/PDB resolvers and FindCallers. Validates the identity
    /// shape (non-empty MVID + token, MethodDef table, row in range) and returns the loaded
    /// module + handle on success.
    /// </summary>
    internal ResolvedMethod TryResolveMethod(MethodIdentity identity)
    {
        if (identity is null)
            return new ResolvedMethod(null, default, new AssemblyError(ErrorKinds.IdentityMalformed, "identity is required."));
        if (identity.ModuleVersionId == Guid.Empty)
            return new ResolvedMethod(null, default, new AssemblyError(ErrorKinds.IdentityMalformed, "moduleVersionId is required."));
        if (identity.MetadataToken == 0)
            return new ResolvedMethod(null, default, new AssemblyError(ErrorKinds.IdentityMalformed, "metadataToken is required."));

        if (!_store.TryGet(identity.ModuleVersionId, out var module))
        {
            return new ResolvedMethod(null, default, new AssemblyError(
                ErrorKinds.ModuleNotFound,
                $"no loaded module has MVID {identity.ModuleVersionId}."));
        }

        EntityHandle h;
        try { h = (EntityHandle)MetadataTokens.Handle(identity.MetadataToken); }
        catch (ArgumentException ex)
        {
            return new ResolvedMethod(null, default, new AssemblyError(ErrorKinds.InvalidArgument,
                $"could not decode metadataToken 0x{identity.MetadataToken:X8}: {ex.Message}"));
        }
        if (h.Kind != HandleKind.MethodDefinition)
        {
            return new ResolvedMethod(null, default, new AssemblyError(ErrorKinds.TokenWrongTable,
                $"metadataToken 0x{identity.MetadataToken:X8} is a {h.Kind}, expected MethodDefinition (table 0x06)."));
        }

        var mh = (MethodDefinitionHandle)h;
        var rid = MetadataTokens.GetRowNumber(mh);
        if (rid <= 0 || rid > module.MD.MethodDefinitions.Count)
        {
            return new ResolvedMethod(null, default, new AssemblyError(ErrorKinds.TokenOutOfRange,
                $"MethodDef row {rid} exceeds the module's table size ({module.MD.MethodDefinitions.Count})."));
        }

        return new ResolvedMethod(module, mh, null);
    }
}

internal readonly record struct MethodSpecDecodeResult(
    ModuleHandle? SpecModule,
    MethodSpecification? SpecRow,
    IReadOnlyList<string>? TypeRendered,
    IReadOnlyList<string>? MethodRendered,
    AssemblyError? Error);

internal readonly record struct ResolvedMethod(ModuleHandle? Module, MethodDefinitionHandle Handle, AssemblyError? Error);
