using Fiberish.Core;
using Xunit;

namespace Fiberish.Tests.Core;

public class PidAllocationTests
{
    [Fact]
    public void AllocateTaskId_StartsFromOnePerScheduler()
    {
        var scheduler1 = new KernelScheduler();
        var scheduler2 = new KernelScheduler();

        Assert.Equal(1, scheduler1.AllocateTaskId());
        Assert.Equal(2, scheduler1.AllocateTaskId());
        Assert.Equal(1, scheduler2.AllocateTaskId());
    }
}
