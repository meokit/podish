using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;

namespace Fiberish.VFS;

public readonly record struct PageIoRequest(long PageIndex, long FileOffset, int Length);
public readonly record struct ReadaheadRequest(long StartPageIndex, int PageCount);
public readonly record struct WritePagesRequest(long StartPageIndex, long EndPageIndex, bool Sync);

public interface IPageCacheOps
{
    int ReadPage(LinuxFile? linuxFile, PageIoRequest request, Span<byte> pageBuffer);
    int Readahead(LinuxFile? linuxFile, ReadaheadRequest request);
    int WritePage(LinuxFile? linuxFile, PageIoRequest request, ReadOnlySpan<byte> pageBuffer, bool sync);
    int WritePages(LinuxFile? linuxFile, WritePagesRequest request);
    int SetPageDirty(long pageIndex);
}

public enum InodeType
{
    Unknown = 0,
    File = 0x8000,
    Directory = 0x4000,
    Symlink = 0xA000,
    CharDev = 0x2000,
    BlockDev = 0x6000,
    Fifo = 0x1000,
    Socket = 0xC000
}

[Flags]
public enum FileFlags
{
    O_RDONLY = 0,
    O_WRONLY = 1,
    O_RDWR = 2,
    O_CREAT = 64,
    O_EXCL = 128,
    O_NOCTTY = 256,
    O_TRUNC = 512,
    O_APPEND = 1024,
    O_NONBLOCK = 2048,
    O_DIRECTORY = 65536,
    O_NOFOLLOW = 131072,
    O_CLOEXEC = 524288
}

public abstract class FileSystem
{
    public string Name { get; set; } = "";
    public abstract SuperBlock ReadSuper(FileSystemType fsType, int flags, string devName, object? data);
}

public class FileSystemType
{
    public string Name { get; init; } = "";
    public Func<FileSystem> Factory { get; init; } = static () =>
        throw new InvalidOperationException("FileSystem factory is not configured.");

    public FileSystem CreateFileSystem()
    {
        return Factory();
    }
}

public abstract class SuperBlock
{
    protected HashSet<Inode> AllInodes = [];
    public FileSystemType Type { get; set; } = null!;
    public Dentry Root { get; set; } = null!;
    public int BlockSize { get; set; } = 4096;
    public List<Inode> Inodes { get; set; } = [];
    public object Lock { get; } = new();

    // Reference counting for lifecycle management
    public int RefCount { get; set; }

    public void Get()
    {
        RefCount++;
    }

    public void Put()
    {
        if (--RefCount <= 0) Shutdown();
    }

    public bool HasActiveInodes()
    {
        return AllInodes.Any(i => i.RefCount > i.Dentries.Count);
    }

    protected virtual void Shutdown()
    {
        // Subclasses can override to clean up resources
        AllInodes.Clear();
        Inodes.Clear();
    }

    public virtual Inode AllocInode()
    {
        throw new NotSupportedException();
    }

    public virtual void WriteInode(Inode inode)
    {
    }
}

public abstract class Inode : IPageCacheOps
{
    protected sealed class NoopWaitRegistration : IDisposable
    {
        public static readonly NoopWaitRegistration Instance = new();
        public void Dispose()
        {
        }
    }

    protected delegate int ReadBackendDelegate(LinuxFile? linuxFile, Span<byte> buffer, long offset);
    protected delegate int WriteBackendDelegate(LinuxFile? linuxFile, ReadOnlySpan<byte> buffer, long offset);

    /// <summary>
    ///     Per-inode page cache. Lazily created on first file mmap.
    ///     Analogous to Linux inode.i_mapping / address_space.
    /// </summary>
    public MemoryObject? PageCache { get; set; }
    public MemoryObjectManager? PageCacheManager { get; set; }

    public virtual ulong Ino { get; set; }
    public virtual InodeType Type { get; set; }
    public virtual int Mode { get; set; }
    public virtual int Uid { get; set; }
    public virtual int Gid { get; set; }
    public virtual ulong Size { get; set; }
    public virtual DateTime MTime { get; set; } = DateTime.Now;
    public virtual DateTime ATime { get; set; } = DateTime.Now;
    public virtual DateTime CTime { get; set; } = DateTime.Now;

    /// <summary>
    ///     Device number (rdev) for character/block devices.
    ///     Encoded as (major << 8) | minor for compatibility.
    /// </summary>
    public virtual uint Rdev { get; set; }

