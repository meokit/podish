using System.Buffers.Binary;
using Fiberish.Core;
using Fiberish.Native;
using Timer = Fiberish.Core.Timer;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
    private const int ITIMER_REAL = 0;
    private const int USEC_PER_SEC = 1_000_000;

    private static long GetRemainingMs(Timer? timer, KernelScheduler scheduler)
    {
        if (timer == null || timer.Canceled) return 0;
        var remaining = timer.ExpirationTick - scheduler.CurrentTick;
        return remaining > 0 ? remaining : 0;
    }

    private static bool TryReadItimerval32(SyscallManager sm, uint ptr, out long intervalMs, out long valueMs)
    {
        intervalMs = 0;
        valueMs = 0;

        var buf = new byte[16];
        if (!sm.Engine.CopyFromUser(ptr, buf)) return false;

        var intervalSec = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(0, 4));
        var intervalUsec = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(4, 4));
        var valueSec = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(8, 4));
        var valueUsec = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(12, 4));

        if (intervalSec < 0 || valueSec < 0) return false;
        if ((uint)intervalUsec >= USEC_PER_SEC || (uint)valueUsec >= USEC_PER_SEC) return false;

        intervalMs = intervalSec * 1000L + intervalUsec / 1000L;
        valueMs = valueSec * 1000L + valueUsec / 1000L;
        return true;
    }

    private static bool WriteItimerval32(SyscallManager sm, uint ptr, long intervalMs, long valueMs)
    {
        var intervalSec = intervalMs / 1000;
        var intervalUsec = intervalMs % 1000 * 1000;
        var valueSec = valueMs / 1000;
        var valueUsec = valueMs % 1000 * 1000;

        var buf = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), (int)intervalSec);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), (int)intervalUsec);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8, 4), (int)valueSec);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(12, 4), (int)valueUsec);
        return sm.Engine.CopyToUser(ptr, buf);
    }

    private static void ArmItimerReal(Process proc, KernelScheduler scheduler, long firstMs, long intervalMs)
    {
        proc.AlarmTimer?.Cancel();
        proc.AlarmTimer = null;
        proc.ItimerRealIntervalMs = intervalMs;

        if (firstMs <= 0) return;

        void Fire()
        {
            scheduler.SignalProcess(proc.TGID, LinuxConstants.SIGALRM);

            if (proc.ItimerRealIntervalMs > 0)
                proc.AlarmTimer = scheduler.ScheduleTimer(proc.ItimerRealIntervalMs, Fire);
            else
                proc.AlarmTimer = null;
        }

        proc.AlarmTimer = scheduler.ScheduleTimer(firstMs, Fire);
    }

