using System.Runtime.InteropServices;
using Fiberish.Native;

namespace Fiberish.Memory;

public sealed class ExternalPageManager
{
    private static readonly Dictionary<nint, int> GlobalRefs = new();
    private static readonly object GlobalLock = new();

    private readonly Dictionary<uint, IntPtr> _pages = new();

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

    public IntPtr GetOrAllocate(uint pageAddr, out bool isNew)
    {
        if (_pages.TryGetValue(pageAddr, out var page))
        {
            isNew = false;
            return page;
        }

        var ptr = AllocateExternalPage();
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
        return ptr;
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