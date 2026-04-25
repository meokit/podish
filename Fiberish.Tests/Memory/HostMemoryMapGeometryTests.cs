using Fiberish.Memory;
using Xunit;

namespace Fiberish.Tests.Memory;

public class HostMemoryMapGeometryTests
{
    [Fact]
    public void CreateCurrent_UsesAtLeastTwoMiBWindowGranularity()
    {
        var geometry = HostMemoryMapGeometry.CreateCurrent();

        if (!geometry.SupportsMappedFileBackend)
            return;

        Assert.True(geometry.AllocationGranularity >= 2 * 1024 * 1024);
        Assert.True(geometry.AllocationGranularity >= geometry.HostPageSize);
        Assert.Equal(0, geometry.AllocationGranularity % geometry.HostPageSize);
        Assert.True(geometry.SupportsDirectMappedTailPage);
    }
}
