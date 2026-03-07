using Fiberish.Memory;
using Fiberish.Native;
using Xunit;

namespace Fiberish.Tests.Memory;

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
}