    public SuperBlock SuperBlock { get; set; } = null!;

    // All dentries pointing to this inode (hard links)
    public List<Dentry> Dentries { get; } = [];
    public object Lock { get; } = new();

    // Reference counting for lifecycle management
    public int RefCount { get; set; }

    // Reference counting methods
    public void Get()
    {
        RefCount++;
    }

    public void Put()
    {
        if (--RefCount <= 0) Release();
    }

    protected virtual void Release()
    {
        // Subclasses can override to clean up resources
        PageCacheManager?.ReleaseInodePageCache(this);
    }

    // Operations
    public virtual Dentry? Lookup(string name)
    {
        return null;
    }

    /// <summary>
    ///     Revalidate a cached child dentry before path walk uses it.
    ///     Return false to drop cache and force fresh Lookup(name).
    /// </summary>
    public virtual bool RevalidateCachedChild(Dentry parent, string name, Dentry cached)
    {
        return true;
    }

    public virtual Dentry Create(Dentry dentry, int mode, int uid, int gid)
    {
        throw new NotSupportedException();
    }

    public virtual int Truncate(long length)
    {
        return -(int)Errno.ENOSYS;
    }

    public virtual Dentry Mkdir(Dentry dentry, int mode, int uid, int gid)
    {
        throw new NotSupportedException();
    }

    public virtual void Unlink(string name)
    {
        throw new NotSupportedException();
    }

    public virtual void Rmdir(string name)
    {
        throw new NotSupportedException();
    }

    public virtual Dentry Link(Dentry dentry, Inode oldInode)
    {
        throw new NotSupportedException();
    }

    public virtual void Rename(string oldName, Inode newParent, string newName)
    {
        throw new NotSupportedException();
    }

    public virtual Dentry Symlink(Dentry dentry, string target, int uid, int gid)
    {
        throw new NotSupportedException();
    }

    public virtual Dentry Mknod(Dentry dentry, int mode, int uid, int gid, InodeType type, uint rdev)
    {
        throw new NotSupportedException();
    }

    public virtual string Readlink()
    {
        throw new NotSupportedException();
    }

    public virtual int Read(LinuxFile linuxFile, Span<byte> buffer, long offset)
    {
        return 0;
    }

    public virtual int Write(LinuxFile linuxFile, ReadOnlySpan<byte> buffer, long offset)
    {
        if (Type == InodeType.Directory) return -(int)Errno.EISDIR;
        return 0;
    }

    protected int ReadWithPageCache(
        LinuxFile? linuxFile,
        Span<byte> buffer,
        long offset,
        ReadBackendDelegate backendRead)
    {
        if (buffer.Length == 0) return 0;
        var pageCache = PageCache;
        if (pageCache == null) return backendRead(linuxFile, buffer, offset);
        if (offset < 0) return -(int)Errno.EINVAL;

        var total = 0;
        var cursor = offset;
        var fileSize = (long)Size;
        var tempPage = new byte[LinuxConstants.PageSize];
        while (total < buffer.Length)
        {
            var fileRemaining = fileSize - cursor;
            if (fileRemaining <= 0) break; // EOF

            var pageIndex = (uint)(cursor / LinuxConstants.PageSize);
            var pageOffset = (int)(cursor & LinuxConstants.PageOffsetMask);
            var toCopy = Math.Min(buffer.Length - total, LinuxConstants.PageSize - pageOffset);
            if (toCopy > fileRemaining) toCopy = (int)fileRemaining;
            if (toCopy <= 0) break;
            var pagePtr = pageCache.GetPage(pageIndex);
            if (pagePtr != IntPtr.Zero)
            {
                unsafe
                {
                    var src = (byte*)pagePtr + pageOffset;
                    fixed (byte* dst = &buffer[total])
                    {
                        Buffer.MemoryCopy(src, dst, toCopy, toCopy);
                    }
                }

                total += toCopy;
                cursor += toCopy;
                continue;
            }

            tempPage.AsSpan().Clear();
            var pageFileOffset = (long)pageIndex * LinuxConstants.PageSize;
            var pageReadLen = (int)Math.Min((long)LinuxConstants.PageSize, Math.Max(0, fileSize - pageFileOffset));
            var rc = ReadPage(linuxFile, new PageIoRequest(pageIndex, pageFileOffset, pageReadLen), tempPage);
            if (rc < 0) return total > 0 ? total : rc;

            pagePtr = pageCache.GetOrCreatePage(pageIndex, p =>
            {
                unsafe
                {
                    var dst = new Span<byte>((void*)p, LinuxConstants.PageSize);
                    dst.Clear();
                    if (pageReadLen > 0) tempPage.AsSpan(0, pageReadLen).CopyTo(dst);
                }

                return true;
            }, out _);

            if (pagePtr == IntPtr.Zero) return total > 0 ? total : -(int)Errno.EIO;

            unsafe
            {
                var src = (byte*)pagePtr + pageOffset;
                fixed (byte* dst = &buffer[total])
                {
                    Buffer.MemoryCopy(src, dst, toCopy, toCopy);
                }
            }

            total += toCopy;
            cursor += toCopy;
        }

        return total;
    }

