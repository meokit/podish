using System.Text;
using Fiberish.Memory;
using Fiberish.Native;

namespace Fiberish.VFS;

/// <summary>
///     Shared superblock implementation for in-memory directory index based filesystems.
/// </summary>
public abstract class IndexedMemorySuperBlock : SuperBlock
{
    protected ulong _nextIno = 1;

    protected IndexedMemorySuperBlock(FileSystemType type, DeviceNumberManager devManager) : base(devManager)
    {
        Type = type;
    }

    // (parent inode, name) -> child dentry
    public Dictionary<DCacheKey, Dentry> Dentries { get; } = [];

    protected abstract IndexedMemoryInode CreateIndexedInode(ulong ino);

    public override Inode AllocInode()
    {
        lock (Lock)
        {
            var inode = CreateIndexedInode(_nextIno++);
            TrackInode(inode);
            return inode;
        }
    }

    public override void WriteInode(Inode inode)
    {
    }

    protected override void Shutdown()
    {
        base.Shutdown();
        Dentries.Clear();
    }
}

/// <summary>
///     Shared inode implementation for indexed in-memory filesystems.
/// </summary>
public abstract class IndexedMemoryInode : Inode
{
    private readonly object _flockGate = new();
    private readonly HashSet<LinuxFile> _sharedHolders = [];
    protected readonly HashSet<string> ChildNames = [];
    protected readonly HashSet<long> DirtyPageIndexes = [];
    protected readonly Dictionary<string, byte[]> XAttrs = new(StringComparer.Ordinal);
    private LinuxFile? _exclusiveHolder;
    private int _lockType; // 0: None, 1: Shared, 2: Exclusive
    protected bool OwnsPageCache;
    protected byte[]? SymlinkData;

    protected IndexedMemoryInode(ulong ino, IndexedMemorySuperBlock sb)
    {
        Ino = ino;
        SuperBlock = sb;
        MTime = ATime = CTime = DateTime.Now;
    }

    protected virtual GlobalAddressSpaceCacheManager.AddressSpaceCacheClass CacheClass =>
        GlobalAddressSpaceCacheManager.AddressSpaceCacheClass.Shmem;

    protected virtual bool PinNamespaceDentries => false;

    protected IndexedMemorySuperBlock IndexedSb => (IndexedMemorySuperBlock)SuperBlock;

    public override bool SupportsMmap => Type == InodeType.File;

    private void TouchDirectoryMutationTimestamps()
    {
        var now = DateTime.Now;
        MTime = now;
        CTime = now;
    }

    private void AttachNamespaceChild(Dentry parentDentry, Dentry childDentry, string reason)
    {
        var alreadyCached =
            parentDentry.TryGetCachedChild(childDentry.Name, out var existing) &&
            ReferenceEquals(existing, childDentry);
        parentDentry.CacheChild(childDentry, reason);
        if (!PinNamespaceDentries || alreadyCached) return;
        childDentry.Get($"{reason}.namespace-pin");
    }

    private bool DetachNamespaceChild(Dentry parentDentry, string name, string reason, out Dentry? removed)
    {
        if (!parentDentry.TryUncacheChild(name, reason, out removed))
            return false;
        if (!PinNamespaceDentries || removed == null) return true;
        removed.Put($"{reason}.namespace-unpin");
        return true;
    }

    /// <summary>
    ///     Register an externally-created dentry as a child of this directory inode.
    /// </summary>
    public void RegisterChild(Dentry parentDentry, string name, Dentry childDentry)
    {
        lock (Lock)
        {
            var key = new DCacheKey(Ino, name);
            lock (IndexedSb.Lock)
            {
                IndexedSb.Dentries[key] = childDentry;
            }

            AttachNamespaceChild(parentDentry, childDentry, "IndexedMemoryInode.RegisterChild");
            ChildNames.Add(name);
        }
    }