#pragma warning disable CS1998 // Async method lacks await operators
    private async ValueTask<int> SysAlarm(Engine engine, uint seconds, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        if (engine.Owner is not FiberTask task) return -(int)Errno.EPERM;

        var proc = task.Process;
        var scheduler = (engine.Owner as FiberTask)?.CommonKernel;
        if (scheduler == null) return -(int)Errno.ENOSYS;

        var remainingSeconds = 0;
        var remainingMs = GetRemainingMs(proc.AlarmTimer, scheduler);
        if (remainingMs > 0)
        {
            remainingSeconds = (int)(remainingMs / 1000);
            if (remainingSeconds == 0) remainingSeconds = 1;
        }

        if (seconds > 0)
        {
            var ms = seconds * 1000L;
            ArmItimerReal(proc, scheduler, ms, 0);
        }
        else
        {
            ArmItimerReal(proc, scheduler, 0, 0);
        }

        return remainingSeconds;
    }

    private async ValueTask<int> SysSetitimer(Engine engine, uint which, uint newValuePtr, uint oldValuePtr,
        uint a4, uint a5, uint a6)
    {
        if (engine.Owner is not FiberTask task) return -(int)Errno.EPERM;
        var scheduler = (engine.Owner as FiberTask)?.CommonKernel;
        if (scheduler == null) return -(int)Errno.ENOSYS;

        if ((int)which != ITIMER_REAL) return -(int)Errno.EINVAL;
        if (newValuePtr == 0) return -(int)Errno.EFAULT;

        var proc = task.Process;
        if (oldValuePtr != 0)
        {
            var oldRemainingMs = GetRemainingMs(proc.AlarmTimer, scheduler);
            if (!WriteItimerval32(this, oldValuePtr, proc.ItimerRealIntervalMs, oldRemainingMs))
                return -(int)Errno.EFAULT;
        }

        if (!TryReadItimerval32(this, newValuePtr, out var newIntervalMs, out var newValueMs))
            return -(int)Errno.EINVAL;

        ArmItimerReal(proc, scheduler, newValueMs, newIntervalMs);
        return 0;
    }

    private async ValueTask<int> SysGetitimer(Engine engine, uint which, uint currValuePtr, uint a3, uint a4,
        uint a5, uint a6)
    {
        if (engine.Owner is not FiberTask task) return -(int)Errno.EPERM;
        var scheduler = (engine.Owner as FiberTask)?.CommonKernel;
        if (scheduler == null) return -(int)Errno.ENOSYS;

        if ((int)which != ITIMER_REAL) return -(int)Errno.EINVAL;
        if (currValuePtr == 0) return -(int)Errno.EFAULT;

        var proc = task.Process;
        var remainingMs = GetRemainingMs(proc.AlarmTimer, scheduler);
        if (!WriteItimerval32(this, currValuePtr, proc.ItimerRealIntervalMs, remainingMs))
            return -(int)Errno.EFAULT;
        return 0;
    }

    private async ValueTask<int> SysClockSetTime64(Engine engine, uint clockId, uint tsPtr, uint a3, uint a4,
        uint a5, uint a6)
    {
        // Not permitted
        return -(int)Errno.EPERM;
    }

    private async ValueTask<int> SysClockAdjTime64(Engine engine, uint clockId, uint txPtr, uint a3, uint a4,
        uint a5, uint a6)
    {
        // Not permitted / Stubbed
        return -(int)Errno.EPERM;
    }

    private async ValueTask<int> SysClockGetResTime64(Engine engine, uint clockId, uint resPtr, uint a3, uint a4,
        uint a5, uint a6)
    {
        if (resPtr != 0)
        {
            var buf = new byte[16];
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(0, 8), 0); // 0 sec
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(8, 8), 1000000); // 1,000,000 ns (1 ms resolution)
            if (!engine.CopyToUser(resPtr, buf)) return -(int)Errno.EFAULT;
        }

        return 0;
    }

    private async ValueTask<int> SysTimerGetTime32(Engine engine, uint timerId, uint currPtr, uint a3, uint a4,
        uint a5, uint a6)
    {
        if (engine.Owner is not FiberTask task) return -(int)Errno.EPERM;

        var proc = task.Process;
        if (!proc.PosixTimers.TryGetValue((int)timerId, out var timer)) return -(int)Errno.EINVAL;

        if (currPtr == 0) return -(int)Errno.EFAULT;

        long valueSec = 0, valueNsec = 0;
        long intervalSec = 0, intervalNsec = 0;

        if (timer.ActiveTimer != null && !timer.ActiveTimer.Canceled)
        {
            var scheduler = (engine.Owner as FiberTask)?.CommonKernel;
            var remainingMs = timer.ActiveTimer.ExpirationTick - (scheduler?.CurrentTick ?? 0);
            if (remainingMs < 0) remainingMs = 0;
            valueSec = remainingMs / 1000;
            valueNsec = remainingMs % 1000 * 1000000;

            intervalSec = (long)(timer.IntervalMs / 1000);
            intervalNsec = (long)(timer.IntervalMs % 1000 * 1000000);
        }

        var buf = new byte[16]; // old_itimerspec is 16 bytes (4 sec, 4 nsec, 4 sec, 4 nsec)
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), (int)intervalSec);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), (int)intervalNsec);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8, 4), (int)valueSec);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(12, 4), (int)valueNsec);

        if (!engine.CopyToUser(currPtr, buf)) return -(int)Errno.EFAULT;
        return 0;
    }

    private async ValueTask<int> SysTimerGetTime64(Engine engine, uint timerId, uint currPtr, uint a3, uint a4,
        uint a5, uint a6)
    {
        if (engine.Owner is not FiberTask task) return -(int)Errno.EPERM;

        var proc = task.Process;
        if (!proc.PosixTimers.TryGetValue((int)timerId, out var timer)) return -(int)Errno.EINVAL;

        if (currPtr == 0) return -(int)Errno.EFAULT;

        long valueSec = 0, valueNsec = 0;
        long intervalSec = 0, intervalNsec = 0;

        if (timer.ActiveTimer != null && !timer.ActiveTimer.Canceled)
        {
            var scheduler = (engine.Owner as FiberTask)?.CommonKernel;
            var remainingMs = timer.ActiveTimer.ExpirationTick - (scheduler?.CurrentTick ?? 0);
            if (remainingMs < 0) remainingMs = 0;
            valueSec = remainingMs / 1000;
            valueNsec = remainingMs % 1000 * 1000000;

            intervalSec = (long)(timer.IntervalMs / 1000);
            intervalNsec = (long)(timer.IntervalMs % 1000 * 1000000);
        }

        var buf = new byte[32]; // itimerspec64 is 32 bytes (8 sec, 8 nsec, 8 sec, 8 nsec) on 32-bit x86
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(0, 8), intervalSec);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(8, 8), intervalNsec);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(16, 8), valueSec);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(24, 8), valueNsec);

        if (!engine.CopyToUser(currPtr, buf)) return -(int)Errno.EFAULT;
        return 0;
    }

    private async ValueTask<int> SysTimerSetTime32(Engine engine, uint timerId, uint flags, uint newPtr,
        uint oldPtr, uint a5, uint a6)
    {
        if (engine.Owner is not FiberTask task) return -(int)Errno.EPERM;
        var scheduler = (engine.Owner as FiberTask)?.CommonKernel;
        if (scheduler == null) return -(int)Errno.ENOSYS;

        var proc = task.Process;
        if (!proc.PosixTimers.TryGetValue((int)timerId, out var timer)) return -(int)Errno.EINVAL;

        if (newPtr == 0) return -(int)Errno.EFAULT;

        var buf = new byte[16];
        if (!engine.CopyFromUser(newPtr, buf)) return -(int)Errno.EFAULT;

        var intervalSec = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(0, 4));
        var intervalNsec = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(4, 4));
        var valueSec = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(8, 4));
        var valueNsec = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(12, 4));

        var intervalMs = (ulong)(intervalSec * 1000L + intervalNsec / 1000000L);
        var valueMs = (ulong)(valueSec * 1000L + valueNsec / 1000000L);

        // Fetch old if needed
        if (oldPtr != 0) await SysTimerGetTime32(engine, timerId, oldPtr, 0, 0, 0, 0);

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

            var targetDelayMs = (long)valueMs;
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

    private async ValueTask<int> SysTimerSetTime64(Engine engine, uint timerId, uint flags, uint newPtr,
        uint oldPtr, uint a5, uint a6)
    {
        if (engine.Owner is not FiberTask task) return -(int)Errno.EPERM;
        var scheduler = (engine.Owner as FiberTask)?.CommonKernel;
        if (scheduler == null) return -(int)Errno.ENOSYS;

        var proc = task.Process;
        if (!proc.PosixTimers.TryGetValue((int)timerId, out var timer)) return -(int)Errno.EINVAL;

        if (newPtr == 0) return -(int)Errno.EFAULT;

        var buf = new byte[32];
        if (!engine.CopyFromUser(newPtr, buf)) return -(int)Errno.EFAULT;

        var intervalSec = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(0, 8));
        var intervalNsec = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(8, 8));
        var valueSec = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(16, 8));
        var valueNsec = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(24, 8));

        var intervalMs = (ulong)(intervalSec * 1000 + intervalNsec / 1000000);
        var valueMs = (ulong)(valueSec * 1000 + valueNsec / 1000000);

        // Fetch old if needed
        if (oldPtr != 0) await SysTimerGetTime64(engine, timerId, oldPtr, 0, 0, 0, 0);

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
            var targetDelayMs = (long)valueMs;
            if ((flags & 1) != 0) // TIMER_ABSTIME
            {
                // Simple approx: valueMs - current physical time (Assuming CLOCK_REALTIME/MONOTONIC matching)
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); // Hack fallback if clocks misaligned
                if (timer.ClockId == LinuxConstants.CLOCK_REALTIME)
                {
                    targetDelayMs = (long)valueMs - now;
                    if (targetDelayMs < 0) targetDelayMs = 0;
                }
                else // CLOCK_MONOTONIC uses (KernelScheduler?)nullTick
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

    private async ValueTask<int> SysTimerCreate(Engine engine, uint clockId, uint sevpPtr, uint timerIdPtr,
        uint a4, uint a5, uint a6)
    {
        if (engine.Owner is not FiberTask task) return -(int)Errno.EPERM;

        var proc = task.Process;
        var timerId = proc.NextPosixTimerId++;

        SigEvent sigEvent = default;
        if (sevpPtr != 0)
        {
            var buf = new byte[64]; // sigevent is generally padded to 64 bytes
            if (!engine.CopyFromUser(sevpPtr, buf)) return -(int)Errno.EFAULT;

            sigEvent.Value =
                BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(0,
                    8)); // Note: Depending on padding, might be (0,4) for 32 bit sys
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

        var timer = new PosixTimer(timerId, (int)clockId, sigEvent, proc);
        proc.PosixTimers[timerId] = timer;

        if (timerIdPtr != 0)
        {
            var idBuf = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(idBuf, timerId);
            if (!engine.CopyToUser(timerIdPtr, idBuf)) return -(int)Errno.EFAULT;
        }

        return 0;
    }

    private async ValueTask<int> SysTimerDelete(Engine engine, uint timerId, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        if (engine.Owner is not FiberTask task) return -(int)Errno.EPERM;

        var proc = task.Process;
        if (!proc.PosixTimers.TryGetValue((int)timerId, out var timer)) return -(int)Errno.EINVAL;

        timer.ActiveTimer?.Cancel();
        proc.PosixTimers.Remove((int)timerId);

        return 0;
    }

    private async ValueTask<int> SysTimerGetOverrun(Engine engine, uint timerId, uint a2, uint a3, uint a4,
        uint a5, uint a6)
    {
        if (engine.Owner is not FiberTask task) return -(int)Errno.EPERM;

        var proc = task.Process;
        if (!proc.PosixTimers.TryGetValue((int)timerId, out var timer)) return -(int)Errno.EINVAL;

        return timer.OverrunCount;
    }

    private async ValueTask<int> SysTimerFdGetTime64(Engine engine, uint fd, uint curValuePtr, uint a3, uint a4,
        uint a5, uint a6)
    {
        if (!FDs.TryGetValue((int)fd, out var file) || file.OpenedInode is not TimerFdInode timerFd)
            return -(int)Errno.EBADF;
        if (engine.Owner is not FiberTask task) return -(int)Errno.EPERM;

        if (curValuePtr == 0) return -(int)Errno.EFAULT;

        timerFd.GetTime(task, out var intervalMs, out var valueMs);

        var buf = new byte[32];
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(0, 8), intervalMs / 1000);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(8, 8), intervalMs % 1000 * 1000000);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(16, 8), valueMs / 1000);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(24, 8), valueMs % 1000 * 1000000);

        if (!engine.CopyToUser(curValuePtr, buf)) return -(int)Errno.EFAULT;

        return 0;
    }

    private async ValueTask<int> SysTimerFdSetTime64(Engine engine, uint fd, uint flags, uint newValuePtr,
        uint oldValuePtr, uint a5, uint a6)
    {
        if (!FDs.TryGetValue((int)fd, out var file) || file.OpenedInode is not TimerFdInode timerFd)
            return -(int)Errno.EBADF;
        if (engine.Owner is not FiberTask task) return -(int)Errno.EPERM;

        if (newValuePtr == 0) return -(int)Errno.EFAULT;

        var buf = new byte[32];
        if (!engine.CopyFromUser(newValuePtr, buf)) return -(int)Errno.EFAULT;

        var intervalSec = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(0, 8));
        var intervalNsec = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(8, 8));
        var valueSec = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(16, 8));
        var valueNsec = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(24, 8));

        if (oldValuePtr != 0) await SysTimerFdGetTime64(engine, fd, oldValuePtr, 0, 0, 0, 0);

        var isAbsolute = (flags & 1) != 0; // TFD_TIMER_ABSTIME

        var intervalMs = (ulong)(intervalSec * 1000 + intervalNsec / 1000000);
        var valueMs = (ulong)(valueSec * 1000 + valueNsec / 1000000);

        timerFd.SetTime(task, (long)intervalMs, (long)valueMs, isAbsolute);

        return 0;
    }
#pragma warning restore CS1998
}