using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using DotnetAssemblyMcp.Core.Errors;
using DotnetAssemblyMcp.Core.Identity;

namespace DotnetAssemblyMcp.Core.Metadata;

/// <summary>
/// <see cref="IMetadataIndex"/> backed by <see cref="PEReader"/> / <see cref="MetadataReader"/>
/// (System.Reflection.Metadata). Library chosen via spike #2 — see
/// <c>docs/handoff-contract.md §8.1</c> for rationale.
/// </summary>
/// <remarks>
/// When constructed with <c>watchForChanges: true</c> the index installs a
/// <see cref="FileSystemWatcher"/> per loaded directory and re-reads the MVID on file
/// updates. A debounce window (<see cref="WatchDebounce"/>) coalesces rapid writes from
/// build tools. The watcher is opt-in so unit tests stay deterministic.
/// </remarks>
public sealed class MetadataIndex : IMetadataIndex, IDisposable
{
    /// <summary>Debounce window applied to <see cref="FileSystemWatcher"/> events.</summary>
    public static readonly TimeSpan WatchDebounce = TimeSpan.FromMilliseconds(250);

    private readonly ConcurrentDictionary<Guid, Module> _modules = new();
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _pendingReloads =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _watch;
    private int _disposed;

    /// <summary>Raised after a watched file change has been processed (success or failure).</summary>
    public event EventHandler<ModuleReloadedEventArgs>? ModuleReloaded;

    /// <summary>Creates an index without filesystem watching (default).</summary>
    public MetadataIndex() : this(watchForChanges: false) { }

    /// <summary>Creates an index, optionally installing per-directory file watchers.</summary>
    /// <param name="watchForChanges">When true, reloads modules on disk changes and invalidates the old MVID.</param>
    public MetadataIndex(bool watchForChanges) => _watch = watchForChanges;

    /// <inheritdoc />
    public LoadResult Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return LoadResult.Fail(new AssemblyError(ErrorKinds.InvalidArgument, "path is required."));
        if (!File.Exists(path))
            return LoadResult.Fail(new AssemblyError(ErrorKinds.ModuleLoadFailed, $"file not found: {path}"));

        var fullPath = Path.GetFullPath(path);
        var loaded = OpenAndRegister(fullPath);
        if (!loaded.IsSuccess) return loaded;

