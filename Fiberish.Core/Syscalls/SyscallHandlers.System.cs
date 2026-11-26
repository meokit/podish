using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
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
    private const long NanosecondsPerSecond = 1_000_000_000L;
    internal const int UserHz = 100; // Linux i386 userspace clock ticks per second for times().
    private const int SysInfo32Size = 64;
    private const int RusageSelf = 0;
    private const int RusageChildren = -1;
    private const int RusageThread = 1;

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

    private static readonly long SysinfoUptimeStartTimestamp = Stopwatch.GetTimestamp();
    private static readonly long TimesStartTimestamp = Stopwatch.GetTimestamp();

    private static int GetSysinfoUptimeSeconds()
    {
        var elapsed = Stopwatch.GetTimestamp() - SysinfoUptimeStartTimestamp;
        if (elapsed <= 0) return 0;
        var seconds = elapsed / Stopwatch.Frequency;
        if (seconds > int.MaxValue) return int.MaxValue;
        return (int)seconds;
    }

    private static int GetElapsedTimesClockTicks()
    {
        var elapsed = Stopwatch.GetTimestamp() - TimesStartTimestamp;
        return ClampClockTicksToInt(NanosecondsToUserClockTicks(FiberTask.ConvertStopwatchTicksToNanoseconds(elapsed)));
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
        var task = engine.Owner as FiberTask;
        var self = task?.Process.GetSelfCpuTimeSnapshot() ?? default;
        var children = task?.Process.GetChildrenCpuTimeSnapshot() ?? default;
        var selfUserTicks = ClampClockTicksToInt(NanosecondsToUserClockTicks(self.UserNs));
        var selfSystemTicks = ClampClockTicksToInt(NanosecondsToUserClockTicks(self.SystemNs));
        var childrenUserTicks = ClampClockTicksToInt(NanosecondsToUserClockTicks(children.UserNs));
        var childrenSystemTicks = ClampClockTicksToInt(NanosecondsToUserClockTicks(children.SystemNs));
        var ticks = GetElapsedTimesClockTicks();

        if (a1 != 0)
        {
            var wordSize = GuestWordSize(engine);
            Span<byte> tms = stackalloc byte[wordSize * 4];
            WriteGuestWord(tms, wordSize, (ulong)selfUserTicks);
            WriteGuestWord(tms[wordSize..], wordSize, (ulong)selfSystemTicks);
            WriteGuestWord(tms[(wordSize * 2)..], wordSize, (ulong)childrenUserTicks);
            WriteGuestWord(tms[(wordSize * 3)..], wordSize, (ulong)childrenSystemTicks);
            if (!engine.CopyToUser(a1, tms)) return -(int)Errno.EFAULT;
        }

        return ticks;
    }

    private async ValueTask<int> SysGetRusage(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var who = unchecked((int)a1);
        var usagePtr = a2;

        CpuTimeSnapshot snapshot;

        switch (who)
        {
            case RusageSelf:
                if (engine.Owner is not FiberTask selfTask)
                    return -(int)Errno.EPERM;
                snapshot = selfTask.Process.GetSelfCpuTimeSnapshot();
                break;
            case RusageThread:
                if (engine.Owner is not FiberTask threadTask)
                    return -(int)Errno.EPERM;
                snapshot = threadTask.SnapshotThreadCpuTime();
                break;
            case RusageChildren:
                if (engine.Owner is not FiberTask childTask)
                    return -(int)Errno.EPERM;
                snapshot = childTask.Process.GetChildrenCpuTimeSnapshot();
                break;
            default:
                return -(int)Errno.EINVAL;
        }

        if (!WriteRusage(engine, usagePtr, snapshot))
            return -(int)Errno.EFAULT;

        return 0;
    }

    private async ValueTask<int> SysUname(Engine engine, ulong a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var t = engine.Owner as FiberTask;
        if (t == null) return -(int)Errno.EPERM;

        var uts = t.Process.UTS;

        var result = new byte[65 * 6];
        void WriteUnameString(int offset, string s)
        {
            var bytes = Encoding.ASCII.GetBytes(s);
            Array.Copy(bytes, 0, result, offset, Math.Min(bytes.Length, 64));
        }

        WriteUnameString(0, uts.SysName);
        WriteUnameString(65, uts.NodeName);
        WriteUnameString(130, uts.Release);
        WriteUnameString(195, uts.Version);
        WriteUnameString(260, engine.IsAArch64 ? "aarch64" : uts.Machine);
        WriteUnameString(325, uts.DomainName);

        return engine.CopyToUser(a1, result) ? 0 : -(int)Errno.EFAULT;
    }

    private async ValueTask<int> SysSysinfo(Engine engine, ulong sysinfoAddr, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var t = engine.Owner as FiberTask;
        if (t == null) return -(int)Errno.EPERM;

        var trackedPages = MemoryContext.AddressSpacePolicy.GetTrackedPageCounts();
        var totalCachedBytes = Math.Max(0, trackedPages.TotalPages) * LinuxConstants.PageSize;
        var shmemBytes = Math.Max(0, trackedPages.ShmemPages) * LinuxConstants.PageSize;
        var sharedBytes = Math.Max(0, shmemBytes + SysVShm.GetResidentBytesSnapshot());
        var allocatedBytes = Math.Max(0, MemoryContext.GetAllocatedBytes() + totalCachedBytes);
        var quotaBytes = MemoryContext.MemoryQuotaBytes;
        var totalBytes = Math.Max(0, quotaBytes > 0 ? quotaBytes : Math.Max(allocatedBytes, 256L * 1024 * 1024));
        var freeBytes = Math.Max(0, totalBytes - allocatedBytes);
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
        var processCount = scheduler?.ProcessCount ?? 1;
        if (processCount <= 0) processCount = 1;
        if (processCount > short.MaxValue) processCount = short.MaxValue;

        if (engine.IsAArch64)
        {
            Span<byte> buf = stackalloc byte[112];
            buf.Clear();
            BinaryPrimitives.WriteInt64LittleEndian(buf, GetSysinfoUptimeSeconds());
            BinaryPrimitives.WriteUInt64LittleEndian(buf[8..], 65536);
            BinaryPrimitives.WriteUInt64LittleEndian(buf[16..], 65536);
            BinaryPrimitives.WriteUInt64LittleEndian(buf[24..], 65536);
            BinaryPrimitives.WriteUInt64LittleEndian(buf[32..], (ulong)Math.Max(0, totalBytes));
            BinaryPrimitives.WriteUInt64LittleEndian(buf[40..], (ulong)Math.Max(0, freeBytes));
            BinaryPrimitives.WriteUInt64LittleEndian(buf[48..], (ulong)Math.Max(0, sharedBytes));
            BinaryPrimitives.WriteUInt64LittleEndian(buf[56..], (ulong)Math.Max(0, bufferBytes));
            BinaryPrimitives.WriteUInt64LittleEndian(buf[64..], (ulong)Math.Max(0, totalSwapBytes));
            BinaryPrimitives.WriteUInt64LittleEndian(buf[72..], (ulong)Math.Max(0, freeSwapBytes));
            BinaryPrimitives.WriteUInt16LittleEndian(buf[80..], (ushort)processCount);
            BinaryPrimitives.WriteUInt64LittleEndian(buf[88..], (ulong)Math.Max(0, totalHighBytes));
            BinaryPrimitives.WriteUInt64LittleEndian(buf[96..], (ulong)Math.Max(0, freeHighBytes));
            BinaryPrimitives.WriteUInt32LittleEndian(buf[104..], 1); // Values are in bytes.
            return engine.CopyToUser(sysinfoAddr, buf) ? 0 : -(int)Errno.EFAULT;
        }

        if (sysinfoAddr != 0)
        {
            Span<byte> buffer = stackalloc byte[SysInfo32Size];
            buffer.Clear();
            BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(0, 4), GetSysinfoUptimeSeconds());
            BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(4, 4), 65536);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(8, 4), 65536);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(12, 4), 65536);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(16, 4), ToSysValue(totalBytes));
            BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(20, 4), ToSysValue(freeBytes));
            BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(24, 4), ToSysValue(sharedBytes));
            BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(28, 4), ToSysValue(bufferBytes));
            BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(32, 4), ToSysValue(totalSwapBytes));
            BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(36, 4), ToSysValue(freeSwapBytes));
            BinaryPrimitives.WriteInt16LittleEndian(buffer.Slice(40, 2), (short)processCount);
            BinaryPrimitives.WriteInt16LittleEndian(buffer.Slice(42, 2), 0);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(44, 4), ToSysValue(totalHighBytes));
            BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(48, 4), ToSysValue(freeHighBytes));
            BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(52, 4), memUnit);
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

    private async ValueTask<int> SysSchedGetParam(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var task = engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        var pid = (int)a1;
        var paramPtr = a2;
        if (paramPtr == 0) return -(int)Errno.EFAULT;

        if (pid != 0)
        {
            var targetTask = task.CommonKernel.GetTask(pid);
            if (targetTask == null) return -(int)Errno.ESRCH;
        }

        // Linux i386 struct sched_param currently only contains one int sched_priority.
        Span<byte> param = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(param, 0);
        if (!engine.CopyToUser(paramPtr, param)) return -(int)Errno.EFAULT;

        return 0;
    }

    private async ValueTask<int> SysSchedGetScheduler(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var task = engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        var pid = (int)a1;
        if (pid != 0)
        {
            var targetTask = task.CommonKernel.GetTask(pid);
            if (targetTask == null) return -(int)Errno.ESRCH;
        }

        return LinuxConstants.SCHED_OTHER;
    }

    private async ValueTask<int> SysSchedGetAffinity(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var affinityMaskBytes = GuestWordSize(engine);
        var cpusetsize = a2;
        var maskPtr = a3;

        if (cpusetsize < affinityMaskBytes || (engine.IsAArch64 && cpusetsize % affinityMaskBytes != 0))
            return -(int)Errno.EINVAL;

        var mask = new byte[engine.IsAArch64 ? affinityMaskBytes : cpusetsize];
        mask[0] = 0x01; // Report CPU0 as available.
        if (!engine.CopyToUser(maskPtr, mask))
            return -(int)Errno.EFAULT;

        return affinityMaskBytes;
    }

    private async ValueTask<int> SysGetPgid(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        if (engine.Owner is not FiberTask task) return -(int)Errno.EPERM;

        var targetPid = (int)a1;
        if (targetPid < 0) return -(int)Errno.ESRCH;
        if (targetPid == 0) return task.Process.PGID;

        var targetProc = task.CommonKernel.GetProcess(targetPid);
        if (targetProc == null) return -(int)Errno.ESRCH;
        return targetProc.PGID;
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

    private async ValueTask<int> SysGetTimeOfDay(Engine engine, ulong a1, ulong a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var tvPtr = a1;

        // The obsolete timezone output still needs to be written/validated.
        if (a2 != 0 && !engine.CopyToUser(a2, new byte[8])) return -(int)Errno.EFAULT;

        // Use UtcNow for REALTIME (gettimeofday is strictly REALTIME)
        var ticks = DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks;
        var secs = ticks / TimeSpan.TicksPerSecond;
        var usecs = ticks % TimeSpan.TicksPerSecond / 10; // 100ns -> 1us

        if (tvPtr != 0)
        {
            var wordSize = GuestWordSize(engine);
            Span<byte> buf = stackalloc byte[wordSize * 2];
            WriteGuestWord(buf, wordSize, (ulong)secs);
            WriteGuestWord(buf[wordSize..], wordSize, (ulong)usecs);
            if (!engine.CopyToUser(tvPtr, buf)) return -(int)Errno.EFAULT;
        }

        return 0;
    }

    private async ValueTask<int> SysClockGetTime(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var clockId = (int)a1;
        var tsPtr = a2;

        if (!TryGetClockTime(engine, clockId, out var secs, out var nsecs))
            return -(int)Errno.EINVAL;

        var buf = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), (int)secs);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), (int)nsecs);
        if (!engine.CopyToUser(tsPtr, buf)) return -(int)Errno.EFAULT;

        return 0;
    }

    private async ValueTask<int> SysClockGetRes(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var clockId = (int)a1;
        var resPtr = a2;

        if (!TryGetClockResolution(clockId, out var resolutionNs))
            return -(int)Errno.EINVAL;

        if (resPtr != 0 && !WriteTimespec32(engine, resPtr, resolutionNs))
            return -(int)Errno.EFAULT;

        return 0;
    }

    private async ValueTask<int> SysClockGetTime64(Engine engine, uint a1, ulong a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var clockId = (int)a1;
        var tsPtr = a2;

        if (!TryGetClockTime(engine, clockId, out var secs, out var nsecs))
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
        var wordSize = GuestWordSize(engine);
        Span<byte> reqBuf = stackalloc byte[wordSize * 2];
        if (!engine.CopyFromUser(a1, reqBuf)) return -(int)Errno.EFAULT;
        var sec = wordSize == 8 ? (long)ReadGuestWord(reqBuf, wordSize) : (int)ReadGuestWord(reqBuf, wordSize);
        var nsec = wordSize == 8
            ? (long)ReadGuestWord(reqBuf[wordSize..], wordSize)
            : (int)ReadGuestWord(reqBuf[wordSize..], wordSize);
        if (sec < 0 || nsec < 0 || nsec >= 1_000_000_000) return -(int)Errno.EINVAL;

        if (engine.Owner is FiberTask ownerTask)
            Logger.LogInformation("[SysNanosleep] pid={Pid} tid={Tid} req={{sec={Sec}, nsec={Nsec}}}",
                ownerTask.Process.TGID, ownerTask.TID, sec, nsec);

        if (engine.Owner is not FiberTask fiberTask) return -(int)Errno.EPERM;
        if (!TryConvertTimespecToTimeoutMs(sec, nsec, out var totalMs, out var timeoutErr))
            return timeoutErr;

        var startNs = GetSleepClockNs(LinuxConstants.CLOCK_MONOTONIC);
        var requestedNs = sec > (long.MaxValue - nsec) / NanosecondsPerSecond
            ? long.MaxValue
            : sec * NanosecondsPerSecond + nsec;

        // Linux nanosleep(2): invalid tv_nsec is EINVAL; interrupted relative sleeps return rem.
        var res = await new SleepAwaitable(totalMs, fiberTask);

        if (res == AwaitResult.Interrupted)
        {
            // Linux nanosleep(2): interrupted relative sleeps report the unslept interval in rem.
            if (a2 != 0)
            {
                var elapsedNs = Math.Max(0L, GetSleepClockNs(LinuxConstants.CLOCK_MONOTONIC) - startNs);
                var remainingNs = Math.Max(0L, requestedNs - elapsedNs);
                var copied = engine.IsAArch64
                    ? WriteTimespec64(engine, a2, remainingNs)
                    : WriteTimespec32(engine, a2, remainingNs);
                if (!copied) return -(int)Errno.EFAULT;
            }

            return -(int)Errno.ERESTARTSYS;
        }

        return 0;
    }

    private ValueTask<int> SysClockNanosleep(Engine engine, uint a1, uint a2, uint a3, uint a4,
        uint a5, uint a6)
    {
        return ClockNanosleep(engine, a1, a2, a3, a4, false);
    }

    private ValueTask<int> SysClockNanosleepTime64(Engine engine, uint a1, uint a2, uint a3, uint a4,
        uint a5, uint a6)
    {
        return ClockNanosleep(engine, a1, a2, a3, a4, true);
    }

    private async ValueTask<int> ClockNanosleep(Engine engine, uint a1, uint a2, uint a3, uint a4, bool time64)
    {
        var clockId = (int)a1;
        var flags = (int)a2;
        var reqPtr = a3;
        var remPtr = a4;

        if ((flags & ~1) != 0) return -(int)Errno.EINVAL;
        switch (clockId)
        {
            case LinuxConstants.CLOCK_REALTIME:
            case LinuxConstants.CLOCK_MONOTONIC:
            case LinuxConstants.CLOCK_BOOTTIME:
            case LinuxConstants.CLOCK_TAI:
                break;
            case LinuxConstants.CLOCK_REALTIME_ALARM:
            case LinuxConstants.CLOCK_BOOTTIME_ALARM:
                // Valid alarm clocks, but sleeping on them requires CAP_WAKE_ALARM.
                return -(int)Errno.EPERM;
            case LinuxConstants.CLOCK_PROCESS_CPUTIME_ID:
            case LinuxConstants.CLOCK_THREAD_CPUTIME_ID:
            case LinuxConstants.CLOCK_MONOTONIC_RAW:
            case LinuxConstants.CLOCK_REALTIME_COARSE:
            case LinuxConstants.CLOCK_MONOTONIC_COARSE:
                // Valid clocks that cannot be slept on.
                return -(int)Errno.EOPNOTSUPP;
            default:
                return -(int)Errno.EINVAL;
        }

        var reqBuf = new byte[time64 ? 16 : 8];
        if (!engine.CopyFromUser(reqPtr, reqBuf)) return -(int)Errno.EFAULT;
        long sec = time64 ? BinaryPrimitives.ReadInt64LittleEndian(reqBuf.AsSpan(0, 8))
            : BinaryPrimitives.ReadInt32LittleEndian(reqBuf.AsSpan(0, 4));
        long nsec = time64 ? BinaryPrimitives.ReadInt64LittleEndian(reqBuf.AsSpan(8, 8))
            : BinaryPrimitives.ReadInt32LittleEndian(reqBuf.AsSpan(4, 4));
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
                if (!(time64 ? WriteTimespec64(engine, remPtr, remainingNs) : WriteTimespec32(engine, remPtr, remainingNs)))
                    return -(int)Errno.EFAULT;
            }

            return -(int)Errno.EINTR;
        }

        return 0;
    }

    private static long GetSleepClockNs(int clockId)
    {
        if (clockId is LinuxConstants.CLOCK_REALTIME or LinuxConstants.CLOCK_TAI)
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

    private static bool TryGetClockTime(Engine engine, int clockId, out long secs, out long nsecs)
    {
        switch (clockId)
        {
            case LinuxConstants.CLOCK_REALTIME:
            case LinuxConstants.CLOCK_REALTIME_COARSE:
            case LinuxConstants.CLOCK_TAI:
            {
                // The emulator has no leap-second table, so TAI == REALTIME.
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
                if (engine.Owner is not FiberTask processTask)
                {
                    secs = 0;
                    nsecs = 0;
                    return false;
                }

                return TrySplitCpuTime(processTask.Process.GetSelfCpuTimeSnapshot(), out secs, out nsecs);
            case LinuxConstants.CLOCK_THREAD_CPUTIME_ID:
                if (engine.Owner is not FiberTask threadTask)
                {
                    secs = 0;
                    nsecs = 0;
                    return false;
                }

                return TrySplitCpuTime(threadTask.SnapshotThreadCpuTime(), out secs, out nsecs);
            default:
                secs = 0;
                nsecs = 0;
                return false;
        }
    }

    private static bool TryGetClockResolution(int clockId, out long resolutionNs)
    {
        switch (clockId)
        {
            case LinuxConstants.CLOCK_REALTIME:
            case LinuxConstants.CLOCK_REALTIME_COARSE:
            case LinuxConstants.CLOCK_MONOTONIC:
            case LinuxConstants.CLOCK_MONOTONIC_RAW:
            case LinuxConstants.CLOCK_MONOTONIC_COARSE:
            case LinuxConstants.CLOCK_BOOTTIME:
            // Reading alarm-clock resolution needs no CAP_WAKE_ALARM. This does
            // not enable creating alarm timers or waking a suspended host.
            case LinuxConstants.CLOCK_REALTIME_ALARM:
            case LinuxConstants.CLOCK_BOOTTIME_ALARM:
            case LinuxConstants.CLOCK_TAI:
                resolutionNs = 1_000_000;
                return true;
            case LinuxConstants.CLOCK_PROCESS_CPUTIME_ID:
            case LinuxConstants.CLOCK_THREAD_CPUTIME_ID:
                resolutionNs = GetCpuClockResolutionNs();
                return true;
            default:
                resolutionNs = 0;
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

    private static bool WriteRusage(Engine engine, uint ptr, CpuTimeSnapshot snapshot, int maxRssKiB = 0)
    {
        if (ptr == 0) return false;

        var wordSize = GuestWordSize(engine);
        Span<byte> buf = stackalloc byte[18 * wordSize];
        buf.Clear();

        var userUsec = snapshot.UserNs / 1_000L;
        var systemUsec = snapshot.SystemNs / 1_000L;
        WriteGuestWord(buf, wordSize, (ulong)(userUsec / 1_000_000L));
        WriteGuestWord(buf[wordSize..], wordSize, (ulong)(userUsec % 1_000_000L));
        WriteGuestWord(buf[(wordSize * 2)..], wordSize, (ulong)(systemUsec / 1_000_000L));
        WriteGuestWord(buf[(wordSize * 3)..], wordSize, (ulong)(systemUsec % 1_000_000L));
        WriteGuestWord(buf[(wordSize * 4)..], wordSize, (ulong)maxRssKiB);

        return engine.CopyToUser(ptr, buf);
    }

    private static bool WriteTimespec64(Engine engine, uint ptr, long totalNs)
    {
        var buf = new byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(0, 8), totalNs / NanosecondsPerSecond);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(8, 8), totalNs % NanosecondsPerSecond);
        return engine.CopyToUser(ptr, buf);
    }

    internal static long NanosecondsToUserClockTicks(long totalNs)
    {
        if (totalNs <= 0)
            return 0;

        var wholeSeconds = totalNs / NanosecondsPerSecond;
        var remainingNs = totalNs % NanosecondsPerSecond;
        return wholeSeconds * UserHz + remainingNs * UserHz / NanosecondsPerSecond;
    }

    private static int ClampClockTicksToInt(long clockTicks)
    {
        if (clockTicks <= int.MinValue)
            return int.MinValue;
        if (clockTicks >= int.MaxValue)
            return int.MaxValue;
        return (int)clockTicks;
    }

    private static bool TrySplitCpuTime(CpuTimeSnapshot snapshot, out long secs, out long nsecs)
    {
        var totalNs = snapshot.UserNs + snapshot.SystemNs;
        if (totalNs < 0)
        {
            secs = 0;
            nsecs = 0;
            return false;
        }

        secs = totalNs / NanosecondsPerSecond;
        nsecs = totalNs % NanosecondsPerSecond;
        return true;
    }

    private static long GetCpuClockResolutionNs()
    {
        var ticksPerSecond = Stopwatch.Frequency;
        if (ticksPerSecond <= 0)
            return 1;

        return Math.Max(1L, (NanosecondsPerSecond + ticksPerSecond - 1) / ticksPerSecond);
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
            case LinuxConstants.PR_SET_PDEATHSIG:
            {
                var sig = (int)a2;
                if (sig < 0 || sig > 64)
                    return -(int)Errno.EINVAL;
                task.Process.ParentDeathSignal = sig;
                return 0;
            }

            case LinuxConstants.PR_GET_PDEATHSIG:
            {
                if (a2 == 0) return -(int)Errno.EFAULT;
                Span<byte> pdeathBuf = stackalloc byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(pdeathBuf, task.Process.ParentDeathSignal);
                return engine.CopyToUser(a2, pdeathBuf) ? 0 : -(int)Errno.EFAULT;
            }

            case LinuxConstants.PR_GET_DUMPABLE:
                return task.Process.Dumpable;

            case LinuxConstants.PR_SET_DUMPABLE:
            {
                var dumpable = (int)a2;
                if (dumpable is not (0 or 1))
                    return -(int)Errno.EINVAL;
                task.Process.Dumpable = dumpable;
                return 0;
            }

            case LinuxConstants.PR_GET_TIMING:
                return 0; // PR_TIMING_STATISTICAL

            case LinuxConstants.PR_SET_TIMING:
                return a2 == 0 ? 0 : -(int)Errno.EINVAL;

            case LinuxConstants.PR_SET_NAME:
            {
                if (a2 == 0) return -(int)Errno.EFAULT;
                var rawName = new byte[16];
                if (!ReadNullTerminatedUserBytes(a2, rawName, out var len, out _))
                    return -(int)Errno.EFAULT;
                var nameLen = Math.Min(len, 15);
                var procName = Encoding.UTF8.GetString(rawName, 0, nameLen);
                task.Process.Name = string.IsNullOrEmpty(procName) ? "process" : procName;
                return 0;
            }

            case LinuxConstants.PR_GET_NAME:
            {
                if (a2 == 0) return -(int)Errno.EFAULT;
                var procName = task.Process.Name ?? task.Process.Comm ?? "fiberish";
                var bytes = Encoding.ASCII.GetBytes(procName);
                var buf = new byte[16];
                Array.Copy(bytes, buf, Math.Min(bytes.Length, 15));
                if (!engine.CopyToUser(a2, buf)) return -(int)Errno.EFAULT;
                return 0;
            }

            case LinuxConstants.PR_GET_TSC:
            {
                if (a2 == 0) return -(int)Errno.EFAULT;
                Span<byte> tscBuf = stackalloc byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(tscBuf, task.Process.TscMode);
                return engine.CopyToUser(a2, tscBuf) ? 0 : -(int)Errno.EFAULT;
            }

            case LinuxConstants.PR_SET_TSC:
            {
                var tscMode = (int)a2;
                if (tscMode is not (LinuxConstants.PR_TSC_ENABLE or LinuxConstants.PR_TSC_SIGSEGV))
                    return -(int)Errno.EINVAL;
                task.Process.TscMode = tscMode;
                return 0;
            }

            case LinuxConstants.PR_SET_TIMERSLACK:
            {
                long slack = a2 == 0 ? task.Process.DefaultTimerSlackNs : (long)a2;
                task.Process.TimerSlackNs = slack;
                return 0;
            }

            case LinuxConstants.PR_GET_TIMERSLACK:
                return task.Process.TimerSlackNs > int.MaxValue ? int.MaxValue : (int)task.Process.TimerSlackNs;

            case LinuxConstants.PR_SET_CHILD_SUBREAPER:
                task.Process.IsChildSubreaper = a2 != 0;
                return 0;

            case LinuxConstants.PR_GET_CHILD_SUBREAPER:
            {
                if (a2 == 0) return -(int)Errno.EFAULT;
                Span<byte> subBuf = stackalloc byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(subBuf, task.Process.IsChildSubreaper ? 1 : 0);
                return engine.CopyToUser(a2, subBuf) ? 0 : -(int)Errno.EFAULT;
            }

            case LinuxConstants.PR_SET_NO_NEW_PRIVS:
                if (a2 != 1 || a3 != 0 || a4 != 0 || a5 != 0)
                    return -(int)Errno.EINVAL;
                task.Process.NoNewPrivs = true;
                return 0;

            case LinuxConstants.PR_GET_NO_NEW_PRIVS:
                if (a2 != 0 || a3 != 0 || a4 != 0 || a5 != 0)
                    return -(int)Errno.EINVAL;
                return task.Process.NoNewPrivs ? 1 : 0;

            case LinuxConstants.PR_SET_THP_DISABLE:
                if (a3 != 0 || a4 != 0 || a5 != 0)
                    return -(int)Errno.EINVAL;
                task.Process.ThpDisabled = a2 != 0;
                return 0;

            case LinuxConstants.PR_GET_THP_DISABLE:
                if (a2 != 0 || a3 != 0 || a4 != 0 || a5 != 0)
                    return -(int)Errno.EINVAL;
                return task.Process.ThpDisabled ? 1 : 0;

            case LinuxConstants.PR_GET_SECCOMP:
            case LinuxConstants.PR_SET_SECCOMP:
            case LinuxConstants.PR_GET_SPECULATION_CTRL:
                return -(int)Errno.EINVAL;

            case LinuxConstants.PR_SET_SECUREBITS:
                if (a3 != 0 || a4 != 0 || a5 != 0)
                    return -(int)Errno.EINVAL;
                return task.Process.HasEffectiveCapability(Process.CapabilitySetPcap)
                    ? -(int)Errno.EINVAL
                    : -(int)Errno.EPERM;

            case LinuxConstants.PR_CAPBSET_READ:
                if (a2 > Process.CapabilityLast)
                    return -(int)Errno.EINVAL;
                return task.Process.HasBoundingCapability((int)a2) ? 1 : 0;

            case LinuxConstants.PR_CAPBSET_DROP:
                if (a3 != 0 || a4 != 0 || a5 != 0)
                    return -(int)Errno.EINVAL;
                if (!task.Process.HasEffectiveCapability(Process.CapabilitySetPcap))
                    return -(int)Errno.EPERM;
                if (a2 > Process.CapabilityLast)
                    return -(int)Errno.EINVAL;
                task.Process.DropBoundingCapability((int)a2);
                return 0;

            default:
                LogUnhandledPrctlOption(option);
                return -(int)Errno.EINVAL;
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
            // Registration controls which processes participate, not whether
            // this caller may issue the global barrier. Guest execution is serialized.
            LinuxConstants.MEMBARRIER_CMD_GLOBAL_EXPEDITED => 0,
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

        // Validate every word before changing any set. Dropping the bounding set must
        // not be bypassable by capset restoring permitted bits or adding inheritable bits.
        var process = task.Process;
        var canSetPcap = process.HasEffectiveCapability(Process.CapabilitySetPcap);
        for (var i = 0; i < count; i++)
        {
            var effective = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i * 12, 4));
            var permitted = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i * 12 + 4, 4));
            var inheritable = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i * 12 + 8, 4));
            if ((effective & ~permitted) != 0 || (permitted & ~process.CapPermitted[i]) != 0 ||
                (inheritable & ~(process.CapInheritable[i] | process.CapBounding[i])) != 0 ||
                (!canSetPcap && (inheritable & ~(process.CapInheritable[i] | process.CapPermitted[i])) != 0))
                return -(int)Errno.EPERM;
        }

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

    private async ValueTask<int> SysGetRandom(Engine engine, ulong a1, uint a2, uint a3, uint a4, uint a5, uint a6)
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
