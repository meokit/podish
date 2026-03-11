using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Fiberish.Core;
using Fiberish.Native;
using Microsoft.Extensions.Logging;
using Timer = Fiberish.Core.Timer;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
    // --- Helpers ---
    private const int NFDBITS = 32;
    private const int FD_SETSIZE = 1024;
    private const uint NSEC_PER_SEC = 1_000_000_000;

    private static async ValueTask<int> SysPoll(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var fdsAddr = a1;
        var nfds = a2;
        var timeout = (int)a3; // milliseconds

        return await DoPoll(sm, fdsAddr, nfds, timeout);
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

    private static async ValueTask<int> SysPselect6(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        if (sm.Engine.Owner is not FiberTask task) return -(int)Errno.EPERM;

        if (!TryReadTimespec32TimeoutMs(sm, a5, out var timeoutMs, out var tsErr)) return tsErr;
        if (!TryReadPselectSigmask(sm, a6, out var hasMask, out var newMask, out var maskErr)) return maskErr;

        var oldMask = task.SignalMask;
        if (hasMask) task.SignalMask = newMask;
        try
        {
            return await DoSelectWithTimeout(sm, (int)a1, a2, a3, a4, timeoutMs);
        }
        finally
        {
            if (hasMask) task.SignalMask = oldMask;
        }
    }

    private static async ValueTask<int> SysPpoll(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        if (sm.Engine.Owner is not FiberTask task) return -(int)Errno.EPERM;

        if (!TryReadTimespec32TimeoutMs(sm, a3, out var timeoutMs, out var tsErr)) return tsErr;
        if (!TryReadDirectSigmask(sm, a4, a5, out var hasMask, out var newMask, out var maskErr)) return maskErr;

        var oldMask = task.SignalMask;
        if (hasMask) task.SignalMask = newMask;
        try
        {
            return await DoPoll(sm, a1, a2, timeoutMs);
        }
        finally
        {
            if (hasMask) task.SignalMask = oldMask;
        }
    }

    private static async ValueTask<int> SysPselect6Time64(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        if (sm.Engine.Owner is not FiberTask task) return -(int)Errno.EPERM;

        if (!TryReadTimespec64TimeoutMs(sm, a5, out var timeoutMs, out var tsErr)) return tsErr;
        if (!TryReadPselectSigmask(sm, a6, out var hasMask, out var newMask, out var maskErr)) return maskErr;

        var oldMask = task.SignalMask;
        if (hasMask) task.SignalMask = newMask;
        try
        {
            return await DoSelectWithTimeout(sm, (int)a1, a2, a3, a4, timeoutMs);
        }
        finally
        {
            if (hasMask) task.SignalMask = oldMask;
        }
    }

    private static async ValueTask<int> SysPpollTime64(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        if (sm.Engine.Owner is not FiberTask task) return -(int)Errno.EPERM;

        if (!TryReadTimespec64TimeoutMs(sm, a3, out var timeoutMs, out var tsErr)) return tsErr;
        if (!TryReadDirectSigmask(sm, a4, a5, out var hasMask, out var newMask, out var maskErr)) return maskErr;

        var oldMask = task.SignalMask;
        if (hasMask) task.SignalMask = newMask;
        try
        {
            return await DoPoll(sm, a1, a2, timeoutMs);
        }
        finally
        {
            if (hasMask) task.SignalMask = oldMask;
        }
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

        return await DoSelectWithTimeout(sm, n, inp, outp, exp, timeoutMs);
    }

    private static async ValueTask<int> DoSelectWithTimeout(SyscallManager sm, int n, uint inp, uint outp, uint exp,
        long timeoutMs)
    {
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
            var ret = await new SelectAwaitable(sm, n, inp, outp, exp, timeoutMs);
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

    private static async ValueTask<int> DoPoll(SyscallManager sm, uint fdsAddr, uint nfds, int timeoutMs)
    {
        // 1. Scan
        var ready = ScanPoll(sm, fdsAddr, nfds);
        if (ready > 0 || timeoutMs == 0) return ready;

        // 2. Await
        return await new PollAwaitable(sm, fdsAddr, nfds, timeoutMs);
    }

    private static bool TryReadPselectSigmask(SyscallManager sm, uint sigArgPtr, out bool hasMask, out ulong mask,
        out int err)
    {
        hasMask = false;
        mask = 0;
        err = 0;
        if (sigArgPtr == 0) return true;

        var argBuf = new byte[8];
        if (!sm.Engine.CopyFromUser(sigArgPtr, argBuf))
        {
            err = -(int)Errno.EFAULT;
            return false;
        }

        var sigmaskPtr = BinaryPrimitives.ReadUInt32LittleEndian(argBuf.AsSpan(0, 4));
        var sigsetsize = BinaryPrimitives.ReadUInt32LittleEndian(argBuf.AsSpan(4, 4));
        return TryReadDirectSigmask(sm, sigmaskPtr, sigsetsize, out hasMask, out mask, out err);
    }

    private static bool TryReadDirectSigmask(SyscallManager sm, uint sigmaskPtr, uint sigsetsize, out bool hasMask,
        out ulong mask, out int err)
    {
        hasMask = false;
        mask = 0;
        err = 0;
        if (sigmaskPtr == 0) return true;
        if (sigsetsize != 8)
        {
            err = -(int)Errno.EINVAL;
            return false;
        }

        var maskBuf = new byte[8];
        if (!sm.Engine.CopyFromUser(sigmaskPtr, maskBuf))
        {
            err = -(int)Errno.EFAULT;
            return false;
        }

        mask = BinaryPrimitives.ReadUInt64LittleEndian(maskBuf);
        mask &= ~(1UL << ((int)Signal.SIGKILL - 1));
        mask &= ~(1UL << ((int)Signal.SIGSTOP - 1));
        hasMask = true;
        return true;
    }

    private static bool TryReadTimespec32TimeoutMs(SyscallManager sm, uint timespecPtr, out int timeoutMs, out int err)
    {
        timeoutMs = -1;
        err = 0;
        if (timespecPtr == 0) return true;

        var buf = new byte[8];
        if (!sm.Engine.CopyFromUser(timespecPtr, buf))
        {
            err = -(int)Errno.EFAULT;
            return false;
        }

        var sec = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(0, 4));
        var nsec = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(4, 4));
        return TryConvertTimespecToTimeoutMs(sec, nsec, out timeoutMs, out err);
    }

    private static bool TryReadTimespec64TimeoutMs(SyscallManager sm, uint timespecPtr, out int timeoutMs, out int err)
    {
        timeoutMs = -1;
        err = 0;
        if (timespecPtr == 0) return true;

        var buf = new byte[16];
        if (!sm.Engine.CopyFromUser(timespecPtr, buf))
        {
            err = -(int)Errno.EFAULT;
            return false;
        }

        var sec = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(0, 8));
        var nsec = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(8, 8));
        return TryConvertTimespecToTimeoutMs(sec, nsec, out timeoutMs, out err);
    }

    private static bool TryConvertTimespecToTimeoutMs(long sec, long nsec, out int timeoutMs, out int err)
    {
        timeoutMs = -1;
        err = 0;
        if (sec < 0 || nsec < 0 || nsec >= NSEC_PER_SEC)
        {
            err = -(int)Errno.EINVAL;
            return false;
        }

        if (sec == 0 && nsec == 0)
        {
            timeoutMs = 0;
            return true;
        }

        if (sec >= int.MaxValue / 1000)
        {
            timeoutMs = int.MaxValue;
            return true;
        }

        var msFromSec = sec * 1000;
        var msFromNsec = (nsec + 999_999) / 1_000_000; // ceil to avoid busy-looping on sub-ms timeout
        var totalMs = msFromSec + msFromNsec;
        timeoutMs = totalMs >= int.MaxValue ? int.MaxValue : (int)totalMs;
        return true;
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

            var revents = file.OpenedInode!.Poll(file, pollEvents);

            if ((revents & (PollEvents.POLLIN | PollEvents.POLLHUP | PollEvents.POLLERR)) != 0 && checkRead)
            {
                resIn[wordIndex] |= mask;
                ready++;
            }

            if ((revents & (PollEvents.POLLOUT | PollEvents.POLLERR)) != 0 && checkWrite)
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
                    var revents = file.OpenedInode!.Poll(file, pfd.Events);
                    Logger.LogTrace("[ScanPoll] FD={Fd} Events={Events} Revents={Revents} Type={Type}", pfd.Fd,
                        pfd.Events, revents, file.OpenedInode!.GetType().Name);

                    if (revents != 0)
                    {
                        pfd.Revents = revents;
                        readyCount++;
                    }
                }
                else
                {
                    Logger.LogTrace("[ScanPoll] FD={Fd} (INVALID)", pfd.Fd);
                    pfd.Revents = PollEvents.POLLNVAL;
                    readyCount++;
                }
            }
            else
            {
                Logger.LogTrace("[ScanPoll] FD={Fd} (IGNORED)", pfd.Fd);
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

    public readonly struct SelectAwaitable
    {
        private readonly SelectAwaitState _state;

        public SelectAwaitable(SyscallManager sm, int n, uint inp, uint outp, uint exp, long timeoutMs)
        {
            _state = new SelectAwaitState(sm, n, inp, outp, exp, timeoutMs);
        }

        public SelectAwaiter GetAwaiter()
        {
            return new SelectAwaiter(_state);
        }
    }

    public readonly struct SelectAwaiter : INotifyCompletion
    {
        private readonly SelectAwaitState _state;

        internal SelectAwaiter(SelectAwaitState state)
        {
            _state = state;
        }

        public bool IsCompleted => false;

        public void OnCompleted(Action continuation)
        {
            _state.OnCompleted(continuation);
        }

        public int GetResult()
        {
            return _state.GetResult();
        }
    }

    internal sealed class SelectAwaitState
    {
        private readonly uint _exp;
        private readonly uint _inp;
        private readonly int _n;
        private readonly uint _outp;
        private readonly SyscallManager _sm;
        private readonly long _timeoutMs;
        private readonly List<IDisposable> _waitRegistrations = [];
        private bool _completed;
        private Action? _continuation;
        private bool _hasTimedOut;
        private int _reschedulePending;
        private int _result;
        private FiberTask _task = null!;
        private Timer? _timer;
        private FiberTask.WaitToken? _token;

        public SelectAwaitState(SyscallManager sm, int n, uint inp, uint outp, uint exp, long timeoutMs)
        {
            _sm = sm;
            _n = n;
            _inp = inp;
            _outp = outp;
            _exp = exp;
            _timeoutMs = timeoutMs;
        }

        public void OnCompleted(Action continuation)
        {
            var scheduler = KernelScheduler.Current!;
            _task = (_sm.Engine.Owner as FiberTask)!;
            _continuation = continuation;
            _token = _task.BeginWaitToken();

            if (_timeoutMs > 0)
                _timer = scheduler.ScheduleTimer(_timeoutMs, () =>
                {
                    _hasTimedOut = true;
                    ScheduleRePoll();
                });

            DoPoll();

            if (!_completed)
                _task.ArmSignalSafetyNet(_token, () => ScheduleRePoll());
        }

        public int GetResult()
        {
            ClearWaitRegistrations();
            if (_token != null) _task.CompleteWaitToken(_token);
            return _result;
        }

        private void ScheduleRePoll()
        {
            if (Interlocked.Exchange(ref _reschedulePending, 1) == 0)
                KernelScheduler.Current!.Schedule(() =>
                {
                    _reschedulePending = 0;
                    DoPoll();
                });
        }

        private void DoPoll()
        {
            if (_completed) return;
            ClearWaitRegistrations();

            if (_task.HasUnblockedPendingSignal())
            {
                _timer?.Cancel();
                _result = -(int)Errno.ERESTARTSYS;
                _completed = true;
                _continuation?.Invoke();
                return;
            }

            var ready = ScanSelect(_sm, _n, _inp, _outp, _exp, out _, out _, out _);
            if (ready > 0)
            {
                _timer?.Cancel();
                _result = ready;
                _completed = true;
                _continuation?.Invoke();
                return;
            }

            if (_hasTimedOut)
            {
                _result = 0; // Timeout
                _completed = true;
                _continuation?.Invoke();
                return;
            }

            RegisterWaits();
        }

        private void RegisterWaits()
        {
            if (_token == null) return;
            var intCount = (_n + NFDBITS - 1) / NFDBITS;
            if (intCount > FD_SETSIZE / 32) intCount = FD_SETSIZE / 32;

            var inSets = new uint[intCount];
            var outSets = new uint[intCount];
            var exSets = new uint[intCount];

            if (_inp != 0) ReadSpan(_sm.Engine, _inp, inSets);
            if (_outp != 0) ReadSpan(_sm.Engine, _outp, outSets);
            if (_exp != 0) ReadSpan(_sm.Engine, _exp, exSets);

            for (var i = 0; i < _n; i++)
            {
                var wordIndex = i / NFDBITS;
                var bitIndex = i % NFDBITS;
                var mask = 1u << bitIndex;

                var checkRead = (inSets[wordIndex] & mask) != 0;
                var checkWrite = (outSets[wordIndex] & mask) != 0;
                var checkEx = (exSets[wordIndex] & mask) != 0;

                if (!checkRead && !checkWrite && !checkEx) continue;

                if (_sm.FDs.TryGetValue(i, out var file))
                {
                    short events = 0;
                    if (checkRead) events |= PollEvents.POLLIN;
                    if (checkWrite) events |= PollEvents.POLLOUT;
                    if (checkEx) events |= PollEvents.POLLPRI;

                    if (events != 0)
                    {
                        var registration = file.OpenedInode!.RegisterWaitHandle(file, ScheduleRePoll, events);
                        if (registration != null) _waitRegistrations.Add(registration);
                    }
                }
            }
        }

        private void ClearWaitRegistrations()
        {
            if (_waitRegistrations.Count == 0) return;
            foreach (var registration in _waitRegistrations) registration.Dispose();
            _waitRegistrations.Clear();
        }
    }

    public readonly struct PollAwaitable
    {
        private readonly PollAwaitState _state;

        public PollAwaitable(SyscallManager sm, uint fdsAddr, uint nfds, int timeoutMs)
        {
            _state = new PollAwaitState(sm, fdsAddr, nfds, timeoutMs);
        }

        public PollAwaiter GetAwaiter()
        {
            return new PollAwaiter(_state);
        }
    }

    public readonly struct PollAwaiter : INotifyCompletion
    {
        private readonly PollAwaitState _state;

        internal PollAwaiter(PollAwaitState state)
        {
            _state = state;
        }

        public bool IsCompleted => false;

        public void OnCompleted(Action continuation)
        {
            _state.OnCompleted(continuation);
        }

        public int GetResult()
        {
            return _state.GetResult();
        }
    }

    internal sealed class PollAwaitState
    {
        private readonly uint _fdsAddr;
        private readonly uint _nfds;
        private readonly SyscallManager _sm;
        private readonly int _timeoutMs;
        private readonly List<IDisposable> _waitRegistrations = [];
        private bool _completed;
        private Action? _continuation;
        private bool _hasTimedOut;
        private int _reschedulePending;
        private int _result;
        private FiberTask _task = null!;
        private Timer? _timer;
        private FiberTask.WaitToken? _token;

        public PollAwaitState(SyscallManager sm, uint fdsAddr, uint nfds, int timeoutMs)
        {
            _sm = sm;
            _fdsAddr = fdsAddr;
            _nfds = nfds;
            _timeoutMs = timeoutMs;
        }

        public void OnCompleted(Action continuation)
        {
            var scheduler = KernelScheduler.Current!;
            _task = (_sm.Engine.Owner as FiberTask)!;
            _continuation = continuation;
            _token = _task.BeginWaitToken();

            if (_timeoutMs > 0)
                _timer = scheduler.ScheduleTimer(_timeoutMs, () =>
                {
                    _hasTimedOut = true;
                    ScheduleRePoll();
                });

            DoPoll();

            if (!_completed)
                _task.ArmSignalSafetyNet(_token, () => ScheduleRePoll());
        }

        public int GetResult()
        {
            ClearWaitRegistrations();
            if (_token != null) _task.CompleteWaitToken(_token);
            return _result;
        }

        private void ScheduleRePoll()
        {
            if (Interlocked.Exchange(ref _reschedulePending, 1) == 0)
                KernelScheduler.Current!.Schedule(() =>
                {
                    _reschedulePending = 0;
                    DoPoll();
                });
        }

        private void DoPoll()
        {
            if (_completed) return;
            ClearWaitRegistrations();

            if (_task.HasUnblockedPendingSignal())
            {
                _timer?.Cancel();
                _result = -(int)Errno.ERESTARTSYS;
                _completed = true;
                _continuation?.Invoke();
                return;
            }

            var ready = ScanPoll(_sm, _fdsAddr, _nfds);
            if (ready > 0)
            {
                _timer?.Cancel();
                _result = ready;
                _completed = true;
                _continuation?.Invoke();
                return;
            }

            if (_hasTimedOut)
            {
                _result = 0;
                _completed = true;
                _continuation?.Invoke();
                return;
            }

            RegisterWaits();
        }

        private void RegisterWaits()
        {
            var sizeOfPollfd = Marshal.SizeOf<Pollfd>();

            for (uint i = 0; i < _nfds; i++)
            {
                var itemAddr = _fdsAddr + i * (uint)sizeOfPollfd;
                var pfd = ReadStruct<Pollfd>(_sm.Engine, itemAddr);
                if (pfd.Fd >= 0 && _sm.FDs.TryGetValue(pfd.Fd, out var file))
                {
                    var registration = file.OpenedInode!.RegisterWaitHandle(file, ScheduleRePoll, pfd.Events);
                    if (registration != null) _waitRegistrations.Add(registration);
                }
            }
        }

        private void ClearWaitRegistrations()
        {
            if (_waitRegistrations.Count == 0) return;
            foreach (var registration in _waitRegistrations) registration.Dispose();
            _waitRegistrations.Clear();
        }
    }
}