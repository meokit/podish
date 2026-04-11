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

public sealed class BackingPagePool : IDisposable, IBackingPageHandleReleaseOwner
{
    private const int PooledSegmentBytes = 4 * 1024 * 1024;
    private const int PooledSegmentPageCount = PooledSegmentBytes / LinuxConstants.PageSize;
    private const int PooledSegmentWordCount = PooledSegmentPageCount / 64;
    private const int PooledReleaseTokenCountsBitCount = 1;
    private const int PooledReleaseTokenSourceBitCount = 2;
    private const int PooledReleaseTokenClassBitCount = 3;
    private const int PooledReleaseTokenPageIndexBitCount = 10;
    private const int PooledReleaseTokenCountsShift = 0;
    private const int PooledReleaseTokenSourceShift =
        PooledReleaseTokenCountsShift + PooledReleaseTokenCountsBitCount;
    private const int PooledReleaseTokenClassShift =
        PooledReleaseTokenSourceShift + PooledReleaseTokenSourceBitCount;
    private const int PooledReleaseTokenPageIndexShift =
        PooledReleaseTokenClassShift + PooledReleaseTokenClassBitCount;
    private const int PooledReleaseTokenSegmentIdShift =
        PooledReleaseTokenPageIndexShift + PooledReleaseTokenPageIndexBitCount;
    private const ulong PooledReleaseTokenCountsMask = (1UL << PooledReleaseTokenCountsBitCount) - 1;
    private const ulong PooledReleaseTokenSourceMask = (1UL << PooledReleaseTokenSourceBitCount) - 1;
    private const ulong PooledReleaseTokenClassMask = (1UL << PooledReleaseTokenClassBitCount) - 1;
    private const ulong PooledReleaseTokenPageIndexMask = (1UL << PooledReleaseTokenPageIndexBitCount) - 1;
    private const ulong PooledReleaseTokenSegmentIdMask = (1UL << (64 - PooledReleaseTokenSegmentIdShift)) - 1;

    private readonly MemoryRuntimeContext _memoryContext;
    private readonly long[] _allocPagesByClass = new long[Enum.GetValues<AllocationClass>().Length];
    private readonly long[] _allocPagesBySource = new long[Enum.GetValues<AllocationSource>().Length];
    private readonly long[] _freedPagesByClass = new long[Enum.GetValues<AllocationClass>().Length];
    private readonly long[] _freedPagesBySource = new long[Enum.GetValues<AllocationSource>().Length];
    private readonly Lock _globalLock = new();
    private readonly List<SegmentEntry> _nonFullPooledSegments = [];
    private readonly List<SegmentEntry> _pooledSegments = [];
    private readonly Dictionary<long, SegmentEntry> _segments = new();
    private long _legacyAllocOverQuota;
    private long _nextSegmentId;
    private long _strictAllocFail;
    private long _strictAllocReclaimSuccess;
    private long _strictAllocSuccess;
    private long _totalAllocatedPages;

    public BackingPagePool(MemoryRuntimeContext memoryContext)
    {
        ArgumentNullException.ThrowIfNull(memoryContext);
        _memoryContext = memoryContext;
    }

    public long MemoryQuotaBytes
    {
        get => Interlocked.Read(ref field);
        set => Interlocked.Exchange(ref field, value);
    } = 2L * 1024 * 1024 * 1024;

    public long GetAllocatedPageCount()
    {
        return Interlocked.Read(ref _totalAllocatedPages);
    }

    public long GetAllocatedBytes()
    {
        return GetAllocatedPageCount() * LinuxConstants.PageSize;
    }

    public IReadOnlyList<AllocationClassStat> GetAllocationClassStats()
    {
        var classes = Enum.GetValues<AllocationClass>();
        var stats = new List<AllocationClassStat>(classes.Length);
        foreach (var cls in classes)
        {
            var idx = (int)cls;
            var allocated = Interlocked.Read(ref _allocPagesByClass[idx]);
            var freed = Interlocked.Read(ref _freedPagesByClass[idx]);
            stats.Add(new AllocationClassStat(cls, allocated, freed, Math.Max(0, allocated - freed)));
        }

        return stats;
    }

