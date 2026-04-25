using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Fiberish.Memory;

internal static class MonotonicTime
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetTimestamp()
    {
        return Stopwatch.GetTimestamp();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ToTimestampDelta(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            return 0;

        return (long)Math.Ceiling(duration.TotalSeconds * Stopwatch.Frequency);
    }
}
