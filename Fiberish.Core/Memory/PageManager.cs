using System.Numerics;
using System.Runtime.InteropServices;
using Fiberish.Native;

namespace Fiberish.Memory;

public enum AllocationClass
{
    Anonymous,
    Cow,
    PageCache,
    Readahead,
    KernelInternal
}

public enum AllocationSource
{
    Unknown,
    AnonFault,
    CowFirstPrivate,
    CowReplacePrivate
}

public enum MappedPageOwnerKind
{
    AddressSpace,
    AnonVma
}

internal sealed class MappedPageBinding
{
    public required IntPtr Ptr { get; init; }
    public required MappedPageOwnerKind OwnerKind { get; init; }
    public AddressSpace? Mapping { get; init; }
    public AnonVma? AnonVma { get; init; }
    public ResidentPageRecord? Page { get; init; }
    public uint PageIndex { get; init; }

    internal static MappedPageBinding FromAddressSpacePage(AddressSpace mapping, uint pageIndex, ResidentPageRecord pageRecord)
    {
        return new MappedPageBinding
        {
            Ptr = pageRecord.Ptr,
            OwnerKind = MappedPageOwnerKind.AddressSpace,
            Mapping = mapping,
            Page = pageRecord,
            PageIndex = pageIndex
        };
    }

    internal static MappedPageBinding FromAnonVmaPage(AnonVma anonVma, uint pageIndex, ResidentPageRecord pageRecord)
    {
        return new MappedPageBinding
        {
            Ptr = pageRecord.Ptr,
            OwnerKind = MappedPageOwnerKind.AnonVma,
            AnonVma = anonVma,
            Page = pageRecord,
            PageIndex = pageIndex
        };
    }
}

public sealed class PageManager
{
    private const int PooledSegmentBytes = 4 * 1024 * 1024;
    private const int PooledSegmentPageCount = PooledSegmentBytes / LinuxConstants.PageSize;
    private const int PooledSegmentWordCount = PooledSegmentPageCount / 64;
    private static readonly AsyncLocal<State?> ScopedState = new();
    private static readonly State DefaultState = new();

    private readonly Dictionary<uint, MappedPageBinding> _pages = new();

    private static State CurrentState => ScopedState.Value ?? DefaultState;

    public static long MemoryQuotaBytes
    {
        get => CurrentState.MemoryQuotaBytes;
        set => CurrentState.MemoryQuotaBytes = value;
    }

    public static IDisposable BeginIsolatedScope()
    {
        var previous = ScopedState.Value;
        var isolated = new State();
        ScopedState.Value = isolated;
        return new ScopeRestore(previous, HostPageManager.BeginIsolatedScope(), isolated);
    }

    public bool TryGet(uint pageAddr, out IntPtr ptr)
    {
        if (_pages.TryGetValue(pageAddr, out var page))
        {
            ptr = page.Ptr;
            return true;
        }

        ptr = IntPtr.Zero;
        return false;
    }

    internal bool TryGetBinding(uint pageAddr, out MappedPageBinding? binding)
    {
        if (_pages.TryGetValue(pageAddr, out var existing))
        {
            binding = existing;
            return true;
        }

        binding = null;
        return false;
    }

    internal bool AddBinding(uint pageAddr, MappedPageBinding binding, out bool addedRef)
    {
        addedRef = false;
        if (binding.Ptr == IntPtr.Zero) return false;
        if (_pages.TryGetValue(pageAddr, out var existing)) return existing.Ptr == binding.Ptr;

        var boundHostPage = binding.Page?.HostPage ?? HostPageManager.GetRequired(binding.Ptr);
        boundHostPage.MapCount++;
        _pages[pageAddr] = binding;
        addedRef = true;
        return true;
    }

    public void Release(uint pageAddr, bool preserveOwnerBinding = false)
    {
        if (!_pages.TryGetValue(pageAddr, out var binding)) return;
        _pages.Remove(pageAddr);
        var hostPage = binding.Page?.HostPage ?? HostPageManager.GetRequired(binding.Ptr);

        if (hostPage.MapCount > 0)
            hostPage.MapCount--;

        if (!preserveOwnerBinding &&
            binding.OwnerKind == MappedPageOwnerKind.AnonVma &&
            binding.AnonVma is { } anonVma &&
            binding.Page is { } anonPage &&
            anonPage.HostPage is { MapCount: <= 0, PinCount: <= 0, OwnerResidentCount: <= 1 })
            anonVma.RemovePageIfMatches(binding.PageIndex, anonPage);

        HostPageManager.TryRemoveIfUnused(hostPage);
    }

