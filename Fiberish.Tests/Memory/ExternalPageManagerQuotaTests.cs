using Fiberish.Memory;
using Fiberish.Native;
using Xunit;

namespace Fiberish.Tests.Memory;

[Collection("ExternalPageManagerSerial")]
public class ExternalPageManagerQuotaTests
{
    private static MemoryRuntimeContext CreateContext(long quotaBytes)
    {
        var context = new MemoryRuntimeContext();
        context.MemoryQuotaBytes = quotaBytes;
        return context;
    }

    [Fact]
    public void StrictAllocation_Fails_WhenQuotaExceeded()
    {
        using var context = CreateContext(quotaBytes: LinuxConstants.PageSize - 1);
        var ok = context.BackingPagePool.TryAllocAnonPageMayFail(out var handle, AllocationClass.Anonymous);
        Assert.False(ok);
        Assert.False(handle.IsValid);
    }

    [Fact]
    public void IsolatedScope_UsesTwoGiBDefaultQuota()
    {
        using var context = new MemoryRuntimeContext();
        Assert.Equal(2L * 1024 * 1024 * 1024, context.MemoryQuotaBytes);
    }

    [Fact]
    public void StrictAllocation_Succeeds_WhenQuotaAllows()
    {
        using var context = CreateContext(quotaBytes: long.MaxValue);
        var handle = default(BackingPageHandle);
        try
        {
            var ok = context.BackingPagePool.TryAllocAnonPageMayFail(out handle, AllocationClass.Anonymous);
            Assert.True(ok);
            Assert.True(handle.IsValid);
        }
        finally
        {
            BackingPageHandle.Release(ref handle);
        }
    }

    [Fact]
    public void LegacyAllocation_CanExceedQuota()
    {
        using var context = CreateContext(quotaBytes: LinuxConstants.PageSize - 1);
        var handle = default(BackingPageHandle);
        try
        {
            handle = context.BackingPagePool.AllocAnonPage();
            Assert.True(handle.IsValid);
        }
        finally
        {
            BackingPageHandle.Release(ref handle);
        }
    }

    [Fact]
    public void ExternalPagePool_ReusesReleasedPage_AndReturnsZeroedMemory()
    {
        using var context = CreateContext(long.MaxValue);
        var handle = context.BackingPagePool.AllocAnonPage(AllocationClass.Anonymous);
        var ptr = handle.Pointer;
        Assert.NotEqual(IntPtr.Zero, ptr);

        var dirty = Enumerable.Repeat((byte)0xA5, LinuxConstants.PageSize).ToArray();
        System.Runtime.InteropServices.Marshal.Copy(dirty, 0, ptr, dirty.Length);
        BackingPageHandle.Release(ref handle);

        var reused = context.BackingPagePool.AllocAnonPage(AllocationClass.Anonymous);
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
        using var context = CreateContext(long.MaxValue);
        const int pooledSegmentBytes = 4 * 1024 * 1024;
        var pagesPerPool = pooledSegmentBytes / LinuxConstants.PageSize;
        var handles = new BackingPageHandle[pagesPerPool + 1];
        var reused = default(BackingPageHandle);

        try
        {
            for (var i = 0; i < handles.Length; i++)
            {
                handles[i] = context.BackingPagePool.AllocAnonPage(AllocationClass.Anonymous);
                Assert.True(handles[i].IsValid);
            }

            var firstPoolPage = handles[0].Pointer;
            BackingPageHandle.Release(ref handles[0]);

            reused = context.BackingPagePool.AllocAnonPage(AllocationClass.Anonymous);
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
