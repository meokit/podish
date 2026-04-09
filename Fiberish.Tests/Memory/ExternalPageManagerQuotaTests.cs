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
        var oldQuota = PageManager.MemoryQuotaBytes;
        PageManager.MemoryQuotaBytes = LinuxConstants.PageSize - 1;
        try
        {
            var ok = PageManager.TryAllocateExternalPageStrict(out var ptr, AllocationClass.Anonymous);
            Assert.False(ok);
            Assert.Equal(IntPtr.Zero, ptr);
        }
        finally
        {
            PageManager.MemoryQuotaBytes = oldQuota;
        }
    }

    [Fact]
    public void IsolatedScope_UsesTwoGiBDefaultQuota()
    {
        using var scope = PageManager.BeginIsolatedScope();
        Assert.Equal(2L * 1024 * 1024 * 1024, PageManager.MemoryQuotaBytes);
    }

    [Fact]
    public void StrictAllocation_Succeeds_WhenQuotaAllows()
    {
        var oldQuota = PageManager.MemoryQuotaBytes;
        PageManager.MemoryQuotaBytes = long.MaxValue;
        var ptr = IntPtr.Zero;
        try
        {
            var ok = PageManager.TryAllocateExternalPageStrict(out ptr, AllocationClass.Anonymous);
            Assert.True(ok);
            Assert.NotEqual(IntPtr.Zero, ptr);
        }
        finally
        {
            if (ptr != IntPtr.Zero) PageManager.ReleasePtr(ptr);
            PageManager.MemoryQuotaBytes = oldQuota;
        }
    }

    [Fact]
    public void LegacyAllocation_CanExceedQuota()
    {
        var oldQuota = PageManager.MemoryQuotaBytes;
        PageManager.MemoryQuotaBytes = LinuxConstants.PageSize - 1;
        var ptr = IntPtr.Zero;
        try
        {
            ptr = PageManager.AllocateExternalPage();
            Assert.NotEqual(IntPtr.Zero, ptr);
        }
        finally
        {
            if (ptr != IntPtr.Zero) PageManager.ReleasePtr(ptr);
            PageManager.MemoryQuotaBytes = oldQuota;
        }
    }

    [Fact]
    public void ContiguousStrictAllocation_Fails_WhenQuotaTooSmall()
    {
        var oldQuota = PageManager.MemoryQuotaBytes;
        PageManager.MemoryQuotaBytes = LinuxConstants.PageSize * 2L;
        try
        {
            var ok = PageManager.TryAllocateExternalContiguousStrict(
                out var basePtr,
                3,
                AllocationClass.Anonymous);
            Assert.False(ok);
            Assert.Equal(IntPtr.Zero, basePtr);
        }
        finally
        {
            PageManager.MemoryQuotaBytes = oldQuota;
        }
    }

    [Fact]
    public void ContiguousStrictAllocation_TracksPerPageRefCount_AndFreesOnLastRelease()
    {
        var oldQuota = PageManager.MemoryQuotaBytes;
        PageManager.MemoryQuotaBytes = long.MaxValue;
        var beforePages = PageManager.GetAllocatedPageCount();

        var basePtr = IntPtr.Zero;
        try
        {
            var ok = PageManager.TryAllocateExternalContiguousStrict(
                out basePtr,
                4,
                AllocationClass.Cow);
            Assert.True(ok);
            Assert.NotEqual(IntPtr.Zero, basePtr);
            Assert.Equal(beforePages + 4, PageManager.GetAllocatedPageCount());

            var p0 = basePtr;
            var p1 = basePtr + LinuxConstants.PageSize;

            Assert.Equal(1, PageManager.GetRefCount(p0));
            PageManager.AddRef(p0);
            Assert.Equal(2, PageManager.GetRefCount(p0));

            PageManager.ReleasePtr(p0);
            Assert.Equal(1, PageManager.GetRefCount(p0));

            // Release all pages from the segment.
            PageManager.ReleasePtr(p0);
            PageManager.ReleasePtr(p1);
            PageManager.ReleasePtr(basePtr + 2 * LinuxConstants.PageSize);
            PageManager.ReleasePtr(basePtr + 3 * LinuxConstants.PageSize);

            Assert.Equal(beforePages, PageManager.GetAllocatedPageCount());
        }
        finally
        {
            PageManager.MemoryQuotaBytes = oldQuota;
        }
    }

    [Fact]
    public void ContiguousStrictAllocation_MmapBackend_WorksAndReleases()
    {
        var oldQuota = PageManager.MemoryQuotaBytes;
        var oldBackend = PageManager.PreferredBackend;
        PageManager.MemoryQuotaBytes = long.MaxValue;
        PageManager.PreferredBackend = PageBackend.MmapAnonymous;
        var beforePages = PageManager.GetAllocatedPageCount();

        var basePtr = IntPtr.Zero;
        try
        {
            var ok = PageManager.TryAllocateExternalContiguousStrict(
                out basePtr,
                2,
                AllocationClass.Anonymous);
            Assert.True(ok);
            Assert.NotEqual(IntPtr.Zero, basePtr);
            Assert.Equal(beforePages + 2, PageManager.GetAllocatedPageCount());

            PageManager.ReleasePtr(basePtr);
            PageManager.ReleasePtr(basePtr + LinuxConstants.PageSize);
            Assert.Equal(beforePages, PageManager.GetAllocatedPageCount());
        }
        finally
        {
            PageManager.PreferredBackend = oldBackend;
            PageManager.MemoryQuotaBytes = oldQuota;
        }
    }
}