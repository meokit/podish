using Fiberish.Core;
using Fiberish.Native;
using System.Runtime.CompilerServices;

namespace Fiberish.Memory;

/// <summary>
///     Global page-cache policy manager.
///     Maintenance runs at syscall safe-points to avoid concurrent VM mutation.
/// </summary>
public static class GlobalPageCacheManager
{
    private static readonly object Gate = new();
    private static readonly Dictionary<int, WeakReference<MemoryObject>> TrackedCaches = [];
    private static long _nextMaintenanceTicks;

    public static TimeSpan WritebackInterval { get; set; } = TimeSpan.FromSeconds(5);
    public static long HighWatermarkBytes { get; set; } = 256L * 1024 * 1024;
    public static long LowWatermarkBytes { get; set; } = 192L * 1024 * 1024;

    public static void TrackPageCache(MemoryObject pageCache)
    {
        lock (Gate)
        {
            TrackedCaches[RuntimeHelpers.GetHashCode(pageCache)] = new WeakReference<MemoryObject>(pageCache);
        }
    }

    public static void MaybeRunMaintenance(VMAManager mm, Engine engine)
    {
        if (engine == null || engine.State == IntPtr.Zero) return;
        var now = DateTime.UtcNow.Ticks;
        lock (Gate)
        {
            if (now < _nextMaintenanceTicks) return;
            _nextMaintenanceTicks = now + WritebackInterval.Ticks;
        }

        // 1) Periodic writeback of mmap-shared dirty pages.
        try
        {
            mm.SyncAllMappedSharedFiles(engine);
        }
        catch
        {
            // Best-effort policy pass: syscall path must not fail because of maintenance.
        }

        // 2) Best-effort cache reclaim under memory pressure.
        try
        {
            ReclaimIfNeeded();
        }
        catch
        {
            // Best-effort policy pass: ignore reclaim errors.
        }
    }

    private static void ReclaimIfNeeded()
    {
        List<MemoryObject> caches;
        lock (Gate)
        {
            caches = new List<MemoryObject>(TrackedCaches.Count);
            var deadKeys = new List<int>();
            foreach (var (key, weak) in TrackedCaches)
            {
                if (weak.TryGetTarget(out var cache))
                    caches.Add(cache);
                else
                    deadKeys.Add(key);
            }

            foreach (var dead in deadKeys) TrackedCaches.Remove(dead);
        }

        if (caches.Count == 0) return;

        long totalBytes = 0;
        foreach (var cache in caches) totalBytes += (long)cache.PageCount * LinuxConstants.PageSize;
        if (totalBytes <= HighWatermarkBytes) return;

        var targetFree = totalBytes - LowWatermarkBytes;
        if (targetFree <= 0) return;

        var candidates = new List<(MemoryObject Cache, uint PageIndex, long LastAccessTicks)>();
        foreach (var cache in caches)
        {
            var states = cache.SnapshotPageStates();
            foreach (var state in states)
            {
                if (state.Dirty) continue;
                candidates.Add((cache, state.PageIndex, state.LastAccessTicks));
            }
        }

        if (candidates.Count == 0) return;
        candidates.Sort(static (a, b) => a.LastAccessTicks.CompareTo(b.LastAccessTicks));

        long freed = 0;
        foreach (var candidate in candidates)
        {
            if (candidate.Cache.TryEvictCleanPage(candidate.PageIndex))
            {
                freed += LinuxConstants.PageSize;
                if (freed >= targetFree) break;
            }
        }
    }
}
