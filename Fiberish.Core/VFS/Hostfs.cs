using System.IO;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fiberish.Native;
using Microsoft.Win32.SafeHandles;

namespace Fiberish.VFS;

public class Hostfs : FileSystem
{
    public Hostfs()
    {
        Name = "hostfs";
    }

    public override SuperBlock ReadSuper(FileSystemType fsType, int flags, string devName, object? data)
    {
        var opts = HostfsMountOptions.Parse(data as string);
        var sb = new HostSuperBlock(fsType, devName, opts); // devName is the root path on host
        var rootDentry = sb.GetDentry(devName, "/", null) ??
                         throw new FileNotFoundException("Root path not found", devName);
        sb.Root = rootDentry;
        sb.Root.Parent = sb.Root;
        return sb;
    }
}

public class HostSuperBlock : SuperBlock
{
    private readonly Dictionary<string, Dentry> _dentryCache = [];
    private ulong _nextIno = 1;

    public HostSuperBlock(FileSystemType type, string hostRoot, HostfsMountOptions options)
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
        if (_dentryCache.TryGetValue(hostPath, out var dentry)) return dentry;

        // Check for symlink first (so we don't follow)
        // FileInfo.LinkTarget returns non-null for symlinks (including broken ones)
        if (new FileInfo(hostPath).LinkTarget != null)
        {
            var newInode = new HostInode(_nextIno++, this, hostPath, InodeType.Symlink);
            MetadataStore.ApplyToInode(hostPath, newInode);
            var newDentry = new Dentry(name, newInode, parent, this);
            _dentryCache[hostPath] = newDentry;
            return newDentry;
        }

        if (Directory.Exists(hostPath))
        {
            var newInode = new HostInode(_nextIno++, this, hostPath, InodeType.Directory);
            MetadataStore.ApplyToInode(hostPath, newInode);
            var newDentry = new Dentry(name, newInode, parent, this);
            _dentryCache[hostPath] = newDentry;
            return newDentry;
        }

        if (File.Exists(hostPath))
        {
            var newInode = new HostInode(_nextIno++, this, hostPath, InodeType.File);
            MetadataStore.ApplyToInode(hostPath, newInode);
            var newDentry = new Dentry(name, newInode, parent, this);
            _dentryCache[hostPath] = newDentry;
            return newDentry;
        }

