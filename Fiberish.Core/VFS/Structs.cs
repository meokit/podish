using Fiberish.Core;
using Fiberish.Diagnostics;
using Fiberish.Memory;
using Fiberish.Native;
using Microsoft.Extensions.Logging;

using System;

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

public enum InodeRefKind
{
    KernelInternal = 0,
    FileOpen = 1,
    FileMmap = 2,
    PathPin = 3
}

public abstract class FileSystem
{
    protected FileSystem(DeviceNumberManager? devManager)
    {
        DevManager = devManager ?? new DeviceNumberManager();
    }

    public string Name { get; set; } = "";
    protected DeviceNumberManager DevManager { get; }

    public abstract SuperBlock ReadSuper(FileSystemType fsType, int flags, string devName, object? data);
}

public class FileSystemType
{
    public string Name { get; init; } = "";
    public Func<DeviceNumberManager, FileSystem> Factory { get; init; } = _ =>
        throw new InvalidOperationException("FileSystem factory is not configured.");

    public FileSystem CreateFileSystem(DeviceNumberManager? devManager = null)
    {
        return Factory(devManager ?? new DeviceNumberManager());
    }
}

public abstract class SuperBlock
{
    private readonly DeviceNumberManager? _devManager;
    private readonly uint _dev;

    protected SuperBlock(DeviceNumberManager? devManager = null)
    {
        _devManager = devManager;
        // 0 means anonymous (no real device)
        _dev = devManager?.Allocate() ?? 0;
    }

    protected HashSet<Inode> AllInodes = [];
    public FileSystemType Type { get; set; } = null!;
    public Dentry Root { get; set; } = null!;
    public int BlockSize { get; set; } = 4096;
    public List<Inode> Inodes { get; set; } = [];
    private readonly HashSet<Dentry> _trackedDentries = [];
    private readonly LinkedList<Dentry> _dentryLru = new();
    private readonly Dictionary<long, LinkedListNode<Dentry>> _dentryLruNodes = [];
    public object Lock { get; } = new();

