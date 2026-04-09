using System.IO.MemoryMappedFiles;
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
    AnonMapPreFault,
    CowFirstPrivate,
    CowReplacePrivate
}

public enum ExternalPageBackend
{
    AlignedAlloc,
    MmapAnonymous
}

public enum MappedPageOwnerKind
{
    AddressSpace,
    AnonVma
}

internal sealed class MappedPageBinding
{
    public required HostPage HostPage { get; init; }
    public IntPtr Ptr => HostPage.Ptr;
    public required MappedPageOwnerKind OwnerKind { get; init; }
    public AddressSpace? Mapping { get; init; }
    public AnonVma? AnonVma { get; init; }
    public VmPage? Page { get; init; }
    public uint PageIndex { get; init; }

    internal static MappedPageBinding FromAddressSpacePage(AddressSpace mapping, uint pageIndex, VmPage page)
    {
        return new MappedPageBinding
        {
            HostPage = page.HostPage,
            OwnerKind = MappedPageOwnerKind.AddressSpace,
            Mapping = mapping,
            Page = page,
            PageIndex = pageIndex
        };
    }

    internal static MappedPageBinding FromAnonVmaPage(AnonVma anonVma, uint pageIndex, VmPage page)
    {
        return new MappedPageBinding
        {
            HostPage = page.HostPage,
            OwnerKind = MappedPageOwnerKind.AnonVma,
            AnonVma = anonVma,
            Page = page,
            PageIndex = pageIndex
        };
    }
}

public sealed class PageManager
{
    private static readonly AsyncLocal<State?> ScopedState = new();
    private static readonly State DefaultState = new();

    private readonly Dictionary<uint, MappedPageBinding> _pages = new();

    private static State CurrentState => ScopedState.Value ?? DefaultState;

    public static long MemoryQuotaBytes
    {
        get => CurrentState.MemoryQuotaBytes;
        set => CurrentState.MemoryQuotaBytes = value;
    }

    public static ExternalPageBackend PreferredBackend
    {
        get => CurrentState.PreferredBackend;
        set => CurrentState.PreferredBackend = value;
    }

    public static IDisposable BeginIsolatedScope()
    {
        var previous = ScopedState.Value;
        ScopedState.Value = new State();
        return new ScopeRestore(previous, HostPageManager.BeginIsolatedScope());
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

        if (!ZeroPageProvider.IsZeroPage(binding.Ptr))
        {
            AddGlobalRef(binding.Ptr);
            addedRef = true;
        }

        binding.HostPage.MapCount++;
        _pages[pageAddr] = binding;
        return true;
    }

    public void Release(uint pageAddr, bool preserveOwnerBinding = false)
    {
        if (!_pages.TryGetValue(pageAddr, out var binding)) return;
        _pages.Remove(pageAddr);
        if (binding.HostPage.MapCount > 0)
            binding.HostPage.MapCount--;

        if (!ZeroPageProvider.IsZeroPage(binding.Ptr))
            ReleaseGlobalRef(binding.Ptr);

        if (!preserveOwnerBinding &&
            binding.OwnerKind == MappedPageOwnerKind.AnonVma &&
            binding.AnonVma is { } anonVma &&
            binding.Page is { } anonPage &&
            anonPage.HostPage is { MapCount: <= 0, PinCount: <= 0, RefCount: <= 0 })
            anonVma.RemovePageIfMatches(binding.PageIndex, anonPage);

        HostPageManager.TryRemoveIfUnused(binding.HostPage);
    }

    public void ReleaseRange(uint addr, uint length, bool preserveOwnerBinding = false)
    {
        if (length == 0) return;
        var start = addr & LinuxConstants.PageMask;
        var end = (addr + length + LinuxConstants.PageOffsetMask) & LinuxConstants.PageMask;
        for (var p = start; p < end; p += LinuxConstants.PageSize) Release(p, preserveOwnerBinding);
    }

