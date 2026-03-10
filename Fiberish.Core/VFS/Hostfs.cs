using System.IO;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fiberish.Memory;
using Fiberish.Native;
using Microsoft.Win32.SafeHandles;

namespace Fiberish.VFS;

public class Hostfs : FileSystem
{
    public Hostfs(DeviceNumberManager? devManager = null) : base(devManager)
    {
        Name = "hostfs";
    }

    public override SuperBlock ReadSuper(FileSystemType fsType, int flags, string devName, object? data)
    {
        var opts = HostfsMountOptions.Parse(data as string);
        var sb = new HostSuperBlock(fsType, devName, opts, DevManager); // devName is the root path on host
        var rootDentry = sb.GetDentry(devName, "/", null) ??
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
    private readonly Dictionary<HostInodeKey, HostInode> _inodeByIdentity = [];
    private readonly Dictionary<HostInode, HostInodeKey> _identityByInode = [];
    private ulong _nextIno = 1;

    public HostSuperBlock(FileSystemType type, string hostRoot, HostfsMountOptions options, DeviceNumberManager? devManager = null) : base(devManager)
    {
        Type = type;
        HostRoot = hostRoot;
        Options = options;
        MetadataStore = new HostfsMetadataStore(HostRoot, !options.MetadataLess);
    }

    public string HostRoot { get; }
    public HostfsMountOptions Options { get; }
    internal HostfsMetadataStore MetadataStore { get; }

    public Dentry? GetDentry(string hostPath, string name, Dentry? parent)
    {
        var normalizedPath = NormalizeHostPath(hostPath);
        lock (Lock)
        {
            if (_dentryCache.TryGetValue(normalizedPath, out var dentry))
            {
                if (PathExistsOnHost(normalizedPath))
                {
                    if (parent != null)
                        dentry.Parent = parent;
                    return dentry;
                }

                RemoveDentryNoLock(normalizedPath, recursive: true);
            }
        }

        if (!TryClassifyPath(normalizedPath, out var nodeType))
            return null;

        var identity = HostInodeIdentityResolver.Resolve(normalizedPath, out var hostNlink);
        var effectiveNlink = nodeType == InodeType.Directory ? null : hostNlink;
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
        MetadataStore.ApplyToInode(normalizedPath, inode);
        if (effectiveNlink.HasValue)
            inode.UpdateLinkCountFromHost(effectiveNlink.Value, "HostSuperBlock.GetDentry");

        var created = new Dentry(name, inode, parent, this);
        lock (Lock)
        {
            IndexPathNoLock(normalizedPath, created);
        }
        return created;
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
                RemovePathNoLock(hit.Key, recursive: false);
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
            RemoveDentryNoLock(normalizedPath, recursive: true);
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

    private static bool PathExistsOnHost(string hostPath)
    {
        var info = new FileInfo(hostPath);
        return info.LinkTarget != null || Directory.Exists(hostPath) || File.Exists(hostPath);
    }

    public void InstantiateDentry(Dentry dentry, string hostPath, bool isDir, int mode = 0)
    {
        var normalizedPath = NormalizeHostPath(hostPath);
        var type = isDir ? InodeType.Directory : InodeType.File;
        if (new FileInfo(normalizedPath).LinkTarget != null)
            type = InodeType.Symlink;

        var identity = HostInodeIdentityResolver.Resolve(normalizedPath, out var hostNlink);
        var effectiveNlink = type == InodeType.Directory ? null : hostNlink;
        HostInode inode;
        lock (Lock)
        {
            if (_inodeByIdentity.TryGetValue(identity, out var existing) && !existing.IsCacheEvicted && !existing.IsFinalized)
            {
                inode = existing;
            }
            else
            {
                inode = CreateHostInodeLocked(normalizedPath, type, identity, effectiveNlink);
            }
        }

        if (mode != 0) inode.Mode = Options.ApplyModeMask(isDir, mode);
        inode.ObservePath(normalizedPath);
        MetadataStore.ApplyToInode(normalizedPath, inode);
        if (effectiveNlink.HasValue)
            inode.UpdateLinkCountFromHost(effectiveNlink.Value, "HostSuperBlock.InstantiateDentry");

        dentry.Instantiate(inode);
        lock (Lock)
        {
            IndexPathNoLock(normalizedPath, dentry);
        }

        dentry.Parent?.CacheChild(dentry, "HostSuperBlock.InstantiateDentry");
    }

    public long DropDentryCache()
    {
        List<Dentry> candidates;
        lock (Lock)
        {
            candidates = _dentryCache.Values
                .Distinct()
                .Where(dentry => !ReferenceEquals(dentry, Root) && !dentry.IsMounted)
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
                .Where(kv => !ReferenceEquals(kv.Value, Root) && !kv.Value.IsMounted)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var key in staleKeys)
                RemovePathNoLock(key, recursive: false);
        }

        return dropped;
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

    private static bool TryClassifyPath(string hostPath, out InodeType type)
    {
        if (new FileInfo(hostPath).LinkTarget != null)
        {
            type = InodeType.Symlink;
            return true;
        }

        if (Directory.Exists(hostPath))
        {
            type = InodeType.Directory;
            return true;
        }

        if (File.Exists(hostPath))
        {
            type = InodeType.File;
            return true;
        }

        type = InodeType.Unknown;
        return false;
    }

    private HostInode CreateHostInodeLocked(string normalizedPath, InodeType type, HostInodeKey identity, int? hostNlink)
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
            RemovePathNoLock(path, recursive: false);

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
            RemovePathNoLock(path, recursive: false);
    }

    private void RemovePathNoLock(string hostPath, bool recursive)
    {
        if (recursive)
        {
            RemoveDentryNoLock(hostPath, recursive: true);
            return;
        }

        if (!_dentryCache.Remove(hostPath, out var dentry))
            return;

        if (_dentryPathById.TryGetValue(dentry.Id, out var mappedPath) &&
            string.Equals(mappedPath, hostPath, PathComparison))
            _dentryPathById.Remove(dentry.Id);

        if (dentry.Inode is HostInode hostInode)
            hostInode.ForgetPath(hostPath);
    }
}

internal readonly record struct HostInodeKey(string Scheme, ulong Value0, ulong Value1, string? FallbackPath)
{
    public static HostInodeKey Unix(ulong dev, ulong ino) => new("unix", dev, ino, null);
    public static HostInodeKey Windows(ulong volumeSerial, ulong fileId) => new("windows", volumeSerial, fileId, null);
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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out BY_HANDLE_FILE_INFORMATION lpFileInformation);