    protected int WriteWithPageCache(
        LinuxFile? linuxFile,
        ReadOnlySpan<byte> buffer,
        long offset,
        WriteBackendDelegate backendWrite)
    {
        if (buffer.Length == 0) return 0;
        var pageCache = PageCache;
        if (pageCache == null) return backendWrite(linuxFile, buffer, offset);
        if (linuxFile == null) return backendWrite(linuxFile, buffer, offset);
        if (offset < 0) return -(int)Errno.EINVAL;

        var consumed = 0;
        var cursor = offset;
        var tempPage = new byte[LinuxConstants.PageSize];
        while (consumed < buffer.Length)
        {
            var pageIndex = (uint)(cursor / LinuxConstants.PageSize);
            var pageOffset = (int)(cursor & LinuxConstants.PageOffsetMask);
            var chunk = Math.Min(buffer.Length - consumed, LinuxConstants.PageSize - pageOffset);
            var pagePtr = pageCache.GetPage(pageIndex);
            if (pagePtr == IntPtr.Zero)
            {
                var fullPageWrite = pageOffset == 0 && chunk == LinuxConstants.PageSize;
                var pageFileOffset = (long)pageIndex * LinuxConstants.PageSize;
                var pageReadLen = 0;
                if (!fullPageWrite && pageFileOffset < (long)Size)
                {
                    tempPage.AsSpan().Clear();
                    pageReadLen = (int)Math.Min((long)LinuxConstants.PageSize, (long)Size - pageFileOffset);
                    var rc = ReadPage(linuxFile, new PageIoRequest(pageIndex, pageFileOffset, pageReadLen), tempPage);
                    if (rc < 0) return consumed > 0 ? consumed : rc;
                }

                pagePtr = pageCache.GetOrCreatePage(pageIndex, p =>
                {
                    unsafe
                    {
                        var dst = new Span<byte>((void*)p, LinuxConstants.PageSize);
                        dst.Clear();
                        if (pageReadLen > 0) tempPage.AsSpan(0, pageReadLen).CopyTo(dst);
                    }

                    return true;
                }, out _);
                if (pagePtr == IntPtr.Zero) return consumed > 0 ? consumed : -(int)Errno.EIO;
            }

            unsafe
            {
                fixed (byte* src = &buffer[consumed])
                {
                    var dst = (byte*)pagePtr + pageOffset;
                    Buffer.MemoryCopy(src, dst, chunk, chunk);
                }
            }

            var dirtyRc = SetPageDirty(pageIndex);
            if (dirtyRc < 0) return consumed > 0 ? consumed : dirtyRc;

            // Route through page-level op for filesystem-specific bookkeeping.
            var writeRc = WritePage(linuxFile, new PageIoRequest(pageIndex, cursor, chunk), buffer.Slice(consumed, chunk), false);
            if (writeRc < 0) return consumed > 0 ? consumed : writeRc;

            consumed += chunk;
            cursor += chunk;
        }

        var end = offset + consumed;
        if (end > (long)Size) Size = (ulong)end;
        MTime = DateTime.Now;
        return consumed;
    }

    protected virtual int AopsReadPage(LinuxFile? linuxFile, PageIoRequest request, Span<byte> pageBuffer)
    {
        // Explicitly unsupported by default: filesystems must opt in.
        return -(int)Errno.EOPNOTSUPP;
    }

    protected virtual int AopsReadahead(LinuxFile? linuxFile, ReadaheadRequest request)
    {
        // Optional optimization hook; default no-op.
        return 0;
    }

    protected virtual int AopsWritePage(LinuxFile? linuxFile, PageIoRequest request, ReadOnlySpan<byte> pageBuffer, bool sync)
    {
        // Explicitly unsupported by default: filesystems must opt in.
        return -(int)Errno.EOPNOTSUPP;
    }

