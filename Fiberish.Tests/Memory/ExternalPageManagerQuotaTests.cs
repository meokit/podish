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
            var ok = PageManager.TryAllocAnonPageMayFail(out var handle, AllocationClass.Anonymous);
            Assert.False(ok);
            Assert.False(handle.IsValid);
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
        var handle = default(BackingPageHandle);
        try
        {
            var ok = PageManager.TryAllocAnonPageMayFail(out handle, AllocationClass.Anonymous);
            Assert.True(ok);
            Assert.True(handle.IsValid);
        }
        finally
        {
            BackingPageHandle.Release(ref handle);
            PageManager.MemoryQuotaBytes = oldQuota;
        }
    }

    [Fact]
    public void LegacyAllocation_CanExceedQuota()
    {
        var oldQuota = PageManager.MemoryQuotaBytes;
        PageManager.MemoryQuotaBytes = LinuxConstants.PageSize - 1;
        var handle = default(BackingPageHandle);
        try
        {
            handle = PageManager.AllocAnonPage();
            Assert.True(handle.IsValid);
        }
        finally
        {
            BackingPageHandle.Release(ref handle);
            PageManager.MemoryQuotaBytes = oldQuota;
        }
    }

    [Fact]
    public void ExternalPagePool_ReusesReleasedPage_AndReturnsZeroedMemory()
    {
        using var scope = PageManager.BeginIsolatedScope();
        var handle = PageManager.AllocAnonPage(AllocationClass.Anonymous);
        var ptr = handle.Pointer;
        Assert.NotEqual(IntPtr.Zero, ptr);

        var dirty = Enumerable.Repeat((byte)0xA5, LinuxConstants.PageSize).ToArray();
        System.Runtime.InteropServices.Marshal.Copy(dirty, 0, ptr, dirty.Length);
        BackingPageHandle.Release(ref handle);

        var reused = PageManager.AllocAnonPage(AllocationClass.Anonymous);
        try
        {
            Assert.Equal(ptr, reused.Pointer);
            var data = new byte[LinuxConstants.PageSize];
            System.Runtime.InteropServices.Marshal.Copy(reused.Pointer, data, 0, data.Length);
            foreach (var b in data)
                Assert.Equal((byte)0, b);
        }
        finally
        {
            BackingPageHandle.Release(ref reused);
        }
    }

    [Fact]
    public void ExternalPagePool_ReaddsFullPoolAfterRelease()
    {
        using var scope = PageManager.BeginIsolatedScope();
        const int pooledSegmentBytes = 4 * 1024 * 1024;
        var pagesPerPool = pooledSegmentBytes / LinuxConstants.PageSize;
        var handles = new BackingPageHandle[pagesPerPool + 1];
        var reused = default(BackingPageHandle);

        try
        {
            for (var i = 0; i < handles.Length; i++)
            {
                handles[i] = PageManager.AllocAnonPage(AllocationClass.Anonymous);
                Assert.True(handles[i].IsValid);
            }

            var firstPoolPage = handles[0].Pointer;
            BackingPageHandle.Release(ref handles[0]);

            reused = PageManager.AllocAnonPage(AllocationClass.Anonymous);
            Assert.Equal(firstPoolPage, reused.Pointer);
        }
        finally
        {
            BackingPageHandle.Release(ref reused);

            for (var i = 0; i < handles.Length; i++)
                BackingPageHandle.Release(ref handles[i]);
        }
    }
}
