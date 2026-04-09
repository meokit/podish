using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.SilkFS;
using Microsoft.Win32.SafeHandles;

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

    internal static long ToUnixNanoseconds(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        var offset = new DateTimeOffset(utc);
        var seconds = offset.ToUnixTimeSeconds();
        var nanos = utc.Ticks % TimeSpan.TicksPerSecond * 100;
        return checked(seconds * 1_000_000_000L + nanos);
    }

    internal static DateTime FromUnixNanoseconds(long nanoseconds)
    {
        var seconds = nanoseconds / 1_000_000_000L;
        var nanosRemainder = nanoseconds % 1_000_000_000L;
        return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.AddTicks(nanosRemainder / 100);
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

        using var session = Repository.OpenMetadataSession();
        var rec = session.GetInode(ino);
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
        loaded.ATime = FromUnixNanoseconds(rec.Value.ATimeNs);
        loaded.MTime = FromUnixNanoseconds(rec.Value.MTimeNs);
        loaded.CTime = FromUnixNanoseconds(rec.Value.CTimeNs);
        loaded.LoadXAttrsFromMetadata(session);
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
        using var session = Repository.OpenMetadataSession();
        var orphanInodes = session.ListOrphanInodes();
        foreach (var orphanIno in orphanInodes)
        {
            session.DeleteInode(orphanIno);
            Repository.DeleteLiveInodeData(orphanIno);
        }

        var inodeRecords = session.ListInodes();
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
            session.UpsertInode((long)rootInode.Ino, SilkInodeKind.Directory, rootInode.Mode, 0, 0,
                rootInode.LinkCount);
            maxIno = Math.Max(maxIno, (long)rootInode.Ino);
        }

        Root = new Dentry("/", rootInode, null, this);
        Root.Parent = Root;

        var primaryDentryByInode = new Dictionary<long, Dentry> { [SilkMetadataStore.RootInode] = Root };
        var pending = session.ListDentries();
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

public sealed class SilkInode : IndexedMemoryInode, IHostMappedCacheDropper
{
    private static readonly AsyncLocal<int> NamespaceMutationDepth = new();
    private static readonly AsyncLocal<MetadataSessionScopeState?> MetadataSessionScope = new();
    private static readonly VmBackingManager BufferedWriteMappings = new();
    private int _metadataDirty;
    private readonly HashSet<long> _dirtyPageIndexes = [];
    private readonly Lock _dirtyPageLock = new();
    private readonly Lock _mappedCacheLock = new();
    private readonly SilkRepository _repository;
    private List<DirectoryEntry>? _cachedEntries;
    private MappedFilePageCache? _mappedPageCache;

    public SilkInode(ulong ino, IndexedMemorySuperBlock sb, SilkRepository repository) : base(ino, sb)
    {
        _repository = repository;
    }

    protected override GlobalAddressSpaceCacheManager.AddressSpaceCacheClass CacheClass =>
        GlobalAddressSpaceCacheManager.AddressSpaceCacheClass.File;

    private static bool IsNamespaceMutationSuppressed => NamespaceMutationDepth.Value > 0;

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

    private MetadataSessionLease EnterMetadataSessionScope(out SilkMetadataSession session)
    {
        var current = MetadataSessionScope.Value;
        if (current != null && ReferenceEquals(current.Repository, _repository))
        {
            current.Depth++;
            session = current.Session;
            return new MetadataSessionLease(current);
        }

        var created = new MetadataSessionScopeState(_repository, _repository.OpenMetadataSession());
        MetadataSessionScope.Value = created;
        session = created.Session;
        return new MetadataSessionLease(created);
    }

    public void LoadXAttrsFromMetadata(SilkMetadataSession session)
    {
        foreach (var kv in session.ListXAttrs((long)Ino))
            _ = base.SetXAttr(kv.Key, kv.Value, 0);
    }

    public void LoadDataFromMetadata()
    {
        if (Type != InodeType.Symlink) return;
        var data = _repository.ReadLiveInodeData((long)Ino);
        if (data == null) return;

        SymlinkData = data.Length == 0 ? Array.Empty<byte>() : [.. data];
        Size = (ulong)SymlinkData.Length;
    }