    /// <summary>
    ///     Device ID for this superblock, encoded as (major &lt;&lt; 8) | minor.
    ///     Allocated from DeviceNumberManager. 0 for anonymous superblocks.
    /// </summary>
    public uint Dev => _dev;

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
        return AllInodes.Any(i => i.HasActiveRuntimeRefs);
    }

    protected virtual void Shutdown()
    {
        // Release device number back to the pool
        _devManager?.Free(_dev);
        // Subclasses can override to clean up resources
        AllInodes.Clear();
        Inodes.Clear();
        _trackedDentries.Clear();
        _dentryLru.Clear();
        _dentryLruNodes.Clear();
    }

    public virtual Inode AllocInode()
    {
        throw new NotSupportedException();
    }

    public virtual void WriteInode(Inode inode)
    {
    }

    protected void TrackInode(Inode inode)
    {
        lock (Lock)
        {
            if (!AllInodes.Add(inode)) return;
            Inodes.Add(inode);
        }
    }

    internal void EnsureInodeTracked(Inode inode)
    {
        TrackInode(inode);
    }

    internal void RegisterDentry(Dentry dentry)
    {
        lock (Lock)
        {
            if (!_trackedDentries.Add(dentry)) return;
            dentry.MarkTrackedBySuperBlock();
            if (dentry.DentryRefCount == 0)
                AddDentryToLruNoLock(dentry);
        }
    }

    internal void UnregisterDentry(Dentry dentry)
    {
        lock (Lock)
        {
            if (!_trackedDentries.Remove(dentry)) return;
            RemoveDentryFromLruNoLock(dentry);
            dentry.MarkUntrackedBySuperBlock();
        }
    }

    internal void NotifyDentryRefAcquired(Dentry dentry)
    {
        lock (Lock)
        {
            RemoveDentryFromLruNoLock(dentry);
        }
    }

    internal void NotifyDentryRefReleasedToZero(Dentry dentry)
    {
        lock (Lock)
        {
            if (!_trackedDentries.Contains(dentry))
                return;
            AddDentryToLruNoLock(dentry);
        }
    }

    internal List<Dentry> SnapshotDentryLru()
    {
        lock (Lock)
        {
            return _dentryLru.ToList();
        }
    }

    private void AddDentryToLruNoLock(Dentry dentry)
    {
        if (_dentryLruNodes.ContainsKey(dentry.Id))
        {
            dentry.SetLruState(true);
            return;
        }

        var node = _dentryLru.AddLast(dentry);
        _dentryLruNodes[dentry.Id] = node;
        dentry.SetLruState(true);
    }

    private void RemoveDentryFromLruNoLock(Dentry dentry)
    {
        if (!_dentryLruNodes.Remove(dentry.Id, out var node))
        {
            dentry.SetLruState(false);
            return;
        }

        _dentryLru.Remove(node);
        dentry.SetLruState(false);
    }

    internal void RemoveInodeFromTracking(Inode inode)
    {
        lock (Lock)
        {
            Inodes.Remove(inode);
            AllInodes.Remove(inode);
        }
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
    ///     Device ID for this inode. Defaults to the owning superblock's Dev.
    /// </summary>
    public virtual uint Dev => SuperBlock?.Dev ?? 0x800;

    /// <summary>
    ///     Device number (rdev) for character/block devices.
    ///     Encoded as (major << 8) | minor for compatibility.
    /// </summary>
    public virtual uint Rdev { get; set; }

    public SuperBlock SuperBlock { get; set; } = null!;

    // All dentries pointing to this inode (hard links / aliases).
    // Exposed as read-only; callers must go through BindInode/UnbindInode.
    private readonly List<Dentry> _dentries = [];
    public IReadOnlyList<Dentry> Dentries => _dentries;
    public object Lock { get; } = new();

    // Reference counting for lifecycle management
    public int RefCount { get; set; }
    public int FileOpenRefCount { get; private set; }
    public int FileMmapRefCount { get; private set; }
    public int PathPinRefCount { get; private set; }
    public int KernelInternalRefCount { get; private set; }
    public bool HasActiveRuntimeRefs => FileOpenRefCount > 0 || FileMmapRefCount > 0 || PathPinRefCount > 0;
    public int LinkCount { get; private set; }
    public bool HasExplicitLinkCount { get; private set; }
    public bool IsCacheEvicted { get; private set; }
    public bool IsFinalized { get; private set; }
    private int _lookupFailureError = -(int)Errno.ENOENT;

    public void AcquireRef(InodeRefKind kind, string? reason = null)
    {
        var before = RefCount;
        if (before < 0)
        {
            VfsDebugTrace.FailInvariant(
                $"Inode.AcquireRef invalid ino={Ino} type={Type} ref={before} kind={kind} reason={reason}");
            return;
        }

        if (IsFinalized)
        {
            VfsDebugTrace.FailInvariant(
                $"Inode.AcquireRef on finalized inode ino={Ino} type={Type} kind={kind} reason={reason}");
            return;
        }

        if (IsCacheEvicted)
        {
            VfsDebugTrace.FailInvariant(
                $"Inode.AcquireRef on cache-evicted inode ino={Ino} type={Type} kind={kind} reason={reason}");
            return;
        }

        IncrementRefKind(kind);
        RefCount++;
        VfsDebugTrace.RecordRefChange(this, $"Inode.AcquireRef.{kind}", before, RefCount, reason);
    }

    public void ReleaseRef(InodeRefKind kind, string? reason = null)
    {
        var before = RefCount;
        if (before <= 0)
        {
            VfsDebugTrace.FailInvariant(
                $"Inode.ReleaseRef underflow ino={Ino} type={Type} ref={before} kind={kind} reason={reason}");
            return;
        }

        if (!DecrementRefKind(kind))
        {
            VfsDebugTrace.FailInvariant(
                $"Inode.ReleaseRef kind underflow ino={Ino} type={Type} kind={kind} reason={reason}");
            return;
        }

        RefCount = before - 1;
        VfsDebugTrace.RecordRefChange(this, $"Inode.ReleaseRef.{kind}", before, RefCount, reason);
        TryFinalizeDelete($"ReleaseRef.{kind}", reason);
    }

    public void SetInitialLinkCount(int linkCount, string? reason = null)
    {
        if (linkCount < 0)
        {
            VfsDebugTrace.FailInvariant(
                $"Inode.SetInitialLinkCount negative ino={Ino} type={Type} link={linkCount} reason={reason}");
            return;
        }

        var before = LinkCount;
        LinkCount = linkCount;
        HasExplicitLinkCount = true;
        VfsDebugTrace.RecordLinkChange(this, "Inode.SetInitialLinkCount", before, LinkCount, reason);
        TryFinalizeDelete("SetInitialLinkCount", reason);
    }

    public void IncLink(string? reason = null)
    {
        EnsureLinkCountInitialized("IncLink");
        var before = LinkCount;
        LinkCount++;
        CTime = DateTime.Now;
        VfsDebugTrace.RecordLinkChange(this, "Inode.IncLink", before, LinkCount, reason);
    }

    public void DecLink(string? reason = null)
    {
        EnsureLinkCountInitialized("DecLink");
        var before = LinkCount;
        if (before <= 0)
        {
            VfsDebugTrace.FailInvariant(
                $"Inode.DecLink underflow ino={Ino} type={Type} link={before} reason={reason}");
            return;
        }

        LinkCount = before - 1;
        CTime = DateTime.Now;
        VfsDebugTrace.RecordLinkChange(this, "Inode.DecLink", before, LinkCount, reason);
        TryFinalizeDelete("DecLink", reason);
    }

    public uint GetLinkCountForStat()
    {
        if (HasExplicitLinkCount)
            return (uint)Math.Max(0, LinkCount);
        return (uint)GetDefaultLinkCountForType();
    }

    public bool TryEvictCache(string operation, string? reason = null)
    {
        if (IsCacheEvicted) return false;
        if (RefCount != 0) return false;
        IsCacheEvicted = true;
        VfsDebugTrace.RecordCacheEvict(this, operation, reason);
        OnEvictCache();
        return true;
    }

    public bool TryFinalizeDelete(string operation, string? reason = null)
    {
        if (IsFinalized) return false;
        if (!HasExplicitLinkCount) return false;
        if (RefCount != 0 || LinkCount != 0) return false;
        IsFinalized = true;
        VfsDebugTrace.RecordFinalize(this, operation, reason);
        OnFinalizeDelete();
        // Finalized (nlink=0 && ref=0) inodes are dead objects and should not linger in cache.
        TryEvictCache($"Finalize.{operation}", reason);
        return true;
    }

    private void EnsureLinkCountInitialized(string source)
    {
        if (HasExplicitLinkCount) return;
        LinkCount = GetDefaultLinkCountForType();
        HasExplicitLinkCount = true;
        VfsDebugTrace.RecordLinkChange(this, $"Inode.{source}.InitDefault", 0, LinkCount, null);
    }

    private int GetDefaultLinkCountForType()
    {
        return Type == InodeType.Directory ? 2 : 1;
    }

    private void IncrementRefKind(InodeRefKind kind)
    {
        switch (kind)
        {
            case InodeRefKind.FileOpen:
                FileOpenRefCount++;
                break;
            case InodeRefKind.FileMmap:
                FileMmapRefCount++;
                break;
            case InodeRefKind.PathPin:
                PathPinRefCount++;
                break;
            case InodeRefKind.KernelInternal:
                KernelInternalRefCount++;
                break;
            default:
                VfsDebugTrace.FailInvariant($"Inode.IncrementRefKind unknown kind={kind} ino={Ino}");
                break;
        }
    }

    private bool DecrementRefKind(InodeRefKind kind)
    {
        switch (kind)
        {
            case InodeRefKind.FileOpen:
                if (FileOpenRefCount <= 0) return false;
                FileOpenRefCount--;
                return true;
            case InodeRefKind.FileMmap:
                if (FileMmapRefCount <= 0) return false;
                FileMmapRefCount--;
                return true;
            case InodeRefKind.PathPin:
                if (PathPinRefCount <= 0) return false;
                PathPinRefCount--;
                return true;
            case InodeRefKind.KernelInternal:
                if (KernelInternalRefCount <= 0) return false;
                KernelInternalRefCount--;
                return true;
            default:
                return false;
        }
    }

    internal void AttachAliasDentry(Dentry dentry, string reason)
    {
        if (dentry.Inode != this)
            VfsDebugTrace.FailInvariant(
                $"AttachAliasDentry inode mismatch ino={Ino} dentry={dentry.Name} dentryInode={dentry.Inode?.Ino}");

        if (_dentries.Contains(dentry))
            VfsDebugTrace.FailInvariant($"AttachAliasDentry duplicate ino={Ino} dentry={dentry.Name}");

        _dentries.Add(dentry);
        VfsDebugTrace.RecordDentryBinding(this, dentry, "attach", reason);
        VfsDebugTrace.AssertDentryMembership(dentry, "AttachAliasDentry");
    }

    internal bool DetachAliasDentry(Dentry dentry, string reason)
    {
        var removed = _dentries.Remove(dentry);
        VfsDebugTrace.RecordDentryBinding(this, dentry, removed ? "detach" : "detach-miss", reason);
        if (!removed) return false;
        if (dentry.Inode != null && dentry.Inode != this)
            VfsDebugTrace.FailInvariant(
                $"DetachAliasDentry inode mismatch ino={Ino} dentry={dentry.Name} dentryInode={dentry.Inode?.Ino}");
        return true;
    }

    public uint GetDebugNlinkForStat(string source, uint nlink)
    {
        VfsDebugTrace.RecordStatNlink(this, source, nlink);
        return nlink;
    }

    protected virtual void OnEvictCache()
    {
        // Release page-cache resources and remove inode from in-core tracking.
        PageCacheManager?.ReleaseInodePageCache(this);
        SuperBlock?.RemoveInodeFromTracking(this);
    }

    protected virtual void OnFinalizeDelete()
    {
    }

    protected void SetLookupFailureError(int errno)
    {
        _lookupFailureError = errno == 0 ? -(int)Errno.ENOENT : errno;
    }

    public virtual int ConsumeLookupFailureError(string name)
    {
        _ = name;
        var error = _lookupFailureError;
        _lookupFailureError = -(int)Errno.ENOENT;
        return error == 0 ? -(int)Errno.ENOENT : error;
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
            }, out _, strictQuota: true, AllocationClass.PageCache);

            if (pagePtr == IntPtr.Zero)
            {
                // OOM on cache population: serve data without caching.
                tempPage.AsSpan(pageOffset, toCopy).CopyTo(buffer[total..]);
                total += toCopy;
                cursor += toCopy;
                continue;
            }

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
                }, out _, strictQuota: true, AllocationClass.PageCache);
                if (pagePtr == IntPtr.Zero) return consumed > 0 ? consumed : -(int)Errno.ENOMEM;
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
            pageCache.MarkDirty(pageIndex);

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

    /// <summary>
    ///     Optional fast path: allow filesystem to provide an externally-owned mapped page.
    ///     Return false to use regular in-memory page cache allocation + ReadPage fallback.
    /// </summary>
    public virtual bool TryAcquireMappedPageHandle(LinuxFile? linuxFile, long pageIndex, long absoluteFileOffset,
        out IPageHandle? pageHandle)
    {
        pageHandle = null;
        return false;
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

    public Dentry(string name, Inode? inode, Dentry? parent, SuperBlock sb)
    {
        Name = name;
        Parent = parent;
        SuperBlock = sb;
        IsOnLru = true;
        EnsureTrackedBySuperBlock("Dentry.ctor");
        if (inode != null) BindInode(inode, "Dentry.ctor");
    }

    public long Id { get; } = Interlocked.Increment(ref _nextId);

    public string Name { get; set; }
    public Inode? Inode { get; private set; }
    public Dentry? Parent { get; set; }
    public SuperBlock SuperBlock { get; set; }
    public Dictionary<string, Dentry> Children { get; } = [];
    public int DentryRefCount { get; private set; }
    public bool IsOnLru { get; private set; }
    public bool IsTrackedBySuperBlock { get; private set; }
    public bool IsHashed { get; private set; }
    public bool IsNegative => Inode == null;

    // Mount point support
    public bool IsMounted { get; set; }

    public void Get(string? reason = null)
    {
        EnsureTrackedBySuperBlock("Dentry.Get");
        var before = DentryRefCount;
        if (before < 0)
        {
            VfsDebugTrace.FailInvariant(
                $"Dentry.Get invalid dentry={Name} dentryId={Id} ref={before} reason={reason}");
            return;
        }

        DentryRefCount = before + 1;
        if (before == 0)
            SuperBlock.NotifyDentryRefAcquired(this);
        VfsDebugTrace.RecordDentryRefChange(this, "Dentry.Get", before, DentryRefCount, reason);
    }

    public void Put(string? reason = null)
    {
        EnsureTrackedBySuperBlock("Dentry.Put");
        var before = DentryRefCount;
        if (before <= 0)
        {
            VfsDebugTrace.FailInvariant(
                $"Dentry.Put underflow dentry={Name} dentryId={Id} ref={before} reason={reason}");
            return;
        }

        DentryRefCount = before - 1;
        if (DentryRefCount == 0)
            SuperBlock.NotifyDentryRefReleasedToZero(this);
        VfsDebugTrace.RecordDentryRefChange(this, "Dentry.Put", before, DentryRefCount, reason);
    }

    public void Instantiate(Inode inode)
    {
        if (Inode != null) throw new InvalidOperationException("Dentry already instantiated");
        BindInode(inode, "Dentry.Instantiate");
    }

    public bool TryGetCachedChild(string name, out Dentry cached)
    {
        if (Children.TryGetValue(name, out var found))
        {
            cached = found;
            return true;
        }

        cached = null!;
        return false;
    }

    public void CacheChild(Dentry child, string reason)
    {
        child.EnsureTrackedBySuperBlock($"{reason}.child-track");
        child.Parent = this;
        Children[child.Name] = child;
        VfsDebugTrace.RecordDentryCacheUpdate(this, child, "cache-add", reason);
    }

    public bool TryUncacheChild(string name, string reason, out Dentry? removed)
    {
        if (!Children.Remove(name, out removed))
            return false;
        VfsDebugTrace.RecordDentryCacheUpdate(this, removed, "cache-remove", reason);
        return true;
    }

    public void ClearCachedChildren(string reason)
    {
        foreach (var child in Children.Values)
            VfsDebugTrace.RecordDentryCacheUpdate(this, child, "cache-clear", reason);
        Children.Clear();
    }

    public void BindInode(Inode inode, string reason)
    {
        if (Inode != null) throw new InvalidOperationException("Dentry already bound");
        if (!ReferenceEquals(inode.SuperBlock, SuperBlock))
        {
            VfsDebugTrace.FailInvariant(
                $"Dentry.BindInode superblock mismatch dentry={Name} dentrySb={SuperBlock.Type?.Name} inodeSb={inode.SuperBlock?.Type?.Name} reason={reason}");
            return;
        }
        EnsureTrackedBySuperBlock("Dentry.BindInode");
        SuperBlock.EnsureInodeTracked(inode);
        Inode = inode;
        Inode.AttachAliasDentry(this, reason);
        Inode.AcquireRef(InodeRefKind.KernelInternal, reason);
        VfsDebugTrace.AssertDentryMembership(this, reason);
    }

    public bool UnbindInode(string reason)
    {
        var inode = Inode;
        if (inode == null) return false;
        var detached = inode.DetachAliasDentry(this, reason);
        Inode = null;
        if (detached)
            inode.ReleaseRef(InodeRefKind.KernelInternal, reason);
        return detached;
    }

    internal void EnsureTrackedBySuperBlock(string reason)
    {
        if (IsTrackedBySuperBlock) return;
        SuperBlock.RegisterDentry(this);
        if (!IsTrackedBySuperBlock)
            VfsDebugTrace.FailInvariant($"Dentry track failed dentry={Name} dentryId={Id} reason={reason}");
    }

    internal void UntrackFromSuperBlock(string reason)
    {
        if (!IsTrackedBySuperBlock) return;
        SuperBlock.UnregisterDentry(this);
        if (IsTrackedBySuperBlock)
            VfsDebugTrace.FailInvariant($"Dentry untrack failed dentry={Name} dentryId={Id} reason={reason}");
    }

    internal void SetLruState(bool onLru)
    {
        IsOnLru = onLru;
    }

    internal void MarkTrackedBySuperBlock()
    {
        IsTrackedBySuperBlock = true;
        IsHashed = true;
    }

    internal void MarkUntrackedBySuperBlock()
    {
        IsTrackedBySuperBlock = false;
        IsHashed = false;
    }
}

