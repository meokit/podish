using Fiberish.SilkFS;
using Fiberish.Memory;
using Fiberish.Native;

namespace Fiberish.VFS;

/// <summary>
/// Core-side VFS adapter for SilkFS.
/// SilkFS project stays storage-focused and independent from Core VFS types.
/// </summary>
public sealed class SilkFileSystem : FileSystem
{
    public SilkFileSystem(DeviceNumberManager? devManager = null) : base(devManager)
    {
        Name = "silkfs";
    }

    public override SuperBlock ReadSuper(FileSystemType fsType, int flags, string devName, object? data)
    {
        var options = SilkFsOptions.FromSource(devName);
        var repository = new SilkRepository(options);
        repository.Initialize();

        var sb = new SilkSuperBlock(fsType, repository, DevManager);
        sb.LoadFromMetadata();
        return sb;
    }
}

public sealed class SilkSuperBlock : IndexedMemorySuperBlock
{
    public SilkSuperBlock(FileSystemType type, SilkRepository repository, DeviceNumberManager devManager) : base(type, devManager)
    {
        Repository = repository;
    }

    public SilkRepository Repository { get; }

    protected override IndexedMemoryInode CreateIndexedInode(ulong ino)
    {
        return new SilkInode(ino, this, Repository);
    }

    public void LoadFromMetadata()
    {
        var inodeRecords = Repository.Metadata.ListInodes();
        var inodeMap = new Dictionary<long, SilkInode>();
        long maxIno = 0;

        // Rebuild inode table from persisted metadata.
        foreach (var rec in inodeRecords)
        {
            var inode = new SilkInode((ulong)rec.Ino, this, Repository)
            {
                Type = SilkInode.MapInodeType(rec.Kind),
                Mode = rec.Mode,
                Uid = rec.Uid,
                Gid = rec.Gid,
                Rdev = (uint)rec.Rdev,
                Size = (ulong)Math.Max(0, rec.Size)
            };
            Inodes.Add(inode);
            AllInodes.Add(inode);
            inodeMap[rec.Ino] = inode;
            if (rec.Ino > maxIno) maxIno = rec.Ino;
        }

        if (!inodeMap.TryGetValue(SilkMetadataStore.RootInode, out var rootInode))
        {
            rootInode = (SilkInode)AllocInode();
            rootInode.Type = InodeType.Directory;
            rootInode.Mode = 0x1FF;
            Repository.Metadata.UpsertInode((long)rootInode.Ino, SilkInodeKind.Directory, rootInode.Mode, 0, 0);
            inodeMap[(long)rootInode.Ino] = rootInode;
            maxIno = Math.Max(maxIno, (long)rootInode.Ino);
        }

        Root = new Dentry("/", rootInode, null, this);
        Root.Parent = Root;

        var primaryDentryByInode = new Dictionary<long, Dentry> { [SilkMetadataStore.RootInode] = Root };
        var pending = Repository.Metadata.ListDentries();
        while (pending.Count > 0)
        {
            var progressed = false;
            for (var i = pending.Count - 1; i >= 0; i--)
            {
                var rec = pending[i];
                if (!primaryDentryByInode.TryGetValue(rec.ParentIno, out var parent)) continue;
                if (!inodeMap.TryGetValue(rec.Ino, out var childInode)) continue;

                var child = new Dentry(rec.Name, childInode, parent, this);
                if (parent.Inode is IndexedMemoryInode dirInode)
                    dirInode.RegisterChild(parent, rec.Name, child);
                else
                    parent.Children[rec.Name] = child;

                primaryDentryByInode.TryAdd(rec.Ino, child);
                pending.RemoveAt(i);
                progressed = true;
            }

            if (!progressed) break;
        }

        foreach (var inode in inodeMap.Values)
        {
            inode.LoadXAttrsFromMetadata();
            inode.LoadDataFromMetadata();
        }

        _nextIno = (ulong)Math.Max(maxIno + 1, 2);
    }
}

public sealed class SilkInode : IndexedMemoryInode
{
    private readonly SilkRepository _repository;
    private readonly SilkMetadataStore _metadata;
    private bool _hasPendingPageWriteback;
    private readonly object _persistLock = new();