        if (_watch) EnsureWatcher(fullPath);
        return loaded;
    }

    private LoadResult OpenAndRegister(string fullPath)
    {
        try
        {
            // Read the bytes once and back the PEReader with a MemoryStream so the file on disk
            // stays unlocked. Required for the Tier-1 watcher to be able to observe rewrites on
            // Windows, where File.Move(overwrite: true) needs the destination to be free of
            // open writable handles. Per the spike, fixture-sized assemblies cost ~tens of KB
            // resident — well within the Tier-1 budget.
            var bytes = File.ReadAllBytes(fullPath);
            var pe = new PEReader(new MemoryStream(bytes, writable: false));
            if (!pe.HasMetadata)
            {
                pe.Dispose();
                return LoadResult.Fail(new AssemblyError(ErrorKinds.ModuleLoadFailed, $"not a managed PE: {fullPath}"));
            }
            var md = pe.GetMetadataReader();
            var mvid = md.GetGuid(md.GetModuleDefinition().Mvid);

            var added = _modules.GetOrAdd(mvid, _ => new Module(mvid, fullPath, pe, md));
            if (!ReferenceEquals(added.PE, pe))
            {
                // Lost a race; another thread loaded the same MVID first. Dispose our duplicate.
                pe.Dispose();
            }
            return LoadResult.Ok(SummarizeModule(added));
        }
        catch (BadImageFormatException ex)
        {
            return LoadResult.Fail(new AssemblyError(ErrorKinds.ModuleLoadFailed, "invalid PE/CLI image.", ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            return LoadResult.Fail(new AssemblyError(ErrorKinds.ModuleLoadFailed, "permission denied.", ex.Message));
        }
        catch (IOException ex)
        {
            return LoadResult.Fail(new AssemblyError(ErrorKinds.ModuleLoadFailed, "i/o error opening assembly.", ex.Message));
        }
    }

    private void EnsureWatcher(string fullPath)
    {
        var dir = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(dir)) return;
        _watchers.GetOrAdd(dir, d =>
        {
            var w = new FileSystemWatcher(d)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime
                               | NotifyFilters.FileName,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };
            w.Changed += OnWatcherEvent;
            w.Created += OnWatcherEvent;
            w.Renamed += OnWatcherRenamed;
            return w;
        });
    }

    private void OnWatcherEvent(object sender, FileSystemEventArgs e) => ScheduleReload(e.FullPath);
    private void OnWatcherRenamed(object sender, RenamedEventArgs e) => ScheduleReload(e.FullPath);

    private void ScheduleReload(string fullPath)
    {
        if (_disposed != 0) return;
        // Only react to paths we actually loaded. Avoids storms on bin/obj rebuilds.
        if (!_modules.Values.Any(m => string.Equals(m.Path, fullPath, StringComparison.OrdinalIgnoreCase)))
            return;

        var now = DateTime.UtcNow;
        _pendingReloads[fullPath] = now;
        _ = Task.Delay(WatchDebounce).ContinueWith(_ => TryReload(fullPath, now), TaskScheduler.Default);
    }

    private void TryReload(string fullPath, DateTime scheduledAt)
    {
        if (_disposed != 0) return;
        // Drop stale debounce timers — only the most recent scheduling wins.
        if (!_pendingReloads.TryGetValue(fullPath, out var latest) || latest != scheduledAt) return;
        _pendingReloads.TryRemove(fullPath, out _);

        var oldEntry = _modules.Values
            .FirstOrDefault(m => string.Equals(m.Path, fullPath, StringComparison.OrdinalIgnoreCase));
        var oldMvid = oldEntry?.Mvid;

        // Tolerate transient ShareViolation/Empty mid-write by skipping; the next event will retry.
        if (!File.Exists(fullPath)) return;

        var result = OpenAndRegister(fullPath);
        if (!result.IsSuccess)
        {
            ModuleReloaded?.Invoke(this, new ModuleReloadedEventArgs(fullPath, oldMvid, null, result.Error));
            return;
        }

        var newMvid = result.Module!.ModuleVersionId;
        if (oldMvid is { } prev && prev != newMvid && _modules.TryRemove(prev, out var stale))
        {
            stale.PE.Dispose();
        }

        ModuleReloaded?.Invoke(this, new ModuleReloadedEventArgs(fullPath, oldMvid, newMvid, null));
    }

    /// <inheritdoc />
    public IReadOnlyList<ModuleSummary> List()
    {
        var list = new List<ModuleSummary>(_modules.Count);
        foreach (var m in _modules.Values)
            list.Add(SummarizeModule(m));
        return list;
    }

    /// <inheritdoc />
    public ResolveResult Resolve(MethodIdentity identity)
    {
        if (identity.ModuleVersionId == Guid.Empty)
            return ResolveResult.Fail(new AssemblyError(ErrorKinds.IdentityMalformed, "moduleVersionId is required."));
        if (identity.MetadataToken == 0)
            return ResolveResult.Fail(new AssemblyError(ErrorKinds.IdentityMalformed, "metadataToken is required."));

        if (!_modules.TryGetValue(identity.ModuleVersionId, out var module))
        {
            return ResolveResult.Fail(new AssemblyError(
                ErrorKinds.ModuleNotFound,
                $"no loaded module has MVID {identity.ModuleVersionId}.",
                "call load_assembly with the path to the assembly first, or list_assemblies to see what is loaded."));
        }

        var handle = MetadataTokens.Handle(identity.MetadataToken);
        if (handle.Kind != HandleKind.MethodDefinition)
        {
            return ResolveResult.Fail(new AssemblyError(
                ErrorKinds.TokenWrongTable,
                $"metadataToken 0x{identity.MetadataToken:X8} is a {handle.Kind}, expected MethodDefinition (table 0x06)."));
        }

        var methodHandle = (MethodDefinitionHandle)handle;
        var rid = MetadataTokens.GetRowNumber(methodHandle);
        if (rid <= 0 || rid > module.MD.MethodDefinitions.Count)
        {
            return ResolveResult.Fail(new AssemblyError(
                ErrorKinds.TokenOutOfRange,
                $"MethodDef row {rid} exceeds the module's table size ({module.MD.MethodDefinitions.Count})."));
        }

        var summary = SummarizeMethod(module, methodHandle, identity.MetadataToken);
        return ResolveResult.Ok(summary);
    }

    private static ModuleSummary SummarizeModule(Module m) =>
        new(m.Mvid, Path.GetFileName(m.Path), m.Path, m.MD.MethodDefinitions.Count);

    private static MethodSummary SummarizeMethod(Module m, MethodDefinitionHandle h, int token)
    {
        var def = m.MD.GetMethodDefinition(h);
        var typeDef = m.MD.GetTypeDefinition(def.GetDeclaringType());
        var ns = m.MD.GetString(typeDef.Namespace);
        var typeName = m.MD.GetString(typeDef.Name);
        var fullType = string.IsNullOrEmpty(ns) ? typeName : $"{ns}.{typeName}";
        var methodName = m.MD.GetString(def.Name);

        var sig = def.DecodeSignature(new StringSignatureProvider(m.MD), genericContext: null);
        var paramList = string.Join(", ", sig.ParameterTypes);
        var signature = $"{sig.ReturnType} {fullType}.{methodName}({paramList})";

        int ilSize = 0;
        if (def.RelativeVirtualAddress != 0)
        {
            try
            {
                var body = m.PE.GetMethodBody(def.RelativeVirtualAddress);
                ilSize = body.GetILBytes()?.Length ?? 0;
            }
            catch (BadImageFormatException)
            {
                ilSize = 0;
            }
        }

        var attrs = FormatAttributes(def.Attributes);
        var handle = HandleFormat.Format(m.Mvid, token);

        return new MethodSummary(
            m.Mvid, token, handle, fullType, methodName, signature,
            ilSize, def.GetGenericParameters().Count, attrs);
    }

    private static List<string> FormatAttributes(MethodAttributes a)
    {
        var list = new List<string>(6);
        switch (a & MethodAttributes.MemberAccessMask)
        {
            case MethodAttributes.Public: list.Add("public"); break;
            case MethodAttributes.Family: list.Add("protected"); break;
            case MethodAttributes.Assembly: list.Add("internal"); break;
            case MethodAttributes.FamORAssem: list.Add("protected internal"); break;
            case MethodAttributes.Private: list.Add("private"); break;
            case MethodAttributes.PrivateScope: list.Add("compiler-generated"); break;
            case MethodAttributes.FamANDAssem: list.Add("private protected"); break;
        }
        if ((a & MethodAttributes.Static) != 0) list.Add("static");
        if ((a & MethodAttributes.Abstract) != 0) list.Add("abstract");
        if ((a & MethodAttributes.Virtual) != 0) list.Add("virtual");
        if ((a & MethodAttributes.Final) != 0) list.Add("sealed");
        if ((a & MethodAttributes.SpecialName) != 0) list.Add("specialname");
        if ((a & MethodAttributes.PinvokeImpl) != 0) list.Add("pinvoke");
        return list;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var w in _watchers.Values)
        {
            w.EnableRaisingEvents = false;
            w.Dispose();
        }
        _watchers.Clear();
        foreach (var m in _modules.Values)
            m.PE.Dispose();
        _modules.Clear();
    }

    private sealed record Module(Guid Mvid, string Path, PEReader PE, MetadataReader MD);
}

