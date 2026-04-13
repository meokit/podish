using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Fiberish.Memory;
using Fiberish.Native;
using Microsoft.Win32.SafeHandles;

namespace Fiberish.VFS;

public class Hostfs : FileSystem
{
    public Hostfs(DeviceNumberManager? devManager = null, MemoryRuntimeContext? memoryContext = null)
        : base(devManager, memoryContext)
    {
        Name = "hostfs";
    }

    public override SuperBlock ReadSuper(FileSystemType fsType, int flags, string devName, object? data)
    {
        var opts = HostfsMountOptions.Parse(data as string);
        var sb = new HostSuperBlock(fsType, devName, opts, DevManager, MemoryContext); // devName is the root path on host
        var rootDentry = sb.GetDentry(devName, FsName.Empty, null) ??
                         throw new FileNotFoundException("Root path not found", devName);
        sb.Root = rootDentry;
        sb.Root.Parent = sb.Root;
        return sb;
    }
}

public class HostSuperBlock : SuperBlock, IDentryCacheDropper
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private readonly Dictionary<string, Dentry> _dentryCache = new(PathComparer);
    private readonly Dictionary<long, string> _dentryPathById = [];
    private readonly Dictionary<HostInode, HostInodeKey> _identityByInode = [];
    private readonly Dictionary<HostInodeKey, HostInode> _inodeByIdentity = [];
    private readonly IMountBoundaryPolicy _mountBoundaryPolicy;
    private readonly ulong? _rootMountDomainId;
    private readonly ISpecialNodePolicy _specialNodePolicy;
    private ulong _nextIno = 1;

    public HostSuperBlock(FileSystemType type, string hostRoot, HostfsMountOptions options,
        DeviceNumberManager? devManager = null, MemoryRuntimeContext? memoryContext = null)
        : base(devManager, memoryContext ?? new MemoryRuntimeContext())
    {
        Type = type;
        HostRoot = NormalizeHostPath(hostRoot);
        Options = options;
        _mountBoundaryPolicy = options.ResolveMountBoundaryPolicy();
        _specialNodePolicy = options.ResolveSpecialNodePolicy();
        _rootMountDomainId = HostInodeIdentityResolver.TryProbe(HostRoot, out var rootNode)
            ? rootNode.MountDomainId
            : null;
        MetadataStore = new HostfsMetadataStore(HostRoot, !options.MetadataLess);
    }

    public string HostRoot { get; }
    public HostfsMountOptions Options { get; }
    internal HostfsMetadataStore MetadataStore { get; }

    public long DropDentryCache()
    {
        List<Dentry> candidates;
        lock (Lock)
        {
            candidates = _dentryCache.Values
                .Distinct()
                .Where(IsPathMappingReclaimableNoLock)
                .ToList();
        }

        var candidateSet = new HashSet<Dentry>(candidates);
        var roots = candidates
            .Where(dentry =>
                dentry.Parent == null ||
                ReferenceEquals(dentry.Parent, dentry) ||
                !candidateSet.Contains(dentry.Parent))
            .ToList();

        long dropped = 0;
        foreach (var root in roots)
            dropped += VfsShrinker.DetachCachedSubtree(root);

        lock (Lock)
        {
            var staleKeys = _dentryCache
                .Where(kv => IsPathMappingReclaimableNoLock(kv.Value))
                .Select(kv => kv.Key)
                .ToList();
            foreach (var key in staleKeys)
                RemovePathNoLock(key, false);
        }

        return dropped;
    }

    public Dentry? GetDentry(string hostPath, FsName name, Dentry? parent)
    {
        return TryGetDentry(hostPath, name, parent, out var dentry, out _) ? dentry : null;
    }

    internal bool TryGetDentry(string hostPath, FsName name, Dentry? parent, out Dentry? dentry, out int error)
    {
        var normalizedPath = NormalizeHostPath(hostPath);
        dentry = null;
        error = -(int)Errno.ENOENT;
        lock (Lock)
        {
            if (_dentryCache.TryGetValue(normalizedPath, out var cached))
            {
                if (PathExistsOnHost(normalizedPath))
                {
                    if (parent != null)
                        cached.Parent = parent;
                    dentry = cached;
                    error = 0;
                    return true;
                }

                RemoveDentryNoLock(normalizedPath, true);
            }
        }

        if (!TryResolveVisibleNode(normalizedPath, out var nodeInfo, out var nodeType, out error))
            return false;

        var identity = nodeInfo.Identity;
        var effectiveNlink = nodeType == InodeType.Directory ? null : nodeInfo.HostLinkCount;
        HostInode inode;
        lock (Lock)
        {
            if (_inodeByIdentity.TryGetValue(identity, out var existing))
            {
                if (existing.IsCacheEvicted || existing.IsFinalized)
                {
                    UnregisterInodeIdentityNoLock(existing);
                    inode = CreateHostInodeLocked(normalizedPath, nodeType, identity, effectiveNlink);
                }
                else
                {
                    inode = existing;
                }
            }
            else
            {
                inode = CreateHostInodeLocked(normalizedPath, nodeType, identity, effectiveNlink);
            }
        }

        inode.ObservePath(normalizedPath);
        MetadataStore.ApplyToInode(normalizedPath, identity, inode);
        if (effectiveNlink.HasValue)
            inode.UpdateLinkCountFromHost(effectiveNlink.Value, "HostSuperBlock.GetDentry");

        dentry = new Dentry(name, inode, parent, this);
        lock (Lock)
        {
            IndexPathNoLock(normalizedPath, dentry);
        }

        error = 0;
        return true;
    }

    internal static bool TryCreateFsNameFromHostName(string hostName, out FsName name)
    {
        ArgumentNullException.ThrowIfNull(hostName);

        if (hostName.Length == 0 || hostName == "/")
        {
            name = FsName.Empty;
            return true;
        }

        try
        {
            name = FsName.FromOwnedBytes(FsEncoding.EncodeUtf8(hostName));
            return true;
        }
        catch (ArgumentException)
        {
            name = default;
            return false;
        }
    }

    public override void WriteInode(Inode inode)
    {
    }

    public void MoveDentry(string oldPath, string newPath, Dentry dentry)
    {
        var normalizedOld = NormalizeHostPath(oldPath);
        var normalizedNew = NormalizeHostPath(newPath);
        lock (Lock)
        {
            var hits = _dentryCache
                .Where(kv => string.Equals(kv.Key, normalizedOld, PathComparison) ||
                             kv.Key.StartsWith(normalizedOld + Path.DirectorySeparatorChar, PathComparison))
                .ToList();

            foreach (var hit in hits)
            {
                var movedPath = string.Equals(hit.Key, normalizedOld, PathComparison)
                    ? normalizedNew
                    : normalizedNew + hit.Key[normalizedOld.Length..];
                RemovePathNoLock(hit.Key, false);
                IndexPathNoLock(movedPath, hit.Value);
            }

            if (!_dentryCache.ContainsKey(normalizedNew))
                IndexPathNoLock(normalizedNew, dentry);
        }
    }

    public void AddDentry(string hostPath, Dentry dentry)
    {
        var normalizedPath = NormalizeHostPath(hostPath);
        lock (Lock)
        {
            IndexPathNoLock(normalizedPath, dentry);
        }
    }

    public void RemoveDentry(string hostPath)
    {
        var normalizedPath = NormalizeHostPath(hostPath);
        lock (Lock)
        {
            RemoveDentryNoLock(normalizedPath, true);
        }
    }

    internal bool TryGetPathForDentry(Dentry? dentry, out string path)
    {
        path = string.Empty;
        if (dentry == null) return false;

        lock (Lock)
        {
            if (!_dentryPathById.TryGetValue(dentry.Id, out var mapped) || string.IsNullOrEmpty(mapped))
                return false;
            path = mapped;
            return true;
        }
    }

    internal void UnregisterInodeIdentity(HostInode inode)
    {
        lock (Lock)
        {
            UnregisterInodeIdentityNoLock(inode);
        }
    }

    private bool PathExistsOnHost(string hostPath)
    {
        return TryResolveVisibleNode(hostPath, out _, out _);
    }

    internal bool PathExistsOnHostRaw(string hostPath)
    {
        return TryResolveRawNode(hostPath, out _);
    }

    internal bool TryGetRawNodeType(string hostPath, out InodeType rawType)
    {
        rawType = InodeType.Unknown;
        if (!TryResolveRawNode(hostPath, out var node)) return false;
        rawType = node.RawType;
        return true;
    }

    private bool TryResolveRawNode(string hostPath, out HostNodeInfo node)
    {
        var normalizedPath = NormalizeHostPath(hostPath);
        return HostInodeIdentityResolver.TryProbe(normalizedPath, out node);
    }

    internal bool TryResolveVisibleNode(string hostPath, out HostNodeInfo node, out InodeType mappedType)
    {
        return TryResolveVisibleNode(hostPath, out node, out mappedType, out _);
    }

    internal bool TryResolveVisibleNode(string hostPath, out HostNodeInfo node, out InodeType mappedType, out int error)
    {
        mappedType = InodeType.Unknown;
        error = -(int)Errno.ENOENT;
        var normalizedPath = NormalizeHostPath(hostPath);
        if (!TryResolveRawNode(normalizedPath, out node))
        {
            error = -(int)Errno.ENOENT;
            return false;
        }

        if (!_mountBoundaryPolicy.Allows(HostRoot, normalizedPath, _rootMountDomainId, node.MountDomainId))
        {
            error = -(int)Errno.EXDEV;
            return false;
        }

        if (!_specialNodePolicy.TryMapType(node.RawType, out mappedType))
        {
            error = -(int)Errno.EOPNOTSUPP;
            return false;
        }

        error = 0;
        return true;
    }

    public int InstantiateDentry(Dentry dentry, string hostPath, bool isDir, int mode = 0)
    {
        _ = isDir;
        var normalizedPath = NormalizeHostPath(hostPath);
        if (!TryResolveVisibleNode(normalizedPath, out var nodeInfo, out var type, out var error))
            return error;

        var identity = nodeInfo.Identity;
        var effectiveNlink = type == InodeType.Directory ? null : nodeInfo.HostLinkCount;
        HostInode inode;
        lock (Lock)
        {
            if (_inodeByIdentity.TryGetValue(identity, out var existing) && !existing.IsCacheEvicted &&
                !existing.IsFinalized)
                inode = existing;
            else
                inode = CreateHostInodeLocked(normalizedPath, type, identity, effectiveNlink);
        }

        if (mode != 0) inode.Mode = Options.ApplyModeMask(isDir, mode);
        inode.ObservePath(normalizedPath);
        MetadataStore.ApplyToInode(normalizedPath, identity, inode);
        if (effectiveNlink.HasValue)
            inode.UpdateLinkCountFromHost(effectiveNlink.Value, "HostSuperBlock.InstantiateDentry");

        dentry.Instantiate(inode);
        lock (Lock)
        {
            IndexPathNoLock(normalizedPath, dentry);
        }

        dentry.Parent?.CacheChild(dentry, "HostSuperBlock.InstantiateDentry");
        return 0;
    }

    protected override void Shutdown()
    {
        base.Shutdown();
        lock (Lock)
        {
            _dentryCache.Clear();
            _dentryPathById.Clear();
            _inodeByIdentity.Clear();
            _identityByInode.Clear();
        }
    }

    private static string NormalizeHostPath(string hostPath)
    {
        return Path.GetFullPath(hostPath);
    }

    private HostInode CreateHostInodeLocked(string normalizedPath, InodeType type, HostInodeKey identity,
        int? hostNlink)
    {
        var inode = new HostInode(_nextIno++, this, normalizedPath, type, hostNlink);
        TrackInode(inode);
        _inodeByIdentity[identity] = inode;
        _identityByInode[inode] = identity;
        return inode;
    }

    private void UnregisterInodeIdentityNoLock(HostInode inode)
    {
        if (!_identityByInode.Remove(inode, out var key))
            return;
        if (_inodeByIdentity.TryGetValue(key, out var mapped) && ReferenceEquals(mapped, inode))
            _inodeByIdentity.Remove(key);
    }

    private void IndexPathNoLock(string path, Dentry dentry)
    {
        if (_dentryCache.TryGetValue(path, out var existing) && !ReferenceEquals(existing, dentry))
            RemovePathNoLock(path, false);

        _dentryCache[path] = dentry;
        _dentryPathById[dentry.Id] = path;
        if (dentry.Inode is HostInode hostInode)
            hostInode.ObservePath(path);
    }

    private void RemoveDentryNoLock(string hostPath, bool recursive)
    {
        var stale = recursive
            ? _dentryCache.Keys
                .Where(path => string.Equals(path, hostPath, PathComparison) ||
                               path.StartsWith(hostPath + Path.DirectorySeparatorChar, PathComparison))
                .ToList()
            : _dentryCache.Keys
                .Where(path => string.Equals(path, hostPath, PathComparison))
                .ToList();

        foreach (var path in stale)
            RemovePathNoLock(path, false);
    }

    private void RemovePathNoLock(string hostPath, bool recursive)
    {
        if (recursive)
        {
            RemoveDentryNoLock(hostPath, true);
            return;
        }

        if (!_dentryCache.Remove(hostPath, out var dentry))
            return;

        if (IsPathMappingReclaimableNoLock(dentry) &&
            _dentryPathById.TryGetValue(dentry.Id, out var mappedPath) &&
            string.Equals(mappedPath, hostPath, PathComparison))
            _dentryPathById.Remove(dentry.Id);

        if (IsPathMappingReclaimableNoLock(dentry) && dentry.Inode is HostInode hostInode)
            hostInode.ForgetPath(hostPath);
    }

    private bool IsPathMappingReclaimableNoLock(Dentry dentry)
    {
        return !ReferenceEquals(dentry, Root) &&
               !dentry.IsMounted &&
               dentry.DentryRefCount == 0;
    }
}