    public void ReleaseRange(uint addr, uint length, bool preserveOwnerBinding = false)
    {
        if (length == 0) return;
        var start = addr & LinuxConstants.PageMask;
        var end = (addr + length + LinuxConstants.PageOffsetMask) & LinuxConstants.PageMask;
        for (var p = start; p < end; p += LinuxConstants.PageSize) Release(p, preserveOwnerBinding);
    }

    public IReadOnlyList<uint> SnapshotMappedPages()
    {
        if (_pages.Count == 0) return Array.Empty<uint>();
        return _pages.Keys.ToArray();
    }

    public static long GetAllocatedPageCount()
    {
        var state = CurrentState;
        return Interlocked.Read(ref state.TotalAllocatedPages);
    }

    public static long GetAllocatedBytes()
    {
        return GetAllocatedPageCount() * LinuxConstants.PageSize;
    }

    internal static long GetCachedBytes()
    {
        return AddressSpacePolicy.GetTotalCachedPages() * LinuxConstants.PageSize;
    }

    internal static long GetTotalTrackedBytes()
    {
        return GetAllocatedBytes() + GetCachedBytes();
    }

    public static IReadOnlyList<AllocationClassStat> GetAllocationClassStats()
    {
        var state = CurrentState;
        var classes = Enum.GetValues<AllocationClass>();
        var stats = new List<AllocationClassStat>(classes.Length);
        foreach (var cls in classes)
        {
            var idx = (int)cls;
            var allocated = Interlocked.Read(ref state.AllocPagesByClass[idx]);
            var freed = Interlocked.Read(ref state.FreedPagesByClass[idx]);
            stats.Add(new AllocationClassStat(cls, allocated, freed, Math.Max(0, allocated - freed)));
        }

        return stats;
    }

    public static string GetAllocationClassStatsSummary()
    {
        var stats = GetAllocationClassStats();
        return string.Join(", ",
            stats.Select(s =>
                $"{s.Class}:live={s.LivePages},alloc={s.AllocatedPages},free={s.FreedPages}"));
    }

    public static IReadOnlyList<AllocationSourceStat> GetAllocationSourceStats()
    {
        var state = CurrentState;
        var sources = Enum.GetValues<AllocationSource>();
        var stats = new List<AllocationSourceStat>(sources.Length);
        foreach (var source in sources)
        {
            var idx = (int)source;
            var allocated = Interlocked.Read(ref state.AllocPagesBySource[idx]);
            var freed = Interlocked.Read(ref state.FreedPagesBySource[idx]);
            stats.Add(new AllocationSourceStat(source, allocated, freed, Math.Max(0, allocated - freed)));
        }

        return stats;
    }

    public static string GetAllocationSourceStatsSummary()
    {
        var stats = GetAllocationSourceStats();
        return string.Join(", ",
            stats.Select(s =>
                $"{s.Source}:live={s.LivePages},alloc={s.AllocatedPages},free={s.FreedPages}"));
    }

    public static BackingPageHandle AllocAnonPage(
        AllocationClass allocationClass = AllocationClass.KernelInternal,
        AllocationSource allocationSource = AllocationSource.Unknown)
    {
        ValidateAnonAllocationClass(allocationClass);
        var state = CurrentState;
        var overQuota = state.MemoryQuotaBytes > 0 &&
                        GetTotalTrackedBytes() + LinuxConstants.PageSize > state.MemoryQuotaBytes;
        if (!TryAllocatePooledPageCore(state, allocationClass, allocationSource, true, out var handle))
            return default;

        if (overQuota) Interlocked.Increment(ref state.LegacyAllocOverQuota);
        return handle;
    }

    public static bool TryAllocAnonPageMayFail(
        out BackingPageHandle handle,
        AllocationClass allocationClass,
        AllocationSource allocationSource = AllocationSource.Unknown)
    {
        ValidateAnonAllocationClass(allocationClass);
        return TryAllocatePooledPageStrictCore(out handle, allocationClass, allocationSource, true, true);
    }

