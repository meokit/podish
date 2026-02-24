using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Fiberish.Auth.Cred;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.X86.Native;
using Microsoft.Extensions.Logging;
using Process = System.Diagnostics.Process;
using Timer = Fiberish.Core.Timer;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
    private const long VirtualCpuHz = 1_000_000_000L; // Assume a fixed 1 GHz virtual CPU.
    private const int UserHz = 100; // Linux i386 userspace clock ticks per second for times().
    private static readonly long VirtualCpuStartTimestamp = Stopwatch.GetTimestamp();

    private static long GetVirtualCpuCycles()
    {
        var elapsed = Stopwatch.GetTimestamp() - VirtualCpuStartTimestamp;
        if (elapsed <= 0) return 0;
        return elapsed * VirtualCpuHz / Stopwatch.Frequency;
    }

    private static int GetTimesClockTicks()
    {
        var cycles = GetVirtualCpuCycles();
        var ticks = cycles / (VirtualCpuHz / UserHz);
        return unchecked((int)ticks);
    }

#pragma warning disable CS1998 // Async method lacks await operators - syscall handlers require async signature
    private static async ValueTask<int> SysTime(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var t = DateTimeOffset.Now.ToUnixTimeSeconds();
        if (a1 != 0)
            if (!sm.Engine.CopyToUser(a1, BitConverter.GetBytes((uint)t)))
                return -(int)Errno.EFAULT;
        return (int)t;
    }

    private static async ValueTask<int> SysTimes(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

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
            if (!sm.Engine.CopyToUser(a1, tms)) return -(int)Errno.EFAULT;
        }

        return ticks;
    }

    private static async ValueTask<int> SysUname(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var t = sm.Engine.Owner as FiberTask;
        if (t == null) return -(int)Errno.EPERM;

        var uts = t.Process.UTS;

        void WriteUnameString(uint addr, string s)
        {
            var buf = new byte[65];
            var bytes = Encoding.ASCII.GetBytes(s);
            Array.Copy(bytes, buf, Math.Min(bytes.Length, 64));
            if (!sm.Engine.CopyToUser(addr, buf)) return;
        }

        WriteUnameString(a1, uts.SysName);
        WriteUnameString(a1 + 65, uts.NodeName);
        WriteUnameString(a1 + 130, uts.Release);
        WriteUnameString(a1 + 195, uts.Version);
        WriteUnameString(a1 + 260, uts.Machine);
        WriteUnameString(a1 + 325, uts.DomainName);

        return 0;
    }

    private static async ValueTask<int> SysSysinfo(IntPtr state, uint sysinfoAddr, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var t = sm.Engine.Owner as FiberTask;
        if (t == null) return -(int)Errno.EPERM;

        var info = new SysInfo
        {
            Uptime = (int)(DateTime.UtcNow - Process.GetCurrentProcess().StartTime).TotalSeconds,
            Loads = [65536, 65536, 65536],
            TotalRam = 256 * 1024 * 1024,
            FreeRam = 128 * 1024 * 1024,
            SharedRam = 0,
            BufferRam = 0,
            TotalSwap = 0,
            FreeSwap = 0,
            Procs = 1, // Simplified for now: current processes count is not easily accessible via public API without listing.
            TotalHigh = 0,
            FreeHigh = 0,
            MemUnit = 1,
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

            if (!sm.Engine.CopyToUser(sysinfoAddr, buffer)) return -(int)Errno.EFAULT;
        }

        return 0;
    }

    private static async ValueTask<int> SysGetPid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var task = sm?.Engine.Owner as FiberTask;
        return task?.Process.TGID ?? 1000;
    }

    private static async ValueTask<int> SysGetPPid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var task = sm.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        return task.Process.PPID;
    }

    private static async ValueTask<int> SysGettid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var task = sm?.Engine.Owner as FiberTask;
        return task?.TID ?? -1;
    }

    private static async ValueTask<int> SysGetPgid(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var task = sm?.Engine.Owner as FiberTask;
        return task?.Process.PGID ?? -1;
    }

    private static async ValueTask<int> SysUmask(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var task = sm?.Engine.Owner as FiberTask;
        if (task == null) return 0;
        return CredentialService.SetUmask(task.Process, (int)a1);
    }

    private static async ValueTask<int> SysSethostname(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        var task = sm.Engine.Owner as FiberTask;
        if (task == null || task.Process.EUID != 0) return -(int)Errno.EPERM;

        var name = sm.ReadString(a1);
        task.Process.UTS.NodeName = name;
        return 0;
    }

    private static async ValueTask<int> SysSetdomainname(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -1;
        var task = sm.Engine.Owner as FiberTask;
        if (task == null || task.Process.EUID != 0) return -(int)Errno.EPERM;

        var name = sm.ReadString(a1);
        task.Process.UTS.DomainName = name;
        return 0;
    }

    private static async ValueTask<int> SysSchedYield(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var sm = Get(state);
        sm?.Engine.Yield();
        return 0;
    }

    private static async ValueTask<int> SysPause(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var task = sm.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        Logger.LogInformation("[SysPause] Task pausing, waiting for signal");

        if (await new PauseAwaiter(task) == AwaitResult.Interrupted)
            return -(int)Errno.ERESTARTSYS;
        return -(int)Errno.EINTR;
    }

    private sealed class PauseAwaiter : INotifyCompletion
    {
        private readonly FiberTask _task;

        public PauseAwaiter(FiberTask task)
        {
            _task = task;
        }

        public bool IsCompleted => false;

        public void OnCompleted(Action continuation)
        {
            if (_task.HasUnblockedPendingSignal())
            {
                _task.WakeReason = WakeReason.Signal;
                KernelScheduler.Current?.Schedule(continuation, _task);
                return;
            }

            _task.Continuation = continuation;
        }

        public AwaitResult GetResult()
        {
            if (_task.WakeReason != WakeReason.None)
            {
                _task.WakeReason = WakeReason.None;
                return AwaitResult.Interrupted;
            }
            return AwaitResult.Completed;
        }

        public PauseAwaiter GetAwaiter() => this;
    }

    private static async ValueTask<int> SysGetTimeOfDay(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

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
            if (!sm.Engine.CopyToUser(tvPtr, buf)) return -(int)Errno.EFAULT;
        }

        return 0;
    }

    private static async ValueTask<int> SysClockGetTime(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var clockId = (int)a1;
        var tsPtr = a2;

        long secs;
        long nsecs;

        if (clockId == LinuxConstants.CLOCK_REALTIME)
        {
            var ticks = DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks;
            secs = ticks / TimeSpan.TicksPerSecond;
            nsecs = ticks % TimeSpan.TicksPerSecond * 100;
        }
        else
        {
            // CLOCK_MONOTONIC and others
            // Use Stopwatch for high precision
            var freq = Stopwatch.Frequency;
            var ticks = Stopwatch.GetTimestamp();

            secs = ticks / freq;
            nsecs = ticks % freq * 1000000000 / freq;
        }

        var buf = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), (int)secs);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), (int)nsecs);
        if (!sm.Engine.CopyToUser(tsPtr, buf)) return -(int)Errno.EFAULT;

        return 0;
    }

    private static async ValueTask<int> SysClockGetTime64(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var clockId = (int)a1;
        var tsPtr = a2;

        long secs;
        long nsecs;

        if (clockId == LinuxConstants.CLOCK_REALTIME)
        {
            var ticks = DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks;
            secs = ticks / TimeSpan.TicksPerSecond;
            nsecs = ticks % TimeSpan.TicksPerSecond * 100;
        }
        else
        {
            // CLOCK_MONOTONIC and others
            var freq = Stopwatch.Frequency;
            var ticks = Stopwatch.GetTimestamp();

            secs = ticks / freq;
            nsecs = ticks % freq * 1000000000 / freq;
        }

        var buf = new byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(0, 8), secs);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(8, 8), nsecs);
        if (!sm.Engine.CopyToUser(tsPtr, buf)) return -(int)Errno.EFAULT;

        return 0;
    }

    private static async ValueTask<int> SysNanosleep(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var reqBuf = new byte[8];
        if (!sm.Engine.CopyFromUser(a1, reqBuf)) return -(int)Errno.EFAULT;
        var sec = BinaryPrimitives.ReadInt32LittleEndian(reqBuf.AsSpan(0, 4));
        var nsec = BinaryPrimitives.ReadInt32LittleEndian(reqBuf.AsSpan(4, 4));
        
        var totalMs = sec * 1000L + nsec / 1000000L;
        if (totalMs <= 0 && (sec > 0 || nsec > 0)) totalMs = 1; // Minimum 1ms tick if requested
        if (totalMs < 0) return 0;

        if (sm.Engine.Owner is not FiberTask fiberTask) return -(int)Errno.EPERM;

        var res = await new NanosleepAwaiter(fiberTask, totalMs);

        if (res == AwaitResult.Interrupted)
            return -(int)Errno.ERESTARTSYS;

        return 0;
    }

    private static async ValueTask<int> SysClockNanosleepTime64(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var clockId = (int)a1;
        var flags = (int)a2; // e.g. TIMER_ABSTIME
        var reqPtr = a3;
        var remPtr = a4;

        var reqBuf = new byte[16]; // timespec64
        if (!sm.Engine.CopyFromUser(reqPtr, reqBuf)) return -(int)Errno.EFAULT;
        var sec = BinaryPrimitives.ReadInt64LittleEndian(reqBuf.AsSpan(0, 8));
        var nsec = BinaryPrimitives.ReadInt64LittleEndian(reqBuf.AsSpan(8, 8));

        var totalMs = sec * 1000L + nsec / 1000000L;

        // Simplified: ignore TIMER_ABSTIME for now, just perform relative sleep
        if (totalMs <= 0 && (sec > 0 || nsec > 0)) totalMs = 1;
        if (totalMs < 0) return 0;

        if (sm.Engine.Owner is not FiberTask fiberTask) return -(int)Errno.EPERM;

        var res = await new NanosleepAwaiter(fiberTask, totalMs);

        if (res == AwaitResult.Interrupted)
            return -(int)Errno.ERESTARTSYS;

        return 0;
    }

    private sealed class NanosleepAwaiter : INotifyCompletion
    {
        private readonly FiberTask _task;
        private readonly long _totalMs;

        public NanosleepAwaiter(FiberTask task, long totalMs)
        {
            _task = task;
            _totalMs = totalMs;
        }

        public bool IsCompleted => false;

        public void OnCompleted(Action continuation)
        {
            if (_task.HasUnblockedPendingSignal())
            {
                _task.WakeReason = WakeReason.Signal;
                KernelScheduler.Current?.Schedule(continuation, _task);
                return;
            }

            var scheduler = KernelScheduler.Current!;
            _task.Continuation = continuation;
            
            var timer = scheduler.ScheduleTimer(_totalMs, () =>
            {
                if (_task.WakeReason == WakeReason.None)
                {
                    _task.WakeReason = WakeReason.Timer;
                    scheduler.Schedule(_task);
                }
            });

            // Store timer in task so we can cancel it if needed (but currently FiberTask doesn't have a generic ActiveTimer).
            // Actually, we don't strictly need to cancel it if we handle WakeReason correctly.
            // If PostSignal wakes it up, _task.WakeReason will be Signal, and _task is scheduled.
            // The timer will eventually fire and see WakeReason != None, or it will just be a no-op if the task continued.
            // But wait, if the task resumes, it might start another wait. If the old timer fires, it might corrupt the new wait!
            // We need a way to cancel it. 
            // We can register the timer specifically for this waiter.
            _task.BlockingTimer = timer;
        }

        public AwaitResult GetResult()
        {
            _task.BlockingTimer?.Cancel();
            _task.BlockingTimer = null;

            if (_task.WakeReason != WakeReason.Timer && _task.WakeReason != WakeReason.None)
            {
                _task.WakeReason = WakeReason.None;
                return AwaitResult.Interrupted;
            }
            _task.WakeReason = WakeReason.None;
            return AwaitResult.Completed;
        }

        public NanosleepAwaiter GetAwaiter() => this;
    }

    private static async ValueTask<int> SysNice(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        // Simple stub: for now, don't actually change host priority
        return 0; // Success
    }

    private static async ValueTask<int> SysGetPriority(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        // Simple stub: return default priority (20)
        return 20;
    }

    private static async ValueTask<int> SysSetPriority(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        // Simple stub: success
        return 0;
    }

    private static async ValueTask<int> SysPersonality(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        // personality(0xffffffff) returns current, otherwise sets.
        // For now, always return PER_LINUX (0)
        return (int)LinuxConstants.PER_LINUX;
    }

    private static async ValueTask<int> SysGetCpu(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var cpuPtr = a1;
        var nodePtr = a2;

        if (cpuPtr != 0)
        {
            if (!sm.Engine.CopyToUser(cpuPtr, BitConverter.GetBytes(0u)))
                return -(int)Errno.EFAULT;
        }

        if (nodePtr != 0)
        {
            if (!sm.Engine.CopyToUser(nodePtr, BitConverter.GetBytes(0u)))
                return -(int)Errno.EFAULT;
        }

        return 0;
    }

    private static async ValueTask<int> SysPrctl(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        if (sm.Engine.Owner is not FiberTask task) return -(int)Errno.EPERM;

        var option = (int)a1;

        switch (option)
        {
            case LinuxConstants.PR_SET_NAME:
                var name = sm.ReadString(a2);
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
                if (!sm.Engine.CopyToUser(a2, buf)) return -(int)Errno.EFAULT;
                return 0;

            default:
                Logger.LogWarning($"[SysPrctl] Unhandled option: {option}");
                return 0; // Success for many non-critical options
        }
    }

    private static async ValueTask<int> SysGetThreadArea(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var uInfoAddr = a1;
        var buf = new byte[16];
        if (!sm.Engine.CopyFromUser(uInfoAddr, buf)) return -(int)Errno.EFAULT;

        // var entry = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(0, 4));
        
        // Return whatever was previously set via SetThreadArea
        // We simplified set_thread_area to just set GS base, so we don't track entry indices properly.
        // Just return current GS base as base_addr.
        var baseAddr = sm.Engine.GetSegBase(Seg.GS);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), baseAddr);
        
        if (!sm.Engine.CopyToUser(uInfoAddr, buf)) return -(int)Errno.EFAULT;
        return 0;
    }

    private static async ValueTask<int> SysCapget(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        const uint LINUX_CAPABILITY_VERSION_1 = 0x19980330;
        const uint LINUX_CAPABILITY_VERSION_2 = 0x20071026;
        const uint LINUX_CAPABILITY_VERSION_3 = 0x20080522;

        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        if (sm.Engine.Owner is not FiberTask task) return -(int)Errno.EPERM;
        if (a1 == 0 || a2 == 0) return -(int)Errno.EFAULT;

        var hdr = new byte[8];
        if (!sm.Engine.CopyFromUser(a1, hdr)) return -(int)Errno.EFAULT;
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
            _ = sm.Engine.CopyToUser(a1, hdr);
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

        if (!sm.Engine.CopyToUser(a2, data)) return -(int)Errno.EFAULT;
        return 0;
    }

    private static async ValueTask<int> SysCapset(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        const uint LINUX_CAPABILITY_VERSION_1 = 0x19980330;
        const uint LINUX_CAPABILITY_VERSION_2 = 0x20071026;
        const uint LINUX_CAPABILITY_VERSION_3 = 0x20080522;

        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        if (sm.Engine.Owner is not FiberTask task) return -(int)Errno.EPERM;
        if (a1 == 0 || a2 == 0) return -(int)Errno.EFAULT;

        var hdr = new byte[8];
        if (!sm.Engine.CopyFromUser(a1, hdr)) return -(int)Errno.EFAULT;
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
        if (!sm.Engine.CopyFromUser(a2, data)) return -(int)Errno.EFAULT;

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

    private static async ValueTask<int> SysGetRandom(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var bufAddr = a1;
        var count = a2;
        var flags = a3;

        if (count == 0) return 0;

        // Flags: 0x01 (GRND_NONBLOCK), 0x02 (GRND_RANDOM), 0x04 (GRND_INSECURE)
        // We act as if we are urandom/random always ready (except strict GRND_RANDOM might block, but we simulate non-blocking behavior for now).

        var buffer = new byte[count];
        System.Security.Cryptography.RandomNumberGenerator.Fill(buffer);

        if (!sm.Engine.CopyToUser(bufAddr, buffer)) return -(int)Errno.EFAULT;

        return (int)count;
    }
}
