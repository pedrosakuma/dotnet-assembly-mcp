using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using HandleKind = System.Reflection.Metadata.HandleKind;
using static DotnetAssemblyMcp.Core.Metadata.MetadataResolver;

namespace DotnetAssemblyMcp.Core.Metadata.Resolvers;

/// <summary>
/// §3.5 Phase Ω(e) post-pass: walks a caller's IL looking for closed-generic call sites that
/// match the requested type / method instantiation. Pure static helpers extracted from
/// <see cref="MetadataIndex"/> in issue #92; behaviour preserved verbatim. The MemberRef→
/// CalleeKey matcher is also reused by <see cref="MethodResolver.MethodSpecTargetsMethodDef"/>
/// for spec target validation.
/// </summary>
internal static class CallerInstantiationMatcher
{
    /// <summary>
    /// Walks the caller's IL looking for any call site whose closed instantiation matches the
    /// requested <paramref name="expectedTypeArgs"/> and/or <paramref name="expectedMethodArgs"/>.
    /// Matches three shapes: (a) <c>MethodSpec</c> rows (method-level generics, optionally on a
    /// TypeSpec parent for type-level generics); (b) <c>MemberRef</c> rows with a TypeSpec
    /// parent (closed type-level instantiation, non-generic method); (c) intra-module
    /// <c>MethodDef</c> tokens — these never carry instantiation info, so they're skipped when
    /// either expected arg list is non-empty.
    /// </summary>
    public static bool CallerHasMatchingInstantiation(
        ModuleHandle callerModule, int callerToken,
        ModuleHandle calleeModule, MethodDefinitionHandle calleeHandle,
        CalleeKey calleeKey,
        IReadOnlyList<string>? expectedTypeArgs,
        IReadOnlyList<string>? expectedMethodArgs)
    {
        MethodDefinitionHandle callerHandle;
        try { callerHandle = (MethodDefinitionHandle)MetadataTokens.Handle(callerToken); }
        catch (ArgumentOutOfRangeException) { return false; }
        catch (InvalidCastException) { return false; }

        MethodDefinition callerDef;
        try { callerDef = callerModule.MD.GetMethodDefinition(callerHandle); }
        catch (BadImageFormatException) { return false; }
        if (callerDef.RelativeVirtualAddress == 0) return false;

        byte[] ilBytes;
        try
        {
            var body = callerModule.PE.GetMethodBody(callerDef.RelativeVirtualAddress);
            ilBytes = body.GetILBytes() ?? Array.Empty<byte>();
        }
        catch (BadImageFormatException) { return false; }

        var calleeIsSameModule = callerModule.Mvid == calleeModule.Mvid;
        var calleeIntraToken = MetadataTokens.GetToken(calleeHandle);
        bool wantMethodArgs = expectedMethodArgs is { Count: > 0 };
        bool wantTypeArgs = expectedTypeArgs is { Count: > 0 };

        var span = ilBytes.AsSpan();
        int pos = 0;
        var provider = new WireFormatSignatureProvider();
        while (pos < span.Length)
        {
            var b1 = span[pos++];
            IlOpcodeTable.Op op;
            if (b1 == 0xFE)
            {
                if (pos >= span.Length) break;
                op = IlOpcodeTable.TwoByteOp(span[pos++]);
            }
            else
            {
                op = IlOpcodeTable.OneByteOp(b1);
            }
            var size = IlOpcodeTable.OperandSize(op);
            if (size == -1)
            {
                if (pos + 4 > span.Length) break;
                var n = BitConverter.ToInt32(span.Slice(pos, 4));
                if (n < 0 || n > (span.Length - pos - 4) / 4) break;
                pos += 4 + n * 4;
                continue;
            }
            if (size == 4 && pos + 4 <= span.Length && op == IlOpcodeTable.Op.InlineMethod)
            {
                var token = BitConverter.ToInt32(span.Slice(pos, 4));
                if (TryMatchInstantiatedCall(
                        callerModule, token, calleeIsSameModule, calleeIntraToken, calleeKey,
                        expectedTypeArgs, expectedMethodArgs, wantTypeArgs, wantMethodArgs,
                        provider))
                {
                    return true;
                }
            }
            pos += Math.Max(0, size);
        }
        return false;
    }

