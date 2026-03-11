using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.SilkFS;

namespace Fiberish.VFS;

/// <summary>
///     Core-side VFS adapter for SilkFS.
///     SilkFS project stays storage-focused and independent from Core VFS types.
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

public sealed class SilkSuperBlock : IndexedMemorySuperBlock, IDentryCacheDropper
{
    public SilkSuperBlock(FileSystemType type, SilkRepository repository, DeviceNumberManager devManager) : base(type,
        devManager)
    {
        Repository = repository;
    }

    public SilkRepository Repository { get; }

    public long DropDentryCache()
    {
        if (Root == null) return 0;

        long dropped = 0;
        var children = Root.Children.Values.ToList();
        foreach (var child in children)
        {
            if (child.IsMounted) continue;
            dropped += VfsShrinker.DetachCachedSubtree(child);
        }

        return dropped;
    }

    protected override IndexedMemoryInode CreateIndexedInode(ulong ino)
    {
        return new SilkInode(ino, this, Repository);
    }

    public SilkInode? GetOrLoadInode(long ino)
    {
        lock (Lock)
        {
            var tracked = Inodes.OfType<SilkInode>().FirstOrDefault(i => (long)i.Ino == ino && !i.IsCacheEvicted);
            if (tracked != null) return tracked;
        }

        var rec = Repository.Metadata.GetInode(ino);
        if (rec == null) return null;

        var loaded = new SilkInode((ulong)rec.Value.Ino, this, Repository)
        {
            Type = SilkInode.MapInodeType(rec.Value.Kind),
            Mode = rec.Value.Mode,
            Uid = rec.Value.Uid,
            Gid = rec.Value.Gid,
            Rdev = (uint)rec.Value.Rdev,
            Size = (ulong)Math.Max(0, rec.Value.Size)
        };
        var persistedNlink = (int)Math.Max(0, rec.Value.Nlink);
        if (rec.Value.Ino == SilkMetadataStore.RootInode && loaded.Type == InodeType.Directory && persistedNlink < 2)
            persistedNlink = 2;
        loaded.SetInitialLinkCount(persistedNlink, "SilkSuperBlock.GetOrLoadInode");
        loaded.LoadXAttrsFromMetadata();
        if (loaded.Type == InodeType.Symlink)
            loaded.LoadDataFromMetadata();

        lock (Lock)
        {
            var tracked = Inodes.OfType<SilkInode>().FirstOrDefault(i => (long)i.Ino == ino && !i.IsCacheEvicted);
            if (tracked != null) return tracked;
            TrackInode(loaded);
        }

        return loaded;
    }

    public void LoadFromMetadata()
    {
        var orphanInodes = Repository.Metadata.ListOrphanInodes();
        foreach (var orphanIno in orphanInodes)
        {
            Repository.Metadata.DeleteInode(orphanIno);
            Repository.DeleteLiveInodeData(orphanIno);
        }

        var inodeRecords = Repository.Metadata.ListInodes();
        long maxIno = 0;

        SilkInode? rootInode = null;
        foreach (var rec in inodeRecords)
        {
            if (rec.Ino == SilkMetadataStore.RootInode)
                rootInode = GetOrLoadInode(rec.Ino);
            if (rec.Ino > maxIno) maxIno = rec.Ino;
        }

        if (rootInode == null)
        {
            rootInode = (SilkInode)AllocInode();
            rootInode.Type = InodeType.Directory;
            rootInode.Mode = 0x1FF;
            rootInode.SetInitialLinkCount(2, "SilkSuperBlock.LoadFromMetadata.root-init");
            Repository.Metadata.UpsertInode((long)rootInode.Ino, SilkInodeKind.Directory, rootInode.Mode, 0, 0,
                rootInode.LinkCount);
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

                var child = new Dentry(rec.Name, null, parent, this);
                if (parent.Inode is IndexedMemoryInode dirInode)
                    dirInode.RegisterChild(parent, rec.Name, child);
                else
                    parent.CacheChild(child, "SilkSuperBlock.LoadFromMetadata");

                primaryDentryByInode.TryAdd(rec.Ino, child);
                pending.RemoveAt(i);
                progressed = true;
            }

            if (!progressed) break;
        }

        _nextIno = (ulong)Math.Max(maxIno + 1, 2);
    }
}

public sealed class SilkInode : IndexedMemoryInode
{
    private static readonly AsyncLocal<int> NamespaceMutationDepth = new();
    private readonly object _mappedCacheLock = new();
    private readonly SilkMetadataStore _metadata;
    private readonly object _persistLock = new();
    private readonly SilkRepository _repository;
    private bool _hasPendingPageWriteback;
    private MappedFilePageCache? _mappedPageCache;

    public SilkInode(ulong ino, IndexedMemorySuperBlock sb, SilkRepository repository) : base(ino, sb)
    {
        _repository = repository;
        _metadata = repository.Metadata;
    }