    public SilkInode(ulong ino, IndexedMemorySuperBlock sb, SilkRepository repository) : base(ino, sb)
    {
        _repository = repository;
        _metadata = repository.Metadata;
    }

    protected override GlobalPageCacheManager.PageCacheClass CacheClass => GlobalPageCacheManager.PageCacheClass.File;

    public static SilkInodeKind MapInodeKind(InodeType type)
    {
        return type switch
        {
            InodeType.Directory => SilkInodeKind.Directory,
            InodeType.File => SilkInodeKind.File,
            InodeType.Symlink => SilkInodeKind.Symlink,
            InodeType.CharDev => SilkInodeKind.CharDevice,
            InodeType.BlockDev => SilkInodeKind.BlockDevice,
            InodeType.Fifo => SilkInodeKind.Fifo,
            InodeType.Socket => SilkInodeKind.Socket,
            _ => SilkInodeKind.File
        };
    }

    public static InodeType MapInodeType(SilkInodeKind type)
    {
        return type switch
        {
            SilkInodeKind.Directory => InodeType.Directory,
            SilkInodeKind.File => InodeType.File,
            SilkInodeKind.Symlink => InodeType.Symlink,
            SilkInodeKind.CharDevice => InodeType.CharDev,
            SilkInodeKind.BlockDevice => InodeType.BlockDev,
            SilkInodeKind.Fifo => InodeType.Fifo,
            SilkInodeKind.Socket => InodeType.Socket,
            _ => InodeType.File
        };
    }

    public void LoadXAttrsFromMetadata()
    {
        foreach (var kv in _metadata.ListXAttrs((long)Ino))
            _ = base.SetXAttr(kv.Key, kv.Value, 0);
    }

    public void LoadDataFromMetadata()
    {
        if (Type != InodeType.File && Type != InodeType.Symlink) return;
        var objectId = _metadata.GetInodeObject((long)Ino);
        if (string.IsNullOrEmpty(objectId)) return;
        var data = _repository.ReadObject(objectId!);
        if (data == null) return;

        _ = base.Truncate(0);
        if (data.Length > 0)
            _ = base.Write(null!, data, 0);
    }

    private void SyncSelf()
    {
        _metadata.UpsertInode(
            (long)Ino,
            MapInodeKind(Type),
            Mode,
            Uid,
            Gid,
            nlink: 1,
            rdev: Rdev,
            size: (long)Size);
    }

    private void PersistData()
    {
        if (Type != InodeType.File && Type != InodeType.Symlink) return;
        var data = ReadAllData();
        var objectId = _repository.PutObject(data);
        var binding = _metadata.SetInodeObjectWithRefCount((long)Ino, objectId);
        if (!string.IsNullOrEmpty(binding.UnreferencedObjectId))
            _repository.DeleteObject(binding.UnreferencedObjectId!);
    }

    private byte[] ReadAllData()
    {
        var len = checked((int)Math.Min((ulong)int.MaxValue, Size));
        if (len == 0) return Array.Empty<byte>();

        var result = new byte[len];
        var pos = 0;
        while (pos < len)
        {
            var n = base.Read(null!, result.AsSpan(pos), pos);
            if (n <= 0) break;
            pos += n;
        }

        return pos == len ? result : result[..pos];
    }