    public static HostInodeKey Resolve(string hostPath, out int? hostLinkCount)
    {
        var normalizedPath = Path.GetFullPath(hostPath);
        if (TryResolveUnix(normalizedPath, out var unixKey, out var unixNlink))
        {
            hostLinkCount = unixNlink;
            return unixKey;
        }

        if (TryResolveWindows(normalizedPath, out var windowsKey, out var windowsNlink))
        {
            hostLinkCount = windowsNlink;
            return windowsKey;
        }

        hostLinkCount = null;
        return HostInodeKey.Fallback(normalizedPath);
    }

    private static bool TryResolveUnix(string hostPath, out HostInodeKey key, out int hostLinkCount)
    {
        key = default;
        hostLinkCount = 0;
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return false;

        var statPath = OperatingSystem.IsMacOS() ? "/usr/bin/stat" : "stat";
        var psi = new ProcessStartInfo
        {
            FileName = statPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        if (OperatingSystem.IsMacOS())
        {
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("%d %i %l");
        }
        else
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("%d %i %h");
        }

        psi.ArgumentList.Add(hostPath);

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit();
            if (proc.ExitCode != 0 || string.IsNullOrEmpty(output)) return false;

            var parts = output.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return false;
            if (!ulong.TryParse(parts[0], out var dev)) return false;
            if (!ulong.TryParse(parts[1], out var ino)) return false;
            if (!int.TryParse(parts[2], out hostLinkCount)) return false;
            if (hostLinkCount < 0) hostLinkCount = 0;

            key = HostInodeKey.Unix(dev, ino);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryResolveWindows(string hostPath, out HostInodeKey key, out int hostLinkCount)
    {
        key = default;
        hostLinkCount = 0;
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            using var handle = File.OpenHandle(
                hostPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                FileOptions.None);

            if (!GetFileInformationByHandle(handle, out var fileInfo))
                return false;

            var fileId = ((ulong)fileInfo.NFileIndexHigh << 32) | fileInfo.NFileIndexLow;
            key = HostInodeKey.Windows(fileInfo.DwVolumeSerialNumber, fileId);
            hostLinkCount = checked((int)fileInfo.NNumberOfLinks);
            if (hostLinkCount < 0) hostLinkCount = 0;
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public sealed class HostfsMountOptions
{
    public int? MountUid { get; init; }
    public int? MountGid { get; init; }
    public int Umask { get; init; } = -1;
    public int Fmask { get; init; } = -1;
    public int Dmask { get; init; } = -1;
    public bool MetadataLess { get; init; }

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
        var metadataLess = false;

        var tokens = optionString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
        {
            var eq = token.IndexOf('=');
            if (eq <= 0 || eq == token.Length - 1)
            {
                var flag = token.Trim().ToLowerInvariant();
                if (flag is "metadataless" or "nometa")
                    metadataLess = true;
                continue;
            }

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
            }
        }

        return new HostfsMountOptions
        {
            MountUid = uid,
            MountGid = gid,
            Umask = umask,
            Fmask = fmask,
            Dmask = dmask,
            MetadataLess = metadataLess
        };
    }

    private static bool TryParseMask(string value, out int parsed)
    {
        try
        {
            if (value.StartsWith("0", StringComparison.Ordinal) && value.Length > 1)
            {
                parsed = Convert.ToInt32(value, 8);
            }
            else
            {
                parsed = int.Parse(value);
            }

            parsed &= 0x1FF;
            return true;
        }
        catch
        {
            parsed = 0;
            return false;
        }
    }
}

internal sealed class HostfsMetadataStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    public const string MetaDirName = ".fiberish_meta";
    private readonly string _hostRoot;
    private readonly bool _enabled;
    private readonly string _metaDir;

