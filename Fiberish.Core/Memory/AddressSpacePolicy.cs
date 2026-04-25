using System.Runtime.CompilerServices;
using Fiberish.Core;
using Fiberish.Native;

namespace Fiberish.Memory;

/// <summary>
///     address_space cache policy manager scoped to a single <see cref="MemoryRuntimeContext"/>.
///     Maintenance runs at syscall safe-points to avoid concurrent VM mutation.
/// </summary>
public sealed class AddressSpacePolicy : IDisposable
{
    public enum AddressSpaceCacheClass
    {
        File,
        Shmem
    }

    private readonly State _state = new();

    public TimeSpan WritebackInterval
    {
        get => _state.WritebackInterval;
        set => _state.WritebackInterval = value;
    }

    public long HighWatermarkBytes
    {
        get => _state.HighWatermarkBytes;
        set => _state.HighWatermarkBytes = value;
    }

    public long LowWatermarkBytes
    {
        get => _state.LowWatermarkBytes;
        set => _state.LowWatermarkBytes = value;
    }

    public void TrackAddressSpace(AddressSpace mapping,
        AddressSpaceCacheClass cacheClass = AddressSpaceCacheClass.File)
    {
        var key = RuntimeHelpers.GetHashCode(mapping);
        var initialPageCount = mapping.PageCount;
        var entry = new TrackedEntry(new WeakReference<AddressSpace>(mapping), cacheClass, initialPageCount);
        TrackedEntry? replaced = null;
        lock (_state.Gate)
        {
            if (_state.TrackedCaches.TryGetValue(key, out var existing) &&
                existing.Cache.TryGetTarget(out var existingCache) &&
                ReferenceEquals(existingCache, mapping))
                return;

            if (_state.TrackedCaches.TryGetValue(key, out existing))
                replaced = existing;
            _state.TrackedCaches[key] = entry;
        }

        if (replaced != null)
        {
            var replacedPageCount = Interlocked.Read(ref replaced.PageCount);
            Interlocked.Add(ref _state.TotalTrackedPages, -replacedPageCount);
            if (replaced.Class == AddressSpaceCacheClass.Shmem)
                Interlocked.Add(ref _state.ShmemTrackedPages, -replacedPageCount);
        }

        Interlocked.Add(ref _state.TotalTrackedPages, initialPageCount);
        if (cacheClass == AddressSpaceCacheClass.Shmem)
            Interlocked.Add(ref _state.ShmemTrackedPages, initialPageCount);

        mapping.SetTrackedPageCountDeltaCallback(delta => OnTrackedPageCountDelta(_state, entry, delta));
    }

    public void MaybeRunMaintenance(VMAManager mm, Engine engine)
    {
        if (engine == null || engine.State == IntPtr.Zero) return;
        var now = DateTime.UtcNow.Ticks;
        lock (_state.Gate)
        {
            if (now < _state.NextMaintenanceTicks) return;
            _state.NextMaintenanceTicks = now + _state.WritebackInterval.Ticks;
        }

        try
        {
            ProcessAddressSpaceSync.SyncAllMappedSharedFiles(mm, engine);
        }
        catch
        {
        }

        try
        {
            ReclaimIfNeeded();
        }
        catch
        {
        }
    }

    public AddressSpaceStats GetAddressSpaceStats()
    {
        long dirty = 0;
        lock (_state.Gate)
        {
            var deadKeys = _state.DeadCacheKeysScratch;
            deadKeys.Clear();
            foreach (var (key, entry) in _state.TrackedCaches)
            {
                if (!entry.Cache.TryGetTarget(out var cache))
                {
                    deadKeys.Add(key);
                    continue;
                }

                cache.GetPageStats(out _, out var dirtyPages);
                dirty += dirtyPages;
            }

            RemoveDeadCachesLocked(_state, deadKeys);
        }

        var total = Interlocked.Read(ref _state.TotalTrackedPages);
        var shmem = Interlocked.Read(ref _state.ShmemTrackedPages);
        return new AddressSpaceStats(
            total,
            total - dirty,
            dirty,
            shmem,
            Interlocked.Read(ref _state.WritebackPagesInFlight));
    }

    public long GetTotalCachedPages()
    {
        return GetTrackedPageCounts().TotalPages;
    }

    public AddressSpaceTrackedPageCounts GetTrackedPageCounts()
    {
        return new AddressSpaceTrackedPageCounts(
            Interlocked.Read(ref _state.TotalTrackedPages),
            Interlocked.Read(ref _state.ShmemTrackedPages));
    }

    public IReadOnlyList<AddressSpacePageState> GetAddressSpacePageStatesSnapshot()
    {
        var caches = GetLiveCaches(_state);
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

    public void BeginAddressSpaceWriteback(int pages = 1)
    {
        if (pages <= 0) return;
        Interlocked.Add(ref _state.WritebackPagesInFlight, pages);
    }

    public void EndAddressSpaceWriteback(int pages = 1)
    {
        if (pages <= 0) return;
        var current = Interlocked.Add(ref _state.WritebackPagesInFlight, -pages);
        if (current >= 0) return;
        Interlocked.Exchange(ref _state.WritebackPagesInFlight, 0);
    }

    public long TryReclaimBytes(long targetBytes)
    {
        if (targetBytes <= 0) return 0;
        var caches = GetLiveCaches(_state);
        return ReclaimFromCaches(caches, targetBytes);
    }

    public void Dispose()
    {
        lock (_state.Gate)
        {
            _state.TrackedCaches.Clear();
            _state.DeadCacheKeysScratch.Clear();
            _state.NextMaintenanceTicks = 0;
            _state.ShmemTrackedPages = 0;
            _state.TotalTrackedPages = 0;
            _state.WritebackPagesInFlight = 0;
        }
    }

    private void ReclaimIfNeeded()
    {
        var caches = GetLiveCaches(_state);

        if (caches.Count == 0) return;

        var totalBytes = Interlocked.Read(ref _state.TotalTrackedPages) * LinuxConstants.PageSize;
        if (totalBytes <= _state.HighWatermarkBytes) return;

        var targetFree = totalBytes - _state.LowWatermarkBytes;
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