    protected override int BackendRead(LinuxFile? linuxFile, Span<byte> buffer, long offset)
    {
        lock (Lock)
        {
            if (Type == InodeType.Directory) return 0;
            if (Type == InodeType.Symlink)
                return base.BackendRead(linuxFile, buffer, offset);
            if (offset < 0) return -(int)Errno.EINVAL;

            var fileSize = (long)Size;
            if (offset >= fileSize) return 0;
            var count = Math.Min(buffer.Length, (int)(fileSize - offset));
            var persisted = ReadPersistedSnapshot();
            var copied = 0;
            var pageCache = EnsurePageCacheLocked();

            while (copied < count)
            {
                var absolute = offset + copied;
                var pageIndex = (uint)(absolute / LinuxConstants.PageSize);
                var pageOffset = (int)(absolute & LinuxConstants.PageOffsetMask);
                var chunk = Math.Min(count - copied, LinuxConstants.PageSize - pageOffset);
                var pagePtr = pageCache.GetPage(pageIndex);
                if (pagePtr != IntPtr.Zero)
                {
                    unsafe
                    {
                        var src = (byte*)pagePtr + pageOffset;
                        fixed (byte* dst = &buffer[copied])
                        {
                            Buffer.MemoryCopy(src, dst, chunk, chunk);
                        }
                    }
                }
                else
                {
                    var available = Math.Max(0, persisted.Length - (int)absolute);
                    var fromPersisted = Math.Min(chunk, available);
                    if (fromPersisted > 0)
                        persisted.AsSpan((int)absolute, fromPersisted).CopyTo(buffer.Slice(copied, fromPersisted));
                    if (fromPersisted < chunk)
                        buffer.Slice(copied + fromPersisted, chunk - fromPersisted).Clear();
                }

                copied += chunk;
            }

            return count;
        }
    }

    private void SyncDentry(Dentry dentry)
    {
        var ino = (long)dentry.Inode!.Ino;
        _metadata.UpsertInode(
            ino,
            MapInodeKind(dentry.Inode.Type),
            dentry.Inode.Mode,
            dentry.Inode.Uid,
            dentry.Inode.Gid,
            nlink: 1,
            rdev: dentry.Inode.Rdev,
            size: (long)dentry.Inode.Size);

        var parentIno = (long)Ino;
        _metadata.UpsertDentry(parentIno, dentry.Name, ino);
        _metadata.ClearWhiteout(parentIno, dentry.Name);
    }

    public override Dentry Create(Dentry dentry, int mode, int uid, int gid)
    {
        var created = base.Create(dentry, mode, uid, gid);
        SyncDentry(created);
        if (created.Inode is SilkInode child)
            child.PersistData();
        return created;
    }

    public override Dentry Mkdir(Dentry dentry, int mode, int uid, int gid)
    {
        var created = base.Mkdir(dentry, mode, uid, gid);
        SyncDentry(created);
        return created;
    }

    public override Dentry Mknod(Dentry dentry, int mode, int uid, int gid, InodeType type, uint rdev)
    {
        var created = base.Mknod(dentry, mode, uid, gid, type, rdev);
        SyncDentry(created);
        if (type == InodeType.CharDev && rdev == 0)
        {
            var parentIno = (long)Ino;
            if (string.Equals(dentry.Name, SilkMetadataStore.OpaqueMarkerName, StringComparison.Ordinal))
                _metadata.MarkOpaque(parentIno);
            else if (dentry.Name.StartsWith(".wh.", StringComparison.Ordinal) && dentry.Name.Length > 4)
                _metadata.MarkWhiteout(parentIno, dentry.Name[4..]);
        }
        return created;
    }

    public override Dentry Symlink(Dentry dentry, string target, int uid, int gid)
    {
        var created = base.Symlink(dentry, target, uid, gid);
        SyncDentry(created);
        if (created.Inode is SilkInode child)
            child.PersistData();
        return created;
    }

    public override Dentry Link(Dentry dentry, Inode oldInode)
    {
        var created = base.Link(dentry, oldInode);
        SyncDentry(created);
        return created;
    }

    public override void Unlink(string name)
    {
        var victim = Lookup(name)?.Inode;
        base.Unlink(name);
        _metadata.RemoveDentry((long)Ino, name);
        _metadata.ClearWhiteout((long)Ino, name);
        CleanupOrphan(victim);
    }

    public override void Rmdir(string name)
    {
        var victim = Lookup(name)?.Inode;
        base.Rmdir(name);
        _metadata.RemoveDentry((long)Ino, name);
        _metadata.ClearWhiteout((long)Ino, name);
        CleanupOrphan(victim);
    }

