using Fiberish.Memory;
using Fiberish.Native;
using Xunit;

namespace Fiberish.Tests.Memory;

[Collection("ExternalPageManagerSerial")]
public class ExternalPageManagerQuotaTests
{
    [Fact]
    public void StrictAllocation_Fails_WhenQuotaExceeded()
    {
        var oldQuota = ExternalPageManager.MemoryQuotaBytes;
        ExternalPageManager.MemoryQuotaBytes = LinuxConstants.PageSize - 1;
        try
        {
            var ok = ExternalPageManager.TryAllocateExternalPageStrict(out var ptr, AllocationClass.Anonymous);
            Assert.False(ok);
            Assert.Equal(IntPtr.Zero, ptr);
        }
        finally
        {
            ExternalPageManager.MemoryQuotaBytes = oldQuota;
        }
    }

    [Fact]
    public void StrictAllocation_Succeeds_WhenQuotaAllows()
    {
        var oldQuota = ExternalPageManager.MemoryQuotaBytes;
        ExternalPageManager.MemoryQuotaBytes = long.MaxValue;
        IntPtr ptr = IntPtr.Zero;
        try
        {
            var ok = ExternalPageManager.TryAllocateExternalPageStrict(out ptr, AllocationClass.Anonymous);
            Assert.True(ok);
            Assert.NotEqual(IntPtr.Zero, ptr);
        }
        finally
        {
            if (ptr != IntPtr.Zero) ExternalPageManager.ReleasePtr(ptr);
            ExternalPageManager.MemoryQuotaBytes = oldQuota;
        }
    }

    [Fact]
    public void LegacyAllocation_CanExceedQuota()
    {
        var oldQuota = ExternalPageManager.MemoryQuotaBytes;
        ExternalPageManager.MemoryQuotaBytes = LinuxConstants.PageSize - 1;
        IntPtr ptr = IntPtr.Zero;
        try
        {
            ptr = ExternalPageManager.AllocateExternalPage();
            Assert.NotEqual(IntPtr.Zero, ptr);
        }
        finally
        {
            if (ptr != IntPtr.Zero) ExternalPageManager.ReleasePtr(ptr);
            ExternalPageManager.MemoryQuotaBytes = oldQuota;
        }
    }

    [Fact]
    public void ContiguousStrictAllocation_Fails_WhenQuotaTooSmall()
    {
        var oldQuota = ExternalPageManager.MemoryQuotaBytes;
        ExternalPageManager.MemoryQuotaBytes = LinuxConstants.PageSize * 2L;
        try
        {
            var ok = ExternalPageManager.TryAllocateExternalContiguousStrict(
                out var basePtr,
                pageCount: 3,
                AllocationClass.Anonymous);
            Assert.False(ok);
            Assert.Equal(IntPtr.Zero, basePtr);
        }
        finally
        {
            ExternalPageManager.MemoryQuotaBytes = oldQuota;
        }
    }

    [Fact]
    public void ContiguousStrictAllocation_TracksPerPageRefCount_AndFreesOnLastRelease()
    {
        var oldQuota = ExternalPageManager.MemoryQuotaBytes;
        ExternalPageManager.MemoryQuotaBytes = long.MaxValue;
        var beforePages = ExternalPageManager.GetAllocatedPageCount();

        IntPtr basePtr = IntPtr.Zero;
        try
        {
            var ok = ExternalPageManager.TryAllocateExternalContiguousStrict(
                out basePtr,
                pageCount: 4,
                AllocationClass.Cow);
            Assert.True(ok);
            Assert.NotEqual(IntPtr.Zero, basePtr);
            Assert.Equal(beforePages + 4, ExternalPageManager.GetAllocatedPageCount());

            var p0 = basePtr;
            var p1 = basePtr + LinuxConstants.PageSize;

            Assert.Equal(1, ExternalPageManager.GetRefCount(p0));
            ExternalPageManager.AddRef(p0);
            Assert.Equal(2, ExternalPageManager.GetRefCount(p0));

            ExternalPageManager.ReleasePtr(p0);
            Assert.Equal(1, ExternalPageManager.GetRefCount(p0));

            // Release all pages from the segment.
            ExternalPageManager.ReleasePtr(p0);
            ExternalPageManager.ReleasePtr(p1);
            ExternalPageManager.ReleasePtr(basePtr + 2 * LinuxConstants.PageSize);
            ExternalPageManager.ReleasePtr(basePtr + 3 * LinuxConstants.PageSize);

            Assert.Equal(beforePages, ExternalPageManager.GetAllocatedPageCount());
        }
        finally
        {
            ExternalPageManager.MemoryQuotaBytes = oldQuota;
        }
    }

    [Fact]
    public void ContiguousStrictAllocation_MmapBackend_WorksAndReleases()
    {
        var oldQuota = ExternalPageManager.MemoryQuotaBytes;
        var oldBackend = ExternalPageManager.PreferredBackend;
        ExternalPageManager.MemoryQuotaBytes = long.MaxValue;
        ExternalPageManager.PreferredBackend = ExternalPageBackend.MmapAnonymous;
        var beforePages = ExternalPageManager.GetAllocatedPageCount();

        IntPtr basePtr = IntPtr.Zero;
        try
        {
            var ok = ExternalPageManager.TryAllocateExternalContiguousStrict(
                out basePtr,
                pageCount: 2,
                AllocationClass.PageCache);
            Assert.True(ok);
            Assert.NotEqual(IntPtr.Zero, basePtr);
            Assert.Equal(beforePages + 2, ExternalPageManager.GetAllocatedPageCount());

            ExternalPageManager.ReleasePtr(basePtr);
            ExternalPageManager.ReleasePtr(basePtr + LinuxConstants.PageSize);
            Assert.Equal(beforePages, ExternalPageManager.GetAllocatedPageCount());
        }
        finally
        {
            ExternalPageManager.PreferredBackend = oldBackend;
            ExternalPageManager.MemoryQuotaBytes = oldQuota;
        }
    }
}
