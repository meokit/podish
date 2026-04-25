using System.Runtime.InteropServices;
using Fiberish.Memory;
using Fiberish.Native;
using Xunit;

namespace Fiberish.Tests.Memory;

public class ZeroPageProtectionTests
{
    private static readonly HostMemoryMapGeometry Geometry16K =
        new(LinuxConstants.PageSize, 16384, 16384, true, true);

    [Fact]
    public void AcquireZeroMappingPage_OnNativeHost_UsesReadOnlyProtectedVirtualStorage()
    {
        if (!PooledSegmentMemory.SupportsReadOnlyProtection)
            return;

        using var memoryContext = new MemoryRuntimeContext();

        var page = memoryContext.AcquireZeroMappingPage(0);

        Assert.NotEqual(IntPtr.Zero, page);
        Assert.True(memoryContext.IsZeroPageHostReadOnlyProtected);
        Assert.Equal(PooledSegmentAllocationKind.VirtualMemory, memoryContext.ZeroPageAllocationKind);

        var data = new byte[64];
        Marshal.Copy(page, data, 0, data.Length);
        Assert.All(data, value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public void AcquireZeroMappingPage_ProtectsEntireConfiguredHostPage()
    {
        if (!PooledSegmentMemory.SupportsReadOnlyProtection)
            return;

        using var memoryContext = new MemoryRuntimeContext(Geometry16K);

        _ = memoryContext.AcquireZeroMappingPage(0);

        Assert.True(memoryContext.IsZeroPageHostReadOnlyProtected);
        Assert.Equal((nuint)Geometry16K.HostPageSize, memoryContext.ZeroPageReservationSizeBytes);
    }
}
