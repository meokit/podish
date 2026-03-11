using Fiberish.Core;
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
            GlobalPageCacheManager.TrackPageCache(cache);
            var page = cache.GetOrCreatePage(0, _ => true, out _, true, AllocationClass.PageCache);
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
            GlobalPageCacheManager.TrackPageCache(cache);
            var page = cache.GetOrCreatePage(0, _ => true, out _, true, AllocationClass.PageCache);
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

    [Fact]
    public void TryRelieveFault_Reclaims_ReadOnlyPrivateAnonymousSharedSource()
    {
        using var pageScope = ExternalPageManager.BeginIsolatedScope();
        using var cacheScope = GlobalPageCacheManager.BeginIsolatedScope();
        using var engine = new Engine();
        var mm = new VMAManager();
        engine.PageFaultResolver =
            (addr, isWrite) => mm.HandleFaultDetailed(addr, isWrite, engine) == FaultResult.Handled;

        const uint addr = 0x74000000;
        Assert.Equal(addr, mm.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
            MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, 0, "anon-reclaim", engine));

        Assert.True(engine.CopyFromUser(addr, new byte[1]));
        var vma = Assert.Single(mm.VMAs);
        var pageIndex = vma.ViewPageOffset;
        Assert.Equal(IntPtr.Zero, vma.SharedObject.GetPage(pageIndex));
        Assert.Equal(IntPtr.Zero, vma.PrivateObject!.GetPage(pageIndex));
        Assert.True(mm.ExternalPages.TryGet(addr, out _));

        var result = MemoryPressureCoordinator.TryRelieveFault(mm, engine, LinuxConstants.PageSize, 1);

        Assert.True(result.MadeProgress);
        Assert.False(mm.ExternalPages.TryGet(addr, out _));
        Assert.Equal(IntPtr.Zero, vma.SharedObject.GetPage(pageIndex));
        Assert.True(engine.CopyFromUser(addr, new byte[1]));
    }

    [Fact]
    public void TryRelieveFault_DoesNotReclaim_PrivateAnonymousOverlayPage()
    {
        using var pageScope = ExternalPageManager.BeginIsolatedScope();
        using var cacheScope = GlobalPageCacheManager.BeginIsolatedScope();
        using var engine = new Engine();
        var mm = new VMAManager();
        engine.PageFaultResolver =
            (addr, isWrite) => mm.HandleFaultDetailed(addr, isWrite, engine) == FaultResult.Handled;

        const uint addr = 0x74100000;
        Assert.Equal(addr, mm.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
            MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, 0, "anon-private-overlay", engine));

        Assert.True(engine.CopyToUser(addr, new byte[] { 0x5A }));
        var vma = Assert.Single(mm.VMAs);
        var pageIndex = vma.ViewPageOffset;
        var privatePage = vma.PrivateObject!.GetPage(pageIndex);
        Assert.NotEqual(IntPtr.Zero, privatePage);

        var result = MemoryPressureCoordinator.TryRelieveFault(mm, engine, LinuxConstants.PageSize, 1);

        Assert.False(result.MadeProgress);
        Assert.True(mm.ExternalPages.TryGet(addr, out _));
        Assert.Equal(privatePage, vma.PrivateObject.GetPage(pageIndex));
        var probe = new byte[1];
        Assert.True(engine.CopyFromUser(addr, probe));
        Assert.Equal((byte)0x5A, probe[0]);
    }
}