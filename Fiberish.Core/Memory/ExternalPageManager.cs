using System.Runtime.InteropServices;
using System.Threading;
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
    private static readonly Dictionary<nint, int> GlobalRefs = new();
    private static readonly object GlobalLock = new();
    private static long _strictAllocSuccess;
    private static long _strictAllocReclaimSuccess;
    private static long _strictAllocFail;
    private static long _legacyAllocOverQuota;

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
            ptr = AllocateExternalPage();
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
            return GlobalRefs.TryGetValue(ptr, out var count) ? count : 0;
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

    private static void AddGlobalRef(IntPtr ptr)
    {
        lock (GlobalLock)
        {
            var key = (nint)ptr;
            if (!GlobalRefs.TryAdd(key, 1)) GlobalRefs[key]++;
        }
    }

    private static void ReleaseGlobalRef(IntPtr ptr)
    {
        lock (GlobalLock)
        {
            var key = (nint)ptr;
            if (!GlobalRefs.TryGetValue(key, out var count)) return;
            if (--count > 0)
            {
                GlobalRefs[key] = count;
                return;
            }

            GlobalRefs.Remove(key);
        }

        unsafe
        {
            NativeMemory.AlignedFree((void*)ptr);
        }
    }

    public static IntPtr AllocateExternalPage()
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

        AddGlobalRef(ptr);
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

        ptr = AllocateExternalPage();
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