    protected override GlobalPageCacheManager.PageCacheClass CacheClass => GlobalPageCacheManager.PageCacheClass.File;

    private static bool IsNamespaceMutationSuppressed => NamespaceMutationDepth.Value > 0;

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

    private static IDisposable SuppressNamespaceMetadataMutations()
    {
        return new NamespaceMutationScope();
    }

    public void LoadXAttrsFromMetadata()
    {
        foreach (var kv in _metadata.ListXAttrs((long)Ino))
            _ = base.SetXAttr(kv.Key, kv.Value, 0);
    }

    public void LoadDataFromMetadata()
    {
        if (Type != InodeType.File && Type != InodeType.Symlink) return;
        var data = _repository.ReadLiveInodeData((long)Ino);
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
            LinkCount,
            Rdev,
            (long)Size);
    }

    private void PersistData()
    {
        if (Type != InodeType.File && Type != InodeType.Symlink) return;
        var data = ReadAllData();
        _repository.WriteLiveInodeData((long)Ino, data);
    }

    private byte[] ReadAllData()
    {
        var len = checked((int)Math.Min(int.MaxValue, Size));
        if (len == 0) return Array.Empty<byte>();

        var result = new byte[len];
        var pos = 0;
        while (pos < len)
        {
            var n = Read(null!, result.AsSpan(pos), pos);
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
            {
                EnsureSymlinkDataLoadedLocked();
                return base.BackendRead(linuxFile, buffer, offset);
            }

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

    public override string Readlink()
    {
        lock (Lock)
        {
            EnsureSymlinkDataLoadedLocked();
            return base.Readlink();
        }
    }

    public override bool RevalidateCachedChild(Dentry parent, string name, Dentry cached)
    {
        lock (Lock)
        {
            return TryHydrateChildDentry(parent, name, cached);
        }
    }

    public override Dentry? Lookup(string name)
    {
        lock (Lock)
        {
            if (Type != InodeType.Directory) return null;
            if (Dentries.Count == 0) return null;
            var primaryDentry = Dentries[0];
            var key = new DCacheKey(Ino, name);
            if (IndexedSb.Dentries.TryGetValue(key, out var cached))
            {
                if (!TryHydrateChildDentry(primaryDentry, name, cached))
                {
                    lock (IndexedSb.Lock)
                    {
                        IndexedSb.Dentries.Remove(key);
                    }

                    _ = primaryDentry.TryUncacheChild(name, "SilkInode.Lookup.refresh-missing", out _);
                    ChildNames.Remove(name);
                    return null;
                }

                primaryDentry.CacheChild(cached, "SilkInode.Lookup.refresh-hit");
                ChildNames.Add(name);
                return cached;
            }

            var childIno = _metadata.LookupDentry((long)Ino, name);
            if (childIno == null) return null;

            var childInode = ((SilkSuperBlock)IndexedSb).GetOrLoadInode(childIno.Value);
            if (childInode == null) return null;

            var created = new Dentry(name, childInode, primaryDentry, SuperBlock);
            lock (IndexedSb.Lock)
            {
                IndexedSb.Dentries[key] = created;
            }

            primaryDentry.CacheChild(created, "SilkInode.Lookup.refresh-create");
            ChildNames.Add(name);
            return created;
        }
    }

    public override List<DirectoryEntry> GetEntries()
    {
        if (Type != InodeType.Directory)
            return base.GetEntries();

        var entries = new List<DirectoryEntry>
        {
            new() { Name = ".", Ino = Ino, Type = InodeType.Directory },
            new() { Name = "..", Ino = Ino, Type = InodeType.Directory }
        };

        foreach (var rec in _metadata.ListDentriesByParent((long)Ino))
        {
            InodeType childType;
            var key = new DCacheKey(Ino, rec.Name);
            if (IndexedSb.Dentries.TryGetValue(key, out var cached) && cached.Inode != null)
            {
                childType = cached.Inode.Type;
            }
            else
            {
                var childRec = _metadata.GetInode(rec.Ino);
                if (childRec == null) continue;
                childType = MapInodeType(childRec.Value.Kind);
            }

            entries.Add(new DirectoryEntry
            {
                Name = rec.Name,
                Ino = (ulong)rec.Ino,
                Type = childType
            });
        }

        return entries;
    }

    private bool TryHydrateChildDentry(Dentry parent, string name, Dentry childDentry)
    {
        if (childDentry.Inode != null)
            return true;

        var childIno = _metadata.LookupDentry((long)Ino, name);
        if (childIno == null)
            return false;

        var childInode = ((SilkSuperBlock)IndexedSb).GetOrLoadInode(childIno.Value);
        if (childInode == null)
            return false;

        if (childDentry.Inode == null)
            childDentry.Instantiate(childInode);
        childDentry.Parent ??= parent;
        childDentry.Name = name;
        ChildNames.Add(name);
        return true;
    }

    private void EnsureSymlinkDataLoadedLocked()
    {
        if (Type != InodeType.Symlink) return;
        if (SymlinkData != null) return;

        var data = ReadPersistedSnapshot();
        SymlinkData = data.Length == 0 ? Array.Empty<byte>() : data;
        Size = (ulong)SymlinkData.Length;
    }

    private static void UpsertInodeMetadata(SilkMetadataStore.SilkMetadataTransaction tx, Inode inode)
    {
        tx.UpsertInode(
            (long)inode.Ino,
            MapInodeKind(inode.Type),
            inode.Mode,
            inode.Uid,
            inode.Gid,
            inode.LinkCount,
            inode.Rdev,
            (long)inode.Size);
    }

    private static void UpsertInodeMetadataIfLive(SilkMetadataStore.SilkMetadataTransaction tx, Inode? inode)
    {
        if (inode == null || inode.IsFinalized)
            return;
        UpsertInodeMetadata(tx, inode);
    }

    public override Dentry Create(Dentry dentry, int mode, int uid, int gid)
    {
        var created = base.Create(dentry, mode, uid, gid);
        _metadata.ExecuteTransaction(tx =>
        {
            UpsertInodeMetadata(tx, created.Inode!);
            tx.UpsertDentry((long)Ino, created.Name, (long)created.Inode!.Ino);
            tx.ClearWhiteout((long)Ino, created.Name);
        });
        if (created.Inode is SilkInode child)
            child.PersistData();
        return created;
    }

    public override Dentry Mkdir(Dentry dentry, int mode, int uid, int gid)
    {
        var created = base.Mkdir(dentry, mode, uid, gid);
        _metadata.ExecuteTransaction(tx =>
        {
            UpsertInodeMetadata(tx, this);
            UpsertInodeMetadata(tx, created.Inode!);
            tx.UpsertDentry((long)Ino, created.Name, (long)created.Inode!.Ino);
            tx.ClearWhiteout((long)Ino, created.Name);
        });
        return created;
    }

    public override Dentry Mknod(Dentry dentry, int mode, int uid, int gid, InodeType type, uint rdev)
    {
        var created = base.Mknod(dentry, mode, uid, gid, type, rdev);
        _metadata.ExecuteTransaction(tx =>
        {
            UpsertInodeMetadata(tx, created.Inode!);
            tx.UpsertDentry((long)Ino, created.Name, (long)created.Inode!.Ino);
            tx.ClearWhiteout((long)Ino, created.Name);
            if (type == InodeType.CharDev && rdev == 0)
            {
                var parentIno = (long)Ino;
                if (string.Equals(dentry.Name, SilkMetadataStore.OpaqueMarkerName, StringComparison.Ordinal))
                    tx.MarkOpaque(parentIno);
                else if (dentry.Name.StartsWith(".wh.", StringComparison.Ordinal) && dentry.Name.Length > 4)
                    tx.MarkWhiteout(parentIno, dentry.Name[4..]);
            }
        });
        return created;
    }

    public override Dentry Symlink(Dentry dentry, string target, int uid, int gid)
    {
        var created = base.Symlink(dentry, target, uid, gid);
        _metadata.ExecuteTransaction(tx =>
        {
            UpsertInodeMetadata(tx, created.Inode!);
            tx.UpsertDentry((long)Ino, created.Name, (long)created.Inode!.Ino);
            tx.ClearWhiteout((long)Ino, created.Name);
        });
        if (created.Inode is SilkInode child)
            child.PersistData();
        return created;
    }

    public override Dentry Link(Dentry dentry, Inode oldInode)
    {
        var created = base.Link(dentry, oldInode);
        _metadata.ExecuteTransaction(tx =>
        {
            UpsertInodeMetadata(tx, oldInode);
            tx.UpsertDentry((long)Ino, created.Name, (long)oldInode.Ino);
            tx.ClearWhiteout((long)Ino, created.Name);
        });
        return created;
    }

    public override void Unlink(string name)
    {
        var victim = Lookup(name)?.Inode;
        base.Unlink(name);
        if (IsNamespaceMutationSuppressed)
            return;

        _metadata.ExecuteTransaction(tx =>
        {
            tx.RemoveDentry((long)Ino, name);
            tx.ClearWhiteout((long)Ino, name);
            UpsertInodeMetadataIfLive(tx, victim);
        });
    }

    public override void Rmdir(string name)
    {
        var victim = Lookup(name)?.Inode;
        base.Rmdir(name);
        if (IsNamespaceMutationSuppressed)
            return;

        _metadata.ExecuteTransaction(tx =>
        {
            UpsertInodeMetadataIfLive(tx, this);
            tx.RemoveDentry((long)Ino, name);
            tx.ClearWhiteout((long)Ino, name);
            UpsertInodeMetadataIfLive(tx, victim);
        });
    }

    public override void Rename(string oldName, Inode newParent, string newName)
    {
        if (Lookup(oldName)?.Inode == null)
            throw new FileNotFoundException("Source does not exist", oldName);

        var overwrittenInode = newParent.Lookup(newName)?.Inode;
        using (SuppressNamespaceMetadataMutations())
        {
            base.Rename(oldName, newParent, newName);
        }

        var movedInode = newParent.Lookup(newName)?.Inode;
        _metadata.ExecuteTransaction(tx =>
        {
            tx.RemoveDentry((long)Ino, oldName);
            if (movedInode != null && !movedInode.IsFinalized)
            {
                UpsertInodeMetadata(tx, movedInode);
                tx.UpsertDentry((long)newParent.Ino, newName, (long)movedInode.Ino);
                tx.ClearWhiteout((long)newParent.Ino, newName);
            }

            UpsertInodeMetadataIfLive(tx, this);
            UpsertInodeMetadataIfLive(tx, newParent);
            if (!ReferenceEquals(overwrittenInode, movedInode))
                UpsertInodeMetadataIfLive(tx, overwrittenInode);
        });
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

    public override int WritePage(LinuxFile? linuxFile, PageIoRequest request, ReadOnlySpan<byte> pageBuffer, bool sync)
    {
        var rc = base.WritePage(linuxFile, request, pageBuffer, sync);
        if (rc == 0 && request.Length > 0)
            lock (_persistLock)
            {
                _hasPendingPageWriteback = true;
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
            lock (_persistLock)
            {
                _hasPendingPageWriteback = true;
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

    private byte[] ReadPersistedSnapshot()
    {
        if (Type != InodeType.File && Type != InodeType.Symlink) return Array.Empty<byte>();
        var live = _repository.ReadLiveInodeData((long)Ino);
        if (live != null) return live;
        return Array.Empty<byte>();
    }

    private void ClearPageCacheDirtyState()
    {
        lock (Lock)
        {
            if (PageCache != null)
                foreach (var state in PageCache.SnapshotPageStates())
                    PageCache.ClearDirty(state.PageIndex);

            DirtyPageIndexes.Clear();
        }
    }

    public override int Truncate(long size)
    {
        var rc = base.Truncate(size);
        if (rc == 0)
        {
            _repository.TruncateLiveInodeData((long)Ino, size);
            lock (_mappedCacheLock)
            {
                _mappedPageCache?.Truncate(size);
            }

            SyncSelf();
            PersistData();
            ClearPageCacheDirtyState();
        }

        return rc;
    }

    public override bool TryAcquireMappedPageHandle(LinuxFile? linuxFile, long pageIndex, long absoluteFileOffset,
        bool writable, out IPageHandle? pageHandle)
    {
        pageHandle = null;
        if (Type != InodeType.File && Type != InodeType.Symlink) return false;
        if (absoluteFileOffset < 0) return false;
        if ((absoluteFileOffset & LinuxConstants.PageOffsetMask) != 0) return false;

        lock (_mappedCacheLock)
        {
            _mappedPageCache ??= new MappedFilePageCache(_repository.GetLiveInodePath((long)Ino));
            return _mappedPageCache.TryAcquirePageHandle(
                absoluteFileOffset / LinuxConstants.PageSize,
                (long)Size,
                writable,
                out pageHandle);
        }
    }

    public override bool TryFlushMappedPage(LinuxFile? linuxFile, long pageIndex)
    {
        lock (_mappedCacheLock)
        {
            if (_mappedPageCache?.TryFlushPage(pageIndex) != true)
                return false;
        }

        lock (Lock)
        {
            DirtyPageIndexes.Remove(pageIndex);
            if (pageIndex >= 0 && pageIndex <= uint.MaxValue)
                PageCache?.ClearDirty((uint)pageIndex);
        }

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
        lock (_mappedCacheLock)
        {
            _mappedPageCache?.Dispose();
            _mappedPageCache = null;
        }

        base.OnEvictCache();
    }

    protected override void OnFinalizeDelete()
    {
        var ino = (long)Ino;
        if (ino != SilkMetadataStore.RootInode)
        {
            _metadata.DeleteInode(ino);
            _repository.DeleteLiveInodeData(ino);
        }

        base.OnFinalizeDelete();
    }

    private sealed class NamespaceMutationScope : IDisposable
    {
        public NamespaceMutationScope()
        {
            NamespaceMutationDepth.Value = NamespaceMutationDepth.Value + 1;
        }

        public void Dispose()
        {
            NamespaceMutationDepth.Value = Math.Max(0, NamespaceMutationDepth.Value - 1);
        }
    }
}