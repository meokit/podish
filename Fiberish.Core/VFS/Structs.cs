using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Fiberish.Auth.Permission;
using Fiberish.Core;
using Fiberish.Diagnostics;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using Process = Fiberish.Core.Process;

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

internal interface IDispatcherEdgeWaitSource
{
    IDisposable? RegisterEdgeTriggeredWaitHandle(LinuxFile linuxFile, IReadyDispatcher dispatcher, Action callback,
        short events);
}

public readonly record struct FileMutationContext(
    VMAManager? AddressSpace = null,
    Engine? Engine = null,
    Process? Process = null)
{
    public bool HasLiveAddressSpace => AddressSpace != null && Engine != null;
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

    public static IDisposable? RegisterHandleOnNextSignal(Action callback, FiberTask task, short events,
        in QueueReadinessWatch first, in QueueReadinessWatch second = default)
    {
        return RegisterHandleOnNextSignalCore(callback, events, task, null, first, second);
    }

    public static IDisposable? RegisterHandleOnNextSignal(Action callback, KernelScheduler scheduler, short events,
        in QueueReadinessWatch first, in QueueReadinessWatch second = default)
    {
        return RegisterHandleOnNextSignalCore(callback, events, null, scheduler, first, second);
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

    private static IDisposable? RegisterHandleOnNextSignalCore(Action callback, short requestedEvents, FiberTask? task,
        KernelScheduler? scheduler, in QueueReadinessWatch first, in QueueReadinessWatch second)
    {
        List<IDisposable>? registrations = null;
        TryRegisterHandleOnNextSignal(callback, requestedEvents, task, scheduler, first, ref registrations);
        TryRegisterHandleOnNextSignal(callback, requestedEvents, task, scheduler, second, ref registrations);
        if (registrations == null || registrations.Count == 0)
            return null;
        if (registrations.Count == 1)
            return registrations[0];
        return new CompositeWaitRegistration(registrations);
    }

    private static void TryRegisterHandleOnNextSignal(Action callback, short requestedEvents, FiberTask? task,
        KernelScheduler? scheduler, in QueueReadinessWatch watch, ref List<IDisposable>? registrations)
    {
        if (!watch.ShouldObserve(requestedEvents) || watch.Queue == null)
            return;

        if (!watch.IsReady)
        {
            watch.ResetIfStale();

            // Edge-triggered epoll callers only reach this path after an earlier readiness check
            // observed "not ready". If readiness flipped true before the waiter was linked, we
            // must deliver that fresh edge immediately instead of waiting for a later toggle.
            if (watch.IsReady)
            {
                ScheduleReadyCallback(callback, task, scheduler);
                registrations ??= [];
                registrations.Add(NoopWaitRegistration.Instance);
                return;
            }
        }

        var registration = task != null
            ? watch.Queue.RegisterCancelableOnNextSignal(callback, task)
            : watch.Queue.RegisterCancelableOnNextSignal(callback,
                scheduler ?? throw new InvalidOperationException("Scheduler is required for wait registration."));
        if (registration == null)
            return;

        registrations ??= [];
        registrations.Add(registration);
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

public readonly record struct WritePagesRequest(
    long StartPageIndex,
    long EndPageIndex,
    PageWritebackMode Mode = PageWritebackMode.Durable)
{
    public bool Sync => true;
}

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

public enum InodeRefTraceAction
{
    AcquireRef = 0,
    ReleaseRef = 1
}

public enum InodeLinkTraceOperation
{
    SetInitialLinkCount = 0,
    IncLink = 1,
    DecLink = 2,
    InitDefaultFromIncLink = 3,
    InitDefaultFromDecLink = 4
}

public enum InodeLifecycleTraceOperation
{
    ReleaseRefKernelInternal = 0,
    ReleaseRefFileOpen = 1,
    ReleaseRefFileMmap = 2,
    ReleaseRefPathPin = 3,
    SetInitialLinkCount = 4,
    DecLink = 5,
    FinalizeReleaseRefKernelInternal = 6,
    FinalizeReleaseRefFileOpen = 7,
    FinalizeReleaseRefFileMmap = 8,
    FinalizeReleaseRefPathPin = 9,
    FinalizeSetInitialLinkCount = 10,
    FinalizeDecLink = 11,
    VfsShrinkerEvictUnusedInodes = 12
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
        VfsDebugTrace.RecordRefChange(this, InodeRefTraceAction.AcquireRef, kind, before, RefCount, reason);
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
        var operation = GetReleaseRefLifecycleOperation(kind);
        VfsDebugTrace.RecordRefChange(this, InodeRefTraceAction.ReleaseRef, kind, before, RefCount, reason);
        ReleaseUnlinkedAliasDentriesIfUnused(operation, reason);
        TryFinalizeDelete(operation, reason);
    }

    private static InodeLifecycleTraceOperation GetReleaseRefLifecycleOperation(InodeRefKind kind)
    {
        return kind switch
        {
            InodeRefKind.KernelInternal => InodeLifecycleTraceOperation.ReleaseRefKernelInternal,
            InodeRefKind.FileOpen => InodeLifecycleTraceOperation.ReleaseRefFileOpen,
            InodeRefKind.FileMmap => InodeLifecycleTraceOperation.ReleaseRefFileMmap,
            InodeRefKind.PathPin => InodeLifecycleTraceOperation.ReleaseRefPathPin,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
    }

    private static InodeLifecycleTraceOperation GetFinalizeCacheEvictOperation(InodeLifecycleTraceOperation operation)
    {
        return operation switch
        {
            InodeLifecycleTraceOperation.ReleaseRefKernelInternal =>
                InodeLifecycleTraceOperation.FinalizeReleaseRefKernelInternal,
            InodeLifecycleTraceOperation.ReleaseRefFileOpen => InodeLifecycleTraceOperation.FinalizeReleaseRefFileOpen,
            InodeLifecycleTraceOperation.ReleaseRefFileMmap => InodeLifecycleTraceOperation.FinalizeReleaseRefFileMmap,
            InodeLifecycleTraceOperation.ReleaseRefPathPin => InodeLifecycleTraceOperation.FinalizeReleaseRefPathPin,
            InodeLifecycleTraceOperation.SetInitialLinkCount => InodeLifecycleTraceOperation.FinalizeSetInitialLinkCount,
            InodeLifecycleTraceOperation.DecLink => InodeLifecycleTraceOperation.FinalizeDecLink,
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
        };
    }

    private static string GetUnlinkedAliasReason(InodeLifecycleTraceOperation operation)
    {
        return operation switch
        {
            InodeLifecycleTraceOperation.ReleaseRefKernelInternal => "ReleaseRef.KernelInternal.unlinked-alias",
            InodeLifecycleTraceOperation.ReleaseRefFileOpen => "ReleaseRef.FileOpen.unlinked-alias",
            InodeLifecycleTraceOperation.ReleaseRefFileMmap => "ReleaseRef.FileMmap.unlinked-alias",
            InodeLifecycleTraceOperation.ReleaseRefPathPin => "ReleaseRef.PathPin.unlinked-alias",
            InodeLifecycleTraceOperation.DecLink => "DecLink.unlinked-alias",
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
        };
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
        VfsDebugTrace.RecordLinkChange(this, InodeLinkTraceOperation.SetInitialLinkCount, before, LinkCount, reason);
        TryFinalizeDelete(InodeLifecycleTraceOperation.SetInitialLinkCount, reason);
    }

    public void IncLink(string? reason = null)
    {
        EnsureLinkCountInitialized(InodeLinkTraceOperation.IncLink);
        var before = LinkCount;
        LinkCount++;
        CTime = DateTime.Now;
        VfsDebugTrace.RecordLinkChange(this, InodeLinkTraceOperation.IncLink, before, LinkCount, reason);
    }

    public void DecLink(string? reason = null)
    {
        EnsureLinkCountInitialized(InodeLinkTraceOperation.DecLink);
        var before = LinkCount;
        if (before <= 0)
        {
            VfsDebugTrace.FailInvariant(
                $"Inode.DecLink underflow ino={Ino} type={Type} link={before} reason={reason}");
            return;
        }

        LinkCount = before - 1;
        CTime = DateTime.Now;
        VfsDebugTrace.RecordLinkChange(this, InodeLinkTraceOperation.DecLink, before, LinkCount, reason);
        ReleaseUnlinkedAliasDentriesIfUnused(InodeLifecycleTraceOperation.DecLink, reason);
        TryFinalizeDelete(InodeLifecycleTraceOperation.DecLink, reason);
    }

    public virtual uint GetLinkCountForStat()
    {
        if (HasExplicitLinkCount)
            return (uint)Math.Max(0, LinkCount);
        return (uint)GetDefaultLinkCountForType();
    }

    public bool TryEvictCache(InodeLifecycleTraceOperation operation, string? reason = null)
    {
        if (IsCacheEvicted) return false;
        if (RefCount != 0) return false;
        IsCacheEvicted = true;
        VfsDebugTrace.RecordCacheEvict(this, operation, reason);
        OnEvictCache();
        return true;
    }

    public bool TryFinalizeDelete(InodeLifecycleTraceOperation operation, string? reason = null)
    {
        if (IsFinalized) return false;
        if (!HasExplicitLinkCount) return false;
        if (RefCount != 0 || LinkCount != 0) return false;
        IsFinalized = true;
        VfsDebugTrace.RecordFinalize(this, operation, reason);
        OnFinalizeDelete();
        // Finalized (nlink=0 && ref=0) inodes are dead objects and should not linger in cache.
        TryEvictCache(GetFinalizeCacheEvictOperation(operation), reason);
        return true;
    }

    private void EnsureLinkCountInitialized(InodeLinkTraceOperation source)
    {
        if (HasExplicitLinkCount) return;
        LinkCount = GetDefaultLinkCountForType();
        HasExplicitLinkCount = true;
        var initOperation = source switch
        {
            InodeLinkTraceOperation.IncLink => InodeLinkTraceOperation.InitDefaultFromIncLink,
            InodeLinkTraceOperation.DecLink => InodeLinkTraceOperation.InitDefaultFromDecLink,
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, null)
        };
        VfsDebugTrace.RecordLinkChange(this, initOperation, 0, LinkCount, null);
    }

    private int GetDefaultLinkCountForType()
    {
        return Type == InodeType.Directory ? 2 : 1;
    }

    private void ReleaseUnlinkedAliasDentriesIfUnused(InodeLifecycleTraceOperation operation, string? reason)
    {
        if (LinkCount != 0 || HasActiveRuntimeRefs || _dentries.Count == 0)
            return;

        var dentryReason = GetUnlinkedAliasReason(operation);

        foreach (var dentry in _dentries.Where(d => !d.IsHashed).ToArray())
        {
            if (!dentry.UnbindInode(dentryReason))
                continue;

            if (dentry.DentryRefCount == 0)
                dentry.UntrackFromSuperBlock(dentryReason);
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

    public virtual int ConsumeLookupFailureError()
    {
        var error = _lookupFailureError;
        _lookupFailureError = -(int)Errno.ENOENT;
        return error == 0 ? -(int)Errno.ENOENT : error;
    }

    public virtual int ConsumeLookupFailureError(string name)
    {
        _ = name;
        return ConsumeLookupFailureError();
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

    public int Truncate(long length, in FileMutationContext context)
    {
        return TruncateWithContextCore(length, context);
    }

    protected internal virtual int TruncateWithContextCore(long length, in FileMutationContext context)
    {
        _ = context;
        return Truncate(length);
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

    public virtual int ReadToHost(FiberTask? task, LinuxFile file, Span<byte> buffer, long offset = -1)
    {
        _ = task;
        _ = file;
        _ = buffer;
        _ = offset;
        return 0;
    }

    public virtual int WriteFromHost(FiberTask? task, LinuxFile file, ReadOnlySpan<byte> buffer, long offset = -1)
    {
        _ = file;
        _ = buffer;
        _ = offset;
        if (Type == InodeType.Directory) return -(int)Errno.EISDIR;
        var rc = 0;
        if (rc > 0)
            ApplySuccessfulWriteSideEffects(task);
        return rc;
    }

    protected void ApplySuccessfulWriteSideEffects(FiberTask? task)
    {
        CTime = DateTime.Now;
        if (task?.Process != null)
        {
            var clearedMode = DacPolicy.ApplySetIdClearOnWrite(task.Process, this);
            if (clearedMode != Mode)
                Mode = clearedMode;
        }
    }

    public virtual async ValueTask<int> ReadV(Engine engine, LinuxFile file, FiberTask? task, ArraySegment<Iovec> iovs,
        long offset, int flags)
    {
        return await ReadVViaHostBuffer(engine, file, task, iovs, offset, flags);
    }

    protected async ValueTask<int> ReadVViaHostBuffer(Engine engine, LinuxFile file, FiberTask? task,
        ArraySegment<Iovec> iovs, long offset, int flags)
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
                        n = ReadToHost(task, file, span, currentOffset);
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
        ArraySegment<Iovec> iovs, long offset, int flags)
    {
        return await WriteVViaHostBuffer(engine, file, task, iovs, offset, flags);
    }

    protected async ValueTask<int> WriteVViaHostBuffer(Engine engine, LinuxFile file, FiberTask? task,
        ArraySegment<Iovec> iovs, long offset, int flags)
    {
        var updatePosition = offset == -1;
        var append = (file.Flags & FileFlags.O_APPEND) != 0;
        var currentOffset = updatePosition
            ? append ? (long)Size : file.Position
            : offset;
        var totalWritten = 0;

        int FinalizeWriteResult(int rc)
        {
            if (updatePosition) file.Position = currentOffset;
            if (rc > 0)
                ApplySuccessfulWriteSideEffects(task);
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
                        n = WriteFromHost(task, file, span, currentOffset);
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

    public virtual bool TryFlushMappedPage(LinuxFile? linuxFile, long pageIndex,
        PageWritebackMode mode = PageWritebackMode.Durable)
    {
        _ = linuxFile;
        _ = pageIndex;
        _ = mode;
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
    ///     Register a callback for the next readiness transition without immediately replaying the
    ///     current ready state. This is used by edge-triggered epoll watches after a delivered event.
    /// </summary>
    public virtual IDisposable? RegisterEdgeTriggeredWaitHandle(LinuxFile linuxFile, Action callback, short events)
    {
        return RegisterWaitHandle(linuxFile, callback, events);
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

    protected internal virtual int FlushWritebackToDurable(LinuxFile? linuxFile)
    {
        _ = linuxFile;
        return 0;
    }

    protected internal virtual ValueTask<int> FlushWritebackToDurableAsync(LinuxFile? linuxFile, FiberTask? task)
    {
        _ = task;
        return ValueTask.FromResult(FlushWritebackToDurable(linuxFile));
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

    protected ValueTask<int> FlushBlockingHostOperationAsync(FiberTask? task, SafeFileHandle? handle,
        string operationName, Func<int> flushOperation, Func<Exception, int> mapException)
    {
        if (OperatingSystem.IsBrowser() || task == null)
            return ValueTask.FromResult(flushOperation());

        var schedulerThreadId = task.CommonKernel.OwnerThreadId;
        if (schedulerThreadId == 0 || schedulerThreadId != Environment.CurrentManagedThreadId)
            return ValueTask.FromResult(flushOperation());

        if (handle == null)
            return ValueTask.FromResult(flushOperation());

        var addRefSucceeded = false;
        try
        {
            handle.DangerousAddRef(ref addRefSucceeded);
        }
        catch (Exception ex)
        {
            return ValueTask.FromResult(mapException(ex));
        }

        if (!addRefSucceeded)
            return ValueTask.FromResult(-(int)Errno.EIO);

        var releasePending = 1;

        void ReleaseHandleOnce()
        {
            if (Interlocked.Exchange(ref releasePending, 0) == 0)
                return;
            handle.DangerousRelease();
        }

        return AwaitFlushAsync();

        async ValueTask<int> AwaitFlushAsync()
        {
            try
            {
                return await new BlockingHostOperationAwaitable(task, operationName, () =>
                {
                    try
                    {
                        return flushOperation();
                    }
                    catch (Exception ex)
                    {
                        return mapException(ex);
                    }
                    finally
                    {
                        ReleaseHandleOnce();
                    }
                });
            }
            catch
            {
                ReleaseHandleOnce();
                throw;
            }
        }
    }

    protected virtual SafeFileHandle? GetOpenHandle(LinuxFile? linuxFile)
    {
        return linuxFile?.PrivateData as SafeFileHandle;
    }

    protected virtual bool FlushMappedWindowsToBacking()
    {
        return true;
    }

    protected virtual void FlushHandleToDisk(SafeFileHandle handle)
    {
        RandomAccess.FlushToDisk(handle);
    }

    protected virtual int FlushDeferredMetadata(LinuxFile? linuxFile)
    {
        _ = linuxFile;
        return 0;
    }

    protected int FlushWritebackToDurableCore(LinuxFile? linuxFile, bool flushMetadata)
    {
        if (!FlushMappedWindowsToBacking())
            return -(int)Errno.EIO;

        if (GetOpenHandle(linuxFile) is { } handle)
            FlushHandleToDisk(handle);

        return flushMetadata ? FlushDeferredMetadata(linuxFile) : 0;
    }

    protected ValueTask<int> FlushWritebackToDurableCoreAsync(LinuxFile? linuxFile, FiberTask? task,
        string operationName, bool flushMetadata, Func<Exception, int> mapException)
    {
        var handle = GetOpenHandle(linuxFile);
        return FlushBlockingHostOperationAsync(task, handle, operationName, () =>
        {
            if (!FlushMappedWindowsToBacking())
                return -(int)Errno.EIO;
            if (handle != null)
                FlushHandleToDisk(handle);
            return flushMetadata ? FlushDeferredMetadata(linuxFile) : 0;
        }, mapException);
    }

    private static int GetBatchInsertCapacity(int currentCount, int additionalEntries)
    {
        if (additionalEntries <= 0)
            return currentCount;

        var minRequired = checked(currentCount + additionalEntries);
        var growth = Math.Max(additionalEntries, Math.Max(256, currentCount / 2));
        return Math.Max(minRequired, checked(currentCount + growth));
    }

    protected virtual AddressSpaceKind MappingKind => AddressSpaceKind.File;

    protected virtual AddressSpacePolicy.AddressSpaceCacheClass? MappingCacheClass => MappingKind switch
    {
        AddressSpaceKind.File => AddressSpacePolicy.AddressSpaceCacheClass.File,
        AddressSpaceKind.Shmem => AddressSpacePolicy.AddressSpaceCacheClass.Shmem,
        _ => null
    };

    protected virtual long CachedSizeForShrinkCoordinator
    {
        get => checked((long)Size);
        set => Size = (ulong)value;
    }

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

    protected internal override int TruncateWithContextCore(long length, in FileMutationContext context)
    {
        var previousSize = CachedSizeForShrinkCoordinator;
        var rc = Truncate(length);
        if (rc == 0)
            FinalizeExplicitSizeChange(previousSize, length, context);
        return rc;
    }

    protected void FinalizeExplicitSizeChange(long previousSize, long newSize, in FileMutationContext context)
    {
        if (newSize < 0)
            newSize = 0;

        if (newSize < previousSize)
        {
            ReconcileShrink(previousSize, newSize, context);
            return;
        }

        CachedSizeForShrinkCoordinator = newSize;
    }

    internal void ObserveBackingSizeChange(long newSize, in FileMutationContext context)
    {
        if (newSize < 0)
            newSize = 0;

        var previousCachedSize = CachedSizeForShrinkCoordinator;
        if (newSize < previousCachedSize)
        {
            ReconcileShrink(previousCachedSize, newSize, context);
            return;
        }

        CachedSizeForShrinkCoordinator = newSize;
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

    protected virtual void RetireHostMappedWindowsBeforeFileShrink(long newSize)
    {
        _ = newSize;
    }

    protected virtual void RetireResidentHostMappedTailPageBeforeFileShrink(long newSize)
    {
        if (newSize <= 0)
            return;

        var tailBytes = (int)(newSize % LinuxConstants.PageSize);
        if (tailBytes == 0)
            return;

        var keepPageIndex = (uint)(newSize / LinuxConstants.PageSize);
        if (!TryGetMappingPageRecord(keepPageIndex, out var record) ||
            record.BackingKind != FilePageBackingKind.HostMappedWindow)
            return;

        var removedResidentPages = Mapping?.RemovePagesInRange(keepPageIndex, keepPageIndex + 1) ?? 0;
        if (removedResidentPages > 0 && !TryGetMappingPageRecord(keepPageIndex, out _))
            return;

        if (removedResidentPages == 0 || TryGetMappingPageRecord(keepPageIndex, out record))
            ReleaseInstalledMappingPage(record);
    }

    protected virtual void OnFileShrinkReconciled(long previousSize, long newSize)
    {
        _ = previousSize;
        _ = newSize;
    }

    private void ReconcileShrink(long previousSize, long newSize, in FileMutationContext context)
    {
        CachedSizeForShrinkCoordinator = newSize;
        RetireHostMappedWindowsBeforeFileShrink(newSize);
        ProcessAddressSpaceSync.NotifyInodeTruncated(this, newSize, context);
        RetireResidentHostMappedTailPageBeforeFileShrink(newSize);
        Mapping?.TruncateToSize(newSize);
        OnFileShrinkReconciled(previousSize, newSize);
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

        if (request.Mode == PageWritebackMode.Durable)
        {
            var flushRc = FlushWritebackToDurable(linuxFile);
            if (flushRc < 0)
                return flushRc;
        }

        CompleteCachedPageSync(linuxFile, mapping, request.PageIndex, null);
        return 0;
    }

    internal virtual int SyncCachedPages(LinuxFile? linuxFile, AddressSpace mapping,
        WritePagesRequest request)
    {
        if (!request.Sync)
            return 0;

        var dirtyPageIndices = mapping.GetDirtyPageIndicesInRangeOrdered(request.StartPageIndex, request.EndPageIndex);
        foreach (var pageIndex in dirtyPageIndices)
        {
            var syncRequest = CreatePageSyncRequest(pageIndex, PageWritebackMode.WritebackOnly);
            var rc = SyncCachedPage(linuxFile, mapping, syncRequest);
            if (rc < 0)
                return rc;
        }

        if (request.Mode == PageWritebackMode.Durable)
        {
            var flushRc = FlushWritebackToDurable(linuxFile);
            if (flushRc < 0)
                return flushRc;
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

    internal InodePageRecord[] SnapshotMappingPageRecords()
    {
        lock (_mappingPageLock)
        {
            return [.. _mappingPages.Values];
        }
    }

    internal void RetireAllHostMappedMappingPagesForResize()
    {
        var records = SnapshotMappingPageRecords();
        if (records.Length == 0)
            return;

        var mapping = AcquireMappingRef();
        try
        {
            foreach (var record in records)
            {
                if (record.BackingKind != FilePageBackingKind.HostMappedWindow)
                    continue;

                var pageIndex = record.PageIndex;
                var removedResidentPages = mapping.RemovePagesInRange(pageIndex, pageIndex + 1);
                if (removedResidentPages == 0)
                {
                    if (TryGetMappingPageRecord(pageIndex, out var current) &&
                        current.BackingKind == FilePageBackingKind.HostMappedWindow)
                        ReleaseInstalledMappingPage(current);
                }
                else if (TryGetMappingPageRecord(pageIndex, out var current) &&
                         current.BackingKind == FilePageBackingKind.HostMappedWindow)
                {
                    ReleaseInstalledMappingPage(current);
                }
            }
        }
        finally
        {
            mapping.Release();
        }
    }

    private int CountLeadingMissingMappingPages(uint startPageIndex, uint endPageIndexInclusive)
    {
        if (endPageIndexInclusive < startPageIndex)
            return 0;

        var rangePageCount = (ulong)endPageIndexInclusive - startPageIndex + 1;
        lock (_mappingPageLock)
        {
            if (_mappingPages.Count == 0)
                return checked((int)rangePageCount);

            if (rangePageCount <= (ulong)_mappingPages.Count)
            {
                var missingCount = 0;
                for (var pageIndex = startPageIndex;; pageIndex++)
                {
                    if (_mappingPages.ContainsKey(pageIndex))
                        return missingCount;

                    missingCount++;
                    if (pageIndex == endPageIndexInclusive)
                        return missingCount;
                }
            }

            var firstPresentPageIndex = uint.MaxValue;
            foreach (var pageIndex in _mappingPages.Keys)
            {
                if (pageIndex < startPageIndex || pageIndex > endPageIndexInclusive)
                    continue;
                if (pageIndex == startPageIndex)
                    return 0;
                if (pageIndex < firstPresentPageIndex)
                    firstPresentPageIndex = pageIndex;
            }

            return firstPresentPageIndex == uint.MaxValue
                ? checked((int)rangePageCount)
                : checked((int)(firstPresentPageIndex - startPageIndex));
        }
    }

    private protected bool TryRegisterMappingPage(uint pageIndex, InodePageRecord record)
    {
        lock (_mappingPageLock)
        {
            return _mappingPages.TryAdd(pageIndex, record);
        }
    }

    private protected void TryRegisterMappingPagesBatch(ReadOnlySpan<InodePageRecord?> records, Span<bool> inserted)
    {
        if (inserted.Length < records.Length)
            throw new ArgumentException("Inserted span is smaller than record batch.", nameof(inserted));

        var count = records.Length;
        if (count == 0)
            return;

        var pendingCount = 0;
        for (var i = 0; i < count; i++)
        {
            if (records[i] != null)
                pendingCount++;
        }

        lock (_mappingPageLock)
        {
            if (pendingCount > 0)
                _mappingPages.EnsureCapacity(GetBatchInsertCapacity(_mappingPages.Count, pendingCount));

            for (var i = 0; i < count; i++)
            {
                var record = records[i];
                inserted[i] = record != null && _mappingPages.TryAdd(record.PageIndex, record);
            }
        }
    }

    private protected bool TryRemoveOwnedMappingPageRecord(uint pageIndex, InodePageRecord record)
    {
        lock (_mappingPageLock)
        {
            if (!_mappingPages.TryGetValue(pageIndex, out var existing) || !ReferenceEquals(existing, record))
                return false;

            _mappingPages.Remove(pageIndex);
            return true;
        }
    }

    protected virtual bool TryPopulateMappingPage(LinuxFile? linuxFile, uint pageIndex, long fileOffset,
        int prefillLength, Span<byte> pageBuffer, out int error)
    {
        error = 0;
        pageBuffer.Clear();
        if (prefillLength <= 0 || MappingKind != AddressSpaceKind.File)
            return true;

        var rc = ReadPage(linuxFile, new PageIoRequest(pageIndex, fileOffset, prefillLength), pageBuffer);
        if (rc >= 0)
            return true;

        error = rc;
        return false;
    }

    private InodePageRecord CreateAllocatedMappingPage(LinuxFile? linuxFile, uint pageIndex, long fileOffset,
        int prefillLength, out int error)
    {
        error = -(int)Errno.ENOMEM;
        if (!SuperBlock.MemoryContext.BackingPagePool.TryAllocatePoolBackedPageStrict(out var pageHandle) ||
            !pageHandle.IsValid)
            return null!;

        var ptr = pageHandle.Pointer;
        try
        {
            unsafe
            {
                var target = new Span<byte>((void*)ptr, LinuxConstants.PageSize);
                if (!TryPopulateMappingPage(linuxFile, pageIndex, fileOffset, prefillLength, target, out error))
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
            Handle = pageHandle,
            IsWritable = writable
        };
        return record;
    }

    private void RetireReadonlyMappingPageForWrite(AddressSpace mapping, uint pageIndex)
    {
        if (!TryGetMappingPageRecord(pageIndex, out var record) || record.IsWritable)
            return;

        var removedResidentPages = mapping.RemovePagesInRange(pageIndex, pageIndex + 1);
        if (removedResidentPages == 0)
        {
            if (TryGetMappingPageRecord(pageIndex, out record))
                ReleaseInstalledMappingPage(record);
        }
        else if (TryGetMappingPageRecord(pageIndex, out record) && !record.IsWritable)
        {
            ReleaseInstalledMappingPage(record);
        }
    }

    private int UpgradeReadonlyMappingPageForWrite(LinuxFile file, AddressSpace mapping, uint pageIndex, long fileSize,
        Span<IntPtr> resolvedPagePointer)
    {
        if (!TryGetMappingPageRecord(pageIndex, out var record) || record.IsWritable)
        {
            if (!resolvedPagePointer.IsEmpty)
                resolvedPagePointer[0] = mapping.PeekPage(pageIndex);
            return 0;
        }

        var useWritableHostMapping = record.BackingKind == FilePageBackingKind.HostMappedWindow &&
                                     PreferHostMappedMappingPage(PageCacheAccessMode.Write);
        RetireReadonlyMappingPageForWrite(mapping, pageIndex);

        if (useWritableHostMapping)
            return InstallHostMappedPageRun(file, mapping, pageIndex, 1, true, fileSize, resolvedPagePointer);

        var fileOffset = (long)pageIndex * LinuxConstants.PageSize;
        var pagePtr = AcquireMappingPage(file, pageIndex, fileOffset, PageCacheAccessMode.Write, 0, out var error,
            true);
        if (pagePtr == IntPtr.Zero)
            return error != 0 ? error : -(int)Errno.EIO;
        if (!resolvedPagePointer.IsEmpty)
            resolvedPagePointer[0] = pagePtr;
        return 0;
    }

    internal IntPtr AcquireMappingPage(LinuxFile? linuxFile, uint pageIndex, long fileOffset,
        PageCacheAccessMode accessMode, int prefillLength, bool allowHostMapped = true)
    {
        return AcquireMappingPage(linuxFile, pageIndex, fileOffset, accessMode, prefillLength, out _,
            allowHostMapped);
    }

    internal IntPtr AcquireMappingPage(LinuxFile? linuxFile, uint pageIndex, long fileOffset,
        PageCacheAccessMode accessMode, int prefillLength, out int error, bool allowHostMapped = true)
    {
        error = 0;
        var mapping = EnsureMapping();
        if (accessMode == PageCacheAccessMode.Write)
            RetireReadonlyMappingPageForWrite(mapping, pageIndex);

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
        if (record == null)
            record = CreateAllocatedMappingPage(linuxFile, pageIndex, fileOffset, prefillLength, out error);
        if (record == null)
        {
            if (error == 0)
                error = -(int)Errno.ENOMEM;
            return IntPtr.Zero;
        }

        return RegisterAndInstallMappingPage(mapping, pageIndex, record);
    }

    internal IntPtr RegisterAndInstallMappingPage(AddressSpace mapping, uint pageIndex, InodePageRecord record)
    {
        if (!TryRegisterMappingPage(pageIndex, record))
        {
            record.ReleaseOwnership();
            if (!TryGetMappingPageRecord(pageIndex, out var resident))
                return mapping.PeekPage(pageIndex);

            return mapping.InstallHostPageIfAbsent(pageIndex, resident.Ptr, ref resident.Handle,
                resident.HostPageKind, this, resident, out _);
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
        return TryFlushMappedPage(linuxFile, request.PageIndex, request.Mode) ? 0 : -(int)Errno.EOPNOTSUPP;
    }

    internal virtual void CompleteCachedPageSync(LinuxFile? linuxFile, AddressSpace mapping, uint pageIndex,
        InodePageRecord? record)
    {
        _ = linuxFile;
        _ = record;
        mapping.ClearDirty(pageIndex);
    }

    protected internal virtual int BackendRead(LinuxFile? linuxFile, Span<byte> buffer, long offset)
    {
        _ = linuxFile;
        _ = buffer;
        _ = offset;
        return 0;
    }

    protected internal virtual int BackendWrite(LinuxFile? linuxFile, ReadOnlySpan<byte> buffer, long offset)
    {
        _ = linuxFile;
        _ = buffer;
        _ = offset;
        return 0;
    }

    protected int ReadWithPageCache(
        LinuxFile? linuxFile,
        Span<byte> buffer,
        long offset)
    {
        if (buffer.Length == 0) return 0;
        var pageCache = Mapping;
        if (pageCache == null) return BackendRead(linuxFile, buffer, offset);
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
        long offset)
    {
        if (buffer.Length == 0) return 0;
        var pageCache = EnsureMapping();
        if (linuxFile == null) return BackendWrite(linuxFile, buffer, offset);
        if (offset < 0) return -(int)Errno.EINVAL;

        var cachedSize = CachedSizeForShrinkCoordinator;
        var consumed = 0;
        var cursor = offset;
        while (consumed < buffer.Length)
        {
            var pageIndex = (uint)(cursor / LinuxConstants.PageSize);
            var pageOffset = (int)(cursor & LinuxConstants.PageOffsetMask);
            var chunk = Math.Min(buffer.Length - consumed, LinuxConstants.PageSize - pageOffset);

            var (pagePtr, pageError) = EnsurePageInCacheForWrite(
                pageCache,
                linuxFile,
                pageIndex,
                pageOffset,
                chunk,
                cachedSize);
            if (pagePtr == IntPtr.Zero) return consumed > 0 ? consumed : pageError;

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
        if (end > cachedSize) CachedSizeForShrinkCoordinator = end;
        var now = DateTime.Now;
        MTime = now;
        CTime = now;
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

    private PageSyncRequest CreatePageSyncRequest(uint pageIndex,
        PageWritebackMode mode = PageWritebackMode.Durable)
    {
        var fileOffset = (long)pageIndex * LinuxConstants.PageSize;
        var remaining = Math.Max(0, CachedSizeForShrinkCoordinator - fileOffset);
        var length = (int)Math.Min(LinuxConstants.PageSize, remaining);
        return new PageSyncRequest(pageIndex, fileOffset, length, mode);
    }

    private (IntPtr PagePtr, int Error) EnsurePageInCacheForWrite(
        AddressSpace pageCache, LinuxFile linuxFile, uint pageIndex, int pageOffset, int chunk, long fileSize)
    {
        var pagePtr = pageCache.GetPage(pageIndex);
        if (pagePtr != IntPtr.Zero) return (pagePtr, 0);

        var pageFileOffset = (long)pageIndex * LinuxConstants.PageSize;
        if (SuperBlock.MemoryContext.HostMemoryMapGeometry.SupportsMappedFileBackend)
        {
            Debug.Assert(pageFileOffset <= fileSize,
                $"paged writes must pre-extend EOF before touching page {pageIndex} (offset {pageFileOffset}, size {fileSize})");
        }
        var pageReadLen = 0;
        byte[]? tempPage = null;

        var fullPageWrite = pageOffset == 0 && chunk == LinuxConstants.PageSize;
        if (!fullPageWrite && pageFileOffset < fileSize)
        {
            tempPage = new byte[LinuxConstants.PageSize];
            tempPage.AsSpan().Clear();
            pageReadLen = (int)Math.Min(LinuxConstants.PageSize, fileSize - pageFileOffset);
            var rc = ReadPage(linuxFile, new PageIoRequest(pageIndex, pageFileOffset, pageReadLen), tempPage);
            if (rc < 0)
                return (IntPtr.Zero, rc);
        }

        if (pageCache.Kind is AddressSpaceKind.File or AddressSpaceKind.Shmem or AddressSpaceKind.Zero)
        {
            var writeEndOffset = pageFileOffset + pageOffset + chunk;
            var allowHostMapped = writeEndOffset <= fileSize;
            pagePtr = AcquireMappingPage(linuxFile, pageIndex, pageFileOffset, PageCacheAccessMode.Write, pageReadLen,
                out var error, allowHostMapped);
            return (pagePtr, pagePtr != IntPtr.Zero ? 0 : error);
        }

        var captured = tempPage;
        var capturedReadLen = pageReadLen;
        pagePtr = pageCache.GetOrCreatePage(pageIndex, p =>
        {
            unsafe
            {
                var dst = new Span<byte>((void*)p, LinuxConstants.PageSize);
                dst.Clear();
                if (capturedReadLen > 0) captured!.AsSpan(0, capturedReadLen).CopyTo(dst);
            }

            return true;
        }, out _, true, AllocationClass.PageCache);
        return (pagePtr, pagePtr != IntPtr.Zero ? 0 : -(int)Errno.ENOMEM);
    }

    #region Batch Vector I/O

    [ThreadStatic] private static MappingIoScratch? _threadMappingIoScratch;

    private sealed class MappingIoScratch
    {
        public ResolvedBufferSegment[] GuestSegments = new ResolvedBufferSegment[1024];
        public IntPtr[] PagePointers = [];
        public long[] ReleaseTokens = [];
        public InodePageRecord?[] Records = [];
        public IntPtr[] FinalPointers = [];
        public IntPtr[] ResolvedPagePointers = [];
        public bool[] Inserted = [];

        public void EnsureGuestSegmentCapacity(int count)
        {
            if (GuestSegments.Length < count)
                Array.Resize(ref GuestSegments, count);
        }

        public void EnsureInstallCapacity(int count)
        {
            if (PagePointers.Length < count)
                Array.Resize(ref PagePointers, count);
            if (ReleaseTokens.Length < count)
                Array.Resize(ref ReleaseTokens, count);
            if (Records.Length < count)
                Array.Resize(ref Records, count);
            if (FinalPointers.Length < count)
                Array.Resize(ref FinalPointers, count);
            if (ResolvedPagePointers.Length < count)
                Array.Resize(ref ResolvedPagePointers, count);
            if (Inserted.Length < count)
                Array.Resize(ref Inserted, count);
        }

        public void ClearInstallRecords(int count)
        {
            Array.Clear(Records, 0, count);
        }
    }

    private static MappingIoScratch GetMappingIoScratch(int guestSegmentCapacity, int pageCount)
    {
        var scratch = _threadMappingIoScratch ??= new MappingIoScratch();
        if (guestSegmentCapacity > 0)
            scratch.EnsureGuestSegmentCapacity(guestSegmentCapacity);
        if (pageCount > 0)
            scratch.EnsureInstallCapacity(pageCount);
        return scratch;
    }

    /// <summary>
    ///     Attempt to acquire host-mapped page leases in batch. Return number of leases acquired.
    /// </summary>
    protected virtual int TryAcquireHostMappedPageLeases(LinuxFile? linuxFile, uint startPageIndex, int pageCount,
        long fileSize, bool writable, Span<IntPtr> pointers, Span<long> releaseTokens) => 0;

    /// <summary>
    ///     Prepare backing file for host-mapped write (e.g. pre-extend EOF).
    /// </summary>
    protected virtual int PrepareHostMappedWrite(LinuxFile? linuxFile, long bufferLength, long offset) => 0;

    /// <summary>
    ///     Called after a range of mapping pages is marked dirty during host-mapped write.
    ///     Subclasses can track dirty pages for their own writeback logic.
    /// </summary>
    protected virtual void OnMappingPagesMarkedDirty(uint startPageIndex, uint endPageIndexInclusive)
    {
    }

    /// <summary>
    ///     Called after a successful host-mapped write to update timestamps and metadata.
    /// </summary>
    protected virtual void OnWriteCompleted(long bytesWritten)
    {
        if (bytesWritten <= 0)
            return;

        var now = DateTime.Now;
        MTime = now;
        CTime = now;
    }

    /// <summary>
    ///     Release a single host-mapped page lease acquired via <see cref="TryAcquireHostMappedPageLeases" />.
    /// </summary>
    protected virtual void ReleaseHostMappedPageLease(long releaseToken)
    {
    }

    public override ValueTask<int> ReadV(Engine engine, LinuxFile file, FiberTask? task, ArraySegment<Iovec> iovs,
        long offset, int flags)
    {
        _ = task;
        _ = flags;
        if (offset < -1)
            return ValueTask.FromResult(-(int)Errno.EINVAL);

        var currentOffset = offset == -1 ? file.Position : offset;
        if (currentOffset < 0)
            return ValueTask.FromResult(-(int)Errno.EINVAL);

        var rc = SuperBlock.MemoryContext.HostMemoryMapGeometry.SupportsMappedFileBackend
            ? ReadVHostMapped(engine, file, iovs, ref currentOffset)
            : ReadVBuffered(engine, file, iovs, ref currentOffset);

        if (offset == -1 && rc >= 0)
            file.Position = currentOffset;
        return ValueTask.FromResult(rc);
    }

    public override ValueTask<int> WriteV(Engine engine, LinuxFile file, FiberTask? task,
        ArraySegment<Iovec> iovs, long offset, int flags)
    {
        _ = task;
        _ = flags;
        if (offset < -1)
            return ValueTask.FromResult(-(int)Errno.EINVAL);

        var append = (file.Flags & FileFlags.O_APPEND) != 0;
        var currentOffset = offset == -1
            ? append ? CachedSizeForShrinkCoordinator : file.Position
            : offset;
        if (currentOffset < 0)
            return ValueTask.FromResult(-(int)Errno.EINVAL);

        var rc = SuperBlock.MemoryContext.HostMemoryMapGeometry.SupportsMappedFileBackend
            ? WriteVHostMapped(engine, file, iovs, ref currentOffset)
            : WriteVBuffered(engine, file, iovs, ref currentOffset);

        if (offset == -1 && rc >= 0)
            file.Position = currentOffset;
        return ValueTask.FromResult(rc);
    }

    private int ReadVBuffered(Engine engine, LinuxFile file, ArraySegment<Iovec> iovs, ref long currentOffset)
    {
        var totalRead = 0;
        var iovIndex = 0;
        uint iovOffset = 0;
        var guestSegments = ArrayPool<ResolvedBufferSegment>.Shared.Rent(256);
        try
        {
            while (iovIndex < iovs.Count)
            {
                var segmentCount = engine.GatherUserBufferSegments(
                    iovs,
                    iovIndex,
                    iovOffset,
                    true,
                    64 * 1024,
                    guestSegments,
                    out var bytesResolved,
                    out var nextIovIndex,
                    out var nextIovOffset);
                if (bytesResolved == 0)
                    return nextIovIndex >= iovs.Count ? totalRead : totalRead > 0 ? totalRead : -(int)Errno.EFAULT;

                for (var i = 0; i < segmentCount; i++)
                {
                    var segment = guestSegments[i];
                    unsafe
                    {
                        var span = new Span<byte>((void*)segment.Pointer, segment.Length);
                        var rc = ReadToHost(null, file, span, currentOffset);
                        if (rc <= 0)
                            return totalRead > 0 ? totalRead : rc;

                        totalRead += rc;
                        currentOffset += rc;
                        if (rc < segment.Length)
                            return totalRead;
                    }
                }

                iovIndex = nextIovIndex;
                iovOffset = nextIovOffset;
            }

            return totalRead;
        }
        finally
        {
            ArrayPool<ResolvedBufferSegment>.Shared.Return(guestSegments);
        }
    }

    private int WriteVBuffered(Engine engine, LinuxFile file, ArraySegment<Iovec> iovs, ref long currentOffset)
    {
        var totalWritten = 0;
        var iovIndex = 0;
        uint iovOffset = 0;
        var guestSegments = ArrayPool<ResolvedBufferSegment>.Shared.Rent(256);
        try
        {
            while (iovIndex < iovs.Count)
            {
                var segmentCount = engine.GatherUserBufferSegments(
                    iovs,
                    iovIndex,
                    iovOffset,
                    false,
                    64 * 1024,
                    guestSegments,
                    out var bytesResolved,
                    out var nextIovIndex,
                    out var nextIovOffset);
                if (bytesResolved == 0)
                    return nextIovIndex >= iovs.Count ? totalWritten : totalWritten > 0 ? totalWritten : -(int)Errno.EFAULT;

                for (var i = 0; i < segmentCount; i++)
                {
                    var segment = guestSegments[i];
                    unsafe
                    {
                        var span = new ReadOnlySpan<byte>((void*)segment.Pointer, segment.Length);
                        var rc = WriteFromHost(null, file, span, currentOffset);
                        if (rc <= 0)
                            return totalWritten > 0 ? totalWritten : rc;

                        totalWritten += rc;
                        currentOffset += rc;
                        if (rc < segment.Length)
                            return totalWritten;
                    }
                }

                iovIndex = nextIovIndex;
                iovOffset = nextIovOffset;
            }

            return totalWritten;
        }
        finally
        {
            ArrayPool<ResolvedBufferSegment>.Shared.Return(guestSegments);
        }
    }

    private int ReadVHostMapped(Engine engine, LinuxFile file, ArraySegment<Iovec> iovs, ref long currentOffset)
    {
        var mapping = EnsureMapping();
        var scratch = GetMappingIoScratch(1024, 0);
        var guestSegments = scratch.GuestSegments;
        var totalRead = 0;
        var iovIndex = 0;
        uint iovOffset = 0;

        while (iovIndex < iovs.Count)
        {
            var fileSize = (long)Size;
            var fileRemaining = fileSize - currentOffset;
            if (fileRemaining <= 0)
                break;

            var maxChunkBytes = GetHostMappedBatchBytes(currentOffset, fileRemaining);
            var guestSegmentCount = engine.GatherUserBufferSegments(
                iovs,
                iovIndex,
                iovOffset,
                true,
                maxChunkBytes,
                guestSegments,
                out var bytesResolved,
                out var nextIovIndex,
                out var nextIovOffset);
            if (bytesResolved == 0)
                return nextIovIndex >= iovs.Count ? totalRead : totalRead > 0 ? totalRead : -(int)Errno.EFAULT;

            var pageCount = GetPageCountForRange(currentOffset, bytesResolved);
            scratch.EnsureInstallCapacity(pageCount);
            var resolvedPagePointers = scratch.ResolvedPagePointers.AsSpan(0, pageCount);
            var ensureRc = EnsureMappedPagesForRange(file, mapping, currentOffset, bytesResolved, false, fileSize,
                resolvedPagePointers);
            if (ensureRc < 0)
                return totalRead > 0 ? totalRead : ensureRc;

            var copyRc = CopyMappedPagesToResolvedSegments(
                resolvedPagePointers,
                currentOffset,
                guestSegments.AsSpan(0, guestSegmentCount),
                bytesResolved);
            if (copyRc < 0)
                return totalRead > 0 ? totalRead : copyRc;

            totalRead += bytesResolved;
            currentOffset += bytesResolved;
            iovIndex = nextIovIndex;
            iovOffset = nextIovOffset;
        }

        return totalRead;
    }

    private int WriteVHostMapped(Engine engine, LinuxFile file, ArraySegment<Iovec> iovs, ref long currentOffset)
    {
        var mapping = EnsureMapping();
        var scratch = GetMappingIoScratch(1024, 0);
        var guestSegments = scratch.GuestSegments;
        var totalWritten = 0;
        var iovIndex = 0;
        uint iovOffset = 0;

        while (iovIndex < iovs.Count)
        {
            var maxChunkBytes = GetHostMappedBatchBytes(currentOffset, long.MaxValue);
            var guestSegmentCount = engine.GatherUserBufferSegments(
                iovs,
                iovIndex,
                iovOffset,
                false,
                maxChunkBytes,
                guestSegments,
                out var bytesResolved,
                out var nextIovIndex,
                out var nextIovOffset);
            if (bytesResolved == 0)
                return nextIovIndex >= iovs.Count ? totalWritten : totalWritten > 0 ? totalWritten : -(int)Errno.EFAULT;

            var prepareRc = PrepareHostMappedWrite(file, bytesResolved, currentOffset);
            if (prepareRc < 0)
            {
                if (totalWritten > 0)
                    CompleteHostMappedWrite(totalWritten, currentOffset);
                return totalWritten > 0 ? totalWritten : prepareRc;
            }

            var fileSize = CachedSizeForShrinkCoordinator;
            var pageCount = GetPageCountForRange(currentOffset, bytesResolved);
            scratch.EnsureInstallCapacity(pageCount);
            var resolvedPagePointers = scratch.ResolvedPagePointers.AsSpan(0, pageCount);
            var ensureRc = EnsureMappedPagesForRange(file, mapping, currentOffset, bytesResolved, true, fileSize,
                resolvedPagePointers);
            if (ensureRc < 0)
            {
                if (totalWritten > 0)
                    CompleteHostMappedWrite(totalWritten, currentOffset);
                return totalWritten > 0 ? totalWritten : ensureRc;
            }

            var copyRc = CopyResolvedSegmentsToMappedPages(
                resolvedPagePointers,
                currentOffset,
                guestSegments.AsSpan(0, guestSegmentCount),
                bytesResolved);
            if (copyRc < 0)
            {
                if (totalWritten > 0)
                    CompleteHostMappedWrite(totalWritten, currentOffset);
                return totalWritten > 0 ? totalWritten : copyRc;
            }

            MarkMappedWriteDirty(currentOffset, bytesResolved);

            totalWritten += bytesResolved;
            currentOffset += bytesResolved;
            iovIndex = nextIovIndex;
            iovOffset = nextIovOffset;
        }

        if (totalWritten > 0)
            CompleteHostMappedWrite(totalWritten, currentOffset);

        return totalWritten;
    }

    private void CompleteHostMappedWrite(int totalWritten, long finalOffset)
    {
        if (totalWritten <= 0)
            return;

        if (finalOffset > CachedSizeForShrinkCoordinator)
            CachedSizeForShrinkCoordinator = finalOffset;

        OnWriteCompleted(totalWritten);
    }

    private int GetHostMappedBatchBytes(long offset, long maxLength)
    {
        if (maxLength <= 0)
            return 0;

        var granularity = Math.Max(LinuxConstants.PageSize,
            SuperBlock.MemoryContext.HostMemoryMapGeometry.AllocationGranularity);
        var windowOffset = (int)(Math.Abs(offset) % granularity);
        var bytesUntilBoundary = granularity - windowOffset;
        if (bytesUntilBoundary <= 0)
            bytesUntilBoundary = granularity;

        return (int)Math.Min(Math.Min((long)bytesUntilBoundary, maxLength), int.MaxValue);
    }

    private static int GetPageCountForRange(long offset, int length)
    {
        if (length <= 0)
            return 0;

        var startPage = offset / LinuxConstants.PageSize;
        var endPage = (offset + length - 1) / LinuxConstants.PageSize;
        return checked((int)(endPage - startPage + 1));
    }

    private int EnsureMappedPagesForRange(LinuxFile file, AddressSpace mapping, long offset, int length, bool writable,
        long fileSize)
    {
        return EnsureMappedPagesForRange(file, mapping, offset, length, writable, fileSize, []);
    }

    private int EnsureMappedPagesForRange(LinuxFile file, AddressSpace mapping, long offset, int length, bool writable,
        long fileSize, Span<IntPtr> resolvedPagePointers)
    {
        if (length <= 0)
            return 0;

        var startPage = (uint)(offset / LinuxConstants.PageSize);
        var endPage = (uint)((offset + length - 1) / LinuxConstants.PageSize);
        var totalPageCount = checked((int)(endPage - startPage + 1));
        if (!resolvedPagePointers.IsEmpty && resolvedPagePointers.Length < totalPageCount)
            throw new ArgumentException("Resolved page output span is smaller than requested range.",
                nameof(resolvedPagePointers));
        var pageIndex = startPage;
        while (pageIndex <= endPage)
        {
            if (writable &&
                TryGetMappingPageRecord(pageIndex, out var existingRecord) &&
                !existingRecord.IsWritable)
            {
                var upgradeRc = UpgradeReadonlyMappingPageForWrite(
                    file,
                    mapping,
                    pageIndex,
                    fileSize,
                    resolvedPagePointers.IsEmpty
                        ? []
                        : resolvedPagePointers.Slice((int)(pageIndex - startPage), 1));
                if (upgradeRc < 0)
                    return upgradeRc;

                pageIndex++;
                continue;
            }

            var residentMissCount = mapping.CountLeadingAbsentPages(pageIndex, endPage);
            if (residentMissCount == 0)
            {
                var existingPtr = mapping.PeekPage(pageIndex);
                if (!resolvedPagePointers.IsEmpty)
                    resolvedPagePointers[(int)(pageIndex - startPage)] = existingPtr;
                pageIndex++;
                continue;
            }

            var mappingMissCount = CountLeadingMissingMappingPages(pageIndex, endPage);
            if (mappingMissCount == 0)
            {
                _ = TryGetMappingPageRecord(pageIndex, out var resident);
                var finalPtr = mapping.InstallHostPageIfAbsent(pageIndex, resident.Ptr, ref resident.Handle,
                    resident.HostPageKind, this, resident, out _);
                if (!resolvedPagePointers.IsEmpty)
                    resolvedPagePointers[(int)(pageIndex - startPage)] = finalPtr;
                pageIndex++;
                continue;
            }

            var runCount = Math.Min(residentMissCount, mappingMissCount);
            var installRc = InstallHostMappedPageRun(file, mapping, pageIndex, runCount, writable, fileSize,
                resolvedPagePointers.IsEmpty
                    ? []
                    : resolvedPagePointers.Slice((int)(pageIndex - startPage), runCount));
            if (installRc < 0)
                return installRc;

            pageIndex += (uint)runCount;
        }

        return 0;
    }

    private int InstallHostMappedPageRun(LinuxFile file, AddressSpace mapping, uint startPageIndex, int pageCount,
        bool writable, long fileSize, Span<IntPtr> resolvedPagePointers)
    {
        if (pageCount <= 0)
            return 0;
        if (!resolvedPagePointers.IsEmpty && resolvedPagePointers.Length < pageCount)
            throw new ArgumentException("Resolved page output span is smaller than install run.",
                nameof(resolvedPagePointers));

        var scratch = GetMappingIoScratch(0, pageCount);
        var pointers = scratch.PagePointers;
        var releaseTokens = scratch.ReleaseTokens;
        var records = scratch.Records;
        var finalPointers = scratch.FinalPointers;
        var inserted = scratch.Inserted;
        try
        {
            var acquiredCount = 0;
            try
            {
                acquiredCount = TryAcquireHostMappedPageLeases(file, startPageIndex, pageCount, fileSize, writable,
                    pointers.AsSpan(0, pageCount), releaseTokens.AsSpan(0, pageCount));

                for (var i = 0; i < acquiredCount; i++)
                {
                    var pageIndex = startPageIndex + (uint)i;
                    var record = new InodePageRecord
                    {
                        PageIndex = pageIndex,
                        Ptr = pointers[i],
                        BackingKind = FilePageBackingKind.HostMappedWindow,
                        Handle = BackingPageHandle.CreateOwned(pointers[i], this, releaseTokens[i]),
                        IsWritable = writable
                    };
                    releaseTokens[i] = 0;
                    records[i] = record;
                }

                if (acquiredCount == 1)
                {
                    var record = records[0]!;
                    if (!TryRegisterMappingPage(record.PageIndex, record))
                    {
                        record.ReleaseOwnership();
                        if (TryGetMappingPageRecord(record.PageIndex, out var resident))
                        {
                            var finalPtr = mapping.InstallHostPageIfAbsent(record.PageIndex, resident.Ptr,
                                ref resident.Handle,
                                resident.HostPageKind, this, resident, out _);
                            if (!resolvedPagePointers.IsEmpty)
                                resolvedPagePointers[0] = finalPtr;
                        }

                        records[0] = null;
                    }
                    else
                    {
                        var finalPtr = mapping.InstallHostPageIfAbsent(record.PageIndex, record.Ptr, ref record.Handle,
                            HostPageKind.PageCache, this, record, out var insertedSingle);
                        if (!resolvedPagePointers.IsEmpty)
                            resolvedPagePointers[0] = finalPtr;

                        if (!insertedSingle && finalPtr != record.Ptr)
                        {
                            TryRemoveOwnedMappingPageRecord(record.PageIndex, record);
                            record.ReleaseOwnership();
                            records[0] = null;
                        }
                    }

                    acquiredCount = 0;
                }

                if (acquiredCount > 0)
                {
                    TryRegisterMappingPagesBatch(records.AsSpan(0, acquiredCount), inserted.AsSpan(0, acquiredCount));
                    for (var i = 0; i < acquiredCount; i++)
                    {
                        if (inserted[i])
                            continue;

                        var record = records[i];
                        if (record == null)
                            continue;

                        record.ReleaseOwnership();
                        if (TryGetMappingPageRecord(record.PageIndex, out var resident))
                        {
                            var finalPtr = mapping.InstallHostPageIfAbsent(record.PageIndex, resident.Ptr,
                                ref resident.Handle,
                                resident.HostPageKind, this, resident, out _);
                            if (!resolvedPagePointers.IsEmpty)
                                resolvedPagePointers[i] = finalPtr;
                        }

                        records[i] = null;
                    }
                }

                if (acquiredCount > 0)
                {
                    mapping.InstallHostPageRecordsIfAbsentBatch(records.AsSpan(0, acquiredCount),
                        HostPageKind.PageCache, this, finalPointers.AsSpan(0, acquiredCount),
                        inserted.AsSpan(0, acquiredCount));

                    if (!resolvedPagePointers.IsEmpty)
                    {
                        for (var i = 0; i < acquiredCount; i++)
                        {
                            var finalPtr = finalPointers[i];
                            if (finalPtr == IntPtr.Zero)
                                finalPtr = mapping.PeekPage(startPageIndex + (uint)i);
                            resolvedPagePointers[i] = finalPtr;
                        }
                    }

                    for (var i = 0; i < acquiredCount; i++)
                    {
                        var record = records[i];
                        if (record == null || inserted[i] || finalPointers[i] == record.Ptr)
                            continue;

                        TryRemoveOwnedMappingPageRecord(record.PageIndex, record);
                        record.ReleaseOwnership();
                        records[i] = null;
                    }
                }
            }
            catch
            {
                for (var i = 0; i < acquiredCount; i++)
                {
                    if (releaseTokens[i] == 0)
                        continue;

                    ReleaseHostMappedPageLease(releaseTokens[i]);
                }

                throw;
            }

            for (var i = acquiredCount; i < pageCount; i++)
            {
                var pageIndex = startPageIndex + (uint)i;
                var fileOffset = (long)pageIndex * LinuxConstants.PageSize;
                var prefillLength = writable
                    ? 0
                    : (int)Math.Min(LinuxConstants.PageSize, Math.Max(0, fileSize - fileOffset));
                var pagePtr = AcquireMappingPage(file, pageIndex, fileOffset,
                    writable ? PageCacheAccessMode.Write : PageCacheAccessMode.Read,
                    prefillLength,
                    out var error,
                    true);
                if (pagePtr == IntPtr.Zero)
                    return error != 0 ? error : -(int)Errno.EIO;
                if (!resolvedPagePointers.IsEmpty)
                    resolvedPagePointers[i] = pagePtr;
            }

            return 0;
        }
        finally
        {
            scratch.ClearInstallRecords(pageCount);
        }
    }

    private static unsafe int CopyResolvedSegmentsToMappedPages(ReadOnlySpan<IntPtr> pagePointers, long offset,
        ReadOnlySpan<ResolvedBufferSegment> sourceSegments, int totalBytes)
    {
        if (totalBytes <= 0)
            return 0;

        var remaining = totalBytes;
        var sourceIndex = 0;
        var sourceOffset = 0;
        var pageIndex = 0;
        var pageOffset = (int)(offset & LinuxConstants.PageOffsetMask);
        var pageRemaining = LinuxConstants.PageSize - pageOffset;
        var pagePtr = pagePointers.Length > 0 ? pagePointers[0] : IntPtr.Zero;
        while (remaining > 0)
        {
            if (pagePtr == IntPtr.Zero)
                return -(int)Errno.EIO;

            var source = sourceSegments[sourceIndex];
            var sourceRemaining = source.Length - sourceOffset;
            var chunk = Math.Min(remaining, Math.Min(pageRemaining, sourceRemaining));
            Buffer.MemoryCopy(
                (byte*)source.Pointer + sourceOffset,
                (byte*)pagePtr + pageOffset,
                chunk,
                chunk);

            pageOffset += chunk;
            pageRemaining -= chunk;
            sourceOffset += chunk;
            remaining -= chunk;

            if (sourceOffset >= source.Length)
            {
                sourceIndex++;
                sourceOffset = 0;
            }

            if (pageRemaining == 0 && remaining > 0)
            {
                pageIndex++;
                pageOffset = 0;
                pageRemaining = LinuxConstants.PageSize;
                pagePtr = pageIndex < pagePointers.Length ? pagePointers[pageIndex] : IntPtr.Zero;
            }
        }

        return 0;
    }

    private static unsafe int CopyMappedPagesToResolvedSegments(ReadOnlySpan<IntPtr> pagePointers, long offset,
        ReadOnlySpan<ResolvedBufferSegment> destinationSegments, int totalBytes)
    {
        if (totalBytes <= 0)
            return 0;

        var remaining = totalBytes;
        var destinationIndex = 0;
        var destinationOffset = 0;
        var pageIndex = 0;
        var pageOffset = (int)(offset & LinuxConstants.PageOffsetMask);
        var pageRemaining = LinuxConstants.PageSize - pageOffset;
        var pagePtr = pagePointers.Length > 0 ? pagePointers[0] : IntPtr.Zero;
        while (remaining > 0)
        {
            if (pagePtr == IntPtr.Zero)
                return -(int)Errno.EIO;

            var destination = destinationSegments[destinationIndex];
            var destinationRemaining = destination.Length - destinationOffset;
            var chunk = Math.Min(remaining, Math.Min(pageRemaining, destinationRemaining));
            Buffer.MemoryCopy(
                (byte*)pagePtr + pageOffset,
                (byte*)destination.Pointer + destinationOffset,
                chunk,
                chunk);

            pageOffset += chunk;
            pageRemaining -= chunk;
            destinationOffset += chunk;
            remaining -= chunk;

            if (destinationOffset >= destination.Length)
            {
                destinationIndex++;
                destinationOffset = 0;
            }

            if (pageRemaining == 0 && remaining > 0)
            {
                pageIndex++;
                pageOffset = 0;
                pageRemaining = LinuxConstants.PageSize;
                pagePtr = pageIndex < pagePointers.Length ? pagePointers[pageIndex] : IntPtr.Zero;
            }
        }

        return 0;
    }

    private void MarkMappedWriteDirty(long offset, int length)
    {
        if (length <= 0)
            return;

        var startPage = (uint)(offset / LinuxConstants.PageSize);
        var endPage = (uint)((offset + length - 1) / LinuxConstants.PageSize);
        OnMappingPagesMarkedDirty(startPage, endPage);
        Mapping?.MarkDirtyRange(startPage, endPage);
    }

    #endregion
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
            VfsDebugTrace.FailInvariant(
                $"Dentry untrack failed dentry={Name.ToDebugString()} dentryId={Id} reason={reason}");
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

    private static string RenderRefChangeOperation(InodeRefTraceAction action, InodeRefKind kind)
    {
        return (action, kind) switch
        {
            (InodeRefTraceAction.AcquireRef, InodeRefKind.KernelInternal) => "Inode.AcquireRef.KernelInternal",
            (InodeRefTraceAction.AcquireRef, InodeRefKind.FileOpen) => "Inode.AcquireRef.FileOpen",
            (InodeRefTraceAction.AcquireRef, InodeRefKind.FileMmap) => "Inode.AcquireRef.FileMmap",
            (InodeRefTraceAction.AcquireRef, InodeRefKind.PathPin) => "Inode.AcquireRef.PathPin",
            (InodeRefTraceAction.ReleaseRef, InodeRefKind.KernelInternal) => "Inode.ReleaseRef.KernelInternal",
            (InodeRefTraceAction.ReleaseRef, InodeRefKind.FileOpen) => "Inode.ReleaseRef.FileOpen",
            (InodeRefTraceAction.ReleaseRef, InodeRefKind.FileMmap) => "Inode.ReleaseRef.FileMmap",
            (InodeRefTraceAction.ReleaseRef, InodeRefKind.PathPin) => "Inode.ReleaseRef.PathPin",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
    }

    private static string RenderLinkOperation(InodeLinkTraceOperation operation)
    {
        return operation switch
        {
            InodeLinkTraceOperation.SetInitialLinkCount => "Inode.SetInitialLinkCount",
            InodeLinkTraceOperation.IncLink => "Inode.IncLink",
            InodeLinkTraceOperation.DecLink => "Inode.DecLink",
            InodeLinkTraceOperation.InitDefaultFromIncLink => "Inode.IncLink.InitDefault",
            InodeLinkTraceOperation.InitDefaultFromDecLink => "Inode.DecLink.InitDefault",
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
        };
    }

    private static string RenderLifecycleOperation(InodeLifecycleTraceOperation operation)
    {
        return operation switch
        {
            InodeLifecycleTraceOperation.ReleaseRefKernelInternal => "ReleaseRef.KernelInternal",
            InodeLifecycleTraceOperation.ReleaseRefFileOpen => "ReleaseRef.FileOpen",
            InodeLifecycleTraceOperation.ReleaseRefFileMmap => "ReleaseRef.FileMmap",
            InodeLifecycleTraceOperation.ReleaseRefPathPin => "ReleaseRef.PathPin",
            InodeLifecycleTraceOperation.SetInitialLinkCount => "SetInitialLinkCount",
            InodeLifecycleTraceOperation.DecLink => "DecLink",
            InodeLifecycleTraceOperation.FinalizeReleaseRefKernelInternal => "Finalize.ReleaseRef.KernelInternal",
            InodeLifecycleTraceOperation.FinalizeReleaseRefFileOpen => "Finalize.ReleaseRef.FileOpen",
            InodeLifecycleTraceOperation.FinalizeReleaseRefFileMmap => "Finalize.ReleaseRef.FileMmap",
            InodeLifecycleTraceOperation.FinalizeReleaseRefPathPin => "Finalize.ReleaseRef.PathPin",
            InodeLifecycleTraceOperation.FinalizeSetInitialLinkCount => "Finalize.SetInitialLinkCount",
            InodeLifecycleTraceOperation.FinalizeDecLink => "Finalize.DecLink",
            InodeLifecycleTraceOperation.VfsShrinkerEvictUnusedInodes => "VfsShrinker.EvictUnusedInodes",
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
        };
    }

    [Conditional("VFS_REFTRACE")]
    public static void RecordRefChange(Inode inode, InodeRefTraceAction action, InodeRefKind kind, int before,
        int after, string? detail)
    {
        var operation = RenderRefChangeOperation(action, kind);
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
    public static void RecordLinkChange(Inode inode, InodeLinkTraceOperation operation, int before, int after,
        string? reason)
    {
        var operationText = RenderLinkOperation(operation);
        if (!Enabled) return;
        LogLinkChangeCore(Logger, operationText, inode.Ino, inode.Type, before, after, inode.RefCount,
            inode.Dentries.Count, reason ?? string.Empty);
    }

    [Conditional("VFS_REFTRACE")]
    public static void RecordCacheEvict(Inode inode, InodeLifecycleTraceOperation operation, string? reason)
    {
        var operationText = RenderLifecycleOperation(operation);
        if (!Enabled) return;
        LogCacheEvictCore(Logger, operationText, inode.Ino, inode.Type, inode.LinkCount, inode.RefCount,
            reason ?? string.Empty);
    }

    [Conditional("VFS_REFTRACE")]
    public static void RecordFinalize(Inode inode, InodeLifecycleTraceOperation operation, string? reason)
    {
        var operationText = RenderLifecycleOperation(operation);
        if (!Enabled) return;
        LogFinalizeCore(Logger, operationText, inode.Ino, inode.Type, inode.LinkCount, inode.RefCount,
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
        var refKind = referenceKind == ReferenceKind.MmapHold ? InodeRefKind.FileMmap : InodeRefKind.FileOpen;
        var holderRegistered = false;
        var inodeRefAcquired = false;
        try
        {
            VfsFileHolderTracking.Register(OpenedInode, this, $"LinuxFile.ctor/{referenceKind}");
            holderRegistered = true;
            OpenedInode?.AcquireRef(refKind, "LinuxFile.ctor");
            inodeRefAcquired = OpenedInode != null;
            OpenedInode?.Open(this);
        }
        catch
        {
            try
            {
                OpenedInode?.Release(this);
            }
            catch
            {
                // Preserve the original open failure; cleanup is best-effort.
            }

            if (inodeRefAcquired)
                OpenedInode!.ReleaseRef(refKind, "LinuxFile.ctor.failed");

            if (holderRegistered)
                VfsFileHolderTracking.Unregister(OpenedInode, this);

            Dentry.Put("LinuxFile.ctor.failed");
            throw;
        }
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
            : Dentry != null
                ? Dentry.Name.ToDebugString()
                : "<unknown>";

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