public readonly record struct InodeRefTrace(
    DateTime TimestampUtc,
    ulong Ino,
    InodeType InodeType,
    string Operation,
    int RefBefore,
    int RefAfter,
    int DentryCount,
    string? Detail);

public static class VfsDebugTrace
{
    private static readonly ILogger Logger = Logging.CreateLogger("Fiberish.VFS.RefTrace");
    private static readonly object TraceLock = new();
    private const int MaxTraceEntries = 4096;
    private static readonly Queue<InodeRefTrace> RefTraceQueue = [];

    public static bool Enabled { get; set; } =
#if DEBUG
        true;
#else
        false;
#endif

    public static bool StrictInvariants { get; set; } =
#if DEBUG
        true;
#else
        false;
#endif

    public static IReadOnlyList<InodeRefTrace> SnapshotRefTrace()
    {
        lock (TraceLock)
        {
            return RefTraceQueue.ToList();
        }
    }

    public static void ClearRefTrace()
    {
        lock (TraceLock)
        {
            RefTraceQueue.Clear();
        }
    }

    public static void RecordRefChange(Inode inode, string operation, int before, int after, string? detail)
    {
        var entry = new InodeRefTrace(
            DateTime.UtcNow,
            inode.Ino,
            inode.Type,
            operation,
            before,
            after,
            inode.Dentries.Count,
            detail);
        lock (TraceLock)
        {
            RefTraceQueue.Enqueue(entry);
            while (RefTraceQueue.Count > MaxTraceEntries)
                RefTraceQueue.Dequeue();
        }

        if (Enabled)
            Logger.LogDebug(
                "[VFS-Ref] op={Operation} ino={Ino} type={Type} ref:{Before}->{After} dentries={DentryCount} detail={Detail}",
                operation, inode.Ino, inode.Type, before, after, inode.Dentries.Count, detail ?? "");
    }

