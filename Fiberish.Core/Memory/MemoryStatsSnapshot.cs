using Fiberish.Core;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;

namespace Fiberish.Memory;

public readonly record struct MemoryStatsSnapshot(
    long MemTotalBytes,
    long UsedBytes,
    long FreeBytes,
    long MemAvailableBytes,
    long CachedBytes,
    long DirtyBytes,
    long AnonPagesBytes,
    long MappedBytes,
    long ReclaimableBytes,
    long ShmemBytes,
    long WritebackBytes,
    long CommittedBytes,
    long ActiveBytes,
    long InactiveBytes,
    long ActiveAnonBytes,
    long InactiveAnonBytes,
    long AnonymousZeroMappedBytes,
    long AnonymousSharedMaterializedBytes,
    long UnreclaimablePrivateOverlayBytes,
    long PrivateAnonBytes,
    long PrivateFileBytes,
    long ActivePrivateAnonBytes,
    long InactivePrivateAnonBytes,
    long ActivePrivateFileBytes,
    long InactivePrivateFileBytes,
    long ActiveFileBytes,
    long InactiveFileBytes,
    long HostMappedWindowBytes,
    int HostMappedWindowCount,
    int HostMappedGuestPageCount)
{
    private static readonly long ActiveThresholdTimestampDelta =
        MonotonicTime.ToTimestampDelta(TimeSpan.FromSeconds(30));

    internal static MemoryStatsSnapshot CreateForRuntime(MemoryRuntimeContext memoryContext, SyscallManager? sm = null)
    {
        ArgumentNullException.ThrowIfNull(memoryContext);

        var cache = memoryContext.AddressSpacePolicy.GetAddressSpaceStats();
        var cacheStates = memoryContext.AddressSpacePolicy.GetAddressSpacePageStatesSnapshot();
        var hostMapped = AggregateHostMappedCacheStats(sm);
        var cachedBytes = cache.TotalPages * LinuxConstants.PageSize;
        var anonymousAllocated = memoryContext.GetAllocatedBytes();
        var allocated = anonymousAllocated + cachedBytes;
        var dirtyBytes = cache.DirtyPages * LinuxConstants.PageSize;
        var reclaimable = cache.CleanPages * LinuxConstants.PageSize;
        var nowTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        var processStats = AggregateProcessMemoryStats(sm, nowTimestamp);
        var privateBreakdown = processStats.PrivateBreakdown;
        var anonymousZeroMappedBytes = processStats.AnonymousZeroMappedBytes;
        const long anonymousSharedMaterializedBytes = 0;
        var privateOverlayBytes = privateBreakdown.TotalAnon + privateBreakdown.TotalFilePrivate;
        var anonBytes = anonymousAllocated + anonymousSharedMaterializedBytes;
        var shmemCacheBytes = cache.ShmemPages * LinuxConstants.PageSize;
        var writebackBytes = cache.WritebackPages * LinuxConstants.PageSize;
        var committedBytes = processStats.CommittedBytes;
        var sysvShmBytes = EstimateSysVShmBytes(sm);
        var shmemBytes = shmemCacheBytes + sysvShmBytes;
        var (activeFile, inactiveFile, activeShmem, inactiveShmem) = SplitCacheByAge(cacheStates, nowTimestamp);
        var activeAnon = privateBreakdown.ActiveAnon + privateBreakdown.ActiveFilePrivate;
        var inactiveAnon = privateBreakdown.InactiveAnon + privateBreakdown.InactiveFilePrivate;
        var active = activeFile + activeShmem + activeAnon;
        var inactive = inactiveFile + inactiveShmem + inactiveAnon;

        var quota = memoryContext.MemoryQuotaBytes;
        var total = quota > 0 ? quota : Math.Max(allocated, 256L * 1024 * 1024);
        var free = Math.Max(0, total - allocated);
        // Simplified MemAvailable heuristic: free + reclaimable cache - dirty/writeback pressure.
        var pressurePenalty = dirtyBytes + writebackBytes;
        var available = Math.Min(total, Math.Max(0, free + reclaimable - pressurePenalty));

        return new MemoryStatsSnapshot(
            total,
            allocated,
            free,
            available,
            cachedBytes,
            dirtyBytes,
            anonBytes,
            allocated,
            reclaimable,
            shmemBytes,
            writebackBytes,
            committedBytes,
            active,
            inactive,
            activeAnon,
            inactiveAnon,
            anonymousZeroMappedBytes,
            anonymousSharedMaterializedBytes,
            privateOverlayBytes,
            privateBreakdown.TotalAnon,
            privateBreakdown.TotalFilePrivate,
            privateBreakdown.ActiveAnon,
            privateBreakdown.InactiveAnon,
            privateBreakdown.ActiveFilePrivate,
            privateBreakdown.InactiveFilePrivate,
            activeFile + activeShmem,
            inactiveFile + inactiveShmem,
            hostMapped.WindowBytes,
            hostMapped.WindowCount,
            hostMapped.GuestPageCount);
    }

    private static FilePageBackendDiagnostics AggregateHostMappedCacheStats(SyscallManager? sm)
    {
        if (sm == null)
            return default;

        var superblocks = new HashSet<SuperBlock>();
        foreach (var mount in sm.Mounts)
            if (mount?.SB != null)
                superblocks.Add(mount.SB);

        var seen = new HashSet<Inode>();
        long windowBytes = 0;
        var windowCount = 0;
        var guestPageCount = 0;
        foreach (var sb in superblocks)
        {
            List<Inode> inodes;
            lock (sb.Lock)
            {
                inodes = sb.Inodes.ToList();
            }

            foreach (var inode in inodes)
            {
                if (!seen.Add(inode))
                    continue;
                if (inode is not IHostMappedCacheDropper mappedCacheDropper)
                    continue;

                var diagnostics = mappedCacheDropper.GetMappedCacheDiagnostics();
                windowBytes += diagnostics.WindowBytes;
                windowCount += diagnostics.WindowCount;
                guestPageCount += diagnostics.GuestPageCount;
            }
        }

        return new FilePageBackendDiagnostics(windowCount, windowBytes, guestPageCount);
    }

    private static long EstimateSysVShmBytes(SyscallManager? sm)
    {
        if (sm == null) return 0;
        return sm.SysVShm.GetResidentBytesSnapshot();
    }

    private static IReadOnlyList<Process> ResolveProcesses(SyscallManager? sm)
    {
        var ownerTask = sm?.CurrentTask;
        var scheduler = ownerTask?.CommonKernel;
        if (scheduler == null) return Array.Empty<Process>();
        return scheduler.GetProcessesSnapshot();
    }

    private static (long ActiveFile, long InactiveFile, long ActiveShmem, long InactiveShmem) SplitCacheByAge(
        IReadOnlyList<AddressSpacePolicy.AddressSpacePageState> cacheStates, long nowTimestamp)
    {
        long activeFile = 0;
        long inactiveFile = 0;
        long activeShmem = 0;
        long inactiveShmem = 0;
        foreach (var state in cacheStates)
        {
            var active = nowTimestamp - state.LastAccessTimestamp <= ActiveThresholdTimestampDelta;
            if (state.Class == AddressSpacePolicy.AddressSpaceCacheClass.Shmem)
            {
                if (active) activeShmem += LinuxConstants.PageSize;
                else inactiveShmem += LinuxConstants.PageSize;
            }
            else
            {
                if (active) activeFile += LinuxConstants.PageSize;
                else inactiveFile += LinuxConstants.PageSize;
            }
        }

        return (activeFile, inactiveFile, activeShmem, inactiveShmem);
    }

    private static ProcessMemoryStats AggregateProcessMemoryStats(SyscallManager? sm, long nowTimestamp)
    {
        var processes = ResolveProcesses(sm);
        if (processes.Count == 0) return default;

        var seenPtrs = new HashSet<nint>();
        long committedBytes = 0;
        long anonymousZeroMappedBytes = 0;
        long totalAnon = 0;
        long totalFilePrivate = 0;
        long activeAnon = 0;
        long inactiveAnon = 0;
        long activeFilePrivate = 0;
        long inactiveFilePrivate = 0;
        foreach (var process in processes)
        foreach (var vma in process.Mem.VMAs)
        {
            if ((vma.Flags & MapFlags.Private) == 0) continue;
            committedBytes += vma.Length;

            if (vma.File == null)
            {
                var privateStates = vma.VmAnonVma?.SnapshotPageStates() ?? Array.Empty<VmPageState>();
                var startPageIndex = vma.GetPageIndex(vma.Start);
                var endPageIndex = startPageIndex + vma.Length / LinuxConstants.PageSize;
                var privatePages = vma.VmAnonVma?.CountPagesInRange(startPageIndex, endPageIndex) ?? 0;
                var zeroPages = Math.Max(0L, endPageIndex - startPageIndex - privatePages);
                anonymousZeroMappedBytes += zeroPages * LinuxConstants.PageSize;

                foreach (var state in privateStates)
                {
                    var key = state.Ptr;
                    if (!seenPtrs.Add(key)) continue;
                    totalAnon += LinuxConstants.PageSize;
                    if (nowTimestamp - state.LastAccessTimestamp <= ActiveThresholdTimestampDelta)
                        activeAnon += LinuxConstants.PageSize;
                    else inactiveAnon += LinuxConstants.PageSize;
                }

                continue;
            }

            if (vma.VmAnonVma == null) continue;
            foreach (var state in vma.VmAnonVma.SnapshotPageStates())
            {
                var key = state.Ptr;
                if (!seenPtrs.Add(key)) continue;
                totalFilePrivate += LinuxConstants.PageSize;
                if (nowTimestamp - state.LastAccessTimestamp <= ActiveThresholdTimestampDelta)
                    activeFilePrivate += LinuxConstants.PageSize;
                else
                    inactiveFilePrivate += LinuxConstants.PageSize;
            }
        }

        return new ProcessMemoryStats(
            committedBytes,
            anonymousZeroMappedBytes,
            new PrivateBreakdown(
                totalAnon,
                totalFilePrivate,
                activeAnon,
                inactiveAnon,
                activeFilePrivate,
                inactiveFilePrivate));
    }

    private readonly record struct PrivateBreakdown(
        long TotalAnon,
        long TotalFilePrivate,
        long ActiveAnon,
        long InactiveAnon,
        long ActiveFilePrivate,
        long InactiveFilePrivate);

    private readonly record struct ProcessMemoryStats(
        long CommittedBytes,
        long AnonymousZeroMappedBytes,
        PrivateBreakdown PrivateBreakdown);
}
