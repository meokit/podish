using Fiberish.Memory;
using Fiberish.Native;
using Xunit;

namespace Fiberish.Tests.Memory;

[Collection("ExternalPageManagerSerial")]
public class MemoryPressureCoordinatorTests
{
    [Fact]
    public void TryReclaimForAllocation_ReclaimsCleanFilePageCache()
    {
        using var pageScope = ExternalPageManager.BeginIsolatedScope();
        using var cacheScope = GlobalPageCacheManager.BeginIsolatedScope();
        var cache = new MemoryObject(MemoryObjectKind.File, null, 0, 0, true);
        try
        {
            GlobalPageCacheManager.TrackPageCache(cache, GlobalPageCacheManager.PageCacheClass.File);
            var page = cache.GetOrCreatePage(0, _ => true, out _, strictQuota: true, AllocationClass.PageCache);
            Assert.NotEqual(IntPtr.Zero, page);
            Assert.Equal(1, cache.PageCount);

            var reclaimed = MemoryPressureCoordinator.TryReclaimForAllocation(
                LinuxConstants.PageSize,
                AllocationClass.Anonymous,
                AllocationSource.AnonFault);
            Assert.True(reclaimed >= LinuxConstants.PageSize);
            Assert.Equal(0, cache.PageCount);
        }
        finally
        {
            cache.Release();
        }
    }

    [Fact]
    public void TryReclaimForAllocation_Readahead_DoesNotTriggerReclaim()
    {
        using var pageScope = ExternalPageManager.BeginIsolatedScope();
        using var cacheScope = GlobalPageCacheManager.BeginIsolatedScope();
        var cache = new MemoryObject(MemoryObjectKind.File, null, 0, 0, true);
        try
        {
            GlobalPageCacheManager.TrackPageCache(cache, GlobalPageCacheManager.PageCacheClass.File);
            var page = cache.GetOrCreatePage(0, _ => true, out _, strictQuota: true, AllocationClass.PageCache);
            Assert.NotEqual(IntPtr.Zero, page);
            Assert.Equal(1, cache.PageCount);

            var reclaimed = MemoryPressureCoordinator.TryReclaimForAllocation(
                LinuxConstants.PageSize,
                AllocationClass.Readahead,
                AllocationSource.Unknown);
            Assert.Equal(0, reclaimed);
            Assert.Equal(1, cache.PageCount);
        }
        finally
        {
            cache.Release();
        }
    }
}