    private static bool TryMatchInstantiatedCall(
        ModuleHandle callerModule, int token,
        bool calleeIsSameModule, int calleeIntraToken, CalleeKey calleeKey,
        IReadOnlyList<string>? expectedTypeArgs,
        IReadOnlyList<string>? expectedMethodArgs,
        bool wantTypeArgs, bool wantMethodArgs,
        WireFormatSignatureProvider provider)
    {
        EntityHandle h;
        try { h = (EntityHandle)MetadataTokens.Handle(token); }
        catch (ArgumentException) { return false; }

        switch (h.Kind)
        {
            case HandleKind.MethodSpecification:
                return TryMatchMethodSpecCall(
                    callerModule, (MethodSpecificationHandle)h,
                    calleeIsSameModule, calleeIntraToken, calleeKey,
                    expectedTypeArgs, expectedMethodArgs, wantTypeArgs, wantMethodArgs, provider);

            case HandleKind.MemberReference:
                // MemberRef-only path: matches when the caller wants only type-level generics
                // (no method-level instantiation) and the call site uses a TypeSpec parent.
                if (wantMethodArgs) return false;
                if (!wantTypeArgs) return false;
                return TryMatchMemberRefCall(
                    callerModule, (MemberReferenceHandle)h,
                    calleeKey, expectedTypeArgs!, provider);

            case HandleKind.MethodDefinition:
                // Intra-module MethodDef tokens carry no instantiation; skipped when either
                // expected arg list is non-empty.
                return false;

            default:
                return false;
        }
    }

    private static bool TryMatchMethodSpecCall(
        ModuleHandle callerModule, MethodSpecificationHandle handle,
        bool calleeIsSameModule, int calleeIntraToken, CalleeKey calleeKey,
        IReadOnlyList<string>? expectedTypeArgs,
        IReadOnlyList<string>? expectedMethodArgs,
        bool wantTypeArgs, bool wantMethodArgs,
        WireFormatSignatureProvider provider)
    {
        MethodSpecification spec;
        try { spec = callerModule.MD.GetMethodSpecification(handle); }
        catch (BadImageFormatException) { return false; }

        // Does spec.Method resolve to the callee?
        switch (spec.Method.Kind)
        {
            case HandleKind.MethodDefinition:
                if (!calleeIsSameModule) return false;
                if (MetadataTokens.GetToken(spec.Method) != calleeIntraToken) return false;
                if (wantTypeArgs) return false; // MethodDef parent has no closed type args
                break;
            case HandleKind.MemberReference:
                MemberReference mr;
                try { mr = callerModule.MD.GetMemberReference((MemberReferenceHandle)spec.Method); }
                catch (BadImageFormatException) { return false; }
                if (mr.GetKind() != MemberReferenceKind.Method) return false;
                if (!MemberRefMatchesCalleeKey(callerModule, mr, calleeKey)) return false;
                if (wantTypeArgs)
                {
                    if (mr.Parent.Kind != HandleKind.TypeSpecification) return false;
                    if (!TypeSpecMatchesTypeArgs(
                            callerModule, (TypeSpecificationHandle)mr.Parent,
                            expectedTypeArgs!, provider))
                        return false;
                }
                break;
            default:
                return false;
        }

        if (wantMethodArgs)
        {
            ImmutableArray<string> decoded;
            try { decoded = spec.DecodeSignature(provider, genericContext: (object?)null); }
            catch (BadImageFormatException) { return false; }
            if (decoded.Length != expectedMethodArgs!.Count) return false;
            for (int i = 0; i < decoded.Length; i++)
                if (!string.Equals(decoded[i], expectedMethodArgs[i], StringComparison.Ordinal))
                    return false;
        }

        return true;
    }