internal readonly record struct HostInodeKey(string Scheme, ulong Value0, ulong Value1, string? FallbackPath)
{
    public static HostInodeKey Unix(ulong dev, ulong ino)
    {
        return new HostInodeKey("unix", dev, ino, null);
    }

    public static HostInodeKey Windows(ulong volumeSerial, ulong fileId)
    {
        return new HostInodeKey("windows", volumeSerial, fileId, null);
    }

    public static HostInodeKey Fallback(string path)
    {
        var normalized = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
            normalized = normalized.ToUpperInvariant();
        return new HostInodeKey("path", 0, 0, normalized);
    }
}

internal static partial class HostInodeIdentityResolver
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out BY_HANDLE_FILE_INFORMATION lpFileInformation);

    public static HostInodeKey Resolve(string hostPath, out int? hostLinkCount)
    {
        var normalizedPath = Path.GetFullPath(hostPath);
        if (TryProbe(normalizedPath, out var node))
        {
            hostLinkCount = node.HostLinkCount;
            return node.Identity;
        }

        hostLinkCount = null;
        return HostInodeKey.Fallback(normalizedPath);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint DwLowDateTime;
        public uint DwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint DwFileAttributes;
        public FILETIME FtCreationTime;
        public FILETIME FtLastAccessTime;
        public FILETIME FtLastWriteTime;
        public uint DwVolumeSerialNumber;
        public uint NFileSizeHigh;
        public uint NFileSizeLow;
        public uint NNumberOfLinks;
        public uint NFileIndexHigh;
        public uint NFileIndexLow;
    }
}

public sealed class HostfsMountOptions
{
    private static readonly IMountBoundaryPolicy DefaultBoundaryPolicy = new SingleDomainMountBoundaryPolicy();
    private static readonly IMountBoundaryPolicy PassthroughBoundaryPolicy = new PassthroughMountBoundaryPolicy();
    private static readonly ISpecialNodePolicy DefaultSpecialNodePolicy = new StrictSpecialNodePolicy();
    private static readonly ISpecialNodePolicy PassthroughSpecialPolicy = new PassthroughSpecialNodePolicy();

    public int? MountUid { get; init; }
    public int? MountGid { get; init; }
    public int Umask { get; init; } = -1;
    public int Fmask { get; init; } = -1;
    public int Dmask { get; init; } = -1;
    public bool MetadataLess { get; init; } = true;
    public HostfsMountBoundaryMode MountBoundaryMode { get; init; } = HostfsMountBoundaryMode.SingleDomain;
    public HostfsSpecialNodeMode SpecialNodeMode { get; init; } = HostfsSpecialNodeMode.Strict;
    public IMountBoundaryPolicy? MountBoundaryPolicy { get; init; }
    public ISpecialNodePolicy? SpecialNodePolicy { get; init; }

    internal IMountBoundaryPolicy ResolveMountBoundaryPolicy()
    {
        if (MountBoundaryPolicy != null)
            return MountBoundaryPolicy;
        return MountBoundaryMode == HostfsMountBoundaryMode.Passthrough
            ? PassthroughBoundaryPolicy
            : DefaultBoundaryPolicy;
    }

    internal ISpecialNodePolicy ResolveSpecialNodePolicy()
    {
        if (SpecialNodePolicy != null)
            return SpecialNodePolicy;
        return SpecialNodeMode == HostfsSpecialNodeMode.Passthrough
            ? PassthroughSpecialPolicy
            : DefaultSpecialNodePolicy;
    }

    public int GetFileMask()
    {
        if (Fmask >= 0) return Fmask & 0x1FF;
        if (Umask >= 0) return Umask & 0x1FF;
        return 0;
    }

    public int GetDirectoryMask()
    {
        if (Dmask >= 0) return Dmask & 0x1FF;
        if (Umask >= 0) return Umask & 0x1FF;
        return 0;
    }

    public int ApplyModeMask(bool isDir, int mode)
    {
        var perm = mode & 0x1FF;
        var nonPerm = mode & ~0x1FF;
        var mask = isDir ? GetDirectoryMask() : GetFileMask();
        return nonPerm | (perm & ~mask);
    }

    public static HostfsMountOptions Parse(string? optionString)
    {
        if (string.IsNullOrWhiteSpace(optionString)) return new HostfsMountOptions();

        int? uid = null;
        int? gid = null;
        var umask = -1;
        var fmask = -1;
        var dmask = -1;
        var metadataLess = true;
        var mountBoundaryMode = HostfsMountBoundaryMode.SingleDomain;
        var specialNodeMode = HostfsSpecialNodeMode.Strict;

        var tokens = optionString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
        {
            var eq = token.IndexOf('=');
            if (eq <= 0 || eq == token.Length - 1) continue;

            var key = token[..eq].Trim().ToLowerInvariant();
            var value = token[(eq + 1)..].Trim();

            switch (key)
            {
                case "uid":
                    if (int.TryParse(value, out var parsedUid) && parsedUid >= 0) uid = parsedUid;
                    break;
                case "gid":
                    if (int.TryParse(value, out var parsedGid) && parsedGid >= 0) gid = parsedGid;
                    break;
                case "umask":
                    if (TryParseMask(value, out var parsedUmask)) umask = parsedUmask;
                    break;
                case "fmask":
                    if (TryParseMask(value, out var parsedFmask)) fmask = parsedFmask;
                    break;
                case "dmask":
                    if (TryParseMask(value, out var parsedDmask)) dmask = parsedDmask;
                    break;
                case "metadata":
                    metadataLess = !IsEnabledOption(value);
                    break;
                case "mount_boundary":
                case "mountboundary":
                case "cross_mount":
                    if (value.Equals("passthrough", StringComparison.OrdinalIgnoreCase) ||
                        value.Equals("allow", StringComparison.OrdinalIgnoreCase))
                        mountBoundaryMode = HostfsMountBoundaryMode.Passthrough;
                    else if (value.Equals("single", StringComparison.OrdinalIgnoreCase) ||
                             value.Equals("strict", StringComparison.OrdinalIgnoreCase))
                        mountBoundaryMode = HostfsMountBoundaryMode.SingleDomain;
                    break;
                case "special_node":
                case "specialnode":
                case "special":
                    if (value.Equals("passthrough", StringComparison.OrdinalIgnoreCase) ||
                        value.Equals("allow", StringComparison.OrdinalIgnoreCase))
                        specialNodeMode = HostfsSpecialNodeMode.Passthrough;
                    else if (value.Equals("strict", StringComparison.OrdinalIgnoreCase) ||
                             value.Equals("deny", StringComparison.OrdinalIgnoreCase))
                        specialNodeMode = HostfsSpecialNodeMode.Strict;
                    break;
            }
        }

        return new HostfsMountOptions
        {
            MountUid = uid,
            MountGid = gid,
            Umask = umask,
            Fmask = fmask,
            Dmask = dmask,
            MetadataLess = metadataLess,
            MountBoundaryMode = mountBoundaryMode,
            SpecialNodeMode = specialNodeMode
        };
    }

    private static bool TryParseMask(string value, out int parsed)
    {
        try
        {
            if (value.StartsWith("0", StringComparison.Ordinal) && value.Length > 1)
                parsed = Convert.ToInt32(value, 8);
            else
                parsed = int.Parse(value);

            parsed &= 0x1FF;
            return true;
        }
        catch
        {
            parsed = 0;
            return false;
        }
    }

