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
        using var pageScope = PageManager.BeginIsolatedScope();
        using var cacheScope = AddressSpacePolicy.BeginIsolatedScope();
        var cache = new AddressSpace(AddressSpaceKind.File);
        try
        {
            AddressSpacePolicy.TrackAddressSpace(cache);
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
        using var pageScope = PageManager.BeginIsolatedScope();
        using var cacheScope = AddressSpacePolicy.BeginIsolatedScope();
        var cache = new AddressSpace(AddressSpaceKind.File);
        try
        {
            AddressSpacePolicy.TrackAddressSpace(cache);
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
    public void GetAddressSpaceStats_CountsPagesWithoutSnapshottingPageStates()
    {
        using var pageScope = PageManager.BeginIsolatedScope();
        using var cacheScope = AddressSpacePolicy.BeginIsolatedScope();
        var fileCache = new AddressSpace(AddressSpaceKind.File);
        var shmemCache = new AddressSpace(AddressSpaceKind.Shmem);
        try
        {
            AddressSpacePolicy.TrackAddressSpace(fileCache);
            AddressSpacePolicy.TrackAddressSpace(shmemCache, AddressSpacePolicy.AddressSpaceCacheClass.Shmem);

            Assert.NotEqual(IntPtr.Zero,
                fileCache.GetOrCreatePage(0, _ => true, out _, true, AllocationClass.PageCache));
            Assert.NotEqual(IntPtr.Zero,
                fileCache.GetOrCreatePage(1, _ => true, out _, true, AllocationClass.PageCache));
            fileCache.MarkDirty(1);

            Assert.NotEqual(IntPtr.Zero,
                shmemCache.GetOrCreatePage(0, _ => true, out _, true, AllocationClass.PageCache));
            shmemCache.MarkDirty(0);

            var stats = AddressSpacePolicy.GetAddressSpaceStats();

            Assert.Equal(3, stats.TotalPages);
            Assert.Equal(1, stats.CleanPages);
            Assert.Equal(2, stats.DirtyPages);
            Assert.Equal(1, stats.ShmemPages);
            Assert.Equal(0, stats.WritebackPages);
            Assert.Equal(3L * LinuxConstants.PageSize, PageManager.GetCachedBytes());
        }
        finally
        {
            fileCache.Release();
            shmemCache.Release();
        }
    }

    [Fact]
    public void TryRelieveFault_Reclaims_ReadOnlyPrivateAnonymousSharedSource()
    {
        using var pageScope = PageManager.BeginIsolatedScope();
        using var cacheScope = AddressSpacePolicy.BeginIsolatedScope();
        var runtime = new TestRuntimeFactory();
        using var engine = runtime.CreateEngine();
        var mm = runtime.CreateAddressSpace();
        engine.PageFaultResolver =
            (addr, isWrite) => mm.HandleFaultDetailed(addr, isWrite, engine) == FaultResult.Handled;

        const uint addr = 0x74000000;
        Assert.Equal(addr, mm.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
            MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "anon-reclaim", engine));

        Assert.True(engine.CopyFromUser(addr, new byte[1]));
        var vma = Assert.Single(mm.VMAs);
        var pageIndex = vma.GetPageIndex(vma.Start);
        Assert.NotNull(vma.VmMapping);
        Assert.True(vma.VmMapping!.IsZeroBacking);
        Assert.Null(vma.VmAnonVma);
        Assert.True(mm.PageMapping.TryGet(addr, out _));

        var result = MemoryPressureCoordinator.TryRelieveFault(mm, engine, LinuxConstants.PageSize, 1);

        Assert.True(result.MadeProgress);
        Assert.False(mm.PageMapping.TryGet(addr, out _));
        Assert.NotNull(vma.VmMapping);
        Assert.True(vma.VmMapping!.IsZeroBacking);
        Assert.True(engine.CopyFromUser(addr, new byte[1]));
    }

    [Fact]
    public void TryRelieveFault_DoesNotReclaim_PrivateAnonymousOverlayPage()
    {
        using var pageScope = PageManager.BeginIsolatedScope();
        using var cacheScope = AddressSpacePolicy.BeginIsolatedScope();
        var runtime = new TestRuntimeFactory();
        using var engine = runtime.CreateEngine();
        var mm = runtime.CreateAddressSpace();
        engine.PageFaultResolver =
            (addr, isWrite) => mm.HandleFaultDetailed(addr, isWrite, engine) == FaultResult.Handled;

        const uint addr = 0x74100000;
        Assert.Equal(addr, mm.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
            MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "anon-private-overlay", engine));

        Assert.True(engine.CopyToUser(addr, new byte[] { 0x5A }));
        var vma = Assert.Single(mm.VMAs);
        var pageIndex = vma.GetPageIndex(vma.Start);
        var privatePage = vma.VmAnonVma!.GetPage(pageIndex);
        Assert.NotEqual(IntPtr.Zero, privatePage);

        var result = MemoryPressureCoordinator.TryRelieveFault(mm, engine, LinuxConstants.PageSize, 1);

        Assert.False(result.MadeProgress);
        Assert.True(mm.PageMapping.TryGet(addr, out _));
        Assert.Equal(privatePage, vma.VmAnonVma.GetPage(pageIndex));
        var probe = new byte[1];
        Assert.True(engine.CopyFromUser(addr, probe));
        Assert.Equal((byte)0x5A, probe[0]);
    }
}
