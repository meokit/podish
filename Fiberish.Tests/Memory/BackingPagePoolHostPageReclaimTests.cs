using Fiberish.Memory;
using Fiberish.Native;
using Xunit;

namespace Fiberish.Tests.Memory;

[Collection("ExternalPageManagerSerial")]
public class BackingPagePoolHostPageReclaimTests
{
    private static readonly HostMemoryMapGeometry Geometry16K =
        new(LinuxConstants.PageSize, 16384, 16384, true, true);

    [Fact]
    public void ReleasePooledPage_AdvisesOnlyAfterLastGuestPageLeavesHostPage()
    {
        var adviseCalls = new List<(nint Address, nuint Length)>();
        var handles = new BackingPageHandle[5];
        using var context = new MemoryRuntimeContext(Geometry16K);

        try
        {
            PooledSegmentMemory.TestAdviseUnusedObserver = (address, length) => adviseCalls.Add((address, length));

            for (var i = 0; i < handles.Length; i++)
            {
                handles[i] = context.BackingPagePool.AllocAnonPage(AllocationClass.Anonymous);
                Assert.True(handles[i].IsValid);
            }

            var firstPointer = handles[0].Pointer.ToInt64();
            Assert.Equal(firstPointer + LinuxConstants.PageSize, handles[1].Pointer.ToInt64());
            Assert.Equal(firstPointer + 2 * LinuxConstants.PageSize, handles[2].Pointer.ToInt64());
            Assert.Equal(firstPointer + 3 * LinuxConstants.PageSize, handles[3].Pointer.ToInt64());
            Assert.Equal(firstPointer + 4 * LinuxConstants.PageSize, handles[4].Pointer.ToInt64());

            for (var i = 0; i < 4 - 1; i++)
            {
                BackingPageHandle.Release(ref handles[i]);
                Assert.Empty(adviseCalls);
            }

            BackingPageHandle.Release(ref handles[3]);

            var adviseCall = Assert.Single(adviseCalls);
            Assert.Equal((nuint)Geometry16K.HostPageSize, adviseCall.Length);
            Assert.Equal((nint)AlignDown(firstPointer, Geometry16K.HostPageSize), adviseCall.Address);
        }
        finally
        {
            for (var i = 0; i < handles.Length; i++)
                BackingPageHandle.Release(ref handles[i]);

            PooledSegmentMemory.TestAdviseUnusedObserver = null;
        }
    }

    private static long AlignDown(long value, int alignment)
    {
        return value / alignment * alignment;
    }
}