    internal static BackingPageHandle AllocatePoolBackedPage(
        AllocationClass allocationClass = AllocationClass.PageCache,
        AllocationSource allocationSource = AllocationSource.Unknown)
    {
        return TryAllocatePooledPageCore(CurrentState, allocationClass, allocationSource, false, out var handle)
            ? handle
            : default;
    }

    internal static bool TryAllocatePoolBackedPageStrict(
        out BackingPageHandle handle,
        AllocationClass allocationClass = AllocationClass.PageCache,
        AllocationSource allocationSource = AllocationSource.Unknown)
    {
        return TryAllocatePooledPageStrictCore(out handle, allocationClass, allocationSource, false, false);
    }

    private static bool TryAllocatePooledPageStrictCore(
        out BackingPageHandle handle,
        AllocationClass allocationClass,
        AllocationSource allocationSource,
        bool countsTowardAnonymousAllocationTotals,
        bool recordStrictAnonStats)
    {
        var state = CurrentState;
        handle = default;
        var reclaimed = false;
        if (!HasQuotaCapacity())
        {
            // Best-effort one-shot reclaim before failing strict allocation.
            reclaimed = MemoryPressureCoordinator.TryReclaimForAllocation(
                LinuxConstants.PageSize,
                allocationClass,
                allocationSource) > 0;

            if (!HasQuotaCapacity())
            {
                if (recordStrictAnonStats)
                    Interlocked.Increment(ref state.StrictAllocFail);
                return false;
            }
        }

        if (!TryAllocatePooledPageCore(state, allocationClass, allocationSource, countsTowardAnonymousAllocationTotals,
                out handle))
        {
            if (recordStrictAnonStats)
                Interlocked.Increment(ref state.StrictAllocFail);
            return false;
        }

        if (!handle.IsValid)
        {
            if (recordStrictAnonStats)
                Interlocked.Increment(ref state.StrictAllocFail);
            return false;
        }

        if (recordStrictAnonStats)
            if (reclaimed)
                Interlocked.Increment(ref state.StrictAllocReclaimSuccess);
            else
                Interlocked.Increment(ref state.StrictAllocSuccess);
        return true;
    }

    private static bool TryAllocatePooledPageCore(State state, AllocationClass allocationClass,
        AllocationSource allocationSource, bool countsTowardAnonymousAllocationTotals, out BackingPageHandle handle)
    {
        lock (state.GlobalLock)
        {
            while (state.NonFullPooledSegments.Count > 0)
            {
                var segment = state.NonFullPooledSegments[^1];
                if (TryAllocateFromPoolSegmentLocked(state, segment, allocationClass, allocationSource,
                        countsTowardAnonymousAllocationTotals, out handle))
                    return true;

                RemoveNonFullSegmentLocked(state, segment);
            }

            var created = CreatePooledSegmentLocked(state);
            if (created != null &&
                TryAllocateFromPoolSegmentLocked(state, created, allocationClass, allocationSource,
                    countsTowardAnonymousAllocationTotals, out handle))
                return true;
        }

        handle = default;
        return false;
    }

    private static SegmentEntry? CreatePooledSegmentLocked(State state)
    {
        unsafe
        {
            var basePtr = (nint)NativeMemory.AlignedAlloc(PooledSegmentBytes, LinuxConstants.PageSize);
            if (basePtr == 0)
                return null;

            new Span<byte>((void*)basePtr, PooledSegmentBytes).Clear();
            var segment = new SegmentEntry
            {
                SegmentId = Interlocked.Increment(ref state.NextSegmentId),
                BasePtr = basePtr,
                PageCount = PooledSegmentPageCount,
                LivePages = 0,
                PoolBitmap = new ulong[PooledSegmentWordCount]
            };
            state.Segments[segment.SegmentId] = segment;
            state.PooledSegments.Add(segment);
            AddNonFullSegmentLocked(state, segment);
            return segment;
        }
    }

