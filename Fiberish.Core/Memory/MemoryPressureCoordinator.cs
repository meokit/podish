using Fiberish.Core;

namespace Fiberish.Memory;

public readonly record struct MemoryPressureResult(long ReclaimedBytes, int UnmappedPages)
{
    public bool MadeProgress => ReclaimedBytes > 0 || UnmappedPages > 0;
}

public sealed class MemoryPressureCoordinator
{
    private readonly AddressSpacePolicy _addressSpacePolicy;

    public MemoryPressureCoordinator(AddressSpacePolicy addressSpacePolicy)
    {
        ArgumentNullException.ThrowIfNull(addressSpacePolicy);
        _addressSpacePolicy = addressSpacePolicy;
    }

    public long TryReclaimForAllocation(long targetBytes, AllocationClass allocationClass,
        AllocationSource allocationSource)
    {
        _ = allocationSource;
        if (targetBytes <= 0) return 0;
        if (allocationClass == AllocationClass.Readahead) return 0;
        return _addressSpacePolicy.TryReclaimBytes(targetBytes);
    }

    public MemoryPressureResult TryRelieveFault(VMAManager addressSpace, Engine engine, long targetBytes,
        int targetMappedPages)
    {
        if (targetBytes <= 0 && targetMappedPages <= 0) return default;
        var unmappedPages = targetMappedPages > 0
            ? addressSpace.DropMappedCleanRecoverablePagesForPressure(engine, targetMappedPages)
            : 0;
        var reclaimedBytes = targetBytes > 0 ? _addressSpacePolicy.TryReclaimBytes(targetBytes) : 0;
        return new MemoryPressureResult(reclaimedBytes, unmappedPages);
    }
}