    private static bool IsEnabledOption(string value)
    {
        return value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class HostfsMetadataStore
{
    private const int CurrentSchemaVersion = 2;

    public const string MetaDirName = ".fiberish_meta";

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private readonly string _identitiesDir;
    private readonly Lock _lock = new();
    private readonly string _manifestPath;
    private readonly string _metaDir;
    private readonly string _objectsDir;
    private readonly string _pathsDir;

    public HostfsMetadataStore(string hostRoot, bool enabled = true)
    {
        IsEnabled = enabled;
        var normalizedRoot = Path.GetFullPath(hostRoot);
        // hostRoot can be either a directory mount or a single-file mount.
        // For file mounts, place sidecar metadata under the parent directory.
        var metaBase = Directory.Exists(normalizedRoot)
            ? normalizedRoot
            : Path.GetDirectoryName(normalizedRoot) ?? normalizedRoot;
        _metaDir = Path.Combine(metaBase, MetaDirName);
        _pathsDir = Path.Combine(_metaDir, "paths");
        _objectsDir = Path.Combine(_metaDir, "objects");
        _identitiesDir = Path.Combine(_metaDir, "identities");
        _manifestPath = Path.Combine(_metaDir, "manifest.json");

        if (IsEnabled)
            InitializeV2Store();
    }

    public bool IsEnabled { get; }

    public bool IsMetaDirPath(string path)
    {
        if (!IsEnabled) return false;
        var full = Path.GetFullPath(path);
        return string.Equals(full, _metaDir, PathComparison);
    }

    public void ApplyToInode(string hostPath, HostInodeKey identity, HostInode inode)
    {
        if (!IsEnabled) return;
        var normalizedPath = NormalizeHostPath(hostPath);

        lock (_lock)
        {
            var objectId = ResolveObjectIdNoLock(normalizedPath, identity);
            inode.MetadataObjectId = objectId;
            if (!TryLoadObjectNoLock(objectId, out var meta))
                return;

            if (meta.NodeType.HasValue)
            {
                inode.Type = meta.NodeType.Value;
                inode.Rdev = meta.Rdev ?? 0;
            }

            inode.SetProjectedTimes(
                meta.ATimeNs.HasValue
                    ? DateTimeOffset.FromUnixTimeMilliseconds(meta.ATimeNs.Value / 1_000_000).UtcDateTime
                        .AddTicks(meta.ATimeNs.Value % 1_000_000 / 100)
                    : null,
                meta.MTimeNs.HasValue
                    ? DateTimeOffset.FromUnixTimeMilliseconds(meta.MTimeNs.Value / 1_000_000).UtcDateTime
                        .AddTicks(meta.MTimeNs.Value % 1_000_000 / 100)
                    : null,
                meta.CTimeNs.HasValue
                    ? DateTimeOffset.FromUnixTimeMilliseconds(meta.CTimeNs.Value / 1_000_000).UtcDateTime
                        .AddTicks(meta.CTimeNs.Value % 1_000_000 / 100)
                    : null);
        }
    }

    public FsNameMap<byte[]> LoadXAttrs(HostInode inode, string hostPath)
    {
        if (!IsEnabled) return new FsNameMap<byte[]>();
        var normalizedPath = NormalizeHostPath(hostPath);
        lock (_lock)
        {
            var objectId = EnsureObjectIdForInodeNoLock(inode, normalizedPath);
            if (!TryLoadObjectNoLock(objectId, out var meta) || meta.XAttrs == null)
                return new FsNameMap<byte[]>();

            var dict = new FsNameMap<byte[]>();
            foreach (var kv in meta.XAttrs)
                try
                {
                    dict.Set(FsName.FromString(kv.Key), Convert.FromBase64String(kv.Value));
                }
                catch
                {
                }

            return dict;
        }
    }

    public void SaveXAttrs(HostInode inode, string hostPath, FsNameMap<byte[]> xattrs)
    {
        if (!IsEnabled) return;
        var normalizedPath = NormalizeHostPath(hostPath);
        lock (_lock)
        {
            var objectId = EnsureObjectIdForInodeNoLock(inode, normalizedPath);
            var encoded = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in xattrs)
                encoded[kv.Key.ToString()] = Convert.ToBase64String(kv.Value);
            var baseRecord = TryLoadObjectNoLock(objectId, out var existing)
                ? existing
                : new HostfsObjectRecord(objectId);
            SaveObjectNoLock(baseRecord with { XAttrs = encoded });
        }
    }

    public void WriteMknod(string hostPath, InodeType type, uint rdev)
    {
        if (!IsEnabled) return;
        var normalizedPath = NormalizeHostPath(hostPath);
        lock (_lock)
        {
            var objectId = ResolveObjectIdNoLock(normalizedPath, null);
            var baseRecord = TryLoadObjectNoLock(objectId, out var existing)
                ? existing
                : new HostfsObjectRecord(objectId);
            SaveObjectNoLock(baseRecord with
            {
                NodeType = type,
                Rdev = rdev
            });
        }
    }

    public void SaveTimes(HostInode inode, string hostPath, DateTime? atime, DateTime? mtime, DateTime? ctime)
    {
        if (!IsEnabled) return;
        var normalizedPath = NormalizeHostPath(hostPath);
        lock (_lock)
        {
            var objectId = EnsureObjectIdForInodeNoLock(inode, normalizedPath);
            var baseRecord = TryLoadObjectNoLock(objectId, out var existing)
                ? existing
                : new HostfsObjectRecord(objectId);
            SaveObjectNoLock(baseRecord with
            {
                ATimeNs = atime.HasValue ? ToUnixNanoseconds(atime.Value) : baseRecord.ATimeNs,
                MTimeNs = mtime.HasValue ? ToUnixNanoseconds(mtime.Value) : baseRecord.MTimeNs,
                CTimeNs = ctime.HasValue ? ToUnixNanoseconds(ctime.Value) : baseRecord.CTimeNs
            });
        }
    }

    public void LinkPath(string sourcePath, string linkedPath, HostInode sourceInode)
    {
        if (!IsEnabled) return;
        var normalizedSource = NormalizeHostPath(sourcePath);
        var normalizedLinked = NormalizeHostPath(linkedPath);
        lock (_lock)
        {
            var objectId = sourceInode.MetadataObjectId;
            if (string.IsNullOrEmpty(objectId))
            {
                objectId = ResolveObjectIdNoLock(normalizedSource, null);
                sourceInode.MetadataObjectId = objectId;
            }

            SavePathBindingNoLock(new HostfsPathBinding(normalizedLinked, objectId));
        }
    }

    public void RemovePath(string hostPath)
    {
        if (!IsEnabled) return;
        var normalizedPath = NormalizeHostPath(hostPath);
        lock (_lock)
        {
            if (!TryLoadPathBindingNoLock(normalizedPath, out var binding))
                return;

            DeleteIfExists(GetPathBindingPath(normalizedPath));
            CollectGarbageNoLock(binding.ObjectId);
        }
    }

    public void RenamePath(string oldPath, string newPath)
    {
        if (!IsEnabled) return;
        var oldFull = NormalizeHostPath(oldPath);
        var newFull = NormalizeHostPath(newPath);
        lock (_lock)
        {
            if (TryLoadPathBindingNoLock(oldFull, out var direct))
            {
                DeleteIfExists(GetPathBindingPath(oldFull));
                SavePathBindingNoLock(direct with { Path = newFull });
            }

            var descendants = ReadAllPathBindingsNoLock()
                .Where(r => r.Path.StartsWith(oldFull + Path.DirectorySeparatorChar, PathComparison))
                .ToList();
            foreach (var entry in descendants)
            {
                var suffix = entry.Path[oldFull.Length..];
                var movedPath = newFull + suffix;
                DeleteIfExists(GetPathBindingPath(entry.Path));
                SavePathBindingNoLock(entry with { Path = movedPath });
            }
        }
    }

    private void InitializeV2Store()
    {
        Directory.CreateDirectory(_metaDir);
        if (!TryReadManifest(out var manifest) || manifest.SchemaVersion != CurrentSchemaVersion)
            ResetStoreToSchemaV2();
        EnsureLayout();
    }

    private void EnsureLayout()
    {
        Directory.CreateDirectory(_pathsDir);
        Directory.CreateDirectory(_objectsDir);
        Directory.CreateDirectory(_identitiesDir);
    }

    private void ResetStoreToSchemaV2()
    {
        if (Directory.Exists(_metaDir))
        {
            foreach (var file in Directory.GetFiles(_metaDir))
                File.Delete(file);
            foreach (var dir in Directory.GetDirectories(_metaDir))
                Directory.Delete(dir, true);
        }
        else
        {
            Directory.CreateDirectory(_metaDir);
        }

        EnsureLayout();
        WriteJsonAtomic(_manifestPath, new HostfsMetaManifest(CurrentSchemaVersion),
            HostfsJsonContext.Default.HostfsMetaManifest);
    }

    private bool TryReadManifest(out HostfsMetaManifest manifest)
    {
        manifest = default!;
        if (!File.Exists(_manifestPath)) return false;
        try
        {
            var parsed = JsonSerializer.Deserialize(File.ReadAllText(_manifestPath),
                HostfsJsonContext.Default.HostfsMetaManifest);
            if (parsed == null) return false;
            manifest = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private string EnsureObjectIdForInodeNoLock(HostInode inode, string normalizedPath)
    {
        if (!string.IsNullOrEmpty(inode.MetadataObjectId))
            return inode.MetadataObjectId!;

        var objectId = ResolveObjectIdNoLock(normalizedPath, null);
        inode.MetadataObjectId = objectId;
        return objectId;
    }

    private string ResolveObjectIdNoLock(string normalizedPath, HostInodeKey? identity)
    {
        if (TryLoadPathBindingNoLock(normalizedPath, out var binding))
        {
            if (identity.HasValue)
                SaveIdentityBindingNoLock(new HostfsIdentityBinding(identity.Value, binding.ObjectId));
            return binding.ObjectId;
        }

        if (identity.HasValue && TryLoadIdentityBindingNoLock(identity.Value, out var identityBinding))
        {
            SavePathBindingNoLock(new HostfsPathBinding(normalizedPath, identityBinding.ObjectId));
            return identityBinding.ObjectId;
        }

        var objectId = Guid.NewGuid().ToString("N");
        SaveObjectNoLock(new HostfsObjectRecord(objectId));
        SavePathBindingNoLock(new HostfsPathBinding(normalizedPath, objectId));
        if (identity.HasValue)
            SaveIdentityBindingNoLock(new HostfsIdentityBinding(identity.Value, objectId));
        return objectId;
    }

    private bool TryLoadPathBindingNoLock(string normalizedPath, out HostfsPathBinding binding)
    {
        var path = GetPathBindingPath(normalizedPath);
        binding = default!;
        if (!File.Exists(path)) return false;
        try
        {
            var parsed =
                JsonSerializer.Deserialize(File.ReadAllText(path), HostfsJsonContext.Default.HostfsPathBinding);
            if (parsed == null) return false;
            binding = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryLoadIdentityBindingNoLock(HostInodeKey identity, out HostfsIdentityBinding binding)
    {
        var path = GetIdentityBindingPath(identity);
        binding = default!;
        if (!File.Exists(path)) return false;
        try
        {
            var parsed =
                JsonSerializer.Deserialize(File.ReadAllText(path), HostfsJsonContext.Default.HostfsIdentityBinding);
            if (parsed == null) return false;
            binding = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryLoadObjectNoLock(string objectId, out HostfsObjectRecord record)
    {
        var path = GetObjectPath(objectId);
        record = default!;
        if (!File.Exists(path)) return false;
        try
        {
            var parsed =
                JsonSerializer.Deserialize(File.ReadAllText(path), HostfsJsonContext.Default.HostfsObjectRecord);
            if (parsed == null) return false;
            record = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private List<HostfsPathBinding> ReadAllPathBindingsNoLock()
    {
        var result = new List<HostfsPathBinding>();
        if (!Directory.Exists(_pathsDir)) return result;
        foreach (var file in Directory.GetFiles(_pathsDir, "*.json"))
            try
            {
                var parsed = JsonSerializer.Deserialize(File.ReadAllText(file),
                    HostfsJsonContext.Default.HostfsPathBinding);
                if (parsed != null) result.Add(parsed);
            }
            catch
            {
            }

        return result;
    }

    private IEnumerable<string> EnumerateIdentityFilesByObjectIdNoLock(string objectId)
    {
        if (!Directory.Exists(_identitiesDir)) yield break;
        foreach (var file in Directory.GetFiles(_identitiesDir, "*.json"))
        {
            HostfsIdentityBinding? parsed = null;
            try
            {
                parsed = JsonSerializer.Deserialize(File.ReadAllText(file),
                    HostfsJsonContext.Default.HostfsIdentityBinding);
            }
            catch
            {
            }

            if (parsed != null && string.Equals(parsed.ObjectId, objectId, StringComparison.Ordinal))
                yield return file;
        }
    }

    private void SavePathBindingNoLock(HostfsPathBinding binding)
    {
        WriteJsonAtomic(GetPathBindingPath(binding.Path), binding, HostfsJsonContext.Default.HostfsPathBinding);
    }

    private void SaveIdentityBindingNoLock(HostfsIdentityBinding binding)
    {
        WriteJsonAtomic(GetIdentityBindingPath(binding.Identity), binding,
            HostfsJsonContext.Default.HostfsIdentityBinding);
    }

    private void SaveObjectNoLock(HostfsObjectRecord record)
    {
        WriteJsonAtomic(GetObjectPath(record.ObjectId), record, HostfsJsonContext.Default.HostfsObjectRecord);
    }

    private void CollectGarbageNoLock(string objectId)
    {
        var stillReferenced = ReadAllPathBindingsNoLock()
            .Any(b => string.Equals(b.ObjectId, objectId, StringComparison.Ordinal));
        if (stillReferenced) return;

        DeleteIfExists(GetObjectPath(objectId));
        foreach (var identityFile in EnumerateIdentityFilesByObjectIdNoLock(objectId))
            DeleteIfExists(identityFile);
    }

    private static string NormalizeHostPath(string hostPath)
    {
        return Path.GetFullPath(hostPath);
    }

    private static long ToUnixNanoseconds(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        var offset = new DateTimeOffset(utc);
        var seconds = offset.ToUnixTimeSeconds();
        var nanos = utc.Ticks % TimeSpan.TicksPerSecond * 100;
        return checked(seconds * 1_000_000_000L + nanos);
    }

    private static string CanonicalizePathKey(string hostPath)
    {
        var full = NormalizeHostPath(hostPath);
        return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
    }

    private static string ComputeHashKey(string raw)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    private string GetPathBindingPath(string hostPath)
    {
        return Path.Combine(_pathsDir, $"{ComputeHashKey(CanonicalizePathKey(hostPath))}.json");
    }

    private string GetObjectPath(string objectId)
    {
        return Path.Combine(_objectsDir, $"{objectId}.json");
    }

    private string GetIdentityBindingPath(HostInodeKey identity)
    {
        var fallback = string.IsNullOrEmpty(identity.FallbackPath)
            ? string.Empty
            : CanonicalizePathKey(identity.FallbackPath);
        var key = $"{identity.Scheme}|{identity.Value0}|{identity.Value1}|{fallback}";
        return Path.Combine(_identitiesDir, $"{ComputeHashKey(key)}.json");
    }

    private static void WriteJsonAtomic<T>(string path, T value,
        JsonTypeInfo<T> jsonType)
    {
        var tempPath = $"{path}.tmp-{Guid.NewGuid():N}";
        var payload = JsonSerializer.Serialize(value, jsonType);
        File.WriteAllText(tempPath, payload);
        File.Move(tempPath, path, true);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}

internal sealed record HostfsMetaManifest(int SchemaVersion);

internal sealed record HostfsPathBinding(
    string Path,
    string ObjectId);

internal sealed record HostfsIdentityBinding(
    HostInodeKey Identity,
    string ObjectId);

internal sealed record HostfsObjectRecord(
    string ObjectId,
    InodeType? NodeType = null,
    uint? Rdev = null,
    Dictionary<string, string>? XAttrs = null,
    long? ATimeNs = null,
    long? MTimeNs = null,
    long? CTimeNs = null);

internal static partial class HostfsOwnershipMapper
{
#if HOSTFS_WINDOWS
    private static readonly bool IsUnixLike = false;
#elif HOSTFS_DARWIN || HOSTFS_LINUX
    private static readonly bool IsUnixLike = true;
#else
    private static readonly bool IsUnixLike = OperatingSystem.IsLinux() ||
                                              OperatingSystem.IsMacOS() ||
                                              OperatingSystem.IsIOS() ||
                                              OperatingSystem.IsTvOS() ||
                                              OperatingSystem.IsWatchOS() ||
                                              OperatingSystem.IsMacCatalyst();
#endif
    private static readonly int CurrentHostUid = IsUnixLike ? geteuid() : 0;
    private static readonly int CurrentHostGid = IsUnixLike ? getegid() : 0;

    [LibraryImport("libc", SetLastError = true)]
    private static partial int geteuid();

    [LibraryImport("libc", SetLastError = true)]
    private static partial int getegid();

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int chown(string path, int owner, int group);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int chmod(string path, uint mode);

    public static void RefreshGuestProjection(HostInode inode, int queryUid, int queryGid, HostfsMountOptions options)
    {
        // Cygwin noacl-style fallback for non-Unix hosts.
        if (!IsUnixLike)
        {
            var mode = options.ApplyModeMask(inode.Type == InodeType.Directory, 0x1ED); // 0755
            inode.Mode = mode;
            inode.Uid = options.MountUid ?? queryUid;
            inode.Gid = options.MountGid ?? queryGid;
            return;
        }

        if (!TryReadHostStat(inode.HostPath, out var hostUid, out var hostGid, out var modeBits)) return;

        inode.Mode = options.ApplyModeMask(inode.Type == InodeType.Directory, modeBits);
        var mappedUid = hostUid == 0 || hostUid == CurrentHostUid ? 0 : hostUid;
        var mappedGid = hostGid == 0 || hostGid == CurrentHostGid ? 0 : hostGid;
        inode.Uid = options.MountUid ?? mappedUid;
        inode.Gid = options.MountGid ?? mappedGid;
    }

    public static int SetGuestOwnership(HostInode inode, int uid, int gid)
    {
        if (uid == -1 && gid == -1)
        {
            inode.CTime = DateTime.Now;
            return 0;
        }

        var opts = ((HostSuperBlock)inode.SuperBlock).Options;
        if ((opts.MountUid.HasValue || opts.MountGid.HasValue) && (uid != -1 || gid != -1))
            return -(int)Errno.EPERM;

        if (!IsUnixLike)
        {
            inode.CTime = DateTime.Now;
            return 0;
        }

        // Root in guest maps to current host user/group in rootless mode.
        var hostUid = uid == -1 ? -1 : uid == 0 ? CurrentHostUid : uid;
        var hostGid = gid == -1 ? -1 : gid == 0 ? CurrentHostGid : gid;

        if (chown(inode.HostPath, hostUid, hostGid) != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            return errno == 0 ? -(int)Errno.EPERM : -errno;
        }

        inode.CTime = DateTime.Now;
        return 0;
    }

    public static int SetGuestMode(HostInode inode, int mode)
    {
        if (!IsUnixLike)
        {
            inode.Mode = 0x1ED; // noacl mode is always 0755
            inode.CTime = DateTime.Now;
            return 0;
        }

        var requestedMode = (uint)(mode & 0xFFF);
        if (chmod(inode.HostPath, requestedMode) != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            return errno == 0 ? -(int)Errno.EPERM : -errno;
        }

        inode.Mode = (int)requestedMode;
        inode.CTime = DateTime.Now;
        return 0;
    }

    private static bool TryReadHostStat(string path, out int uid, out int gid, out int modeBits)
    {
        uid = 0;
        gid = 0;
        modeBits = 0;
        if (!HostInodeIdentityResolver.TryReadUnixStat(path, out var statData))
            return false;

        uid = unchecked((int)statData.Uid);
        gid = unchecked((int)statData.Gid);
        modeBits = (int)(statData.Mode & 0x0FFF);
        return true;
    }
}

public partial class HostInode : MappingBackedInode, IHostMappedCacheDropper
{
    private readonly HashSet<string> _aliasPaths = new(OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal);

    private readonly HashSet<long> _dirtyPageIndexes = [];
    private readonly Lock _dirtyPageLock = new();
    private readonly Lock _mappedCacheLock = new();
    private readonly Lock _xattrLock = new();
    private string _hostPath;
    private MappedFilePageCache? _mappedPageCache;
    private DateTime? _projectedATime;
    private DateTime? _projectedCTime;
    private DateTime? _projectedMTime;

    private FsNameMap<byte[]>? _xattrs;

    public HostInode(ulong ino, SuperBlock sb, string hostPath, InodeType type, int? initialLinkCount = null)
    {
        Ino = ino;
        SuperBlock = sb;
        _hostPath = Path.GetFullPath(hostPath);
        _aliasPaths.Add(_hostPath);
        Type = type;
        var isDir = type == InodeType.Directory;
        Mode = isDir ? 0x1FF : 0x1B6; // 777 or 666
        SetInitialLinkCount(Math.Max(0, initialLinkCount ?? ComputeInitialLinkCount()), "HostInode.ctor");
    }

    public string HostPath
    {
        get
        {
            lock (Lock)
            {
                if (PathExists(_hostPath)) return _hostPath;
                foreach (var path in _aliasPaths)
                {
                    if (!PathExists(path)) continue;
                    _hostPath = path;
                    return _hostPath;
                }

                return _hostPath;
            }
        }
        set
        {
            var normalized = Path.GetFullPath(value);
            lock (Lock)
            {
                _hostPath = normalized;
                _aliasPaths.Add(normalized);
            }

            lock (_mappedCacheLock)
            {
                _mappedPageCache?.UpdatePath(normalized);
            }
        }
    }

    public override bool SupportsMmap => Type == InodeType.File;

    internal string? MetadataObjectId { get; set; }

    private bool TryGetBackingFileLength(out ulong hostLength)
    {
        try
        {
            hostLength = (ulong)new FileInfo(HostPath).Length;
            return true;
        }
        catch
        {
            hostLength = 0;
            return false;
        }
    }

    public override ulong Size
    {
        get
        {
            if (Type == InodeType.Directory) return 4096;
            if (Type == InodeType.Symlink) return 0;

            if (!TryGetBackingFileLength(out var hostLength))
                return base.Size;

            return HasDirtyPageCachePages()
                ? Math.Max(hostLength, base.Size)
                : hostLength;
        }
        set => base.Size = value;
    }

    public override DateTime MTime
    {
        get
        {
            if (_projectedMTime.HasValue) return _projectedMTime.Value;
            try
            {
                return File.GetLastWriteTimeUtc(HostPath);
            }
            catch
            {
                return base.MTime;
            }
        }
        set => base.MTime = value;
    }

    public override DateTime ATime
    {
        get
        {
            if (_projectedATime.HasValue) return _projectedATime.Value;
            try
            {
                return File.GetLastAccessTimeUtc(HostPath);
            }
            catch
            {
                return base.ATime;
            }
        }
        set => base.ATime = value;
    }

    public override DateTime CTime
    {
        get
        {
            if (_projectedCTime.HasValue) return _projectedCTime.Value;
            try
            {
                return File.GetCreationTimeUtc(HostPath);
            }
            catch
            {
                return base.CTime;
            }
        }
        set => base.CTime = value;
    }

    FilePageBackendDiagnostics IHostMappedCacheDropper.GetMappedCacheDiagnostics()
    {
        return GetMappedPageCacheDiagnostics();
    }

    long IHostMappedCacheDropper.TrimMappedCache(bool aggressive)
    {
        lock (_mappedCacheLock)
        {
            return _mappedPageCache?.Trim(aggressive) ?? 0;
        }
    }

    internal override bool PreferHostMappedMappingPage(PageCacheAccessMode accessMode)
    {
        return Type == InodeType.File;
    }

    private static int NegErrnoFromPInvoke(int fallback = (int)Errno.EIO)
    {
        var errno = Marshal.GetLastPInvokeError();
        return errno == 0 ? -fallback : -errno;
    }

    private static int MapHostException(Exception ex, Errno fallback = Errno.EIO)
    {
        return ex switch
        {
            UnauthorizedAccessException => -(int)Errno.EACCES,
            FileNotFoundException => -(int)Errno.ENOENT,
            DirectoryNotFoundException => -(int)Errno.ENOENT,
            PathTooLongException => -(int)Errno.EINVAL,
            NotSupportedException => -(int)Errno.EOPNOTSUPP,
            ArgumentException => -(int)Errno.EINVAL,
            IOException => -(int)fallback,
            _ => -(int)fallback
        };
    }

    private static bool IsDescendantPath(string ancestorPath, string candidatePath)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(ancestorPath, candidatePath, comparison))
            return false;
        var prefix = ancestorPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                     Path.DirectorySeparatorChar;
        return candidatePath.StartsWith(prefix, comparison);
    }

    internal void SetProjectedTimes(DateTime? atime, DateTime? mtime, DateTime? ctime)
    {
        _projectedATime = atime;
        _projectedMTime = mtime;
        _projectedCTime = ctime;
        if (atime.HasValue) base.ATime = atime.Value;
        if (mtime.HasValue) base.MTime = mtime.Value;
        if (ctime.HasValue) base.CTime = ctime.Value;
    }

    private static DateTime NormalizeUtc(DateTime value)
    {
        return value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
    }

    private void ApplyHostTimes(DateTime? atime, DateTime? mtime, DateTime? ctime)
    {
        var hostPath = ResolveHostPath();
        if (Type == InodeType.Directory)
        {
            if (atime.HasValue) Directory.SetLastAccessTimeUtc(hostPath, NormalizeUtc(atime.Value));
            if (mtime.HasValue) Directory.SetLastWriteTimeUtc(hostPath, NormalizeUtc(mtime.Value));
            if (ctime.HasValue) Directory.SetCreationTimeUtc(hostPath, NormalizeUtc(ctime.Value));
            return;
        }

        if (atime.HasValue) File.SetLastAccessTimeUtc(hostPath, NormalizeUtc(atime.Value));
        if (mtime.HasValue) File.SetLastWriteTimeUtc(hostPath, NormalizeUtc(mtime.Value));
        if (ctime.HasValue) File.SetCreationTimeUtc(hostPath, NormalizeUtc(ctime.Value));
    }

    public override int UpdateTimes(DateTime? atime, DateTime? mtime, DateTime? ctime)
    {
        try
        {
            ApplyHostTimes(atime, mtime, ctime);
        }
        catch (UnauthorizedAccessException)
        {
            return -(int)Errno.EPERM;
        }
        catch (IOException)
        {
            return -(int)Errno.EIO;
        }
        catch (NotSupportedException)
        {
            return -(int)Errno.EOPNOTSUPP;
        }

        var metadataStore = ((HostSuperBlock)SuperBlock).MetadataStore;
        if (metadataStore.IsEnabled)
        {
            SetProjectedTimes(
                atime ?? _projectedATime,
                mtime ?? _projectedMTime,
                ctime ?? _projectedCTime);
            metadataStore.SaveTimes(this, ResolveHostPath(), atime, mtime, ctime);
        }
        else
        {
            SetProjectedTimes(null, null, null);
            if (atime.HasValue) base.ATime = NormalizeUtc(atime.Value);
            if (mtime.HasValue) base.MTime = NormalizeUtc(mtime.Value);
            if (ctime.HasValue) base.CTime = NormalizeUtc(ctime.Value);
        }

        return 0;
    }

    public void RefreshProjectedMetadata(int queryUid, int queryGid)
    {
        var opts = ((HostSuperBlock)SuperBlock).Options;
        HostfsOwnershipMapper.RefreshGuestProjection(this, queryUid, queryGid, opts);
    }

    public int SetProjectedOwnership(int uid, int gid)
    {
        return HostfsOwnershipMapper.SetGuestOwnership(this, uid, gid);
    }

    public int SetProjectedMode(int mode)
    {
        return HostfsOwnershipMapper.SetGuestMode(this, mode);
    }

    public override Dentry? Lookup(ReadOnlySpan<byte> name)
    {
        if (Type != InodeType.Directory) return null;
        if (!TryDecodeHostComponent(name, out var componentName, out var component))
        {
            SetLookupFailureError(-(int)Errno.ENOENT);
            return null;
        }

        if (component == HostfsMetadataStore.MetaDirName) return null;

        var subPath = Path.Combine(ResolveHostPath(), component);
        if (Dentries.Count == 0) return null;
        if (((HostSuperBlock)SuperBlock).TryGetDentry(subPath, componentName, Dentries[0], out var dentry,
                out var error))
        {
            SetLookupFailureError(-(int)Errno.ENOENT);
            return dentry;
        }

        SetLookupFailureError(error);
        return null;
    }

    public override Dentry? Lookup(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Lookup(FsEncoding.EncodeUtf8(name));
    }

    public override int Create(Dentry dentry, int mode, int uid, int gid)
    {
        if (Type != InodeType.Directory) return -(int)Errno.ENOTDIR;
        if (!TryDecodeHostComponent(dentry.Name, out var component))
            return -(int)Errno.EINVAL;
        var subPath = Path.Combine(ResolveHostPath(), component);
        var sb = (HostSuperBlock)SuperBlock;
        if (sb.PathExistsOnHostRaw(subPath)) return -(int)Errno.EEXIST;

        try
        {
            using (File.Create(subPath))
            {
            } // Create empty file
        }
        catch (Exception ex)
        {
            return MapHostException(ex);
        }

        sb.MetadataStore.RemovePath(subPath);
        var instantiateRc = sb.InstantiateDentry(dentry, subPath, false, mode);
        if (instantiateRc < 0)
        {
            try
            {
                File.Delete(subPath);
            }
            catch
            {
            }

            return instantiateRc;
        }

        return 0;
    }

    public override int Mkdir(Dentry dentry, int mode, int uid, int gid)
    {
        if (Type != InodeType.Directory) return -(int)Errno.ENOTDIR;
        if (!TryDecodeHostComponent(dentry.Name, out var component))
            return -(int)Errno.EINVAL;
        var subPath = Path.Combine(ResolveHostPath(), component);
        var sb = (HostSuperBlock)SuperBlock;
        if (sb.PathExistsOnHostRaw(subPath)) return -(int)Errno.EEXIST;

        try
        {
            Directory.CreateDirectory(subPath);
        }
        catch (Exception ex)
        {
            return MapHostException(ex);
        }

        sb.MetadataStore.RemovePath(subPath);
        var instantiateRc = sb.InstantiateDentry(dentry, subPath, true, mode);
        if (instantiateRc < 0)
        {
            try
            {
                Directory.Delete(subPath, false);
            }
            catch
            {
            }

            return instantiateRc;
        }

        if (dentry.Inode != null)
            NamespaceOps.OnDirectoryCreated(this, dentry.Inode, "HostInode.Mkdir");
        return 0;
    }

    public override int Mknod(Dentry dentry, int mode, int uid, int gid, InodeType type, uint rdev)
    {
        if (Type != InodeType.Directory) return -(int)Errno.ENOTDIR;
        if (!TryDecodeHostComponent(dentry.Name, out var component))
            return -(int)Errno.EINVAL;
        var subPath = Path.Combine(ResolveHostPath(), component);
        var sb = (HostSuperBlock)SuperBlock;
        if (sb.PathExistsOnHostRaw(subPath)) return -(int)Errno.EEXIST;

        try
        {
            // Hostfs fallback: materialize a placeholder and persist node semantics in sidecar metadata.
            using (File.Create(subPath))
            {
            }

            sb.MetadataStore.WriteMknod(subPath, type, rdev);
        }
        catch (Exception ex)
        {
            return MapHostException(ex);
        }

        var instantiateRc = sb.InstantiateDentry(dentry, subPath, false, mode);
        if (instantiateRc < 0)
        {
            try
            {
                File.Delete(subPath);
            }
            catch
            {
            }

            return instantiateRc;
        }

        if (dentry.Inode != null)
        {
            dentry.Inode.Type = type;
            dentry.Inode.Rdev = rdev;
            dentry.Inode.Mode = mode;
            dentry.Inode.Uid = uid;
            dentry.Inode.Gid = gid;
        }

        return 0;
    }

    public override int Unlink(ReadOnlySpan<byte> name)
    {
        if (!TryDecodeHostComponent(name, out var componentName, out var component))
            return -(int)Errno.EINVAL;

        var subPath = Path.Combine(ResolveHostPath(), component);
        var sb = (HostSuperBlock)SuperBlock;
        if (!sb.TryGetRawNodeType(subPath, out var targetType))
            return -(int)Errno.ENOENT;
        if (targetType == InodeType.Directory)
            return -(int)Errno.EISDIR;

        // unlink(2) deletes the directory entry itself and must not follow symlinks.
        var dentry = sb.GetDentry(subPath, componentName, null);
        try
        {
            File.Delete(subPath);
        }
        catch (Exception ex)
        {
            return MapHostException(ex);
        }

        sb.MetadataStore.RemovePath(subPath);
        NamespaceOps.OnEntryRemoved(dentry?.Inode, "HostInode.Unlink");
        if (dentry?.Inode != null && !dentry.Inode.HasActiveRuntimeRefs)
            dentry.UnbindInode("HostInode.Unlink");
        if (Dentries.Count > 0)
            _ = Dentries[0].TryUncacheChild(componentName, "HostInode.Unlink", out _);
        sb.RemoveDentry(subPath);
        return 0;
    }

    public override int Unlink(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Unlink(FsEncoding.EncodeUtf8(name));
    }

    public override int Rmdir(ReadOnlySpan<byte> name)
    {
        if (!TryDecodeHostComponent(name, out var componentName, out var component))
            return -(int)Errno.EINVAL;

        var subPath = Path.Combine(ResolveHostPath(), component);
        var sb = (HostSuperBlock)SuperBlock;
        if (!sb.TryGetRawNodeType(subPath, out var targetType))
            return -(int)Errno.ENOENT;
        if (targetType != InodeType.Directory)
            return -(int)Errno.ENOTDIR;

        var dentry = sb.GetDentry(subPath, componentName, null);
        try
        {
            Directory.Delete(subPath, false);
        }
        catch (IOException)
        {
            return -(int)Errno.ENOTEMPTY;
        }
        catch (Exception ex)
        {
            return MapHostException(ex);
        }

        sb.MetadataStore.RemovePath(subPath);
        if (dentry?.Inode != null)
        {
            NamespaceOps.OnDirectoryRemoved(this, dentry.Inode, "HostInode.Rmdir");
            dentry.UnbindInode("HostInode.Rmdir");
        }

        if (Dentries.Count > 0)
            _ = Dentries[0].TryUncacheChild(componentName, "HostInode.Rmdir", out _);
        sb.RemoveDentry(subPath);
        return 0;
    }

    public override int Rmdir(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Rmdir(FsEncoding.EncodeUtf8(name));
    }

    public override int Rename(ReadOnlySpan<byte> oldName, Inode newParent, ReadOnlySpan<byte> newName)
    {
        if (Type != InodeType.Directory || newParent.Type != InodeType.Directory)
            return -(int)Errno.ENOTDIR;
        if (!TryDecodeHostComponent(oldName, out var oldComponentName, out var oldComponent) ||
            !TryDecodeHostComponent(newName, out var newComponentName, out var newComponent))
            return -(int)Errno.EINVAL;

        if (newParent is not HostInode targetParent)
            return -(int)Errno.EXDEV;
        var oldFullPath = Path.Combine(ResolveHostPath(), oldComponent);
        var newFullPath = Path.Combine(targetParent.ResolveHostPath(), newComponent);
        if (IsDescendantPath(oldFullPath, newFullPath))
            return -(int)Errno.EINVAL;

        var sb = (HostSuperBlock)SuperBlock;
        var dentry = sb.GetDentry(oldFullPath, oldComponentName, null);
        if (dentry == null)
            return -(int)Errno.ENOENT;
        var sourceIsDirectory = dentry.Inode!.Type == InodeType.Directory;
        var movedAcrossParents = sourceIsDirectory && !ReferenceEquals(this, targetParent);

        // Handle overwrite
        if (sb.TryGetRawNodeType(newFullPath, out var targetType))
        {
            var targetDentry = sb.GetDentry(newFullPath, newComponentName, null);
            if (targetDentry != null && ReferenceEquals(targetDentry.Inode, dentry.Inode))
                return 0;

            var targetIsDirectory = targetType == InodeType.Directory;

            if (targetIsDirectory)
            {
                if (!sourceIsDirectory)
                    return -(int)Errno.EISDIR;

                if (targetDentry != null && targetDentry.Children.Count > 0)
                    return -(int)Errno.ENOTEMPTY;

                try
                {
                    Directory.Delete(newFullPath, false);
                }
                catch (IOException)
                {
                    return -(int)Errno.ENOTEMPTY;
                }
                catch (Exception ex)
                {
                    return MapHostException(ex);
                }

                if (targetDentry?.Inode != null)
                    NamespaceOps.OnDirectoryRemoved(targetParent, targetDentry.Inode,
                        "HostInode.Rename.overwrite-target-dir");
            }
            else
            {
                if (sourceIsDirectory)
                    return -(int)Errno.ENOTDIR;

                try
                {
                    File.Delete(newFullPath);
                }
                catch (Exception ex)
                {
                    return MapHostException(ex);
                }

                NamespaceOps.OnRenameOverwrite(dentry.Inode, targetDentry?.Inode, "HostInode.Rename.overwrite-target");
            }

            targetDentry?.UnbindInode("HostInode.Rename.overwrite-target");
            if (targetParent.Dentries.Count > 0)
                _ = targetParent.Dentries[0].TryUncacheChild(newComponentName,
                    "HostInode.Rename.overwrite-target", out _);
            sb.MetadataStore.RemovePath(newFullPath);
            sb.RemoveDentry(newFullPath);
        }

        try
        {
            if (sourceIsDirectory)
                Directory.Move(oldFullPath, newFullPath);
            else
                File.Move(oldFullPath, newFullPath);
        }
        catch (Exception ex)
        {
            return MapHostException(ex);
        }

        sb.MetadataStore.RenamePath(oldFullPath, newFullPath);

        // Update cache and internal path
        sb.MoveDentry(oldFullPath, newFullPath, dentry);
        if (dentry.Inode is HostInode movedInode)
        {
            movedInode.ForgetPath(oldFullPath);
            movedInode.ObservePath(newFullPath);
        }

        if (Dentries.Count > 0)
            _ = Dentries[0].TryUncacheChild(oldComponentName, "HostInode.Rename.old-parent", out _);
        dentry.Name = newComponentName;
        if (targetParent.Dentries.Count > 0)
        {
            dentry.Parent = targetParent.Dentries[0];
            dentry.Parent.CacheChild(dentry, "HostInode.Rename.new-parent");
        }

        if (movedAcrossParents)
            NamespaceOps.OnDirectoryMovedAcrossParents(this, targetParent, "HostInode.Rename");

        return 0;
    }

    public override int Rename(string oldName, Inode newParent, string newName)
    {
        ArgumentNullException.ThrowIfNull(oldName);
        ArgumentNullException.ThrowIfNull(newName);
        return Rename(FsEncoding.EncodeUtf8(oldName), newParent, FsEncoding.EncodeUtf8(newName));
    }

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int link(string oldpath, string newpath);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int flock(int fd, int operation);

    public override int Flock(LinuxFile linuxFile, int operation)
    {
        if (linuxFile.PrivateData is SafeFileHandle handle)
        {
            var fd = handle.DangerousGetHandle().ToInt32();
            if (flock(fd, operation) != 0) return -Marshal.GetLastPInvokeError();

            return 0;
        }

        return -(int)Errno.EBADF;
    }

    public override int Link(Dentry dentry, Inode oldInode)
    {
        if (Type != InodeType.Directory) return -(int)Errno.ENOTDIR;
        if (oldInode is not HostInode hi) return -(int)Errno.EXDEV;
        if (!TryDecodeHostComponent(dentry.Name, out var component))
            return -(int)Errno.EINVAL;

        var newPath = Path.Combine(ResolveHostPath(), component);
        var sourcePath = hi.ResolveHostPath();
        if (link(sourcePath, newPath) != 0)
            return NegErrnoFromPInvoke();

        var sb = (HostSuperBlock)SuperBlock;
        dentry.Instantiate(oldInode);
        sb.MetadataStore.LinkPath(sourcePath, newPath, hi);
        NamespaceOps.OnLinkAdded(oldInode, "HostInode.Link");
        sb.AddDentry(newPath, dentry);
        if (Dentries.Count > 0)
            Dentries[0].CacheChild(dentry, "HostInode.Link");

        return 0;
    }

    public override int Symlink(Dentry dentry, string target, int uid, int gid)
    {
        if (Type != InodeType.Directory) return -(int)Errno.ENOTDIR;
        if (!TryDecodeHostComponent(dentry.Name, out var component))
            return -(int)Errno.EINVAL;
        var newPath = Path.Combine(ResolveHostPath(), component);
        var sb = (HostSuperBlock)SuperBlock;
        if (sb.PathExistsOnHostRaw(newPath))
            return -(int)Errno.EEXIST;

        try
        {
            File.CreateSymbolicLink(newPath, target);
        }
        catch (Exception ex)
        {
            return MapHostException(ex);
        }

        sb.MetadataStore.RemovePath(newPath);
        var instantiateRc = sb.InstantiateDentry(dentry, newPath, false); // symlinks don't really use mode in Create
        if (instantiateRc < 0)
        {
            try
            {
                File.Delete(newPath);
            }
            catch
            {
            }

            return instantiateRc;
        }

        return 0;
    }

    public override int Readlink(out byte[]? target)
    {
        var rc = Readlink(out string? decodedTarget);
        if (rc < 0 || decodedTarget == null)
        {
            target = null;
            return rc;
        }

        if (!FsEncoding.TryEncodeUtf8(decodedTarget, out target))
        {
            target = null;
            return -(int)Errno.EIO;
        }

        return 0;
    }

    public override int Readlink(out string? target)
    {
        try
        {
            var info = new FileInfo(ResolveHostPath());
            target = info.LinkTarget;
            return string.IsNullOrEmpty(target) ? -(int)Errno.EINVAL : 0;
        }
        catch (Exception ex)
        {
            target = null;
            return MapHostException(ex);
        }
    }

    public override void Open(LinuxFile linuxFile)
    {
        if (Type == InodeType.File)
        {
            var mode = FileMode.Open;
            var access = FileAccess.Read;
            var share = FileShare.ReadWrite;

            if ((linuxFile.Flags & FileFlags.O_WRONLY) != 0) access = FileAccess.ReadWrite;
            else
                access = FileAccess
                    .ReadWrite; // Default to ReadWrite at host level to support CopyUp, even if guest asked for ReadOnly

            var hasCreate = (linuxFile.Flags & FileFlags.O_CREAT) != 0;
            var hasExcl = (linuxFile.Flags & FileFlags.O_EXCL) != 0;

            if (hasCreate && hasExcl) mode = FileMode.CreateNew;
            else if (hasCreate) mode = FileMode.OpenOrCreate;

            // Use SafeFileHandle with RandomAccess for thread-safe I/O without locks
            var openPath = ResolveHostPath(linuxFile);
            try
            {
                var handle = File.OpenHandle(openPath, mode, access, share);
                linuxFile.PrivateData = handle;
            }
            catch (UnauthorizedAccessException) when (access == FileAccess.ReadWrite &&
                                                      (linuxFile.Flags & FileFlags.O_WRONLY) == 0 &&
                                                      (linuxFile.Flags & FileFlags.O_RDWR) == 0)
            {
                // Fallback for ReadOnly files (e.g. executable binaries in Docker image)
                var handle = File.OpenHandle(openPath, mode, FileAccess.Read, share);
                linuxFile.PrivateData = handle;
            }
            catch (IOException) when (access == FileAccess.ReadWrite && (linuxFile.Flags & FileFlags.O_WRONLY) == 0 &&
                                      (linuxFile.Flags & FileFlags.O_RDWR) == 0)
            {
                // Fallback for ReadOnly files (e.g. executable binaries in Docker image)
                var handle = File.OpenHandle(openPath, mode, FileAccess.Read, share);
                linuxFile.PrivateData = handle;
            }
        }
    }

    public override void Release(LinuxFile linuxFile)
    {
        _ = FlushDirtyDataIfNeeded(linuxFile);
        FlushHandleToDiskIfNeeded(linuxFile);
        if (linuxFile.PrivateData is SafeFileHandle handle)
        {
            Flock(linuxFile, LinuxConstants.LOCK_UN);
            handle.Dispose();
            linuxFile.PrivateData = null;
        }
    }

    public override void Sync(LinuxFile linuxFile)
    {
        _ = FlushDirtyDataIfNeeded(linuxFile);
        FlushHandleToDiskIfNeeded(linuxFile);
    }

    private bool HasDirtyPageCachePages()
    {
        lock (_dirtyPageLock)
        {
            if (_dirtyPageIndexes.Count != 0)
                return true;
        }

        return Mapping?.SnapshotPageStates().Any(static state => state.Dirty) == true;
    }

    internal bool FlushDirtyDataIfNeeded(LinuxFile? linuxFile)
    {
        if (Type != InodeType.File || !HasDirtyPageCachePages())
            return true;

        var rc = WritePages(linuxFile, new WritePagesRequest(0, long.MaxValue, true));
        if (rc < 0)
            return false;

        return !HasDirtyPageCachePages();
    }

    private static void FlushHandleToDiskIfNeeded(LinuxFile? linuxFile)
    {
        if (linuxFile?.PrivateData is SafeFileHandle handle)
            RandomAccess.FlushToDisk(handle);
    }

    private int BackendRead(LinuxFile? linuxFile, Span<byte> buffer, long offset)
    {
        if (Type == InodeType.Directory) return 0;

        if (linuxFile?.PrivateData is SafeFileHandle handle)
            // RandomAccess.Read is thread-safe and doesn't require locking
            // It uses the offset parameter directly instead of shared Position state
            return RandomAccess.Read(handle, buffer, offset);

        // Fallback for unopened files
        using var tempHandle = File.OpenHandle(ResolveHostPath(linuxFile), FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite);
        return RandomAccess.Read(tempHandle, buffer, offset);
    }

    protected internal override int ReadSpan(LinuxFile? linuxFile, Span<byte> buffer, long offset)
    {
        return ReadWithPageCache(linuxFile, buffer, offset, BackendRead);
    }

    private int BackendWrite(LinuxFile? linuxFile, ReadOnlySpan<byte> buffer, long offset)
    {
        if (Type == InodeType.Directory) return -(int)Errno.EISDIR;
        var append = ((linuxFile?.Flags ?? 0) & FileFlags.O_APPEND) != 0;

        if (linuxFile?.PrivateData is SafeFileHandle handle)
        {
            if (append)
            {
                lock (handle)
                {
                    var writeOffset = RandomAccess.GetLength(handle);
                    RandomAccess.Write(handle, buffer, writeOffset);
                    Size = (ulong)RandomAccess.GetLength(handle);
                }
            }
            else
            {
                RandomAccess.Write(handle, buffer, offset);
                Size = (ulong)RandomAccess.GetLength(handle);
            }

            CTime = DateTime.Now;
            return buffer.Length;
        }

        // Fallback for unopened files
        using var tempHandle = File.OpenHandle(ResolveHostPath(linuxFile), FileMode.Open, FileAccess.Write,
            FileShare.ReadWrite);
        if (append)
        {
            lock
                (tempHandle) // Note: locking on a local 'using' handle is less effective across threads, but preserves the logic
            {
                var tempWriteOffset = RandomAccess.GetLength(tempHandle);
                RandomAccess.Write(tempHandle, buffer, tempWriteOffset);
                Size = (ulong)RandomAccess.GetLength(tempHandle);
            }
        }
        else
        {
            RandomAccess.Write(tempHandle, buffer, offset);
            Size = (ulong)RandomAccess.GetLength(tempHandle);
        }

        CTime = DateTime.Now;
        return buffer.Length;
    }

    protected internal override int WriteSpan(LinuxFile? linuxFile, ReadOnlySpan<byte> buffer, long offset)
    {
        var originalSize = (long)Size;
        var rc = WriteWithPageCache(linuxFile, buffer, offset, BackendWrite);
        if (rc > 0 && linuxFile != null && offset + rc > originalSize)
        {
            var startPage = offset / LinuxConstants.PageSize;
            var endPage = (offset + rc - 1) / LinuxConstants.PageSize;
            var syncRc = WritePages(linuxFile, new WritePagesRequest(startPage, endPage, true));
            if (syncRc < 0)
                return syncRc;
        }

        return rc;
    }

    protected override int AopsReadPage(LinuxFile? linuxFile, PageIoRequest request, Span<byte> pageBuffer)
    {
        if (request.Length < 0 || request.Length > pageBuffer.Length)
            return -(int)Errno.EINVAL;
        pageBuffer.Clear();
        if (request.Length == 0) return 0;
        var rc = BackendRead(linuxFile, pageBuffer[..request.Length], request.FileOffset);
        if (rc < 0)
            return rc;
        if (rc != request.Length)
            return -(int)Errno.EIO;

        return 0;
    }

    internal override void OnMappingPageReleased(uint pageIndex, InodePageRecord record)
    {
        lock (_dirtyPageLock)
        {
            _dirtyPageIndexes.Remove(pageIndex);
        }
    }

    internal override int SyncMappedPage(LinuxFile? linuxFile, AddressSpace mapping,
        PageSyncRequest request, InodePageRecord record)
    {
        _ = linuxFile;
        _ = mapping;
        _ = record;
        lock (_mappedCacheLock)
        {
            return _mappedPageCache?.TryFlushPage(request.PageIndex) == true ? 0 : -(int)Errno.EIO;
        }
    }

    internal override void CompleteCachedPageSync(LinuxFile? linuxFile, AddressSpace mapping, uint pageIndex,
        InodePageRecord? record)
    {
        lock (_dirtyPageLock)
        {
            _dirtyPageIndexes.Remove(pageIndex);
        }

        base.CompleteCachedPageSync(linuxFile, mapping, pageIndex, record);
    }

    protected override int AopsReadahead(LinuxFile? linuxFile, ReadaheadRequest request)
    {
        if (request.PageCount <= 0 || Mapping == null) return 0;
        for (var i = 0; i < request.PageCount; i++)
        {
            var pageIndex = request.StartPageIndex + i;
            if (Mapping.PeekPage((uint)pageIndex) != IntPtr.Zero) continue;
            var fileOffset = pageIndex * LinuxConstants.PageSize;
            var readLen = (int)Math.Min(LinuxConstants.PageSize, Math.Max(0, (long)Size - fileOffset));
            var ptr = AcquireMappingPage(linuxFile, (uint)pageIndex, fileOffset, PageCacheAccessMode.Read,
                readLen);

            if (ptr == IntPtr.Zero) return 0;
        }

        return 0;
    }

    protected override int AopsWritePage(LinuxFile? linuxFile, PageIoRequest request, ReadOnlySpan<byte> pageBuffer,
        bool sync)
    {
        if (request.Length < 0 || request.Length > pageBuffer.Length)
            return -(int)Errno.EINVAL;
        if (request.Length == 0) return 0;
        if (!sync) return 0;

        int rc;
        SuperBlock.MemoryContext.AddressSpacePolicy.BeginAddressSpaceWriteback();
        try
        {
            rc = BackendWrite(linuxFile, pageBuffer[..request.Length], request.FileOffset);
        }
        finally
        {
            SuperBlock.MemoryContext.AddressSpacePolicy.EndAddressSpaceWriteback();
        }

        if (rc < 0) return rc;
        lock (_dirtyPageLock)
        {
            _dirtyPageIndexes.Remove(request.PageIndex);
        }

        if (request.PageIndex >= 0 && request.PageIndex <= uint.MaxValue)
            Mapping?.ClearDirty((uint)request.PageIndex);
        FlushHandleToDiskIfNeeded(linuxFile);
        return 0;
    }

    protected override int AopsWritePages(LinuxFile? linuxFile, WritePagesRequest request)
    {
        if (!request.Sync) return 0;
        if (Mapping == null) return 0;

        List<long> toFlush;
        lock (_dirtyPageLock)
        {
            toFlush = _dirtyPageIndexes
                .Where(i => i >= request.StartPageIndex && i <= request.EndPageIndex)
                .ToList();
        }

        foreach (var pageIndex in toFlush)
        {
            var pagePtr = Mapping.PeekPage((uint)pageIndex);
            if (pagePtr == IntPtr.Zero)
            {
                lock (_dirtyPageLock)
                {
                    _dirtyPageIndexes.Remove(pageIndex);
                }

                if (pageIndex >= 0 && pageIndex <= uint.MaxValue)
                    Mapping.ClearDirty((uint)pageIndex);
                continue;
            }

            var fileOffset = pageIndex * LinuxConstants.PageSize;
            var remaining = (long)Size - fileOffset;
            if (remaining <= 0)
            {
                lock (_dirtyPageLock)
                {
                    _dirtyPageIndexes.Remove(pageIndex);
                }

                if (pageIndex >= 0 && pageIndex <= uint.MaxValue)
                    Mapping.ClearDirty((uint)pageIndex);
                continue;
            }

            var writeLen = (int)Math.Min(LinuxConstants.PageSize, remaining);
            unsafe
            {
                ReadOnlySpan<byte> pageData = new((void*)pagePtr, LinuxConstants.PageSize);
                int rc;
                SuperBlock.MemoryContext.AddressSpacePolicy.BeginAddressSpaceWriteback();
                try
                {
                    rc = BackendWrite(linuxFile, pageData[..writeLen], fileOffset);
                }
                finally
                {
                    SuperBlock.MemoryContext.AddressSpacePolicy.EndAddressSpaceWriteback();
                }

                if (rc < 0) return rc;
            }

            lock (_dirtyPageLock)
            {
                _dirtyPageIndexes.Remove(pageIndex);
            }

            if (pageIndex >= 0 && pageIndex <= uint.MaxValue)
                Mapping.ClearDirty((uint)pageIndex);
        }

        FlushHandleToDiskIfNeeded(linuxFile);
        return 0;
    }

    protected override int AopsSetPageDirty(long pageIndex)
    {
        lock (_dirtyPageLock)
        {
            _dirtyPageIndexes.Add(pageIndex);
        }

        return 0;
    }

    public override int Truncate(long size)
    {
        if (size < 0) return -(int)Errno.EINVAL;
        using var handle = File.OpenHandle(ResolveHostPath(), FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        RandomAccess.SetLength(handle, size);
        lock (_mappedCacheLock)
        {
            _mappedPageCache?.Truncate(size);
        }

        if (Mapping != null)
        {
            Mapping.TruncateToSize(size);
            var firstDroppedPage = (size + LinuxConstants.PageOffsetMask) / LinuxConstants.PageSize;
            lock (_dirtyPageLock)
            {
                _dirtyPageIndexes.RemoveWhere(i => i >= firstDroppedPage);
            }
        }

        Size = (ulong)size;
        MTime = DateTime.Now;
        CTime = MTime;
        return 0;
    }

    public override bool TryAcquireMappedPageHandle(LinuxFile? linuxFile, long pageIndex, long absoluteFileOffset,
        bool writable, out BackingPageHandle backingPageHandle)
    {
        backingPageHandle = default;
        if (Type != InodeType.File) return false;
        if (absoluteFileOffset < 0) return false;
        if ((absoluteFileOffset & LinuxConstants.PageOffsetMask) != 0) return false;

        lock (_mappedCacheLock)
        {
            _mappedPageCache ??= new MappedFilePageCache(
                ResolveHostPath(linuxFile),
                SuperBlock.MemoryContext.HostMemoryMapGeometry);
            if (!_mappedPageCache.TryAcquirePageLease(
                    absoluteFileOffset / LinuxConstants.PageSize,
                    (long)Size,
                    writable,
                    out var pointer,
                    out var releaseToken))
                return false;

            backingPageHandle = BackingPageHandle.CreateOwned(pointer, this, releaseToken);
            return true;
        }
    }

    protected internal override void ReleaseMappedPageHandle(long releaseToken)
    {
        lock (_mappedCacheLock)
        {
            _mappedPageCache?.ReleasePageLease(releaseToken);
        }
    }

    public override bool TryFlushMappedPage(LinuxFile? linuxFile, long pageIndex)
    {
        lock (_mappedCacheLock)
        {
            if (_mappedPageCache?.TryFlushPage(pageIndex) != true)
                return false;
        }

        lock (_dirtyPageLock)
        {
            _dirtyPageIndexes.Remove(pageIndex);
        }

        if (pageIndex >= 0 && pageIndex <= uint.MaxValue)
            Mapping?.ClearDirty((uint)pageIndex);
        FlushHandleToDiskIfNeeded(linuxFile);
        return true;
    }

    internal FilePageBackendDiagnostics GetMappedPageCacheDiagnostics()
    {
        lock (_mappedCacheLock)
        {
            return _mappedPageCache?.GetDiagnostics() ?? default;
        }
    }

    protected override void OnEvictCache()
    {
        base.OnEvictCache();
        ((HostSuperBlock)SuperBlock).UnregisterInodeIdentity(this);
        lock (_mappedCacheLock)
        {
            _mappedPageCache?.Dispose();
            _mappedPageCache = null;
        }
    }

    protected override void OnFinalizeDelete()
    {
        ((HostSuperBlock)SuperBlock).UnregisterInodeIdentity(this);
        base.OnFinalizeDelete();
    }

    public override List<DirectoryEntry> GetEntries()
    {
        var list = new List<DirectoryEntry>();
        if (Type != InodeType.Directory) return list;

        list.Add(new DirectoryEntry { Name = FsName.FromString("."), Ino = Ino, Type = InodeType.Directory });
        list.Add(new DirectoryEntry { Name = FsName.FromString(".."), Ino = Ino, Type = InodeType.Directory });

        var entries = Directory.GetFileSystemEntries(ResolveHostPath());
        var sb = (HostSuperBlock)SuperBlock;
        foreach (var entryPath in entries)
        {
            var name = Path.GetFileName(entryPath);
            if (name == HostfsMetadataStore.MetaDirName) continue;
            if (!HostSuperBlock.TryCreateFsNameFromHostName(name, out var encodedName))
                continue;
            var dentry = sb.GetDentry(entryPath, encodedName, Dentries.Count > 0 ? Dentries[0] : null);
            if (dentry != null)
                list.Add(new DirectoryEntry
                {
                    Name = encodedName,
                    Ino = dentry.Inode!.Ino,
                    Type = dentry.Inode.Type
                });
        }

        return list;
    }

    public override int SetXAttr(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value, int flags)
    {
        const int XATTR_CREATE = 1;
        const int XATTR_REPLACE = 2;
        if (!TryCreateFsName(name, out var attrName))
            return -(int)Errno.EINVAL;
        lock (_xattrLock)
        {
            EnsureXAttrsLoaded();
            var exists = _xattrs!.TryGetValue(attrName, out _);
            if ((flags & XATTR_CREATE) != 0 && exists) return -(int)Errno.EEXIST;
            if ((flags & XATTR_REPLACE) != 0 && !exists) return -(int)Errno.ENODATA;
            _xattrs!.Set(attrName, value.ToArray());
            PersistXAttrs();
            return 0;
        }
    }

    public override int SetXAttr(string name, ReadOnlySpan<byte> value, int flags)
    {
        ArgumentNullException.ThrowIfNull(name);
        return SetXAttr(FsEncoding.EncodeUtf8(name), value, flags);
    }

    public override int GetXAttr(ReadOnlySpan<byte> name, Span<byte> value)
    {
        if (!TryCreateFsName(name, out var attrName))
            return -(int)Errno.EINVAL;
        lock (_xattrLock)
        {
            EnsureXAttrsLoaded();
            if (!_xattrs!.TryGetValue(attrName, out var data)) return -(int)Errno.ENODATA;
            if (value.Length == 0) return data.Length;
            if (value.Length < data.Length) return -(int)Errno.ERANGE;
            data.CopyTo(value);
            return data.Length;
        }
    }

    public override int GetXAttr(string name, Span<byte> value)
    {
        ArgumentNullException.ThrowIfNull(name);
        return GetXAttr(FsEncoding.EncodeUtf8(name), value);
    }

    public override int ListXAttr(Span<byte> list)
    {
        lock (_xattrLock)
        {
            EnsureXAttrsLoaded();
            var names = _xattrs!.Select(static kv => kv.Key).OrderBy(static k => k, FsName.BytewiseComparer).ToArray();
            var required = 0;
            foreach (var n in names)
                required += n.Length + 1;
            if (list.Length == 0) return required;
            if (list.Length < required) return -(int)Errno.ERANGE;

            var off = 0;
            foreach (var n in names)
            {
                n.Bytes.CopyTo(list[off..]);
                off += n.Length;
                list[off++] = 0;
            }

            return required;
        }
    }

    public override int RemoveXAttr(ReadOnlySpan<byte> name)
    {
        if (!TryCreateFsName(name, out var attrName))
            return -(int)Errno.EINVAL;
        lock (_xattrLock)
        {
            EnsureXAttrsLoaded();
            if (!_xattrs!.Remove(attrName)) return -(int)Errno.ENODATA;
            PersistXAttrs();
            return 0;
        }
    }

    public override int RemoveXAttr(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return RemoveXAttr(FsEncoding.EncodeUtf8(name));
    }

    private void EnsureXAttrsLoaded()
    {
        if (_xattrs != null) return;
        var sb = (HostSuperBlock)SuperBlock;
        _xattrs = sb.MetadataStore.LoadXAttrs(this, ResolveHostPath());
    }

    private static bool TryDecodeHostComponent(ReadOnlySpan<byte> name, out FsName componentName, out string decoded)
    {
        if (!FsEncoding.TryDecodeUtf8(name, out decoded))
        {
            componentName = default;
            return false;
        }

        try
        {
            componentName = FsName.FromBytes(name);
            return true;
        }
        catch (ArgumentException)
        {
            componentName = default;
            return false;
        }
    }

    private static bool TryDecodeHostComponent(FsName name, out string decoded)
    {
        return FsEncoding.TryDecodeUtf8(name.Bytes, out decoded);
    }

    private static bool TryCreateFsName(ReadOnlySpan<byte> name, out FsName fsName)
    {
        if (!FsEncoding.IsValidUtf8(name))
        {
            fsName = default;
            return false;
        }

        try
        {
            fsName = FsName.FromBytes(name);
            return true;
        }
        catch (ArgumentException)
        {
            fsName = default;
            return false;
        }
    }

    private int ComputeInitialLinkCount()
    {
        if (Type != InodeType.Directory) return 1;

        var nlink = 2; // '.' and '..'
        try
        {
            var sb = (HostSuperBlock)SuperBlock;
            foreach (var entryPath in Directory.EnumerateFileSystemEntries(ResolveHostPath()))
            {
                var name = Path.GetFileName(entryPath);
                if (string.Equals(name, HostfsMetadataStore.MetaDirName, StringComparison.Ordinal))
                    continue;

                if (sb.TryResolveVisibleNode(entryPath, out _, out var entryType) && entryType == InodeType.Directory)
                    nlink++;
            }
        }
        catch
        {
            // Keep minimal valid directory nlink when host probing fails.
        }

        return nlink;
    }

    private void PersistXAttrs()
    {
        var sb = (HostSuperBlock)SuperBlock;
        sb.MetadataStore.SaveXAttrs(this, ResolveHostPath(), _xattrs!);
    }

    internal void ObservePath(string hostPath)
    {
        HostPath = hostPath;
    }

    internal void ForgetPath(string hostPath)
    {
        var normalized = Path.GetFullPath(hostPath);
        lock (Lock)
        {
            _aliasPaths.Remove(normalized);
            if (!string.Equals(_hostPath, normalized, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
                return;

            foreach (var path in _aliasPaths)
            {
                _hostPath = path;
                return;
            }
        }
    }

    internal void UpdateLinkCountFromHost(int hostNlink, string reason)
    {
        SetInitialLinkCount(Math.Max(0, hostNlink), reason);
    }

    private string ResolveHostPath(LinuxFile? linuxFile = null)
    {
        var sb = (HostSuperBlock)SuperBlock;
        if (sb.TryGetPathForDentry(linuxFile?.Dentry, out var dentryPath))
            return dentryPath;
        return HostPath;
    }

    private static bool PathExists(string path)
    {
        return HostInodeIdentityResolver.TryProbe(path, out _);
    }
}
