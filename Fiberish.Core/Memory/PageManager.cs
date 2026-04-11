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

        if (HostPageManager.GetRequired(binding.Ptr).Kind != HostPageKind.Zero)
        {
            AddGlobalRef(binding.Ptr);
            addedRef = true;
        }

        var boundHostPage = HostPageManager.GetRequired(binding.Ptr);
        boundHostPage.MapCount++;
        _pages[pageAddr] = binding;
        return true;
    }

    public void Release(uint pageAddr, bool preserveOwnerBinding = false)
    {
        if (!_pages.TryGetValue(pageAddr, out var binding)) return;
        _pages.Remove(pageAddr);
        var hostPage = HostPageManager.GetRequired(binding.Ptr);

        if (hostPage.MapCount > 0)
            hostPage.MapCount--;

        if (hostPage.Kind != HostPageKind.Zero)
            ReleaseGlobalRef(binding.Ptr);

        if (!preserveOwnerBinding &&
            binding.OwnerKind == MappedPageOwnerKind.AnonVma &&
            binding.AnonVma is { } anonVma &&
            binding.Page is { } anonPage &&
            HostPageManager.GetRequired(anonPage.Ptr) is { MapCount: <= 0, PinCount: <= 0, RefCount: <= 0 })
            anonVma.RemovePageIfMatches(binding.PageIndex, anonPage);

        HostPageManager.TryRemoveIfUnused(binding.Ptr);
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

    public static void RetainAnonPage(IntPtr ptr)
    {
        AddGlobalRef(ptr);
    }

    public static int GetAnonPageRefCount(IntPtr ptr)
    {
        var state = CurrentState;
        lock (state.GlobalLock)
        {
            return state.PageRefs.TryGetValue(ptr, out var entry) ? entry.RefCount : 0;
        }
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

    public static (long StrictSuccess, long StrictReclaimSuccess, long StrictFail, long LegacyOverQuota)
        GetAllocationStats()
    {
        var state = CurrentState;
        return (
            Interlocked.Read(ref state.StrictAllocSuccess),
            Interlocked.Read(ref state.StrictAllocReclaimSuccess),
            Interlocked.Read(ref state.StrictAllocFail),
            Interlocked.Read(ref state.LegacyAllocOverQuota));
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

    private static void AddGlobalRef(IntPtr ptr, AllocationClass? allocationClass = null,
        AllocationSource? allocationSource = null, IDisposable? externalOwner = null)
    {
        if (ptr == IntPtr.Zero) return;
        var state = CurrentState;
        HostPageManager.TryRetainExisting(ptr);
        lock (state.GlobalLock)
        {
            var key = ptr;
            if (state.PageRefs.TryGetValue(key, out var existing))
            {
                existing.RefCount++;
                externalOwner?.Dispose();
                return;
            }

            // Backward-compatible fallback: allow attaching metadata to an externally supplied
            // standalone page pointer. This keeps existing call sites working while we move
            // internals to segment/page accounting.
            var cls = allocationClass ?? AllocationClass.KernelInternal;
            var source = allocationSource ?? AllocationSource.Unknown;
            var segmentId = Interlocked.Increment(ref state.NextSegmentId);
            state.Segments[segmentId] = new SegmentEntry
            {
                BasePtr = key,
                PageCount = 1,
                // Unknown pointer source: do not assume ownership.
                Owned = false,
                BackingKind = SegmentBackingKind.ExternalUnknown,
                LivePages = 1,
                ExternalOwner = externalOwner
            };
            state.PageRefs[key] = new PageRefEntry
            {
                SegmentId = segmentId,
                PageIndex = 0,
                Ptr = key,
                Class = cls,
                Source = source,
                RefCount = 1
            };
            Interlocked.Increment(ref state.TotalAllocatedPages);
            Interlocked.Increment(ref state.AllocPagesByClass[(int)cls]);
            Interlocked.Increment(ref state.AllocPagesBySource[(int)source]);
        }
    }

    private static void ReleaseGlobalRef(IntPtr ptr)
    {
        var state = CurrentState;
        AllocationClass allocationClass;
        AllocationSource allocationSource;
        SegmentEntry? segmentToFree = null;

        lock (state.GlobalLock)
        {
            var key = ptr;
            if (!state.PageRefs.TryGetValue(key, out var entry)) return;
            entry.RefCount--;
            if (entry.RefCount > 0) return;

            allocationClass = entry.Class;
            allocationSource = entry.Source;
            state.PageRefs.Remove(key);
            Interlocked.Decrement(ref state.TotalAllocatedPages);

            if (state.Segments.TryGetValue(entry.SegmentId, out var segment))
            {
                var wasFull = segment.BackingKind == SegmentBackingKind.PooledAlignedAlloc &&
                              segment.LivePages >= segment.PageCount;
                segment.LivePages--;
                if (segment.BackingKind == SegmentBackingKind.PooledAlignedAlloc)
                {
                    unsafe
                    {
                        new Span<byte>((void*)ptr, LinuxConstants.PageSize).Clear();
                    }

                    ClearPoolBit(segment, entry.PageIndex);
                    if (wasFull)
                        AddNonFullSegmentLocked(state, segment);
                }
                else if (segment.LivePages <= 0)
                {
                    state.Segments.Remove(entry.SegmentId);
                    segmentToFree = segment;
                }
            }
        }

        HostPageManager.Release(ptr);

        Interlocked.Increment(ref state.FreedPagesByClass[(int)allocationClass]);
        Interlocked.Increment(ref state.FreedPagesBySource[(int)allocationSource]);

        if (segmentToFree is { Owned: true })
        {
            unsafe
            {
                NativeMemory.AlignedFree((void*)segmentToFree.BasePtr);
            }
        }
        else
        {
            segmentToFree?.ExternalOwner?.Dispose();
        }
    }

    public static IntPtr AllocAnonPage(
        AllocationClass allocationClass = AllocationClass.KernelInternal,
        AllocationSource allocationSource = AllocationSource.Unknown)
    {
        ValidateManagedAllocationClass(allocationClass);
        var state = CurrentState;
        var overQuota = state.MemoryQuotaBytes > 0 &&
                        GlobalMemoryAccounting.GetTotalTrackedBytes() + LinuxConstants.PageSize > state.MemoryQuotaBytes;
        if (!TryAllocatePooledPage(state, allocationClass, allocationSource, out var ptr))
            return IntPtr.Zero;

        if (overQuota) Interlocked.Increment(ref state.LegacyAllocOverQuota);
        return ptr;
    }

    public static bool TryAllocAnonPageMayFail(
        out IntPtr ptr,
        AllocationClass allocationClass,
        AllocationSource allocationSource = AllocationSource.Unknown)
    {
        ValidateManagedAllocationClass(allocationClass);
        var state = CurrentState;
        ptr = IntPtr.Zero;
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
                Interlocked.Increment(ref state.StrictAllocFail);
                return false;
            }
        }

        ptr = AllocAnonPage(allocationClass, allocationSource);
        if (ptr == IntPtr.Zero)
        {
            Interlocked.Increment(ref state.StrictAllocFail);
            return false;
        }

        if (reclaimed)
            Interlocked.Increment(ref state.StrictAllocReclaimSuccess);
        else
            Interlocked.Increment(ref state.StrictAllocSuccess);
        return true;
    }

    private static bool TryAllocatePooledPage(State state, AllocationClass allocationClass,
        AllocationSource allocationSource, out IntPtr ptr)
    {
        lock (state.GlobalLock)
        {
            while (state.NonFullPooledSegments.Count > 0)
            {
                var segment = state.NonFullPooledSegments[^1];
                if (TryAllocateFromPoolSegmentLocked(state, segment, allocationClass, allocationSource, out ptr))
                    return true;

                RemoveNonFullSegmentLocked(state, segment);
            }

            var created = CreatePooledSegmentLocked(state);
            if (created != null &&
                TryAllocateFromPoolSegmentLocked(state, created, allocationClass, allocationSource, out ptr))
                return true;
        }

        ptr = IntPtr.Zero;
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
                Owned = true,
                BackingKind = SegmentBackingKind.PooledAlignedAlloc,
                LivePages = 0,
                ExternalOwner = null,
                PoolBitmap = new ulong[PooledSegmentWordCount]
            };
            state.Segments[segment.SegmentId] = segment;
            state.PooledSegments.Add(segment);
            AddNonFullSegmentLocked(state, segment);
            return segment;
        }
    }

    private static bool TryAllocateFromPoolSegmentLocked(State state, SegmentEntry segment, AllocationClass allocationClass,
        AllocationSource allocationSource, out IntPtr ptr)
    {
        if (segment.PoolBitmap == null || segment.LivePages >= segment.PageCount)
        {
            ptr = IntPtr.Zero;
            return false;
        }

        var pageIndex = FindFirstZeroUniversal(segment.PoolBitmap, segment.NextFreeWordHint);
        if (pageIndex < 0 || pageIndex >= segment.PageCount)
        {
            ptr = IntPtr.Zero;
            return false;
        }

        SetPoolBit(segment, pageIndex);
        segment.LivePages++;
        if (segment.LivePages >= segment.PageCount)
            RemoveNonFullSegmentLocked(state, segment);

        var pagePtr = segment.BasePtr + pageIndex * LinuxConstants.PageSize;
        state.PageRefs[pagePtr] = new PageRefEntry
        {
            SegmentId = segment.SegmentId,
            PageIndex = pageIndex,
            Ptr = pagePtr,
            Class = allocationClass,
            Source = allocationSource,
            RefCount = 1
        };
        Interlocked.Increment(ref state.TotalAllocatedPages);
        Interlocked.Increment(ref state.AllocPagesByClass[(int)allocationClass]);
        Interlocked.Increment(ref state.AllocPagesBySource[(int)allocationSource]);
        ptr = pagePtr;
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
        var next = GlobalMemoryAccounting.GetTotalTrackedBytes() + LinuxConstants.PageSize;
        return next <= state.MemoryQuotaBytes;
    }

    public static void FreeAnonPage(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return;
        ReleaseGlobalRef(ptr);
    }

    private static void ValidateManagedAllocationClass(AllocationClass allocationClass)
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
        public readonly Dictionary<nint, PageRefEntry> PageRefs = new();
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

    private enum SegmentBackingKind
    {
        ExternalUnknown,
        PooledAlignedAlloc
    }

    private sealed class SegmentEntry
    {
        public required SegmentBackingKind BackingKind;
        public required nint BasePtr;
        public IDisposable? ExternalOwner;
        public int LivePages;
        public int NextFreeWordHint;
        public int NonFullListIndex = -1;
        public required bool Owned;
        public required int PageCount;
        public ulong[]? PoolBitmap;
        public long SegmentId;
    }

    private sealed class PageRefEntry
    {
        public required AllocationClass Class;
        public required int PageIndex;
        public required nint Ptr;
        public int RefCount;
        public required long SegmentId;
        public required AllocationSource Source;
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
        }
    }
}