    private void InvalidateEntriesCache()
    {
        lock (Lock)
        {
            _cachedEntries = null;
        }
    }

    private void SyncSelf(SilkMetadataSession session)
    {
        session.UpsertInode(
            (long)Ino,
            MapInodeKind(Type),
            Mode,
            Uid,
            Gid,
            LinkCount,
            Rdev,
            (long)Size,
            SilkSuperBlock.ToUnixNanoseconds(ATime),
            SilkSuperBlock.ToUnixNanoseconds(MTime),
            SilkSuperBlock.ToUnixNanoseconds(CTime));
    }

    private void MarkMetadataDirty()
    {
        Volatile.Write(ref _metadataDirty, 1);
    }

    private void FlushDirtyMetadataIfNeeded(SilkMetadataSession session)
    {
        if (Interlocked.Exchange(ref _metadataDirty, 0) == 0)
            return;

        try
        {
            SyncSelf(session);
        }
        catch
        {
            MarkMetadataDirty();
            throw;
        }
    }

    private void FlushDirtyMetadataIfNeeded()
    {
        using var metadataScope = EnterMetadataSessionScope(out var session);
        FlushDirtyMetadataIfNeeded(session);
    }

    private void EnsureBufferedWriteMapping()
    {
        if (Type != InodeType.File || Mapping != null)
            return;

        var manager = MappingManager ?? BufferedWriteMappings;
        var mapping = manager.GetOrCreateMapping(this);
        mapping.Release();
    }

    public override int UpdateTimes(DateTime? atime, DateTime? mtime, DateTime? ctime)
    {
        var rc = base.UpdateTimes(atime, mtime, ctime);
        if (rc == 0)
            MarkMetadataDirty();

        return rc;
    }

    protected override int BackendRead(LinuxFile? linuxFile, Span<byte> buffer, long offset)
    {
        if (Type == InodeType.Directory) return 0;
        if (Type == InodeType.Symlink)
            lock (Lock)
            {
                EnsureSymlinkDataLoadedLocked();
                return base.BackendRead(linuxFile, buffer, offset);
            }

        if (offset < 0) return -(int)Errno.EINVAL;

        var fileSize = (long)Size;
        if (offset >= fileSize) return 0;

        if (linuxFile?.PrivateData is SafeFileHandle handle)
            return RandomAccess.Read(handle, buffer, offset);

        using var tempHandle = _repository.OpenLiveInodeHandle((long)Ino, FileMode.OpenOrCreate, FileAccess.Read);
        return RandomAccess.Read(tempHandle, buffer, offset);
    }

    public override int Readlink(out string? target)
    {
        lock (Lock)
        {
            EnsureSymlinkDataLoadedLocked();
            return base.Readlink(out target);
        }
    }

    public override bool RevalidateCachedChild(Dentry parent, string name, Dentry cached)
    {
        using var metadataScope = EnterMetadataSessionScope(out var session);
        lock (Lock)
        {
            return TryHydrateChildDentry(parent, name, cached, session);
        }
    }