    public string GetAllocationClassStatsSummary()
    {
        var stats = GetAllocationClassStats();
        return string.Join(", ",
            stats.Select(s =>
                $"{s.Class}:live={s.LivePages},alloc={s.AllocatedPages},free={s.FreedPages}"));
    }

    public IReadOnlyList<AllocationSourceStat> GetAllocationSourceStats()
    {
        var sources = Enum.GetValues<AllocationSource>();
        var stats = new List<AllocationSourceStat>(sources.Length);
        foreach (var source in sources)
        {
            var idx = (int)source;
            var allocated = Interlocked.Read(ref _allocPagesBySource[idx]);
            var freed = Interlocked.Read(ref _freedPagesBySource[idx]);
            stats.Add(new AllocationSourceStat(source, allocated, freed, Math.Max(0, allocated - freed)));
        }

        return stats;
    }

    public BackingPageHandle AllocAnonPage(
        AllocationClass allocationClass = AllocationClass.KernelInternal,
        AllocationSource allocationSource = AllocationSource.Unknown)
    {
        ValidateAnonAllocationClass(allocationClass);
        var overQuota = MemoryQuotaBytes > 0 &&
                        _memoryContext.GetTotalTrackedBytes() + LinuxConstants.PageSize > MemoryQuotaBytes;
        if (!TryAllocatePooledPageCore(allocationClass, allocationSource, true, out var handle))
            return default;

        if (overQuota) Interlocked.Increment(ref _legacyAllocOverQuota);
        return handle;
    }

    public bool TryAllocAnonPageMayFail(
        out BackingPageHandle handle,
        AllocationClass allocationClass,
        AllocationSource allocationSource = AllocationSource.Unknown)
    {
        ValidateAnonAllocationClass(allocationClass);
        return TryAllocatePooledPageStrictCore(out handle, allocationClass, allocationSource, true, true);
    }

    internal BackingPageHandle AllocatePoolBackedPage(
        AllocationClass allocationClass = AllocationClass.PageCache,
        AllocationSource allocationSource = AllocationSource.Unknown)
    {
        return TryAllocatePooledPageCore(allocationClass, allocationSource, false, out var handle)
            ? handle
            : default;
    }

    internal bool TryAllocatePoolBackedPageStrict(
        out BackingPageHandle handle,
        AllocationClass allocationClass = AllocationClass.PageCache,
        AllocationSource allocationSource = AllocationSource.Unknown)
    {
        return TryAllocatePooledPageStrictCore(out handle, allocationClass, allocationSource, false, false);
    }

    internal static long CreatePooledReleaseToken(long segmentId, int pageIndex,
        AllocationClass allocationClass, AllocationSource allocationSource,
        bool countsTowardAnonymousAllocationTotals)
    {
        if (segmentId <= 0 || (ulong)segmentId > PooledReleaseTokenSegmentIdMask)
            throw new ArgumentOutOfRangeException(nameof(segmentId), segmentId,
                "Pooled segment id does not fit inside the packed release token.");
        if ((uint)pageIndex > PooledReleaseTokenPageIndexMask)
            throw new ArgumentOutOfRangeException(nameof(pageIndex), pageIndex,
                "Pooled page index does not fit inside the packed release token.");
        if ((ulong)(uint)allocationClass > PooledReleaseTokenClassMask)
            throw new ArgumentOutOfRangeException(nameof(allocationClass), allocationClass,
                "Allocation class does not fit inside the packed release token.");
        if ((ulong)(uint)allocationSource > PooledReleaseTokenSourceMask)
            throw new ArgumentOutOfRangeException(nameof(allocationSource), allocationSource,
                "Allocation source does not fit inside the packed release token.");

        var rawToken =
            ((ulong)segmentId << PooledReleaseTokenSegmentIdShift) |
            ((ulong)(uint)pageIndex << PooledReleaseTokenPageIndexShift) |
            ((ulong)(uint)allocationClass << PooledReleaseTokenClassShift) |
            ((ulong)(uint)allocationSource << PooledReleaseTokenSourceShift) |
            ((countsTowardAnonymousAllocationTotals ? 1UL : 0UL) << PooledReleaseTokenCountsShift);
        return unchecked((long)rawToken);
    }

