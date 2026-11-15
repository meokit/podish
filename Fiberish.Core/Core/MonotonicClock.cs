using System.Diagnostics;

namespace Fiberish.Core;

/// <summary>
///     Monotonic wall-clock readings used by CLOCK_MONOTONIC and timerfd/POSIX
///     absolute deadlines. It intentionally shares Stopwatch's origin with the
///     guest clock implementation instead of using a scheduler-start-relative tick.
/// </summary>
public static class MonotonicClock
{
    public static long Milliseconds
    {
        get
        {
            var frequency = Stopwatch.Frequency;
            var timestamp = Stopwatch.GetTimestamp();
            return timestamp / frequency * 1000 + timestamp % frequency * 1000 / frequency;
        }
    }
}
