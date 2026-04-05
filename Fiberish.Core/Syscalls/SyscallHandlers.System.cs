using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Fiberish.Auth.Cred;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.X86.Native;
using Microsoft.Extensions.Logging;
using Process = Fiberish.Core.Process;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
    private const long VirtualCpuHz = 1_000_000_000L; // Assume a fixed 1 GHz virtual CPU.
    private const int UserHz = 100; // Linux i386 userspace clock ticks per second for times().

    private const int SupportedMembarrierCommands =
        LinuxConstants.MEMBARRIER_CMD_GLOBAL |
        LinuxConstants.MEMBARRIER_CMD_GLOBAL_EXPEDITED |
        LinuxConstants.MEMBARRIER_CMD_REGISTER_GLOBAL_EXPEDITED |
        LinuxConstants.MEMBARRIER_CMD_PRIVATE_EXPEDITED |
        LinuxConstants.MEMBARRIER_CMD_REGISTER_PRIVATE_EXPEDITED |
        LinuxConstants.MEMBARRIER_CMD_PRIVATE_EXPEDITED_SYNC_CORE |
        LinuxConstants.MEMBARRIER_CMD_REGISTER_PRIVATE_EXPEDITED_SYNC_CORE;

    private const int MembarrierRegisterCommands =
        LinuxConstants.MEMBARRIER_CMD_REGISTER_GLOBAL_EXPEDITED |
        LinuxConstants.MEMBARRIER_CMD_REGISTER_PRIVATE_EXPEDITED |
        LinuxConstants.MEMBARRIER_CMD_REGISTER_PRIVATE_EXPEDITED_SYNC_CORE;

    private static readonly long VirtualCpuStartTimestamp = Stopwatch.GetTimestamp();
    private static readonly long SysinfoUptimeStartTimestamp = Stopwatch.GetTimestamp();

    private static long GetVirtualCpuCycles()
    {
        var elapsed = Stopwatch.GetTimestamp() - VirtualCpuStartTimestamp;
        if (elapsed <= 0) return 0;
        // Avoid 64-bit overflow: (elapsed * 1e9) can overflow quickly on high-frequency timers.
        var q = elapsed / Stopwatch.Frequency;
        var r = elapsed % Stopwatch.Frequency;
        return q * VirtualCpuHz + r * VirtualCpuHz / Stopwatch.Frequency;
    }

    private static int GetTimesClockTicks()
    {
        var cycles = GetVirtualCpuCycles();
        var ticks = cycles / (VirtualCpuHz / UserHz);
        return unchecked((int)ticks);
    }

    private static int GetSysinfoUptimeSeconds()
    {
        var elapsed = Stopwatch.GetTimestamp() - SysinfoUptimeStartTimestamp;
        if (elapsed <= 0) return 0;
        var seconds = elapsed / Stopwatch.Frequency;
        if (seconds > int.MaxValue) return int.MaxValue;
        return (int)seconds;
    }