    private static PooledReleaseToken DecodePooledReleaseToken(long releaseToken)
    {
        var rawToken = unchecked((ulong)releaseToken);
        return new PooledReleaseToken(
            (long)((rawToken >> PooledReleaseTokenSegmentIdShift) & PooledReleaseTokenSegmentIdMask),
            (int)((rawToken >> PooledReleaseTokenPageIndexShift) & PooledReleaseTokenPageIndexMask),
            (AllocationClass)((rawToken >> PooledReleaseTokenClassShift) & PooledReleaseTokenClassMask),
            (AllocationSource)((rawToken >> PooledReleaseTokenSourceShift) & PooledReleaseTokenSourceMask),
            ((rawToken >> PooledReleaseTokenCountsShift) & PooledReleaseTokenCountsMask) != 0);
    }

    internal void ReleasePooledPage(IntPtr ptr, long segmentId, int pageIndex,
        AllocationClass allocationClass, AllocationSource allocationSource,
        bool countsTowardAnonymousAllocationTotals)
    {
        if (ptr == IntPtr.Zero)
            return;

        lock (_globalLock)
        {
            if (!_segments.TryGetValue(segmentId, out var segment))
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
                AddNonFullSegmentLocked(segment);
        }

        if (countsTowardAnonymousAllocationTotals)
            Interlocked.Decrement(ref _totalAllocatedPages);
        Interlocked.Increment(ref _freedPagesByClass[(int)allocationClass]);
        Interlocked.Increment(ref _freedPagesBySource[(int)allocationSource]);
    }

    void IBackingPageHandleReleaseOwner.ReleaseBackingPageHandle(IntPtr pointer, long releaseToken)
    {
        var pooledRelease = DecodePooledReleaseToken(releaseToken);
        ReleasePooledPage(pointer, pooledRelease.SegmentId, pooledRelease.PageIndex, pooledRelease.AllocationClass,
            pooledRelease.AllocationSource, pooledRelease.CountsTowardAnonymousAllocationTotals);
    }

    public void Dispose()
    {
        lock (_globalLock)
        {
            foreach (var segment in _pooledSegments)
                unsafe
                {
                    NativeMemory.AlignedFree((void*)segment.BasePtr);
                }

            _pooledSegments.Clear();
            _nonFullPooledSegments.Clear();
            _segments.Clear();
        }
    }

    private bool TryAllocatePooledPageStrictCore(
        out BackingPageHandle handle,
        AllocationClass allocationClass,
        AllocationSource allocationSource,
        bool countsTowardAnonymousAllocationTotals,
        bool recordStrictAnonStats)
    {
        handle = default;
        var reclaimed = false;
        if (!HasQuotaCapacity())
        {
            reclaimed = _memoryContext.MemoryPressure.TryReclaimForAllocation(
                LinuxConstants.PageSize,
                allocationClass,
                allocationSource) > 0;

            if (!HasQuotaCapacity())
            {
                if (recordStrictAnonStats)
                    Interlocked.Increment(ref _strictAllocFail);
                return false;
            }
        }

        if (!TryAllocatePooledPageCore(allocationClass, allocationSource, countsTowardAnonymousAllocationTotals,
                out handle))
        {
            if (recordStrictAnonStats)
                Interlocked.Increment(ref _strictAllocFail);
            return false;
        }

        if (!handle.IsValid)
        {
            if (recordStrictAnonStats)
                Interlocked.Increment(ref _strictAllocFail);
            return false;
        }

        if (recordStrictAnonStats)
            if (reclaimed)
                Interlocked.Increment(ref _strictAllocReclaimSuccess);
            else
                Interlocked.Increment(ref _strictAllocSuccess);
        return true;
    }

