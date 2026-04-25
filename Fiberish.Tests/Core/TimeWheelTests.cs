using Fiberish.Core;
using Xunit;

namespace Fiberish.Tests.Core;

public class TimeWheelTests
{
    [Fact]
    public void Schedule_FiresAtCorrectTick()
    {
        var wheel = new TimeWheel();
        var fired1 = false;
        var fired2 = false;

        wheel.Schedule(50, () => fired1 = true);
        wheel.Schedule(100, () => fired2 = true);

        Assert.True(wheel.HasTimers);

        wheel.Advance(50);
        Assert.False(fired1);
        Assert.False(fired2);

        wheel.Advance(51);
        Assert.True(fired1);
        Assert.False(fired2);

        wheel.Advance(101);
        Assert.True(fired2);
    }

    [Fact]
    public void Schedule_Rollover_CascadesProperly()
    {
        var wheel = new TimeWheel();
        var firedTv2 = false;
        var firedTv3 = false;
        var firedTv4 = false;

        long delayTv2 = 300; // Requires _tv2 (1 << 8 = 256)
        long delayTv3 = 20000; // Requires _tv3 (1 << 14 = 16384)
        long delayTv4 = 2000000; // Requires _tv4 (1 << 20 = 1048576)

        wheel.Schedule(delayTv2, () => firedTv2 = true);
        wheel.Schedule(delayTv3, () => firedTv3 = true);
        wheel.Schedule(delayTv4, () => firedTv4 = true);

        wheel.Advance(delayTv2);
        Assert.False(firedTv2);
        wheel.Advance(delayTv2 + 1);
        Assert.True(firedTv2);

        wheel.Advance(delayTv3);
        Assert.False(firedTv3);
        wheel.Advance(delayTv3 + 1);
        Assert.True(firedTv3);

        wheel.Advance(delayTv4);
        Assert.False(firedTv4);
        wheel.Advance(delayTv4 + 1);
        Assert.True(firedTv4);
    }

    [Fact]
    public void Cancel_Timer_DoesNotFire()
    {
        var wheel = new TimeWheel();
        var fired = false;

        var timer = wheel.Schedule(100, () => fired = true);
        timer.Cancel();

        wheel.Advance(101);
        Assert.False(fired); // Should not have fired
        Assert.False(wheel.HasTimers); // Swept
    }

    [Fact]
    public void HasTimers_Accurate()
    {
        var wheel = new TimeWheel();
        Assert.False(wheel.HasTimers);

        var timer = wheel.Schedule(10, () => { });
        Assert.True(wheel.HasTimers);

        wheel.Advance(11);
        Assert.False(wheel.HasTimers);
    }
}