using System.ComponentModel;
using DotnetAssemblyMcp.Core;
using DotnetAssemblyMcp.Core.Decompilation;
using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Handles;
using DotnetAssemblyMcp.Core.Identity;
using DotnetAssemblyMcp.Core.Metadata;
using ModelContextProtocol.Server;

namespace DotnetAssemblyMcp.Server.Tools;

public sealed partial class AssemblyTools
{
    private static bool TryParseAttributeTarget(string target, out AttributeTarget parsed, out AssemblyError? error)
    {
        parsed = null!;
        if (string.IsNullOrWhiteSpace(target))
        {
            error = new AssemblyError(ErrorKinds.InvalidArgument, "target is required.");
            return false;
        }
        if (!HandleSyntax.TryParseAny(target, out var kind, out var mvid, out var token, out var sequence))
        {
            error = new AssemblyError(ErrorKinds.InvalidArgument,
                $"could not parse '{target}'. Expected one of: 'a:<mvid>', 't:<mvid>:0x<token>', "
                + "'m:<mvid>:0x<token>', 'pa:<mvid>:0x<methodToken>:<sequence>', 'f:<mvid>:0x<token>', "
                + "'p:<mvid>:0x<token>', 'e:<mvid>:0x<token>'.");
            return false;
        }
        parsed = kind switch
        {
            HandleKind.Assembly => AttributeTarget.Assembly(mvid),
            HandleKind.Type => AttributeTarget.Type(mvid, token),
            HandleKind.Method => AttributeTarget.Method(mvid, token),
            HandleKind.Parameter => AttributeTarget.Parameter(mvid, token, sequence),
            HandleKind.Field => AttributeTarget.Field(mvid, token),
            HandleKind.Property => AttributeTarget.Property(mvid, token),
            HandleKind.Event => AttributeTarget.Event(mvid, token),
            _ => null!,
        };
        if (parsed is null)
        {
            error = new AssemblyError(ErrorKinds.InvalidArgument, $"unsupported handle kind '{kind}' in '{target}'.");
            return false;
        }
        error = null;
        return true;
    }


    private static bool TryParseIdentity(string moduleVersionId, string? metadataToken,
        out MethodIdentity identity, out AssemblyError? error)
    {
        identity = default!;
        if (!TryResolveMethodTokens(moduleVersionId, metadataToken, out var mvid, out var token, out error))
            return false;
        identity = new MethodIdentity(mvid, token);
        return true;
    }

    /// <summary>
    /// Resolves the <c>(mvid, token)</c> pair for a method-addressed tool. <paramref name="moduleVersionId"/>
    /// accepts EITHER the canonical handoff anchor — a bare MVID GUID, in which case
    /// <paramref name="metadataToken"/> is required — OR (intra-server convenience) a full method handle
    /// <c>m:&lt;mvid&gt;:0x&lt;token&gt;</c> as emitted by <c>list_methods</c> / <c>find_method</c>, in which
    /// case <paramref name="metadataToken"/> may be omitted and, if supplied, is cross-checked.
    /// </summary>
    private static bool TryResolveMethodTokens(string moduleVersionId, string? metadataToken,
        out Guid mvid, out int token, out AssemblyError? error)
        => TryResolveTokens(moduleVersionId, metadataToken, HandleKind.Method, out mvid, out token, out error);

    /// <summary>
    /// Type-addressed analogue of <see cref="TryResolveMethodTokens"/>: accepts a bare MVID GUID
    /// (then <paramref name="metadataToken"/> — a TypeDef token — is required) or a full type handle
    /// <c>t:&lt;mvid&gt;:0x&lt;typeToken&gt;</c> as emitted by <c>list_types</c> / <c>get_type</c>.
    /// </summary>
    private static bool TryResolveTypeTokens(string moduleVersionId, string? metadataToken,
        out Guid mvid, out int token, out AssemblyError? error)
        => TryResolveTokens(moduleVersionId, metadataToken, HandleKind.Type, out mvid, out token, out error);

