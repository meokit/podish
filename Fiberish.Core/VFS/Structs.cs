using System.Diagnostics;
using System.Runtime.CompilerServices;
using Fiberish.Auth.Permission;
using Fiberish.Core;
using Fiberish.Diagnostics;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Microsoft.Extensions.Logging;

namespace Fiberish.VFS;

internal interface ITaskWaitSource
{
    bool RegisterWait(LinuxFile linuxFile, FiberTask task, Action callback, short events);
    IDisposable? RegisterWaitHandle(LinuxFile linuxFile, FiberTask task, Action callback, short events);
}

internal interface ITaskPollSource
{
    short Poll(LinuxFile linuxFile, FiberTask task, short events);
}

internal interface IDispatcherWaitSource
{
    bool RegisterWait(LinuxFile linuxFile, IReadyDispatcher dispatcher, Action callback, short events);
    IDisposable? RegisterWaitHandle(LinuxFile linuxFile, IReadyDispatcher dispatcher, Action callback, short events);
}

internal readonly struct QueueReadinessWatch
{
    private readonly bool _isReadySnapshot;
    private readonly Func<bool>? _isReadyProbe;

    public QueueReadinessWatch(short eventMask, bool isReady, AsyncWaitQueue? queue, Action? resetStaleSignal = null)
    {
        EventMask = eventMask;
        _isReadySnapshot = isReady;
        _isReadyProbe = null;
        Queue = queue;
        ResetStaleSignal = resetStaleSignal;
    }

    public QueueReadinessWatch(short eventMask, Func<bool> isReadyProbe, AsyncWaitQueue? queue,
        Action? resetStaleSignal = null)
    {
        EventMask = eventMask;
        _isReadySnapshot = false;
        _isReadyProbe = isReadyProbe ?? throw new ArgumentNullException(nameof(isReadyProbe));
        Queue = queue;
        ResetStaleSignal = resetStaleSignal;
    }

    public short EventMask { get; }
    public bool IsReady => _isReadyProbe?.Invoke() ?? _isReadySnapshot;
    public AsyncWaitQueue? Queue { get; }
    public Action? ResetStaleSignal { get; }

    public bool ShouldObserve(short requestedEvents)
    {
        return Queue != null && (requestedEvents & EventMask) != 0;
    }

    public void ResetIfStale()
    {
        // Linux poll(2): readiness means the requested I/O would not block.
        // If the queue is still signaled after readiness became false, clear the stale edge
        // before registering so we don't spuriously wake callbacks on non-ready fds.
        if (!IsReady && Queue != null && Queue.IsSignaled)
            ResetStaleSignal?.Invoke();
    }
}

internal static class QueueReadinessRegistration
{
    public static short ComputeRevents(short events, in QueueReadinessWatch first,
        in QueueReadinessWatch second = default)
    {
        short revents = 0;
        revents |= ComputeWatchRevents(events, first);
        revents |= ComputeWatchRevents(events, second);
        return revents;
    }

    public static bool Register(Action callback, FiberTask task, short events, in QueueReadinessWatch first,
        in QueueReadinessWatch second = default)
    {
        var registered = false;
        registered |= RegisterWatch(callback, task, events, first);
        registered |= RegisterWatch(callback, task, events, second);
        return registered;
    }

    public static bool Register(Action callback, KernelScheduler scheduler, short events, in QueueReadinessWatch first,
        in QueueReadinessWatch second = default)
    {
        var registered = false;
        registered |= RegisterWatch(callback, scheduler, events, first);
        registered |= RegisterWatch(callback, scheduler, events, second);
        return registered;
    }

    public static IDisposable? RegisterHandle(Action callback, FiberTask task, short events,
        in QueueReadinessWatch first, in QueueReadinessWatch second = default)
    {
        return RegisterHandleCore(callback, events, task, null, first, second);
    }

    public static IDisposable? RegisterHandle(Action callback, KernelScheduler scheduler, short events,
        in QueueReadinessWatch first, in QueueReadinessWatch second = default)
    {
        return RegisterHandleCore(callback, events, null, scheduler, first, second);
    }

    private static short ComputeWatchRevents(short requestedEvents, in QueueReadinessWatch watch)
    {
        return watch.ShouldObserve(requestedEvents) && watch.IsReady ? watch.EventMask : (short)0;
    }

    private static bool RegisterWatch(Action callback, FiberTask task, short requestedEvents,
        in QueueReadinessWatch watch)
    {
        if (!watch.ShouldObserve(requestedEvents))
            return false;

        if (watch.IsReady)
        {
            task.CommonKernel.ScheduleContinuation(callback, task);
            return true;
        }

        watch.ResetIfStale();

        watch.Queue!.Register(callback, task);
        return true;
    }

    private static bool RegisterWatch(Action callback, KernelScheduler scheduler, short requestedEvents,
        in QueueReadinessWatch watch)
    {
        if (!watch.ShouldObserve(requestedEvents))
            return false;

        if (watch.IsReady)
        {
            scheduler.Schedule(callback);
            return true;
        }

        watch.ResetIfStale();

        watch.Queue!.Register(callback, scheduler);
        return true;
    }

    private static IDisposable? RegisterHandleCore(Action callback, short requestedEvents, FiberTask? task,
        KernelScheduler? scheduler, in QueueReadinessWatch first, in QueueReadinessWatch second)
    {
        List<IDisposable>? registrations = null;
        TryRegisterHandle(callback, requestedEvents, task, scheduler, first, ref registrations);
        TryRegisterHandle(callback, requestedEvents, task, scheduler, second, ref registrations);
        if (registrations == null || registrations.Count == 0)
            return null;
        if (registrations.Count == 1)
            return registrations[0];
        return new CompositeWaitRegistration(registrations);
    }

    private static void TryRegisterHandle(Action callback, short requestedEvents, FiberTask? task,
        KernelScheduler? scheduler, in QueueReadinessWatch watch, ref List<IDisposable>? registrations)
    {
        if (!watch.ShouldObserve(requestedEvents))
            return;

        if (watch.IsReady)
        {
            ScheduleReadyCallback(callback, task, scheduler);
            registrations ??= [];
            registrations.Add(NoopWaitRegistration.Instance);
            return;
        }

        watch.ResetIfStale();

        var registration = task != null
            ? watch.Queue!.RegisterCancelable(callback, task)
            : watch.Queue!.RegisterCancelable(callback,
                scheduler ?? throw new InvalidOperationException("Scheduler is required for wait registration."));
        if (registration == null)
            return;

        registrations ??= [];
        registrations.Add(registration);
    }

    private static void ScheduleReadyCallback(Action callback, FiberTask? task, KernelScheduler? scheduler)
    {
        if (task != null)
        {
            task.CommonKernel.ScheduleContinuation(callback, task);
            return;
        }

        (scheduler ?? throw new InvalidOperationException("Scheduler is required for wait registration."))
            .Schedule(callback);
    }

    private sealed class CompositeWaitRegistration : IDisposable
    {
        private readonly List<IDisposable> _registrations;

        public CompositeWaitRegistration(List<IDisposable> registrations)
        {
            _registrations = registrations;
        }

        public void Dispose()
        {
            foreach (var registration in _registrations)
                registration.Dispose();
        }
    }

    private sealed class NoopWaitRegistration : IDisposable
    {
        public static readonly NoopWaitRegistration Instance = new();

        public void Dispose()
        {
        }
    }
}

internal interface IHostMappedCacheDropper
{
    FilePageBackendDiagnostics GetMappedCacheDiagnostics();
    long TrimMappedCache(bool aggressive);
}

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

public interface IAddressSpaceOperations : IPageCacheOps
{
    int ReadFolio(LinuxFile? linuxFile, PageIoRequest request, Span<byte> pageBuffer);
    int DirtyFolio(long pageIndex);
    int Writepage(LinuxFile? linuxFile, PageIoRequest request, ReadOnlySpan<byte> pageBuffer, bool sync);
    int Writepages(LinuxFile? linuxFile, WritePagesRequest request);
    int InvalidateFolio(long pageIndex);
    int ReleaseFolio(long pageIndex);
}

public delegate bool ReadOnlySpanVisitor(ReadOnlySpan<byte> buffer);

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
    O_ACCMODE = 3,
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
    protected FileSystem(DeviceNumberManager? devManager, MemoryRuntimeContext? memoryContext = null)
    {
        DevManager = devManager ?? new DeviceNumberManager();
        MemoryContext = memoryContext ?? new MemoryRuntimeContext();
    }

    public string Name { get; set; } = "";
    protected DeviceNumberManager DevManager { get; }
    public MemoryRuntimeContext MemoryContext { get; internal set; }

    public abstract SuperBlock ReadSuper(FileSystemType fsType, int flags, string devName, object? data);
}

public class FileSystemType
{
    public string Name { get; init; } = "";

    public Func<DeviceNumberManager, FileSystem> Factory { get; init; } = _ =>
        throw new InvalidOperationException("FileSystem factory is not configured.");
    public Func<DeviceNumberManager, MemoryRuntimeContext, FileSystem>? FactoryWithContext { get; init; }

    public FileSystem CreateFileSystem(DeviceNumberManager devManager)
    {
        ArgumentNullException.ThrowIfNull(devManager);
        return Factory(devManager);
    }

    public FileSystem CreateFileSystem(DeviceNumberManager devManager, MemoryRuntimeContext memoryContext)
    {
        ArgumentNullException.ThrowIfNull(memoryContext);
        var fileSystem = FactoryWithContext != null
            ? FactoryWithContext(devManager, memoryContext)
            : CreateFileSystem(devManager);
        if (!ReferenceEquals(fileSystem.MemoryContext, memoryContext))
            fileSystem.MemoryContext = memoryContext;
        return fileSystem;
    }