    public override void Rename(string oldName, Inode newParent, string newName)
    {
        // Capture the existing destination inode BEFORE base.Rename overwrites/unlinks it,
        // so we can clean it up from the metadata DB afterward.
        var overwrittenInode = newParent.Lookup(newName)?.Inode;

        base.Rename(oldName, newParent, newName);
        _metadata.RemoveDentry((long)Ino, oldName);

        if (newParent.Lookup(newName) is { Inode: not null } moved)
        {
            var parentIno = (long)newParent.Ino;
            var ino = (long)moved.Inode.Ino;
            _metadata.UpsertDentry(parentIno, newName, ino);
        }

        // If the rename overwrote an existing file, purge its stale data from the DB.
        // Without this, re-opening the file path after rename would reload old content.
        if (overwrittenInode != null && overwrittenInode != newParent.Lookup(newName)?.Inode)
            CleanupOrphan(overwrittenInode);
    }

    public override int Write(LinuxFile linuxFile, ReadOnlySpan<byte> buffer, long offset)
    {
        var rc = base.Write(linuxFile, buffer, offset);
        if (rc > 0)
        {
            SyncSelf();
            PersistData();
            ClearPageCacheDirtyState();
        }
        return rc;
    }

    public override int Truncate(long size)
    {
        var rc = base.Truncate(size);
        if (rc == 0)
        {
            SyncSelf();
            PersistData();
            ClearPageCacheDirtyState();
        }
        return rc;
    }

    public override int WritePage(LinuxFile? linuxFile, PageIoRequest request, ReadOnlySpan<byte> pageBuffer, bool sync)
    {
        var rc = base.WritePage(linuxFile, request, pageBuffer, sync);
        if (rc == 0 && request.Length > 0)
        {
            lock (_persistLock)
            {
                _hasPendingPageWriteback = true;
            }
        }

        return rc;
    }

    public override int WritePages(LinuxFile? linuxFile, WritePagesRequest request)
    {
        var rc = base.WritePages(linuxFile, request);
        if (rc < 0) return rc;
        if (!request.Sync) return 0;

        lock (_persistLock)
        {
            if (!_hasPendingPageWriteback) return 0;
            SyncSelf();
            PersistData();
            ClearPageCacheDirtyState();
            _hasPendingPageWriteback = false;
        }

        return 0;
    }

    public override int SetPageDirty(long pageIndex)
    {
        var rc = base.SetPageDirty(pageIndex);
        if (rc == 0)
        {
            lock (_persistLock)
            {
                _hasPendingPageWriteback = true;
            }
        }

        return rc;
    }

    public override int SetXAttr(string name, ReadOnlySpan<byte> value, int flags)
    {
        var rc = base.SetXAttr(name, value, flags);
        if (rc == 0) _metadata.SetXAttr((long)Ino, name, value);
        return rc;
    }

    public override int RemoveXAttr(string name)
    {
        var rc = base.RemoveXAttr(name);
        if (rc == 0) _metadata.RemoveXAttr((long)Ino, name);
        return rc;
    }

    private void CleanupOrphan(Inode? inode)
    {
        if (inode == null) return;
        var ino = (long)inode.Ino;
        if (ino == SilkMetadataStore.RootInode) return;
        if (_metadata.CountDentryRefs(ino) != 0) return;
        var unrefObject = _metadata.DeleteInodeWithObjectRefCount(ino);
        if (!string.IsNullOrEmpty(unrefObject))
            _repository.DeleteObject(unrefObject!);
    }

    private byte[] ReadPersistedSnapshot()
    {
        if (Type != InodeType.File && Type != InodeType.Symlink) return Array.Empty<byte>();
        var objectId = _metadata.GetInodeObject((long)Ino);
        if (string.IsNullOrEmpty(objectId)) return Array.Empty<byte>();
        return _repository.ReadObject(objectId!) ?? Array.Empty<byte>();
    }

    private void ClearPageCacheDirtyState()
    {
        lock (Lock)
        {
            if (PageCache != null)
            {
                foreach (var state in PageCache.SnapshotPageStates())
                    PageCache.ClearDirty(state.PageIndex);
            }

            DirtyPageIndexes.Clear();
        }
    }
}
