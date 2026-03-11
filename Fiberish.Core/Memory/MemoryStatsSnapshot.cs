using Fiberish.Core;
using Fiberish.Native;
using Fiberish.Syscalls;

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
    long InactiveFileBytes)
{
    private static readonly long ActiveThresholdTicks = TimeSpan.FromSeconds(30).Ticks;

    public static MemoryStatsSnapshot Capture(SyscallManager? sm = null)
    {
        var allocated = ExternalPageManager.GetAllocatedBytes();
        var cache = GlobalPageCacheManager.GetCacheStats();
        var cacheStates = GlobalPageCacheManager.GetPageStatesSnapshot();
        var cachedBytes = (cache.TotalPages - cache.AnonSharedSourcePages) * LinuxConstants.PageSize;
        var dirtyBytes = cache.DirtyPages * LinuxConstants.PageSize;
        var reclaimable = cache.CleanPages * LinuxConstants.PageSize;
        var nowTicks = DateTime.UtcNow.Ticks;
        var privateBreakdown = EstimatePrivateBreakdown(sm, nowTicks);
        var anonymousZeroMappedBytes = EstimateAnonymousZeroMappedBytes(sm);
        var anonymousSharedMaterializedBytes = cache.AnonSharedSourcePages * LinuxConstants.PageSize;
        var privateOverlayBytes = privateBreakdown.TotalAnon + privateBreakdown.TotalFilePrivate;
        var anonBytes = Math.Max(0, allocated - cachedBytes) + anonymousSharedMaterializedBytes;
        var shmemCacheBytes = cache.ShmemPages * LinuxConstants.PageSize;
        var writebackBytes = cache.WritebackPages * LinuxConstants.PageSize;
        var committedBytes = EstimateCommittedBytes(sm);
        var sysvShmBytes = EstimateSysVShmBytes(sm);
        var shmemBytes = shmemCacheBytes + sysvShmBytes;
        var (activeFile, inactiveFile, activeShmem, inactiveShmem) = SplitCacheByAge(cacheStates, nowTicks);
        var activeAnon = privateBreakdown.ActiveAnon + privateBreakdown.ActiveFilePrivate;
        var inactiveAnon = privateBreakdown.InactiveAnon + privateBreakdown.InactiveFilePrivate;
        var active = activeFile + activeShmem + activeAnon;
        var inactive = inactiveFile + inactiveShmem + inactiveAnon;

        var quota = ExternalPageManager.MemoryQuotaBytes;
        var total = quota > 0 ? quota : Math.Max(allocated, 256L * 1024 * 1024);
        var free = Math.Max(0, total - allocated);
        // Simplified MemAvailable heuristic: free + reclaimable cache - dirty/writeback pressure.
        var pressurePenalty = dirtyBytes + writebackBytes;
        var available = Math.Min(total, Math.Max(0, free + reclaimable - pressurePenalty));

        return new MemoryStatsSnapshot(
            MemTotalBytes: total,
            UsedBytes: allocated,
            FreeBytes: free,
            MemAvailableBytes: available,
            CachedBytes: cachedBytes,
            DirtyBytes: dirtyBytes,
            AnonPagesBytes: anonBytes,
            MappedBytes: allocated,
            ReclaimableBytes: reclaimable,
            ShmemBytes: shmemBytes,
            WritebackBytes: writebackBytes,
            CommittedBytes: committedBytes,
            ActiveBytes: active,
            InactiveBytes: inactive,
            ActiveAnonBytes: activeAnon,
            InactiveAnonBytes: inactiveAnon,
            AnonymousZeroMappedBytes: anonymousZeroMappedBytes,
            AnonymousSharedMaterializedBytes: anonymousSharedMaterializedBytes,
            UnreclaimablePrivateOverlayBytes: privateOverlayBytes,
            PrivateAnonBytes: privateBreakdown.TotalAnon,
            PrivateFileBytes: privateBreakdown.TotalFilePrivate,
            ActivePrivateAnonBytes: privateBreakdown.ActiveAnon,
            InactivePrivateAnonBytes: privateBreakdown.InactiveAnon,
            ActivePrivateFileBytes: privateBreakdown.ActiveFilePrivate,
            InactivePrivateFileBytes: privateBreakdown.InactiveFilePrivate,
            ActiveFileBytes: activeFile + activeShmem,
            InactiveFileBytes: inactiveFile + inactiveShmem);
    }

    private static long EstimateCommittedBytes(SyscallManager? sm)
    {
        var processes = ResolveProcesses(sm);
        long committed = 0;
        foreach (var process in processes)
        {
            foreach (var vma in process.Mem.VMAs)
            {
                // Simplified committed-as model: private mappings potentially consume private memory.
                if ((vma.Flags & MapFlags.Private) == 0) continue;
                committed += vma.Length;
            }
        }

        return committed;
    }

    private static long EstimateSysVShmBytes(SyscallManager? sm)
    {
        if (sm == null) return 0;
        return sm.SysVShm.GetResidentBytesSnapshot();
    }

    private static long EstimateAnonymousZeroMappedBytes(SyscallManager? sm)
    {
        var processes = ResolveProcesses(sm);
        if (processes.Count == 0) return 0;

        long bytes = 0;
        foreach (var process in processes)
        {
            foreach (var vma in process.Mem.VMAs)
            {
                if ((vma.Flags & MapFlags.Private) == 0) continue;
                if (vma.File != null) continue;
                if (vma.SharedObject.Role != MemoryObjectRole.AnonSharedSourceZeroFill) continue;

                var pageCount = vma.Length / LinuxConstants.PageSize;
                for (uint i = 0; i < pageCount; i++)
                {
                    var pageIndex = vma.ViewPageOffset + i;
                    if (vma.SharedObject.GetPage(pageIndex) != IntPtr.Zero) continue;
                    if (vma.PrivateObject?.GetPage(pageIndex) != IntPtr.Zero) continue;
                    bytes += LinuxConstants.PageSize;
                }
            }
        }

        return bytes;
    }

    private static IReadOnlyList<Process> ResolveProcesses(SyscallManager? sm)
    {
        var ownerTask = sm?.Engine?.Owner as FiberTask;
        var scheduler = ownerTask?.CommonKernel ?? KernelScheduler.Current;
        if (scheduler == null) return Array.Empty<Process>();
        return scheduler.GetProcessesSnapshot();
    }

    private static (long ActiveFile, long InactiveFile, long ActiveShmem, long InactiveShmem) SplitCacheByAge(
        IReadOnlyList<GlobalPageCacheManager.CachePageState> cacheStates, long nowTicks)
    {
        long activeFile = 0;
        long inactiveFile = 0;
        long activeShmem = 0;
        long inactiveShmem = 0;
        foreach (var state in cacheStates)
        {
            var active = nowTicks - state.LastAccessTicks <= ActiveThresholdTicks;
            if (state.Class == GlobalPageCacheManager.PageCacheClass.Shmem)
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

    private readonly record struct PrivateBreakdown(
        long TotalAnon,
        long TotalFilePrivate,
        long ActiveAnon,
        long InactiveAnon,
        long ActiveFilePrivate,
        long InactiveFilePrivate);

    private static PrivateBreakdown EstimatePrivateBreakdown(SyscallManager? sm, long nowTicks)
    {
        var processes = ResolveProcesses(sm);
        if (processes.Count == 0) return default;

        var seenPtrs = new HashSet<nint>();
        long totalAnon = 0;
        long totalFilePrivate = 0;
        long activeAnon = 0;
        long inactiveAnon = 0;
        long activeFilePrivate = 0;
        long inactiveFilePrivate = 0;
        foreach (var process in processes)
        {
            foreach (var vma in process.Mem.VMAs)
            {
                if ((vma.Flags & MapFlags.Private) == 0) continue;

                if (vma.File == null)
                {
                    foreach (var state in vma.SharedObject.SnapshotPageStates())
                    {
                        var key = (nint)state.Ptr;
                        if (!seenPtrs.Add(key)) continue;
                        totalAnon += LinuxConstants.PageSize;
                        if (nowTicks - state.LastAccessTicks <= ActiveThresholdTicks) activeAnon += LinuxConstants.PageSize;
                        else inactiveAnon += LinuxConstants.PageSize;
                    }
                }

                if (vma.PrivateObject == null) continue;
                foreach (var state in vma.PrivateObject.SnapshotPageStates())
                {
                    var key = (nint)state.Ptr;
                    if (!seenPtrs.Add(key)) continue;
                    if (vma.File == null)
                    {
                        totalAnon += LinuxConstants.PageSize;
                        if (nowTicks - state.LastAccessTicks <= ActiveThresholdTicks) activeAnon += LinuxConstants.PageSize;
                        else inactiveAnon += LinuxConstants.PageSize;
                    }
                    else
                    {
                        totalFilePrivate += LinuxConstants.PageSize;
                        if (nowTicks - state.LastAccessTicks <= ActiveThresholdTicks)
                            activeFilePrivate += LinuxConstants.PageSize;
                        else
                            inactiveFilePrivate += LinuxConstants.PageSize;
                    }
                }
            }
        }

        return new PrivateBreakdown(
            totalAnon,
            totalFilePrivate,
            activeAnon,
            inactiveAnon,
            activeFilePrivate,
            inactiveFilePrivate);
    }
}
