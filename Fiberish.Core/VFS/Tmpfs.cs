using Fiberish.Native;
using Fiberish.Memory;
using System.Text;

namespace Fiberish.VFS;

public class Tmpfs : FileSystem
{
    public Tmpfs(DeviceNumberManager? devManager = null) : base(devManager)
    {
        Name = "tmpfs";
    }

    public override SuperBlock ReadSuper(FileSystemType fsType, int flags, string devName, object? data)
    {
        var sb = new TmpfsSuperBlock(fsType, DevManager);
        var rootInode = sb.AllocInode();
        rootInode.Type = InodeType.Directory;
        rootInode.Mode = 0x1FF; // 777

        sb.Root = new Dentry("/", rootInode, null, sb);
        sb.Root.Parent = sb.Root;

        return sb;
    }
}

public class TmpfsSuperBlock : SuperBlock
{
    protected ulong _nextIno = 1;

    public TmpfsSuperBlock(FileSystemType type, DeviceNumberManager devManager) : base(devManager)
    {
        Type = type;
    }

    // The "Hash Table" requested by user: (ParentDentryID, Name) -> ChildDentry
    public Dictionary<DCacheKey, Dentry> Dentries { get; } = [];

    public override Inode AllocInode()
    {
        lock (Lock)
        {
            var inode = new TmpfsInode(_nextIno++, this);
            Inodes.Add(inode);
            AllInodes.Add(inode);
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

public class TmpfsInode : Inode
{
    private readonly HashSet<string> _childNames = [];
    private readonly Dictionary<string, byte[]> _xattrs = new(StringComparer.Ordinal);
    private readonly HashSet<long> _dirtyPageIndexes = [];
    private byte[]? _symlinkData;
    private bool _ownsPageCache;

    // Track open file handles to prevent data loss on unlink-while-open
    private int _openCount = 0;

    // Flock state
    private int _lockType = 0; // 0: None, 1: Shared, 2: Exclusive
    private readonly HashSet<LinuxFile> _sharedHolders = [];
    private LinuxFile? _exclusiveHolder = null;

    public TmpfsInode(ulong ino, SuperBlock sb)
    {
        Ino = ino;
        SuperBlock = sb;
        MTime = ATime = CTime = DateTime.Now;
    }

    public override bool SupportsMmap => Type == InodeType.File;

    /// <summary>
    ///     Register an externally-created dentry as a child of this directory inode.
    ///     This updates _childNames AND the TmpfsSuperBlock.Dentries DCache so that
    ///     the entry is visible via both Lookup() and GetEntries().
    /// </summary>
    public void RegisterChild(Dentry parentDentry, string name, Dentry childDentry)
    {
        lock (Lock)
        {
            var sb = (TmpfsSuperBlock)SuperBlock;
            var key = new DCacheKey(Ino, name);
            lock (sb.Lock)
            {
                sb.Dentries[key] = childDentry;
            }

            parentDentry.Children[name] = childDentry;
            _childNames.Add(name);
        }
    }

    public override Dentry? Lookup(string name)
    {
        lock (Lock)
        {
            if (Dentries.Count == 0) return null;
            var primaryDentry = Dentries[0];

            var sb = (TmpfsSuperBlock)SuperBlock;
            var key = new DCacheKey(Ino, name);
            if (sb.Dentries.TryGetValue(key, out var dentry))
            {
                primaryDentry.Children[name] = dentry;
                return dentry;
            }

            return null;
        }
    }

    public override Dentry Create(Dentry dentry, int mode, int uid, int gid)
    {
        lock (Lock)
        {
            if (Type != InodeType.Directory) throw new InvalidOperationException("Not a directory");

            var primaryDentry = Dentries[0];
            var sb = (TmpfsSuperBlock)SuperBlock;
            var key = new DCacheKey(Ino, dentry.Name);
            if (sb.Dentries.ContainsKey(key)) throw new InvalidOperationException("Exists");

            var inode = (TmpfsInode)sb.AllocInode();
            inode.Type = InodeType.File;
            inode.Mode = mode;
            inode.Uid = uid;
            inode.Gid = gid;

            dentry.Instantiate(inode);

            lock (sb.Lock)
            {
                sb.Dentries[key] = dentry;
            }

            primaryDentry.Children[dentry.Name] = dentry;
            _childNames.Add(dentry.Name);

            return dentry;
        }
    }

    public override Dentry Mkdir(Dentry dentry, int mode, int uid, int gid)
    {
        lock (Lock)
        {
            if (Type != InodeType.Directory) throw new InvalidOperationException("Not a directory");

            var primaryDentry = Dentries[0];
            var sb = (TmpfsSuperBlock)SuperBlock;
            var key = new DCacheKey(Ino, dentry.Name);
            if (sb.Dentries.ContainsKey(key)) throw new InvalidOperationException("Exists");

            var inode = (TmpfsInode)sb.AllocInode();
            inode.Type = InodeType.Directory;
            inode.Mode = mode;
            inode.Uid = uid;
            inode.Gid = gid;

            dentry.Instantiate(inode);

            lock (sb.Lock)
            {
                sb.Dentries[key] = dentry;
            }

            primaryDentry.Children[dentry.Name] = dentry;
            _childNames.Add(dentry.Name);

            return dentry;
        }
    }

    public override void Unlink(string name)
    {
        lock (Lock)
        {
            if (Dentries.Count == 0) return;
            var primaryDentry = Dentries[0];
            var sb = (TmpfsSuperBlock)SuperBlock;
            var key = new DCacheKey(Ino, name);
            if (!sb.Dentries.TryGetValue(key, out var dentry))
                throw new FileNotFoundException("Source does not exist", name);

            if (dentry.Inode?.Type == InodeType.Directory)
                throw new InvalidOperationException("Is a directory");

            lock (sb.Lock)
            {
                sb.Dentries.Remove(key);
            }

            primaryDentry.Children.Remove(name);
            var unlinkedInode = dentry.Inode;
            unlinkedInode?.Dentries.Remove(dentry);
            _childNames.Remove(name);

            // Decrement refcount of the unlinked inode
            unlinkedInode?.Put();
        }
    }

    public override void Rmdir(string name)
    {
        lock (Lock)
        {
            if (Dentries.Count == 0) return;
            var primaryDentry = Dentries[0];
            var sb = (TmpfsSuperBlock)SuperBlock;
            var key = new DCacheKey(Ino, name);
            if (!sb.Dentries.TryGetValue(key, out var dentry))
                throw new DirectoryNotFoundException(name);

            if (dentry.Inode?.Type != InodeType.Directory)
                throw new InvalidOperationException("Not a directory");

            if (dentry.Children.Count > 0)
                throw new InvalidOperationException("Directory not empty");

            lock (sb.Lock)
            {
                sb.Dentries.Remove(key);
            }

            primaryDentry.Children.Remove(name);
            dentry.Inode.Dentries.Remove(dentry);
            _childNames.Remove(name);
            dentry.Inode.Put();
        }
    }

    public override void Rename(string oldName, Inode newParent, string newName)
    {
        var targetParent = (TmpfsInode)newParent;
        // 1. Determine lock order to prevent deadlocks
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
                    DoRename(oldName, targetParent, newName);
                }
            else
                DoRename(oldName, targetParent, newName);
        }
    }

    private void DoRename(string oldName, TmpfsInode targetParent, string newName)
    {
        var sb = (TmpfsSuperBlock)SuperBlock;

        if (Dentries.Count == 0) throw new InvalidOperationException("Source parent detached");
        var oldPrimary = Dentries[0];

        if (targetParent.Dentries.Count == 0) throw new InvalidOperationException("Target parent detached");
        var newPrimary = targetParent.Dentries[0];

        var oldKey = new DCacheKey(Ino, oldName);
        var newKey = new DCacheKey(targetParent.Ino, newName);

        lock (sb.Lock)
        {
            if (!oldPrimary.Children.TryGetValue(oldName, out var dentry))
            {
                // Fallback to searching all Dentries for this inode if not in primary
                foreach (var parentDentry in Dentries)
                {
                    if (parentDentry.Children.TryGetValue(oldName, out dentry)) break;
                }

                if (dentry == null)
                {
                    // Fallback to sb cache search
                    if (sb.Dentries.TryGetValue(oldKey, out var cacheMatch))
                    {
                        dentry = cacheMatch;
                    }
                    else
                    {
                        throw new InvalidOperationException("Source does not exist");
                    }
                }
            }

            // 2. Cycle Check: Ensure we aren't moving a directory into its own subdirectory
            if (dentry.Inode!.Type == InodeType.Directory)
            {
                var curr = newPrimary;
                while (curr != null)
                {
                    if (curr == dentry)
                        throw new InvalidOperationException("Cannot move directory into its own subdirectory");
                    if (curr == curr.Parent) break;
                    curr = curr.Parent;
                }
            }

            // 3. Atomic Replacement: If destination exists, unlink it
            if (sb.Dentries.TryGetValue(newKey, out var existingDentry))
            {
                if (existingDentry.Inode!.Type == InodeType.Directory)
                {
                    // Check if empty (only . and ..)
                    if (existingDentry.Children.Count > 0)
                        throw new InvalidOperationException("Directory not empty");
                    targetParent.Rmdir(newName);
                }
                else
                {
                    // Invalidate the old target inode's page cache BEFORE unlinking,
                    // so any open handle still holding this inode will not serve stale data.
                    existingDentry.Inode.PageCache = null;
                    targetParent.Unlink(newName);
                }
            }

            // 4. Move dentry in superblock cache
            sb.Dentries.Remove(oldKey);
            sb.Dentries[newKey] = dentry;

            // 5. Move dentry in directory structures
            oldPrimary.Children.Remove(oldName);
            _childNames.Remove(oldName);

            dentry.Parent = newPrimary;
            dentry.Name = newName;

            newPrimary.Children[newName] = dentry;
            targetParent._childNames.Add(newName);
        }
    }

    public override Dentry Link(Dentry dentry, Inode oldInode)
    {
        lock (Lock)
        {
            if (Type != InodeType.Directory) throw new InvalidOperationException("Not a directory");

            var primaryDentry = Dentries[0];
            var sb = (TmpfsSuperBlock)SuperBlock;
            var key = new DCacheKey(Ino, dentry.Name);
            
            if (sb.Dentries.ContainsKey(key)) throw new InvalidOperationException("Exists");

            dentry.Instantiate(oldInode);

            lock (sb.Lock)
            {
                sb.Dentries[key] = dentry;
            }

            primaryDentry.Children[dentry.Name] = dentry;
            _childNames.Add(dentry.Name);
            return dentry;
        }
    }

    public override Dentry Symlink(Dentry dentry, string target, int uid, int gid)
    {
        lock (Lock)
        {
            if (Type != InodeType.Directory) throw new InvalidOperationException("Not a directory");

            var primaryDentry = Dentries[0];
            var sb = (TmpfsSuperBlock)SuperBlock;
            var key = new DCacheKey(Ino, dentry.Name);
            if (sb.Dentries.ContainsKey(key)) throw new InvalidOperationException("Exists");

            var inode = (TmpfsInode)sb.AllocInode();
            inode.Type = InodeType.Symlink;
            inode.Mode = 0x1FF; // 777
            inode.Uid = uid;
            inode.Gid = gid;
            inode._symlinkData = Encoding.UTF8.GetBytes(target);
            inode.Size = (ulong)inode._symlinkData.Length;

            dentry.Instantiate(inode);

            lock (sb.Lock)
            {
                sb.Dentries[key] = dentry;
            }

            primaryDentry.Children[dentry.Name] = dentry;
            _childNames.Add(dentry.Name);

            return dentry;
        }
    }

    public override Dentry Mknod(Dentry dentry, int mode, int uid, int gid, InodeType type, uint rdev)
    {
        lock (Lock)
        {
            if (Type != InodeType.Directory) throw new InvalidOperationException("Not a directory");
            if (type != InodeType.CharDev && type != InodeType.BlockDev && type != InodeType.Fifo &&
                type != InodeType.Socket)
                throw new InvalidOperationException("Unsupported node type");

            var primaryDentry = Dentries[0];
            var sb = (TmpfsSuperBlock)SuperBlock;
            var key = new DCacheKey(Ino, dentry.Name);
            if (sb.Dentries.ContainsKey(key)) throw new InvalidOperationException("Exists");

            var inode = (TmpfsInode)sb.AllocInode();
            inode.Type = type;
            inode.Mode = mode;
            inode.Uid = uid;
            inode.Gid = gid;
            inode.Rdev = rdev;

            dentry.Instantiate(inode);

            lock (sb.Lock)
            {
                sb.Dentries[key] = dentry;
            }

            primaryDentry.Children[dentry.Name] = dentry;
            _childNames.Add(dentry.Name);
            return dentry;
        }
    }

    public override string Readlink()
    {
        lock (Lock)
        {
            if (Type != InodeType.Symlink || _symlinkData == null) throw new InvalidOperationException("Not a symlink");
            return Encoding.UTF8.GetString(_symlinkData);
        }
    }

    private int BackendRead(LinuxFile? linuxFile, Span<byte> buffer, long offset)
    {
        lock (Lock)
        {
            if (Type == InodeType.Directory) return 0;
            if (Type == InodeType.Symlink)
            {
                if (_symlinkData == null || offset >= _symlinkData.Length) return 0;
                var n = Math.Min(buffer.Length, _symlinkData.Length - (int)offset);
                _symlinkData.AsSpan((int)offset, n).CopyTo(buffer);
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

    public override int Read(LinuxFile linuxFile, Span<byte> buffer, long offset)
    {
        return BackendRead(linuxFile, buffer, offset);
    }

    public override int Flock(LinuxFile linuxFile, int operation)
    {
        bool nonBlock = (operation & LinuxConstants.LOCK_NB) != 0;
        int op = operation & ~LinuxConstants.LOCK_NB;

        lock (Lock)
        {
            while (true)
            {
                if (op == LinuxConstants.LOCK_UN)
                {
                    if (_exclusiveHolder == linuxFile) _exclusiveHolder = null;
                    _sharedHolders.Remove(linuxFile);
                    if (_exclusiveHolder == null && _sharedHolders.Count == 0) _lockType = 0;
                    Monitor.PulseAll(Lock);
                    return 0;
                }

                bool canAcquire = false;
                if (op == LinuxConstants.LOCK_SH)
                {
                    // Can acquire shared lock if no one has exclusive lock, OR if WE have the exclusive lock (downgrade)
                    if (_exclusiveHolder == null || _exclusiveHolder == linuxFile) canAcquire = true;
                }
                else if (op == LinuxConstants.LOCK_EX)
                {
                    // Can acquire exclusive lock if (no one has ANY lock) OR (WE are the only one holding locks)
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
                        if (_exclusiveHolder == linuxFile) _exclusiveHolder = null; // Downgrade
                        _sharedHolders.Add(linuxFile);
                        _lockType = 1;
                    }
                    else
                    {
                        _sharedHolders.Remove(linuxFile); // Ensure not in shared if moving to exclusive
                        _exclusiveHolder = linuxFile;
                        _lockType = 2;
                    }

                    return 0;
                }

                if (nonBlock) return -(int)Errno.EAGAIN;
                Monitor.Wait(Lock);
            }
        }
    }

    private int BackendWrite(LinuxFile? linuxFile, ReadOnlySpan<byte> buffer, long offset)
    {
        lock (Lock)
        {
            if (Type == InodeType.Directory) return -(int)Errno.EISDIR;
            if (Type == InodeType.Symlink) return -(int)Errno.EINVAL;
            if (offset < 0) return -(int)Errno.EINVAL;

            var pageCache = EnsurePageCacheLocked();
            WriteToPageCacheLocked(pageCache, offset, buffer);
            var end = offset + buffer.Length;
            if (end > (long)Size) Size = (ulong)end;
            MTime = DateTime.Now;
            return buffer.Length;
        }
    }

    public override int Write(LinuxFile linuxFile, ReadOnlySpan<byte> buffer, long offset)
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
        // tmpfs is already memory resident; no read-ahead needed.
        return 0;
    }

    protected override int AopsWritePage(LinuxFile? linuxFile, PageIoRequest request, ReadOnlySpan<byte> pageBuffer, bool sync)
    {
        if (request.Length < 0 || request.Length > pageBuffer.Length)
            return -(int)Errno.EINVAL;
        if (request.Length == 0)
        {
            lock (Lock) _dirtyPageIndexes.Remove(request.PageIndex);
            return 0;
        }

        var rc = BackendWrite(linuxFile, pageBuffer[..request.Length], request.FileOffset);
        if (rc < 0) return rc;
        lock (Lock) _dirtyPageIndexes.Remove(request.PageIndex);
        return 0;
    }

    protected override int AopsWritePages(LinuxFile? linuxFile, WritePagesRequest request)
    {
        if (!request.Sync) return 0;
        lock (Lock)
        {
            _dirtyPageIndexes.RemoveWhere(i => i >= request.StartPageIndex && i <= request.EndPageIndex);
        }

        return 0;
    }

    protected override int AopsSetPageDirty(long pageIndex)
    {
        lock (Lock)
        {
            _dirtyPageIndexes.Add(pageIndex);
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
                Array.Resize(ref _symlinkData, (int)size);
                Size = (ulong)size;
                MTime = DateTime.Now;
                return 0;
            }

            if (PageCache != null)
            {
                PageCache.TruncateToSize(size);
                var firstDroppedPage = (size + LinuxConstants.PageOffsetMask) / LinuxConstants.PageSize;
                _dirtyPageIndexes.RemoveWhere(i => i >= firstDroppedPage);
            }

            Size = (ulong)size;
            MTime = DateTime.Now;
        }
        return 0;
    }

    public override int SetXAttr(string name, ReadOnlySpan<byte> value, int flags)
    {
        const int XATTR_CREATE = 1;
        const int XATTR_REPLACE = 2;

        lock (Lock)
        {
            var exists = _xattrs.ContainsKey(name);
            if ((flags & XATTR_CREATE) != 0 && exists) return -(int)Errno.EEXIST;
            if ((flags & XATTR_REPLACE) != 0 && !exists) return -(int)Errno.ENODATA;
            _xattrs[name] = value.ToArray();
            return 0;
        }
    }

    public override int GetXAttr(string name, Span<byte> value)
    {
        lock (Lock)
        {
            if (!_xattrs.TryGetValue(name, out var data)) return -(int)Errno.ENODATA;
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
            var names = _xattrs.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
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
            if (!_xattrs.Remove(name)) return -(int)Errno.ENODATA;
            return 0;
        }
    }

    public override List<DirectoryEntry> GetEntries()
    {
        var list = new List<DirectoryEntry>();
        if (Type != InodeType.Directory) return list;
        if (Dentries.Count == 0) return list;
        var primaryDentry = Dentries[0];

        list.Add(new DirectoryEntry { Name = ".", Ino = Ino, Type = InodeType.Directory });
        list.Add(new DirectoryEntry { Name = "..", Ino = Ino, Type = InodeType.Directory });

        var sb = (TmpfsSuperBlock)SuperBlock;
        foreach (var name in _childNames)
            if (sb.Dentries.TryGetValue(new DCacheKey(Ino, name), out var dentry))
                list.Add(new DirectoryEntry { Name = name, Ino = dentry.Inode!.Ino, Type = dentry.Inode.Type });

        return list;
    }

    public override void Open(LinuxFile linuxFile)
    {
        Interlocked.Increment(ref _openCount);
    }

    protected override void Release()
    {
        // Only clear data if no open file handles remain.
        // This handles the case where a file is unlinked while still open,
        // and the underlying TmpfsInode's refcount drops to 0 due to
        // OverlayFS not forwarding Get/Put to underlying inodes.
        if (_openCount == 0)
        {
            if (Type == InodeType.Symlink) _symlinkData = null;
            ReleaseOwnedPageCache();
            _childNames.Clear();
        }
    }

    public override void Release(LinuxFile linuxFile)
    {
        Interlocked.Decrement(ref _openCount);

        // Drop any locks held by this file description
        Flock(linuxFile, LinuxConstants.LOCK_UN);

        // If this was the last open handle and the inode is no longer linked,
        // clean up the data now.
        if (_openCount == 0 && Dentries.Count == 0)
        {
            if (Type == InodeType.Symlink) _symlinkData = null;
            ReleaseOwnedPageCache();
        }

        base.Release(linuxFile);
    }

    private MemoryObject EnsurePageCacheLocked()
    {
        if (PageCache != null) return PageCache;
        PageCache = new MemoryObject(MemoryObjectKind.File, null, 0, 0, true);
        GlobalPageCacheManager.TrackPageCache(PageCache, GlobalPageCacheManager.PageCacheClass.Shmem);
        _ownsPageCache = true;
        return PageCache;
    }

    private void ReleaseOwnedPageCache()
    {
        if (!_ownsPageCache || PageCache == null) return;
        PageCache.Release();
        PageCache = null;
        _ownsPageCache = false;
        _dirtyPageIndexes.Clear();
    }

    private static void ReadFromPageCacheLocked(MemoryObject pageCache, long offset, Span<byte> destination)
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
            {
                destination.Slice(copied, chunk).Clear();
            }
            else
            {
                unsafe
                {
                    var src = (byte*)pagePtr + pageOffset;
                    fixed (byte* dst = &destination[copied])
                    {
                        Buffer.MemoryCopy(src, dst, chunk, chunk);
                    }
                }
            }

            copied += chunk;
        }
    }

    private static void WriteToPageCacheLocked(MemoryObject pageCache, long offset, ReadOnlySpan<byte> source)
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
            }, out _, strictQuota: true, AllocationClass.PageCache);

            if (pagePtr == IntPtr.Zero)
                throw new OutOfMemoryException("Failed to allocate tmpfs page cache page");

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
}
