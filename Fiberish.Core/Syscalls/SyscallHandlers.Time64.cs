using System.Buffers.Binary;
using Fiberish.Core;
using Fiberish.Native;
using Microsoft.Extensions.Logging;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
#pragma warning disable CS1998 // Async method lacks await operators
    private static async ValueTask<int> SysAlarm(IntPtr state, uint seconds, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        if (sm.Engine.Owner is not FiberTask task) return -(int)Errno.EPERM;

        var proc = task.Process;
        var scheduler = KernelScheduler.Current;
        if (scheduler == null) return -(int)Errno.ENOSYS;

        var remainingSeconds = 0;
        
        // Cancel existing alarm
        if (proc.AlarmTimer != null)
        {
            if (!proc.AlarmTimer.Canceled)
            {
                var remainingMs = proc.AlarmTimer.ExpirationTick - scheduler.CurrentTick;
                remainingSeconds = (int)(remainingMs / 1000);
                if (remainingSeconds == 0 && remainingMs > 0) remainingSeconds = 1; // Round up
            }
            proc.AlarmTimer.Cancel();
            proc.AlarmTimer = null;
        }

        // Set new alarm
        if (seconds > 0)
        {
            var ms = seconds * 1000L;
            proc.AlarmTimer = scheduler.ScheduleTimer(ms, () =>
            {
                // Send SIGALRM to process
                scheduler.SignalProcess(proc.TGID, LinuxConstants.SIGALRM);
            });
        }

        return remainingSeconds;
    }

    private static async ValueTask<int> SysClockSetTime64(IntPtr state, uint clockId, uint tsPtr, uint a3, uint a4, uint a5, uint a6)
    {
        // Not permitted
        return -(int)Errno.EPERM;
    }

    private static async ValueTask<int> SysClockAdjTime64(IntPtr state, uint clockId, uint txPtr, uint a3, uint a4, uint a5, uint a6)
    {
        // Not permitted / Stubbed
        return -(int)Errno.EPERM;
    }

    private static async ValueTask<int> SysClockGetResTime64(IntPtr state, uint clockId, uint resPtr, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        if (resPtr != 0)
        {
            var buf = new byte[16];
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(0, 8), 0); // 0 sec
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(8, 8), 1000000); // 1,000,000 ns (1 ms resolution)
            if (!sm.Engine.CopyToUser(resPtr, buf)) return -(int)Errno.EFAULT;
        }

        return 0;
    }

    private static async ValueTask<int> SysTimerGetTime32(IntPtr state, uint timerId, uint currPtr, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        if (sm.Engine.Owner is not FiberTask task) return -(int)Errno.EPERM;

        var proc = task.Process;
        if (!proc.PosixTimers.TryGetValue((int)timerId, out var timer)) return -(int)Errno.EINVAL;

        if (currPtr == 0) return -(int)Errno.EFAULT;

        long valueSec = 0, valueNsec = 0;
        long intervalSec = 0, intervalNsec = 0;

        if (timer.ActiveTimer != null && !timer.ActiveTimer.Canceled)
        {
            var remainingMs = timer.ActiveTimer.ExpirationTick - KernelScheduler.Current!.CurrentTick;
            if (remainingMs < 0) remainingMs = 0;
            valueSec = remainingMs / 1000;
            valueNsec = (remainingMs % 1000) * 1000000;
            
            intervalSec = (long)(timer.IntervalMs / 1000);
            intervalNsec = (long)((timer.IntervalMs % 1000) * 1000000);
        }

        var buf = new byte[16]; // old_itimerspec is 16 bytes (4 sec, 4 nsec, 4 sec, 4 nsec)
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), (int)intervalSec);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), (int)intervalNsec);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8, 4), (int)valueSec);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(12, 4), (int)valueNsec);

        if (!sm.Engine.CopyToUser(currPtr, buf)) return -(int)Errno.EFAULT;
        return 0;
    }

    private static async ValueTask<int> SysTimerGetTime64(IntPtr state, uint timerId, uint currPtr, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        if (sm.Engine.Owner is not FiberTask task) return -(int)Errno.EPERM;

        var proc = task.Process;
        if (!proc.PosixTimers.TryGetValue((int)timerId, out var timer)) return -(int)Errno.EINVAL;

        if (currPtr == 0) return -(int)Errno.EFAULT;

        long valueSec = 0, valueNsec = 0;
        long intervalSec = 0, intervalNsec = 0;

        if (timer.ActiveTimer != null && !timer.ActiveTimer.Canceled)
        {
            var remainingMs = timer.ActiveTimer.ExpirationTick - KernelScheduler.Current!.CurrentTick;
            if (remainingMs < 0) remainingMs = 0;
            valueSec = remainingMs / 1000;
            valueNsec = (remainingMs % 1000) * 1000000;
            
            intervalSec = (long)(timer.IntervalMs / 1000);
            intervalNsec = (long)((timer.IntervalMs % 1000) * 1000000);
        }

        var buf = new byte[32]; // itimerspec64 is 32 bytes (8 sec, 8 nsec, 8 sec, 8 nsec) on 32-bit x86
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(0, 8), intervalSec);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(8, 8), intervalNsec);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(16, 8), valueSec);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(24, 8), valueNsec);

        if (!sm.Engine.CopyToUser(currPtr, buf)) return -(int)Errno.EFAULT;
        return 0;
    }

    private static async ValueTask<int> SysTimerSetTime32(IntPtr state, uint timerId, uint flags, uint newPtr, uint oldPtr, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        if (sm.Engine.Owner is not FiberTask task) return -(int)Errno.EPERM;
        var scheduler = KernelScheduler.Current;
        if (scheduler == null) return -(int)Errno.ENOSYS;

        var proc = task.Process;
        if (!proc.PosixTimers.TryGetValue((int)timerId, out var timer)) return -(int)Errno.EINVAL;

        if (newPtr == 0) return -(int)Errno.EFAULT;

        var buf = new byte[16];
        if (!sm.Engine.CopyFromUser(newPtr, buf)) return -(int)Errno.EFAULT;

        var intervalSec = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(0, 4));
        var intervalNsec = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(4, 4));
        var valueSec = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(8, 4));
        var valueNsec = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(12, 4));

        ulong intervalMs = (ulong)(intervalSec * 1000L + intervalNsec / 1000000L);
        ulong valueMs = (ulong)(valueSec * 1000L + valueNsec / 1000000L);

        // Fetch old if needed
        if (oldPtr != 0)
        {
            await SysTimerGetTime32(state, timerId, oldPtr, 0, 0, 0, 0);
        }

        // Apply new
        timer.ActiveTimer?.Cancel();
        timer.ActiveTimer = null;
        timer.OverrunCount = 0;
        timer.IntervalMs = intervalMs;
        timer.ValueMs = valueMs;

        if (valueMs > 0)
        {
            void OnTimerTick()
            {
                if (timer.SigEvent.Notify == LinuxConstants.SIGEV_SIGNAL)
                {
                    // Send signal
                    var info = new SigInfo
                    {
                        Signo = timer.SigEvent.Signo,
                        Errno = 0,
                        Code = 2, // SI_TIMER
                        Pid = 0,
                        Uid = 0,
                        Value = timer.SigEvent.Value,
                        TimerId = (int)timerId,
                        Overrun = timer.OverrunCount
                    };
                    scheduler.SignalProcessInfo(proc.TGID, timer.SigEvent.Signo, info);
                }

                if (timer.IntervalMs > 0)
                {
                    timer.OverrunCount++;
                    timer.ActiveTimer = scheduler.ScheduleTimer((long)timer.IntervalMs, OnTimerTick);
                }
            }

            long targetDelayMs = (long)valueMs;
            if ((flags & 1) != 0) // TIMER_ABSTIME
            {
                if (timer.ClockId == LinuxConstants.CLOCK_REALTIME)
                {
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); 
                    targetDelayMs = (long)valueMs - now;
                    if (targetDelayMs < 0) targetDelayMs = 0; 
                }
                else 
                {
                    targetDelayMs = (long)valueMs - scheduler.CurrentTick;
                    if (targetDelayMs < 0) targetDelayMs = 0;
                }
            }

            if (targetDelayMs == 0 && valueMs > 0) targetDelayMs = 1; // Fire ASAP
            
            timer.ActiveTimer = scheduler.ScheduleTimer(targetDelayMs, OnTimerTick);
        }

        return 0;
    }

    private static async ValueTask<int> SysTimerSetTime64(IntPtr state, uint timerId, uint flags, uint newPtr, uint oldPtr, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        if (sm.Engine.Owner is not FiberTask task) return -(int)Errno.EPERM;
        var scheduler = KernelScheduler.Current;
        if (scheduler == null) return -(int)Errno.ENOSYS;

        var proc = task.Process;
        if (!proc.PosixTimers.TryGetValue((int)timerId, out var timer)) return -(int)Errno.EINVAL;

        if (newPtr == 0) return -(int)Errno.EFAULT;

        var buf = new byte[32];
        if (!sm.Engine.CopyFromUser(newPtr, buf)) return -(int)Errno.EFAULT;

        var intervalSec = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(0, 8));
        var intervalNsec = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(8, 8));
        var valueSec = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(16, 8));
        var valueNsec = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(24, 8));

        ulong intervalMs = (ulong)(intervalSec * 1000 + intervalNsec / 1000000);
        ulong valueMs = (ulong)(valueSec * 1000 + valueNsec / 1000000);

        // Fetch old if needed
        if (oldPtr != 0)
        {
            await SysTimerGetTime64(state, timerId, oldPtr, 0, 0, 0, 0);
        }

        // Apply new
        timer.ActiveTimer?.Cancel();
        timer.ActiveTimer = null;
        timer.OverrunCount = 0;
        timer.IntervalMs = intervalMs;
        timer.ValueMs = valueMs;

        if (valueMs > 0)
        {
            void OnTimerTick()
            {
                if (timer.SigEvent.Notify == LinuxConstants.SIGEV_SIGNAL)
                {
                    // Send signal
                    var info = new SigInfo
                    {
                        Signo = timer.SigEvent.Signo,
                        Errno = 0,
                        Code = 2, // SI_TIMER
                        Pid = 0,
                        Uid = 0,
                        Value = timer.SigEvent.Value,
                        TimerId = (int)timerId,
                        Overrun = timer.OverrunCount
                    };
                    scheduler.SignalProcessInfo(proc.TGID, timer.SigEvent.Signo, info);
                }

                if (timer.IntervalMs > 0)
                {
                    timer.OverrunCount++;
                    timer.ActiveTimer = scheduler.ScheduleTimer((long)timer.IntervalMs, OnTimerTick);
                }
            }

            // Note: TIMER_ABSTIME (1) means valueMs is absolute time, otherwise relative
            // For now, we assume relative or simulate relative delta if absolute is requested.
            // A perfect implementation would parse `flags & TIMER_ABSTIME`.
            long targetDelayMs = (long)valueMs;
            if ((flags & 1) != 0) // TIMER_ABSTIME
            {
                // Simple approx: valueMs - current physical time (Assuming CLOCK_REALTIME/MONOTONIC matching)
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); // Hack fallback if clocks misaligned
                if (timer.ClockId == LinuxConstants.CLOCK_REALTIME)
                {
                    targetDelayMs = (long)valueMs - now;
                    if (targetDelayMs < 0) targetDelayMs = 0; 
                }
                else // CLOCK_MONOTONIC uses KernelScheduler.CurrentTick
                {
                    targetDelayMs = (long)valueMs - scheduler.CurrentTick;
                    if (targetDelayMs < 0) targetDelayMs = 0;
                }
            }

            if (targetDelayMs == 0 && valueMs > 0) targetDelayMs = 1; // Fire ASAP
            
            timer.ActiveTimer = scheduler.ScheduleTimer(targetDelayMs, OnTimerTick);
        }

        return 0;
    }

    private static async ValueTask<int> SysTimerCreate(IntPtr state, uint clockId, uint sevpPtr, uint timerIdPtr, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        if (sm.Engine.Owner is not FiberTask task) return -(int)Errno.EPERM;

        var proc = task.Process;
        var timerId = proc.NextPosixTimerId++;

        SigEvent sigEvent = default;
        if (sevpPtr != 0)
        {
            var buf = new byte[64]; // sigevent is generally padded to 64 bytes
            if (!sm.Engine.CopyFromUser(sevpPtr, buf)) return -(int)Errno.EFAULT;
            
            sigEvent.Value = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(0, 8)); // Note: Depending on padding, might be (0,4) for 32 bit sys
            // i386 sigevent: union sigval (4 bytes), int sigev_signo (4 bytes), int sigev_notify (4 bytes), union (padding + thread spec)
            
            // Re-read carefully for 32-bit x86 sigevent ABI
            sigEvent.Value = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(0, 4));
            sigEvent.Signo = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(4, 4));
            sigEvent.Notify = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(8, 4));
            sigEvent.Tid = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(12, 4));
        }
        else
        {
            sigEvent.Notify = LinuxConstants.SIGEV_SIGNAL;
            sigEvent.Signo = LinuxConstants.SIGALRM;
            sigEvent.Value = (ulong)timerId;
        }

        var timer = new PosixTimer((int)timerId, (int)clockId, sigEvent, proc);
        proc.PosixTimers[(int)timerId] = timer;

        if (timerIdPtr != 0)
        {
            var idBuf = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(idBuf, (int)timerId);
            if (!sm.Engine.CopyToUser(timerIdPtr, idBuf)) return -(int)Errno.EFAULT;
        }

        return 0;
    }

    private static async ValueTask<int> SysTimerDelete(IntPtr state, uint timerId, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        if (sm.Engine.Owner is not FiberTask task) return -(int)Errno.EPERM;

        var proc = task.Process;
        if (!proc.PosixTimers.TryGetValue((int)timerId, out var timer)) return -(int)Errno.EINVAL;

        timer.ActiveTimer?.Cancel();
        proc.PosixTimers.Remove((int)timerId);

        return 0;
    }

    private static async ValueTask<int> SysTimerGetOverrun(IntPtr state, uint timerId, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        if (sm.Engine.Owner is not FiberTask task) return -(int)Errno.EPERM;

        var proc = task.Process;
        if (!proc.PosixTimers.TryGetValue((int)timerId, out var timer)) return -(int)Errno.EINVAL;

        return timer.OverrunCount;
    }

    private static async ValueTask<int> SysTimerFdGetTime64(IntPtr state, uint fd, uint curValuePtr, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        if (!sm.FDs.TryGetValue((int)fd, out var file) || file.Dentry.Inode is not TimerFdInode timerFd)
            return -(int)Errno.EBADF;

        if (curValuePtr == 0) return -(int)Errno.EFAULT;

        timerFd.GetTime(out long intervalMs, out long valueMs);

        var buf = new byte[32];
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(0, 8), intervalMs / 1000);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(8, 8), (intervalMs % 1000) * 1000000);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(16, 8), valueMs / 1000);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(24, 8), (valueMs % 1000) * 1000000);

        if (!sm.Engine.CopyToUser(curValuePtr, buf)) return -(int)Errno.EFAULT;

        return 0; 
    }

    private static async ValueTask<int> SysTimerFdSetTime64(IntPtr state, uint fd, uint flags, uint newValuePtr, uint oldValuePtr, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        if (!sm.FDs.TryGetValue((int)fd, out var file) || file.Dentry.Inode is not TimerFdInode timerFd)
            return -(int)Errno.EBADF;

        if (newValuePtr == 0) return -(int)Errno.EFAULT;

        var buf = new byte[32];
        if (!sm.Engine.CopyFromUser(newValuePtr, buf)) return -(int)Errno.EFAULT;

        var intervalSec = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(0, 8));
        var intervalNsec = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(8, 8));
        var valueSec = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(16, 8));
        var valueNsec = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(24, 8));

        if (oldValuePtr != 0)
        {
            await SysTimerFdGetTime64(state, fd, oldValuePtr, 0, 0, 0, 0);
        }

        var isAbsolute = (flags & 1) != 0; // TFD_TIMER_ABSTIME

        ulong intervalMs = (ulong)(intervalSec * 1000 + intervalNsec / 1000000);
        ulong valueMs = (ulong)(valueSec * 1000 + valueNsec / 1000000);

        timerFd.SetTime((long)intervalMs, (long)valueMs, isAbsolute);

        return 0;
    }
#pragma warning restore CS1998
}
