using System.Runtime.InteropServices;
using Fiberish.Native;

namespace Fiberish.Memory;

internal static class GlobalMemoryAccounting
{
    public static long GetCachedBytes()
    {
        return AddressSpacePolicy.GetTotalCachedPages() * LinuxConstants.PageSize;
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
    public static PageHandle AllocatePage(
        AllocationClass allocationClass = AllocationClass.PageCache,
        AllocationSource allocationSource = AllocationSource.Unknown)
    {
        _ = allocationClass;
        _ = allocationSource;

        unsafe
        {
            var ptr = (nint)NativeMemory.AlignedAlloc(LinuxConstants.PageSize, LinuxConstants.PageSize);
            if (ptr == 0)
                return default;

            new Span<byte>((void*)ptr, LinuxConstants.PageSize).Clear();
            return PageHandle.CreateNative(ptr);
        }
    }

    public static bool TryAllocatePageStrict(
        out PageHandle pageHandle,
        AllocationClass allocationClass = AllocationClass.PageCache,
        AllocationSource allocationSource = AllocationSource.Unknown)
    {
        pageHandle = default;
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
        return pageHandle.IsValid;
    }
}
