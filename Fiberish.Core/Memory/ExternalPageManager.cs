using System.Runtime.InteropServices;
using System.Threading;
using System.Linq;
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

public sealed class ExternalPageManager
{
    private sealed class GlobalRefEntry
    {
        public int RefCount;
        public AllocationClass Class;
    }

    private static readonly Dictionary<nint, GlobalRefEntry> GlobalRefs = new();
    private static readonly object GlobalLock = new();
    private static long _strictAllocSuccess;
    private static long _strictAllocReclaimSuccess;
    private static long _strictAllocFail;
    private static long _legacyAllocOverQuota;
    private static readonly long[] AllocPagesByClass = new long[Enum.GetValues<AllocationClass>().Length];
    private static readonly long[] FreedPagesByClass = new long[Enum.GetValues<AllocationClass>().Length];

    private readonly Dictionary<uint, IntPtr> _pages = new();

    public static long MemoryQuotaBytes { get; set; } = 256L * 1024 * 1024;

    public bool TryGet(uint pageAddr, out IntPtr ptr)
    {
        if (_pages.TryGetValue(pageAddr, out var page))
        {
            ptr = page;
            return true;
        }

        ptr = IntPtr.Zero;
        return false;
    }

    public IntPtr GetOrAllocate(uint pageAddr, out bool isNew, bool strictQuota = false,
        AllocationClass allocationClass = AllocationClass.KernelInternal)
    {
        if (_pages.TryGetValue(pageAddr, out var page))
        {
            isNew = false;
            return page;
        }

        IntPtr ptr;
        if (strictQuota)
        {
            if (!TryAllocateExternalPageStrict(out ptr, allocationClass))
            {
                isNew = false;
                return IntPtr.Zero;
            }
        }
        else
        {
            ptr = AllocateExternalPage(allocationClass);
        }

        if (ptr == IntPtr.Zero)
        {
            isNew = false;
            return IntPtr.Zero;
        }

        _pages[pageAddr] = ptr;
        isNew = true;
        return ptr;
    }

    public bool AddMapping(uint pageAddr, IntPtr ptr, out bool addedRef)
    {
        addedRef = false;
        if (ptr == IntPtr.Zero) return false;
        if (_pages.TryGetValue(pageAddr, out var existing))
        {
            return existing == ptr;
        }

        AddGlobalRef(ptr);
        addedRef = true;
        _pages[pageAddr] = ptr;
        return true;
    }

    public void Release(uint pageAddr)
    {
        if (!_pages.TryGetValue(pageAddr, out var ptr)) return;
        _pages.Remove(pageAddr);
        ReleaseGlobalRef(ptr);
    }

    public void ReleaseRange(uint addr, uint length)
    {
        if (length == 0) return;
        var start = addr & LinuxConstants.PageMask;
        var end = (addr + length + LinuxConstants.PageOffsetMask) & LinuxConstants.PageMask;
        for (var p = start; p < end; p += LinuxConstants.PageSize) Release(p);
    }

    public void Clear()
    {
        foreach (var pageAddr in _pages.Keys.ToArray()) Release(pageAddr);
    }

    public static void AddRef(IntPtr ptr)
    {
        AddGlobalRef(ptr);
    }

    public static int GetRefCount(IntPtr ptr)
    {
        lock (GlobalLock)
        {
            return GlobalRefs.TryGetValue(ptr, out var entry) ? entry.RefCount : 0;
        }
    }

    public static long GetAllocatedPageCount()
    {
        lock (GlobalLock)
        {
            return GlobalRefs.Count;
        }
    }

    public static long GetAllocatedBytes()
    {
        return GetAllocatedPageCount() * LinuxConstants.PageSize;
    }

    public static (long StrictSuccess, long StrictReclaimSuccess, long StrictFail, long LegacyOverQuota)
        GetAllocationStats()
    {
        return (
            Interlocked.Read(ref _strictAllocSuccess),
            Interlocked.Read(ref _strictAllocReclaimSuccess),
            Interlocked.Read(ref _strictAllocFail),
            Interlocked.Read(ref _legacyAllocOverQuota));
    }

    public readonly record struct AllocationClassStat(
        AllocationClass Class,
        long AllocatedPages,
        long FreedPages,
        long LivePages);

    public static IReadOnlyList<AllocationClassStat> GetAllocationClassStats()
    {
        var classes = Enum.GetValues<AllocationClass>();
        var stats = new List<AllocationClassStat>(classes.Length);
        foreach (var cls in classes)
        {
            var idx = (int)cls;
            var allocated = Interlocked.Read(ref AllocPagesByClass[idx]);
            var freed = Interlocked.Read(ref FreedPagesByClass[idx]);
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

    private static void AddGlobalRef(IntPtr ptr, AllocationClass? allocationClass = null)
    {
        lock (GlobalLock)
        {
            var key = (nint)ptr;
            if (GlobalRefs.TryGetValue(key, out var existing))
            {
                existing.RefCount++;
                return;
            }

            var cls = allocationClass ?? AllocationClass.KernelInternal;
            GlobalRefs[key] = new GlobalRefEntry { RefCount = 1, Class = cls };
            Interlocked.Increment(ref AllocPagesByClass[(int)cls]);
        }
    }

    private static void ReleaseGlobalRef(IntPtr ptr)
    {
        AllocationClass allocationClass;
        lock (GlobalLock)
        {
            var key = (nint)ptr;
            if (!GlobalRefs.TryGetValue(key, out var entry)) return;
            entry.RefCount--;
            if (entry.RefCount > 0)
            {
                return;
            }

            allocationClass = entry.Class;
            GlobalRefs.Remove(key);
        }

        Interlocked.Increment(ref FreedPagesByClass[(int)allocationClass]);

        unsafe
        {
            NativeMemory.AlignedFree((void*)ptr);
        }
    }

    public static IntPtr AllocateExternalPage(AllocationClass allocationClass = AllocationClass.KernelInternal)
    {
        var overQuota = MemoryQuotaBytes > 0 && GetAllocatedBytes() + LinuxConstants.PageSize > MemoryQuotaBytes;
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

        AddGlobalRef(ptr, allocationClass);
        if (overQuota) Interlocked.Increment(ref _legacyAllocOverQuota);
        return ptr;
    }

    public static bool TryAllocateExternalPageStrict(out IntPtr ptr, AllocationClass allocationClass)
    {
        ptr = IntPtr.Zero;
        var reclaimed = false;
        if (!HasQuotaCapacity())
        {
            // Best-effort one-shot reclaim before failing strict allocation.
            if (allocationClass != AllocationClass.Readahead)
                reclaimed = GlobalPageCacheManager.TryReclaimBytes(LinuxConstants.PageSize) > 0;

            if (!HasQuotaCapacity())
            {
                Interlocked.Increment(ref _strictAllocFail);
                return false;
            }
        }

        ptr = AllocateExternalPage(allocationClass);
        if (ptr == IntPtr.Zero)
        {
            Interlocked.Increment(ref _strictAllocFail);
            return false;
        }

        if (reclaimed)
            Interlocked.Increment(ref _strictAllocReclaimSuccess);
        else
            Interlocked.Increment(ref _strictAllocSuccess);
        return true;
    }

    private static bool HasQuotaCapacity()
    {
        if (MemoryQuotaBytes <= 0) return true;
        var next = GetAllocatedBytes() + LinuxConstants.PageSize;
        return next <= MemoryQuotaBytes;
    }

    public static void AddRefPtr(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return;
        AddGlobalRef(ptr);
    }

    public static void ReleasePtr(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return;
        ReleaseGlobalRef(ptr);
    }
}