/// <summary>Payload of <see cref="MetadataIndex.ModuleReloaded"/>.</summary>
public sealed class ModuleReloadedEventArgs : EventArgs
{
    public ModuleReloadedEventArgs(string path, Guid? oldMvid, Guid? newMvid, AssemblyError? error)
    {
        Path = path;
        OldMvid = oldMvid;
        NewMvid = newMvid;
        Error = error;
    }

    /// <summary>Absolute path of the file that was reloaded.</summary>
    public string Path { get; }
    /// <summary>MVID that was loaded before the change (null if first load).</summary>
    public Guid? OldMvid { get; }
    /// <summary>MVID after the change (null when <see cref="Error"/> is set).</summary>
    public Guid? NewMvid { get; }
    /// <summary>Populated when the reload failed (e.g. corrupted intermediate write).</summary>
    public AssemblyError? Error { get; }
}

/// <summary>Stable string handle format used across all tool responses.</summary>
public static class HandleFormat
{
    public static string Format(Guid mvid, int token) => $"m:{mvid:D}:0x{token:X8}";
}

/// <summary>
/// Minimal signature decoder producing readable strings. Not a full pretty-printer — good
/// enough for the <see cref="MethodSummary.Signature"/> field.
/// </summary>
internal sealed class StringSignatureProvider : ISignatureTypeProvider<string, object?>
{
    private readonly MetadataReader _md;
    public StringSignatureProvider(MetadataReader md) => _md = md;

    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.Boolean => "bool",
        PrimitiveTypeCode.Byte => "byte",
        PrimitiveTypeCode.SByte => "sbyte",
        PrimitiveTypeCode.Char => "char",
        PrimitiveTypeCode.Int16 => "short",
        PrimitiveTypeCode.UInt16 => "ushort",
        PrimitiveTypeCode.Int32 => "int",
        PrimitiveTypeCode.UInt32 => "uint",
        PrimitiveTypeCode.Int64 => "long",
        PrimitiveTypeCode.UInt64 => "ulong",
        PrimitiveTypeCode.Single => "float",
        PrimitiveTypeCode.Double => "double",
        PrimitiveTypeCode.String => "string",
        PrimitiveTypeCode.Object => "object",
        PrimitiveTypeCode.Void => "void",
        PrimitiveTypeCode.IntPtr => "nint",
        PrimitiveTypeCode.UIntPtr => "nuint",
        PrimitiveTypeCode.TypedReference => "typedref",
        _ => typeCode.ToString(),
    };

    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetArrayType(string elementType, ArrayShape shape) =>
        elementType + "[" + new string(',', Math.Max(0, shape.Rank - 1)) + "]";
    public string GetByReferenceType(string elementType) => "ref " + elementType;
    public string GetPointerType(string elementType) => elementType + "*";
    public string GetPinnedType(string elementType) => "pinned " + elementType;
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
        genericType + "<" + string.Join(",", typeArguments) + ">";
    public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
    public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
    public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var t = reader.GetTypeDefinition(handle);
        var ns = reader.GetString(t.Namespace);
        var n = reader.GetString(t.Name);
        return string.IsNullOrEmpty(ns) ? n : ns + "." + n;
    }

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var t = reader.GetTypeReference(handle);
        var ns = reader.GetString(t.Namespace);
        var n = reader.GetString(t.Name);
        return string.IsNullOrEmpty(ns) ? n : ns + "." + n;
    }

    public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
}
