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
    public FindStringReferencesReadResult FindStringReferences(
        string query,
        StringMatchMode matchMode,
        Guid moduleVersionIdFilter = default,
        int maxHits = 0,
        CancellationToken cancellationToken = default)
        => _stringIndex.FindStringReferences(query, matchMode, moduleVersionIdFilter, maxHits, cancellationToken);
    /// <inheritdoc />
    public FindAttributeTargetsReadResult FindAttributeTargets(
        string attributeTypeFullName,
        Guid moduleVersionIdFilter = default,
        IReadOnlyCollection<AttributeTargetKind>? targetKindsFilter = null,
        int maxHits = 0,
        CancellationToken cancellationToken = default)
        => _attributeIndex.FindAttributeTargets(attributeTypeFullName, moduleVersionIdFilter, targetKindsFilter, maxHits, cancellationToken);
    /// <inheritdoc />
    public FindFieldReferencesReadResult FindFieldReferences(
        Guid moduleVersionId,
        int fieldMetadataToken,
        FieldAccessMode mode = FieldAccessMode.All,
        int maxHits = 0,
        CancellationToken cancellationToken = default)
        => _fieldAccessIndex.FindFieldReferences(moduleVersionId, fieldMetadataToken, mode, maxHits, cancellationToken);
    /// <inheritdoc />
    public FindPropertyReferencesReadResult FindPropertyReferences(
        Guid moduleVersionId,
        int propertyMetadataToken,
        PropertyAccessorFilter accessor = PropertyAccessorFilter.All,
        int maxHits = 0,
        CancellationToken cancellationToken = default)
        => _fieldAccessIndex.FindPropertyReferences(moduleVersionId, propertyMetadataToken, accessor, maxHits, cancellationToken);
    /// <inheritdoc />
    public FindEventReferencesReadResult FindEventReferences(
        Guid moduleVersionId,
        int eventMetadataToken,
        EventAccessorFilter accessor = EventAccessorFilter.All,
        int maxHits = 0,
        CancellationToken cancellationToken = default)
        => _fieldAccessIndex.FindEventReferences(moduleVersionId, eventMetadataToken, accessor, maxHits, cancellationToken);
    /// <inheritdoc />
    public FindTypeReferencesReadResult FindTypeReferences(Guid moduleVersionId, int typeMetadataToken, CancellationToken cancellationToken = default)
    {
        if (moduleVersionId == Guid.Empty)
            return FindTypeReferencesReadResult.Fail(new AssemblyError(ErrorKinds.IdentityMalformed, "moduleVersionId is required."));
        if (!_store.TryGet(moduleVersionId, out var module))
            return FindTypeReferencesReadResult.Fail(new AssemblyError(ErrorKinds.ModuleNotFound,
                $"no loaded module has MVID {moduleVersionId:D}."));

        EntityHandle targetHandle;
        try { targetHandle = (EntityHandle)MetadataTokens.Handle(typeMetadataToken); }
        catch (ArgumentException ex)
        {
            return FindTypeReferencesReadResult.Fail(new AssemblyError(ErrorKinds.IdentityMalformed,
                $"could not interpret token 0x{typeMetadataToken:X8} as a metadata handle.", ex.Message));
        }
        if (targetHandle.Kind != HandleKind.TypeDefinition)
            return FindTypeReferencesReadResult.Fail(new AssemblyError(ErrorKinds.TokenWrongTable,
                $"token 0x{typeMetadataToken:X8} is in table {targetHandle.Kind}, expected TypeDefinition (0x02)."));

        var targetRow = MetadataTokens.GetRowNumber((TypeDefinitionHandle)targetHandle);
        if (targetRow <= 0 || targetRow > module.MD.TypeDefinitions.Count)
            return FindTypeReferencesReadResult.Fail(new AssemblyError(ErrorKinds.TokenOutOfRange,
                $"TypeDef token 0x{typeMetadataToken:X8} is not present in this module."));

        // Resolve target identity for cross-module matching once.
        var targetDef = module.MD.GetTypeDefinition((TypeDefinitionHandle)targetHandle);
        var targetFullName = TypeName(module, targetDef);
        var targetAssemblyName = module.MD.IsAssembly
            ? module.MD.GetString(module.MD.GetAssemblyDefinition().Name)
            : null;

        var fromCache = true;
        XrefData xref;
        try
        {
            xref = _xrefIndex.LoadOrBuildXref(module, ref fromCache, cancellationToken);
        }
        catch (ModuleTooLargeException ex)
        {
            return FindTypeReferencesReadResult.Fail(new AssemblyError(
                ErrorKinds.ModuleTooLarge,
                "xref index for the target's module would exceed the per-module budget.",
                ex.Message));
        }

        var references = new List<TypeReferenceRef>();

        // Intra-module sites.
        if (xref.TypeIntra.TryGetValue(typeMetadataToken, out var localSites))
        {
            foreach (var site in localSites)
                references.Add(RenderTypeReferenceSite(module, site));
        }

        // Cross-module sites: probe every other loaded module's outbound type-refs.
        var modulesSearched = 1;
        List<Guid>? skipped = null;
        if (targetAssemblyName is not null)
        {
            foreach (var other in _store.Modules)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (other.Mvid == module.Mvid) continue;
                modulesSearched++;

                XrefData otherXref;
                try
                {
                    otherXref = _xrefIndex.LoadOrBuildXref(other, cancellationToken);
                }
                catch (ModuleTooLargeException)
                {
                    (skipped ??= new List<Guid>()).Add(other.Mvid);
                    continue;
                }
                foreach (var entry in otherXref.TypeOutbound)
                {
                    if (!string.Equals(entry.TargetAssemblyName, targetAssemblyName, StringComparison.Ordinal)) continue;
                    if (!string.Equals(entry.TargetTypeFullName, targetFullName, StringComparison.Ordinal)) continue;
                    references.Add(RenderTypeReferenceSite(other,
                        new TypeReferenceSite(entry.SiteToken, entry.SiteKind, entry.ReferenceKind)));
                }
            }
        }

        var targetHandleStr = HandleSyntax.FormatType(module.Mvid, typeMetadataToken);
        return FindTypeReferencesReadResult.Ok(new FindTypeReferencesResult(
            module.Mvid, typeMetadataToken, targetHandleStr,
            references, modulesSearched, FromCache: fromCache,
            SkippedOverBudgetModules: skipped));
    }
    private static TypeReferenceRef RenderTypeReferenceSite(ModuleHandle module, TypeReferenceSite site)
    {
        string handle;
        string display;
        switch (site.SiteKind)
        {
            case MemberKind.Method:
            {
                var mh = (MethodDefinitionHandle)MetadataTokens.Handle(site.SiteToken);
                handle = HandleSyntax.FormatMethod(module.Mvid, site.SiteToken);
                display = RenderMethodDef(module, mh);
                break;
            }
            case MemberKind.Field:
            {
                handle = HandleSyntax.FormatField(module.Mvid, site.SiteToken);
                display = RenderFieldDef(module, (FieldDefinitionHandle)MetadataTokens.Handle(site.SiteToken));
                break;
            }
            case MemberKind.Property:
            {
                handle = HandleSyntax.FormatProperty(module.Mvid, site.SiteToken);
                display = RenderPropertyDef(module, (PropertyDefinitionHandle)MetadataTokens.Handle(site.SiteToken));
                break;
            }
            case MemberKind.Event:
            {
                handle = HandleSyntax.FormatEvent(module.Mvid, site.SiteToken);
                display = RenderEventDef(module, (EventDefinitionHandle)MetadataTokens.Handle(site.SiteToken));
                break;
            }
            case MemberKind.Type:
            {
                handle = HandleSyntax.FormatType(module.Mvid, site.SiteToken);
                try
                {
                    var tdh = (TypeDefinitionHandle)MetadataTokens.Handle(site.SiteToken);
                    var td = module.MD.GetTypeDefinition(tdh);
                    display = TypeName(module, td);
                }
                catch (BadImageFormatException) { display = $"<type 0x{site.SiteToken:X8}>"; }
                break;
            }
            default:
                handle = $"?:{module.Mvid:D}:0x{site.SiteToken:X8}";
                display = $"<unknown site kind {site.SiteKind}>";
                break;
        }
        return new TypeReferenceRef(module.Mvid, site.SiteToken, site.SiteKind, site.ReferenceKind, handle, display);
    }
}
