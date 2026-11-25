using System.Buffers.Binary;
using Fiberish.Core;
using Fiberish.Native;
using Timer = Fiberish.Core.Timer;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
    private const int ITIMER_REAL = 0;
    private const int ITIMER_VIRTUAL = 1;
    private const int ITIMER_PROF = 2;
    private const int USEC_PER_SEC = 1_000_000;

    private static long AbsoluteClockDelayMs(int clockId, long absoluteMs)
    {
        var nowMs = clockId == LinuxConstants.CLOCK_REALTIME
            ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            : MonotonicClock.Milliseconds;
        var delay = absoluteMs - nowMs;
        return delay < 0 ? 0 : delay;
    }

    private static long GetProcessCpuMs(Process proc, bool userOnly, bool roundUp = false)
    {
        var snap = proc.GetSelfCpuTimeSnapshot();
        var ns = userOnly ? snap.UserNs : snap.UserNs + snap.SystemNs;
        return ns / 1_000_000L + (roundUp && ns % 1_000_000L != 0 ? 1 : 0);
    }

    internal static void CheckCpuItimers(Process proc, KernelScheduler scheduler, CpuTimeSnapshot snapshot)
    {
        var userMs = snapshot.UserNs / 1_000_000L;
        var totalMs = (snapshot.UserNs + snapshot.SystemNs) / 1_000_000L;

        for (var which = ITIMER_VIRTUAL; which <= ITIMER_PROF; which++)
        {
            var state = proc.CpuItimers[which - 1];
            if (state == null || state.ValueMs <= 0)
                continue;

            // ITIMER_VIRTUAL counts user time only; ITIMER_PROF counts user + system.
            var cpuMs = which == ITIMER_VIRTUAL ? userMs : totalMs;
            if (cpuMs < state.NextFireCpuMs)
                continue;

            scheduler.SignalProcess(proc.TGID,
                which == ITIMER_VIRTUAL ? (int)Signal.SIGVTALRM : (int)Signal.SIGPROF);

            if (state.IntervalMs > 0)
            {
                // Signals coalesce, so collapse every missed period into one advance
                // instead of looping once per elapsed interval.
                var periods = (cpuMs - state.NextFireCpuMs) / state.IntervalMs + 1;
                state.NextFireCpuMs += periods * state.IntervalMs;
            }
            else
            {
                proc.CpuItimers[which - 1] = null;
            }
        }
    }

    private static void ArmCpuItimer(Process proc, int which, long valueMs, long intervalMs)
    {
        if (valueMs <= 0)
        {
            proc.CpuItimers[which - 1] = null;
            return;
        }

        // ITIMER_VIRTUAL counts user CPU only; ITIMER_PROF counts user + system.
        var userOnly = which == ITIMER_VIRTUAL;
        proc.CpuItimers[which - 1] = new CpuItimerState
        {
            ValueMs = valueMs,
            IntervalMs = intervalMs,
            NextFireCpuMs = GetProcessCpuMs(proc, userOnly, roundUp: true) + valueMs
        };
    }

    private static long GetRemainingMs(Timer? timer, KernelScheduler scheduler)
    {
        if (timer == null || timer.Canceled) return 0;
        // ScheduleTimer compensates for scheduler lag against the real clock, so the
        // deadline must be compared with the same reference or remaining time is
        // reported larger than requested.
        var now = Math.Max(scheduler.CurrentTick, scheduler.CurrentRealTick);
        var remaining = timer.ExpirationTick - now;
        return remaining > 0 ? remaining : 0;
    }

    private static int ReadItimerval(Engine engine, uint ptr, out long intervalMs, out long valueMs)
    {
        intervalMs = 0;
        valueMs = 0;

        var wordSize = GuestWordSize(engine);
        Span<byte> buf = stackalloc byte[wordSize * 4];
        if (!engine.CopyFromUser(ptr, buf)) return engine.IsAArch64 ? -(int)Errno.EFAULT : -(int)Errno.EINVAL;

        if (!TryTimevalToMilliseconds(buf, wordSize, out intervalMs) ||
            !TryTimevalToMilliseconds(buf[(wordSize * 2)..], wordSize, out valueMs))
            return -(int)Errno.EINVAL;
        return 0;
    }

    private static bool TryTimevalToMilliseconds(ReadOnlySpan<byte> timeval, int wordSize, out long ms)
    {
        ms = 0;
        var seconds = wordSize == 8 ? (long)ReadGuestWord(timeval, wordSize) : (int)ReadGuestWord(timeval, wordSize);
        var micros = ReadGuestWord(timeval[wordSize..], wordSize);
        if (seconds < 0 || micros >= USEC_PER_SEC) return false;

        // Round up to the scheduler's millisecond granularity and saturate at
        // Linux's signed 64-bit nanosecond limit.
        const long maxMs = long.MaxValue / 1_000_000;
        ms = seconds > maxMs / 1000
            ? maxMs
            : Math.Min(maxMs, seconds * 1000 + (long)(micros + 999) / 1000);
        return true;
    }

    private static bool WriteItimerval(Engine engine, uint ptr, long intervalMs, long valueMs)
    {
        var wordSize = GuestWordSize(engine);
        Span<byte> buf = stackalloc byte[wordSize * 4];
        WriteGuestWord(buf, wordSize, (ulong)(intervalMs / 1000));
        WriteGuestWord(buf[wordSize..], wordSize, (ulong)(intervalMs % 1000 * 1000));
        WriteGuestWord(buf[(wordSize * 2)..], wordSize, (ulong)(valueMs / 1000));
        WriteGuestWord(buf[(wordSize * 3)..], wordSize, (ulong)(valueMs % 1000 * 1000));
        return engine.CopyToUser(ptr, buf);
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
            // Linux alarm(2) rounds the remaining time up to whole seconds.
            remainingSeconds = (int)((remainingMs + 999) / 1000);

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
        var scheduler = task.CommonKernel;

        if ((int)which is < ITIMER_REAL or > ITIMER_PROF) return -(int)Errno.EINVAL;
        if (newValuePtr == 0) return -(int)Errno.EFAULT;

        // Linux validates the new value before touching old_value; an EINVAL must not
        // clobber the caller's output buffer.
        var readError = ReadItimerval(engine, newValuePtr, out var newIntervalMs, out var newValueMs);
        if (readError != 0) return readError;

        var proc = task.Process;
        if (oldValuePtr != 0)
        {
            var (oldIntervalMs, oldRemainingMs) = ReadItimerState(proc, scheduler, (int)which);
            if (!WriteItimerval(engine, oldValuePtr, oldIntervalMs, oldRemainingMs))
                return -(int)Errno.EFAULT;
        }

        if ((int)which == ITIMER_REAL)
            ArmItimerReal(proc, scheduler, newValueMs, newIntervalMs);
        else
            ArmCpuItimer(proc, (int)which, newValueMs, newIntervalMs);
        return 0;
    }

    private async ValueTask<int> SysGetitimer(Engine engine, uint which, uint currValuePtr, uint a3, uint a4,
        uint a5, uint a6)
    {
        if (engine.Owner is not FiberTask task) return -(int)Errno.EPERM;
        var scheduler = task.CommonKernel;

        if ((int)which is < ITIMER_REAL or > ITIMER_PROF) return -(int)Errno.EINVAL;
        if (currValuePtr == 0) return -(int)Errno.EFAULT;

        var (intervalMs, remainingMs) = ReadItimerState(task.Process, scheduler, (int)which);
        if (!WriteItimerval(engine, currValuePtr, intervalMs, remainingMs))
            return -(int)Errno.EFAULT;
        return 0;
    }

    private static (long IntervalMs, long RemainingMs) ReadItimerState(Process proc, KernelScheduler scheduler,
        int which)
    {
        if (which == ITIMER_REAL)
            return (proc.ItimerRealIntervalMs, GetRemainingMs(proc.AlarmTimer, scheduler));

        var state = proc.CpuItimers[which - 1];
        if (state == null || state.ValueMs <= 0)
            return (0, 0);

        var cpuMs = GetProcessCpuMs(proc, which == ITIMER_VIRTUAL);
        var remaining = state.NextFireCpuMs - cpuMs;
        return (state.IntervalMs, remaining > 0 ? remaining : 0);
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
        if (!TryGetClockResolution((int)clockId, out var resolutionNs)) return -(int)Errno.EINVAL;
        if (resPtr != 0 && !WriteTimespec64(engine, resPtr, resolutionNs)) return -(int)Errno.EFAULT;

        return 0;
    }

    private static bool IsValidSignalNumber(int signal)
    {
        return signal is >= 1 and <= 64;
    }

    private static int ValidatePosixTimerSigEvent(KernelScheduler scheduler, Process proc, in SigEvent sigEvent)
    {
        switch (sigEvent.Notify)
        {
            case LinuxConstants.SIGEV_SIGNAL:
                return IsValidSignalNumber(sigEvent.Signo) ? 0 : -(int)Errno.EINVAL;

            case LinuxConstants.SIGEV_NONE:
                return 0;

            case LinuxConstants.SIGEV_THREAD_ID:
                if (!IsValidSignalNumber(sigEvent.Signo)) return -(int)Errno.EINVAL;
                var targetTask = scheduler.GetTask(sigEvent.Tid);
                return targetTask != null && targetTask.Process == proc ? 0 : -(int)Errno.EINVAL;

            case LinuxConstants.SIGEV_THREAD:
                // SIGEV_THREAD is largely implemented in user space by libc/NPTL on Linux.
                // The raw timer_create syscall in this emulator does not model spawning a new
                // guest thread to run sigev_notify_function, so reject it explicitly instead of
                // silently creating a timer that never notifies.
                return -(int)Errno.EINVAL;

            default:
                return -(int)Errno.EINVAL;
        }
    }

    private static SigInfo BuildPosixTimerSigInfo(PosixTimer timer)
    {
        return new SigInfo
        {
            Signo = timer.SigEvent.Signo,
            Errno = 0,
            Code = 2, // SI_TIMER
            Pid = 0,
            Uid = 0,
            Value = timer.SigEvent.Value,
            TimerId = timer.Id,
            Overrun = timer.OverrunCount
        };
    }

    private static void DeliverPosixTimerNotification(KernelScheduler scheduler, Process proc, PosixTimer timer)
    {
        switch (timer.SigEvent.Notify)
        {
            case LinuxConstants.SIGEV_NONE:
                return;

            case LinuxConstants.SIGEV_SIGNAL:
                scheduler.SignalProcessInfo(proc.TGID, timer.SigEvent.Signo, BuildPosixTimerSigInfo(timer));
                return;

            case LinuxConstants.SIGEV_THREAD_ID:
                var targetTask = scheduler.GetTask(timer.SigEvent.Tid);
                if (targetTask?.Process == proc)
                    targetTask.PostSignalInfo(BuildPosixTimerSigInfo(timer));
                return;
        }
    }

    private async ValueTask<int> SysTimerGetTime32(Engine engine, uint timerId, uint currPtr, uint a3, uint a4,
        uint a5, uint a6)
    {
        if (engine.IsAArch64) return await SysTimerGetTime64(engine, timerId, currPtr, a3, a4, a5, a6);
        if (engine.Owner is not FiberTask task) return -(int)Errno.EPERM;

        var proc = task.Process;
        if (!proc.PosixTimers.TryGetValue((int)timerId, out var timer)) return -(int)Errno.EINVAL;

        if (currPtr == 0) return -(int)Errno.EFAULT;

        long valueSec = 0, valueNsec = 0;
        long intervalSec = 0, intervalNsec = 0;

        if (timer.ActiveTimer != null && !timer.ActiveTimer.Canceled)
        {
            var scheduler = (engine.Owner as FiberTask)?.CommonKernel;
            var nowMs = scheduler == null
                ? 0
                : Math.Max(scheduler.CurrentTick, scheduler.CurrentRealTick);
            var remainingMs = timer.ActiveTimer.ExpirationTick - nowMs;
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
            var nowMs = scheduler == null
                ? 0
                : Math.Max(scheduler.CurrentTick, scheduler.CurrentRealTick);
            var remainingMs = timer.ActiveTimer.ExpirationTick - nowMs;
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

        if (intervalSec < 0 || valueSec < 0 ||
            intervalNsec is < 0 or >= 1_000_000_000 || valueNsec is < 0 or >= 1_000_000_000)
            return -(int)Errno.EINVAL;

        var intervalMs = (ulong)(intervalSec * 1000L + intervalNsec / 1000000L);
        var valueMs = (ulong)(valueSec * 1000L + valueNsec / 1000000L);

        // Fetch old after validation; a rejected value must not clobber old_value.
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
                DeliverPosixTimerNotification(scheduler, proc, timer);

                if (timer.IntervalMs > 0)
                {
                    timer.OverrunCount++;
                    timer.ActiveTimer = scheduler.ScheduleTimer((long)timer.IntervalMs, OnTimerTick);
                }
            }

            var targetDelayMs = (long)valueMs;
            if ((flags & 1) != 0) // TIMER_ABSTIME
                targetDelayMs = AbsoluteClockDelayMs(timer.ClockId, (long)valueMs);

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

        if (intervalSec < 0 || valueSec < 0 ||
            intervalNsec is < 0 or >= 1_000_000_000 || valueNsec is < 0 or >= 1_000_000_000 ||
            intervalSec > long.MaxValue / 1000 || valueSec > long.MaxValue / 1000)
            return -(int)Errno.EINVAL;

        var intervalMs = (ulong)(intervalSec * 1000 + intervalNsec / 1000000);
        var valueMs = (ulong)(valueSec * 1000 + valueNsec / 1000000);

        // Fetch old after validation; a rejected value must not clobber old_value.
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
                DeliverPosixTimerNotification(scheduler, proc, timer);

                if (timer.IntervalMs > 0)
                {
                    timer.OverrunCount++;
                    timer.ActiveTimer = scheduler.ScheduleTimer((long)timer.IntervalMs, OnTimerTick);
                }
            }

            // TIMER_ABSTIME (1): valueMs is an absolute CLOCK_REALTIME/MONOTONIC value.
            var targetDelayMs = (long)valueMs;
            if ((flags & 1) != 0)
                targetDelayMs = AbsoluteClockDelayMs(timer.ClockId, (long)valueMs);

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
        var scheduler = task.CommonKernel;
        var timerId = proc.NextPosixTimerId++;

        SigEvent sigEvent = default;
        if (sevpPtr != 0)
        {
            var buf = new byte[64]; // sigevent is generally padded to 64 bytes
            if (!engine.CopyFromUser(sevpPtr, buf)) return -(int)Errno.EFAULT;

            var wordSize = GuestWordSize(engine);
            sigEvent.Value = ReadGuestWord(buf, wordSize);
            sigEvent.Signo = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(wordSize, 4));
            sigEvent.Notify = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(wordSize + 4, 4));
            sigEvent.Tid = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(wordSize + 8, 4));
        }
        else
        {
            sigEvent.Notify = LinuxConstants.SIGEV_SIGNAL;
            sigEvent.Signo = LinuxConstants.SIGALRM;
            sigEvent.Value = (ulong)timerId;
        }

        var validateSigEventRc = ValidatePosixTimerSigEvent(scheduler, proc, sigEvent);
        if (validateSigEventRc < 0) return validateSigEventRc;

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
        if (!FDs.TryGetValue((int)fd, out var file)) return -(int)Errno.EBADF;
        if (file.OpenedInode is not TimerFdInode timerFd) return -(int)Errno.EINVAL;
        if (engine.Owner is not FiberTask task) return -(int)Errno.EPERM;

        if (curValuePtr == 0) return -(int)Errno.EFAULT;

        timerFd.GetTime(task, out var intervalMs, out var valueMs);

        Span<byte> buf = stackalloc byte[32];
        BinaryPrimitives.WriteInt64LittleEndian(buf[..8], intervalMs / 1000);
        BinaryPrimitives.WriteInt64LittleEndian(buf.Slice(8, 8), intervalMs % 1000 * 1000000);
        BinaryPrimitives.WriteInt64LittleEndian(buf.Slice(16, 8), valueMs / 1000);
        BinaryPrimitives.WriteInt64LittleEndian(buf.Slice(24, 8), valueMs % 1000 * 1000000);

        if (!engine.CopyToUser(curValuePtr, buf)) return -(int)Errno.EFAULT;

        return 0;
    }

    private async ValueTask<int> SysTimerFdSetTime64(Engine engine, uint fd, uint flags, uint newValuePtr,
        uint oldValuePtr, uint a5, uint a6)
    {
        if (!FDs.TryGetValue((int)fd, out var file)) return -(int)Errno.EBADF;
        if (file.OpenedInode is not TimerFdInode timerFd) return -(int)Errno.EINVAL;
        if (engine.Owner is not FiberTask task) return -(int)Errno.EPERM;

        if ((flags & ~(uint)(LinuxConstants.TFD_TIMER_ABSTIME | LinuxConstants.TFD_TIMER_CANCEL_ON_SET)) != 0) return -(int)Errno.EINVAL;

        if (newValuePtr == 0) return -(int)Errno.EFAULT;

        Span<byte> buf = stackalloc byte[32];
        if (!engine.CopyFromUser(newValuePtr, buf)) return -(int)Errno.EFAULT;

        var intervalSec = BinaryPrimitives.ReadInt64LittleEndian(buf[..8]);
        var intervalNsec = BinaryPrimitives.ReadInt64LittleEndian(buf.Slice(8, 8));
        var valueSec = BinaryPrimitives.ReadInt64LittleEndian(buf.Slice(16, 8));
        var valueNsec = BinaryPrimitives.ReadInt64LittleEndian(buf.Slice(24, 8));

        if (intervalSec < 0 || valueSec < 0 ||
            intervalNsec is < 0 or >= 1_000_000_000 || valueNsec is < 0 or >= 1_000_000_000 ||
            intervalSec > long.MaxValue / 1000 || valueSec > long.MaxValue / 1000)
            return -(int)Errno.EINVAL;

        if (oldValuePtr != 0)
        {
            var oldRc = await SysTimerFdGetTime64(engine, fd, oldValuePtr, 0, 0, 0, 0);
            if (oldRc != 0) return oldRc;
        }

        var isAbsolute = (flags & LinuxConstants.TFD_TIMER_ABSTIME) != 0;

        var intervalMs = (ulong)(intervalSec * 1000 + intervalNsec / 1000000);
        var valueMs = (ulong)(valueSec * 1000 + valueNsec / 1000000);

        timerFd.SetTime(task, (long)intervalMs, (long)valueMs, isAbsolute);

        return 0;
    }
#pragma warning restore CS1998
}
