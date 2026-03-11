using Fiberish.Core;

namespace Fiberish.Memory;

public readonly record struct MemoryPressureResult(long ReclaimedBytes, int UnmappedPages)
{
    public bool MadeProgress => ReclaimedBytes > 0 || UnmappedPages > 0;
}

public static class MemoryPressureCoordinator
{
    public static long TryReclaimForAllocation(long targetBytes, AllocationClass allocationClass,
        AllocationSource allocationSource)
    {
        _ = allocationSource;
        if (targetBytes <= 0) return 0;
        if (allocationClass == AllocationClass.Readahead) return 0;
        return GlobalPageCacheManager.TryReclaimBytes(targetBytes);
    }

    public static MemoryPressureResult TryRelieveFault(VMAManager addressSpace, Engine engine, long targetBytes,
        int targetMappedPages)
    {
        if (targetBytes <= 0 && targetMappedPages <= 0) return default;
        var unmappedPages = targetMappedPages > 0
            ? addressSpace.DropMappedCleanRecoverablePagesForPressure(engine, targetMappedPages)
            : 0;
        var reclaimedBytes = targetBytes > 0 ? GlobalPageCacheManager.TryReclaimBytes(targetBytes) : 0;
        return new MemoryPressureResult(reclaimedBytes, unmappedPages);
    }
}
