using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Fiberish.Core;

/// <summary>
///     Interruptible wait with sub-millisecond accuracy for the scheduler idle path.
///     <para>
///         Windows rounds <see cref="System.Threading.WaitHandle.WaitOne(int)" /> and
///         <c>Thread.Sleep</c> up to the system timer tick, which is ~15.6&#160;ms unless a
///         process-wide resolution change is in effect. A high-resolution waitable timer
///         (Windows 10 1803+) expires within a few hundred microseconds, so guest sleeps of a
///         few milliseconds no longer overshoot by a whole tick. Waiting on both the timer and
///         the scheduler wake event keeps external wakeups (ingress work, host I/O) prompt.
///     </para>
/// </summary>
internal sealed class HighResolutionWait : IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x00000002;

    // TIMER_MODIFY_STATE | SYNCHRONIZE: enough to set the timer and wait on it.
    private const uint TimerModifyState = 0x0002;
    private const uint Synchronize = 0x00100000;
    private const uint Infinite = 0xFFFFFFFF;
    private const uint WaitObject0 = 0;

    private readonly IntPtr[] _handles = new IntPtr[2];
    private SafeWaitHandle? _timer;

    public HighResolutionWait()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            _timer = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution,
                TimerModifyState | Synchronize);
            if (_timer.IsInvalid)
            {
                _timer.Dispose();
                _timer = null;
            }
        }
        catch
        {
            _timer = null;
        }
    }

    private bool HasHighResolutionTimer => _timer != null;

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>
    ///     Waits until <paramref name="wake" /> is signaled or the timeout elapses.
    /// </summary>
    /// <param name="wake">Auto-reset event the scheduler raises when new work arrives.</param>
    /// <param name="timeoutMs">Relative timeout in milliseconds; ignored when <paramref name="infinite" />.</param>
    /// <param name="infinite">Wait without a timeout.</param>
    /// <returns><c>true</c> when the wake event was signaled, <c>false</c> on timeout.</returns>
    public bool Wait(WaitHandle wake, double timeoutMs, bool infinite)
    {
        if (!infinite && HasHighResolutionTimer && timeoutMs >= 0)
        {
            // SetWaitableTimer takes a relative interval in 100ns units (negative = relative).
            var units = (long)Math.Round(Math.Min(timeoutMs, 4_000_000_000d) * 10_000d);
            if (units < 1) units = 1;
            var due = -units;
            if (SetWaitableTimer(_timer!, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            {
                _handles[0] = wake.SafeWaitHandle.DangerousGetHandle();
                _handles[1] = _timer!.DangerousGetHandle();
                var result = WaitForMultipleObjects(2, _handles, false, Infinite);
                if (result == WaitObject0) return true;
                if (result == WaitObject0 + 1) return false;
                // WAIT_FAILED is not a timer expiration. Fall back to the managed wait,
                // which also reports an invalid/disposed wake handle instead of busy-spinning.
            }
        }

        return wake.WaitOne(infinite ? Timeout.Infinite : ToWaitMilliseconds(timeoutMs));
    }

    private static int ToWaitMilliseconds(double timeoutMs)
    {
        if (double.IsNaN(timeoutMs) || timeoutMs <= 0) return 0;
        if (timeoutMs >= int.MaxValue) return int.MaxValue;
        return (int)Math.Ceiling(timeoutMs);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags,
        uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(SafeWaitHandle handle, ref long dueTime, int period,
        IntPtr completionRoutine, IntPtr completionArgument, bool resume);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForMultipleObjects(uint count, IntPtr[] handles, bool waitAll, uint milliseconds);
}