    private static bool TryMatchMemberRefCall(
        ModuleHandle callerModule, MemberReferenceHandle handle,
        CalleeKey calleeKey,
        IReadOnlyList<string> expectedTypeArgs,
        WireFormatSignatureProvider provider)
    {
        MemberReference mr;
        try { mr = callerModule.MD.GetMemberReference(handle); }
        catch (BadImageFormatException) { return false; }
        if (mr.GetKind() != MemberReferenceKind.Method) return false;
        if (!MemberRefMatchesCalleeKey(callerModule, mr, calleeKey)) return false;
        if (mr.Parent.Kind != HandleKind.TypeSpecification) return false;
        return TypeSpecMatchesTypeArgs(
            callerModule, (TypeSpecificationHandle)mr.Parent, expectedTypeArgs, provider);
    }

    private static bool TypeSpecMatchesTypeArgs(
        ModuleHandle callerModule, TypeSpecificationHandle handle,
        IReadOnlyList<string> expectedTypeArgs,
        WireFormatSignatureProvider provider)
    {
        try
        {
            var ts = callerModule.MD.GetTypeSpecification(handle);
            var decoded = ts.DecodeSignature(provider, genericContext: (object?)null);
            if (!GenericTypeName.TryParse(decoded, out var node, out _, out _)) return false;
            if (node is not GenericTypeName.Named named) return false;
            if (named.TypeArguments.IsDefaultOrEmpty) return false;
            if (named.TypeArguments.Length != expectedTypeArgs.Count) return false;
            for (int i = 0; i < expectedTypeArgs.Count; i++)
            {
                var formatted = named.TypeArguments[i].Format();
                if (!string.Equals(formatted, expectedTypeArgs[i], StringComparison.Ordinal))
                    return false;
            }
            return true;
        }
        catch (BadImageFormatException) { return false; }
    }

    /// <summary>
    /// Compares a MemberRef row against a callee identity key: assembly name, type full name,
    /// method name, calling convention, parameter count, generic arity, and parameter signature
    /// (decoded with <see cref="StringSignatureProvider"/>). This is the cross-module xref
    /// invariant used by <see cref="FieldAccessIndex"/>-style lookups and §3.5 spec-target
    /// validation alike — the typeFullName must include the nested-type chain (<c>Outer+Inner</c>).
    /// </summary>
    public static bool MemberRefMatchesCalleeKey(ModuleHandle callerModule, MemberReference mr, CalleeKey key)
    {
        try
        {
            var typeName = ResolveOutboundTypeName(callerModule, mr.Parent, out var assemblyName);
            if (typeName is null || assemblyName is null) return false;
            if (!string.Equals(assemblyName, key.AssemblyName, StringComparison.Ordinal)) return false;
            if (!string.Equals(typeName, key.TypeFullName, StringComparison.Ordinal)) return false;
            var methodName = callerModule.MD.GetString(mr.Name);
            if (!string.Equals(methodName, key.MethodName, StringComparison.Ordinal)) return false;
            var decoder = new SignatureDecoder<string, object?>(
                new StringSignatureProvider(callerModule.MD), callerModule.MD, genericContext: null);
            var blob = callerModule.MD.GetBlobReader(mr.Signature);
            var sig = decoder.DecodeMethodSignature(ref blob);
            if (sig.Header.RawValue != key.CallingConvention) return false;
            if (sig.RequiredParameterCount != key.ParameterCount) return false;
            if (sig.GenericParameterCount != key.GenericArity) return false;
            var paramSig = sig.RequiredParameterCount == sig.ParameterTypes.Length
                ? string.Join(",", sig.ParameterTypes)
                : string.Join(",", sig.ParameterTypes.Take(sig.RequiredParameterCount));
            return string.Equals(paramSig, key.ParameterSignature, StringComparison.Ordinal);
        }
        catch (BadImageFormatException) { return false; }
    }
}