    private static readonly string[] HandlePrefixes = { "pa:", "a:", "t:", "m:", "f:", "p:", "e:" };

    private static bool TryResolveTokens(string moduleVersionId, string? metadataToken,
        HandleKind expectedKind, out Guid mvid, out int token, out AssemblyError? error)
    {
        mvid = Guid.Empty;
        token = 0;

        var handleForm = expectedKind == HandleKind.Type
            ? "t:<mvid>:0x<typeToken>"
            : "m:<mvid>:0x<methodToken>";

        if (string.IsNullOrWhiteSpace(moduleVersionId))
        {
            error = new AssemblyError(ErrorKinds.InvalidArgument, "moduleVersionId is required.");
            return false;
        }

        // Intra-server convenience: a full handle carries both anchors in one string.
        if (HandleSyntax.TryParseAny(moduleVersionId, out var kind, out mvid, out token, out _))
        {
            if (kind != expectedKind)
            {
                error = new AssemblyError(ErrorKinds.InvalidArgument,
                    $"moduleVersionId '{moduleVersionId}' is a {kind} handle; expected a GUID or '{handleForm}'.");
                return false;
            }
            if (!string.IsNullOrWhiteSpace(metadataToken))
            {
                if (!TryParseToken(metadataToken!, out var explicitToken))
                {
                    error = new AssemblyError(ErrorKinds.InvalidArgument,
                        $"could not parse metadataToken '{metadataToken}' as a 32-bit metadata token.");
                    return false;
                }
                if (explicitToken != token)
                {
                    error = new AssemblyError(ErrorKinds.InvalidArgument,
                        $"moduleVersionId handle token 0x{token:X8} does not match metadataToken 0x{explicitToken:X8}.");
                    return false;
                }
            }
            error = null;
            return true;
        }

        // Looks like a (malformed or wrong-kind) handle but did not parse — give a targeted message
        // instead of the confusing "not a valid GUID" fallthrough.
        if (HandlePrefixes.Any(p => moduleVersionId.AsSpan().TrimStart().StartsWith(p, StringComparison.Ordinal)))
        {
            error = new AssemblyError(ErrorKinds.InvalidArgument,
                $"could not parse moduleVersionId '{moduleVersionId}' as a handle. Expected a GUID or '{handleForm}'.");
            return false;
        }

        // Canonical handoff pair: GUID + required token.
        if (!Guid.TryParse(moduleVersionId, out mvid))
        {
            error = new AssemblyError(ErrorKinds.InvalidArgument,
                $"could not parse '{moduleVersionId}' as a GUID or '{handleForm}' handle.");
            return false;
        }
        if (string.IsNullOrWhiteSpace(metadataToken))
        {
            error = new AssemblyError(ErrorKinds.IdentityMalformed,
                $"metadataToken is required when moduleVersionId is a GUID; pass metadataToken or use a full '{handleForm}' handle.");
            return false;
        }
        if (!TryParseToken(metadataToken!, out token))
        {
            error = new AssemblyError(ErrorKinds.InvalidArgument,
                $"could not parse metadataToken '{metadataToken}' as a 32-bit metadata token.");
            return false;
        }
        error = null;
        return true;
    }


    /// <summary>
    /// Parses an array of canonical CLR-style type names (see <c>docs/handoff-contract.md §3.5</c>)
    /// into <see cref="GenericTypeName"/> nodes for forwarding through <see cref="MethodIdentity"/>.
    /// Returns <c>true</c> with a non-null list (possibly empty) on success, or <c>false</c> with
    /// the first parser error and a null list. Null/empty input yields <c>(true, null)</c> so the
    /// caller can distinguish "absent" from "empty".
    /// </summary>
    private static bool TryParseGenericArgs(string[]? raw, string paramName,
        out IReadOnlyList<GenericTypeName>? parsed, out AssemblyError? error)
    {
        parsed = null;
        error = null;
        if (raw is null || raw.Length == 0) return true;
        var list = new List<GenericTypeName>(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            if (!GenericTypeName.TryParse(raw[i], out var node, out var kind, out var msg))
            {
                error = new AssemblyError(kind ?? ErrorKinds.InvalidArgument,
                    $"{paramName}[{i}] is invalid: {msg}");
                return false;
            }
            list.Add(node!);
        }
        parsed = list;
        return true;
    }

