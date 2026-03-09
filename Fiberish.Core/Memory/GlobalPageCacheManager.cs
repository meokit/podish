using Fiberish.Core;
using Fiberish.Native;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Fiberish.Memory;

/// <summary>
///     Global page-cache policy manager.
///     Maintenance runs at syscall safe-points to avoid concurrent VM mutation.
/// </summary>
public static class GlobalPageCacheManager
{
    private sealed class State
    {
        public readonly object Gate = new();
        public readonly Dictionary<int, TrackedEntry> TrackedCaches = [];
        public long NextMaintenanceTicks;
        public long WritebackPagesInFlight;
        public TimeSpan WritebackInterval = TimeSpan.FromSeconds(5);
        public long HighWatermarkBytes = 256L * 1024 * 1024;
        public long LowWatermarkBytes = 192L * 1024 * 1024;
    }

    private sealed class ScopeRestore : IDisposable
    {
        private readonly State? _previous;

        public ScopeRestore(State? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            ScopedState.Value = _previous;
        }
    }

    public enum PageCacheClass
    {
        File,
        Shmem
    }

    private readonly record struct TrackedEntry(WeakReference<MemoryObject> Cache, PageCacheClass Class);

    private static readonly AsyncLocal<State?> ScopedState = new();
    private static readonly State DefaultState = new();
    private static State CurrentState => ScopedState.Value ?? DefaultState;

    public static TimeSpan WritebackInterval
    {
        get => CurrentState.WritebackInterval;
        set => CurrentState.WritebackInterval = value;
    }

    public static long HighWatermarkBytes
    {
        get => CurrentState.HighWatermarkBytes;
        set => CurrentState.HighWatermarkBytes = value;
    }

    public static long LowWatermarkBytes
    {
        get => CurrentState.LowWatermarkBytes;
        set => CurrentState.LowWatermarkBytes = value;
    }

    public static IDisposable BeginIsolatedScope()
    {
        var previous = ScopedState.Value;
        ScopedState.Value = new State();
        return new ScopeRestore(previous);
    }

    public static void TrackPageCache(MemoryObject pageCache, PageCacheClass cacheClass = PageCacheClass.File)
    {
        var state = CurrentState;
        lock (state.Gate)
        {
            state.TrackedCaches[RuntimeHelpers.GetHashCode(pageCache)] =
                new TrackedEntry(new WeakReference<MemoryObject>(pageCache), cacheClass);
        }
    }

    public static void MaybeRunMaintenance(VMAManager mm, Engine engine)
    {
        if (engine == null || engine.State == IntPtr.Zero) return;
        var state = CurrentState;
        var now = DateTime.UtcNow.Ticks;
        lock (state.Gate)
        {
            if (now < state.NextMaintenanceTicks) return;
            state.NextMaintenanceTicks = now + state.WritebackInterval.Ticks;
        }

        // 1) Periodic writeback of mmap-shared dirty pages.
        try
        {
            ProcessAddressSpaceSync.SyncAllMappedSharedFiles(mm, engine);
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

    public readonly record struct CacheStats(long TotalPages, long CleanPages, long DirtyPages, long ShmemPages,
        long WritebackPages);
    public readonly record struct CachePageState(PageCacheClass Class, bool Dirty, long LastAccessTicks);

    public static CacheStats GetCacheStats()
    {
        var state = CurrentState;
        var caches = GetLiveCaches(state);
        long total = 0;
        long dirty = 0;
        long shmem = 0;
        foreach (var (cache, cacheClass) in caches)
        {
            var states = cache.SnapshotPageStates();
            total += states.Count;
            if (cacheClass == PageCacheClass.Shmem) shmem += states.Count;
            foreach (var pageState in states)
            {
                if (pageState.Dirty) dirty++;
            }
        }

        return new CacheStats(total, total - dirty, dirty, shmem, Interlocked.Read(ref state.WritebackPagesInFlight));
    }

    public static IReadOnlyList<CachePageState> GetPageStatesSnapshot()
    {
        var caches = GetLiveCaches(CurrentState);
        if (caches.Count == 0) return Array.Empty<CachePageState>();

        var states = new List<CachePageState>();
        foreach (var (cache, cacheClass) in caches)
        {
            var cacheStates = cache.SnapshotPageStates();
            foreach (var state in cacheStates)
            {
                states.Add(new CachePageState(cacheClass, state.Dirty, state.LastAccessTicks));
            }
        }

        return states;
    }

    public static void BeginWritebackPages(int pages = 1)
    {
        if (pages <= 0) return;
        var state = CurrentState;
        Interlocked.Add(ref state.WritebackPagesInFlight, pages);
    }

    public static void EndWritebackPages(int pages = 1)
    {
        if (pages <= 0) return;
        var state = CurrentState;
        var current = Interlocked.Add(ref state.WritebackPagesInFlight, -pages);
        if (current >= 0) return;
        Interlocked.Exchange(ref state.WritebackPagesInFlight, 0);
    }

    public static long TryReclaimBytes(long targetBytes)
    {
        if (targetBytes <= 0) return 0;
        var caches = GetLiveCaches(CurrentState);
        return ReclaimFromCaches(caches, targetBytes);
    }

    private static void ReclaimIfNeeded()
    {
        var state = CurrentState;
        var caches = GetLiveCaches(state);

        if (caches.Count == 0) return;

        long totalBytes = 0;
        foreach (var (cache, _) in caches) totalBytes += (long)cache.PageCount * LinuxConstants.PageSize;
        if (totalBytes <= state.HighWatermarkBytes) return;

        var targetFree = totalBytes - state.LowWatermarkBytes;
        if (targetFree <= 0) return;
        ReclaimFromCaches(caches, targetFree);
    }

    private static List<(MemoryObject Cache, PageCacheClass Class)> GetLiveCaches(State state)
    {
        lock (state.Gate)
        {
            var caches = new List<(MemoryObject Cache, PageCacheClass Class)>(state.TrackedCaches.Count);
            var deadKeys = new List<int>();
            foreach (var (key, entry) in state.TrackedCaches)
            {
                if (entry.Cache.TryGetTarget(out var cache))
                    caches.Add((cache, entry.Class));
                else
                    deadKeys.Add(key);
            }

            foreach (var dead in deadKeys) state.TrackedCaches.Remove(dead);
            return caches;
        }
    }

    private static long ReclaimFromCaches(List<(MemoryObject Cache, PageCacheClass Class)> caches, long targetFreeBytes)
    {
        if (targetFreeBytes <= 0) return 0;
        var candidates = new List<(MemoryObject Cache, uint PageIndex, long LastAccessTicks)>();
        foreach (var (cache, cacheClass) in caches)
        {
            if (cacheClass == PageCacheClass.Shmem) continue;
            var states = cache.SnapshotPageStates();
            foreach (var state in states)
            {
                if (state.Dirty) continue;
                candidates.Add((cache, state.PageIndex, state.LastAccessTicks));
            }
        }

        if (candidates.Count == 0) return 0;
        candidates.Sort(static (a, b) => a.LastAccessTicks.CompareTo(b.LastAccessTicks));

        long freed = 0;
        foreach (var candidate in candidates)
        {
            if (candidate.Cache.TryEvictCleanPage(candidate.PageIndex))
            {
                freed += LinuxConstants.PageSize;
                if (freed >= targetFreeBytes) break;
            }
        }

        return freed;
    }
}