    public void Clear()
    {
        foreach (var pageAddr in _pages.Keys.ToArray()) Release(pageAddr);
    }

    public IReadOnlyList<uint> SnapshotMappedPages()
    {
        if (_pages.Count == 0) return Array.Empty<uint>();
        return _pages.Keys.ToArray();
    }

    public static void AddRef(IntPtr ptr)
    {
        if (ZeroPageProvider.IsZeroPage(ptr)) return;
        AddGlobalRef(ptr);
    }

    public static int GetRefCount(IntPtr ptr)
    {
        if (ZeroPageProvider.IsZeroPage(ptr)) return 0;
        var state = CurrentState;
        lock (state.GlobalLock)
        {
            return state.PageRefs.TryGetValue(ptr, out var entry) ? entry.RefCount : 0;
        }
    }

    public static long GetAllocatedPageCount()
    {
        var state = CurrentState;
        lock (state.GlobalLock)
        {
            return state.PageRefs.Count;
        }
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
                AllocationBytes = LinuxConstants.PageSize,
                MappedFile = null,
                ViewAccessor = null,
                RawViewPointer = 0,
                PointerAcquired = false,
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
            Interlocked.Increment(ref state.AllocPagesByClass[(int)cls]);
            Interlocked.Increment(ref state.AllocPagesBySource[(int)source]);
        }
    }

    private static void RegisterOwnedSinglePage(State state, nint ptr, AllocationClass allocationClass,
        AllocationSource allocationSource)
    {
        var segmentId = Interlocked.Increment(ref state.NextSegmentId);
        state.Segments[segmentId] = new SegmentEntry
        {
            BasePtr = ptr,
            PageCount = 1,
            Owned = true,
            BackingKind = SegmentBackingKind.AlignedAlloc,
            AllocationBytes = LinuxConstants.PageSize,
            MappedFile = null,
            ViewAccessor = null,
            RawViewPointer = 0,
            PointerAcquired = false,
            LivePages = 1,
            ExternalOwner = null
        };

        state.PageRefs[ptr] = new PageRefEntry
        {
            SegmentId = segmentId,
            PageIndex = 0,
            Ptr = ptr,
            Class = allocationClass,
            Source = allocationSource,
            RefCount = 1
        };
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

            if (state.Segments.TryGetValue(entry.SegmentId, out var segment))
            {
                segment.LivePages--;
                if (segment.LivePages <= 0)
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
            if (segmentToFree.BackingKind == SegmentBackingKind.MmapAnonymous)
            {
                if (segmentToFree.PointerAcquired && segmentToFree.ViewAccessor != null)
                    segmentToFree.ViewAccessor.SafeMemoryMappedViewHandle.ReleasePointer();

                segmentToFree.ViewAccessor?.Dispose();
                segmentToFree.MappedFile?.Dispose();
            }
            else
            {
                unsafe
                {
                    NativeMemory.AlignedFree((void*)segmentToFree.BasePtr);
                }
            }
        }
        else
        {
            segmentToFree?.ExternalOwner?.Dispose();
        }
    }

    public static IntPtr AllocateExternalPage(
        AllocationClass allocationClass = AllocationClass.KernelInternal,
        AllocationSource allocationSource = AllocationSource.Unknown)
    {
        var state = CurrentState;
        var overQuota = state.MemoryQuotaBytes > 0 &&
                        GetAllocatedBytes() + LinuxConstants.PageSize > state.MemoryQuotaBytes;
        IntPtr ptr;
        unsafe
        {
            ptr = (IntPtr)NativeMemory.AlignedAlloc(LinuxConstants.PageSize, LinuxConstants.PageSize);
        }

        if (ptr == IntPtr.Zero) return IntPtr.Zero;

        unsafe
        {
            new Span<byte>((void*)ptr, LinuxConstants.PageSize).Clear();
        }

        lock (state.GlobalLock)
        {
            RegisterOwnedSinglePage(state, ptr, allocationClass, allocationSource);
        }

        Interlocked.Increment(ref state.AllocPagesByClass[(int)allocationClass]);
        Interlocked.Increment(ref state.AllocPagesBySource[(int)allocationSource]);

        if (overQuota) Interlocked.Increment(ref state.LegacyAllocOverQuota);
        return ptr;
    }

    public static bool TryAllocateExternalContiguousStrict(
        out IntPtr basePtr,
        int pageCount,
        AllocationClass allocationClass,
        AllocationSource allocationSource = AllocationSource.Unknown)
    {
        var state = CurrentState;
        basePtr = IntPtr.Zero;
        if (pageCount <= 0) return false;

        var bytesLong = (long)pageCount * LinuxConstants.PageSize;
        var reclaimed = false;

        if (state.MemoryQuotaBytes > 0 && GetAllocatedBytes() + bytesLong > state.MemoryQuotaBytes)
        {
            reclaimed =
                MemoryPressureCoordinator.TryReclaimForAllocation(bytesLong, allocationClass, allocationSource) >
                0;

            if (state.MemoryQuotaBytes > 0 && GetAllocatedBytes() + bytesLong > state.MemoryQuotaBytes)
            {
                Interlocked.Increment(ref state.StrictAllocFail);
                return false;
            }
        }

        var bytes = (nuint)bytesLong;
        nint ptr = 0;
        var backingKind = SegmentBackingKind.AlignedAlloc;
        MemoryMappedFile? mappedFile = null;
        MemoryMappedViewAccessor? viewAccessor = null;
        nint rawViewPointer = 0;
        var pointerAcquired = false;
        if (state.PreferredBackend == ExternalPageBackend.MmapAnonymous)
        {
            try
            {
                mappedFile = MemoryMappedFile.CreateNew(null, bytesLong, MemoryMappedFileAccess.ReadWrite);
                viewAccessor = mappedFile.CreateViewAccessor(0, bytesLong, MemoryMappedFileAccess.ReadWrite);
                unsafe
                {
                    byte* raw = null;
                    viewAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref raw);
                    if (raw != null)
                    {
                        pointerAcquired = true;
                        rawViewPointer = (nint)raw;
                        ptr = rawViewPointer + (nint)viewAccessor.PointerOffset;
                        backingKind = SegmentBackingKind.MmapAnonymous;
                    }
                }
            }
            catch
            {
                ptr = 0;
            }

            if (ptr == 0)
            {
                if (pointerAcquired && viewAccessor != null) viewAccessor.SafeMemoryMappedViewHandle.ReleasePointer();

                viewAccessor?.Dispose();
                mappedFile?.Dispose();
                viewAccessor = null;
                mappedFile = null;
                pointerAcquired = false;
                rawViewPointer = 0;
            }
        }

        if (ptr == 0)
        {
            unsafe
            {
                ptr = (nint)NativeMemory.AlignedAlloc(bytes, LinuxConstants.PageSize);
            }

            backingKind = SegmentBackingKind.AlignedAlloc;
        }

        if (ptr == 0)
        {
            Interlocked.Increment(ref state.StrictAllocFail);
            return false;
        }

        unsafe
        {
            new Span<byte>((void*)ptr, (int)bytes).Clear();
        }

        lock (state.GlobalLock)
        {
            var segmentId = Interlocked.Increment(ref state.NextSegmentId);
            state.Segments[segmentId] = new SegmentEntry
            {
                BasePtr = ptr,
                PageCount = pageCount,
                Owned = true,
                BackingKind = backingKind,
                AllocationBytes = bytes,
                MappedFile = mappedFile,
                ViewAccessor = viewAccessor,
                RawViewPointer = rawViewPointer,
                PointerAcquired = pointerAcquired,
                LivePages = pageCount,
                ExternalOwner = null
            };

            for (var i = 0; i < pageCount; i++)
            {
                var pagePtr = ptr + i * LinuxConstants.PageSize;
                state.PageRefs[pagePtr] = new PageRefEntry
                {
                    SegmentId = segmentId,
                    PageIndex = i,
                    Ptr = pagePtr,
                    Class = allocationClass,
                    Source = allocationSource,
                    RefCount = 1
                };
            }
        }

        Interlocked.Add(ref state.AllocPagesByClass[(int)allocationClass], pageCount);
        Interlocked.Add(ref state.AllocPagesBySource[(int)allocationSource], pageCount);

        if (reclaimed)
            Interlocked.Increment(ref state.StrictAllocReclaimSuccess);
        else
            Interlocked.Increment(ref state.StrictAllocSuccess);

        basePtr = ptr;
        return true;
    }

    public static bool TryAllocateExternalPageStrict(
        out IntPtr ptr,
        AllocationClass allocationClass,
        AllocationSource allocationSource = AllocationSource.Unknown)
    {
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

        ptr = AllocateExternalPage(allocationClass, allocationSource);
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

    private static bool HasQuotaCapacity()
    {
        var state = CurrentState;
        if (state.MemoryQuotaBytes <= 0) return true;
        var next = GetAllocatedBytes() + LinuxConstants.PageSize;
        return next <= state.MemoryQuotaBytes;
    }

    public static void AddRefPtr(IntPtr ptr, IDisposable? externalOwner = null)
    {
        if (ptr == IntPtr.Zero) return;
        if (ZeroPageProvider.IsZeroPage(ptr))
        {
            externalOwner?.Dispose();
            return;
        }

        AddGlobalRef(ptr, externalOwner: externalOwner);
    }

    public static void ReleasePtr(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return;
        if (ZeroPageProvider.IsZeroPage(ptr)) return;
        ReleaseGlobalRef(ptr);
    }

    private sealed class State
    {
        public readonly long[] AllocPagesByClass = new long[Enum.GetValues<AllocationClass>().Length];
        public readonly long[] AllocPagesBySource = new long[Enum.GetValues<AllocationSource>().Length];
        public readonly long[] FreedPagesByClass = new long[Enum.GetValues<AllocationClass>().Length];
        public readonly long[] FreedPagesBySource = new long[Enum.GetValues<AllocationSource>().Length];
        public readonly Lock GlobalLock = new();
        public readonly Dictionary<nint, PageRefEntry> PageRefs = new();
        public readonly Dictionary<long, SegmentEntry> Segments = new();
        public long LegacyAllocOverQuota;
        public long MemoryQuotaBytes = 2L * 1024 * 1024 * 1024;
        public long NextSegmentId;
        public ExternalPageBackend PreferredBackend = ExternalPageBackend.AlignedAlloc;
        public long StrictAllocFail;
        public long StrictAllocReclaimSuccess;
        public long StrictAllocSuccess;
    }

    private sealed class ScopeRestore : IDisposable
    {
        private readonly IDisposable _hostPageScope;
        private readonly State? _previous;

        public ScopeRestore(State? previous, IDisposable hostPageScope)
        {
            _previous = previous;
            _hostPageScope = hostPageScope;
        }

        public void Dispose()
        {
            ScopedState.Value = _previous;
            _hostPageScope.Dispose();
        }
    }

    private enum SegmentBackingKind
    {
        ExternalUnknown,
        AlignedAlloc,
        MmapAnonymous
    }

    private sealed class SegmentEntry
    {
        public required nuint AllocationBytes;
        public required SegmentBackingKind BackingKind;
        public required nint BasePtr;
        public IDisposable? ExternalOwner;
        public int LivePages;
        public MemoryMappedFile? MappedFile;
        public required bool Owned;
        public required int PageCount;
        public bool PointerAcquired;
        public nint RawViewPointer;
        public MemoryMappedViewAccessor? ViewAccessor;
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
}
