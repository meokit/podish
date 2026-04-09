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
    public void ExternalPagePool_ReusesReleasedPage_AndReturnsZeroedMemory()
    {
        using var scope = PageManager.BeginIsolatedScope();
        var ptr = PageManager.AllocateExternalPage(AllocationClass.Anonymous);
        Assert.NotEqual(IntPtr.Zero, ptr);

        var dirty = Enumerable.Repeat((byte)0xA5, LinuxConstants.PageSize).ToArray();
        System.Runtime.InteropServices.Marshal.Copy(dirty, 0, ptr, dirty.Length);
        PageManager.ReleasePtr(ptr);

        var reused = PageManager.AllocateExternalPage(AllocationClass.Anonymous);
        try
        {
            Assert.Equal(ptr, reused);
            var data = new byte[LinuxConstants.PageSize];
            System.Runtime.InteropServices.Marshal.Copy(reused, data, 0, data.Length);
            foreach (var b in data)
                Assert.Equal((byte)0, b);
        }
        finally
        {
            if (reused != IntPtr.Zero)
                PageManager.ReleasePtr(reused);
        }
    }

    [Fact]
    public void ExternalPagePool_ReaddsFullPoolAfterRelease()
    {
        using var scope = PageManager.BeginIsolatedScope();
        const int pooledSegmentBytes = 4 * 1024 * 1024;
        var pagesPerPool = pooledSegmentBytes / LinuxConstants.PageSize;
        var ptrs = new IntPtr[pagesPerPool + 1];
        var reused = IntPtr.Zero;

        try
        {
            for (var i = 0; i < ptrs.Length; i++)
            {
                ptrs[i] = PageManager.AllocateExternalPage(AllocationClass.Anonymous);
                Assert.NotEqual(IntPtr.Zero, ptrs[i]);
            }

            var firstPoolPage = ptrs[0];
            PageManager.ReleasePtr(firstPoolPage);
            ptrs[0] = IntPtr.Zero;

            reused = PageManager.AllocateExternalPage(AllocationClass.Anonymous);
            Assert.Equal(firstPoolPage, reused);
        }
        finally
        {
            if (reused != IntPtr.Zero)
                PageManager.ReleasePtr(reused);

            foreach (var ptr in ptrs)
                if (ptr != IntPtr.Zero)
                    PageManager.ReleasePtr(ptr);
        }
    }
}
