using Fiberish.Core;
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
    public SilkFileSystem(DeviceNumberManager? devManager = null, MemoryRuntimeContext? memoryContext = null)
        : base(devManager, memoryContext)
    {
        Name = "silkfs";
    }

    public override SuperBlock ReadSuper(FileSystemType fsType, int flags, string devName, object? data)
    {
        var options = SilkFsOptions.FromSource(devName);
        var repository = new SilkRepository(options);
        repository.Initialize();

        var sb = new SilkSuperBlock(fsType, repository, DevManager, MemoryContext);
        sb.LoadFromMetadata();
        return sb;
    }
}

public sealed class SilkSuperBlock : SuperBlock, IDentryCacheDropper
{
    private ulong _nextIno = 1;
    private readonly Dictionary<ulong, FsNameMap<Dentry>> _dentriesByParent = [];
    private readonly object _metadataSessionGate = new();
    private SilkMetadataSession? _metadataSession;
    private bool _metadataSessionDisposed;

    public SilkSuperBlock(FileSystemType type, SilkRepository repository, DeviceNumberManager devManager,
        MemoryRuntimeContext? memoryContext = null) : base(devManager, memoryContext ?? new MemoryRuntimeContext())
    {
        Type = type;
        Repository = repository;
    }

    public SilkRepository Repository { get; }

    public override Inode AllocInode()
    {
        lock (Lock)
        {
            var inode = new SilkInode(_nextIno++, this, Repository);
            TrackInode(inode);
            return inode;
        }
    }

    public override void WriteInode(Inode inode)
    {
    }

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

    protected override void Shutdown()
    {
        List<SilkInode> trackedInodes;
        lock (Lock)
        {
            trackedInodes = Inodes.OfType<SilkInode>()
                .Where(inode => !inode.IsFinalized)
                .ToList();
        }

        using (var lease = AcquireMetadataSession(out var session))
        {
            foreach (var inode in trackedInodes)
                inode.FlushMetadataForShutdown(session);
        }

        try
        {
            base.Shutdown();
        }
        finally
        {
            _dentriesByParent.Clear();
            DisposeMetadataSession();
        }
    }

    public bool ContainsDentry(ulong parentIno, ReadOnlySpan<byte> name)
    {
        lock (Lock)
        {
            return _dentriesByParent.TryGetValue(parentIno, out var names) && names.TryGetValue(name, out _);
        }
    }

    public bool TryGetDentry(ulong parentIno, ReadOnlySpan<byte> name, out Dentry dentry)
    {
        lock (Lock)
        {
            if (_dentriesByParent.TryGetValue(parentIno, out var names) && names.TryGetValue(name, out dentry))
                return true;
        }

        dentry = null!;
        return false;
    }

    public void SetDentry(ulong parentIno, Dentry dentry)
    {
        lock (Lock)
        {
            if (!_dentriesByParent.TryGetValue(parentIno, out var names))
            {
                names = new FsNameMap<Dentry>();
                _dentriesByParent[parentIno] = names;
            }

            names.Set(dentry.Name, dentry);
        }
    }