    private static bool TryAllocateFromPoolSegmentLocked(State state, SegmentEntry segment, AllocationClass allocationClass,
        AllocationSource allocationSource, bool countsTowardAnonymousAllocationTotals, out BackingPageHandle handle)
    {
        if (segment.PoolBitmap == null || segment.LivePages >= segment.PageCount)
        {
            handle = default;
            return false;
        }

        var pageIndex = FindFirstZeroUniversal(segment.PoolBitmap, segment.NextFreeWordHint);
        if (pageIndex < 0 || pageIndex >= segment.PageCount)
        {
            handle = default;
            return false;
        }

        SetPoolBit(segment, pageIndex);
        segment.LivePages++;
        if (segment.LivePages >= segment.PageCount)
            RemoveNonFullSegmentLocked(state, segment);

        var pagePtr = segment.BasePtr + pageIndex * LinuxConstants.PageSize;
        if (countsTowardAnonymousAllocationTotals)
            Interlocked.Increment(ref state.TotalAllocatedPages);
        Interlocked.Increment(ref state.AllocPagesByClass[(int)allocationClass]);
        Interlocked.Increment(ref state.AllocPagesBySource[(int)allocationSource]);
        handle = BackingPageHandle.CreatePooled(pagePtr, segment.SegmentId, pageIndex, allocationClass,
            allocationSource, countsTowardAnonymousAllocationTotals);
        return true;
    }

    private static int FindFirstZeroUniversal(ReadOnlySpan<ulong> data, int startWordIndex = 0)
    {
        if (data.Length == 0)
            return -1;

        if ((uint)startWordIndex >= (uint)data.Length)
            startWordIndex = 0;

        if (startWordIndex > 0)
        {
            var tailResult = FindFirstZeroUniversalCore(data.Slice(startWordIndex));
            if (tailResult >= 0)
                return startWordIndex * 64 + tailResult;
        }

        if (startWordIndex == 0)
            return FindFirstZeroUniversalCore(data);

        return FindFirstZeroUniversalCore(data[..startWordIndex]);
    }

    private static int FindFirstZeroUniversalCore(ReadOnlySpan<ulong> data)
    {
        var vCount = Vector<ulong>.Count;
        var i = 0;

        for (; i <= data.Length - vCount; i += vCount)
        {
            var vector = new Vector<ulong>(data.Slice(i, vCount));
            var inverted = Vector.OnesComplement(vector);

            if (inverted != Vector<ulong>.Zero)
                for (var j = 0; j < vCount; j++)
                    if (inverted[j] != 0)
                        return (i + j) * 64 + BitOperations.TrailingZeroCount(inverted[j]);
        }

        for (; i < data.Length; i++)
            if (data[i] != ulong.MaxValue)
                return i * 64 + BitOperations.TrailingZeroCount(~data[i]);

        return -1;
    }

    private static void SetPoolBit(SegmentEntry segment, int pageIndex)
    {
        var wordIndex = pageIndex >> 6;
        var bitIndex = pageIndex & 63;
        var bitmap = segment.PoolBitmap!;
        bitmap[wordIndex] |= 1UL << bitIndex;
        segment.NextFreeWordHint = bitmap[wordIndex] == ulong.MaxValue
            ? wordIndex + 1 < bitmap.Length ? wordIndex + 1 : 0
            : wordIndex;
    }

    private static void ClearPoolBit(SegmentEntry segment, int pageIndex)
    {
        var wordIndex = pageIndex >> 6;
        var bitIndex = pageIndex & 63;
        segment.PoolBitmap![wordIndex] &= ~(1UL << bitIndex);
        segment.NextFreeWordHint = wordIndex;
    }

    private static void AddNonFullSegmentLocked(State state, SegmentEntry segment)
    {
        if (segment.NonFullListIndex >= 0)
            return;

        segment.NonFullListIndex = state.NonFullPooledSegments.Count;
        state.NonFullPooledSegments.Add(segment);
    }

    private static void RemoveNonFullSegmentLocked(State state, SegmentEntry segment)
    {
        var index = segment.NonFullListIndex;
        if (index < 0)
            return;

        var lastIndex = state.NonFullPooledSegments.Count - 1;
        var last = state.NonFullPooledSegments[lastIndex];
        state.NonFullPooledSegments[index] = last;
        last.NonFullListIndex = index;
        state.NonFullPooledSegments.RemoveAt(lastIndex);
        segment.NonFullListIndex = -1;
    }

    private static bool HasQuotaCapacity()
    {
        var state = CurrentState;
        if (state.MemoryQuotaBytes <= 0) return true;
        var next = GetTotalTrackedBytes() + LinuxConstants.PageSize;
        return next <= state.MemoryQuotaBytes;
    }