    public override Dentry? Lookup(string name)
    {
        lock (Lock)
        {
            if (Dentries.Count == 0) return null;
            var primaryDentry = Dentries[0];

            var key = new DCacheKey(Ino, name);
            if (IndexedSb.Dentries.TryGetValue(key, out var dentry))
            {
                AttachNamespaceChild(primaryDentry, dentry, "IndexedMemoryInode.Lookup");
                return dentry;
            }

            return null;
        }
    }

    public override int Create(Dentry dentry, int mode, int uid, int gid)
    {
        lock (Lock)
        {
            if (Type != InodeType.Directory) return -(int)Errno.ENOTDIR;

            var primaryDentry = Dentries[0];
            var key = new DCacheKey(Ino, dentry.Name);
            if (IndexedSb.Dentries.ContainsKey(key)) return -(int)Errno.EEXIST;

            var inode = (IndexedMemoryInode)IndexedSb.AllocInode();
            inode.Type = InodeType.File;
            inode.Mode = mode;
            inode.Uid = uid;
            inode.Gid = gid;
            inode.SetInitialLinkCount(1, "IndexedMemoryInode.Create");

            dentry.Instantiate(inode);

            lock (IndexedSb.Lock)
            {
                IndexedSb.Dentries[key] = dentry;
            }

            AttachNamespaceChild(primaryDentry, dentry, "IndexedMemoryInode.Create");
            ChildNames.Add(dentry.Name);
            TouchDirectoryMutationTimestamps();

            return 0;
        }
    }

    public override int Mkdir(Dentry dentry, int mode, int uid, int gid)
    {
        lock (Lock)
        {
            if (Type != InodeType.Directory) return -(int)Errno.ENOTDIR;

            var primaryDentry = Dentries[0];
            var key = new DCacheKey(Ino, dentry.Name);
            if (IndexedSb.Dentries.ContainsKey(key)) return -(int)Errno.EEXIST;

            var inode = (IndexedMemoryInode)IndexedSb.AllocInode();
            inode.Type = InodeType.Directory;
            inode.Mode = mode;
            inode.Uid = uid;
            inode.Gid = gid;
            NamespaceOps.OnDirectoryCreated(this, inode, "IndexedMemoryInode.Mkdir");

            dentry.Instantiate(inode);

            lock (IndexedSb.Lock)
            {
                IndexedSb.Dentries[key] = dentry;
            }

            AttachNamespaceChild(primaryDentry, dentry, "IndexedMemoryInode.Mkdir");
            ChildNames.Add(dentry.Name);
            TouchDirectoryMutationTimestamps();

            return 0;
        }
    }

    public override int Unlink(string name)
    {
        lock (Lock)
        {
            if (Dentries.Count == 0) return 0;
            var primaryDentry = Dentries[0];
            var key = new DCacheKey(Ino, name);
            if (!IndexedSb.Dentries.TryGetValue(key, out var dentry))
                return -(int)Errno.ENOENT;

            if (dentry.Inode?.Type == InodeType.Directory)
                return -(int)Errno.EISDIR;

            lock (IndexedSb.Lock)
            {
                IndexedSb.Dentries.Remove(key);
            }

            _ = DetachNamespaceChild(primaryDentry, name, "IndexedMemoryInode.Unlink", out _);
            var unlinkedInode = dentry.Inode;
            if (unlinkedInode != null)
            {
                NamespaceOps.OnEntryRemoved(unlinkedInode, "IndexedMemoryInode.Unlink");
                dentry.UnbindInode("IndexedMemoryInode.Unlink");
            }

            ChildNames.Remove(name);
            TouchDirectoryMutationTimestamps();
            return 0;
        }
    }

