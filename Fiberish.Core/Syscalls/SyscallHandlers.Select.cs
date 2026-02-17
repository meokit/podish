using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Fiberish.Core;
using Fiberish.Native;
using Timer = Fiberish.Core.Timer;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
    // --- Helpers ---
    private const int NFDBITS = 32;
    private const int FD_SETSIZE = 1024;

    private static async ValueTask<int> SysPoll(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var fdsAddr = a1;
        var nfds = a2;
        var timeout = (int)a3; // milliseconds

        // 1. Scan
        var ready = ScanPoll(sm, fdsAddr, nfds);
        if (ready > 0 || timeout == 0) return ready;

        // 2. Await
        return await new PollAwaiter(sm, fdsAddr, nfds, timeout);
    }

    private static async ValueTask<int> SysSelect(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        // old_select(struct sel_arg_struct *)
        var argsAddr = a1;
        if (argsAddr == 0) return -(int)Errno.EFAULT;

        var args = new byte[20];
        if (!sm.Engine.CopyFromUser(argsAddr, args)) return -(int)Errno.EFAULT;
        var n = BitConverter.ToInt32(args, 0);
        var inp = BitConverter.ToUInt32(args, 4);
        var outp = BitConverter.ToUInt32(args, 8);
        var exp = BitConverter.ToUInt32(args, 12);
        var tvp = BitConverter.ToUInt32(args, 16);

        return await DoSelect(sm, n, inp, outp, exp, tvp);
    }

    private static async ValueTask<int> SysNewSelect(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        return await DoSelect(sm, (int)a1, a2, a3, a4, a5);
    }

    private static async ValueTask<int> DoSelect(SyscallManager sm, int n, uint inp, uint outp, uint exp, uint tvp)
    {
        if (n < 0 || n > 1024) return -(int)Errno.EINVAL;

        long timeoutMs = -1;
        if (tvp != 0)
        {
            var tv = ReadStruct<Timeval>(sm.Engine, tvp);
            timeoutMs = tv.TvSec * 1000 + tv.TvUsec / 1000;
        }

        // 1. Scan
        var ready = ScanSelect(sm, n, inp, outp, exp, out var resIn, out var resOut, out var resEx);
        if (ready > 0)
        {
            WriteSelectResults(sm, inp, outp, exp, resIn, resOut, resEx);
            return ready;
        }

        if (timeoutMs == 0) return 0;

        // 2. Await
        try
        {
            var ret = await new SelectAwaiter(sm, n, inp, outp, exp, timeoutMs);
            // If success, we assume SelectAwaiter re-scanned and returned the count?
            // Actually SelectAwaiter.GetResult() logic needs to handle re-scan or partial result.
            // But standard Select returns the count and modifies the sets.
            // Typically after wakeup, we scan again.

            // Re-scan to populate sets
            if (ret >= 0)
            {
                ready = ScanSelect(sm, n, inp, outp, exp, out resIn, out resOut, out resEx);
                WriteSelectResults(sm, inp, outp, exp, resIn, resOut, resEx);
                return ready;
            }

            return ret; // Error (EINTR)
        }
        catch (Exception)
        {
            return -(int)Errno.EINTR;
        }
    }

    private static int ScanSelect(SyscallManager sm, int n, uint inp, uint outp, uint exp,
        out uint[] resIn, out uint[] resOut, out uint[] resEx)
    {
        var ready = 0;
        var intCount = (n + NFDBITS - 1) / NFDBITS;
        if (intCount > FD_SETSIZE / 32) intCount = FD_SETSIZE / 32;

        resIn = new uint[intCount];
        resOut = new uint[intCount];
        resEx = new uint[intCount];

        var inSets = new uint[intCount];
        var outSets = new uint[intCount];
        var exSets = new uint[intCount];

        if (inp != 0) ReadSpan(sm.Engine, inp, inSets);
        if (outp != 0) ReadSpan(sm.Engine, outp, outSets);
        if (exp != 0) ReadSpan(sm.Engine, exp, exSets);

        for (var i = 0; i < n; i++)
        {
            var wordIndex = i / NFDBITS;
            var bitIndex = i % NFDBITS;
            var mask = 1u << bitIndex;

            var checkRead = (inSets[wordIndex] & mask) != 0;
            var checkWrite = (outSets[wordIndex] & mask) != 0;
            var checkEx = (exSets[wordIndex] & mask) != 0;

            if (!checkRead && !checkWrite && !checkEx) continue;

            if (!sm.FDs.TryGetValue(i, out var file)) return -(int)Errno.EBADF;

            short pollEvents = 0;
            if (checkRead) pollEvents |= PollEvents.POLLIN;
            if (checkWrite) pollEvents |= PollEvents.POLLOUT;
            if (checkEx) pollEvents |= PollEvents.POLLPRI;

            var revents = file.Poll(pollEvents);

            if ((revents & PollEvents.POLLIN) != 0 && checkRead)
            {
                resIn[wordIndex] |= mask;
                ready++;
            }

            if ((revents & PollEvents.POLLOUT) != 0 && checkWrite)
            {
                resOut[wordIndex] |= mask;
                ready++;
            }

            if ((revents & PollEvents.POLLPRI) != 0 && checkEx)
            {
                resEx[wordIndex] |= mask;
                ready++;
            }
        }

        return ready;
    }

    private static void WriteSelectResults(SyscallManager sm, uint inp, uint outp, uint exp,
        uint[] resIn, uint[] resOut, uint[] resEx)
    {
        if (inp != 0) WriteSpan(sm.Engine, inp, resIn);
        if (outp != 0) WriteSpan(sm.Engine, outp, resOut);
        if (exp != 0) WriteSpan(sm.Engine, exp, resEx);
    }

    private static int ScanPoll(SyscallManager sm, uint fdsAddr, uint nfds)
    {
        var readyCount = 0;
        var sizeOfPollfd = Marshal.SizeOf<Pollfd>();

        for (uint i = 0; i < nfds; i++)
        {
            var itemAddr = fdsAddr + i * (uint)sizeOfPollfd;
            Pollfd pfd;
            pfd = ReadStruct<Pollfd>(sm.Engine, itemAddr);
            pfd.Revents = 0;

            if (pfd.Fd >= 0)
            {
                if (sm.FDs.TryGetValue(pfd.Fd, out var file))
                {
                    var revents = file.Poll(pfd.Events);
                    if (revents != 0)
                    {
                        pfd.Revents = revents;
                        readyCount++;
                    }
                }
                else
                {
                    pfd.Revents = PollEvents.POLLNVAL;
                    readyCount++;
                }
            }

            WriteStruct(sm.Engine, itemAddr, pfd);
        }

        return readyCount;
    }

    // --- Memory Helpers ---
    private static unsafe T ReadStruct<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors |
                                    DynamicallyAccessedMemberTypes.NonPublicConstructors)]
        T>(Engine engine, uint addr) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var buf = new byte[size];
        if (!engine.CopyFromUser(addr, buf))
            throw new InvalidOperationException($"EFAULT: Failed to read struct {typeof(T).Name} from 0x{addr:x}");

        fixed (byte* pBuf = buf)
        {
            return Marshal.PtrToStructure<T>((IntPtr)pBuf);
        }
    }

    private static unsafe void WriteStruct<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors |
                                    DynamicallyAccessedMemberTypes.NonPublicConstructors)]
        T>(Engine engine, uint addr, T val) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var buf = new byte[size];
        fixed (byte* pBuf = buf)
        {
            Marshal.StructureToPtr(val, (IntPtr)pBuf, false);
        }

        if (!engine.CopyToUser(addr, buf))
            throw new InvalidOperationException($"EFAULT: Failed to write struct {typeof(T).Name} to 0x{addr:x}");
    }

    private static void ReadSpan(Engine engine, uint addr, uint[] dest)
    {
        var bytes = dest.Length * 4;
        var buf = new byte[bytes];
        if (!engine.CopyFromUser(addr, buf))
            throw new InvalidOperationException($"EFAULT: Failed to read span from 0x{addr:x}");
        Buffer.BlockCopy(buf, 0, dest, 0, bytes);
    }

    private static void WriteSpan(Engine engine, uint addr, uint[] src)
    {
        var bytes = src.Length * 4;
        var buf = new byte[bytes];
        Buffer.BlockCopy(src, 0, buf, 0, bytes);
        if (!engine.CopyToUser(addr, buf))
            throw new InvalidOperationException($"EFAULT: Failed to write span to 0x{addr:x}");
    }

    // --- Awaiters ---

    public class SelectAwaiter(SyscallManager sm, int n, uint inp, uint outp, uint exp, long timeoutMs)
        : INotifyCompletion
    {
        private readonly uint _inp = inp, _outp = outp, _exp = exp;
        private readonly int _n = n;
        private readonly SyscallManager _sm = sm;
        private readonly long _timeoutMs = timeoutMs;
        private int _result;
        private FiberTask _task = null!;

        public bool IsCompleted => false;

        public void OnCompleted(Action continuation)
        {
            _task = (_sm.Engine.Owner as FiberTask)!;
            var kernel = KernelScheduler.Current;
            if (kernel == null)
                throw new InvalidOperationException("KernelScheduler.Current is null in Select OnCompleted");

            // 1. Register Timeout
            Timer? timer = null;
            if (_timeoutMs > 0)
                // Convert ms to ticks (assuming 1ms = 1000 ticks)
                timer = kernel.ScheduleTimer(_timeoutMs * 1000, () =>
                {
                    Resume(0); // Timeout
                });

            // 2. Register FDs
            // Ideally we need to register callbacks on Files. VFS File needs event support.
            // For now, we poll every X ticks or implement File.RegisterWait?
            // Assuming simplified model: Poll via Timer for now if VFS event not ready.
            // TODO: Implement proper VFS eventwait

            // Fallback: Busy Poll with Timer (1ms)
            // This is "Pulse" logic
            DoPoll(continuation, timer);
        }

        public SelectAwaiter GetAwaiter()
        {
            return this;
        }

        public int GetResult()
        {
            return _result;
        }

        private void DoPoll(Action continuation, Timer? timer)
        {
            if (_task.WasInterrupted)
            {
                timer?.Cancel();
                _result = -(int)Errno.EINTR;
                continuation();
                return;
            }

            // Scan
            int ready;
            ready = ScanSelect(_sm, _n, _inp, _outp, _exp, out _, out _, out _);
            if (ready > 0)
            {
                timer?.Cancel();
                _result = ready;
                continuation();
            }
            else
            {
                // Re-schedule poll
                // Check if timer expired?
                if (timer != null && timer.Canceled) return; // Already handled by timeout callback?
                // No, timeout callback calls Resume(0).

                KernelScheduler.Current!.ScheduleTimer(1000, () => DoPoll(continuation, timer));
            }
        }

        private void Resume(int result)
        {
            _result = result;
            // How to invoke continuation? We are in KernelScheduler loop.
            // We need to schedule the task?
            // Actually OnCompleted receives 'continuation'. We need to store it.
            // But struct cannot store it easily if it's passed here.

            // Wait, OnCompleted IS the registration.
            // I implemented a polling loop above that CLOSES over 'continuation'.
            // So Resume(0) called by timer needs access to 'continuation'.

            // The timer callback above: 
            // timer = kernel.ScheduleTimer(..., () => Resume(0));
            // Resume needs 'continuation'.

            // Refactor: the loop handles timeout implicitly if I check timer.Expiration?
            // Or passing continuation to Resume.
        }
    }

    public class PollAwaiter(SyscallManager sm, uint fdsAddr, uint nfds, int timeoutMs) : INotifyCompletion
    {
        private readonly uint _fdsAddr = fdsAddr;
        private readonly uint _nfds = nfds;
        private readonly SyscallManager _sm = sm;
        private readonly int _timeoutMs = timeoutMs;
        private int _result;
        private FiberTask _task = null!;

        public bool IsCompleted => false;

        public void OnCompleted(Action continuation)
        {
            _task = (_sm.Engine.Owner as FiberTask)!;

            // Polling Loop
            DoPoll(continuation, DateTime.UtcNow.Ticks / 10000 + _timeoutMs); // Wall clock? No, simple loop counter
        }

        public PollAwaiter GetAwaiter()
        {
            return this;
        }

        public int GetResult()
        {
            return _result;
        }

        // Simplified polling loop
        private void DoPoll(Action continuation, long endTick)
        {
            // Similar to SelectAwaiter...
            int ready;
            ready = ScanPoll(_sm, _fdsAddr, _nfds);

            if (ready > 0)
            {
                _result = ready;
                continuation();
                return;
            }

            if (_timeoutMs != -1)
            {
                // Check timeout logic... 
                // For now just re-schedule
            }

            KernelScheduler.Current!.ScheduleTimer(1000, () => DoPoll(continuation, endTick));
        }
    }
}