    public HostfsMetadataStore(string hostRoot, bool enabled = true)
    {
        _enabled = enabled;
        _hostRoot = Path.GetFullPath(hostRoot);
        // hostRoot can be either a directory mount or a single-file mount.
        // For file mounts, place sidecar metadata under the parent directory.
        var metaBase = Directory.Exists(_hostRoot)
            ? _hostRoot
            : (Path.GetDirectoryName(_hostRoot) ?? _hostRoot);
        _metaDir = Path.Combine(metaBase, MetaDirName);
        if (_enabled) Directory.CreateDirectory(_metaDir);
    }

    public bool IsMetaDirPath(string path)
    {
        if (!_enabled) return false;
        var full = Path.GetFullPath(path);
        return string.Equals(full, _metaDir, StringComparison.Ordinal);
    }

    public bool TryLoad(string hostPath, out HostfsMetaRecord record)
    {
        if (!_enabled)
        {
            record = default!;
            return false;
        }

        var metaPath = GetMetaPath(hostPath);
        if (!File.Exists(metaPath))
        {
            record = default!;
            return false;
        }

        try
        {
            var json = File.ReadAllText(metaPath);
            var parsed = JsonSerializer.Deserialize(json, HostfsJsonContext.Default.HostfsMetaRecord);
            if (parsed == null)
            {
                record = default!;
                return false;
            }

            record = parsed;
            return true;
        }
        catch
        {
            record = default!;
            return false;
        }
    }

    public void Save(string hostPath, HostfsMetaRecord record)
    {
        if (!_enabled) return;
        var normalizedPath = Path.GetFullPath(hostPath);
        var metaPath = GetMetaPath(normalizedPath);
        var toSave = record with { Path = normalizedPath };
        File.WriteAllText(metaPath, JsonSerializer.Serialize(toSave, HostfsJsonContext.Default.HostfsMetaRecord));
    }

    public void Remove(string hostPath)
    {
        if (!_enabled) return;
        var metaPath = GetMetaPath(hostPath);
        if (File.Exists(metaPath)) File.Delete(metaPath);
    }

    public void RenameMetadata(string oldPath, string newPath)
    {
        if (!_enabled) return;
        var oldFull = Path.GetFullPath(oldPath);
        var newFull = Path.GetFullPath(newPath);

        if (File.Exists(oldFull) || Directory.Exists(oldFull))
        {
            // The host path moved already. We only need metadata remap.
        }

        if (TryLoad(oldFull, out var direct))
        {
            Remove(oldFull);
            Save(newFull, direct with { Path = newFull });
        }

        // Directory rename: remap all descendants.
        var records = ReadAllRecords().ToList();
        foreach (var r in records)
        {
            if (!r.Path.StartsWith(oldFull + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            var suffix = r.Path[oldFull.Length..];
            var movedPath = newFull + suffix;
            Remove(r.Path);
            Save(movedPath, r with { Path = movedPath });
        }
    }

    public void ApplyToInode(string hostPath, HostInode inode)
    {
        if (!_enabled) return;
        if (!TryLoad(hostPath, out var meta)) return;
        if (meta.NodeType.HasValue)
        {
            inode.Type = meta.NodeType.Value;
            inode.Rdev = meta.Rdev ?? 0;
        }
    }

    public Dictionary<string, byte[]> LoadXAttrs(string hostPath)
    {
        if (!_enabled) return new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (!TryLoad(hostPath, out var meta) || meta.XAttrs == null)
            return new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var dict = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var kv in meta.XAttrs)
        {
            try
            {
                dict[kv.Key] = Convert.FromBase64String(kv.Value);
            }
            catch
            {
            }
        }

        return dict;
    }

    public void SaveXAttrs(string hostPath, Dictionary<string, byte[]> xattrs)
    {
        if (!_enabled) return;
        var hasExisting = TryLoad(hostPath, out var existing);
        var encoded = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in xattrs)
            encoded[kv.Key] = Convert.ToBase64String(kv.Value);
        var baseRecord = hasExisting ? existing : new HostfsMetaRecord(Path.GetFullPath(hostPath));
        Save(hostPath, baseRecord with { XAttrs = encoded });
    }

    public void SaveMknod(string hostPath, InodeType type, uint rdev)
    {
        if (!_enabled) return;
        var hasExisting = TryLoad(hostPath, out var existing);
        var baseRecord = hasExisting ? existing : new HostfsMetaRecord(Path.GetFullPath(hostPath));
        Save(hostPath, baseRecord with
        {
            NodeType = type,
            Rdev = rdev
        });
    }

    private IEnumerable<HostfsMetaRecord> ReadAllRecords()
    {
        if (!Directory.Exists(_metaDir)) yield break;
        foreach (var file in Directory.GetFiles(_metaDir, "*.json"))
        {
            HostfsMetaRecord? record = null;
            try
            {
                record = JsonSerializer.Deserialize(File.ReadAllText(file), HostfsJsonContext.Default.HostfsMetaRecord);
            }
            catch
            {
            }

            if (record != null) yield return record;
        }
    }