    public override int Rmdir(string name)
    {
        lock (Lock)
        {
            if (Dentries.Count == 0) return 0;
            var primaryDentry = Dentries[0];
            var key = new DCacheKey(Ino, name);
            if (!IndexedSb.Dentries.TryGetValue(key, out var dentry))
                return -(int)Errno.ENOENT;

            if (dentry.Inode?.Type != InodeType.Directory)
                return -(int)Errno.ENOTDIR;

            if (dentry.Children.Count > 0)
                return -(int)Errno.ENOTEMPTY;

            lock (IndexedSb.Lock)
            {
                IndexedSb.Dentries.Remove(key);
            }

            _ = DetachNamespaceChild(primaryDentry, name, "IndexedMemoryInode.Rmdir", out _);
            var removedInode = dentry.Inode;
            if (removedInode != null)
            {
                NamespaceOps.OnDirectoryRemoved(this, removedInode, "IndexedMemoryInode.Rmdir");
                dentry.UnbindInode("IndexedMemoryInode.Rmdir");
            }

            ChildNames.Remove(name);
            TouchDirectoryMutationTimestamps();
            return 0;
        }
    }

    public override int Rename(string oldName, Inode newParent, string newName)
    {
        var targetParent = (IndexedMemoryInode)newParent;
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
                lock (second.Lock)
                {
                    return DoRename(oldName, targetParent, newName);
                }

            return DoRename(oldName, targetParent, newName);
        }
    }

    private int DoRename(string oldName, IndexedMemoryInode targetParent, string newName)
    {
        if (Dentries.Count == 0) return -(int)Errno.ENOENT;
        var oldPrimary = Dentries[0];

        if (targetParent.Dentries.Count == 0) return -(int)Errno.ENOENT;
        var newPrimary = targetParent.Dentries[0];

        var oldKey = new DCacheKey(Ino, oldName);
        var newKey = new DCacheKey(targetParent.Ino, newName);

        lock (IndexedSb.Lock)
        {
            if (!oldPrimary.Children.TryGetValue(oldName, out var dentry))
            {
                foreach (var parentDentry in Dentries)
                    if (parentDentry.Children.TryGetValue(oldName, out dentry))
                        break;

                if (dentry == null)
                {
                    if (IndexedSb.Dentries.TryGetValue(oldKey, out var cacheMatch))
                        dentry = cacheMatch;
                    else
                        return -(int)Errno.ENOENT;
                }
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

            if (IndexedSb.Dentries.TryGetValue(newKey, out var existingDentry))
            {
                if (ReferenceEquals(existingDentry.Inode, dentry.Inode))
                    return 0;

                if (existingDentry.Inode!.Type == InodeType.Directory)
                {
                    if (existingDentry.Children.Count > 0)
                        return -(int)Errno.ENOTEMPTY;
                    var rmdirRc = targetParent.Rmdir(newName);
                    if (rmdirRc < 0)
                        return rmdirRc;
                }
                else
                {
                    existingDentry.Inode.Mapping = null;
                    var unlinkRc = targetParent.Unlink(newName);
                    if (unlinkRc < 0)
                        return unlinkRc;
                }
            }

            var sourceIsDirectory = dentry.Inode.Type == InodeType.Directory;
            var movedAcrossParents = sourceIsDirectory && !ReferenceEquals(this, targetParent);

            IndexedSb.Dentries.Remove(oldKey);
            IndexedSb.Dentries[newKey] = dentry;

            _ = DetachNamespaceChild(oldPrimary, oldName, "IndexedMemoryInode.Rename.old-parent", out _);
            ChildNames.Remove(oldName);

            dentry.Parent = newPrimary;
            dentry.Name = newName;

            AttachNamespaceChild(newPrimary, dentry, "IndexedMemoryInode.Rename.new-parent");
            targetParent.ChildNames.Add(newName);
            TouchDirectoryMutationTimestamps();
            targetParent.TouchDirectoryMutationTimestamps();

            if (movedAcrossParents)
                NamespaceOps.OnDirectoryMovedAcrossParents(this, targetParent, "IndexedMemoryInode.Rename");

            return 0;
        }
    }

    public override int Link(Dentry dentry, Inode oldInode)
    {
        lock (Lock)
        {
            if (Type != InodeType.Directory) return -(int)Errno.ENOTDIR;

            var primaryDentry = Dentries[0];
            var key = new DCacheKey(Ino, dentry.Name);
            if (IndexedSb.Dentries.ContainsKey(key)) return -(int)Errno.EEXIST;

            dentry.Instantiate(oldInode);
            NamespaceOps.OnLinkAdded(oldInode, "IndexedMemoryInode.Link");

            lock (IndexedSb.Lock)
            {
                IndexedSb.Dentries[key] = dentry;
            }

            AttachNamespaceChild(primaryDentry, dentry, "IndexedMemoryInode.Link");
            ChildNames.Add(dentry.Name);
            TouchDirectoryMutationTimestamps();
            return 0;
        }
    }

    public override int Symlink(Dentry dentry, string target, int uid, int gid)
    {
        lock (Lock)
        {
            if (Type != InodeType.Directory) return -(int)Errno.ENOTDIR;

            var primaryDentry = Dentries[0];
            var key = new DCacheKey(Ino, dentry.Name);
            if (IndexedSb.Dentries.ContainsKey(key)) return -(int)Errno.EEXIST;

            var inode = (IndexedMemoryInode)IndexedSb.AllocInode();
            inode.Type = InodeType.Symlink;
            inode.Mode = 0x1FF;
            inode.Uid = uid;
            inode.Gid = gid;
            inode.SymlinkData = Encoding.UTF8.GetBytes(target);
            inode.Size = (ulong)inode.SymlinkData.Length;
            inode.SetInitialLinkCount(1, "IndexedMemoryInode.Symlink");

            dentry.Instantiate(inode);

            lock (IndexedSb.Lock)
            {
                IndexedSb.Dentries[key] = dentry;
            }

            AttachNamespaceChild(primaryDentry, dentry, "IndexedMemoryInode.Symlink");
            ChildNames.Add(dentry.Name);
            TouchDirectoryMutationTimestamps();

            return 0;
        }
    }

    public override int Mknod(Dentry dentry, int mode, int uid, int gid, InodeType type, uint rdev)
    {
        lock (Lock)
        {
            if (Type != InodeType.Directory) return -(int)Errno.ENOTDIR;
            if (type != InodeType.CharDev && type != InodeType.BlockDev && type != InodeType.Fifo &&
                type != InodeType.Socket)
                return -(int)Errno.EINVAL;

            var primaryDentry = Dentries[0];
            var key = new DCacheKey(Ino, dentry.Name);
            if (IndexedSb.Dentries.ContainsKey(key)) return -(int)Errno.EEXIST;

            var inode = (IndexedMemoryInode)IndexedSb.AllocInode();
            inode.Type = type;
            inode.Mode = mode;
            inode.Uid = uid;
            inode.Gid = gid;
            inode.Rdev = rdev;
            inode.SetInitialLinkCount(1, "IndexedMemoryInode.Mknod");

            dentry.Instantiate(inode);

            lock (IndexedSb.Lock)
            {
                IndexedSb.Dentries[key] = dentry;
            }

            AttachNamespaceChild(primaryDentry, dentry, "IndexedMemoryInode.Mknod");
            ChildNames.Add(dentry.Name);
            TouchDirectoryMutationTimestamps();
            return 0;
        }
    }

    public override int Readlink(out string? target)
    {
        lock (Lock)
        {
            if (Type != InodeType.Symlink || SymlinkData == null)
            {
                target = null;
                return -(int)Errno.EINVAL;
            }

            target = Encoding.UTF8.GetString(SymlinkData);
            return 0;
        }
    }

    protected virtual int BackendRead(LinuxFile? linuxFile, Span<byte> buffer, long offset)
    {
        lock (Lock)
        {
            if (Type == InodeType.Directory) return 0;
            if (Type == InodeType.Symlink)
            {
                if (SymlinkData == null || offset >= SymlinkData.Length) return 0;
                var n = Math.Min(buffer.Length, SymlinkData.Length - (int)offset);
                SymlinkData.AsSpan((int)offset, n).CopyTo(buffer);
                return n;
            }

            if (offset < 0) return -(int)Errno.EINVAL;
            var fileSize = (long)Size;
            if (offset >= fileSize) return 0;

            var count = Math.Min(buffer.Length, (int)(fileSize - offset));
            var pageCache = EnsurePageCacheLocked();
            ReadFromPageCacheLocked(pageCache, offset, buffer[..count]);
            return count;
        }
    }

    protected internal override int ReadSpan(LinuxFile linuxFile, Span<byte> buffer, long offset)
    {
        return BackendRead(linuxFile, buffer, offset);
    }

    public override ReadableSegmentEnumerator GetReadableSegments(LinuxFile? linuxFile, long offset, int length)
    {
        if (offset < 0 || length < 0)
            return ReadableSegmentEnumerator.Failed;
        if (length == 0)
            return ReadableSegmentEnumerator.Empty;

        lock (Lock)
        {
            if (Type != InodeType.File)
                return ReadableSegmentEnumerator.Failed;

            var fileSize = (long)Size;
            if (offset > fileSize || length > fileSize - offset)
                return ReadableSegmentEnumerator.Failed;

            // EnsurePageCacheLocked allocates the address space if not yet present.
            // The returned AddressSpace is safe to iterate after releasing the lock
            // because in-memory pages are never evicted or moved.
            var pageCache = EnsurePageCacheLocked();
            return new ReadableSegmentEnumerator(this, linuxFile, pageCache, offset, length, fileSize);
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

    protected virtual int BackendWrite(LinuxFile? linuxFile, ReadOnlySpan<byte> buffer, long offset)
    {
        lock (Lock)
        {
            if (Type == InodeType.Directory) return -(int)Errno.EISDIR;
            if (Type == InodeType.Symlink) return -(int)Errno.EINVAL;
            if (offset < 0) return -(int)Errno.EINVAL;

            var pageCache = EnsurePageCacheLocked();
            WriteToPageCacheLocked(pageCache, offset, buffer);
            MarkDirtyRangeLocked(pageCache, offset, buffer.Length);
            var end = offset + buffer.Length;
            if (end > (long)Size) Size = (ulong)end;
            MTime = DateTime.Now;
            CTime = MTime;
            return buffer.Length;
        }
    }

    protected internal override int WriteSpan(LinuxFile linuxFile, ReadOnlySpan<byte> buffer, long offset)
    {
        return BackendWrite(linuxFile, buffer, offset);
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
        return 0;
    }

    protected override int AopsWritePage(LinuxFile? linuxFile, PageIoRequest request, ReadOnlySpan<byte> pageBuffer,
        bool sync)
    {
        if (request.Length < 0 || request.Length > pageBuffer.Length)
            return -(int)Errno.EINVAL;
        if (request.Length == 0)
        {
            lock (Lock)
            {
                DirtyPageIndexes.Remove(request.PageIndex);
            }

            return 0;
        }

        var rc = BackendWrite(linuxFile, pageBuffer[..request.Length], request.FileOffset);
        if (rc < 0) return rc;
        lock (Lock)
        {
            DirtyPageIndexes.Remove(request.PageIndex);
        }

        return 0;
    }

    protected override int AopsWritePages(LinuxFile? linuxFile, WritePagesRequest request)
    {
        if (!request.Sync) return 0;
        lock (Lock)
        {
            DirtyPageIndexes.RemoveWhere(i => i >= request.StartPageIndex && i <= request.EndPageIndex);
        }

        return 0;
    }

    protected override int AopsSetPageDirty(long pageIndex)
    {
        lock (Lock)
        {
            DirtyPageIndexes.Add(pageIndex);
        }

        return 0;
    }

    public override int Truncate(long size)
    {
        if (Type == InodeType.Directory) return -(int)Errno.EISDIR;
        if (size < 0) return -(int)Errno.EINVAL;

        lock (Lock)
        {
            if (Type == InodeType.Symlink)
            {
                Array.Resize(ref SymlinkData, (int)size);
                Size = (ulong)size;
                MTime = DateTime.Now;
                CTime = MTime;
                return 0;
            }

            if (Mapping != null)
            {
                Mapping.TruncateToSize(size);
                var firstDroppedPage = (size + LinuxConstants.PageOffsetMask) / LinuxConstants.PageSize;
                DirtyPageIndexes.RemoveWhere(i => i >= firstDroppedPage);
            }

            Size = (ulong)size;
            MTime = DateTime.Now;
            CTime = MTime;
        }

        return 0;
    }

    public override int SetXAttr(string name, ReadOnlySpan<byte> value, int flags)
    {
        const int XATTR_CREATE = 1;
        const int XATTR_REPLACE = 2;

        lock (Lock)
        {
            var exists = XAttrs.ContainsKey(name);
            if ((flags & XATTR_CREATE) != 0 && exists) return -(int)Errno.EEXIST;
            if ((flags & XATTR_REPLACE) != 0 && !exists) return -(int)Errno.ENODATA;
            XAttrs[name] = value.ToArray();
            return 0;
        }
    }

    public override int GetXAttr(string name, Span<byte> value)
    {
        lock (Lock)
        {
            if (!XAttrs.TryGetValue(name, out var data)) return -(int)Errno.ENODATA;
            if (value.Length == 0) return data.Length;
            if (value.Length < data.Length) return -(int)Errno.ERANGE;
            data.CopyTo(value);
            return data.Length;
        }
    }

    public override int ListXAttr(Span<byte> list)
    {
        lock (Lock)
        {
            var names = XAttrs.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
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
        lock (Lock)
        {
            if (!XAttrs.Remove(name)) return -(int)Errno.ENODATA;
            return 0;
        }
    }

    public override List<DirectoryEntry> GetEntries()
    {
        var list = new List<DirectoryEntry>();
        if (Type != InodeType.Directory) return list;
        if (Dentries.Count == 0) return list;

        list.Add(new DirectoryEntry { Name = ".", Ino = Ino, Type = InodeType.Directory });
        list.Add(new DirectoryEntry { Name = "..", Ino = Ino, Type = InodeType.Directory });

        foreach (var name in ChildNames)
            if (IndexedSb.Dentries.TryGetValue(new DCacheKey(Ino, name), out var dentry))
                list.Add(new DirectoryEntry { Name = name, Ino = dentry.Inode!.Ino, Type = dentry.Inode.Type });

        return list;
    }

    protected override void OnEvictCache()
    {
        if (Type == InodeType.Symlink) SymlinkData = null;
        ReleaseOwnedPageCache();
        ChildNames.Clear();
        base.OnEvictCache();
    }

    public override void Release(LinuxFile linuxFile)
    {
        Flock(linuxFile, LinuxConstants.LOCK_UN);

        base.Release(linuxFile);
    }

    protected AddressSpace EnsurePageCacheLocked()
    {
        if (Mapping != null) return Mapping;
        Mapping = new AddressSpace(CacheClass == GlobalAddressSpaceCacheManager.AddressSpaceCacheClass.Shmem
            ? AddressSpaceKind.Shmem
            : AddressSpaceKind.File);
        GlobalAddressSpaceCacheManager.TrackAddressSpace(Mapping, CacheClass);
        OwnsPageCache = true;
        return Mapping;
    }

    protected void ReleaseOwnedPageCache()
    {
        if (!OwnsPageCache || Mapping == null) return;
        Mapping.Release();
        Mapping = null;
        OwnsPageCache = false;
        DirtyPageIndexes.Clear();
    }

    protected static void ReadFromPageCacheLocked(AddressSpace pageCache, long offset, Span<byte> destination)
    {
        var copied = 0;
        while (copied < destination.Length)
        {
            var absolute = offset + copied;
            var pageIndex = (uint)(absolute / LinuxConstants.PageSize);
            var pageOffset = (int)(absolute & LinuxConstants.PageOffsetMask);
            var chunk = Math.Min(destination.Length - copied, LinuxConstants.PageSize - pageOffset);
            var pagePtr = pageCache.GetPage(pageIndex);
            if (pagePtr == IntPtr.Zero)
                destination.Slice(copied, chunk).Clear();
            else
                unsafe
                {
                    var src = (byte*)pagePtr + pageOffset;
                    fixed (byte* dst = &destination[copied])
                    {
                        Buffer.MemoryCopy(src, dst, chunk, chunk);
                    }
                }

            copied += chunk;
        }
    }

    protected static void WriteToPageCacheLocked(AddressSpace pageCache, long offset, ReadOnlySpan<byte> source)
    {
        var consumed = 0;
        while (consumed < source.Length)
        {
            var absolute = offset + consumed;
            var pageIndex = (uint)(absolute / LinuxConstants.PageSize);
            var pageOffset = (int)(absolute & LinuxConstants.PageOffsetMask);
            var chunk = Math.Min(source.Length - consumed, LinuxConstants.PageSize - pageOffset);
            var pagePtr = pageCache.GetOrCreatePage(pageIndex, ptr =>
            {
                unsafe
                {
                    new Span<byte>((void*)ptr, LinuxConstants.PageSize).Clear();
                }

                return true;
            }, out _, true, AllocationClass.PageCache);

            if (pagePtr == IntPtr.Zero)
                throw new OutOfMemoryException("Failed to allocate indexed page cache page");

            unsafe
            {
                fixed (byte* src = &source[consumed])
                {
                    var dst = (byte*)pagePtr + pageOffset;
                    Buffer.MemoryCopy(src, dst, chunk, chunk);
                }
            }

            consumed += chunk;
        }
    }

    protected void MarkDirtyRangeLocked(AddressSpace pageCache, long offset, int length)
    {
        if (length <= 0) return;
        var startPage = (uint)(offset / LinuxConstants.PageSize);
        var endPage = (uint)((offset + length - 1) / LinuxConstants.PageSize);
        for (var page = startPage; page <= endPage; page++)
        {
            pageCache.MarkDirty(page);
            DirtyPageIndexes.Add(page);
        }
    }

    public bool VisitReadSegments(long offset, int length, Func<IntPtr, int, bool> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);

        if (offset < 0 || length < 0)
            return false;
        if (length == 0)
            return true;

        lock (Lock)
        {
            if (Type != InodeType.File)
                return false;

            var fileSize = (long)Size;
            if (offset > fileSize || length > fileSize - offset)
                return false;

            var pageCache = EnsurePageCacheLocked();
            var probeRemaining = length;
            var probeAbsolute = offset;
            while (probeRemaining > 0)
            {
                var pageIndex = (uint)(probeAbsolute / LinuxConstants.PageSize);
                var pageOffset = (int)(probeAbsolute & LinuxConstants.PageOffsetMask);
                var chunk = Math.Min(probeRemaining, LinuxConstants.PageSize - pageOffset);
                if (pageCache.GetPage(pageIndex) == IntPtr.Zero)
                    return false;

                probeAbsolute += chunk;
                probeRemaining -= chunk;
            }

            var remaining = length;
            var absolute = offset;
            while (remaining > 0)
            {
                var pageIndex = (uint)(absolute / LinuxConstants.PageSize);
                var pageOffset = (int)(absolute & LinuxConstants.PageOffsetMask);
                var chunk = Math.Min(remaining, LinuxConstants.PageSize - pageOffset);
                var pagePtr = pageCache.GetPage(pageIndex);
                if (pagePtr == IntPtr.Zero)
                    return false;

                var chunkPtr = IntPtr.Add(pagePtr, pageOffset);
                if (!visitor(chunkPtr, chunk))
                    return false;

                absolute += chunk;
                remaining -= chunk;
            }

            return true;
        }
    }
}