using System.Runtime.InteropServices;
using Fiberish.Native;

namespace Fiberish.Memory;

internal static class GlobalMemoryAccounting
{
    public static long GetCachedBytes()
    {
        return AddressSpacePolicy.GetAddressSpaceStats().TotalPages * LinuxConstants.PageSize;
    }

    public static long GetTotalTrackedBytes()
    {
        return PageManager.GetAllocatedBytes() + GetCachedBytes();
    }

    public static bool HasQuotaCapacity(long additionalBytes)
    {
        var quota = PageManager.MemoryQuotaBytes;
        if (quota <= 0)
            return true;

        return GetTotalTrackedBytes() + additionalBytes <= quota;
    }
}

internal static class InodePageAllocator
{
    public static IPageHandle? AllocatePage(
        AllocationClass allocationClass = AllocationClass.PageCache,
        AllocationSource allocationSource = AllocationSource.Unknown)
    {
        _ = allocationClass;
        _ = allocationSource;

        unsafe
        {
            var ptr = (nint)NativeMemory.AlignedAlloc((nuint)LinuxConstants.PageSize, LinuxConstants.PageSize);
            if (ptr == 0)
                return null;

            new Span<byte>((void*)ptr, LinuxConstants.PageSize).Clear();
            return new OwnedNativePageHandle((IntPtr)ptr);
        }
    }

    public static bool TryAllocatePageStrict(
        out IPageHandle? pageHandle,
        AllocationClass allocationClass = AllocationClass.PageCache,
        AllocationSource allocationSource = AllocationSource.Unknown)
    {
        pageHandle = null;
        if (!GlobalMemoryAccounting.HasQuotaCapacity(LinuxConstants.PageSize))
        {
            _ = MemoryPressureCoordinator.TryReclaimForAllocation(
                LinuxConstants.PageSize,
                allocationClass,
                allocationSource);

            if (!GlobalMemoryAccounting.HasQuotaCapacity(LinuxConstants.PageSize))
                return false;
        }

        pageHandle = AllocatePage(allocationClass, allocationSource);
        return pageHandle != null;
    }

    private sealed class OwnedNativePageHandle : IPageHandle
    {
        private int _disposed;

        public OwnedNativePageHandle(IntPtr pointer)
        {
            Pointer = pointer;
        }

        public IntPtr Pointer { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            unsafe
            {
                NativeMemory.AlignedFree((void*)Pointer);
            }
        }
    }
}