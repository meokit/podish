using System.Runtime.CompilerServices;
using Fiberish.Core;
using Fiberish.Native;

namespace Fiberish.Memory;

/// <summary>
///     Global address_space cache policy manager.
///     Maintenance runs at syscall safe-points to avoid concurrent VM mutation.
/// </summary>
public static class AddressSpacePolicy
{
    public enum AddressSpaceCacheClass
    {
        File,
        Shmem
    }

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

    public static void TrackAddressSpace(AddressSpace mapping,
        AddressSpaceCacheClass cacheClass = AddressSpaceCacheClass.File)
    {
        var state = CurrentState;
        var key = RuntimeHelpers.GetHashCode(mapping);
        var initialPageCount = mapping.PageCount;
        var entry = new TrackedEntry(new WeakReference<AddressSpace>(mapping), cacheClass, initialPageCount);
        TrackedEntry? replaced = null;
        lock (state.Gate)
        {
            if (state.TrackedCaches.TryGetValue(key, out var existing) &&
                existing.Cache.TryGetTarget(out var existingCache) &&
                ReferenceEquals(existingCache, mapping))
                return;

            if (state.TrackedCaches.TryGetValue(key, out existing))
                replaced = existing;
            state.TrackedCaches[key] = entry;
        }

        if (replaced != null)
        {
            var replacedPageCount = Interlocked.Read(ref replaced.PageCount);
            Interlocked.Add(ref state.TotalTrackedPages, -replacedPageCount);
            if (replaced.Class == AddressSpaceCacheClass.Shmem)
                Interlocked.Add(ref state.ShmemTrackedPages, -replacedPageCount);
        }

        Interlocked.Add(ref state.TotalTrackedPages, initialPageCount);
        if (cacheClass == AddressSpaceCacheClass.Shmem)
            Interlocked.Add(ref state.ShmemTrackedPages, initialPageCount);

        mapping.SetTrackedPageCountDeltaCallback(delta => OnTrackedPageCountDelta(state, entry, delta));
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

    public static AddressSpaceStats GetAddressSpaceStats()
    {
        var state = CurrentState;
        long dirty = 0;
        lock (state.Gate)
        {
            var deadKeys = state.DeadCacheKeysScratch;
            deadKeys.Clear();
            foreach (var (key, entry) in state.TrackedCaches)
            {
                if (!entry.Cache.TryGetTarget(out var cache))
                {
                    deadKeys.Add(key);
                    continue;
                }

                cache.GetPageStats(out var totalPages, out var dirtyPages);
                dirty += dirtyPages;
            }

            RemoveDeadCachesLocked(state, deadKeys);
        }

        var total = Interlocked.Read(ref state.TotalTrackedPages);
        var shmem = Interlocked.Read(ref state.ShmemTrackedPages);
        return new AddressSpaceStats(
            total,
            total - dirty,
            dirty,
            shmem,
            Interlocked.Read(ref state.WritebackPagesInFlight));
    }

    public static long GetTotalCachedPages()
    {
        return GetTrackedPageCounts().TotalPages;
    }

    public static AddressSpaceTrackedPageCounts GetTrackedPageCounts()
    {
        var state = CurrentState;
        return new AddressSpaceTrackedPageCounts(
            Interlocked.Read(ref state.TotalTrackedPages),
            Interlocked.Read(ref state.ShmemTrackedPages));
    }

    public static IReadOnlyList<AddressSpacePageState> GetAddressSpacePageStatesSnapshot()
    {
        var caches = GetLiveCaches(CurrentState);
        if (caches.Count == 0) return Array.Empty<AddressSpacePageState>();

        var states = new List<AddressSpacePageState>();
        foreach (var (cache, cacheClass) in caches)
        {
            var cacheStates = cache.SnapshotPageStates();
            foreach (var state in cacheStates)
                states.Add(new AddressSpacePageState(cacheClass, state.Dirty, state.LastAccessTimestamp));
        }

        return states;
    }

    public static void BeginAddressSpaceWriteback(int pages = 1)
    {
        if (pages <= 0) return;
        var state = CurrentState;
        Interlocked.Add(ref state.WritebackPagesInFlight, pages);
    }

    public static void EndAddressSpaceWriteback(int pages = 1)
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

        var totalBytes = Interlocked.Read(ref state.TotalTrackedPages) * LinuxConstants.PageSize;
        if (totalBytes <= state.HighWatermarkBytes) return;

        var targetFree = totalBytes - state.LowWatermarkBytes;
        if (targetFree <= 0) return;
        ReclaimFromCaches(caches, targetFree);
    }