    public FileSystem CreateAnonymousFileSystem()
    {
        return Factory(new DeviceNumberManager());
    }

    public FileSystem CreateAnonymousFileSystem(MemoryRuntimeContext memoryContext)
    {
        ArgumentNullException.ThrowIfNull(memoryContext);
        var fileSystem = FactoryWithContext != null
            ? FactoryWithContext(new DeviceNumberManager(), memoryContext)
            : CreateAnonymousFileSystem();
        if (!ReferenceEquals(fileSystem.MemoryContext, memoryContext))
            fileSystem.MemoryContext = memoryContext;
        return fileSystem;
    }
}

public abstract class SuperBlock
{
    private readonly LinkedList<Dentry> _dentryLru = new();
    private readonly Dictionary<long, LinkedListNode<Dentry>> _dentryLruNodes = [];
    private readonly DeviceNumberManager? _devManager;
    private readonly HashSet<Dentry> _trackedDentries = [];

    protected HashSet<Inode> AllInodes = [];

    protected SuperBlock(DeviceNumberManager? devManager, MemoryRuntimeContext memoryContext)
    {
        ArgumentNullException.ThrowIfNull(memoryContext);
        _devManager = devManager;
        MemoryContext = memoryContext;
        // 0 means anonymous (no real device)
        Dev = devManager?.Allocate() ?? 0;
    }

    public FileSystemType Type { get; set; } = null!;
    public Dentry Root { get; set; } = null!;
    public List<Inode> Inodes { get; set; } = [];
    public Lock Lock { get; } = new();
    public MemoryRuntimeContext MemoryContext { get; internal set; }

    /// <summary>
    ///     Device ID for this superblock, encoded as (major &lt;&lt; 8) | minor.
    ///     Allocated from DeviceNumberManager. 0 for anonymous superblocks.
    /// </summary>
    public uint Dev { get; }

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
        _devManager?.Free(Dev);
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

internal static class VfsFileHolderTracking
{
    [Conditional("VFS_FILE_HOLDER_TRACKING")]
    public static void Register(Inode? inode, LinuxFile file, string attachReason)
    {
        inode?.RegisterActiveFileHolderCore(file, attachReason);
    }

    [Conditional("VFS_FILE_HOLDER_TRACKING")]
    public static void Unregister(Inode? inode, LinuxFile file)
    {
        inode?.UnregisterActiveFileHolderCore(file);
    }
}

public abstract class Inode : IAddressSpaceOperations, IBackingPageHandleReleaseOwner
{
    // All dentries pointing to this inode (hard links / aliases).
    // Exposed as read-only; callers must go through BindInode/UnbindInode.
    private readonly List<Dentry> _dentries = [];
    private int _lookupFailureError = -(int)Errno.ENOENT;
    private VMAManager[] _mappedAddressSpaces = [];

    /// <summary>
    ///     Optional per-inode page cache / address_space.
    ///     Mapping-backed inode families manage its lifecycle via <see cref="MappingBackedInode" />.
    /// </summary>
    public AddressSpace? Mapping { get; protected set; }

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
    ///     If the inode is detached from any superblock, report 0 instead of a fake device id.
    /// </summary>
    public virtual uint Dev => SuperBlock?.Dev ?? 0;

    /// <summary>
    ///     Device number (rdev) for character/block devices.
    ///     Encoded as (major << 8) | minor for compatibility.
    /// </summary>
    public virtual uint Rdev { get; set; }

    public SuperBlock SuperBlock { get; set; } = null!;
    public IReadOnlyList<Dentry> Dentries => _dentries;
    public Lock Lock { get; } = new();

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

    /// <summary>
    ///     Whether this inode supports file-backed mmap.
    ///     Most inodes (devices, sockets, proc dynamic files, anon inodes) do not.
    /// </summary>
    public virtual bool SupportsMmap => false;

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

    public virtual int ReadFolio(LinuxFile? linuxFile, PageIoRequest request, Span<byte> pageBuffer)
    {
        return ReadPage(linuxFile, request, pageBuffer);
    }

    public virtual int DirtyFolio(long pageIndex)
    {
        return SetPageDirty(pageIndex);
    }

    public virtual int Writepage(LinuxFile? linuxFile, PageIoRequest request, ReadOnlySpan<byte> pageBuffer, bool sync)
    {
        return WritePage(linuxFile, request, pageBuffer, sync);
    }

    public virtual int Writepages(LinuxFile? linuxFile, WritePagesRequest request)
    {
        return WritePages(linuxFile, request);
    }

    public virtual int InvalidateFolio(long pageIndex)
    {
        return 0;
    }

    public virtual int ReleaseFolio(long pageIndex)
    {
        return 0;
    }

    internal void RegisterActiveFileHolderCore(LinuxFile file, string attachReason)
    {
#if VFS_FILE_HOLDER_TRACKING
        lock (_activeFileHoldersSync)
        {
            _activeFileHolders[file] = attachReason;
        }
#endif
    }

    internal void UnregisterActiveFileHolderCore(LinuxFile file)
    {
#if VFS_FILE_HOLDER_TRACKING
        lock (_activeFileHoldersSync)
        {
            _activeFileHolders.Remove(file);
        }
#endif
    }

    internal string DescribeActiveFileHoldersForDebug()
    {
#if VFS_FILE_HOLDER_TRACKING
        lock (_activeFileHoldersSync)
        {
            if (_activeFileHolders.Count == 0)
                return "<none>";

            return string.Join(" || ",
                _activeFileHolders
                    .OrderBy(kv => kv.Key.DebugId)
                    .Select(kv => $"{kv.Key.GetDebugSummary()} attach={kv.Value}"));
        }
#else
        return "<tracking-disabled>";
#endif
    }

    public virtual int UpdateTimes(DateTime? atime, DateTime? mtime, DateTime? ctime)
    {
        if (atime.HasValue) ATime = atime.Value;
        if (mtime.HasValue) MTime = mtime.Value;
        if (ctime.HasValue) CTime = ctime.Value;
        return 0;
    }

    public virtual void InvalidateInodePages2Range(uint startPageIndex, uint endPageIndex)
    {
        Mapping?.RemovePagesInRange(startPageIndex, endPageIndex, static page => !page.Dirty);
    }

    public virtual void TruncateInodePagesRange(long start, long end)
    {
        if (end < start) return;
        if (Mapping == null) return;
        var startPage = (uint)Math.Max(0, start / LinuxConstants.PageSize);
        var endPageExclusive = (uint)Math.Max(startPage, (end + LinuxConstants.PageSize) / LinuxConstants.PageSize);
        Mapping.RemovePagesInRange(startPage, endPageExclusive);
    }

    public virtual void UnmapMappingRange(long start, long len, bool evenCows)
    {
        ProcessAddressSpaceSync.UnmapMappingRange(this, start, len, evenCows);
    }

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
        ReleaseUnlinkedAliasDentriesIfUnused($"ReleaseRef.{kind}", reason);
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
        CTime = DateTime.Now;
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
        ReleaseUnlinkedAliasDentriesIfUnused("DecLink", reason);
        TryFinalizeDelete("DecLink", reason);
    }

    public virtual uint GetLinkCountForStat()
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