    private string GetMetaPath(string hostPath)
    {
        var full = Path.GetFullPath(hostPath);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full))).ToLowerInvariant();
        return Path.Combine(_metaDir, $"{key}.json");
    }
}

internal sealed record HostfsMetaRecord(
    string Path,
    InodeType? NodeType = null,
    uint? Rdev = null,
    Dictionary<string, string>? XAttrs = null);

internal static partial class HostfsOwnershipMapper
{
    private static readonly bool IsUnixLike = OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();
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
        var mappedUid = (hostUid == 0 || hostUid == CurrentHostUid) ? 0 : hostUid;
        var mappedGid = (hostGid == 0 || hostGid == CurrentHostGid) ? 0 : hostGid;
        inode.Uid = options.MountUid ?? mappedUid;
        inode.Gid = options.MountGid ?? mappedGid;
    }

    public static int SetGuestOwnership(HostInode inode, int uid, int gid)
    {
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

        var statPath = OperatingSystem.IsMacOS() ? "/usr/bin/stat" : "stat";
        var psi = new ProcessStartInfo
        {
            FileName = statPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        if (OperatingSystem.IsMacOS())
        {
            // %p includes file type + permissions in octal on macOS.
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("%u %g %p");
        }
        else
        {
            // %f is raw mode in hex on GNU stat.
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("%u %g %f");
        }

        psi.ArgumentList.Add(path);

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit();
            if (proc.ExitCode != 0 || string.IsNullOrEmpty(output)) return false;

            var parts = output.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return false;

            if (!int.TryParse(parts[0], out uid)) return false;
            if (!int.TryParse(parts[1], out gid)) return false;

            if (OperatingSystem.IsMacOS())
            {
                if (!TryParseInt(parts[2], 8, out var rawMode)) return false;
                modeBits = rawMode & 0xFFF;
            }
            else
            {
                if (!TryParseInt(parts[2], 16, out var rawMode)) return false;
                modeBits = rawMode & 0xFFF;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseInt(string s, int fromBase, out int value)
    {
        try
        {
            value = Convert.ToInt32(s, fromBase);
            return true;
        }
        catch
        {
            value = 0;
            return false;
        }
    }
}

public partial class HostInode : Inode
{
    private readonly object _xattrLock = new();
    private readonly object _dirtyPageLock = new();
    private readonly HashSet<long> _dirtyPageIndexes = [];
    private readonly object _mappedCacheLock = new();
    private MappedFilePageCache? _mappedPageCache;
    private string _hostPath;
    private readonly HashSet<string> _aliasPaths = new(OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal);
    private Dictionary<string, byte[]>? _xattrs;

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

    public override ulong Size
    {
        get
        {
            if (Type == InodeType.Directory) return 4096;
            if (Type == InodeType.Symlink) return 0;
            try
            {
                return (ulong)new FileInfo(HostPath).Length;
            }
            catch
            {
                return base.Size;
            }
        }
        set => base.Size = value;
    }

    public override DateTime MTime
    {
        get
        {
            try
            {
                return File.GetLastWriteTime(HostPath);
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
            try
            {
                return File.GetLastAccessTime(HostPath);
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
            try
            {
                return File.GetCreationTime(HostPath);
            }
            catch
            {
                return base.CTime;
            }
        }
        set => base.CTime = value;
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

    public override Dentry? Lookup(string name)
    {
        if (Type != InodeType.Directory) return null;
        if (name == HostfsMetadataStore.MetaDirName) return null;
        var subPath = Path.Combine(ResolveHostPath(), name);
        if (Dentries.Count == 0) return null;
        return ((HostSuperBlock)SuperBlock).GetDentry(subPath, name, Dentries[0]);
    }

    public override Dentry Create(Dentry dentry, int mode, int uid, int gid)
    {
        if (Type != InodeType.Directory) throw new InvalidOperationException("Not a directory");
        var subPath = Path.Combine(ResolveHostPath(), dentry.Name);
        if (File.Exists(subPath) || Directory.Exists(subPath)) throw new InvalidOperationException("Exists");

        using (File.Create(subPath))
        {
        } // Create empty file

        var sb = (HostSuperBlock)SuperBlock;
        sb.MetadataStore.Remove(subPath);
        sb.InstantiateDentry(dentry, subPath, false, mode);
        return dentry;
    }

    public override Dentry Mkdir(Dentry dentry, int mode, int uid, int gid)
    {
        if (Type != InodeType.Directory) throw new InvalidOperationException("Not a directory");
        var subPath = Path.Combine(ResolveHostPath(), dentry.Name);
        if (File.Exists(subPath) || Directory.Exists(subPath)) throw new InvalidOperationException("Exists");

        Directory.CreateDirectory(subPath);

        var sb = (HostSuperBlock)SuperBlock;
        sb.MetadataStore.Remove(subPath);
        sb.InstantiateDentry(dentry, subPath, true, mode);
        if (dentry.Inode != null)
            NamespaceOps.OnDirectoryCreated(this, dentry.Inode, "HostInode.Mkdir");
        return dentry;
    }

    public override Dentry Mknod(Dentry dentry, int mode, int uid, int gid, InodeType type, uint rdev)
    {
        if (Type != InodeType.Directory) throw new InvalidOperationException("Not a directory");
        var subPath = Path.Combine(ResolveHostPath(), dentry.Name);
        if (File.Exists(subPath) || Directory.Exists(subPath)) throw new InvalidOperationException("Exists");

        // Hostfs fallback: materialize a placeholder and persist node semantics in sidecar metadata.
        using (File.Create(subPath))
        {
        }

        var sb = (HostSuperBlock)SuperBlock;
        sb.MetadataStore.SaveMknod(subPath, type, rdev);
        sb.InstantiateDentry(dentry, subPath, false, mode);
        if (dentry.Inode != null)
        {
            dentry.Inode.Type = type;
            dentry.Inode.Rdev = rdev;
            dentry.Inode.Mode = mode;
            dentry.Inode.Uid = uid;
            dentry.Inode.Gid = gid;
        }

        return dentry;
    }

    public override void Unlink(string name)
    {
        var subPath = Path.Combine(ResolveHostPath(), name);
        var info = new FileInfo(subPath);
        var sb = (HostSuperBlock)SuperBlock;

        // unlink(2) deletes the directory entry itself and must not follow symlinks.
        if (info.LinkTarget != null || File.Exists(subPath))
        {
            var dentry = sb.GetDentry(subPath, name, null);
            File.Delete(subPath);
            sb.MetadataStore.Remove(subPath);
            NamespaceOps.OnEntryRemoved(dentry?.Inode, "HostInode.Unlink");
            dentry?.UnbindInode("HostInode.Unlink");
            if (Dentries.Count > 0)
                _ = Dentries[0].TryUncacheChild(name, "HostInode.Unlink", out _);
            sb.RemoveDentry(subPath);
            return;
        }

        if (Directory.Exists(subPath)) throw new InvalidOperationException("Is a directory");
        throw new FileNotFoundException("Entry not found", subPath);
    }

    public override void Rmdir(string name)
    {
        var subPath = Path.Combine(ResolveHostPath(), name);
        var info = new FileInfo(subPath);
        if (info.LinkTarget != null) throw new InvalidOperationException("Not a directory");
        if (!Directory.Exists(subPath)) throw new DirectoryNotFoundException(subPath);

        var sb = (HostSuperBlock)SuperBlock;
        var dentry = sb.GetDentry(subPath, name, null);
        Directory.Delete(subPath, false);
        sb.MetadataStore.Remove(subPath);
        if (dentry?.Inode != null)
        {
            NamespaceOps.OnDirectoryRemoved(this, dentry.Inode, "HostInode.Rmdir");
            dentry.UnbindInode("HostInode.Rmdir");
        }
        if (Dentries.Count > 0)
            _ = Dentries[0].TryUncacheChild(name, "HostInode.Rmdir", out _);
        sb.RemoveDentry(subPath);
    }

    public override void Rename(string oldName, Inode newParent, string newName)
    {
        if (Type != InodeType.Directory || newParent.Type != InodeType.Directory)
            throw new InvalidOperationException("Not a directory");

        var targetParent = (HostInode)newParent;
        var oldFullPath = Path.Combine(ResolveHostPath(), oldName);
        var newFullPath = Path.Combine(targetParent.ResolveHostPath(), newName);

        var sb = (HostSuperBlock)SuperBlock;
        var dentry = sb.GetDentry(oldFullPath, oldName, null) ??
                     throw new FileNotFoundException("Source not found", oldName);
        var sourceIsDirectory = dentry.Inode!.Type == InodeType.Directory;
        var movedAcrossParents = sourceIsDirectory && !ReferenceEquals(this, targetParent);

        // Handle overwrite
        if (File.Exists(newFullPath) || Directory.Exists(newFullPath) || new FileInfo(newFullPath).LinkTarget != null)
        {
            var targetDentry = sb.GetDentry(newFullPath, newName, null);
            if (targetDentry != null && ReferenceEquals(targetDentry.Inode, dentry.Inode))
                return;

            var targetIsSymlink = new FileInfo(newFullPath).LinkTarget != null;
            var targetIsDirectory = !targetIsSymlink && Directory.Exists(newFullPath);

            if (targetIsDirectory)
            {
                if (!sourceIsDirectory)
                    throw new InvalidOperationException("Is a directory");

                if (targetDentry != null && targetDentry.Children.Count > 0)
                    throw new InvalidOperationException("Directory not empty");

                Directory.Delete(newFullPath, false);
                if (targetDentry?.Inode != null)
                    NamespaceOps.OnDirectoryRemoved(targetParent, targetDentry.Inode,
                        "HostInode.Rename.overwrite-target-dir");
            }
            else
            {
                if (sourceIsDirectory)
                    throw new InvalidOperationException("Not a directory");

                File.Delete(newFullPath);
                NamespaceOps.OnRenameOverwrite(dentry.Inode, targetDentry?.Inode, "HostInode.Rename.overwrite-target");
            }
            targetDentry?.UnbindInode("HostInode.Rename.overwrite-target");
            if (targetParent.Dentries.Count > 0)
                _ = targetParent.Dentries[0].TryUncacheChild(newName, "HostInode.Rename.overwrite-target", out _);
            sb.RemoveDentry(newFullPath);
        }

        if (sourceIsDirectory)
            Directory.Move(oldFullPath, newFullPath);
        else
            File.Move(oldFullPath, newFullPath);

        sb.MetadataStore.RenameMetadata(oldFullPath, newFullPath);

        // Update cache and internal path
        sb.MoveDentry(oldFullPath, newFullPath, dentry);
        if (dentry.Inode is HostInode movedInode)
        {
            movedInode.ForgetPath(oldFullPath);
            movedInode.ObservePath(newFullPath);
        }
        if (Dentries.Count > 0)
            _ = Dentries[0].TryUncacheChild(oldName, "HostInode.Rename.old-parent", out _);
        dentry.Name = newName;
        if (targetParent.Dentries.Count > 0)
        {
            dentry.Parent = targetParent.Dentries[0];
            dentry.Parent.CacheChild(dentry, "HostInode.Rename.new-parent");
        }

        if (movedAcrossParents)
            NamespaceOps.OnDirectoryMovedAcrossParents(this, targetParent, "HostInode.Rename");
    }

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int link(string oldpath, string newpath);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int flock(int fd, int operation);

    public override int Flock(LinuxFile linuxFile, int operation)
    {
        if (linuxFile.PrivateData is SafeFileHandle handle)
        {
            int fd = handle.DangerousGetHandle().ToInt32();
            if (flock(fd, operation) != 0)
            {
                return -Marshal.GetLastPInvokeError();
            }

            return 0;
        }

        return -(int)Errno.EBADF;
    }

    public override Dentry Link(Dentry dentry, Inode oldInode)
    {
        if (Type != InodeType.Directory) throw new InvalidOperationException("Not a directory");
        if (oldInode is not HostInode hi) throw new InvalidOperationException("Not a host inode");

        var newPath = Path.Combine(ResolveHostPath(), dentry.Name);
        if (link(hi.ResolveHostPath(), newPath) != 0)
            throw new IOException($"link failed with error {Marshal.GetLastPInvokeError()}");

        var sb = (HostSuperBlock)SuperBlock;
        dentry.Instantiate(oldInode);
        NamespaceOps.OnLinkAdded(oldInode, "HostInode.Link");
        sb.AddDentry(newPath, dentry);
        if (Dentries.Count > 0)
            Dentries[0].CacheChild(dentry, "HostInode.Link");

        return dentry;
    }

    public override Dentry Symlink(Dentry dentry, string target, int uid, int gid)
    {
        if (Type != InodeType.Directory) throw new InvalidOperationException("Not a directory");
        var newPath = Path.Combine(ResolveHostPath(), dentry.Name);
        if (File.Exists(newPath) || Directory.Exists(newPath) || new FileInfo(newPath).LinkTarget != null)
            throw new InvalidOperationException("Exists");

        File.CreateSymbolicLink(newPath, target);
        var sb = (HostSuperBlock)SuperBlock;
        sb.MetadataStore.Remove(newPath);
        sb.InstantiateDentry(dentry, newPath, false); // symlinks don't really use mode in Create
        return dentry;
    }

    public override string Readlink()
    {
        var info = new FileInfo(ResolveHostPath());
        return info.LinkTarget ?? throw new IOException("Not a link or target missing");
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
            var hasTrunc = (linuxFile.Flags & FileFlags.O_TRUNC) != 0;

            if (hasCreate && hasExcl) mode = FileMode.CreateNew;
            else if (hasCreate && hasTrunc) mode = FileMode.Create;
            else if (hasCreate) mode = FileMode.OpenOrCreate;
            else if (hasTrunc) mode = FileMode.Truncate;

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
        if (linuxFile.PrivateData is SafeFileHandle handle)
        {
            Flock(linuxFile, LinuxConstants.LOCK_UN);
            handle.Dispose();
            linuxFile.PrivateData = null;
        }
    }

    public override void Sync(LinuxFile linuxFile)
    {
        if (linuxFile.PrivateData is SafeFileHandle handle)
            RandomAccess.FlushToDisk(handle);
    }

    private int BackendRead(LinuxFile? linuxFile, Span<byte> buffer, long offset)
    {
        if (Type == InodeType.Directory) return 0;

        if (linuxFile?.PrivateData is SafeFileHandle handle)
        {
            // RandomAccess.Read is thread-safe and doesn't require locking
            // It uses the offset parameter directly instead of shared Position state
            return RandomAccess.Read(handle, buffer, offset);
        }

        // Fallback for unopened files
        using var tempHandle = File.OpenHandle(ResolveHostPath(linuxFile), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return RandomAccess.Read(tempHandle, buffer, offset);
    }

    public override int Read(LinuxFile? linuxFile, Span<byte> buffer, long offset)
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
                    long writeOffset = RandomAccess.GetLength(handle);
                    RandomAccess.Write(handle, buffer, writeOffset);
                    Size = (ulong)RandomAccess.GetLength(handle);
                }
            }
            else
            {
                RandomAccess.Write(handle, buffer, offset);
                Size = (ulong)RandomAccess.GetLength(handle);
            }

            return buffer.Length;
        }

        // Fallback for unopened files
        using var tempHandle = File.OpenHandle(ResolveHostPath(linuxFile), FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
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

        return buffer.Length;
    }

    public override int Write(LinuxFile? linuxFile, ReadOnlySpan<byte> buffer, long offset)
    {
        return WriteWithPageCache(linuxFile, buffer, offset, BackendWrite);
    }

    protected override int AopsReadPage(LinuxFile? linuxFile, PageIoRequest request, Span<byte> pageBuffer)
    {
        if (request.Length < 0 || request.Length > pageBuffer.Length)
            return -(int)Errno.EINVAL;
        pageBuffer.Clear();
        if (request.Length == 0) return 0;
        var rc = BackendRead(linuxFile, pageBuffer[..request.Length], request.FileOffset);
        return rc < 0 ? rc : 0;
    }

    protected override int AopsReadahead(LinuxFile? linuxFile, ReadaheadRequest request)
    {
        if (request.PageCount <= 0 || PageCache == null) return 0;
        var page = new byte[LinuxConstants.PageSize];
        for (var i = 0; i < request.PageCount; i++)
        {
            var pageIndex = request.StartPageIndex + i;
            if (PageCache.GetPage((uint)pageIndex) != IntPtr.Zero) continue;

            var ptr = PageCache.GetOrCreatePage((uint)pageIndex, p =>
            {
                var n = BackendRead(linuxFile, page, pageIndex * LinuxConstants.PageSize);
                unsafe
                {
                    var dst = new Span<byte>((void*)p, LinuxConstants.PageSize);
                    dst.Clear();
                    if (n > 0) page.AsSpan(0, n).CopyTo(dst);
                }

                return n >= 0;
            }, out _, strictQuota: true, Fiberish.Memory.AllocationClass.Readahead);

            if (ptr == IntPtr.Zero) return 0;
        }

        return 0;
    }

    protected override int AopsWritePage(LinuxFile? linuxFile, PageIoRequest request, ReadOnlySpan<byte> pageBuffer, bool sync)
    {
        if (request.Length < 0 || request.Length > pageBuffer.Length)
            return -(int)Errno.EINVAL;
        if (request.Length == 0) return 0;
        if (!sync) return 0;

        int rc;
        GlobalPageCacheManager.BeginWritebackPages();
        try
        {
            rc = BackendWrite(linuxFile, pageBuffer[..request.Length], request.FileOffset);
        }
        finally
        {
            GlobalPageCacheManager.EndWritebackPages();
        }
        if (rc < 0) return rc;
        lock (_dirtyPageLock) _dirtyPageIndexes.Remove(request.PageIndex);
        if (request.PageIndex >= 0 && request.PageIndex <= uint.MaxValue)
            PageCache?.ClearDirty((uint)request.PageIndex);
        if (linuxFile != null) Sync(linuxFile);
        return 0;
    }

    protected override int AopsWritePages(LinuxFile? linuxFile, WritePagesRequest request)
    {
        if (!request.Sync) return 0;
        if (PageCache == null) return 0;

        List<long> toFlush;
        lock (_dirtyPageLock)
        {
            toFlush = _dirtyPageIndexes
                .Where(i => i >= request.StartPageIndex && i <= request.EndPageIndex)
                .ToList();
        }

        foreach (var pageIndex in toFlush)
        {
            var pagePtr = PageCache.GetPage((uint)pageIndex);
            if (pagePtr == IntPtr.Zero)
            {
                lock (_dirtyPageLock) _dirtyPageIndexes.Remove(pageIndex);
                if (pageIndex >= 0 && pageIndex <= uint.MaxValue)
                    PageCache.ClearDirty((uint)pageIndex);
                continue;
            }

            var fileOffset = pageIndex * LinuxConstants.PageSize;
            var remaining = (long)Size - fileOffset;
            if (remaining <= 0)
            {
                lock (_dirtyPageLock) _dirtyPageIndexes.Remove(pageIndex);
                if (pageIndex >= 0 && pageIndex <= uint.MaxValue)
                    PageCache.ClearDirty((uint)pageIndex);
                continue;
            }

            var writeLen = (int)Math.Min((long)LinuxConstants.PageSize, remaining);
            unsafe
            {
                ReadOnlySpan<byte> pageData = new((void*)pagePtr, LinuxConstants.PageSize);
                int rc;
                GlobalPageCacheManager.BeginWritebackPages();
                try
                {
                    rc = BackendWrite(linuxFile, pageData[..writeLen], fileOffset);
                }
                finally
                {
                    GlobalPageCacheManager.EndWritebackPages();
                }
                if (rc < 0) return rc;
            }

            lock (_dirtyPageLock) _dirtyPageIndexes.Remove(pageIndex);
            if (pageIndex >= 0 && pageIndex <= uint.MaxValue)
                PageCache.ClearDirty((uint)pageIndex);
        }

        if (linuxFile != null) Sync(linuxFile);
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
        if (PageCache != null)
        {
            PageCache.TruncateToSize(size);
            var firstDroppedPage = (size + LinuxConstants.PageOffsetMask) / LinuxConstants.PageSize;
            lock (_dirtyPageLock)
            {
                _dirtyPageIndexes.RemoveWhere(i => i >= firstDroppedPage);
            }
        }

        Size = (ulong)size;
        MTime = DateTime.Now;
        return 0;
    }

    public override bool TryAcquireMappedPageHandle(LinuxFile? linuxFile, long pageIndex, long absoluteFileOffset,
        out IPageHandle? pageHandle)
    {
        pageHandle = null;
        if (Type != InodeType.File) return false;
        if (absoluteFileOffset < 0) return false;
        if ((absoluteFileOffset & LinuxConstants.PageOffsetMask) != 0) return false;

        lock (_mappedCacheLock)
        {
            _mappedPageCache ??= new MappedFilePageCache(ResolveHostPath(linuxFile), writable: true);
            return _mappedPageCache.TryAcquirePageHandle(
                absoluteFileOffset / LinuxConstants.PageSize,
                (long)Size,
                out pageHandle);
        }
    }

    protected override void OnEvictCache()
    {
        ((HostSuperBlock)SuperBlock).UnregisterInodeIdentity(this);
        lock (_mappedCacheLock)
        {
            _mappedPageCache?.Dispose();
            _mappedPageCache = null;
        }

        base.OnEvictCache();
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

        list.Add(new DirectoryEntry { Name = ".", Ino = Ino, Type = InodeType.Directory });
        list.Add(new DirectoryEntry { Name = "..", Ino = Ino, Type = InodeType.Directory });

        var entries = Directory.GetFileSystemEntries(ResolveHostPath());
        var sb = (HostSuperBlock)SuperBlock;
        foreach (var entryPath in entries)
        {
            var name = Path.GetFileName(entryPath);
            if (name == HostfsMetadataStore.MetaDirName) continue;
            var dentry = sb.GetDentry(entryPath, name, Dentries.Count > 0 ? Dentries[0] : null);
            if (dentry != null)
                list.Add(new DirectoryEntry { Name = name, Ino = dentry.Inode!.Ino, Type = dentry.Inode.Type });
        }

        return list;
    }

    public override int SetXAttr(string name, ReadOnlySpan<byte> value, int flags)
    {
        const int XATTR_CREATE = 1;
        const int XATTR_REPLACE = 2;
        lock (_xattrLock)
        {
            EnsureXAttrsLoaded();
            var exists = _xattrs!.ContainsKey(name);
            if ((flags & XATTR_CREATE) != 0 && exists) return -(int)Errno.EEXIST;
            if ((flags & XATTR_REPLACE) != 0 && !exists) return -(int)Errno.ENODATA;
            _xattrs[name] = value.ToArray();
            PersistXAttrs();
            return 0;
        }
    }

    public override int GetXAttr(string name, Span<byte> value)
    {
        lock (_xattrLock)
        {
            EnsureXAttrsLoaded();
            if (!_xattrs!.TryGetValue(name, out var data)) return -(int)Errno.ENODATA;
            if (value.Length == 0) return data.Length;
            if (value.Length < data.Length) return -(int)Errno.ERANGE;
            data.CopyTo(value);
            return data.Length;
        }
    }

    public override int ListXAttr(Span<byte> list)
    {
        lock (_xattrLock)
        {
            EnsureXAttrsLoaded();
            var names = _xattrs!.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
            var required = 0;
            foreach (var n in names) required += Encoding.UTF8.GetByteCount(n) + 1;
            if (list.Length == 0) return required;
            if (list.Length < required) return -(int)Errno.ERANGE;

            var off = 0;
            foreach (var n in names)
            {
                var nlen = Encoding.UTF8.GetBytes(n, list[off..]);
                off += nlen;
                list[off++] = 0;
            }

            return required;
        }
    }

    public override int RemoveXAttr(string name)
    {
        lock (_xattrLock)
        {
            EnsureXAttrsLoaded();
            if (!_xattrs!.Remove(name)) return -(int)Errno.ENODATA;
            PersistXAttrs();
            return 0;
        }
    }

    private void EnsureXAttrsLoaded()
    {
        if (_xattrs != null) return;
        var sb = (HostSuperBlock)SuperBlock;
        _xattrs = sb.MetadataStore.LoadXAttrs(ResolveHostPath());
    }

    private int ComputeInitialLinkCount()
    {
        if (Type != InodeType.Directory) return 1;

        var nlink = 2; // '.' and '..'
        try
        {
            foreach (var entryPath in Directory.EnumerateFileSystemEntries(ResolveHostPath()))
            {
                var name = Path.GetFileName(entryPath);
                if (string.Equals(name, HostfsMetadataStore.MetaDirName, StringComparison.Ordinal))
                    continue;

                var info = new FileInfo(entryPath);
                if (info.LinkTarget != null)
                    continue; // symlink entries do not contribute to parent nlink
                if (Directory.Exists(entryPath))
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
        sb.MetadataStore.SaveXAttrs(ResolveHostPath(), _xattrs!);
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
        var info = new FileInfo(path);
        return info.LinkTarget != null || Directory.Exists(path) || File.Exists(path);
    }
}