    public bool RemoveDentry(ulong parentIno, ReadOnlySpan<byte> name, out Dentry? removed)
    {
        lock (Lock)
        {
            if (_dentriesByParent.TryGetValue(parentIno, out var names) && names.Remove(name, out removed))
            {
                if (names.Count == 0)
                    _dentriesByParent.Remove(parentIno);
                return true;
            }
        }

        removed = null;
        return false;
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

    public SilkInode? GetOrLoadInode(long ino)
    {
        lock (Lock)
        {
            var tracked = Inodes.OfType<SilkInode>().FirstOrDefault(i => (long)i.Ino == ino && !i.IsCacheEvicted);
            if (tracked != null) return tracked;
        }

        using var lease = AcquireMetadataSession(out var session);
        var rec = session.GetInode(ino);
        if (rec == null) return null;

        var loaded = new SilkInode((ulong)rec.Value.Ino, this, Repository)
        {
            Type = SilkInode.MapInodeType(rec.Value.Kind)
        };
        loaded.RestorePersistedMetadata(rec.Value);
        var persistedNlink = (int)Math.Max(0, rec.Value.Nlink);
        if (rec.Value.Ino == SilkMetadataStore.RootInode && loaded.Type == InodeType.Directory && persistedNlink < 2)
            persistedNlink = 2;
        loaded.SetInitialLinkCount(persistedNlink, "SilkSuperBlock.GetOrLoadInode");
        loaded.RestorePersistedTimestamps(rec.Value);
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
        using var lease = AcquireMetadataSession(out var session);
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

        Root = new Dentry(FsName.Empty, rootInode, null, this);
        Root.Parent = Root;

        var primaryDentryByInode = new Dictionary<long, Dentry> { [SilkMetadataStore.RootInode] = Root };
        var pending = session.ListDentries();
        while (pending.Count > 0)
        {
            var progressed = false;
            for (var i = pending.Count - 1; i >= 0; i--)
            {
                var rec = pending[i];
                if (rec.Name.Length == 0)
                {
                    if (rec.Ino == SilkMetadataStore.RootInode)
                    {
                        primaryDentryByInode.TryAdd(rec.Ino, Root);
                        pending.RemoveAt(i);
                        progressed = true;
                        continue;
                    }

                    throw new InvalidDataException(
                        $"Silk metadata contains an empty dentry name for parent={rec.ParentIno} ino={rec.Ino}.");
                }

                if (!primaryDentryByInode.TryGetValue(rec.ParentIno, out var parent)) continue;

                var childName = FsName.FromOwnedBytes(rec.Name);
                var child = new Dentry(childName, null, parent, this);
                if (parent.Inode is SilkInode dirInode)
                    dirInode.RegisterChild(parent, childName, child);
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

    internal MetadataSessionLease AcquireMetadataSession(out SilkMetadataSession session)
    {
        Monitor.Enter(_metadataSessionGate);
        try
        {
            ObjectDisposedException.ThrowIf(_metadataSessionDisposed, this);
            session = _metadataSession ??= Repository.OpenMetadataSession();
            return new MetadataSessionLease(this);
        }
        catch
        {
            Monitor.Exit(_metadataSessionGate);
            throw;
        }
    }

    private void DisposeMetadataSession()
    {
        lock (_metadataSessionGate)
        {
            if (_metadataSessionDisposed)
                return;

            _metadataSession?.Dispose();
            _metadataSession = null;
            _metadataSessionDisposed = true;
        }
    }

    internal readonly struct MetadataSessionLease : IDisposable
    {
        private readonly SilkSuperBlock? _owner;

        public MetadataSessionLease(SilkSuperBlock owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            if (_owner != null)
                Monitor.Exit(_owner._metadataSessionGate);
        }
    }
}

public sealed class SilkInode : MappingBackedInode, IHostMappedCacheDropper
{
    private static readonly FsName DotName = FsName.FromString(".");
    private static readonly FsName DotDotName = FsName.FromString("..");
    private static readonly AsyncLocal<int> NamespaceMutationDepth = new();
    private readonly object _flockGate = new();
    private readonly HashSet<LinuxFile> _sharedHolders = [];
    private readonly FsNameSet _childNames = [];
    private readonly HashSet<long> _dirtyPageIndexes = [];
    private readonly Lock _dirtyPageLock = new();
    private readonly Lock _mappedCacheLock = new();
    private readonly SilkRepository _repository;
    private readonly FsNameMap<byte[]> _xAttrs = [];
    private List<DirectoryEntry>? _cachedEntries;
    private MappedFilePageCache? _mappedPageCache;
    private DateTime _atime = DateTime.Now;
    private DateTime _ctime = DateTime.Now;
    private LinuxFile? _exclusiveHolder;
    private int _metadataDirty;
    private int _metadataMutationSuppressionDepth;
    private int _lockType;
    private int _mode;
    private DateTime _mtime = DateTime.Now;
    private uint _rdev;
    private ulong _size;
    private byte[]? _symlinkData;
    private int _uid;
    private int _gid;

    public SilkInode(ulong ino, SilkSuperBlock sb, SilkRepository repository)
    {
        Ino = ino;
        SuperBlock = sb;
        MTime = ATime = CTime = DateTime.Now;
        _repository = repository;
    }

    private SilkSuperBlock SilkSb => (SilkSuperBlock)SuperBlock;

    public override bool SupportsMmap => Type == InodeType.File;

    protected override AddressSpacePolicy.AddressSpaceCacheClass? MappingCacheClass =>
        AddressSpacePolicy.AddressSpaceCacheClass.File;

    private static bool IsNamespaceMutationSuppressed => NamespaceMutationDepth.Value > 0;
    private bool IsMetadataMutationTrackingSuppressed => _metadataMutationSuppressionDepth > 0;

    private void TouchDirectoryMutationTimestamps()
    {
        var now = DateTime.Now;
        MTime = now;
        CTime = now;
    }

    private void AttachNamespaceChild(Dentry parentDentry, Dentry childDentry, string reason)
    {
        parentDentry.CacheChild(childDentry, reason);
    }

    private bool DetachNamespaceChild(Dentry parentDentry, ReadOnlySpan<byte> name, string reason,
        out Dentry? removed)
    {
        return parentDentry.TryUncacheChild(name, reason, out removed);
    }

    public void RegisterChild(Dentry parentDentry, FsName name, Dentry childDentry)
    {
        lock (Lock)
        {
            SilkSb.SetDentry(Ino, childDentry);
            AttachNamespaceChild(parentDentry, childDentry, "SilkInode.RegisterChild");
            _childNames.Add(name);
        }
    }

    public override int Mode
    {
        get => _mode;
        set
        {
            if (_mode == value)
                return;
            _mode = value;
            OnMetadataFieldChanged();
        }
    }

    public override int Uid
    {
        get => _uid;
        set
        {
            if (_uid == value)
                return;
            _uid = value;
            OnMetadataFieldChanged();
        }
    }

    public override int Gid
    {
        get => _gid;
        set
        {
            if (_gid == value)
                return;
            _gid = value;
            OnMetadataFieldChanged();
        }
    }

    public override ulong Size
    {
        get => _size;
        set
        {
            if (_size == value)
                return;
            _size = value;
            OnMetadataFieldChanged();
        }
    }

    public override DateTime MTime
    {
        get => _mtime;
        set
        {
            if (_mtime == value)
                return;
            _mtime = value;
            OnMetadataFieldChanged();
        }
    }

    public override DateTime ATime
    {
        get => _atime;
        set
        {
            if (_atime == value)
                return;
            _atime = value;
            OnMetadataFieldChanged();
        }
    }

    public override DateTime CTime
    {
        get => _ctime;
        set
        {
            if (_ctime == value)
                return;
            _ctime = value;
            OnMetadataFieldChanged();
        }
    }

    public override uint Rdev
    {
        get => _rdev;
        set
        {
            if (_rdev == value)
                return;
            _rdev = value;
            OnMetadataFieldChanged();
        }
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

    private IDisposable SuppressMetadataMutationTracking()
    {
        return new MetadataMutationTrackingScope(this);
    }

    private SilkSuperBlock.MetadataSessionLease EnterMetadataSessionScope(out SilkMetadataSession session)
    {
        return SilkSb.AcquireMetadataSession(out session);
    }

    internal void RestorePersistedMetadata(SilkInodeRecord record)
    {
        using var suppression = SuppressMetadataMutationTracking();
        Type = MapInodeType(record.Kind);
        Mode = record.Mode;
        Uid = record.Uid;
        Gid = record.Gid;
        Rdev = (uint)record.Rdev;
        Size = (ulong)Math.Max(0, record.Size);
        RestorePersistedTimestamps(record);
    }

    internal void RestorePersistedTimestamps(SilkInodeRecord record)
    {
        using var suppression = SuppressMetadataMutationTracking();
        ATime = SilkSuperBlock.FromUnixNanoseconds(record.ATimeNs);
        MTime = SilkSuperBlock.FromUnixNanoseconds(record.MTimeNs);
        CTime = SilkSuperBlock.FromUnixNanoseconds(record.CTimeNs);
    }

    public void LoadXAttrsFromMetadata(SilkMetadataSession session)
    {
        lock (Lock)
        {
            _xAttrs.Clear();
        foreach (var kv in session.ListXAttrs((long)Ino))
                _xAttrs.Set(FsName.FromBytes(kv.Key), kv.Value.ToArray());
        }
    }

    public void LoadDataFromMetadata()
    {
        if (Type != InodeType.Symlink) return;
        var data = _repository.ReadLiveInodeData((long)Ino);
        if (data == null) return;

        _symlinkData = data.Length == 0 ? Array.Empty<byte>() : [.. data];
        Size = (ulong)_symlinkData.Length;
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

    private void OnMetadataFieldChanged()
    {
        if (!IsMetadataMutationTrackingSuppressed)
            MarkMetadataDirty();
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

    internal void FlushMetadataForShutdown(SilkMetadataSession session)
    {
        FlushDirtyMetadataIfNeeded(session);
    }

    internal void PersistMetadataImmediately()
    {
        FlushDirtyMetadataIfNeeded();
    }

    public override int UpdateTimes(DateTime? atime, DateTime? mtime, DateTime? ctime)
    {
        var rc = base.UpdateTimes(atime, mtime, ctime);
        if (rc == 0)
            MarkMetadataDirty();

        return rc;
    }

    protected internal override int BackendRead(LinuxFile? linuxFile, Span<byte> buffer, long offset)
    {
        if (Type == InodeType.Directory) return 0;
        if (Type == InodeType.Symlink)
            lock (Lock)
            {
                EnsureSymlinkDataLoadedLocked();
                if (_symlinkData == null || offset >= _symlinkData.Length) return 0;
                var n = Math.Min(buffer.Length, _symlinkData.Length - (int)offset);
                _symlinkData.AsSpan((int)offset, n).CopyTo(buffer);
                return n;
            }

        if (offset < 0) return -(int)Errno.EINVAL;

        var fileSize = (long)Size;
        if (offset >= fileSize) return 0;

        try
        {
            if (linuxFile?.PrivateData is SafeFileHandle handle)
                try
                {
                    return RandomAccess.Read(handle, buffer, offset);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    // O_WRONLY handles cannot service read-for-prefill; fall back to a temporary read handle.
                }

            using var tempHandle = _repository.OpenLiveInodeHandle((long)Ino, FileMode.Open, FileAccess.Read);
            return RandomAccess.Read(tempHandle, buffer, offset);
        }
        catch (FileNotFoundException)
        {
            return -(int)Errno.EIO;
        }
        catch (DirectoryNotFoundException)
        {
            return -(int)Errno.EIO;
        }
        catch (IOException)
        {
            return -(int)Errno.EIO;
        }
    }

    public override int Readlink(out byte[]? target)
    {
        lock (Lock)
        {
            EnsureSymlinkDataLoadedLocked();
            if (Type != InodeType.Symlink || _symlinkData == null)
            {
                target = null;
                return -(int)Errno.EINVAL;
            }

            target = _symlinkData;
            return 0;
        }
    }

    public override int Flock(LinuxFile linuxFile, int operation)
    {
        var nonBlock = (operation & LinuxConstants.LOCK_NB) != 0;
        var op = operation & ~LinuxConstants.LOCK_NB;

        lock (_flockGate)
        {
            while (true)
            {
                if (op == LinuxConstants.LOCK_UN)
                {
                    if (_exclusiveHolder == linuxFile) _exclusiveHolder = null;
                    _sharedHolders.Remove(linuxFile);
                    if (_exclusiveHolder == null && _sharedHolders.Count == 0) _lockType = 0;
                    Monitor.PulseAll(_flockGate);
                    return 0;
                }

                var canAcquire = false;
                if (op == LinuxConstants.LOCK_SH)
                {
                    if (_exclusiveHolder == null || _exclusiveHolder == linuxFile) canAcquire = true;
                }
                else if (op == LinuxConstants.LOCK_EX)
                {
                    if (_lockType == 0) canAcquire = true;
                    else if (_exclusiveHolder == linuxFile) canAcquire = true;
                    else if (_sharedHolders.Count == 1 && _sharedHolders.Contains(linuxFile) &&
                             _exclusiveHolder == null)
                        canAcquire = true;
                }

                if (canAcquire)
                {
                    if (op == LinuxConstants.LOCK_SH)
                    {
                        if (_exclusiveHolder == linuxFile) _exclusiveHolder = null;
                        _sharedHolders.Add(linuxFile);
                        _lockType = 1;
                    }
                    else
                    {
                        _sharedHolders.Remove(linuxFile);
                        _exclusiveHolder = linuxFile;
                        _lockType = 2;
                    }

                    return 0;
                }

                if (nonBlock) return -(int)Errno.EAGAIN;
                Monitor.Wait(_flockGate);
            }
        }
    }

    public override bool RevalidateCachedChild(Dentry parent, ReadOnlySpan<byte> name, Dentry cached)
    {
        using var metadataScope = EnterMetadataSessionScope(out var session);
        lock (Lock)
        {
            return TryHydrateChildDentry(parent, name, cached, session);
        }
    }

    public override Dentry? Lookup(ReadOnlySpan<byte> name)
    {
        using var metadataScope = EnterMetadataSessionScope(out var session);
        lock (Lock)
        {
            if (Type != InodeType.Directory) return null;
            if (Dentries.Count == 0) return null;
            var primaryDentry = Dentries[0];
            if (SilkSb.TryGetDentry(Ino, name, out var cached))
            {
                if (!TryHydrateChildDentry(primaryDentry, name, cached, session))
                {
                    _ = SilkSb.RemoveDentry(Ino, name, out _);
                    _ = primaryDentry.TryUncacheChild(name, "SilkInode.Lookup.refresh-missing", out _);
                    _childNames.Remove(name);
                    return null;
                }

                primaryDentry.CacheChild(cached, "SilkInode.Lookup.refresh-hit");
                _childNames.Add(cached.Name);
                return cached;
            }

            var childIno = session.LookupDentry((long)Ino, name);
            if (childIno == null) return null;

            var childInode = SilkSb.GetOrLoadInode(childIno.Value);
            if (childInode == null) return null;

            var childName = FsName.FromBytes(name);
            var created = new Dentry(childName, childInode, primaryDentry, SuperBlock);
            SilkSb.SetDentry(Ino, created);

            primaryDentry.CacheChild(created, "SilkInode.Lookup.refresh-create");
            _childNames.Add(childName);
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
                new() { Name = FsName.FromString("."), Ino = Ino, Type = InodeType.Directory },
                new() { Name = FsName.FromString(".."), Ino = Ino, Type = InodeType.Directory }
            };

            foreach (var rec in session.ListDentriesByParent((long)Ino))
            {
                InodeType childType;
                if (SilkSb.TryGetDentry(Ino, rec.Name, out var cached) && cached.Inode != null)
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
                    Name = FsName.FromOwnedBytes(rec.Name),
                    Ino = (ulong)rec.Ino,
                    Type = childType
                });
            }

            _cachedEntries = entries;
            return entries;
        }
    }

    private bool TryHydrateChildDentry(Dentry parent, ReadOnlySpan<byte> name, Dentry childDentry,
        SilkMetadataSession session)
    {
        if (childDentry.Inode != null)
            return true;

        var childIno = session.LookupDentry((long)Ino, name);
        if (childIno == null)
            return false;

        var childInode = SilkSb.GetOrLoadInode(childIno.Value);
        if (childInode == null)
            return false;

        if (childDentry.Inode == null)
            childDentry.Instantiate(childInode);
        childDentry.Parent ??= parent;
        if (!childDentry.Name.Equals(name))
            childDentry.Name = FsName.FromBytes(name);
        return true;
    }

    private void EnsureSymlinkDataLoadedLocked()
    {
        if (Type != InodeType.Symlink) return;
        if (_symlinkData != null) return;

        var data = _repository.ReadLiveInodeData((long)Ino) ?? Array.Empty<byte>();
        _symlinkData = data.Length == 0 ? Array.Empty<byte>() : data;
        Size = (ulong)_symlinkData.Length;
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

        if (inode.LinkCount == 0)
        {
            tx.DeleteInode((long)inode.Ino);
            return;
        }

        UpsertInodeMetadata(tx, inode);
    }

    private static void DeleteInodeMetadataIfReleased(SilkMetadataSession session, Inode? inode)
    {
        if (inode == null || inode.IsFinalized)
            return;

        session.DeleteInode((long)inode.Ino);
    }

    private void BestEffortRollbackCreatedEntry(Dentry dentry)
    {
        try
        {
            _ = UnlinkLocal(dentry.Name.Bytes);
        }
        catch
        {
            // Preserve the original create/open failure; rollback is best-effort.
        }
    }

    private int CreateLocal(Dentry dentry, int mode, int uid, int gid)
    {
        lock (Lock)
        {
            if (Type != InodeType.Directory) return -(int)Errno.ENOTDIR;
            if (Dentries.Count == 0) return -(int)Errno.ENOENT;

            var primaryDentry = Dentries[0];
            if (SilkSb.ContainsDentry(Ino, dentry.Name.Bytes)) return -(int)Errno.EEXIST;

            var inode = (SilkInode)SilkSb.AllocInode();
            inode.Type = InodeType.File;
            inode.Mode = mode;
            inode.Uid = uid;
            inode.Gid = gid;
            inode.SetInitialLinkCount(1, "SilkInode.CreateLocal");

            dentry.Instantiate(inode);

            SilkSb.SetDentry(Ino, dentry);
            AttachNamespaceChild(primaryDentry, dentry, "SilkInode.CreateLocal");
            _childNames.Add(dentry.Name);
            TouchDirectoryMutationTimestamps();

            return 0;
        }
    }

    private int MkdirLocal(Dentry dentry, int mode, int uid, int gid)
    {
        lock (Lock)
        {
            if (Type != InodeType.Directory) return -(int)Errno.ENOTDIR;
            if (Dentries.Count == 0) return -(int)Errno.ENOENT;

            var primaryDentry = Dentries[0];
            if (SilkSb.ContainsDentry(Ino, dentry.Name.Bytes)) return -(int)Errno.EEXIST;

            var inode = (SilkInode)SilkSb.AllocInode();
            inode.Type = InodeType.Directory;
            inode.Mode = mode;
            inode.Uid = uid;
            inode.Gid = gid;
            NamespaceOps.OnDirectoryCreated(this, inode, "SilkInode.MkdirLocal");

            dentry.Instantiate(inode);

            SilkSb.SetDentry(Ino, dentry);
            AttachNamespaceChild(primaryDentry, dentry, "SilkInode.MkdirLocal");
            _childNames.Add(dentry.Name);
            TouchDirectoryMutationTimestamps();

            return 0;
        }
    }

    private int MknodLocal(Dentry dentry, int mode, int uid, int gid, InodeType type, uint rdev)
    {
        lock (Lock)
        {
            if (Type != InodeType.Directory) return -(int)Errno.ENOTDIR;
            if (type != InodeType.CharDev && type != InodeType.BlockDev && type != InodeType.Fifo &&
                type != InodeType.Socket)
                return -(int)Errno.EINVAL;
            if (Dentries.Count == 0) return -(int)Errno.ENOENT;

            var primaryDentry = Dentries[0];
            if (SilkSb.ContainsDentry(Ino, dentry.Name.Bytes)) return -(int)Errno.EEXIST;

            var inode = (SilkInode)SilkSb.AllocInode();
            inode.Type = type;
            inode.Mode = mode;
            inode.Uid = uid;
            inode.Gid = gid;
            inode.Rdev = rdev;
            inode.SetInitialLinkCount(1, "SilkInode.MknodLocal");

            dentry.Instantiate(inode);

            SilkSb.SetDentry(Ino, dentry);
            AttachNamespaceChild(primaryDentry, dentry, "SilkInode.MknodLocal");
            _childNames.Add(dentry.Name);
            TouchDirectoryMutationTimestamps();
            return 0;
        }
    }

    private int SymlinkLocal(Dentry dentry, byte[] target, int uid, int gid)
    {
        lock (Lock)
        {
            if (Type != InodeType.Directory) return -(int)Errno.ENOTDIR;
            if (Dentries.Count == 0) return -(int)Errno.ENOENT;

            var primaryDentry = Dentries[0];
            if (SilkSb.ContainsDentry(Ino, dentry.Name.Bytes)) return -(int)Errno.EEXIST;

            var inode = (SilkInode)SilkSb.AllocInode();
            inode.Type = InodeType.Symlink;
            inode.Mode = 0x1FF;
            inode.Uid = uid;
            inode.Gid = gid;
            inode._symlinkData = target.ToArray();
            inode.Size = (ulong)inode._symlinkData.Length;
            inode.SetInitialLinkCount(1, "SilkInode.SymlinkLocal");

            dentry.Instantiate(inode);

            SilkSb.SetDentry(Ino, dentry);
            AttachNamespaceChild(primaryDentry, dentry, "SilkInode.SymlinkLocal");
            _childNames.Add(dentry.Name);
            TouchDirectoryMutationTimestamps();

            return 0;
        }
    }

    private int LinkLocal(Dentry dentry, Inode oldInode)
    {
        lock (Lock)
        {
            if (Type != InodeType.Directory) return -(int)Errno.ENOTDIR;
            if (Dentries.Count == 0) return -(int)Errno.ENOENT;

            var primaryDentry = Dentries[0];
            if (SilkSb.ContainsDentry(Ino, dentry.Name.Bytes)) return -(int)Errno.EEXIST;

            dentry.Instantiate(oldInode);
            NamespaceOps.OnLinkAdded(oldInode, "SilkInode.LinkLocal");

            SilkSb.SetDentry(Ino, dentry);
            AttachNamespaceChild(primaryDentry, dentry, "SilkInode.LinkLocal");
            _childNames.Add(dentry.Name);
            TouchDirectoryMutationTimestamps();
            return 0;
        }
    }

    private int UnlinkLocal(ReadOnlySpan<byte> name)
    {
        lock (Lock)
        {
            if (Dentries.Count == 0) return 0;
            var primaryDentry = Dentries[0];
            if (!SilkSb.TryGetDentry(Ino, name, out var dentry))
                return -(int)Errno.ENOENT;

            if (dentry.Inode?.Type == InodeType.Directory)
                return -(int)Errno.EISDIR;

            _ = SilkSb.RemoveDentry(Ino, name, out _);
            _ = DetachNamespaceChild(primaryDentry, name, "SilkInode.UnlinkLocal", out _);
            var unlinkedInode = dentry.Inode;
            if (unlinkedInode != null)
            {
                NamespaceOps.OnEntryRemoved(unlinkedInode, "SilkInode.UnlinkLocal");
                if (!unlinkedInode.HasActiveRuntimeRefs)
                    dentry.UnbindInode("SilkInode.UnlinkLocal");
            }

            _childNames.Remove(name);
            TouchDirectoryMutationTimestamps();
            return 0;
        }
    }

    private int RmdirLocal(ReadOnlySpan<byte> name)
    {
        lock (Lock)
        {
            if (Dentries.Count == 0) return 0;
            var primaryDentry = Dentries[0];
            if (!SilkSb.TryGetDentry(Ino, name, out var dentry))
                return -(int)Errno.ENOENT;

            if (dentry.Inode?.Type != InodeType.Directory)
                return -(int)Errno.ENOTDIR;

            if (dentry.Children.Count > 0)
                return -(int)Errno.ENOTEMPTY;

            _ = SilkSb.RemoveDentry(Ino, name, out _);
            _ = DetachNamespaceChild(primaryDentry, name, "SilkInode.RmdirLocal", out _);
            var removedInode = dentry.Inode;
            if (removedInode != null)
            {
                NamespaceOps.OnDirectoryRemoved(this, removedInode, "SilkInode.RmdirLocal");
                dentry.UnbindInode("SilkInode.RmdirLocal");
            }

            _childNames.Remove(name);
            TouchDirectoryMutationTimestamps();
            return 0;
        }
    }

    private int RenameLocal(ReadOnlySpan<byte> oldName, SilkInode targetParent, ReadOnlySpan<byte> newName)
    {
        var oldNameFs = FsName.FromBytes(oldName);
        var newNameFs = FsName.FromBytes(newName);
        if (oldNameFs.IsDotOrDotDot || newNameFs.IsDotOrDotDot)
            return -(int)Errno.EINVAL;

        Inode first = this;
        Inode second = targetParent;
        if (first.Ino > second.Ino)
        {
            first = targetParent;
            second = this;
        }

        lock (first.Lock)
        {
            if (first != second)
            {
                lock (second.Lock)
                {
                    return RenameLocalLocked(oldName, targetParent, newName);
                }
            }

            return RenameLocalLocked(oldName, targetParent, newName);
        }
    }

    private int RenameLocalLocked(ReadOnlySpan<byte> oldName, SilkInode targetParent, ReadOnlySpan<byte> newName)
    {
        if (Dentries.Count == 0) return -(int)Errno.ENOENT;
        var oldPrimary = Dentries[0];
        if (targetParent.Dentries.Count == 0) return -(int)Errno.ENOENT;
        var newPrimary = targetParent.Dentries[0];

        if (!oldPrimary.Children.TryGetValue(oldName, out var dentry))
        {
            foreach (var parentDentry in Dentries)
                if (parentDentry.Children.TryGetValue(oldName, out dentry))
                    break;

            if (dentry == null && !SilkSb.TryGetDentry(Ino, oldName, out dentry))
                return -(int)Errno.ENOENT;
        }

        if (dentry.Inode!.Type == InodeType.Directory)
        {
            var curr = newPrimary;
            while (curr != null)
            {
                if (curr == dentry)
                    return -(int)Errno.EINVAL;
                if (curr == curr.Parent) break;
                curr = curr.Parent;
            }
        }

        if (SilkSb.TryGetDentry(targetParent.Ino, newName, out var existingDentry))
        {
            if (ReferenceEquals(existingDentry.Inode, dentry.Inode))
                return 0;

            var sourceEntryIsDirectory = dentry.Inode.Type == InodeType.Directory;
            var targetIsDirectory = existingDentry.Inode!.Type == InodeType.Directory;
            if (sourceEntryIsDirectory && !targetIsDirectory)
                return -(int)Errno.ENOTDIR;
            if (!sourceEntryIsDirectory && targetIsDirectory)
                return -(int)Errno.EISDIR;

            if (targetIsDirectory)
            {
                if (existingDentry.Children.Count > 0)
                    return -(int)Errno.ENOTEMPTY;
                var rmdirRc = targetParent.RmdirLocal(newName);
                if (rmdirRc < 0)
                    return rmdirRc;
            }
            else
            {
                var unlinkRc = targetParent.UnlinkLocal(newName);
                if (unlinkRc < 0)
                    return unlinkRc;
            }
        }

        var sourceIsDirectory = dentry.Inode.Type == InodeType.Directory;
        var movedAcrossParents = sourceIsDirectory && !ReferenceEquals(this, targetParent);

        _ = SilkSb.RemoveDentry(Ino, oldName, out _);
        _ = DetachNamespaceChild(oldPrimary, oldName, "SilkInode.RenameLocal.old-parent", out _);
        _childNames.Remove(oldName);

        dentry.Parent = newPrimary;
        dentry.Name = FsName.FromBytes(newName);
        SilkSb.SetDentry(targetParent.Ino, dentry);

        AttachNamespaceChild(newPrimary, dentry, "SilkInode.RenameLocal.new-parent");
        targetParent._childNames.Add(dentry.Name);
        TouchDirectoryMutationTimestamps();
        targetParent.TouchDirectoryMutationTimestamps();

        if (movedAcrossParents)
            NamespaceOps.OnDirectoryMovedAcrossParents(this, targetParent, "SilkInode.RenameLocal");

        return 0;
    }

    public override int Create(Dentry dentry, int mode, int uid, int gid)
    {
        using var metadataScope = EnterMetadataSessionScope(out var session);
        var rc = CreateLocal(dentry, mode, uid, gid);
        if (rc < 0)
            return rc;

        try
        {
            if (dentry.Inode is SilkInode child)
            {
                if (child.Type == InodeType.Symlink)
                    child.PersistSymlinkData();
                else if (child.Type == InodeType.File)
                    child.EnsureRegularFileBackingExists();
            }

            session.ExecuteTransaction(tx =>
            {
                UpsertInodeMetadata(tx, dentry.Inode!);
                tx.UpsertDentry((long)Ino, dentry.Name.Bytes, (long)dentry.Inode!.Ino);
                tx.ClearWhiteout((long)Ino, dentry.Name.Bytes);
            });
        }
        catch
        {
            BestEffortRollbackCreatedEntry(dentry);
            throw;
        }

        InvalidateEntriesCache();
        return 0;
    }

    public override int Symlink(Dentry dentry, string target, int uid, int gid)
    {
        ArgumentNullException.ThrowIfNull(target);
        return Symlink(dentry, FsEncoding.EncodeUtf8(target), uid, gid);
    }

    public override int Mkdir(Dentry dentry, int mode, int uid, int gid)
    {
        using var metadataScope = EnterMetadataSessionScope(out var session);
        var rc = MkdirLocal(dentry, mode, uid, gid);
        if (rc < 0)
            return rc;

        session.ExecuteTransaction(tx =>
        {
            UpsertInodeMetadata(tx, this);
            UpsertInodeMetadata(tx, dentry.Inode!);
            tx.UpsertDentry((long)Ino, dentry.Name.Bytes, (long)dentry.Inode!.Ino);
            tx.ClearWhiteout((long)Ino, dentry.Name.Bytes);
        });
        InvalidateEntriesCache();
        return 0;
    }

    public override int Mknod(Dentry dentry, int mode, int uid, int gid, InodeType type, uint rdev)
    {
        using var metadataScope = EnterMetadataSessionScope(out var session);
        var rc = MknodLocal(dentry, mode, uid, gid, type, rdev);
        if (rc < 0)
            return rc;

        session.ExecuteTransaction(tx =>
        {
            UpsertInodeMetadata(tx, dentry.Inode!);
            tx.UpsertDentry((long)Ino, dentry.Name.Bytes, (long)dentry.Inode!.Ino);
            tx.ClearWhiteout((long)Ino, dentry.Name.Bytes);
            if (type == InodeType.CharDev && rdev == 0)
            {
                var parentIno = (long)Ino;
                if (dentry.Name.Bytes.SequenceEqual(SilkMetadataStore.OpaqueMarkerNameBytes))
                    tx.MarkOpaque(parentIno);
                else if (dentry.Name.Bytes.StartsWith(".wh."u8) && dentry.Name.Length > 4)
                    tx.MarkWhiteout(parentIno, dentry.Name.Bytes[4..]);
            }
        });
        InvalidateEntriesCache();
        return 0;
    }

    public override int Symlink(Dentry dentry, byte[] target, int uid, int gid)
    {
        using var metadataScope = EnterMetadataSessionScope(out var session);
        var rc = SymlinkLocal(dentry, target, uid, gid);
        if (rc < 0)
            return rc;

        try
        {
            if (dentry.Inode is SilkInode child)
                child.PersistSymlinkData();

            session.ExecuteTransaction(tx =>
            {
                UpsertInodeMetadata(tx, dentry.Inode!);
                tx.UpsertDentry((long)Ino, dentry.Name.Bytes, (long)dentry.Inode!.Ino);
                tx.ClearWhiteout((long)Ino, dentry.Name.Bytes);
            });
        }
        catch
        {
            BestEffortRollbackCreatedEntry(dentry);
            throw;
        }

        InvalidateEntriesCache();
        return 0;
    }

    public override int Link(Dentry dentry, Inode oldInode)
    {
        using var metadataScope = EnterMetadataSessionScope(out var session);
        var rc = LinkLocal(dentry, oldInode);
        if (rc < 0)
            return rc;

        session.ExecuteTransaction(tx =>
        {
            UpsertInodeMetadata(tx, oldInode);
            tx.UpsertDentry((long)Ino, dentry.Name.Bytes, (long)oldInode.Ino);
            tx.ClearWhiteout((long)Ino, dentry.Name.Bytes);
        });
        InvalidateEntriesCache();
        return 0;
    }

    public override void Open(LinuxFile linuxFile)
    {
        if (Type != InodeType.File) return;

        var mode = FileMode.Open;
        const int OAccMode = 3;
        var access = ((int)linuxFile.Flags & OAccMode) switch
        {
            (int)FileFlags.O_WRONLY => FileAccess.Write,
            (int)FileFlags.O_RDWR => FileAccess.ReadWrite,
            _ => FileAccess.Read
        };

        var hasCreate = (linuxFile.Flags & FileFlags.O_CREAT) != 0;
        var hasExcl = (linuxFile.Flags & FileFlags.O_EXCL) != 0;
        if (hasCreate && hasExcl) mode = FileMode.CreateNew;
        else if (hasCreate) mode = FileMode.OpenOrCreate;

        linuxFile.PrivateData = _repository.OpenLiveInodeHandle((long)Ino, mode, access);
    }

    public override void Release(LinuxFile linuxFile)
    {
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
        _ = FlushWritebackToDurable(linuxFile);
    }

    public override int Unlink(ReadOnlySpan<byte> name)
    {
        using var metadataScope = EnterMetadataSessionScope(out var session);
        var victim = Lookup(name)?.Inode;
        var rc = UnlinkLocal(name);
        if (rc < 0)
            return rc;
        if (IsNamespaceMutationSuppressed)
            return 0;

        var nameBytes = name.ToArray();
        session.ExecuteTransaction(tx =>
        {
            tx.RemoveDentry((long)Ino, nameBytes);
            tx.ClearWhiteout((long)Ino, nameBytes);
            UpsertInodeMetadataIfLive(tx, victim);
        });
        DeleteInodeMetadataIfReleased(session, victim);

        InvalidateEntriesCache();
        return 0;
    }

    public override int Unlink(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Unlink(FsEncoding.EncodeUtf8(name));
    }

    public override int Rmdir(ReadOnlySpan<byte> name)
    {
        using var metadataScope = EnterMetadataSessionScope(out var session);
        var victim = Lookup(name)?.Inode;
        var rc = RmdirLocal(name);
        if (rc < 0)
            return rc;
        if (IsNamespaceMutationSuppressed)
            return 0;

        var nameBytes = name.ToArray();
        session.ExecuteTransaction(tx =>
        {
            UpsertInodeMetadataIfLive(tx, this);
            tx.RemoveDentry((long)Ino, nameBytes);
            tx.ClearWhiteout((long)Ino, nameBytes);
            UpsertInodeMetadataIfLive(tx, victim);
        });
        DeleteInodeMetadataIfReleased(session, victim);

        InvalidateEntriesCache();
        return 0;
    }

    public override int Rmdir(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Rmdir(FsEncoding.EncodeUtf8(name));
    }

    public override int Rename(ReadOnlySpan<byte> oldName, Inode newParent, ReadOnlySpan<byte> newName)
    {
        using var metadataScope = EnterMetadataSessionScope(out var session);
        if (Lookup(oldName)?.Inode == null)
            return -(int)Errno.ENOENT;
        if (newParent is not SilkInode targetParent)
            return -(int)Errno.EXDEV;

        var overwrittenInode = newParent.Lookup(newName)?.Inode;
        using (SuppressNamespaceMetadataMutations())
        {
            var rc = RenameLocal(oldName, targetParent, newName);
            if (rc < 0)
                return rc;
        }

        var movedInode = newParent.Lookup(newName)?.Inode;
        var oldNameBytes = oldName.ToArray();
        var newNameBytes = newName.ToArray();
        session.ExecuteTransaction(tx =>
        {
            tx.RemoveDentry((long)Ino, oldNameBytes);
            if (movedInode != null && !movedInode.IsFinalized)
            {
                UpsertInodeMetadata(tx, movedInode);
                tx.UpsertDentry((long)newParent.Ino, newNameBytes, (long)movedInode.Ino);
                tx.ClearWhiteout((long)newParent.Ino, newNameBytes);
            }

            UpsertInodeMetadataIfLive(tx, this);
            UpsertInodeMetadataIfLive(tx, newParent);
            if (!ReferenceEquals(overwrittenInode, movedInode))
                UpsertInodeMetadataIfLive(tx, overwrittenInode);
        });
        if (!ReferenceEquals(overwrittenInode, movedInode))
            DeleteInodeMetadataIfReleased(session, overwrittenInode);

        InvalidateEntriesCache();
        if (newParent is SilkInode parentSilk)
            parentSilk.InvalidateEntriesCache();
        return 0;
    }

    public override int Rename(string oldName, Inode newParent, string newName)
    {
        ArgumentNullException.ThrowIfNull(oldName);
        ArgumentNullException.ThrowIfNull(newName);
        return Rename(FsEncoding.EncodeUtf8(oldName), newParent, FsEncoding.EncodeUtf8(newName));
    }

    public override int ReadToHost(FiberTask? task, LinuxFile linuxFile, Span<byte> buffer,
        long offset)
    {
        _ = task;
        return ReadWithPageCache(linuxFile, buffer, offset);
    }

    protected internal override int BackendWrite(LinuxFile? linuxFile, ReadOnlySpan<byte> buffer, long offset)
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
            var now = DateTime.Now;
            MTime = now;
            CTime = now;
            MarkMetadataDirty();
            return buffer.Length;
        }
        finally
        {
            if (linuxFile?.PrivateData is not SafeFileHandle)
                handle?.Dispose();
        }
    }

    public override int WriteFromHost(FiberTask? task, LinuxFile linuxFile,
        ReadOnlySpan<byte> buffer, long offset)
    {
        _ = task;
        var prepareRc = PrepareHostMappedWrite(linuxFile, buffer.Length, offset);
        if (prepareRc < 0)
            return prepareRc;

        var rc = WriteWithPageCache(linuxFile, buffer, offset);
        if (rc > 0)
            MarkMetadataDirty();

        return rc;
    }

    internal bool FlushDirtyDataIfNeeded(LinuxFile? linuxFile)
    {
        if (Type != InodeType.File || Mapping == null)
            return true;

        bool hasDirtyPages;
        lock (_dirtyPageLock)
        {
            hasDirtyPages = _dirtyPageIndexes.Count > 0;
        }

        if (!hasDirtyPages)
            return true;

        var rc = WritePages(linuxFile, new WritePagesRequest(0, long.MaxValue, PageWritebackMode.Durable));
        if (rc < 0)
            return false;

        lock (_dirtyPageLock)
        {
            return _dirtyPageIndexes.Count == 0;
        }
    }

    protected override SafeFileHandle? GetOpenHandle(LinuxFile? linuxFile)
    {
        return linuxFile?.PrivateData as SafeFileHandle;
    }

    protected override bool FlushMappedWindowsToBacking()
    {
        if (!SuperBlock.MemoryContext.HostMemoryMapGeometry.SupportsMappedFileBackend)
            return true;

        lock (_mappedCacheLock)
        {
            return _mappedPageCache?.TryFlushAllActiveWritableWindows() ?? true;
        }
    }

    protected override int FlushDeferredMetadata(LinuxFile? linuxFile)
    {
        _ = linuxFile;
        FlushDirtyMetadataIfNeeded();
        return 0;
    }

    protected internal override int FlushWritebackToDurable(LinuxFile? linuxFile)
    {
        return FlushWritebackToDurableCore(linuxFile, true);
    }

    protected internal override ValueTask<int> FlushWritebackToDurableAsync(LinuxFile? linuxFile, FiberTask? task)
    {
        return FlushWritebackToDurableCoreAsync(linuxFile, task, "silkfs.FlushToDisk", true,
            _ => -(int)Errno.EIO);
    }

    private bool HasDirtyPageAtOrAfter(long pageIndex)
    {
        lock (_dirtyPageLock)
        {
            foreach (var dirtyPageIndex in _dirtyPageIndexes)
                if (dirtyPageIndex >= pageIndex)
                    return true;

            return false;
        }
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
        if (rc == request.Length)
            return 0;

        // Buffered sparse writes can extend logical size before the live inode file is written back.
        if (HasDirtyPageAtOrAfter(request.PageIndex))
            return 0;

        return -(int)Errno.EIO;
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
            toFlush = [];
            foreach (var pageIndex in _dirtyPageIndexes)
            {
                if (pageIndex < request.StartPageIndex || pageIndex > request.EndPageIndex)
                    continue;
                toFlush.Add(pageIndex);
            }
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

    public override int SetXAttr(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value, int flags)
    {
        const int XATTR_CREATE = 1;
        const int XATTR_REPLACE = 2;

        int rc;
        lock (Lock)
        {
            var exists = _xAttrs.TryGetValue(name, out _);
            if ((flags & XATTR_CREATE) != 0 && exists) return -(int)Errno.EEXIST;
            if ((flags & XATTR_REPLACE) != 0 && !exists) return -(int)Errno.ENODATA;
            _xAttrs.Set(FsName.FromBytes(name), value.ToArray());
            rc = 0;
        }

        if (rc == 0)
        {
            using var metadataScope = EnterMetadataSessionScope(out var session);
            session.SetXAttr((long)Ino, name, value);
        }

        return rc;
    }

    public override int SetXAttr(string name, ReadOnlySpan<byte> value, int flags)
    {
        ArgumentNullException.ThrowIfNull(name);
        return SetXAttr(FsEncoding.EncodeUtf8(name), value, flags);
    }

    public override int GetXAttr(ReadOnlySpan<byte> name, Span<byte> value)
    {
        lock (Lock)
        {
            if (!_xAttrs.TryGetValue(name, out var data)) return -(int)Errno.ENODATA;
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
        lock (Lock)
        {
            var names = _xAttrs.Select(static kv => kv.Key).OrderBy(static k => k, FsName.BytewiseComparer).ToArray();
            var required = 0;
            foreach (var n in names) required += n.Length + 1;
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
        int rc;
        lock (Lock)
        {
            rc = _xAttrs.Remove(name) ? 0 : -(int)Errno.ENODATA;
        }

        if (rc == 0)
        {
            using var metadataScope = EnterMetadataSessionScope(out var session);
            session.RemoveXAttr((long)Ino, name);
        }

        return rc;
    }

    public override int RemoveXAttr(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return RemoveXAttr(FsEncoding.EncodeUtf8(name));
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
            if (size < 0) return -(int)Errno.EINVAL;
            lock (Lock)
            {
                Array.Resize(ref _symlinkData, (int)size);
                Size = (ulong)size;
                var timestamp = DateTime.Now;
                MTime = timestamp;
                CTime = timestamp;
            }

            rc = 0;
            if (rc == 0)
                PersistSymlinkData();
            return rc;
        }

        if (Type == InodeType.Directory) return -(int)Errno.EISDIR;
        if (size < 0) return -(int)Errno.EINVAL;
        var oldSize = (long)Size;

        using (var handle = _repository.OpenLiveInodeHandle((long)Ino, FileMode.OpenOrCreate, FileAccess.ReadWrite))
        {
            RandomAccess.SetLength(handle, size);
        }

        Size = (ulong)size;
        var now = DateTime.Now;
        MTime = now;
        CTime = now;
        rc = 0;
        if (rc == 0)
        {
            MarkMetadataDirty();
        }

        return rc;
    }

    protected override void RetireHostMappedWindowsBeforeFileShrink(long newSize)
    {
        lock (_mappedCacheLock)
        {
            _mappedPageCache?.Truncate(newSize);
        }
    }

    protected override void OnFileShrinkReconciled(long previousSize, long newSize)
    {
        _ = previousSize;
        var firstDroppedPage = (newSize + LinuxConstants.PageOffsetMask) / LinuxConstants.PageSize;
        lock (_dirtyPageLock)
        {
            _dirtyPageIndexes.RemoveWhere(i => i >= firstDroppedPage);
        }
    }

    public override bool TryAcquireMappedPageHandle(LinuxFile? linuxFile, long pageIndex, long absoluteFileOffset,
        bool writable, out BackingPageHandle backingPageHandle)
    {
        backingPageHandle = default;
        if (Type != InodeType.File && Type != InodeType.Symlink) return false;
        if (absoluteFileOffset < 0) return false;
        if ((absoluteFileOffset & LinuxConstants.PageOffsetMask) != 0) return false;
        if (!TryGetLiveBackingFileLength(linuxFile, out var backingLength)) return false;

        lock (_mappedCacheLock)
        {
            var livePath = _repository.GetLiveInodePath((long)Ino);
            _mappedPageCache ??= new MappedFilePageCache(
                livePath,
                SuperBlock.MemoryContext.HostMemoryMapGeometry);
            if (!_mappedPageCache.TryAcquirePageLease(
                    pageIndex,
                    backingLength,
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

    public override bool TryFlushMappedPage(LinuxFile? linuxFile, long pageIndex,
        PageWritebackMode mode = PageWritebackMode.Durable)
    {
        _ = mode;
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
        if (LinkCount > 0)
        {
            _ = FlushDirtyDataIfNeeded(null);
            FlushDirtyMetadataIfNeeded();
        }
        _symlinkData = null;
        _childNames.Clear();
        _cachedEntries = null;
        base.OnEvictCache();
        lock (_mappedCacheLock)
        {
            _mappedPageCache?.Dispose();
            _mappedPageCache = null;
        }
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
        _repository.WriteLiveInodeData((long)Ino, _symlinkData ?? Array.Empty<byte>());
    }

    private void EnsureRegularFileBackingExists()
    {
        if (Type != InodeType.File) return;
        _repository.EnsureLiveInodeDataFile((long)Ino);
    }

    private void SyncRegularFileIfNeeded()
    {
        if (Type == InodeType.File && LinkCount > 0)
            FlushDirtyMetadataIfNeeded();
    }

    private bool TryGetLiveBackingFileLength(LinuxFile? linuxFile, out long backingLength)
    {
        backingLength = 0;
        if (Type != InodeType.File && Type != InodeType.Symlink)
            return false;

        SafeFileHandle? tempHandle = null;
        try
        {
            var handle = linuxFile?.PrivateData as SafeFileHandle;
            if (handle == null)
                tempHandle = handle = _repository.OpenLiveInodeHandle((long)Ino, FileMode.Open, FileAccess.Read);

            backingLength = RandomAccess.GetLength(handle);
            return backingLength >= 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            tempHandle?.Dispose();
        }
    }

    protected override int TryAcquireHostMappedPageLeases(LinuxFile? linuxFile, uint startPageIndex, int pageCount,
        long fileSize, bool writable, Span<IntPtr> pointers, Span<long> releaseTokens)
    {
        if ((Type != InodeType.File && Type != InodeType.Symlink) || pageCount <= 0)
            return 0;
        if (pointers.Length < pageCount)
            throw new ArgumentException("Pointer output span is smaller than page count.", nameof(pointers));
        if (releaseTokens.Length < pageCount)
            throw new ArgumentException("Release-token output span is smaller than page count.", nameof(releaseTokens));

        if (!TryGetLiveBackingFileLength(linuxFile, out var backingLength))
            backingLength = Math.Max(0, fileSize);

        lock (_mappedCacheLock)
        {
            var livePath = _repository.GetLiveInodePath((long)Ino);
            _mappedPageCache ??= new MappedFilePageCache(
                livePath,
                SuperBlock.MemoryContext.HostMemoryMapGeometry);
            return _mappedPageCache.TryAcquirePageLeases((long)startPageIndex, pageCount, backingLength, writable,
                pointers, releaseTokens);
        }
    }

    protected override void OnMappingPagesMarkedDirty(uint startPageIndex, uint endPageIndexInclusive)
    {
        lock (_dirtyPageLock)
        {
            for (var pageIndex = startPageIndex; pageIndex <= endPageIndexInclusive; pageIndex++)
                _dirtyPageIndexes.Add(pageIndex);
        }
    }

    protected override void OnWriteCompleted(long bytesWritten)
    {
        if (bytesWritten <= 0)
            return;

        var now = DateTime.Now;
        MTime = now;
        CTime = now;
        MarkMetadataDirty();
    }

    protected override void ReleaseHostMappedPageLease(long releaseToken)
    {
        lock (_mappedCacheLock)
        {
            _mappedPageCache?.ReleasePageLease(releaseToken);
        }
    }

    protected override int PrepareHostMappedWrite(LinuxFile? linuxFile, long bufferLength, long offset)
    {
        if (bufferLength == 0) return 0;
        if (Type != InodeType.File || linuxFile == null) return 0;
        if (offset < 0) return -(int)Errno.EINVAL;
        if (!SuperBlock.MemoryContext.HostMemoryMapGeometry.SupportsMappedFileBackend)
            return 0;

        long writeEnd;
        try
        {
            writeEnd = checked(offset + bufferLength);
        }
        catch (OverflowException)
        {
            return -(int)Errno.EINVAL;
        }

        if (!TryGetLiveBackingFileLength(linuxFile, out var backingLength))
            return -(int)Errno.EIO;
        if (writeEnd <= backingLength)
        {
            Size = Math.Max(Size, (ulong)writeEnd);
            return 0;
        }

        SafeFileHandle? tempHandle = null;
        try
        {
            var handle = linuxFile.PrivateData as SafeFileHandle;
            if (handle == null)
                tempHandle = handle = _repository.OpenLiveInodeHandle((long)Ino, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite);

            RandomAccess.SetLength(handle, writeEnd);
            Size = (ulong)writeEnd;
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return -(int)Errno.EACCES;
        }
        catch (IOException)
        {
            return -(int)Errno.EIO;
        }
        finally
        {
            tempHandle?.Dispose();
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

    private sealed class MetadataMutationTrackingScope : IDisposable
    {
        private readonly SilkInode _owner;

        public MetadataMutationTrackingScope(SilkInode owner)
        {
            _owner = owner;
            _owner._metadataMutationSuppressionDepth++;
        }

        public void Dispose()
        {
            _owner._metadataMutationSuppressionDepth = Math.Max(0, _owner._metadataMutationSuppressionDepth - 1);
        }
    }
}