        return null;
    }

    public override void WriteInode(Inode inode)
    {
    }

    public void MoveDentry(string oldPath, string newPath, Dentry dentry)
    {
        lock (Lock)
        {
            if (!string.IsNullOrEmpty(oldPath)) _dentryCache.Remove(oldPath);
            _dentryCache[newPath] = dentry;
        }
    }

    public void RemoveDentry(string hostPath)
    {
        lock (Lock)
        {
            _dentryCache.Remove(hostPath);
        }
    }

    public void InstantiateDentry(Dentry dentry, string hostPath, bool isDir, int mode = 0)
    {
        var type = isDir ? InodeType.Directory : InodeType.File;
        // Check for symlink
        if (new FileInfo(hostPath).LinkTarget != null) type = InodeType.Symlink;

        var inode = new HostInode(_nextIno++, this, hostPath, type);
        if (mode != 0) inode.Mode = Options.ApplyModeMask(isDir, mode);
        MetadataStore.ApplyToInode(hostPath, inode);
        dentry.Instantiate(inode);
        lock (Lock)
        {
            _dentryCache[hostPath] = dentry;
        }

        if (dentry.Parent != null) dentry.Parent.Children[dentry.Name] = dentry;
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
    private Dictionary<string, byte[]>? _xattrs;

    public HostInode(ulong ino, SuperBlock sb, string hostPath, InodeType type)
    {
        Ino = ino;
        SuperBlock = sb;
        HostPath = hostPath;
        Type = type;
        var isDir = type == InodeType.Directory;
        Mode = isDir ? 0x1FF : 0x1B6; // 777 or 666
    }

    public string HostPath { get; set; }
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
        var subPath = Path.Combine(HostPath, name);
        if (Dentries.Count == 0) return null;
        return ((HostSuperBlock)SuperBlock).GetDentry(subPath, name, Dentries[0]);
    }

    public override Dentry Create(Dentry dentry, int mode, int uid, int gid)
    {
        if (Type != InodeType.Directory) throw new InvalidOperationException("Not a directory");
        var subPath = Path.Combine(HostPath, dentry.Name);
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
        var subPath = Path.Combine(HostPath, dentry.Name);
        if (File.Exists(subPath) || Directory.Exists(subPath)) throw new InvalidOperationException("Exists");

        Directory.CreateDirectory(subPath);

        var sb = (HostSuperBlock)SuperBlock;
        sb.MetadataStore.Remove(subPath);
        sb.InstantiateDentry(dentry, subPath, true, mode);
        return dentry;
    }

    public override Dentry Mknod(Dentry dentry, int mode, int uid, int gid, InodeType type, uint rdev)
    {
        if (Type != InodeType.Directory) throw new InvalidOperationException("Not a directory");
        var subPath = Path.Combine(HostPath, dentry.Name);
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
        var subPath = Path.Combine(HostPath, name);
        var info = new FileInfo(subPath);
        var sb = (HostSuperBlock)SuperBlock;

        // unlink(2) deletes the directory entry itself and must not follow symlinks.
        if (info.LinkTarget != null || File.Exists(subPath))
        {
            var dentry = sb.GetDentry(subPath, name, null);
            File.Delete(subPath);
            sb.MetadataStore.Remove(subPath);
            dentry?.Inode?.Put();
            sb.RemoveDentry(subPath);
            return;
        }

        if (Directory.Exists(subPath)) throw new InvalidOperationException("Is a directory");
        throw new FileNotFoundException("Entry not found", subPath);
    }

    public override void Rmdir(string name)
    {
        var subPath = Path.Combine(HostPath, name);
        var info = new FileInfo(subPath);
        if (info.LinkTarget != null) throw new InvalidOperationException("Not a directory");
        if (!Directory.Exists(subPath)) throw new DirectoryNotFoundException(subPath);

        var sb = (HostSuperBlock)SuperBlock;
        var dentry = sb.GetDentry(subPath, name, null);
        Directory.Delete(subPath, false);
        sb.MetadataStore.Remove(subPath);
        dentry?.Inode?.Put();
        sb.RemoveDentry(subPath);
    }

    public override void Rename(string oldName, Inode newParent, string newName)
    {
        if (Type != InodeType.Directory || newParent.Type != InodeType.Directory)
            throw new InvalidOperationException("Not a directory");

        var targetParent = (HostInode)newParent;
        var oldFullPath = Path.Combine(HostPath, oldName);
        var newFullPath = Path.Combine(targetParent.HostPath, newName);

        var sb = (HostSuperBlock)SuperBlock;
        var dentry = sb.GetDentry(oldFullPath, oldName, null) ??
                     throw new FileNotFoundException("Source not found", oldName);

        // Handle overwrite
        if (File.Exists(newFullPath) || Directory.Exists(newFullPath))
        {
            var targetDentry = sb.GetDentry(newFullPath, newName, null);
            if (Directory.Exists(newFullPath)) Directory.Delete(newFullPath, true);
            else File.Delete(newFullPath);
            targetDentry?.Inode?.Put();
            sb.RemoveDentry(newFullPath);
        }

        if (dentry.Inode!.Type == InodeType.Directory)
            Directory.Move(oldFullPath, newFullPath);
        else
            File.Move(oldFullPath, newFullPath);

        sb.MetadataStore.RenameMetadata(oldFullPath, newFullPath);

        // Update cache and internal path
        sb.MoveDentry(oldFullPath, newFullPath, dentry);
        ((HostInode)dentry.Inode).HostPath = newFullPath;
        dentry.Name = newName;
        if (targetParent.Dentries.Count > 0) dentry.Parent = targetParent.Dentries[0];
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

        var newPath = Path.Combine(HostPath, dentry.Name);
        if (link(hi.HostPath, newPath) != 0)
            throw new IOException($"link failed with error {Marshal.GetLastPInvokeError()}");

        var sb = (HostSuperBlock)SuperBlock;
        dentry.Instantiate(oldInode);
        lock (sb.Lock)
        {
            sb.MoveDentry("", newPath,
                dentry); // hack: we don't have an old host path for this new link yet in the cache
        }

        return dentry;
    }

    public override Dentry Symlink(Dentry dentry, string target, int uid, int gid)
    {
        if (Type != InodeType.Directory) throw new InvalidOperationException("Not a directory");
        var newPath = Path.Combine(HostPath, dentry.Name);
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
        var info = new FileInfo(HostPath);
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
            try
            {
                var handle = File.OpenHandle(HostPath, mode, access, share);
                linuxFile.PrivateData = handle;
            }
            catch (UnauthorizedAccessException) when (access == FileAccess.ReadWrite &&
                                                      (linuxFile.Flags & FileFlags.O_WRONLY) == 0 &&
                                                      (linuxFile.Flags & FileFlags.O_RDWR) == 0)
            {
                // Fallback for ReadOnly files (e.g. executable binaries in Docker image)
                var handle = File.OpenHandle(HostPath, mode, FileAccess.Read, share);
                linuxFile.PrivateData = handle;
            }
            catch (IOException) when (access == FileAccess.ReadWrite && (linuxFile.Flags & FileFlags.O_WRONLY) == 0 &&
                                      (linuxFile.Flags & FileFlags.O_RDWR) == 0)
            {
                // Fallback for ReadOnly files (e.g. executable binaries in Docker image)
                var handle = File.OpenHandle(HostPath, mode, FileAccess.Read, share);
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
        using var tempHandle = File.OpenHandle(HostPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
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
        using var tempHandle = File.OpenHandle(HostPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
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

    public override int ReadPage(LinuxFile? linuxFile, PageIoRequest request, Span<byte> pageBuffer)
    {
        if (request.Length < 0 || request.Length > pageBuffer.Length)
            return -(int)Errno.EINVAL;
        pageBuffer.Clear();
        if (request.Length == 0) return 0;
        var rc = BackendRead(linuxFile, pageBuffer[..request.Length], request.FileOffset);
        return rc < 0 ? rc : 0;
    }

    public override int Readahead(LinuxFile? linuxFile, ReadaheadRequest request)
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
            }, out _);

            if (ptr == IntPtr.Zero) return -(int)Errno.EIO;
        }

        return 0;
    }

    public override int WritePage(LinuxFile? linuxFile, PageIoRequest request, ReadOnlySpan<byte> pageBuffer, bool sync)
    {
        if (request.Length < 0 || request.Length > pageBuffer.Length)
            return -(int)Errno.EINVAL;
        if (request.Length == 0)
        {
            lock (_dirtyPageLock) _dirtyPageIndexes.Remove(request.PageIndex);
            return 0;
        }

        var rc = BackendWrite(linuxFile, pageBuffer[..request.Length], request.FileOffset);
        if (rc < 0) return rc;
        lock (_dirtyPageLock) _dirtyPageIndexes.Remove(request.PageIndex);
        if (sync && linuxFile != null) Sync(linuxFile);
        return 0;
    }

    public override int WritePages(LinuxFile? linuxFile, WritePagesRequest request)
    {
        if (request.Sync && linuxFile != null) Sync(linuxFile);
        lock (_dirtyPageLock)
        {
            _dirtyPageIndexes.RemoveWhere(i => i >= request.StartPageIndex && i <= request.EndPageIndex);
        }

        return 0;
    }

    public override int SetPageDirty(long pageIndex)
    {
        lock (_dirtyPageLock)
        {
            _dirtyPageIndexes.Add(pageIndex);
        }

        return 0;
    }

    public override int Truncate(long size)
    {
        using var handle = File.OpenHandle(HostPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        RandomAccess.SetLength(handle, size);
        Size = (ulong)size;
        return 0;
    }

    public override List<DirectoryEntry> GetEntries()
    {
        var list = new List<DirectoryEntry>();
        if (Type != InodeType.Directory) return list;

        list.Add(new DirectoryEntry { Name = ".", Ino = Ino, Type = InodeType.Directory });
        list.Add(new DirectoryEntry { Name = "..", Ino = Ino, Type = InodeType.Directory });

        var entries = Directory.GetFileSystemEntries(HostPath);
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
        _xattrs = sb.MetadataStore.LoadXAttrs(HostPath);
    }

    private void PersistXAttrs()
    {
        var sb = (HostSuperBlock)SuperBlock;
        sb.MetadataStore.SaveXAttrs(HostPath, _xattrs!);
    }
}