    public static void RecordDentryBinding(Inode inode, Dentry dentry, string operation, string reason)
    {
        if (!Enabled) return;
        Logger.LogDebug(
            "[VFS-Dentry] op={Operation} reason={Reason} ino={Ino} dentry={Name} dentryId={DentryId} parent={Parent} dentries={DentryCount}",
            operation, reason, inode.Ino, dentry.Name, dentry.Id, dentry.Parent?.Name ?? "<null>", inode.Dentries.Count);
    }

    public static void RecordDentryRefChange(Dentry dentry, string operation, int before, int after, string? reason)
    {
        if (!Enabled) return;
        Logger.LogDebug(
            "[VFS-DentryRef] op={Operation} dentry={Name} dentryId={DentryId} ref:{Before}->{After} lru={Lru} reason={Reason}",
            operation, dentry.Name, dentry.Id, before, after, dentry.IsOnLru, reason ?? "");
    }

    public static void RecordDentryCacheUpdate(Dentry parent, Dentry child, string operation, string reason)
    {
        if (!Enabled) return;
        Logger.LogDebug(
            "[VFS-Dcache] op={Operation} reason={Reason} parent={ParentName} parentId={ParentId} child={ChildName} childId={ChildId}",
            operation, reason, parent.Name, parent.Id, child.Name, child.Id);
    }

