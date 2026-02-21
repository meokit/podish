using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Fiberish.Core;
using Fiberish.Native;
using Microsoft.Extensions.Logging;
using Process = System.Diagnostics.Process;
using Timer = Fiberish.Core.Timer;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
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
        var old = task.Process.Umask;
        task.Process.Umask = (int)(a1 & 0x1FF);
        return old;
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

        var buf = new byte[12];
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(0, 8), secs);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8, 4), (int)nsecs);
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
        // 1 tick = 1 microsecond? (Assumed based on legacy code)
        var totalUsec = sec * 1000000L + nsec / 1000L;
        if (totalUsec < 0) return 0;

        if (sm.Engine.Owner is not FiberTask fiberTask) return -(int)Errno.EPERM;

        var res = await new NanosleepAwaiter(fiberTask, totalUsec);

        if (res == AwaitResult.Interrupted)
        {
            if (a2 != 0)
            {
                // TODO: Write remaining time properly
            }
            return -(int)Errno.ERESTARTSYS;
        }

        return 0;
    }

    private sealed class NanosleepAwaiter : INotifyCompletion
    {
        private readonly FiberTask _task;
        private readonly long _totalUsec;

        public NanosleepAwaiter(FiberTask task, long totalUsec)
        {
            _task = task;
            _totalUsec = totalUsec;
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
            scheduler.ScheduleTimer(scheduler.CurrentTick + _totalUsec, () =>
            {
                _task.WakeReason = WakeReason.Timer;
                _task.Continuation = continuation;
                scheduler.Schedule(_task);
            });
        }

        public AwaitResult GetResult()
        {
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
}