#pragma warning disable CS1998 // Async method lacks await operators - syscall handlers require async signature
    private async ValueTask<int> SysTime(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var t = DateTimeOffset.Now.ToUnixTimeSeconds();
        if (a1 != 0)
            if (!engine.CopyToUser(a1, BitConverter.GetBytes((uint)t)))
                return -(int)Errno.EFAULT;
        return (int)t;
    }

    private async ValueTask<int> SysTimes(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var ticks = GetTimesClockTicks();

        // struct tms on i386:
        // long tms_utime;  long tms_stime;  long tms_cutime;  long tms_cstime;
        if (a1 != 0)
        {
            var tms = new byte[16];
            BinaryPrimitives.WriteInt32LittleEndian(tms.AsSpan(0, 4), ticks);
            BinaryPrimitives.WriteInt32LittleEndian(tms.AsSpan(4, 4), 0);
            BinaryPrimitives.WriteInt32LittleEndian(tms.AsSpan(8, 4), 0);
            BinaryPrimitives.WriteInt32LittleEndian(tms.AsSpan(12, 4), 0);
            if (!engine.CopyToUser(a1, tms)) return -(int)Errno.EFAULT;
        }

        return ticks;
    }

    private async ValueTask<int> SysUname(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var t = engine.Owner as FiberTask;
        if (t == null) return -(int)Errno.EPERM;

        var uts = t.Process.UTS;

        void WriteUnameString(uint addr, string s)
        {
            var buf = new byte[65];
            var bytes = Encoding.ASCII.GetBytes(s);
            Array.Copy(bytes, buf, Math.Min(bytes.Length, 64));
            if (!engine.CopyToUser(addr, buf)) return;
        }

        WriteUnameString(a1, uts.SysName);
        WriteUnameString(a1 + 65, uts.NodeName);
        WriteUnameString(a1 + 130, uts.Release);
        WriteUnameString(a1 + 195, uts.Version);
        WriteUnameString(a1 + 260, uts.Machine);
        WriteUnameString(a1 + 325, uts.DomainName);

        return 0;
    }

    private async ValueTask<int> SysSysinfo(Engine engine, uint sysinfoAddr, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var t = engine.Owner as FiberTask;
        if (t == null) return -(int)Errno.EPERM;

        var snapshot = MemoryStatsSnapshot.Capture(this);
        var totalBytes = Math.Max(0, snapshot.MemTotalBytes);
        var freeBytes = Math.Max(0, snapshot.FreeBytes);
        var sharedBytes = Math.Max(0, snapshot.ShmemBytes);
        var bufferBytes = 0L; // /proc/meminfo currently reports Buffers: 0
        var totalSwapBytes = 0L;
        var freeSwapBytes = 0L;
        var totalHighBytes = 0L;
        var freeHighBytes = 0L;

        var maxBytes = Math.Max(
            totalBytes,
            Math.Max(
                freeBytes,
                Math.Max(
                    sharedBytes,
                    Math.Max(bufferBytes,
                        Math.Max(totalSwapBytes, Math.Max(freeSwapBytes, Math.Max(totalHighBytes, freeHighBytes)))))));

        var memUnit = maxBytes <= int.MaxValue ? 1 : LinuxConstants.PageSize;

        int ToSysValue(long bytes)
        {
            if (bytes <= 0) return 0;
            var scaled = bytes / memUnit;
            if (scaled > int.MaxValue) return int.MaxValue;
            return (int)scaled;
        }

        var scheduler = t.CommonKernel;
        var processCount = scheduler?.GetProcessesSnapshot().Count ?? 1;
        if (processCount <= 0) processCount = 1;
        if (processCount > short.MaxValue) processCount = short.MaxValue;

        var info = new SysInfo
        {
            Uptime = GetSysinfoUptimeSeconds(),
            Loads = [65536, 65536, 65536],
            TotalRam = ToSysValue(totalBytes),
            FreeRam = ToSysValue(freeBytes),
            SharedRam = ToSysValue(sharedBytes),
            BufferRam = ToSysValue(bufferBytes),
            TotalSwap = ToSysValue(totalSwapBytes),
            FreeSwap = ToSysValue(freeSwapBytes),
            Procs = (short)processCount,
            TotalHigh = ToSysValue(totalHighBytes),
            FreeHigh = ToSysValue(freeHighBytes),
            MemUnit = memUnit,
            Padding = new byte[8]
        };

        if (sysinfoAddr != 0)
        {
            var size = Marshal.SizeOf<SysInfo>();
            var buffer = new byte[size];
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                Marshal.Copy(ptr, buffer, 0, size);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }

            if (!engine.CopyToUser(sysinfoAddr, buffer)) return -(int)Errno.EFAULT;
        }

        return 0;
    }

    private async ValueTask<int> SysGetPid(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var task = engine.Owner as FiberTask;
        return task?.Process.TGID ?? 1;
    }

    private async ValueTask<int> SysGetPPid(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var task = engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        return task.Process.PPID;
    }

    private async ValueTask<int> SysGettid(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var task = engine.Owner as FiberTask;
        return task?.TID ?? -1;
    }

    private async ValueTask<int> SysSchedGetAffinity(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        const int affinityMaskBytes = sizeof(uint);
        var cpusetsize = a2;
        var maskPtr = a3;

        if (cpusetsize < affinityMaskBytes)
            return -(int)Errno.EINVAL;

        var mask = new byte[cpusetsize];
        mask[0] = 0x01; // Report CPU0 as available.
        if (!engine.CopyToUser(maskPtr, mask))
            return -(int)Errno.EFAULT;

        return affinityMaskBytes;
    }

    private async ValueTask<int> SysGetPgid(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var task = engine.Owner as FiberTask;
        return task?.Process.PGID ?? -1;
    }

    private async ValueTask<int> SysUmask(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var task = engine.Owner as FiberTask;
        if (task == null) return 0;
        return CredentialService.SetUmask(task.Process, (int)a1);
    }

    private async ValueTask<int> SysSethostname(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var task = engine.Owner as FiberTask;
        if (task == null || task.Process.EUID != 0) return -(int)Errno.EPERM;

        var name = ReadString(a1);
        task.Process.UTS.NodeName = name;
        return 0;
    }

    private async ValueTask<int> SysSetdomainname(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var task = engine.Owner as FiberTask;
        if (task == null || task.Process.EUID != 0) return -(int)Errno.EPERM;

        var name = ReadString(a1);
        task.Process.UTS.DomainName = name;
        return 0;
    }

    private async ValueTask<int> SysSchedYield(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        engine.Yield();
        return 0;
    }

    private async ValueTask<int> SysPause(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var task = engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        Logger.LogInformation("[SysPause] Task pausing, waiting for signal");

        if (await new PauseAwaitable(task) == AwaitResult.Interrupted)
            return -(int)Errno.ERESTARTSYS;
        return -(int)Errno.EINTR;
    }

    private readonly struct PauseAwaitable
    {
        private readonly FiberTask _task;

        public PauseAwaitable(FiberTask task)
        {
            _task = task;
        }

        public PauseAwaiter GetAwaiter()
        {
            return new PauseAwaiter(_task);
        }
    }

    private struct PauseAwaiter : INotifyCompletion
    {
        private readonly FiberTask _task;
        private readonly FiberTask.WaitToken _token;

        public PauseAwaiter(FiberTask task)
        {
            _task = task;
            _token = task.BeginWaitToken();
        }

        public bool IsCompleted => false;

        public void OnCompleted(Action continuation)
        {
            if (!_task.TryEnterAsyncOperation(_token, out var operation) || operation == null)
                return;

            var state = new PauseOperation(_task, continuation, operation);
            _task.RegisterSignalWait(_token, 0, FiberTask.SignalWaitKind.Interrupting);
            _task.ArmInterruptingSignalSafetyNet(_token, state.OnSignal);
        }

        public AwaitResult GetResult()
        {
            var reason = _task.CompleteWaitToken(_token);
            if (reason != WakeReason.None) return AwaitResult.Interrupted;

            return AwaitResult.Completed;
        }

        private sealed class PauseOperation
        {
            private readonly TaskAsyncOperationHandle _operation;

            public PauseOperation(FiberTask task, Action continuation, TaskAsyncOperationHandle operation)
            {
                _operation = operation;
                _operation.TryInitialize(continuation);
            }

            public void OnSignal()
            {
                _operation.TryComplete(WakeReason.Signal);
            }
        }
    }

    private async ValueTask<int> SysGetTimeOfDay(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var tvPtr = a1;

        // Use UtcNow for REALTIME (gettimeofday is strictly REALTIME)
        var ticks = DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks;
        var secs = ticks / TimeSpan.TicksPerSecond;
        var usecs = ticks % TimeSpan.TicksPerSecond / 10; // 100ns -> 1us

        if (tvPtr != 0)
        {
            var buf = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), (int)secs);
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), (int)usecs);
            if (!engine.CopyToUser(tvPtr, buf)) return -(int)Errno.EFAULT;
        }

        return 0;
    }

    private async ValueTask<int> SysClockGetTime(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var clockId = (int)a1;
        var tsPtr = a2;

        if (!TryGetClockTime(clockId, out var secs, out var nsecs))
            return -(int)Errno.EINVAL;

        var buf = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), (int)secs);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), (int)nsecs);
        if (!engine.CopyToUser(tsPtr, buf)) return -(int)Errno.EFAULT;

        return 0;
    }

    private async ValueTask<int> SysClockGetTime64(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var clockId = (int)a1;
        var tsPtr = a2;

        if (!TryGetClockTime(clockId, out var secs, out var nsecs))
            return -(int)Errno.EINVAL;

        var buf = new byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(0, 8), secs);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(8, 8), nsecs);

        if (Strace)
            Logger.LogTrace(
                "[SysClockGetTime64] clockId={ClockId} tsPtr=0x{TsPtr:X} secs={Secs} nsecs={Nsecs} bytes={Bytes}",
                clockId,
                tsPtr,
                secs,
                nsecs,
                Convert.ToHexString(buf));

        if (!engine.CopyToUser(tsPtr, buf)) return -(int)Errno.EFAULT;

        return 0;
    }

    private async ValueTask<int> SysNanosleep(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var reqBuf = new byte[8];
        if (!engine.CopyFromUser(a1, reqBuf)) return -(int)Errno.EFAULT;
        var sec = BinaryPrimitives.ReadInt32LittleEndian(reqBuf.AsSpan(0, 4));
        var nsec = BinaryPrimitives.ReadInt32LittleEndian(reqBuf.AsSpan(4, 4));
        if (sec < 0 || nsec < 0 || nsec >= 1_000_000_000) return -(int)Errno.EINVAL;

        if (engine.Owner is FiberTask ownerTask)
            Logger.LogInformation("[SysNanosleep] pid={Pid} tid={Tid} req={{sec={Sec}, nsec={Nsec}}}",
                ownerTask.Process.TGID, ownerTask.TID, sec, nsec);

        if (engine.Owner is not FiberTask fiberTask) return -(int)Errno.EPERM;
        if (!TryConvertTimespecToTimeoutMs(sec, nsec, out var totalMs, out var timeoutErr))
            return timeoutErr;

        var startNs = GetSleepClockNs(LinuxConstants.CLOCK_MONOTONIC);
        var requestedNs = checked(sec * 1_000_000_000L + nsec);

        // Linux nanosleep(2): invalid tv_nsec is EINVAL; interrupted relative sleeps return rem.
        var res = await new SleepAwaitable(totalMs, fiberTask);

        if (res == AwaitResult.Interrupted)
        {
            // Linux nanosleep(2): interrupted relative sleeps report the unslept interval in rem.
            if (a2 != 0)
            {
                var elapsedNs = Math.Max(0L, GetSleepClockNs(LinuxConstants.CLOCK_MONOTONIC) - startNs);
                var remainingNs = Math.Max(0L, requestedNs - elapsedNs);
                if (!WriteTimespec32(engine, a2, remainingNs)) return -(int)Errno.EFAULT;
            }

            return -(int)Errno.ERESTARTSYS;
        }

        return 0;
    }

    private async ValueTask<int> SysClockNanosleepTime64(Engine engine, uint a1, uint a2, uint a3, uint a4,
        uint a5, uint a6)
    {
        var clockId = (int)a1;
        var flags = (int)a2;
        var reqPtr = a3;
        var remPtr = a4;

        if ((flags & ~1) != 0) return -(int)Errno.EINVAL;
        if (clockId != LinuxConstants.CLOCK_REALTIME && clockId != LinuxConstants.CLOCK_MONOTONIC &&
            clockId != LinuxConstants.CLOCK_BOOTTIME)
            return -(int)Errno.EINVAL;

        var reqBuf = new byte[16]; // timespec64
        if (!engine.CopyFromUser(reqPtr, reqBuf)) return -(int)Errno.EFAULT;
        var sec = BinaryPrimitives.ReadInt64LittleEndian(reqBuf.AsSpan(0, 8));
        var nsec = BinaryPrimitives.ReadInt64LittleEndian(reqBuf.AsSpan(8, 8));
        if (sec < 0 || nsec < 0 || nsec >= 1_000_000_000) return -(int)Errno.EINVAL;

        if (engine.Owner is not FiberTask fiberTask) return -(int)Errno.EPERM;

        var absolute = (flags & 1) != 0;
        var nowNs = GetSleepClockNs(clockId);
        var requestNs = checked(sec * 1_000_000_000L + nsec);
        var durationNs = absolute ? Math.Max(0L, requestNs - nowNs) : requestNs;
        if (absolute && durationNs == 0)
            return 0;

        var totalMs = durationNs == 0 ? 0 : (int)Math.Min(int.MaxValue, (durationNs + 999_999) / 1_000_000);
        var startNs = nowNs;

        // Linux clock_nanosleep(2): TIMER_ABSTIME sleeps use an absolute deadline and do not write rem.
        var res = await new SleepAwaitable(totalMs, fiberTask);

        if (res == AwaitResult.Interrupted)
        {
            // Linux clock_nanosleep(2): rem is only written for relative sleeps.
            if (!absolute && remPtr != 0)
            {
                var elapsedNs = Math.Max(0L, GetSleepClockNs(clockId) - startNs);
                var remainingNs = Math.Max(0L, durationNs - elapsedNs);
                if (!WriteTimespec64(engine, remPtr, remainingNs)) return -(int)Errno.EFAULT;
            }

            return -(int)Errno.ERESTARTSYS;
        }

        return 0;
    }

    private static long GetSleepClockNs(int clockId)
    {
        if (clockId == LinuxConstants.CLOCK_REALTIME)
        {
            var ticks = DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks;
            return ticks * 100;
        }

        var freq = Stopwatch.Frequency;
        var ticksNow = Stopwatch.GetTimestamp();
        var secs = ticksNow / freq;
        var rem = ticksNow % freq;
        return secs * 1_000_000_000L + rem * 1_000_000_000L / freq;
    }

    private static bool TryGetClockTime(int clockId, out long secs, out long nsecs)
    {
        switch (clockId)
        {
            case LinuxConstants.CLOCK_REALTIME:
            case LinuxConstants.CLOCK_REALTIME_COARSE:
            {
                var ticks = DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks;
                secs = ticks / TimeSpan.TicksPerSecond;
                nsecs = ticks % TimeSpan.TicksPerSecond * 100;
                return true;
            }
            case LinuxConstants.CLOCK_MONOTONIC:
            case LinuxConstants.CLOCK_MONOTONIC_RAW:
            case LinuxConstants.CLOCK_MONOTONIC_COARSE:
            case LinuxConstants.CLOCK_BOOTTIME:
            {
                // We currently have one monotonic time source and no suspend accounting,
                // so RAW/COARSE/BOOTTIME are approximated with the scheduler monotonic clock.
                var freq = Stopwatch.Frequency;
                var ticks = Stopwatch.GetTimestamp();
                secs = ticks / freq;
                nsecs = ticks % freq * 1_000_000_000L / freq;
                return true;
            }
            case LinuxConstants.CLOCK_PROCESS_CPUTIME_ID:
            case LinuxConstants.CLOCK_THREAD_CPUTIME_ID:
                // We don't maintain per-process or per-thread CPU accounting yet.
                secs = 0;
                nsecs = 0;
                return false;
            default:
                secs = 0;
                nsecs = 0;
                return false;
        }
    }

    private static bool WriteTimespec32(Engine engine, uint ptr, long totalNs)
    {
        var buf = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), (int)(totalNs / 1_000_000_000L));
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), (int)(totalNs % 1_000_000_000L));
        return engine.CopyToUser(ptr, buf);
    }

    private static bool WriteTimespec64(Engine engine, uint ptr, long totalNs)
    {
        var buf = new byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(0, 8), totalNs / 1_000_000_000L);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(8, 8), totalNs % 1_000_000_000L);
        return engine.CopyToUser(ptr, buf);
    }

    private async ValueTask<int> SysNice(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        // Simple stub: for now, don't actually change host priority
        return 0; // Success
    }

    private async ValueTask<int> SysGetPriority(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        // Simple stub: return default priority (20)
        return 20;
    }

    private async ValueTask<int> SysSetPriority(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        // Simple stub: success
        return 0;
    }

    private async ValueTask<int> SysPersonality(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        // personality(0xffffffff) returns current, otherwise sets.
        // For now, always return PER_LINUX (0)
        return (int)LinuxConstants.PER_LINUX;
    }

    private async ValueTask<int> SysGetCpu(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var cpuPtr = a1;
        var nodePtr = a2;

        if (cpuPtr != 0)
            if (!engine.CopyToUser(cpuPtr, BitConverter.GetBytes(0u)))
                return -(int)Errno.EFAULT;

        if (nodePtr != 0)
            if (!engine.CopyToUser(nodePtr, BitConverter.GetBytes(0u)))
                return -(int)Errno.EFAULT;

        return 0;
    }

    private async ValueTask<int> SysPrctl(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        if (engine.Owner is not FiberTask task) return -(int)Errno.EPERM;

        var option = (int)a1;

        switch (option)
        {
            case LinuxConstants.PR_SET_NAME:
                var name = ReadString(a2);
                if (name != null)
                {
                    task.Process.Name = name;
                    return 0;
                }

                return -(int)Errno.EFAULT;

            case LinuxConstants.PR_GET_NAME:
                var procName = task.Process.Name ?? "fiberish";
                var bytes = Encoding.ASCII.GetBytes(procName);
                var buf = new byte[16];
                Array.Copy(bytes, buf, Math.Min(bytes.Length, 15));
                if (!engine.CopyToUser(a2, buf)) return -(int)Errno.EFAULT;
                return 0;

            default:
                Logger.LogWarning($"[SysPrctl] Unhandled option: {option}");
                return 0; // Success for many non-critical options
        }
    }

    private async ValueTask<int> SysMembarrier(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        if (engine.Owner is not FiberTask task) return -(int)Errno.EPERM;

        var cmd = (int)a1;
        var flags = a2;
        if (flags != 0) return -(int)Errno.EINVAL;

        if ((cmd & ~SupportedMembarrierCommands) != 0 && cmd != LinuxConstants.MEMBARRIER_CMD_QUERY)
            return -(int)Errno.EINVAL;

        var registered = task.Process.MembarrierRegisteredCommands;
        return cmd switch
        {
            LinuxConstants.MEMBARRIER_CMD_QUERY => SupportedMembarrierCommands,
            LinuxConstants.MEMBARRIER_CMD_GLOBAL => 0,
            LinuxConstants.MEMBARRIER_CMD_REGISTER_GLOBAL_EXPEDITED => RegisterMembarrier(task.Process,
                LinuxConstants.MEMBARRIER_CMD_REGISTER_GLOBAL_EXPEDITED),
            LinuxConstants.MEMBARRIER_CMD_GLOBAL_EXPEDITED => RequireMembarrierRegistration(registered,
                LinuxConstants.MEMBARRIER_CMD_REGISTER_GLOBAL_EXPEDITED),
            LinuxConstants.MEMBARRIER_CMD_REGISTER_PRIVATE_EXPEDITED => RegisterMembarrier(task.Process,
                LinuxConstants.MEMBARRIER_CMD_REGISTER_PRIVATE_EXPEDITED),
            LinuxConstants.MEMBARRIER_CMD_PRIVATE_EXPEDITED => RequireMembarrierRegistration(registered,
                LinuxConstants.MEMBARRIER_CMD_REGISTER_PRIVATE_EXPEDITED),
            LinuxConstants.MEMBARRIER_CMD_REGISTER_PRIVATE_EXPEDITED_SYNC_CORE => RegisterMembarrier(task.Process,
                LinuxConstants.MEMBARRIER_CMD_REGISTER_PRIVATE_EXPEDITED_SYNC_CORE),
            LinuxConstants.MEMBARRIER_CMD_PRIVATE_EXPEDITED_SYNC_CORE => RequireMembarrierRegistration(registered,
                LinuxConstants.MEMBARRIER_CMD_REGISTER_PRIVATE_EXPEDITED_SYNC_CORE),
            _ => -(int)Errno.EINVAL
        };
    }

    private static int RegisterMembarrier(Process process, int registerCmd)
    {
        if ((registerCmd & MembarrierRegisterCommands) == 0) return -(int)Errno.EINVAL;
        process.MembarrierRegisteredCommands |= registerCmd;
        return 0;
    }

    private static int RequireMembarrierRegistration(int registeredMask, int requiredRegisterCmd)
    {
        return (registeredMask & requiredRegisterCmd) != 0 ? 0 : -(int)Errno.EPERM;
    }

    private async ValueTask<int> SysGetThreadArea(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var uInfoAddr = a1;
        var buf = new byte[16];
        if (!engine.CopyFromUser(uInfoAddr, buf)) return -(int)Errno.EFAULT;

        // var entry = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(0, 4));

        // Return whatever was previously set via SetThreadArea
        // We simplified set_thread_area to just set GS base, so we don't track entry indices properly.
        // Just return current GS base as base_addr.
        var baseAddr = engine.GetSegBase(Seg.GS);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), baseAddr);

        if (!engine.CopyToUser(uInfoAddr, buf)) return -(int)Errno.EFAULT;
        return 0;
    }

    private async ValueTask<int> SysCapget(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        const uint LINUX_CAPABILITY_VERSION_1 = 0x19980330;
        const uint LINUX_CAPABILITY_VERSION_2 = 0x20071026;
        const uint LINUX_CAPABILITY_VERSION_3 = 0x20080522;

        if (engine.Owner is not FiberTask task) return -(int)Errno.EPERM;
        if (a1 == 0 || a2 == 0) return -(int)Errno.EFAULT;

        var hdr = new byte[8];
        if (!engine.CopyFromUser(a1, hdr)) return -(int)Errno.EFAULT;
        var version = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(0, 4));
        var pid = BinaryPrimitives.ReadInt32LittleEndian(hdr.AsSpan(4, 4));

        var count = version switch
        {
            LINUX_CAPABILITY_VERSION_1 => 1,
            LINUX_CAPABILITY_VERSION_2 => 2,
            LINUX_CAPABILITY_VERSION_3 => 2,
            _ => 0
        };

        if (count == 0)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(0, 4), LINUX_CAPABILITY_VERSION_3);
            _ = engine.CopyToUser(a1, hdr);
            return -(int)Errno.EINVAL;
        }

        if (pid != 0 && pid != task.Process.TGID) return -(int)Errno.EPERM;

        var data = new byte[count * 12];
        for (var i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(i * 12 + 0, 4), task.Process.CapEffective[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(i * 12 + 4, 4), task.Process.CapPermitted[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(i * 12 + 8, 4), task.Process.CapInheritable[i]);
        }

        if (!engine.CopyToUser(a2, data)) return -(int)Errno.EFAULT;
        return 0;
    }

    private async ValueTask<int> SysCapset(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        const uint LINUX_CAPABILITY_VERSION_1 = 0x19980330;
        const uint LINUX_CAPABILITY_VERSION_2 = 0x20071026;
        const uint LINUX_CAPABILITY_VERSION_3 = 0x20080522;

        if (engine.Owner is not FiberTask task) return -(int)Errno.EPERM;
        if (a1 == 0 || a2 == 0) return -(int)Errno.EFAULT;

        var hdr = new byte[8];
        if (!engine.CopyFromUser(a1, hdr)) return -(int)Errno.EFAULT;
        var version = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(0, 4));
        var pid = BinaryPrimitives.ReadInt32LittleEndian(hdr.AsSpan(4, 4));

        var count = version switch
        {
            LINUX_CAPABILITY_VERSION_1 => 1,
            LINUX_CAPABILITY_VERSION_2 => 2,
            LINUX_CAPABILITY_VERSION_3 => 2,
            _ => 0
        };
        if (count == 0) return -(int)Errno.EINVAL;
        if (pid != 0 && pid != task.Process.TGID) return -(int)Errno.EPERM;

        var data = new byte[count * 12];
        if (!engine.CopyFromUser(a2, data)) return -(int)Errno.EFAULT;

        for (var i = 0; i < count; i++)
        {
            task.Process.CapEffective[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i * 12 + 0, 4));
            task.Process.CapPermitted[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i * 12 + 4, 4));
            task.Process.CapInheritable[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i * 12 + 8, 4));
        }

        // Zero out upper slot when V1 payload was used.
        if (count == 1)
        {
            task.Process.CapEffective[1] = 0;
            task.Process.CapPermitted[1] = 0;
            task.Process.CapInheritable[1] = 0;
        }

        return 0;
    }

    private async ValueTask<int> SysGetRandom(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var bufAddr = a1;
        var count = a2;
        var flags = a3;

        if (count == 0) return 0;

        // Flags: 0x01 (GRND_NONBLOCK), 0x02 (GRND_RANDOM), 0x04 (GRND_INSECURE)
        // We act as if we are urandom/random always ready (except strict GRND_RANDOM might block, but we simulate non-blocking behavior for now).

        var buffer = new byte[count];
        RandomNumberGenerator.Fill(buffer);

        if (!engine.CopyToUser(bufAddr, buffer)) return -(int)Errno.EFAULT;

        return (int)count;
    }
}