    protected virtual int AopsWritePages(LinuxFile? linuxFile, WritePagesRequest request)
    {
        // Optional batch writeback hook; default no-op.
        return 0;
    }

    protected virtual int AopsSetPageDirty(long pageIndex)
    {
        // Optional dirty accounting hook for filesystems.
        return 0;
    }

    public virtual int ReadPage(LinuxFile? linuxFile, PageIoRequest request, Span<byte> pageBuffer)
    {
        return AopsReadPage(linuxFile, request, pageBuffer);
    }

    public virtual int Readahead(LinuxFile? linuxFile, ReadaheadRequest request)
    {
        return AopsReadahead(linuxFile, request);
    }

    public virtual int WritePage(LinuxFile? linuxFile, PageIoRequest request, ReadOnlySpan<byte> pageBuffer, bool sync)
    {
        return AopsWritePage(linuxFile, request, pageBuffer, sync);
    }

    public virtual int WritePages(LinuxFile? linuxFile, WritePagesRequest request)
    {
        return AopsWritePages(linuxFile, request);
    }

    public virtual int SetPageDirty(long pageIndex)
    {
        return AopsSetPageDirty(pageIndex);
    }

    // Async blocking support
    public virtual ValueTask WaitForRead(LinuxFile linuxFile)
    {
        return ValueTask.CompletedTask;
    }

    public virtual ValueTask WaitForWrite(LinuxFile linuxFile)
    {
        return ValueTask.CompletedTask;
    }

    /// <summary>
    ///     Check for readiness. Returns a bitmask of POLL* constants.
    ///     Default implementation for regular files: Always readable and writable.
    /// </summary>
    public virtual short Poll(LinuxFile linuxFile, short events)
    {
        // Regular files are always ready for read/write
        // See Linux: fs/select.c
        const short POLLIN = 0x0001;
        const short POLLOUT = 0x0004;
        short revents = 0;
        if ((events & POLLIN) != 0) revents |= POLLIN;
        if ((events & POLLOUT) != 0) revents |= POLLOUT;
        return revents;
    }

    /// <summary>
    ///     Register a callback to be invoked when the file might be ready for the requested events.
    ///     The callback should be scheduled on the KernelScheduler.
    ///     Returns true if the wait was successfully registered, false if waiting is not supported or not necessary.
    /// </summary>
    public virtual bool RegisterWait(LinuxFile linuxFile, Action callback, short events)
    {
        // Default: Do nothing, return false.
        // For regular files, Poll() returns ready immediately, so we shouldn't be waiting.
        // If we are waiting on a regular file, it's weird, but we can just invoke callback immediately?
        // No, if Poll() returned 0 (not ready) for some reason, we would wait.
        // But for regular files Poll is always ready.
        return false;
    }

    /// <summary>
    ///     Register a callback and return a disposable handle that cancels the wait registration.
    ///     Default implementation falls back to RegisterWait and returns a no-op handle if accepted.
    /// </summary>
    public virtual IDisposable? RegisterWaitHandle(LinuxFile linuxFile, Action callback, short events)
    {
        return RegisterWait(linuxFile, callback, events) ? NoopWaitRegistration.Instance : null;
    }


    /// <summary>
    ///     Handle ioctl requests for this inode. Default implementation returns ENOTTY.
    /// </summary>
    public virtual int Ioctl(LinuxFile linuxFile, uint request, uint arg, Engine engine)
    {
        return -(int)Errno.ENOTTY;
    }

    /// <summary>
    ///     Handle flock requests for this inode. Default implementation returns ENOSYS.
    /// </summary>
    public virtual int Flock(LinuxFile linuxFile, int operation)
    {
        return -(int)Errno.ENOSYS;
    }

    /// <summary>
    ///     Whether this inode supports file-backed mmap.
    ///     Most inodes (devices, sockets, proc dynamic files, anon inodes) do not.
    /// </summary>
    public virtual bool SupportsMmap => false;

    public virtual int SetXAttr(string name, ReadOnlySpan<byte> value, int flags)
    {
        return -(int)Errno.EOPNOTSUPP;
    }

    public virtual int GetXAttr(string name, Span<byte> value)
    {
        return -(int)Errno.EOPNOTSUPP;
    }

    public virtual int ListXAttr(Span<byte> list)
    {
        return -(int)Errno.EOPNOTSUPP;
    }