    private static bool TryParseMethodSpec(string? mvidStr, string? tokenStr,
        out MethodSpecHandle? spec, out AssemblyError? error)
    {
        spec = null;
        error = null;
        bool hasMvid = !string.IsNullOrWhiteSpace(mvidStr);
        bool hasToken = !string.IsNullOrWhiteSpace(tokenStr);
        if (!hasMvid && !hasToken) return true;
        if (hasMvid != hasToken)
        {
            error = new AssemblyError(ErrorKinds.InvalidArgument,
                "methodSpecModuleVersionId and methodSpecMetadataToken must be supplied together.");
            return false;
        }
        if (!Guid.TryParse(mvidStr, out var mvid))
        {
            error = new AssemblyError(ErrorKinds.InvalidArgument,
                $"could not parse methodSpecModuleVersionId '{mvidStr}' as a GUID.");
            return false;
        }
        if (!TryParseToken(tokenStr!, out var token))
        {
            error = new AssemblyError(ErrorKinds.InvalidArgument,
                $"could not parse methodSpecMetadataToken '{tokenStr}' as a 32-bit metadata token.");
            return false;
        }
        spec = new MethodSpecHandle(mvid, token);
        return true;
    }

    private static bool TryResolveModuleId(IMetadataIndex index, string mvidOrPath,
        out Guid mvid, out AssemblyError? error)
    {
        mvid = Guid.Empty;
        if (string.IsNullOrWhiteSpace(mvidOrPath))
        {
            error = new AssemblyError(ErrorKinds.InvalidArgument, "mvidOrPath is required.");
            return false;
        }
        if (Guid.TryParse(mvidOrPath, out var parsed))
        {
            mvid = parsed;
            error = null;
            return true;
        }
        // Treat as path — auto-load (idempotent if MVID already known).
        var load = index.Load(mvidOrPath);
        if (!load.IsSuccess)
        {
            error = load.Error;
            return false;
        }
        mvid = load.Module!.ModuleVersionId;
        error = null;
        return true;
    }

    private static bool TryResolveTypeIdentity(IMetadataIndex index, string? typeHandle,
        string? mvidOrPath, string? typeFullName,
        out Guid mvid, out int typeToken, out AssemblyError? error)
    {
        mvid = Guid.Empty;
        typeToken = 0;

        if (!string.IsNullOrWhiteSpace(typeHandle))
        {
            if (!HandleSyntax.TryParseType(typeHandle!, out mvid, out typeToken))
            {
                error = new AssemblyError(ErrorKinds.InvalidArgument,
                    $"could not parse typeHandle '{typeHandle}'. Expected 't:<mvid>:0x<typeToken>'.");
                return false;
            }
            error = null;
            return true;
        }

        if (string.IsNullOrWhiteSpace(mvidOrPath) || string.IsNullOrWhiteSpace(typeFullName))
        {
            error = new AssemblyError(ErrorKinds.InvalidArgument,
                "either typeHandle, or both mvidOrPath and typeFullName, are required.");
            return false;
        }

        if (!TryResolveModuleId(index, mvidOrPath, out mvid, out var modErr))
        {
            error = modErr;
            return false;
        }

        var find = index.FindTypeByFullName(mvid, typeFullName!);
        if (!find.IsSuccess)
        {
            error = find.Error;
            return false;
        }
        typeToken = find.Type!.MetadataToken;
        error = null;
        return true;
    }

    private static bool TryParseToken(string raw, out int token) => HandleSyntax.TryParseToken(raw, out token);
}