    private void ReleaseUnlinkedAliasDentriesIfUnused(string operation, string? reason)
    {
        if (LinkCount != 0 || HasActiveRuntimeRefs || _dentries.Count == 0)
            return;

        foreach (var dentry in _dentries.Where(d => !d.IsHashed).ToArray())
        {
            if (!dentry.UnbindInode($"{operation}.unlinked-alias"))
                continue;

            if (dentry.DentryRefCount == 0)
                dentry.UntrackFromSuperBlock($"{operation}.unlinked-alias");
        }
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

    internal void RegisterMappedAddressSpace(VMAManager addressSpace)
    {
        foreach (var mapped in _mappedAddressSpaces)
            if (ReferenceEquals(mapped, addressSpace))
                return;
        var next = new VMAManager[_mappedAddressSpaces.Length + 1];
        Array.Copy(_mappedAddressSpaces, next, _mappedAddressSpaces.Length);
        next[^1] = addressSpace;
        _mappedAddressSpaces = next;
    }

    internal void UnregisterMappedAddressSpace(VMAManager addressSpace)
    {
        var index = -1;
        for (var i = 0; i < _mappedAddressSpaces.Length; i++)
            if (ReferenceEquals(_mappedAddressSpaces[i], addressSpace))
            {
                index = i;
                break;
            }

        if (index < 0) return;
        if (_mappedAddressSpaces.Length == 1)
        {
            _mappedAddressSpaces = [];
            return;
        }

        var next = new VMAManager[_mappedAddressSpaces.Length - 1];
        if (index > 0)
            Array.Copy(_mappedAddressSpaces, 0, next, 0, index);
        if (index < _mappedAddressSpaces.Length - 1)
            Array.Copy(_mappedAddressSpaces, index + 1, next, index, _mappedAddressSpaces.Length - index - 1);
        _mappedAddressSpaces = next;
    }

    internal VMAManager[] SnapshotMappedAddressSpaces()
    {
        return _mappedAddressSpaces;
    }

    public uint GetDebugNlinkForStat(string source, uint nlink)
    {
        VfsDebugTrace.RecordStatNlink(this, source, nlink);
        return nlink;
    }

    protected virtual void OnEvictCache()
    {
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
    public virtual Dentry? Lookup(ReadOnlySpan<byte> name)
    {
        if (!FsEncoding.TryDecodeUtf8(name, out var decoded))
            return null;
        return Lookup(decoded);
    }

    public virtual Dentry? Lookup(FsName name)
    {
        return Lookup(name.Bytes);
    }

    public virtual Dentry? Lookup(string name)
    {
        return null;
    }

    /// <summary>
    ///     Revalidate a cached child dentry before path walk uses it.
    ///     Return false to drop cache and force fresh Lookup(name).
    /// </summary>
    public virtual bool RevalidateCachedChild(Dentry parent, ReadOnlySpan<byte> name, Dentry cached)
    {
        return true;
    }

    public virtual bool RevalidateCachedChild(Dentry parent, FsName name, Dentry cached)
    {
        return RevalidateCachedChild(parent, name.Bytes, cached);
    }

    public virtual bool RevalidateCachedChild(Dentry parent, string name, Dentry cached)
    {
        return true;
    }

    public virtual int Create(Dentry dentry, int mode, int uid, int gid)
    {
        return -(int)Errno.EOPNOTSUPP;
    }

    public virtual int Truncate(long length)
    {
        return -(int)Errno.ENOSYS;
    }

    public virtual int Mkdir(Dentry dentry, int mode, int uid, int gid)
    {
        return -(int)Errno.EOPNOTSUPP;
    }

    public virtual int Unlink(ReadOnlySpan<byte> name)
    {
        if (!FsEncoding.TryDecodeUtf8(name, out var decoded))
            return -(int)Errno.EINVAL;
        return Unlink(decoded);
    }

    public virtual int Unlink(FsName name)
    {
        return Unlink(name.Bytes);
    }

    public virtual int Unlink(string name)
    {
        return -(int)Errno.EOPNOTSUPP;
    }

    public virtual int Rmdir(ReadOnlySpan<byte> name)
    {
        if (!FsEncoding.TryDecodeUtf8(name, out var decoded))
            return -(int)Errno.EINVAL;
        return Rmdir(decoded);
    }

    public virtual int Rmdir(FsName name)
    {
        return Rmdir(name.Bytes);
    }

    public virtual int Rmdir(string name)
    {
        return -(int)Errno.EOPNOTSUPP;
    }

    public virtual int Link(Dentry dentry, Inode oldInode)
    {
        return -(int)Errno.EOPNOTSUPP;
    }

    public virtual int Rename(ReadOnlySpan<byte> oldName, Inode newParent, ReadOnlySpan<byte> newName)
    {
        if (!FsEncoding.TryDecodeUtf8(oldName, out var decodedOld) ||
            !FsEncoding.TryDecodeUtf8(newName, out var decodedNew))
            return -(int)Errno.EINVAL;
        return Rename(decodedOld, newParent, decodedNew);
    }

    public virtual int Rename(FsName oldName, Inode newParent, FsName newName)
    {
        return Rename(oldName.Bytes, newParent, newName.Bytes);
    }

    public virtual int Rename(string oldName, Inode newParent, string newName)
    {
        return -(int)Errno.EOPNOTSUPP;
    }

    public virtual int Symlink(Dentry dentry, byte[] target, int uid, int gid)
    {
        if (!FsEncoding.TryDecodeUtf8(target, out var decodedTarget))
            return -(int)Errno.EINVAL;
        return Symlink(dentry, decodedTarget, uid, gid);
    }

    public virtual int Symlink(Dentry dentry, string target, int uid, int gid)
    {
        return -(int)Errno.EOPNOTSUPP;
    }

    public virtual int Mknod(Dentry dentry, int mode, int uid, int gid, InodeType type, uint rdev)
    {
        return -(int)Errno.EOPNOTSUPP;
    }

    public virtual int Readlink(out byte[]? target)
    {
        var rc = Readlink(out string? decodedTarget);
        if (rc < 0 || decodedTarget == null)
        {
            target = null;
            return rc;
        }

        target = FsEncoding.EncodeUtf8(decodedTarget);
        return 0;
    }

    public virtual int Readlink(out string? target)
    {
        target = null;
        return -(int)Errno.EINVAL;
    }

    public int ReadToHost(FiberTask? task, LinuxFile file, Span<byte> buffer, long offset = -1)
    {
        return ReadSpan(task, file, buffer, offset);
    }

    public int WriteFromHost(FiberTask? task, LinuxFile file, ReadOnlySpan<byte> buffer, long offset = -1)
    {
        return WriteSpan(task, file, buffer, offset);
    }

    protected internal virtual int ReadSpan(FiberTask? task, LinuxFile linuxFile, Span<byte> buffer, long offset)
    {
        return ReadSpan(linuxFile, buffer, offset);
    }

    protected internal virtual int ReadSpan(LinuxFile linuxFile, Span<byte> buffer, long offset)
    {
        return 0;
    }

    protected internal virtual int WriteSpan(FiberTask? task, LinuxFile linuxFile, ReadOnlySpan<byte> buffer,
        long offset)
    {
        var rc = WriteSpan(linuxFile, buffer, offset);
        if (rc > 0)
        {
            CTime = DateTime.Now;
            if (task?.Process != null)
            {
                var clearedMode = DacPolicy.ApplySetIdClearOnWrite(task.Process, this);
                if (clearedMode != Mode)
                    Mode = clearedMode;
            }
        }

        return rc;
    }

    protected internal virtual int WriteSpan(LinuxFile linuxFile, ReadOnlySpan<byte> buffer, long offset)
    {
        if (Type == InodeType.Directory) return -(int)Errno.EISDIR;
        return 0;
    }

    public virtual async ValueTask<int> ReadV(Engine engine, LinuxFile file, FiberTask? task, IReadOnlyList<Iovec> iovs,
        long offset, int flags)
    {
        var updatePosition = offset == -1;
        var currentOffset = updatePosition ? file.Position : offset;
        var totalRead = 0;

        for (var i = 0; i < iovs.Count; i++)
        {
            var iov = iovs[i];
            if (iov.Len == 0) continue;

            var iovProcessed = 0u;
            while (iovProcessed < iov.Len)
            {
                var currAddr = iov.BaseAddr + iovProcessed;
                var ptr = engine.GetPhysicalAddressSafe(currAddr, true); // true = We are writing TO guest memory
                if (ptr == IntPtr.Zero)
                    if (engine.PageFaultResolver != null && engine.PageFaultResolver(currAddr, true))
                        ptr = engine.GetPhysicalAddressSafe(currAddr, true);

                if (ptr == IntPtr.Zero) return totalRead > 0 ? totalRead : -(int)Errno.EFAULT;

                var pageOffset = currAddr & 0xFFF;
                var chunkLen = Math.Min(iov.Len - iovProcessed, 4096 - pageOffset);

                while (true)
                {
                    int n;
                    unsafe
                    {
                        var span = new Span<byte>((void*)ptr, (int)chunkLen);
                        n = ReadSpan(task, file, span, currentOffset);
                    }

                    if (n == -(int)Errno.EAGAIN)
                    {
                        if ((file.Flags & FileFlags.O_NONBLOCK) != 0 || (flags & 0x00000008) != 0 /* RWF_NOWAIT */)
                        {
                            if (updatePosition) file.Position = currentOffset;
                            return totalRead > 0 ? totalRead : -(int)Errno.EAGAIN;
                        }

                        if (task == null)
                        {
                            if (updatePosition) file.Position = currentOffset;
                            return totalRead > 0 ? totalRead : -(int)Errno.EAGAIN;
                        }

                        if (await WaitForRead(file, task) == AwaitResult.Interrupted)
                        {
                            if (updatePosition) file.Position = currentOffset;
                            return totalRead > 0 ? totalRead : -(int)Errno.ERESTARTSYS;
                        }

                        continue;
                    }

                    if (n > 0)
                    {
                        totalRead += n;
                        currentOffset += n;
                        iovProcessed += (uint)n;
                        if (n < chunkLen)
                        {
                            if (updatePosition) file.Position = currentOffset;
                            return totalRead;
                        }

                        break; // Processed this chunk
                    }

                    // EOF or real error (n <= 0)
                    if (updatePosition) file.Position = currentOffset;
                    return totalRead > 0 ? totalRead : n;
                }
            }
        }

        if (updatePosition) file.Position = currentOffset;
        return totalRead;
    }

    public virtual async ValueTask<int> WriteV(Engine engine, LinuxFile file, FiberTask? task,
        IReadOnlyList<Iovec> iovs, long offset, int flags)
    {
        var updatePosition = offset == -1;
        var append = (file.Flags & FileFlags.O_APPEND) != 0;
        var currentOffset = updatePosition
            ? append ? (long)Size : file.Position
            : offset;
        var sizeBeforeWrite = (long)Size;
        var totalWritten = 0;

        int FinalizeWriteResult(int rc)
        {
            if (updatePosition) file.Position = currentOffset;
            return rc;
        }

        for (var i = 0; i < iovs.Count; i++)
        {
            var iov = iovs[i];
            if (iov.Len == 0) continue;

            var iovProcessed = 0u;
            while (iovProcessed < iov.Len)
            {
                var currAddr = iov.BaseAddr + iovProcessed;
                var ptr = engine.GetPhysicalAddressSafe(currAddr, false); // false = We are reading FROM guest memory
                if (ptr == IntPtr.Zero)
                    if (engine.PageFaultResolver != null && engine.PageFaultResolver(currAddr, false))
                        ptr = engine.GetPhysicalAddressSafe(currAddr, false);

                if (ptr == IntPtr.Zero)
                    return FinalizeWriteResult(totalWritten > 0 ? totalWritten : -(int)Errno.EFAULT);

                var pageOffset = currAddr & 0xFFF;
                var chunkLen = Math.Min(iov.Len - iovProcessed, 4096 - pageOffset);

                while (true)
                {
                    int n;
                    unsafe
                    {
                        var span = new ReadOnlySpan<byte>((void*)ptr, (int)chunkLen);
                        n = WriteSpan(task, file, span, currentOffset);
                    }

                    if (n == -(int)Errno.EPIPE)
                    {
                        task?.PostSignal((int)Signal.SIGPIPE);
                        return FinalizeWriteResult(n);
                    }

                    if (n == -(int)Errno.EAGAIN)
                    {
                        if ((file.Flags & FileFlags.O_NONBLOCK) != 0 || (flags & 0x00000008) != 0 /* RWF_NOWAIT */)
                            return FinalizeWriteResult(totalWritten > 0 ? totalWritten : -(int)Errno.EAGAIN);

                        if (task == null)
                            return FinalizeWriteResult(totalWritten > 0 ? totalWritten : -(int)Errno.EAGAIN);

                        // Linux pipe(7): blocking writes up to PIPE_BUF stay atomic, so a pipe
                        // writer may need to wait for the whole chunk to fit even though poll(2)
                        // would already report POLLOUT once any space is available.
                        var minWritableBytes = chunkLen <= PipeInode.PipeBuf ? (int)chunkLen : 1;
                        if (await WaitForWrite(file, task, minWritableBytes) == AwaitResult.Interrupted)
                            return FinalizeWriteResult(totalWritten > 0 ? totalWritten : -(int)Errno.ERESTARTSYS);
                        continue;
                    }

                    if (n > 0)
                    {
                        totalWritten += n;
                        currentOffset += n;
                        iovProcessed += (uint)n;
                        if (n < chunkLen) return FinalizeWriteResult(totalWritten);
                        break; // Processed this chunk
                    }

                    return FinalizeWriteResult(totalWritten > 0 ? totalWritten : n);
                }
            }
        }

        return FinalizeWriteResult(totalWritten);
    }

    /// <summary>
    ///     Returns an enumerator over the readable byte segments in [<paramref name="offset" />, <paramref name="offset" />+
    ///     <paramref name="length" />).
    ///     Use with <c>foreach</c>; check <see cref="ReadableSegmentEnumerator.Succeeded" /> after the loop.
    /// </summary>
    public virtual ReadableSegmentEnumerator GetReadableSegments(LinuxFile? linuxFile, long offset, int length)
    {
        if (offset < 0 || length < 0 || Type == InodeType.Directory)
            return ReadableSegmentEnumerator.Failed;
        if (length == 0)
            return ReadableSegmentEnumerator.Empty;

        if (this is not MappingBackedInode mappingBacked)
            return ReadableSegmentEnumerator.Failed;

        var fileSize = (long)Size;
        if (offset > fileSize || length > fileSize - offset)
            return ReadableSegmentEnumerator.Failed;

        return new ReadableSegmentEnumerator(mappingBacked, linuxFile, Mapping, offset, length, fileSize);
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

    protected virtual int AopsWritePage(LinuxFile? linuxFile, PageIoRequest request, ReadOnlySpan<byte> pageBuffer,
        bool sync)
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

    /// <summary>
    ///     Optional fast path: allow filesystem to provide an externally-owned mapped page.
    ///     Return false to use regular in-memory page cache allocation + ReadPage fallback.
    /// </summary>
    public virtual bool TryAcquireMappedPageHandle(LinuxFile? linuxFile, long pageIndex, long absoluteFileOffset,
        bool writable, out BackingPageHandle backingPageHandle)
    {
        _ = writable;
        backingPageHandle = default;
        return false;
    }

    protected internal virtual void ReleaseMappedPageHandle(long releaseToken)
    {
        _ = releaseToken;
    }

    void IBackingPageHandleReleaseOwner.ReleaseBackingPageHandle(IntPtr pointer, long releaseToken)
    {
        _ = pointer;
        ReleaseMappedPageHandle(releaseToken);
    }

    public virtual bool TryFlushMappedPage(LinuxFile? linuxFile, long pageIndex)
    {
        _ = linuxFile;
        _ = pageIndex;
        return false;
    }

    // Async blocking support
    public virtual ValueTask<AwaitResult> WaitForRead(LinuxFile linuxFile, FiberTask task)
    {
        _ = task;
        return ValueTask.FromResult(AwaitResult.Completed);
    }

    public virtual ValueTask<AwaitResult> WaitForWrite(LinuxFile linuxFile, FiberTask task,
        int minWritableBytes = 1)
    {
        _ = minWritableBytes;
        _ = task;
        return ValueTask.FromResult(AwaitResult.Completed);
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
    public virtual int Ioctl(LinuxFile linuxFile, FiberTask task, uint request, uint arg)
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

    public virtual int SetXAttr(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value, int flags)
    {
        if (!FsEncoding.TryDecodeUtf8(name, out var decoded))
            return -(int)Errno.EINVAL;
        return SetXAttr(decoded, value, flags);
    }

    public virtual int SetXAttr(string name, ReadOnlySpan<byte> value, int flags)
    {
        return -(int)Errno.EOPNOTSUPP;
    }

    public virtual int GetXAttr(ReadOnlySpan<byte> name, Span<byte> value)
    {
        if (!FsEncoding.TryDecodeUtf8(name, out var decoded))
            return -(int)Errno.EINVAL;
        return GetXAttr(decoded, value);
    }

    public virtual int GetXAttr(string name, Span<byte> value)
    {
        return -(int)Errno.EOPNOTSUPP;
    }

    public virtual int ListXAttr(Span<byte> list)
    {
        return -(int)Errno.EOPNOTSUPP;
    }

    public virtual int RemoveXAttr(ReadOnlySpan<byte> name)
    {
        if (!FsEncoding.TryDecodeUtf8(name, out var decoded))
            return -(int)Errno.EINVAL;
        return RemoveXAttr(decoded);
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

    protected sealed class NoopWaitRegistration : IDisposable
    {
        public static readonly NoopWaitRegistration Instance = new();

        public void Dispose()
        {
        }
    }

    protected delegate int ReadBackendDelegate(LinuxFile? linuxFile, Span<byte> buffer, long offset);

    protected delegate int WriteBackendDelegate(LinuxFile? linuxFile, ReadOnlySpan<byte> buffer, long offset);
#if VFS_FILE_HOLDER_TRACKING
    private readonly object _activeFileHoldersSync = new();
    private readonly Dictionary<LinuxFile, string> _activeFileHolders = [];
#endif
}

public abstract class MappingBackedInode : Inode
{
    private readonly Lock _mappingLock = new();
    private readonly Lock _mappingPageLock = new();
    private readonly Dictionary<uint, InodePageRecord> _mappingPages = [];

    protected virtual AddressSpaceKind MappingKind => AddressSpaceKind.File;

    protected virtual AddressSpacePolicy.AddressSpaceCacheClass? MappingCacheClass => MappingKind switch
    {
        AddressSpaceKind.File => AddressSpacePolicy.AddressSpaceCacheClass.File,
        AddressSpaceKind.Shmem => AddressSpacePolicy.AddressSpaceCacheClass.Shmem,
        _ => null
    };

    public override int WritePage(LinuxFile? linuxFile, PageIoRequest request, ReadOnlySpan<byte> pageBuffer,
        bool sync)
    {
        if (sync && Mapping is { } mapping &&
            TryCreatePageSyncRequest(request, out var syncRequest) &&
            mapping.IsDirty(syncRequest.PageIndex))
            return SyncCachedPage(linuxFile, mapping, syncRequest);

        return base.WritePage(linuxFile, request, pageBuffer, sync);
    }

    public override int WritePages(LinuxFile? linuxFile, WritePagesRequest request)
    {
        if (request.Sync && Mapping is { } mapping)
            return SyncCachedPages(linuxFile, mapping, request);

        return base.WritePages(linuxFile, request);
    }

    public override int InvalidateFolio(long pageIndex)
    {
        Mapping?.RemovePagesInRange((uint)pageIndex, (uint)pageIndex + 1);
        return 0;
    }

    public override int ReleaseFolio(long pageIndex)
    {
        Mapping?.RemovePagesInRange((uint)pageIndex, (uint)pageIndex + 1, static page => !page.Dirty);
        return 0;
    }

    internal virtual bool PreferHostMappedMappingPage(PageCacheAccessMode accessMode)
    {
        return accessMode == PageCacheAccessMode.Read;
    }

    internal virtual InodePageRecord? TryCreateIntrinsicMappingPage(uint pageIndex)
    {
        _ = pageIndex;
        return null;
    }

    internal AddressSpace AcquireMappingRef()
    {
        lock (_mappingLock)
        {
            var mapping = Mapping ?? CreateMapping();
            mapping.AddRef();
            return mapping;
        }
    }

    protected AddressSpace CreateMapping()
    {
        var mapping = new AddressSpace(SuperBlock.MemoryContext, MappingKind);
        if (MappingCacheClass is { } cacheClass)
            SuperBlock.MemoryContext.AddressSpacePolicy.TrackAddressSpace(mapping, cacheClass);

        Mapping = mapping;
        return mapping;
    }

    protected AddressSpace EnsureMapping()
    {
        if (Mapping != null)
            return Mapping;

        var mapping = AcquireMappingRef();
        mapping.Release();
        return mapping;
    }

    private void ReleaseMappingOwnerRef()
    {
        AddressSpace? mapping;
        lock (_mappingLock)
        {
            mapping = Mapping;
            Mapping = null;
        }

        mapping?.Release();
    }

    internal virtual void OnMappingPageReleased(uint pageIndex, InodePageRecord record)
    {
    }

    internal virtual int SyncCachedPage(LinuxFile? linuxFile, AddressSpace mapping,
        PageSyncRequest request)
    {
        if (request.Length < 0)
            return -(int)Errno.EINVAL;
        if (!mapping.IsDirty(request.PageIndex))
            return 0;

        var pagePtr = mapping.PeekPage(request.PageIndex);
        if (pagePtr == IntPtr.Zero || request.Length == 0)
        {
            CompleteCachedPageSync(linuxFile, mapping, request.PageIndex, null);
            return 0;
        }

        if (TryGetMappingPageRecord(request.PageIndex, out var record) &&
            record.BackingKind == FilePageBackingKind.HostMappedWindow)
        {
            SuperBlock.MemoryContext.AddressSpacePolicy.BeginAddressSpaceWriteback();
            try
            {
                var flushRc = SyncMappedPage(linuxFile, mapping, request, record);
                if (flushRc < 0)
                    return flushRc;
            }
            finally
            {
                SuperBlock.MemoryContext.AddressSpacePolicy.EndAddressSpaceWriteback();
            }

            CompleteCachedPageSync(linuxFile, mapping, request.PageIndex, record);
            return 0;
        }

        unsafe
        {
            ReadOnlySpan<byte> pageBuffer = new((void*)pagePtr, LinuxConstants.PageSize);
            var rc = AopsWritePage(linuxFile, new PageIoRequest(request.PageIndex, request.FileOffset, request.Length),
                pageBuffer, true);
            if (rc < 0)
                return rc;
        }

        CompleteCachedPageSync(linuxFile, mapping, request.PageIndex, null);
        return 0;
    }

    internal virtual int SyncCachedPages(LinuxFile? linuxFile, AddressSpace mapping,
        WritePagesRequest request)
    {
        if (!request.Sync)
            return 0;

        var pageStates = mapping.SnapshotPageStates()
            .Where(state => state.Dirty && state.PageIndex >= request.StartPageIndex &&
                            state.PageIndex <= request.EndPageIndex)
            .OrderBy(state => state.PageIndex)
            .ToList();

        foreach (var state in pageStates)
        {
            var syncRequest = CreatePageSyncRequest(state.PageIndex);
            var rc = SyncCachedPage(linuxFile, mapping, syncRequest);
            if (rc < 0)
                return rc;
        }

        return 0;
    }

    internal void ReleaseInstalledMappingPage(InodePageRecord record)
    {
        var pageIndex = record.PageIndex;
        lock (_mappingPageLock)
        {
            if (_mappingPages.TryGetValue(pageIndex, out var existing) && ReferenceEquals(existing, record))
                _mappingPages.Remove(pageIndex);
        }

        OnMappingPageReleased(pageIndex, record);
        record.ReleaseOwnership();
    }

    internal bool TryGetMappingPageRecord(uint pageIndex, out InodePageRecord record)
    {
        lock (_mappingPageLock)
        {
            return _mappingPages.TryGetValue(pageIndex, out record!);
        }
    }

    private bool TryRegisterMappingPage(uint pageIndex, InodePageRecord record)
    {
        lock (_mappingPageLock)
        {
            if (_mappingPages.ContainsKey(pageIndex))
                return false;

            _mappingPages.Add(pageIndex, record);
            return true;
        }
    }

    protected virtual bool TryPopulateMappingPage(LinuxFile? linuxFile, uint pageIndex, long fileOffset,
        int prefillLength, Span<byte> pageBuffer)
    {
        pageBuffer.Clear();
        if (prefillLength <= 0 || MappingKind != AddressSpaceKind.File)
            return true;

        var rc = ReadPage(linuxFile, new PageIoRequest(pageIndex, fileOffset, prefillLength), pageBuffer);
        return rc >= 0;
    }

    private InodePageRecord CreateAllocatedMappingPage(LinuxFile? linuxFile, uint pageIndex, long fileOffset,
        int prefillLength)
    {
        if (!SuperBlock.MemoryContext.BackingPagePool.TryAllocatePoolBackedPageStrict(out var pageHandle) ||
            !pageHandle.IsValid)
            return null!;

        var ptr = pageHandle.Pointer;
        try
        {
            unsafe
            {
                var target = new Span<byte>((void*)ptr, LinuxConstants.PageSize);
                if (!TryPopulateMappingPage(linuxFile, pageIndex, fileOffset, prefillLength, target))
                {
                    BackingPageHandle.Release(ref pageHandle);
                    return null!;
                }
            }

            var record = new InodePageRecord
            {
                PageIndex = pageIndex,
                Ptr = ptr,
                BackingKind = FilePageBackingKind.AllocatedPageCache,
                Handle = pageHandle
            };
            return record;
        }
        catch
        {
            BackingPageHandle.Release(ref pageHandle);
            throw;
        }
    }

    private InodePageRecord? TryCreateHostMappedMappingPage(LinuxFile? linuxFile, uint pageIndex, long fileOffset,
        bool writable)
    {
        if (!TryAcquireMappedPageHandle(linuxFile, pageIndex, fileOffset, writable, out var pageHandle) ||
            !pageHandle.IsValid)
            return null;

        if (pageHandle.Pointer == IntPtr.Zero)
        {
            BackingPageHandle.Release(ref pageHandle);
            return null;
        }
        var record = new InodePageRecord
        {
            PageIndex = pageIndex,
            Ptr = pageHandle.Pointer,
            BackingKind = FilePageBackingKind.HostMappedWindow,
            Handle = pageHandle
        };
        return record;
    }

    internal IntPtr AcquireMappingPage(LinuxFile? linuxFile, uint pageIndex, long fileOffset,
        PageCacheAccessMode accessMode, int prefillLength, bool allowHostMapped = true)
    {
        var mapping = EnsureMapping();
        var pagePtr = mapping.GetPage(pageIndex);
        if (pagePtr != IntPtr.Zero)
            return pagePtr;

        if (TryGetMappingPageRecord(pageIndex, out var resident))
        {
            var installed = mapping.InstallHostPageIfAbsent(pageIndex, resident.Ptr, ref resident.Handle,
                resident.HostPageKind, this, resident, out _);
            return installed;
        }

        var writable = accessMode == PageCacheAccessMode.Write;
        var record = TryCreateIntrinsicMappingPage(pageIndex);
        if (record == null && allowHostMapped && mapping.Kind == AddressSpaceKind.File &&
            PreferHostMappedMappingPage(accessMode))
            record = TryCreateHostMappedMappingPage(linuxFile, pageIndex, fileOffset, writable);
        record ??= CreateAllocatedMappingPage(linuxFile, pageIndex, fileOffset, prefillLength);
        if (record == null)
            return IntPtr.Zero;

        if (!TryRegisterMappingPage(pageIndex, record))
        {
            record.ReleaseOwnership();
            if (!TryGetMappingPageRecord(pageIndex, out resident))
                return mapping.PeekPage(pageIndex);

            var installed = mapping.InstallHostPageIfAbsent(pageIndex, resident.Ptr, ref resident.Handle,
                resident.HostPageKind, this, resident, out _);
            return installed;
        }

        var finalPtr = mapping.InstallHostPageIfAbsent(pageIndex, record.Ptr, ref record.Handle, record.HostPageKind,
            this, record, out var inserted);
        if (!inserted && finalPtr != record.Ptr)
        {
            lock (_mappingPageLock)
            {
                if (_mappingPages.TryGetValue(pageIndex, out var existing) && ReferenceEquals(existing, record))
                    _mappingPages.Remove(pageIndex);
            }

            record.ReleaseOwnership();
        }

        return finalPtr;
    }

    internal virtual int SyncMappedPage(LinuxFile? linuxFile, AddressSpace mapping,
        PageSyncRequest request, InodePageRecord record)
    {
        _ = mapping;
        _ = record;
        return TryFlushMappedPage(linuxFile, request.PageIndex) ? 0 : -(int)Errno.EOPNOTSUPP;
    }

    internal virtual void CompleteCachedPageSync(LinuxFile? linuxFile, AddressSpace mapping, uint pageIndex,
        InodePageRecord? record)
    {
        _ = linuxFile;
        _ = record;
        mapping.ClearDirty(pageIndex);
    }

    protected int ReadWithPageCache(
        LinuxFile? linuxFile,
        Span<byte> buffer,
        long offset,
        ReadBackendDelegate backendRead)
    {
        if (buffer.Length == 0) return 0;
        var pageCache = Mapping;
        if (pageCache == null) return backendRead(linuxFile, buffer, offset);
        if (offset < 0) return -(int)Errno.EINVAL;

        var total = 0;
        var cursor = offset;
        var fileSize = (long)Size;
        while (total < buffer.Length)
        {
            var fileRemaining = fileSize - cursor;
            if (fileRemaining <= 0) break;

            var pageIndex = (uint)(cursor / LinuxConstants.PageSize);
            var pageOffset = (int)(cursor & LinuxConstants.PageOffsetMask);
            var toCopy = (int)Math.Min(Math.Min(buffer.Length - total, LinuxConstants.PageSize - pageOffset),
                fileRemaining);
            if (toCopy <= 0) break;

            var (pagePtr, tempFallback) = LoadPageIntoCacheWithFallback(pageCache, linuxFile, pageIndex, fileSize);
            if (pagePtr == IntPtr.Zero && tempFallback == null)
                return total > 0 ? total : -(int)Errno.EIO;

            if (tempFallback != null)
                tempFallback.AsSpan(pageOffset, toCopy).CopyTo(buffer[total..]);
            else
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
        var pageCache = EnsureMapping();
        if (linuxFile == null) return backendWrite(linuxFile, buffer, offset);
        if (offset < 0) return -(int)Errno.EINVAL;

        var consumed = 0;
        var cursor = offset;
        while (consumed < buffer.Length)
        {
            var pageIndex = (uint)(cursor / LinuxConstants.PageSize);
            var pageOffset = (int)(cursor & LinuxConstants.PageOffsetMask);
            var chunk = Math.Min(buffer.Length - consumed, LinuxConstants.PageSize - pageOffset);

            var pagePtr = EnsurePageInCacheForWrite(pageCache, linuxFile, pageIndex, pageOffset, chunk);
            if (pagePtr == IntPtr.Zero) return consumed > 0 ? consumed : -(int)Errno.ENOMEM;

            unsafe
            {
                fixed (byte* src = &buffer[consumed])
                {
                    Buffer.MemoryCopy(src, (byte*)pagePtr + pageOffset, chunk, chunk);
                }
            }

            var dirtyRc = SetPageDirty(pageIndex);
            if (dirtyRc < 0) return consumed > 0 ? consumed : dirtyRc;
            pageCache.MarkDirty(pageIndex);

            var writeRc = WritePage(linuxFile, new PageIoRequest(pageIndex, cursor, chunk),
                buffer.Slice(consumed, chunk), false);
            if (writeRc < 0) return consumed > 0 ? consumed : writeRc;

            consumed += chunk;
            cursor += chunk;
        }

        var end = offset + consumed;
        if (end > (long)Size) Size = (ulong)end;
        MTime = DateTime.Now;
        CTime = MTime;
        return consumed;
    }

    internal (IntPtr PagePtr, byte[]? TempFallback) LoadPageIntoCacheWithFallback(
        AddressSpace? pageCache, LinuxFile? linuxFile, uint pageIndex, long fileSize)
    {
        pageCache ??= EnsureMapping();
        if (pageCache != null)
        {
            var pagePtr = pageCache.GetPage(pageIndex);
            if (pagePtr != IntPtr.Zero) return (pagePtr, null);
        }

        if (linuxFile == null) return (IntPtr.Zero, null);

        var pageFileOffset = (long)pageIndex * LinuxConstants.PageSize;
        var pageReadLen = (int)Math.Min(LinuxConstants.PageSize, Math.Max(0, fileSize - pageFileOffset));

        if (pageCache?.Kind == AddressSpaceKind.File)
        {
            var mappedPage = AcquireMappingPage(linuxFile, pageIndex, pageFileOffset, PageCacheAccessMode.Read,
                pageReadLen);
            if (mappedPage != IntPtr.Zero)
                return (mappedPage, null);
        }

        var tempPage = new byte[LinuxConstants.PageSize];
        tempPage.AsSpan().Clear();
        if (pageReadLen > 0)
        {
            var rc = ReadPage(linuxFile, new PageIoRequest(pageIndex, pageFileOffset, pageReadLen), tempPage);
            if (rc < 0)
                return (IntPtr.Zero, null);
        }

        if (pageCache == null)
            return (IntPtr.Zero, tempPage);

        if (pageCache.Kind is AddressSpaceKind.File or AddressSpaceKind.Shmem or AddressSpaceKind.Zero)
            return (IntPtr.Zero, tempPage);

        var populatedPtr = pageCache.GetOrCreatePage(pageIndex, p =>
        {
            unsafe
            {
                var dst = new Span<byte>((void*)p, LinuxConstants.PageSize);
                dst.Clear();
                if (pageReadLen > 0) tempPage.AsSpan(0, pageReadLen).CopyTo(dst);
            }

            return true;
        }, out _, true, AllocationClass.PageCache);

        return populatedPtr != IntPtr.Zero ? (populatedPtr, null) : (IntPtr.Zero, tempPage);
    }

    protected override void OnEvictCache()
    {
        ReleaseMappingOwnerRef();
        base.OnEvictCache();
    }

    private bool TryCreatePageSyncRequest(PageIoRequest request, out PageSyncRequest syncRequest)
    {
        if (request.PageIndex < 0 || request.PageIndex > uint.MaxValue)
        {
            syncRequest = default;
            return false;
        }

        syncRequest = new PageSyncRequest((uint)request.PageIndex, request.FileOffset, request.Length);
        return true;
    }

    private PageSyncRequest CreatePageSyncRequest(uint pageIndex)
    {
        var fileOffset = (long)pageIndex * LinuxConstants.PageSize;
        var remaining = Math.Max(0, (long)Size - fileOffset);
        var length = (int)Math.Min(LinuxConstants.PageSize, remaining);
        return new PageSyncRequest(pageIndex, fileOffset, length);
    }

    private IntPtr EnsurePageInCacheForWrite(
        AddressSpace pageCache, LinuxFile linuxFile, uint pageIndex, int pageOffset, int chunk)
    {
        var pagePtr = pageCache.GetPage(pageIndex);
        if (pagePtr != IntPtr.Zero) return pagePtr;

        var pageFileOffset = (long)pageIndex * LinuxConstants.PageSize;
        var pageReadLen = 0;
        byte[]? tempPage = null;

        var fullPageWrite = pageOffset == 0 && chunk == LinuxConstants.PageSize;
        if (!fullPageWrite && pageFileOffset < (long)Size)
        {
            tempPage = new byte[LinuxConstants.PageSize];
            tempPage.AsSpan().Clear();
            pageReadLen = (int)Math.Min(LinuxConstants.PageSize, (long)Size - pageFileOffset);
            var rc = ReadPage(linuxFile, new PageIoRequest(pageIndex, pageFileOffset, pageReadLen), tempPage);
            if (rc < 0)
                return IntPtr.Zero;
        }

        if (pageCache.Kind is AddressSpaceKind.File or AddressSpaceKind.Shmem or AddressSpaceKind.Zero)
        {
            var writeEndOffset = pageFileOffset + pageOffset + chunk;
            var allowHostMapped = writeEndOffset <= (long)Size;
            return AcquireMappingPage(linuxFile, pageIndex, pageFileOffset, PageCacheAccessMode.Write, pageReadLen,
                allowHostMapped);
        }

        var captured = tempPage;
        var capturedReadLen = pageReadLen;
        return pageCache.GetOrCreatePage(pageIndex, p =>
        {
            unsafe
            {
                var dst = new Span<byte>((void*)p, LinuxConstants.PageSize);
                dst.Clear();
                if (capturedReadLen > 0) captured!.AsSpan(0, capturedReadLen).CopyTo(dst);
            }

            return true;
        }, out _, true, AllocationClass.PageCache);
    }
}

public struct DirectoryEntry
{
    public FsName Name;
    public ulong Ino;
    public InodeType Type;
}

public interface IMagicSymlinkInode
{
    bool TryResolveLink(out PathLocation path);
}

public interface IContextualMagicSymlinkInode
{
    bool TryResolveLink(FiberTask task, out PathLocation path);
}

public interface IContextualSymlinkInode
{
    byte[] Readlink(FiberTask task);
}

public interface IContextualDirectoryInode
{
    Dentry? Lookup(FiberTask task, ReadOnlySpan<byte> name);
    bool RevalidateCachedChild(FiberTask task, Dentry parent, ReadOnlySpan<byte> name, Dentry cached);
    List<DirectoryEntry> GetEntries(FiberTask task);
}

public interface ITaskContextBoundInode
{
    void BindTaskContext(LinuxFile linuxFile, FiberTask task);
}

/// <summary>
///     A zero-GC enumerator over the readable byte segments of an <see cref="Inode" /> region.
///     Returned by <see cref="Inode.GetReadableSegments" />; use with <c>foreach</c>.
///     <para>
///         Check <see cref="Succeeded" /> after the loop to distinguish a clean EOF from an I/O error.
///     </para>
/// </summary>
/// <example>
///     <code>
/// var segments = inode.GetReadableSegments(file, offset, length);
/// foreach (ReadOnlySpan&lt;byte&gt; chunk in segments)
///     sink.Write(chunk);
/// if (!segments.Succeeded) return false;
/// </code>
/// </example>
public ref struct ReadableSegmentEnumerator
{
    // Sentinel factory methods — ref structs cannot have static fields of their own type.
    internal static ReadableSegmentEnumerator Empty => new() { _remaining = 0, Succeeded = true };
    internal static ReadableSegmentEnumerator Failed => new() { _remaining = 0, Succeeded = false };

    private readonly MappingBackedInode? _inode;
    private readonly LinuxFile? _linuxFile;
    private readonly AddressSpace? _pageCache;
    private readonly long _fileSize;

    private long _absolute;
    private int _remaining;

    internal ReadableSegmentEnumerator(
        MappingBackedInode inode, LinuxFile? linuxFile, AddressSpace? pageCache,
        long offset, int length, long fileSize)
    {
        _inode = inode;
        _linuxFile = linuxFile;
        _pageCache = pageCache;
        _absolute = offset;
        _remaining = length;
        _fileSize = fileSize;
        Succeeded = true;
        Current = default;
    }

    /// <summary>The current byte segment. Valid only inside a <c>foreach</c> body.</summary>
    public ReadOnlySpan<byte> Current { get; private set; }

    public bool Succeeded { get; private set; }

    /// <summary>Number of bytes successfully yielded so far.</summary>
    private int TotalBytes { get; set; }

    /// <summary>Enables <c>foreach</c> without boxing — returns <c>this</c>.</summary>
    public readonly ReadableSegmentEnumerator GetEnumerator()
    {
        return this;
    }

    public bool MoveNext()
    {
        if (_remaining <= 0 || !Succeeded)
            return false;

        var pageIndex = (uint)(_absolute / LinuxConstants.PageSize);
        var pageOffset = (int)(_absolute & LinuxConstants.PageOffsetMask);
        var chunk = Math.Min(_remaining, LinuxConstants.PageSize - pageOffset);

        var (pagePtr, tempFallback) =
            _inode!.LoadPageIntoCacheWithFallback(_pageCache, _linuxFile, pageIndex, _fileSize);
        if (pagePtr == IntPtr.Zero && tempFallback == null)
        {
            Succeeded = false;
            return false;
        }

        if (tempFallback != null)
            Current = tempFallback.AsSpan(pageOffset, chunk);
        else
            unsafe
            {
                Current = new ReadOnlySpan<byte>((byte*)pagePtr + pageOffset, chunk);
            }

        _absolute += chunk;
        _remaining -= chunk;
        TotalBytes += chunk;
        return true;
    }
}

public class Dentry
{
    private static long _nextId;
    private bool _isMounted;

    public Dentry(FsName name, Inode? inode, Dentry? parent, SuperBlock sb)
    {
        Name = name;
        Parent = parent;
        SuperBlock = sb;
        IsOnLru = true;
        IsNegative = inode == null;
        EnsureTrackedBySuperBlock("Dentry.ctor");
        // Root dentry is always considered hashed in this model.
        if (parent == null)
            SetHashedState(true);
        if (inode != null) BindInode(inode, "Dentry.ctor");
    }

    public long Id { get; } = Interlocked.Increment(ref _nextId);

    public FsName Name { get; set; }
    public Inode? Inode { get; private set; }
    public Dentry? Parent { get; set; }
    public SuperBlock SuperBlock { get; set; }
    public FsNameMap<Dentry> Children { get; } = new();
    public int DentryRefCount { get; private set; }
    public bool IsOnLru { get; private set; }
    public bool IsTrackedBySuperBlock { get; private set; }
    public bool IsHashed { get; private set; }
    public bool IsNegative { get; private set; }

    // Mount point support
    public bool IsMounted
    {
        get => _isMounted;
        set
        {
            if (_isMounted == value) return;
            _isMounted = value;
            if (_isMounted)
                AssertMountedCacheInvariant("Dentry.IsMounted.set-true");
        }
    }

    public void Get(string? reason = null)
    {
        EnsureTrackedBySuperBlock("Dentry.Get");
        var before = DentryRefCount;
        if (before < 0)
        {
            VfsDebugTrace.FailInvariant(
                $"Dentry.Get invalid dentry={Name.ToDebugString()} dentryId={Id} ref={before} reason={reason}");
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
                $"Dentry.Put underflow dentry={Name.ToDebugString()} dentryId={Id} ref={before} reason={reason}");
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

    public bool TryGetCachedChild(ReadOnlySpan<byte> name, out Dentry cached)
    {
        if (Children.TryGetValue(name, out var found))
        {
            cached = found;
            return true;
        }

        cached = null!;
        return false;
    }

    public bool TryGetCachedChild(FsName name, out Dentry cached)
    {
        return TryGetCachedChild(name.Bytes, out cached);
    }

    public bool TryGetCachedChild(string name, out Dentry cached)
    {
        return TryGetCachedChild(FsEncoding.EncodeUtf8(name), out cached);
    }

    public void CacheChild(Dentry child, string reason)
    {
        child.EnsureTrackedBySuperBlock(reason, "child-track");
        child.Parent = this;
        if (Children.TryGetValue(child.Name, out var replaced) && !ReferenceEquals(replaced, child))
        {
            if (replaced.IsMounted)
            {
                VfsDebugTrace.FailInvariant(
                    $"Dentry.CacheChild replacing mounted child parent={Name.ToDebugString()} child={child.Name.ToDebugString()} reason={reason}");
                return;
            }

            replaced.SetHashedState(false);
        }

        Children.Set(child.Name, child);
        child.SetHashedState(true);
        child.AssertMountedCacheInvariant(reason, "cache-child");
        VfsDebugTrace.RecordDentryCacheUpdate(this, child, "cache-add", reason);
    }

    public bool TryUncacheChild(ReadOnlySpan<byte> name, string reason, out Dentry? removed)
    {
        if (!Children.TryGetValue(name, out var target))
        {
            removed = null;
            return false;
        }

        if (target.IsMounted)
        {
            VfsDebugTrace.FailInvariant(
                $"Dentry.TryUncacheChild mounted child parent={Name.ToDebugString()} child={FsEncoding.DecodeUtf8Lossy(name)} reason={reason}");
            removed = null;
            return false;
        }

        if (!Children.Remove(name, out removed))
            return false;

        if (removed is null)
        {
            VfsDebugTrace.FailInvariant(
                $"Dentry.TryUncacheChild removed null child parent={Name.ToDebugString()} child={FsEncoding.DecodeUtf8Lossy(name)} reason={reason}");
            return false;
        }

        removed.SetHashedState(false);
        VfsDebugTrace.RecordDentryCacheUpdate(this, removed, "cache-remove", reason);
        return true;
    }

    public bool TryUncacheChild(FsName name, string reason, out Dentry? removed)
    {
        return TryUncacheChild(name.Bytes, reason, out removed);
    }

    public bool TryUncacheChild(string name, string reason, out Dentry? removed)
    {
        return TryUncacheChild(FsEncoding.EncodeUtf8(name), reason, out removed);
    }

    public int PruneCachedChildren(Predicate<Dentry> shouldRemove, string reason)
    {
        var keys = Children
            .Where(kv => shouldRemove(kv.Value))
            .Select(kv => kv.Key)
            .ToList();

        var removedCount = 0;
        foreach (var key in keys)
        {
            if (!Children.TryGetValue(key, out var child))
                continue;

            if (child.IsMounted)
            {
                VfsDebugTrace.FailInvariant(
                    $"Dentry.PruneCachedChildren mounted child parent={Name.ToDebugString()} child={child.Name.ToDebugString()} reason={reason}");
                continue;
            }

            if (!Children.Remove(key, out _))
                continue;

            child.SetHashedState(false);
            VfsDebugTrace.RecordDentryCacheUpdate(this, child, "cache-clear", reason);
            removedCount++;
        }

        return removedCount;
    }

    public void BindInode(Inode inode, string reason)
    {
        if (Inode != null) throw new InvalidOperationException("Dentry already bound");
        if (!ReferenceEquals(inode.SuperBlock, SuperBlock))
        {
            VfsDebugTrace.FailInvariant(
                $"Dentry.BindInode superblock mismatch dentry={Name.ToDebugString()} dentrySb={SuperBlock.Type?.Name} inodeSb={inode.SuperBlock?.Type?.Name} reason={reason}");
            return;
        }

        EnsureTrackedBySuperBlock("Dentry.BindInode");
        SuperBlock.EnsureInodeTracked(inode);
        Inode = inode;
        IsNegative = false;
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
        IsNegative = true;
        if (detached)
            inode.ReleaseRef(InodeRefKind.KernelInternal, reason);
        return detached;
    }

    internal void EnsureTrackedBySuperBlock(string reason, string? phase = null)
    {
        if (IsTrackedBySuperBlock) return;
        SuperBlock.RegisterDentry(this);
        if (!IsTrackedBySuperBlock)
            VfsDebugTrace.FailInvariant(phase == null
                ? $"Dentry track failed dentry={Name.ToDebugString()} dentryId={Id} reason={reason}"
                : $"Dentry track failed dentry={Name.ToDebugString()} dentryId={Id} reason={reason} phase={phase}");
    }

    internal void UntrackFromSuperBlock(string reason)
    {
        if (!IsTrackedBySuperBlock) return;
        if (IsMounted)
        {
            VfsDebugTrace.FailInvariant(
                $"Dentry.UntrackFromSuperBlock mounted dentry={Name.ToDebugString()} dentryId={Id} reason={reason}");
            return;
        }

        SuperBlock.UnregisterDentry(this);
        if (IsTrackedBySuperBlock)
            VfsDebugTrace.FailInvariant($"Dentry untrack failed dentry={Name.ToDebugString()} dentryId={Id} reason={reason}");
    }

    internal void SetLruState(bool onLru)
    {
        IsOnLru = onLru;
    }

    internal void MarkTrackedBySuperBlock()
    {
        IsTrackedBySuperBlock = true;
    }

    internal void MarkUntrackedBySuperBlock()
    {
        if (IsMounted)
        {
            VfsDebugTrace.FailInvariant(
                $"Dentry.MarkUntrackedBySuperBlock mounted dentry={Name.ToDebugString()} dentryId={Id}");
            return;
        }

        IsTrackedBySuperBlock = false;
        SetHashedState(false);
    }

    private void SetHashedState(bool hashed)
    {
        if (!hashed && IsMounted)
        {
            VfsDebugTrace.FailInvariant(
                $"Dentry.SetHashedState unhash mounted dentry={Name.ToDebugString()} dentryId={Id}");
            return;
        }

        IsHashed = hashed;
        if (hashed)
            AssertMountedCacheInvariant("Dentry.SetHashedState");
    }

    private void AssertMountedCacheInvariant(string source, string? phase = null)
    {
        if (!IsMounted) return;
        if (!IsHashed)
        {
            VfsDebugTrace.FailInvariant(
                phase == null
                    ? $"Dentry mount invariant unhashed source={source} dentry={Name.ToDebugString()} dentryId={Id}"
                    : $"Dentry mount invariant unhashed source={source}/{phase} dentry={Name.ToDebugString()} dentryId={Id}");
            return;
        }

        var parent = Parent;
        if (parent == null || ReferenceEquals(parent, this))
        {
            if (Name.IsEmpty)
                return;
            VfsDebugTrace.FailInvariant(
                phase == null
                    ? $"Dentry mount invariant missing-parent source={source} dentry={Name.ToDebugString()} dentryId={Id}"
                    : $"Dentry mount invariant missing-parent source={source}/{phase} dentry={Name.ToDebugString()} dentryId={Id}");
            return;
        }

        if (!parent.TryGetCachedChild(Name, out var cached) || !ReferenceEquals(cached, this))
            VfsDebugTrace.FailInvariant(
                phase == null
                    ? $"Dentry mount invariant parent-cache-miss source={source} parent={parent.Name.ToDebugString()} child={Name.ToDebugString()} dentryId={Id}"
                    : $"Dentry mount invariant parent-cache-miss source={source}/{phase} parent={parent.Name.ToDebugString()} child={Name.ToDebugString()} dentryId={Id}");
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

public static partial class VfsDebugTrace
{
    private const int MaxTraceEntries = 4096;
    private static readonly ILogger Logger = Logging.CreateLogger("Fiberish.VFS.RefTrace");
    private static readonly Lock TraceLock = new();
    private static readonly Queue<InodeRefTrace> RefTraceQueue = [];

    public static bool CompilerEnabled =>
#if VFS_REFTRACE
        true;
#else
        false;
#endif

    public static bool Enabled { get; set; } = CompilerEnabled;

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

    [Conditional("VFS_REFTRACE")]
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
            LogRefChangeCore(Logger, operation, inode.Ino, inode.Type, before, after, inode.Dentries.Count,
                detail ?? string.Empty);
    }

    [Conditional("VFS_REFTRACE")]
    public static void RecordDentryBinding(Inode inode, Dentry dentry, string operation, string reason)
    {
        if (!Enabled) return;
        LogDentryBindingCore(Logger, operation, reason, inode.Ino, dentry.Name.ToDebugString(), dentry.Id,
            dentry.Parent != null ? dentry.Parent.Name.ToDebugString() : "<null>", inode.Dentries.Count);
    }

    [Conditional("VFS_REFTRACE")]
    public static void RecordDentryRefChange(Dentry dentry, string operation, int before, int after, string? reason)
    {
        if (!Enabled) return;
        LogDentryRefChangeCore(Logger, operation, dentry.Name, dentry.Id, before, after, dentry.IsOnLru,
            reason ?? string.Empty);
    }

    [Conditional("VFS_REFTRACE")]
    public static void RecordDentryCacheUpdate(Dentry parent, Dentry child, string operation, string reason)
    {
        if (!Enabled) return;
        LogDentryCacheUpdateCore(Logger, operation, reason, parent.Name, parent.Id, child.Name, child.Id);
    }

    [Conditional("VFS_REFTRACE")]
    public static void RecordStatNlink(Inode inode, string source, uint nlink)
    {
        if (!Enabled) return;
        LogStatNlinkCore(Logger, source, inode.Ino, inode.Type, nlink, inode.RefCount, inode.Dentries.Count);
    }

    [Conditional("VFS_REFTRACE")]
    public static void RecordLinkChange(Inode inode, string operation, int before, int after, string? reason)
    {
        if (!Enabled) return;
        LogLinkChangeCore(Logger, operation, inode.Ino, inode.Type, before, after, inode.RefCount,
            inode.Dentries.Count, reason ?? string.Empty);
    }

    [Conditional("VFS_REFTRACE")]
    public static void RecordCacheEvict(Inode inode, string operation, string? reason)
    {
        if (!Enabled) return;
        LogCacheEvictCore(Logger, operation, inode.Ino, inode.Type, inode.LinkCount, inode.RefCount,
            reason ?? string.Empty);
    }

    [Conditional("VFS_REFTRACE")]
    public static void RecordFinalize(Inode inode, string operation, string? reason)
    {
        if (!Enabled) return;
        LogFinalizeCore(Logger, operation, inode.Ino, inode.Type, inode.LinkCount, inode.RefCount,
            reason ?? string.Empty);
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

public class LinuxFile : IDisposable
{
    public enum ReferenceKind
    {
        Normal = 0,
        MmapHold = 1
    }
#if VFS_FILE_HOLDER_TRACKING
    private static int _nextDebugId;
#endif

    private int _refCount = 1;

    public LinuxFile(PathLocation livePath, FileFlags flags, ReferenceKind referenceKind = ReferenceKind.Normal,
        Inode? openedInode = null,
        [CallerMemberName] string callerMember = "",
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0)
    {
        Dentry = livePath.Dentry ?? throw new ArgumentNullException(nameof(livePath));
        OpenedInode = openedInode ?? Dentry.Inode;
        Flags = flags;
        Mount = livePath.Mount;
        Kind = referenceKind;
#if VFS_FILE_HOLDER_TRACKING
        DebugId = Interlocked.Increment(ref _nextDebugId);
        DebugOrigin = FormatDebugOrigin(callerMember, callerFile, callerLine);
#else
        DebugId = 0;
        DebugOrigin = string.Empty;
#endif
        Dentry.Get("LinuxFile.ctor");
        VfsFileHolderTracking.Register(OpenedInode, this, $"LinuxFile.ctor/{referenceKind}");
        var refKind = referenceKind == ReferenceKind.MmapHold ? InodeRefKind.FileMmap : InodeRefKind.FileOpen;
        OpenedInode?.AcquireRef(refKind, "LinuxFile.ctor");
        OpenedInode?.Open(this);
        // Note: Mount reference is managed by caller if provided
    }

    public LinuxFile(Dentry dentry, FileFlags flags, Mount? mount, ReferenceKind referenceKind = ReferenceKind.Normal,
        Inode? openedInode = null,
        [CallerMemberName] string callerMember = "",
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0)
        : this(new PathLocation(dentry, mount), flags, referenceKind, openedInode, callerMember, callerFile, callerLine)
    {
    }

    public PathLocation LivePath => new(Dentry, Mount);
    public Dentry Dentry { get; }
    public Inode? OpenedInode { get; }
    public int DebugId { get; }
    public string DebugOrigin { get; }
    public long Position { get; set; }
    public FileFlags Flags { get; set; }
    public Mount? Mount { get; protected set; }
    public object? PrivateData { get; set; }
    public bool IsTmpFile { get; set; }
    public ReferenceKind Kind { get; }

    public void Dispose()
    {
        Close();
    }

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
            try
            {
                Dentry.Parent.Inode.Unlink(Dentry.Name.Bytes);
            }
            catch
            {
                // Ignore cleanup errors
            }

        var dentry = Dentry;
        OpenedInode?.Release(this);
        var refKind = Kind == ReferenceKind.MmapHold ? InodeRefKind.FileMmap : InodeRefKind.FileOpen;
        OpenedInode?.ReleaseRef(refKind, "LinuxFile.Close");
        VfsFileHolderTracking.Unregister(OpenedInode, this);
        dentry?.Put("LinuxFile.Close");
        if (dentry != null && dentry.DentryRefCount == 0 && dentry.Inode == null && dentry.IsTrackedBySuperBlock)
            dentry.UntrackFromSuperBlock("LinuxFile.Close.unlinked-dentry");
        // Note: Mount reference is not released here as it's typically
        // managed by the filesystem/superblock lifecycle
    }

    public string GetDebugSummary()
    {
#if VFS_FILE_HOLDER_TRACKING
        return
            $"id={DebugId} path={GetBestEffortPath()} flags=0x{(uint)Flags:X} kind={Kind} origin={DebugOrigin}";
#else
        return $"path={GetBestEffortPath()} flags=0x{(uint)Flags:X} kind={Kind}";
#endif
    }

    private static string FormatDebugOrigin(string callerMember, string callerFile, int callerLine)
    {
        var fileName = string.IsNullOrEmpty(callerFile) ? "<unknown>" : Path.GetFileName(callerFile);
        return $"{fileName}:{callerLine} {callerMember}";
    }

    public string GetBestEffortPath()
    {
        var guestPath = TryBuildGuestPath(Dentry, Mount, out var builtGuestPath)
            ? builtGuestPath
            : Dentry != null ? Dentry.Name.ToDebugString() : "<unknown>";

        if (Dentry?.SuperBlock is HostSuperBlock hostSb && hostSb.TryGetPathForDentry(Dentry, out var hostPath))
            return $"{guestPath} (host:{hostPath})";

        if (OpenedInode is HostInode hostInode)
            return $"{guestPath} (host:{hostInode.HostPath})";

        return guestPath;
    }

    private static bool TryBuildGuestPath(Dentry? dentry, Mount? mount, out string path)
    {
        path = string.Empty;
        if (dentry == null)
            return false;

        var components = new List<string>();
        var currentDentry = dentry;
        var currentMount = mount;

        while (true)
        {
            if (currentMount != null && ReferenceEquals(currentDentry, currentMount.Root))
            {
                if (currentMount.Parent == null || currentMount.MountPoint == null)
                    break;

                currentDentry = currentMount.MountPoint;
                currentMount = currentMount.Parent;
                continue;
            }

            if (currentDentry.Parent == null || ReferenceEquals(currentDentry.Parent, currentDentry))
                break;

            if (!currentDentry.Name.IsEmpty)
                components.Add(currentDentry.Name.ToDebugString());

            currentDentry = currentDentry.Parent;
        }

        components.Reverse();
        path = components.Count == 0 ? "/" : "/" + string.Join("/", components);
        return true;
    }
}

public struct DCacheKey(ulong parentIno, FsName name) : IEquatable<DCacheKey>
{
    public ulong ParentIno = parentIno;
    public FsName Name = name;

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