    public virtual int RemoveXAttr(string name)
    {
        return -(int)Errno.EOPNOTSUPP;
    }

    // File operations hooks
    public virtual void Open(LinuxFile linuxFile)
    {
    }

    public virtual void Release(LinuxFile linuxFile)
    {
    }

    public virtual void Sync(LinuxFile linuxFile)
    {
    }

    // For directories, we need iteration. 
    public virtual List<DirectoryEntry> GetEntries()
    {
        return new List<DirectoryEntry>();
    }
}

public struct DirectoryEntry
{
    public string Name;
    public ulong Ino;
    public InodeType Type;
}

public interface IMagicSymlinkInode
{
    bool TryResolveLink(out LinuxFile file);
}

public class Dentry
{
    private static long _nextId;
    private int _refCount = 1;

    public Dentry(string name, Inode? inode, Dentry? parent, SuperBlock sb)
    {
        Name = name;
        Inode = inode;
        Parent = parent;
        SuperBlock = sb;
        if (inode != null)
        {
            inode.Dentries.Add(this);
            inode.Get();
        }
    }

    public long Id { get; } = Interlocked.Increment(ref _nextId);

    public string Name { get; set; }
    public Inode? Inode { get; set; }
    public Dentry? Parent { get; set; }
    public SuperBlock SuperBlock { get; set; }
    public Dictionary<string, Dentry> Children { get; } = [];

    // Mount point support
    public bool IsMounted { get; set; }

    /// <summary>
    ///     Increment reference count.
    /// </summary>
    public void Get()
    {
        Interlocked.Increment(ref _refCount);
    }

    /// <summary>
    ///     Decrement reference count.
    /// </summary>
    public void Put()
    {
        Interlocked.Decrement(ref _refCount);
    }

    public void Instantiate(Inode inode)
    {
        if (Inode != null) throw new InvalidOperationException("Dentry already instantiated");
        Inode = inode;
        Inode.Dentries.Add(this);
        Inode.Get();
    }
}

public class LinuxFile
{
    private int _refCount = 1;

    public LinuxFile(Dentry dentry, FileFlags flags, Mount mount)
    {
        Dentry = dentry;
        Flags = flags;
        Mount = mount; // The mount this file was opened through
        dentry.Inode?.Get(); // Increase reference count
        dentry.Inode?.Open(this);
        // Note: Mount reference is managed by caller if provided
    }

    public Dentry Dentry { get; set; }
    public long Position { get; set; }
    public FileFlags Flags { get; set; }
    public Mount Mount { get; set; }
    public object? PrivateData { get; set; }
    public bool IsTmpFile { get; set; }

    /// <summary>
    ///     Check if write operation is allowed (mount read-only check).
    ///     Similar to Linux kernel's mnt_want_write().
    /// </summary>
    public int WantWrite()
    {
        // Check file flags first
        var accessMode = (int)Flags & 3; // O_ACCMODE
        if (accessMode == 0) // O_RDONLY
            return -(int)Errno.EBADF;

        // Check mount read-only
        if (Mount != null && Mount.IsReadOnly)
            return -(int)Errno.EROFS;

        return 0;
    }

    /// <summary>
    ///     Increment the reference count (used by dup, SCM_RIGHTS, etc.).
    /// </summary>
    public void Get()
    {
        Interlocked.Increment(ref _refCount);
    }

    public virtual void Close()
    {
        if (Interlocked.Decrement(ref _refCount) > 0) return;

        if (IsTmpFile && Dentry.Parent?.Inode != null)
        {
            try
            {
                Dentry.Parent.Inode.Unlink(Dentry.Name);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }

        Dentry.Inode?.Release(this);
        Dentry.Inode?.Put(); // Decrease reference count
        // Note: Mount reference is not released here as it's typically
        // managed by the filesystem/superblock lifecycle
    }
}

public struct DCacheKey(ulong parentIno, string name) : IEquatable<DCacheKey>
{
    public ulong ParentIno = parentIno;
    public string Name = name;

    public readonly bool Equals(DCacheKey other)
    {
        return ParentIno == other.ParentIno && Name == other.Name;
    }

    public override bool Equals(object? obj)
    {
        return obj is DCacheKey other && Equals(other);
    }

    public readonly override int GetHashCode()
    {
        return HashCode.Combine(ParentIno, Name);
    }

    public static bool operator ==(DCacheKey left, DCacheKey right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(DCacheKey left, DCacheKey right)
    {
        return !(left == right);
    }
}
