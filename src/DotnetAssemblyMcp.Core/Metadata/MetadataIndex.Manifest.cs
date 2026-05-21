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
    public ListAssemblyReferencesResult ListAssemblyReferences(Guid moduleVersionId)
    {
        if (moduleVersionId == Guid.Empty)
            return ListAssemblyReferencesResult.Fail(new AssemblyError(ErrorKinds.IdentityMalformed, "moduleVersionId is required."));
        if (!_store.TryGet(moduleVersionId, out var module))
            return ListAssemblyReferencesResult.Fail(new AssemblyError(ErrorKinds.ModuleNotFound,
                $"no loaded module has MVID {moduleVersionId:D}."));

        var refs = new List<AssemblyReferenceSummary>(module.MD.AssemblyReferences.Count);
        foreach (var arh in module.MD.AssemblyReferences)
        {
            try
            {
                var ar = module.MD.GetAssemblyReference(arh);
                var name = module.MD.GetString(ar.Name);
                var culture = ar.Culture.IsNil ? null : module.MD.GetString(ar.Culture);
                if (string.IsNullOrEmpty(culture)) culture = null;
                string? pkt = null;
                if (!ar.PublicKeyOrToken.IsNil)
                {
                    var bytes = module.MD.GetBlobBytes(ar.PublicKeyOrToken);
                    if (bytes.Length > 0) pkt = Convert.ToHexString(bytes).ToLowerInvariant();
                }
                var token = MetadataTokens.GetToken(arh);
                refs.Add(new AssemblyReferenceSummary(
                    MetadataToken: token,
                    // Issue #80: canonical 'a:<mvid>' handle of the containing module — the
                    // previous ad-hoc 'a:<mvid>:0x<token>' was not parseable by
                    // HandleSyntax.TryParseAssembly. The row id stays in MetadataToken.
                    Handle: HandleSyntax.FormatAssembly(module.Mvid),
                    Name: name,
                    Version: ar.Version.ToString(),
                    Culture: culture,
                    PublicKeyTokenHex: pkt,
                    Flags: (int)ar.Flags));
            }
            catch (BadImageFormatException) { /* skip malformed row */ }
        }

        return ListAssemblyReferencesResult.Ok(new ListAssemblyReferencesPage(module.Mvid, refs));
    }
    /// <inheritdoc />
    public ListResourcesResult ListResources(Guid moduleVersionId)
    {
        if (moduleVersionId == Guid.Empty)
            return ListResourcesResult.Fail(new AssemblyError(ErrorKinds.IdentityMalformed, "moduleVersionId is required."));
        if (!_store.TryGet(moduleVersionId, out var module))
            return ListResourcesResult.Fail(new AssemblyError(ErrorKinds.ModuleNotFound,
                $"no loaded module has MVID {moduleVersionId:D}."));

        var md = module.MD;
        var list = new List<ResourceSummary>(md.ManifestResources.Count);

        // Resolve the .mresources section once so per-resource length decoding is O(1).
        var pe = module.PE;
        var resourceDir = pe.PEHeaders.CorHeader?.ResourcesDirectory;
        // GetSectionData over a default RVA returns an empty PEMemoryBlock which is safe to slice.
        var resBlock = (resourceDir is { RelativeVirtualAddress: > 0 })
            ? pe.GetSectionData(resourceDir.Value.RelativeVirtualAddress)
            : default;
        // GetSectionData runs to the end of the containing PE section — typically far beyond the
        // CLI resources data directory. Use Size as the real upper bound, clamped to the actual
        // block length in case Size is malformed.
        int resLimit = resourceDir is { Size: > 0 }
            ? Math.Min(resBlock.Length, resourceDir.Value.Size)
            : 0;

        foreach (var mrh in md.ManifestResources)
        {
            try
            {
                var mr = md.GetManifestResource(mrh);
                var name = md.GetString(mr.Name);
                var token = MetadataTokens.GetToken(mrh);
                var isPublic = (mr.Attributes & ManifestResourceAttributes.Public) != 0;

                if (mr.Implementation.IsNil)
                {
                    // In-PE resource: decode the 4-byte little-endian length prefix at the offset.
                    // All bounds arithmetic in long to defeat malformed offsets near int.MaxValue
                    // (offset+4 evaluated as int would overflow negative and slip past the check).
                    int? length = null;
                    long off = mr.Offset;
                    if (resLimit > 0 && off >= 0 && off + 4L <= resLimit)
                    {
                        int offset = (int)off;
                        var reader = resBlock.GetReader(offset, 4);
                        uint declared = reader.ReadUInt32();
                        // Sanity: the declared payload must fit inside the resources directory.
                        long endExclusive = (long)offset + 4L + declared;
                        if (declared <= int.MaxValue && endExclusive <= resLimit)
                            length = (int)declared;
                    }

                    list.Add(new ResourceSummary(
                        MetadataToken: token,
                        Name: name,
                        IsPublic: isPublic,
                        Implementation: ResourceImplementationKind.InPe,
                        Offset: mr.Offset,
                        Length: length));
                }
                else if (mr.Implementation.Kind == HandleKind.AssemblyFile)
                {
                    var fileHandle = (AssemblyFileHandle)mr.Implementation;
                    var file = md.GetAssemblyFile(fileHandle);
                    list.Add(new ResourceSummary(
                        MetadataToken: token,
                        Name: name,
                        IsPublic: isPublic,
                        Implementation: ResourceImplementationKind.LinkedFile,
                        LinkedFileName: md.GetString(file.Name)));
                }
                else if (mr.Implementation.Kind == HandleKind.AssemblyReference)
                {
                    var refHandle = (AssemblyReferenceHandle)mr.Implementation;
                    var asmRef = md.GetAssemblyReference(refHandle);
                    list.Add(new ResourceSummary(
                        MetadataToken: token,
                        Name: name,
                        IsPublic: isPublic,
                        Implementation: ResourceImplementationKind.ForwardedToAssembly,
                        LinkedAssemblyName: md.GetString(asmRef.Name)));
                }
                // Other Implementation kinds are not defined by ECMA-335 §22.24 — skip silently.
            }
            catch (BadImageFormatException) { /* skip malformed row */ }
        }

        return ListResourcesResult.Ok(new ListResourcesPage(module.Mvid, list));
    }
}