    public static void RecordStatNlink(Inode inode, string source, uint nlink)
    {
        if (!Enabled) return;
        Logger.LogDebug(
            "[VFS-StatNlink] source={Source} ino={Ino} type={Type} nlink={Nlink} ref={RefCount} dentries={DentryCount}",
            source, inode.Ino, inode.Type, nlink, inode.RefCount, inode.Dentries.Count);
    }

    public static void RecordLinkChange(Inode inode, string operation, int before, int after, string? reason)
    {
        if (!Enabled) return;
        Logger.LogDebug(
            "[VFS-Link] op={Operation} ino={Ino} type={Type} nlink:{Before}->{After} ref={RefCount} dentries={DentryCount} reason={Reason}",
            operation, inode.Ino, inode.Type, before, after, inode.RefCount, inode.Dentries.Count, reason ?? "");
    }

    public static void RecordCacheEvict(Inode inode, string operation, string? reason)
    {
        if (!Enabled) return;
        Logger.LogDebug(
            "[VFS-CacheEvict] op={Operation} ino={Ino} type={Type} nlink={Nlink} ref={RefCount} reason={Reason}",
            operation, inode.Ino, inode.Type, inode.LinkCount, inode.RefCount, reason ?? "");
    }

    public static void RecordFinalize(Inode inode, string operation, string? reason)
    {
        if (!Enabled) return;
        Logger.LogDebug(
            "[VFS-Finalize] op={Operation} ino={Ino} type={Type} nlink={Nlink} ref={RefCount} reason={Reason}",
            operation, inode.Ino, inode.Type, inode.LinkCount, inode.RefCount, reason ?? "");
    }