    private bool TryAllocatePooledPageCore(AllocationClass allocationClass,
        AllocationSource allocationSource, bool countsTowardAnonymousAllocationTotals, out BackingPageHandle handle)
    {
        lock (_globalLock)
        {
            while (_nonFullPooledSegments.Count > 0)
            {
                var segment = _nonFullPooledSegments[^1];
                if (TryAllocateFromPoolSegmentLocked(segment, allocationClass, allocationSource,
                        countsTowardAnonymousAllocationTotals, out handle))
                    return true;

                RemoveNonFullSegmentLocked(segment);
            }

            var created = CreatePooledSegmentLocked();
            if (created != null &&
                TryAllocateFromPoolSegmentLocked(created, allocationClass, allocationSource,
                    countsTowardAnonymousAllocationTotals, out handle))
                return true;
        }

        handle = default;
        return false;
    }

    private SegmentEntry? CreatePooledSegmentLocked()
    {
        unsafe
        {
            var basePtr = (nint)NativeMemory.AlignedAlloc(PooledSegmentBytes, LinuxConstants.PageSize);
            if (basePtr == 0)
                return null;

            new Span<byte>((void*)basePtr, PooledSegmentBytes).Clear();
            var segment = new SegmentEntry
            {
                SegmentId = Interlocked.Increment(ref _nextSegmentId),
                BasePtr = basePtr,
                PageCount = PooledSegmentPageCount,
                LivePages = 0,
                PoolBitmap = new ulong[PooledSegmentWordCount]
            };
            _segments[segment.SegmentId] = segment;
            _pooledSegments.Add(segment);
            AddNonFullSegmentLocked(segment);
            return segment;
        }
    }

    private bool TryAllocateFromPoolSegmentLocked(SegmentEntry segment, AllocationClass allocationClass,
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
            RemoveNonFullSegmentLocked(segment);

        var pagePtr = segment.BasePtr + pageIndex * LinuxConstants.PageSize;
        if (countsTowardAnonymousAllocationTotals)
            Interlocked.Increment(ref _totalAllocatedPages);
        Interlocked.Increment(ref _allocPagesByClass[(int)allocationClass]);
        Interlocked.Increment(ref _allocPagesBySource[(int)allocationSource]);
        handle = BackingPageHandle.CreatePooled(this, pagePtr, segment.SegmentId, pageIndex, allocationClass,
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

    private void AddNonFullSegmentLocked(SegmentEntry segment)
    {
        if (segment.NonFullListIndex >= 0)
            return;

        segment.NonFullListIndex = _nonFullPooledSegments.Count;
        _nonFullPooledSegments.Add(segment);
    }

    private void RemoveNonFullSegmentLocked(SegmentEntry segment)
    {
        var index = segment.NonFullListIndex;
        if (index < 0)
            return;

        var lastIndex = _nonFullPooledSegments.Count - 1;
        var last = _nonFullPooledSegments[lastIndex];
        _nonFullPooledSegments[index] = last;
        last.NonFullListIndex = index;
        _nonFullPooledSegments.RemoveAt(lastIndex);
        segment.NonFullListIndex = -1;
    }

    private bool HasQuotaCapacity()
    {
        if (MemoryQuotaBytes <= 0) return true;
        var next = _memoryContext.GetTotalTrackedBytes() + LinuxConstants.PageSize;
        return next <= MemoryQuotaBytes;
    }

    private static void ValidateAnonAllocationClass(AllocationClass allocationClass)
    {
        if (allocationClass is AllocationClass.PageCache or AllocationClass.Readahead)
            throw new InvalidOperationException(
                $"{allocationClass} pages must be allocated through inode-managed page backing.");
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

    private readonly record struct PooledReleaseToken(
        long SegmentId,
        int PageIndex,
        AllocationClass AllocationClass,
        AllocationSource AllocationSource,
        bool CountsTowardAnonymousAllocationTotals);
}