    private static List<(AddressSpace Cache, AddressSpaceCacheClass Class)> GetLiveCaches(State state)
    {
        lock (state.Gate)
        {
            var caches = new List<(AddressSpace Cache, AddressSpaceCacheClass Class)>(state.TrackedCaches.Count);
            var deadKeys = state.DeadCacheKeysScratch;
            deadKeys.Clear();
            foreach (var (key, entry) in state.TrackedCaches)
                if (entry.Cache.TryGetTarget(out var cache))
                    caches.Add((cache, entry.Class));
                else
                    deadKeys.Add(key);

            RemoveDeadCachesLocked(state, deadKeys);
            return caches;
        }
    }

    private static void RemoveDeadCachesLocked(State state, List<int> deadKeys)
    {
        if (deadKeys.Count == 0)
            return;

        foreach (var dead in deadKeys)
            if (state.TrackedCaches.Remove(dead, out var entry))
            {
                var pageCount = Interlocked.Read(ref entry.PageCount);
                Interlocked.Add(ref state.TotalTrackedPages, -pageCount);
                if (entry.Class == AddressSpaceCacheClass.Shmem)
                    Interlocked.Add(ref state.ShmemTrackedPages, -pageCount);
            }
        deadKeys.Clear();
    }

    private static void OnTrackedPageCountDelta(State state, TrackedEntry entry, int delta)
    {
        if (delta == 0)
            return;

        Interlocked.Add(ref entry.PageCount, delta);
        Interlocked.Add(ref state.TotalTrackedPages, delta);
        if (entry.Class == AddressSpaceCacheClass.Shmem)
            Interlocked.Add(ref state.ShmemTrackedPages, delta);
    }

    private static long ReclaimFromCaches(List<(AddressSpace Cache, AddressSpaceCacheClass Class)> caches,
        long targetFreeBytes)
    {
        if (targetFreeBytes <= 0) return 0;
        var candidates = new List<(AddressSpace Cache, uint PageIndex, long LastAccessTimestamp)>();
        foreach (var (cache, cacheClass) in caches)
        {
            if (cacheClass == AddressSpaceCacheClass.Shmem) continue;
            var states = cache.SnapshotPageStates();
            foreach (var state in states)
            {
                if (state.Dirty) continue;
                candidates.Add((cache, state.PageIndex, state.LastAccessTimestamp));
            }
        }

        if (candidates.Count == 0) return 0;
        candidates.Sort(static (a, b) => a.LastAccessTimestamp.CompareTo(b.LastAccessTimestamp));

        long freed = 0;
        foreach (var candidate in candidates)
            if (candidate.Cache.TryEvictCleanPage(candidate.PageIndex))
            {
                freed += LinuxConstants.PageSize;
                if (freed >= targetFreeBytes) break;
            }

        return freed;
    }

    private sealed class State
    {
        public readonly List<int> DeadCacheKeysScratch = [];
        public readonly Lock Gate = new();
        public readonly Dictionary<int, TrackedEntry> TrackedCaches = [];
        public long HighWatermarkBytes = 256L * 1024 * 1024;
        public long LowWatermarkBytes = 192L * 1024 * 1024;
        public long NextMaintenanceTicks;
        public long ShmemTrackedPages;
        public long TotalTrackedPages;
        public TimeSpan WritebackInterval = TimeSpan.FromSeconds(5);
        public long WritebackPagesInFlight;
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

    private sealed class TrackedEntry
    {
        public TrackedEntry(WeakReference<AddressSpace> cache, AddressSpaceCacheClass @class, long pageCount)
        {
            Cache = cache;
            Class = @class;
            PageCount = pageCount;
        }

        public WeakReference<AddressSpace> Cache { get; }
        public AddressSpaceCacheClass Class { get; }
        public long PageCount;
    }

    public readonly record struct AddressSpaceStats(
        long TotalPages,
        long CleanPages,
        long DirtyPages,
        long ShmemPages,
        long WritebackPages);

    public readonly record struct AddressSpaceTrackedPageCounts(long TotalPages, long ShmemPages);

    public readonly record struct AddressSpacePageState(AddressSpaceCacheClass Class, bool Dirty,
        long LastAccessTimestamp);
}