    public static void AssertDentryMembership(Dentry dentry, string source)
    {
        var inode = dentry.Inode;
        if (inode == null) return;
        if (inode.Dentries.Contains(dentry)) return;
        FailInvariant(
            $"Dentry membership invariant broken source={source} dentry={dentry.Name} inode={inode.Ino} dentryId={dentry.Id}");
    }

    public static void FailInvariant(string message)
    {
        Logger.LogError("[VFS-Invariant] {Message}", message);
        if (StrictInvariants)
            throw new InvalidOperationException(message);
    }
}

public class LinuxFile
{
    private int _refCount = 1;

    public enum ReferenceKind
    {
        Normal = 0,
        MmapHold = 1
    }

    public LinuxFile(Dentry dentry, FileFlags flags, Mount mount, ReferenceKind referenceKind = ReferenceKind.Normal)
    {
        Dentry = dentry;
        OpenedInode = dentry.Inode;
        Flags = flags;
        Mount = mount; // The mount this file was opened through
        Kind = referenceKind;
        Dentry.Get("LinuxFile.ctor");
        var refKind = referenceKind == ReferenceKind.MmapHold ? InodeRefKind.FileMmap : InodeRefKind.FileOpen;
        OpenedInode?.AcquireRef(refKind, "LinuxFile.ctor");
        OpenedInode?.Open(this);
        // Note: Mount reference is managed by caller if provided
    }

    public Dentry Dentry { get; set; }
    public Inode? OpenedInode { get; }
    public long Position { get; set; }
    public FileFlags Flags { get; set; }
    public Mount Mount { get; set; }
    public object? PrivateData { get; set; }
    public bool IsTmpFile { get; set; }
    public ReferenceKind Kind { get; }

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

        OpenedInode?.Release(this);
        var refKind = Kind == ReferenceKind.MmapHold ? InodeRefKind.FileMmap : InodeRefKind.FileOpen;
        OpenedInode?.ReleaseRef(refKind, "LinuxFile.Close");
        Dentry.Put("LinuxFile.Close");
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