    internal static void ReleasePooledPage(IntPtr ptr, long segmentId, int pageIndex,
        AllocationClass allocationClass, AllocationSource allocationSource,
        bool countsTowardAnonymousAllocationTotals)
    {
        if (ptr == IntPtr.Zero)
            return;

        var state = CurrentState;
        lock (state.GlobalLock)
        {
            if (!state.Segments.TryGetValue(segmentId, out var segment))
                throw new InvalidOperationException($"Unknown anonymous pooled segment {segmentId} for 0x{ptr.ToInt64():X}.");

            if (segment.PoolBitmap == null)
                throw new InvalidOperationException($"Anonymous pooled segment {segmentId} is missing its bitmap.");

            var expectedPtr = segment.BasePtr + pageIndex * LinuxConstants.PageSize;
            if (expectedPtr != ptr)
                throw new InvalidOperationException(
                    $"Anonymous pooled page handle mismatch: segment={segmentId}, index={pageIndex}, expected=0x{expectedPtr.ToInt64():X}, actual=0x{ptr.ToInt64():X}.");

            var wasFull = segment.LivePages >= segment.PageCount;
            unsafe
            {
                new Span<byte>((void*)ptr, LinuxConstants.PageSize).Clear();
            }

            ClearPoolBit(segment, pageIndex);
            if (segment.LivePages > 0)
                segment.LivePages--;
            if (wasFull)
                AddNonFullSegmentLocked(state, segment);
        }

        if (countsTowardAnonymousAllocationTotals)
            Interlocked.Decrement(ref state.TotalAllocatedPages);
        Interlocked.Increment(ref state.FreedPagesByClass[(int)allocationClass]);
        Interlocked.Increment(ref state.FreedPagesBySource[(int)allocationSource]);
    }

    private static void ValidateAnonAllocationClass(AllocationClass allocationClass)
    {
        if (allocationClass is AllocationClass.PageCache or AllocationClass.Readahead)
            throw new InvalidOperationException(
                $"{allocationClass} pages must be allocated through inode-managed page backing.");
    }

    private sealed class State
    {
        public readonly long[] AllocPagesByClass = new long[Enum.GetValues<AllocationClass>().Length];
        public readonly long[] AllocPagesBySource = new long[Enum.GetValues<AllocationSource>().Length];
        public readonly long[] FreedPagesByClass = new long[Enum.GetValues<AllocationClass>().Length];
        public readonly long[] FreedPagesBySource = new long[Enum.GetValues<AllocationSource>().Length];
        public readonly Lock GlobalLock = new();
        public readonly List<SegmentEntry> NonFullPooledSegments = [];
        public readonly List<SegmentEntry> PooledSegments = [];
        public readonly Dictionary<long, SegmentEntry> Segments = new();
        public long LegacyAllocOverQuota;
        public long MemoryQuotaBytes = 2L * 1024 * 1024 * 1024;
        public long NextSegmentId;
        public long StrictAllocFail;
        public long StrictAllocReclaimSuccess;
        public long StrictAllocSuccess;
        public long TotalAllocatedPages;
    }

    private sealed class ScopeRestore : IDisposable
    {
        private readonly State _active;
        private readonly IDisposable _hostPageScope;
        private readonly State? _previous;

        public ScopeRestore(State? previous, IDisposable hostPageScope, State active)
        {
            _previous = previous;
            _hostPageScope = hostPageScope;
            _active = active;
        }

        public void Dispose()
        {
            DisposePooledSegments(_active);
            ScopedState.Value = _previous;
            _hostPageScope.Dispose();
        }
    }

    private sealed class SegmentEntry
    {
        public required nint BasePtr;
        public int LivePages;
        public int NextFreeWordHint;
        public int NonFullListIndex = -1;
        public required int PageCount;
        public ulong[]? PoolBitmap;
        public long SegmentId;
    }

    public readonly record struct AllocationClassStat(
        AllocationClass Class,
        long AllocatedPages,
        long FreedPages,
        long LivePages);

    public readonly record struct AllocationSourceStat(
        AllocationSource Source,
        long AllocatedPages,
        long FreedPages,
        long LivePages);

    private static void DisposePooledSegments(State state)
    {
        lock (state.GlobalLock)
        {
            foreach (var segment in state.PooledSegments)
                unsafe
                {
                    NativeMemory.AlignedFree((void*)segment.BasePtr);
                }

            state.PooledSegments.Clear();
            state.NonFullPooledSegments.Clear();
            state.Segments.Clear();
        }
    }
}