    public override Dentry? Lookup(string name)
    {
        using var metadataScope = EnterMetadataSessionScope(out var session);
        lock (Lock)
        {
            if (Type != InodeType.Directory) return null;
            if (Dentries.Count == 0) return null;
            var primaryDentry = Dentries[0];
            var key = new DCacheKey(Ino, name);
            if (IndexedSb.Dentries.TryGetValue(key, out var cached))
            {
                if (!TryHydrateChildDentry(primaryDentry, name, cached, session))
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

            var childIno = session.LookupDentry((long)Ino, name);
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

        using var metadataScope = EnterMetadataSessionScope(out var session);
        lock (Lock)
        {
            if (_cachedEntries != null)
                return _cachedEntries;

            var entries = new List<DirectoryEntry>
            {
                new() { Name = ".", Ino = Ino, Type = InodeType.Directory },
                new() { Name = "..", Ino = Ino, Type = InodeType.Directory }
            };

            foreach (var rec in session.ListDentriesByParent((long)Ino))
            {
                InodeType childType;
                var key = new DCacheKey(Ino, rec.Name);
                if (IndexedSb.Dentries.TryGetValue(key, out var cached) && cached.Inode != null)
                {
                    childType = cached.Inode.Type;
                }
                else
                {
                    var childRec = session.GetInode(rec.Ino);
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

            _cachedEntries = entries;
            return entries;
        }
    }

    private bool TryHydrateChildDentry(Dentry parent, string name, Dentry childDentry, SilkMetadataSession session)
    {
        if (childDentry.Inode != null)
            return true;

        var childIno = session.LookupDentry((long)Ino, name);
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

        var data = _repository.ReadLiveInodeData((long)Ino) ?? Array.Empty<byte>();
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
            (long)inode.Size,
            SilkSuperBlock.ToUnixNanoseconds(inode.ATime),
            SilkSuperBlock.ToUnixNanoseconds(inode.MTime),
            SilkSuperBlock.ToUnixNanoseconds(inode.CTime));
    }

    private static void UpsertInodeMetadataIfLive(SilkMetadataStore.SilkMetadataTransaction tx, Inode? inode)
    {
        if (inode == null || inode.IsFinalized)
            return;
        UpsertInodeMetadata(tx, inode);
    }

    public override int Create(Dentry dentry, int mode, int uid, int gid)
    {
        using var metadataScope = EnterMetadataSessionScope(out var session);
        var rc = base.Create(dentry, mode, uid, gid);
        if (rc < 0)
            return rc;

        session.ExecuteTransaction(tx =>
        {
            UpsertInodeMetadata(tx, dentry.Inode!);
            tx.UpsertDentry((long)Ino, dentry.Name, (long)dentry.Inode!.Ino);
            tx.ClearWhiteout((long)Ino, dentry.Name);
        });
        if (dentry.Inode is SilkInode child)
        {
            if (child.Type == InodeType.Symlink)
                child.PersistSymlinkData();
            else if (child.Type == InodeType.File)
                child.EnsureRegularFileBackingExists();
        }

        InvalidateEntriesCache();
        return 0;
    }

    public override int Mkdir(Dentry dentry, int mode, int uid, int gid)
    {
        using var metadataScope = EnterMetadataSessionScope(out var session);
        var rc = base.Mkdir(dentry, mode, uid, gid);
        if (rc < 0)
            return rc;

        session.ExecuteTransaction(tx =>
        {
            UpsertInodeMetadata(tx, this);
            UpsertInodeMetadata(tx, dentry.Inode!);
            tx.UpsertDentry((long)Ino, dentry.Name, (long)dentry.Inode!.Ino);
            tx.ClearWhiteout((long)Ino, dentry.Name);
        });
        InvalidateEntriesCache();
        return 0;
    }

    public override int Mknod(Dentry dentry, int mode, int uid, int gid, InodeType type, uint rdev)
    {
        using var metadataScope = EnterMetadataSessionScope(out var session);
        var rc = base.Mknod(dentry, mode, uid, gid, type, rdev);
        if (rc < 0)
            return rc;

        session.ExecuteTransaction(tx =>
        {
            UpsertInodeMetadata(tx, dentry.Inode!);
            tx.UpsertDentry((long)Ino, dentry.Name, (long)dentry.Inode!.Ino);
            tx.ClearWhiteout((long)Ino, dentry.Name);
            if (type == InodeType.CharDev && rdev == 0)
            {
                var parentIno = (long)Ino;
                if (string.Equals(dentry.Name, SilkMetadataStore.OpaqueMarkerName, StringComparison.Ordinal))
                    tx.MarkOpaque(parentIno);
                else if (dentry.Name.StartsWith(".wh.", StringComparison.Ordinal) && dentry.Name.Length > 4)
                    tx.MarkWhiteout(parentIno, dentry.Name[4..]);
            }
        });
        InvalidateEntriesCache();
        return 0;
    }

    public override int Symlink(Dentry dentry, string target, int uid, int gid)
    {
        using var metadataScope = EnterMetadataSessionScope(out var session);
        var rc = base.Symlink(dentry, target, uid, gid);
        if (rc < 0)
            return rc;

        session.ExecuteTransaction(tx =>
        {
            UpsertInodeMetadata(tx, dentry.Inode!);
            tx.UpsertDentry((long)Ino, dentry.Name, (long)dentry.Inode!.Ino);
            tx.ClearWhiteout((long)Ino, dentry.Name);
        });
        if (dentry.Inode is SilkInode child)
            child.PersistSymlinkData();
        InvalidateEntriesCache();
        return 0;
    }

    public override int Link(Dentry dentry, Inode oldInode)
    {
        using var metadataScope = EnterMetadataSessionScope(out var session);
        var rc = base.Link(dentry, oldInode);
        if (rc < 0)
            return rc;

        session.ExecuteTransaction(tx =>
        {
            UpsertInodeMetadata(tx, oldInode);
            tx.UpsertDentry((long)Ino, dentry.Name, (long)oldInode.Ino);
            tx.ClearWhiteout((long)Ino, dentry.Name);
        });
        InvalidateEntriesCache();
        return 0;
    }

    public override void Open(LinuxFile linuxFile)
    {
        if (Type != InodeType.File) return;

        var mode = FileMode.Open;
        var access = FileAccess.ReadWrite;

        var hasCreate = (linuxFile.Flags & FileFlags.O_CREAT) != 0;
        var hasExcl = (linuxFile.Flags & FileFlags.O_EXCL) != 0;
        if (hasCreate && hasExcl) mode = FileMode.CreateNew;
        else if (hasCreate) mode = FileMode.OpenOrCreate;

        linuxFile.PrivateData = _repository.OpenLiveInodeHandle((long)Ino, mode, access);
    }

    public override void Release(LinuxFile linuxFile)
    {
        FlushDirtyDataIfNeeded(linuxFile);
        FlushDirtyMetadataIfNeeded();
        if (linuxFile.PrivateData is SafeFileHandle handle)
        {
            Flock(linuxFile, LinuxConstants.LOCK_UN);
            handle.Dispose();
            linuxFile.PrivateData = null;
        }

        base.Release(linuxFile);
    }

    public override void Sync(LinuxFile linuxFile)
    {
        FlushDirtyDataIfNeeded(linuxFile);
        if (linuxFile.PrivateData is SafeFileHandle handle)
            RandomAccess.FlushToDisk(handle);
        FlushDirtyMetadataIfNeeded();
    }

    public override int Unlink(string name)
    {
        using var metadataScope = EnterMetadataSessionScope(out var session);
        var victim = Lookup(name)?.Inode;
        var rc = base.Unlink(name);
        if (rc < 0)
            return rc;
        if (IsNamespaceMutationSuppressed)
            return 0;

        session.ExecuteTransaction(tx =>
        {
            tx.RemoveDentry((long)Ino, name);
            tx.ClearWhiteout((long)Ino, name);
            UpsertInodeMetadataIfLive(tx, victim);
        });

        InvalidateEntriesCache();
        return 0;
    }

    public override int Rmdir(string name)
    {
        using var metadataScope = EnterMetadataSessionScope(out var session);
        var victim = Lookup(name)?.Inode;
        var rc = base.Rmdir(name);
        if (rc < 0)
            return rc;
        if (IsNamespaceMutationSuppressed)
            return 0;

        session.ExecuteTransaction(tx =>
        {
            UpsertInodeMetadataIfLive(tx, this);
            tx.RemoveDentry((long)Ino, name);
            tx.ClearWhiteout((long)Ino, name);
            UpsertInodeMetadataIfLive(tx, victim);
        });

        InvalidateEntriesCache();
        return 0;
    }

    public override int Rename(string oldName, Inode newParent, string newName)
    {
        using var metadataScope = EnterMetadataSessionScope(out var session);
        if (Lookup(oldName)?.Inode == null)
            return -(int)Errno.ENOENT;

        var overwrittenInode = newParent.Lookup(newName)?.Inode;
        using (SuppressNamespaceMetadataMutations())
        {
            var rc = base.Rename(oldName, newParent, newName);
            if (rc < 0)
                return rc;
        }

        var movedInode = newParent.Lookup(newName)?.Inode;
        session.ExecuteTransaction(tx =>
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

        InvalidateEntriesCache();
        if (newParent is SilkInode parentSilk)
            parentSilk.InvalidateEntriesCache();
        return 0;
    }

    protected internal override int ReadSpan(LinuxFile linuxFile, Span<byte> buffer, long offset)
    {
        return ReadWithPageCache(linuxFile, buffer, offset, BackendRead);
    }

    protected override int BackendWrite(LinuxFile? linuxFile, ReadOnlySpan<byte> buffer, long offset)
    {
        if (Type == InodeType.Directory) return -(int)Errno.EISDIR;
        if (Type == InodeType.Symlink) return -(int)Errno.EINVAL;
        if (offset < 0) return -(int)Errno.EINVAL;

        SafeFileHandle? handle = null;
        try
        {
            handle = linuxFile?.PrivateData as SafeFileHandle ??
                     _repository.OpenLiveInodeHandle((long)Ino, FileMode.OpenOrCreate, FileAccess.ReadWrite);
            RandomAccess.Write(handle, buffer, offset);
            var fileSize = RandomAccess.GetLength(handle);
            if ((ulong)fileSize > Size) Size = (ulong)fileSize;
            MTime = DateTime.Now;
            CTime = MTime;
            MarkMetadataDirty();
            return buffer.Length;
        }
        finally
        {
            if (linuxFile?.PrivateData is not SafeFileHandle)
                handle?.Dispose();
        }
    }

    protected internal override int WriteSpan(LinuxFile linuxFile, ReadOnlySpan<byte> buffer, long offset)
    {
        EnsureBufferedWriteMapping();
        var rc = WriteWithPageCache(linuxFile, buffer, offset, BackendWrite);
        if (rc > 0)
            MarkMetadataDirty();

        return rc;
    }

    private void FlushDirtyDataIfNeeded(LinuxFile? linuxFile)
    {
        if (Type != InodeType.File || Mapping == null)
            return;

        bool hasDirtyPages;
        lock (_dirtyPageLock)
        {
            hasDirtyPages = _dirtyPageIndexes.Count > 0;
        }

        if (!hasDirtyPages)
            return;

        _ = WritePages(linuxFile, new WritePagesRequest(0, long.MaxValue, true));
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

    protected override int AopsWritePage(LinuxFile? linuxFile, PageIoRequest request, ReadOnlySpan<byte> pageBuffer,
        bool sync)
    {
        if (request.Length < 0 || request.Length > pageBuffer.Length)
            return -(int)Errno.EINVAL;
        if (request.Length == 0) return 0;
        if (!sync) return 0;

        int rc;
        GlobalAddressSpaceCacheManager.BeginAddressSpaceWriteback();
        try
        {
            rc = BackendWrite(linuxFile, pageBuffer[..request.Length], request.FileOffset);
        }
        finally
        {
            GlobalAddressSpaceCacheManager.EndAddressSpaceWriteback();
        }

        if (rc < 0) return rc;
        lock (_dirtyPageLock)
        {
            _dirtyPageIndexes.Remove(request.PageIndex);
        }

        if (request.PageIndex >= 0 && request.PageIndex <= uint.MaxValue)
            Mapping?.ClearDirty((uint)request.PageIndex);
        SyncRegularFileIfNeeded();
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
                GlobalAddressSpaceCacheManager.BeginAddressSpaceWriteback();
                try
                {
                    rc = BackendWrite(linuxFile, pageData[..writeLen], fileOffset);
                }
                finally
                {
                    GlobalAddressSpaceCacheManager.EndAddressSpaceWriteback();
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

        SyncRegularFileIfNeeded();
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

    public override int SetXAttr(string name, ReadOnlySpan<byte> value, int flags)
    {
        var rc = base.SetXAttr(name, value, flags);
        if (rc == 0)
        {
            using var metadataScope = EnterMetadataSessionScope(out var session);
            session.SetXAttr((long)Ino, name, value);
        }

        return rc;
    }

    public override int RemoveXAttr(string name)
    {
        var rc = base.RemoveXAttr(name);
        if (rc == 0)
        {
            using var metadataScope = EnterMetadataSessionScope(out var session);
            session.RemoveXAttr((long)Ino, name);
        }

        return rc;
    }

    private void ClearPageCacheDirtyState()
    {
        lock (_dirtyPageLock)
        {
            if (Mapping != null)
                foreach (var state in Mapping.SnapshotPageStates())
                    Mapping.ClearDirty(state.PageIndex);

            _dirtyPageIndexes.Clear();
        }
    }

    public override int Truncate(long size)
    {
        int rc;
        if (Type == InodeType.Symlink)
        {
            rc = base.Truncate(size);
            if (rc == 0)
                PersistSymlinkData();
            return rc;
        }

        if (Type == InodeType.Directory) return -(int)Errno.EISDIR;
        if (size < 0) return -(int)Errno.EINVAL;

        using (var handle = _repository.OpenLiveInodeHandle((long)Ino, FileMode.OpenOrCreate, FileAccess.ReadWrite))
        {
            RandomAccess.SetLength(handle, size);
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
        rc = 0;
        if (rc == 0)
        {
            lock (_mappedCacheLock)
            {
                _mappedPageCache?.Truncate(size);
            }

            MarkMetadataDirty();
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
            var livePath = _repository.GetLiveInodePath((long)Ino);
            _mappedPageCache ??= new MappedFilePageCache(
                livePath,
                SuperBlock.MemoryContext.HostMemoryMapGeometry);
            return _mappedPageCache.TryAcquirePageHandle(
                absoluteFileOffset / LinuxConstants.PageSize,
                (long)Size,
                writable,
                out pageHandle);
        }
    }

    public override bool TryFlushMappedPage(LinuxFile? linuxFile, long pageIndex)
    {
        if (Type == InodeType.File)
            return false;

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
        if (linuxFile != null) Sync(linuxFile);
        SyncRegularFileIfNeeded();
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
            using var metadataScope = EnterMetadataSessionScope(out var session);
            session.DeleteInode(ino);
            _repository.DeleteLiveInodeData(ino);
        }

        base.OnFinalizeDelete();
    }

    private void PersistSymlinkData()
    {
        if (Type != InodeType.Symlink) return;
        _repository.WriteLiveInodeData((long)Ino, SymlinkData ?? Array.Empty<byte>());
    }

    private void EnsureRegularFileBackingExists()
    {
        if (Type != InodeType.File) return;
        _repository.EnsureLiveInodeDataFile((long)Ino);
    }

    private void SyncRegularFileIfNeeded()
    {
        if (Type == InodeType.File)
            FlushDirtyMetadataIfNeeded();
    }

    private sealed class MetadataSessionScopeState
    {
        public MetadataSessionScopeState(SilkRepository repository, SilkMetadataSession session)
        {
            Repository = repository;
            Session = session;
            Depth = 1;
        }

        public int Depth { get; set; }
        public SilkRepository Repository { get; }
        public SilkMetadataSession Session { get; }
    }

    private readonly struct MetadataSessionLease : IDisposable
    {
        private readonly MetadataSessionScopeState? _state;

        public MetadataSessionLease(MetadataSessionScopeState state)
        {
            _state = state;
        }

        public void Dispose()
        {
            if (_state == null)
                return;

            _state.Depth--;
            if (_state.Depth > 0)
                return;

            if (ReferenceEquals(MetadataSessionScope.Value, _state))
                MetadataSessionScope.Value = null;
            _state.Session.Dispose();
        }
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
