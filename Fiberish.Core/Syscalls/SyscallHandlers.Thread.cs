using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.VFS;
using Fiberish.X86.Native;
using Microsoft.Extensions.Logging;
using Timer = Fiberish.Core.Timer;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
#pragma warning disable CS1998 // Async method lacks await operators - syscall handlers require async signature
    private async ValueTask<int> SysFutex(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var uaddr = a1;
        var op = (int)a2;
        var val = a3;

        var opCode = op & 0x7F;
        var isPrivate = (op & LinuxConstants.FUTEX_PRIVATE_FLAG) != 0;
        var useClockRealtime = (op & LinuxConstants.FUTEX_CLOCK_REALTIME) != 0;

        if (ValidateFutexAddress(uaddr) != 0) return -(int)Errno.EINVAL;
        if (useClockRealtime && !SupportsFutexClockRealtime(opCode)) return -(int)Errno.ENOSYS;

        if (opCode == 0) // WAIT
        {
            int? timeoutMs = null;
            if (a4 != 0)
            {
                if (!TryReadTimespec32TimeoutMs(engine, a4, out var parsedTimeout, out var timeoutErr))
                    return timeoutErr;

                timeoutMs = parsedTimeout;
            }

            return await FutexWait(engine, uaddr, val, timeoutMs, isPrivate, LinuxConstants.FUTEX_BITSET_MATCH_ANY,
                "WAIT");
        }

        if (opCode == 1) // WAKE
        {
            var count = (int)val;
            if (!TryResolveFutexKey(engine, uaddr, !isPrivate, out var wakeKey, out var error))
                return error;

            return Futex.Wake(wakeKey, count);
        }

        if (opCode == LinuxConstants.FUTEX_WAIT_BITSET)
        {
            if (a6 == 0) return -(int)Errno.EINVAL;

            int? timeoutMs = null;
            if (a4 != 0)
            {
                if (!TryReadAbsoluteTimespec32TimeoutMs(engine, a4,
                        useClockRealtime ? LinuxConstants.CLOCK_REALTIME : LinuxConstants.CLOCK_MONOTONIC,
                        out var parsedTimeout, out var timeoutErr))
                    return timeoutErr;

                timeoutMs = parsedTimeout;
            }

            return await FutexWait(engine, uaddr, val, timeoutMs, isPrivate, a6, "WAIT_BITSET");
        }

        if (opCode == LinuxConstants.FUTEX_WAKE_BITSET)
        {
            if (a6 == 0) return -(int)Errno.EINVAL;

            var count = (int)val;
            if (!TryResolveFutexKey(engine, uaddr, !isPrivate, out var wakeKey, out var error))
                return error;

            return Futex.Wake(wakeKey, count, a6);
        }

        if (opCode == LinuxConstants.FUTEX_WAIT_REQUEUE_PI)
        {
            var uaddr2 = a5;
            if (uaddr2 == 0 || uaddr2 == uaddr) return -(int)Errno.EINVAL;
            if (ValidateFutexAddress(uaddr2) != 0) return -(int)Errno.EINVAL;
            if (!TryResolveFutexKey(engine, uaddr2, !isPrivate, out _, out var targetError))
                return targetError;

            int? timeoutMs = null;
            if (a4 != 0)
            {
                if (!TryReadAbsoluteTimespec32TimeoutMs(engine, a4,
                        useClockRealtime ? LinuxConstants.CLOCK_REALTIME : LinuxConstants.CLOCK_MONOTONIC,
                        out var parsedTimeout, out var timeoutErr))
                    return timeoutErr;

                timeoutMs = parsedTimeout;
            }

            return await FutexWait(engine, uaddr, val, timeoutMs, isPrivate, LinuxConstants.FUTEX_BITSET_MATCH_ANY,
                "WAIT_REQUEUE_PI");
        }

        if (opCode == LinuxConstants.FUTEX_REQUEUE)
        {
            var wakeCount = (int)val;
            var requeueCount = (int)a4;
            var uaddr2 = a5;

            if (ValidateFutexAddress(uaddr2) != 0) return -(int)Errno.EINVAL;
            if (wakeCount < 0 || requeueCount < 0 || uaddr2 == 0) return -(int)Errno.EINVAL;
            if (!TryResolveFutexKey(engine, uaddr, !isPrivate, out var sourceKey, out var sourceError))
                return sourceError;
            if (!TryResolveFutexKey(engine, uaddr2, !isPrivate, out var targetKey, out var targetError))
                return targetError;

            var woke = Futex.Wake(sourceKey, wakeCount);
            return woke + Futex.Requeue(sourceKey, targetKey, requeueCount);
        }

        if (opCode == LinuxConstants.FUTEX_CMP_REQUEUE_PI)
        {
            var wakeCount = (int)val;
            var requeueCount = (int)a4;
            var uaddr2 = a5;
            var expected = a6;

            if (wakeCount != 1 || requeueCount < 0 || uaddr2 == 0 || uaddr2 == uaddr) return -(int)Errno.EINVAL;
            if (ValidateFutexAddress(uaddr2) != 0) return -(int)Errno.EINVAL;

            if (!TryReadUserUInt32(engine, uaddr, out var currentVal)) return -(int)Errno.EFAULT;
            if (currentVal != expected) return -(int)Errno.EAGAIN;

            if (!TryResolveFutexKey(engine, uaddr, !isPrivate, true, out var sourceKey, out var sourceError))
                return sourceError;
            if (!TryResolveFutexKey(engine, uaddr2, !isPrivate, out var targetKey, out var targetError))
                return targetError;

            var woke = Futex.Wake(sourceKey, wakeCount);
            return woke + Futex.Requeue(sourceKey, targetKey, requeueCount);
        }

        if (opCode == LinuxConstants.FUTEX_CMP_REQUEUE)
        {
            var wakeCount = (int)val;
            var requeueCount = (int)a4;
            var uaddr2 = a5;
            var expected = a6;

            if (ValidateFutexAddress(uaddr2) != 0) return -(int)Errno.EINVAL;
            if (wakeCount < 0 || requeueCount < 0 || uaddr2 == 0) return -(int)Errno.EINVAL;

            if (!TryReadUserUInt32(engine, uaddr, out var currentVal)) return -(int)Errno.EFAULT;
            if (currentVal != expected) return -(int)Errno.EAGAIN;

            if (!TryResolveFutexKey(engine, uaddr, !isPrivate, true, out var sourceKey, out var sourceError))
                return sourceError;
            if (!TryResolveFutexKey(engine, uaddr2, !isPrivate, out var targetKey, out var targetError))
                return targetError;

            var woke = Futex.Wake(sourceKey, wakeCount);
            return woke + Futex.Requeue(sourceKey, targetKey, requeueCount);
        }

        if (opCode == LinuxConstants.FUTEX_WAKE_OP)
        {
            var wakeCount = (int)val;
            var wakeCount2 = (int)a4;
            var uaddr2 = a5;

            if (ValidateFutexAddress(uaddr2) != 0) return -(int)Errno.EINVAL;
            if (wakeCount < 0 || wakeCount2 < 0 || uaddr2 == 0) return -(int)Errno.EINVAL;

            if (!TryResolveFutexKey(engine, uaddr, !isPrivate, out var sourceKey, out var sourceError))
                return sourceError;
            if (!TryReadUserUInt32(engine, uaddr2, out var oldVal)) return -(int)Errno.EFAULT;
            if (!TryResolveFutexKey(engine, uaddr2, !isPrivate, true, out var targetKey, out var targetError))
                return targetError;

            if (!TryApplyWakeOp(a6, oldVal, out var newVal, out var wakeSecond, out var wakeOpErr))
                return wakeOpErr;

            if (!TryWriteUserUInt32(engine, uaddr2, newVal)) return -(int)Errno.EFAULT;

            var woke = Futex.Wake(sourceKey, wakeCount);
            if (wakeSecond)
                woke += Futex.Wake(targetKey, wakeCount2);

            return woke;
        }

        if (opCode is LinuxConstants.FUTEX_LOCK_PI or LinuxConstants.FUTEX_TRYLOCK_PI)
        {
            var task = engine.Owner as FiberTask;
            if (task == null) return -(int)Errno.EINVAL;

            while (true)
            {
                if (!TryReadUserUInt32(engine, uaddr, out var currentVal)) return -(int)Errno.EFAULT;
                if (!TryResolveFutexKey(engine, uaddr, !isPrivate, true, out var lockKey, out var error))
                    return error;
                var owner = currentVal & LinuxConstants.FUTEX_TID_MASK;
                var hasWaiters = (currentVal & LinuxConstants.FUTEX_WAITERS) != 0;

                if (owner == 0)
                {
                    var queuedWaiters = Futex.GetWaiterCount(lockKey);
                    var nextVal = (uint)task.TID;
                    if (hasWaiters || queuedWaiters > 0) nextVal |= LinuxConstants.FUTEX_WAITERS;
                    if (!TryWriteUserUInt32(engine, uaddr, nextVal)) return -(int)Errno.EFAULT;

                    if (Logger.IsEnabled(LogLevel.Trace))
                        Logger.LogTrace("[SysFutex {Op}] TID={TID} acquired uaddr=0x{Uaddr:x} waiters={Waiters}",
                            opCode == LinuxConstants.FUTEX_LOCK_PI ? "LOCK_PI" : "TRYLOCK_PI",
                            task.TID, uaddr, queuedWaiters);
                    return 0;
                }

                if (owner == (uint)task.TID) return -(int)Errno.EDEADLK;
                if (opCode == LinuxConstants.FUTEX_TRYLOCK_PI) return -(int)Errno.EBUSY;

                if (!hasWaiters)
                {
                    if (!TryWriteUserUInt32(engine, uaddr, currentVal | LinuxConstants.FUTEX_WAITERS))
                        return -(int)Errno.EFAULT;
                }

                var waiter = Futex.PrepareWait(lockKey);
                var registration = Futex.CreateWaitRegistration(lockKey, waiter);

                if (Logger.IsEnabled(LogLevel.Trace))
                    Logger.LogTrace(
                        "[SysFutex LOCK_PI] TID={TID} waiting uaddr=0x{Uaddr:x} owner={Owner} isPrivate={IsPrivate} key={KeyKind}:{PageValue:x}:{Offset}",
                        task.TID, uaddr, owner, isPrivate, lockKey.Kind, lockKey.PageValue, lockKey.OffsetWithinPage);
                var result = await new FutexAwaitable(waiter, task, registration, null);
                if (result == FutexWaitOutcome.Interrupted)
                {
                    Futex.CancelWait(lockKey, waiter);
                    return -(int)Errno.ERESTARTSYS;
                }

                if (result == FutexWaitOutcome.TimedOut)
                {
                    Futex.CancelWait(lockKey, waiter);
                    return -(int)Errno.ETIMEDOUT;
                }
            }
        }

        if (opCode == LinuxConstants.FUTEX_UNLOCK_PI)
        {
            var task = engine.Owner as FiberTask;
            if (task == null) return -(int)Errno.EINVAL;

            if (!TryReadUserUInt32(engine, uaddr, out var currentVal)) return -(int)Errno.EFAULT;
            var owner = currentVal & LinuxConstants.FUTEX_TID_MASK;
            if (owner != (uint)task.TID) return -(int)Errno.EPERM;

            if (!TryResolveFutexKey(engine, uaddr, !isPrivate, true, out var unlockKey, out var error))
                return error;

            var queuedWaiters = Futex.GetWaiterCount(unlockKey);
            var nextVal = queuedWaiters > 0 ? LinuxConstants.FUTEX_WAITERS : 0u;
            if (!TryWriteUserUInt32(engine, uaddr, nextVal)) return -(int)Errno.EFAULT;

            var woke = Futex.Wake(unlockKey, 1);

            if (Logger.IsEnabled(LogLevel.Trace))
                Logger.LogTrace(
                    "[SysFutex UNLOCK_PI] TID={TID} released uaddr=0x{Uaddr:x} queuedWaiters={Waiters} woke={Woke}",
                    task.TID, uaddr, queuedWaiters, woke);
            return 0;
        }

        if (opCode == LinuxConstants.FUTEX_FD)
            return -(int)Errno.ENOSYS;

        return -(int)Errno.ENOSYS;
    }

    private async ValueTask<int> FutexWait(Engine engine, uint uaddr, uint expectedValue, int? timeoutMs,
        bool isPrivate,
        uint bitsetMask, string opName)
    {
        if (!TryReadUserUInt32(engine, uaddr, out var currentVal)) return -(int)Errno.EFAULT;
        if (currentVal != expectedValue) return -(int)Errno.EAGAIN;

        if (!TryResolveFutexKey(engine, uaddr, !isPrivate, true, out var waitKey, out var error))
            return error;

        var waiter = Futex.PrepareWait(waitKey, bitsetMask);
        var registration = Futex.CreateWaitRegistration(waitKey, waiter);

        var task = engine.Owner as FiberTask;
        if (task == null)
        {
            registration.Cancel();
            return -(int)Errno.EINVAL;
        }

        // Linux FUTEX_WAIT compare-and-block must not lose a racing wake, so re-check after enqueue.
        if (!TryReadUserUInt32(engine, uaddr, out currentVal))
        {
            registration.Cancel();
            return -(int)Errno.EFAULT;
        }

        if (currentVal != expectedValue)
        {
            registration.Cancel();
            return -(int)Errno.EAGAIN;
        }

        if (timeoutMs == 0)
        {
            registration.Cancel();
            return -(int)Errno.ETIMEDOUT;
        }

        if (Logger.IsEnabled(LogLevel.Trace))
            Logger.LogTrace(
                "[SysFutex {Op}] TID={TID} uaddr=0x{Uaddr:x} val={Val} bitset=0x{Bitset:x8} isPrivate={IsPrivate} key={KeyKind}:{PageValue:x}:{Offset} WakeReason={WR} PendingSig=0x{PS:x}",
                opName, task.TID, uaddr, expectedValue, bitsetMask, isPrivate, waitKey.Kind, waitKey.PageValue,
                waitKey.OffsetWithinPage, task.WakeReason, task.PendingSignals);
        var result = await new FutexAwaitable(waiter, task, registration, timeoutMs);
        if (Logger.IsEnabled(LogLevel.Trace))
            Logger.LogTrace(
                "[SysFutex {Op}] TID={TID} awaiter result={Result} WakeReason={WR} PendingSig=0x{PS:x}",
                opName, task.TID, result, task.WakeReason, task.PendingSignals);
        if (result == FutexWaitOutcome.Interrupted)
        {
            Futex.CancelWait(waitKey, waiter);
            return -(int)Errno.ERESTARTSYS;
        }

        if (result == FutexWaitOutcome.TimedOut)
        {
            Futex.CancelWait(waitKey, waiter);
            return -(int)Errno.ETIMEDOUT;
        }

        return 0;
    }

    private readonly struct FutexAwaitable
    {
        private readonly Waiter _waiter;
        private readonly ITaskAsyncRegistration _registration;
        private readonly FiberTask _task;
        private readonly int? _timeoutMs;

        public FutexAwaitable(Waiter waiter, FiberTask task, ITaskAsyncRegistration registration, int? timeoutMs)
        {
            _waiter = waiter;
            _task = task;
            _registration = registration;
            _timeoutMs = timeoutMs;
        }

        public FutexAwaiter GetAwaiter()
        {
            return new FutexAwaiter(_waiter, _task, _registration, _timeoutMs);
        }
    }

    internal int WakeFutexAddress(Engine engine, uint uaddr, int count, bool includePrivate = true,
        bool includeShared = true)
    {
        var woke = 0;
        if (includePrivate && TryResolvePrivateFutexKey(engine, uaddr, out var privateKey))
            woke += Futex.Wake(privateKey, count);
        if (includeShared && TryResolveSharedFutexKey(engine, uaddr, out var sharedKey))
            woke += Futex.Wake(sharedKey, count);
        return woke;
    }

    private static int ValidateFutexAddress(uint uaddr)
    {
        return (uaddr & 0x3) == 0 ? 0 : -(int)Errno.EINVAL;
    }

    private static bool SupportsFutexClockRealtime(int opCode)
    {
        return opCode == LinuxConstants.FUTEX_WAIT ||
               opCode == LinuxConstants.FUTEX_WAIT_BITSET ||
               opCode == LinuxConstants.FUTEX_WAIT_REQUEUE_PI;
    }

    private static bool TryReadAbsoluteTimespec32TimeoutMs(Engine engine, uint timespecPtr, int clockId,
        out int timeoutMs, out int err)
    {
        timeoutMs = -1;
        err = 0;
        if (timespecPtr == 0) return true;

        Span<byte> buf = stackalloc byte[8];
        if (!engine.CopyFromUser(timespecPtr, buf))
        {
            err = -(int)Errno.EFAULT;
            return false;
        }

        var sec = BinaryPrimitives.ReadInt32LittleEndian(buf[..4]);
        var nsec = BinaryPrimitives.ReadInt32LittleEndian(buf[4..]);
        if (sec < 0 || nsec < 0 || nsec >= NSEC_PER_SEC)
        {
            err = -(int)Errno.EINVAL;
            return false;
        }

        var deadlineNs = checked(sec * 1_000_000_000L + nsec);
        var nowNs = GetSleepClockNs(clockId);
        var remainingNs = Math.Max(0L, deadlineNs - nowNs);
        return TryConvertTimespecToTimeoutMs(remainingNs / 1_000_000_000L, remainingNs % 1_000_000_000L,
            out timeoutMs, out err);
    }

    private static bool TryApplyWakeOp(uint encodedWakeOp, uint oldValue, out uint newValue, out bool wakeSecond,
        out int error)
    {
        newValue = oldValue;
        wakeSecond = false;
        error = 0;

        var op = (int)((encodedWakeOp >> 28) & 0xF);
        var cmp = (int)((encodedWakeOp >> 24) & 0xF);
        var opArg = SignExtend12Bit((int)((encodedWakeOp >> 12) & 0xFFF));
        var cmpArg = SignExtend12Bit((int)(encodedWakeOp & 0xFFF));

        var shiftArg = (op & LinuxConstants.FUTEX_OP_OPARG_SHIFT) != 0;
        var baseOp = op & ~LinuxConstants.FUTEX_OP_OPARG_SHIFT;
        if (shiftArg)
        {
            if (opArg < 0 || opArg > 31)
            {
                error = -(int)Errno.EINVAL;
                return false;
            }

            opArg = unchecked((int)(1u << opArg));
        }

        var oldSigned = unchecked((int)oldValue);
        int newSigned;
        switch (baseOp)
        {
            case LinuxConstants.FUTEX_OP_SET:
                newSigned = opArg;
                break;
            case LinuxConstants.FUTEX_OP_ADD:
                newSigned = oldSigned + opArg;
                break;
            case LinuxConstants.FUTEX_OP_OR:
                newSigned = oldSigned | opArg;
                break;
            case LinuxConstants.FUTEX_OP_ANDN:
                newSigned = oldSigned & ~opArg;
                break;
            case LinuxConstants.FUTEX_OP_XOR:
                newSigned = oldSigned ^ opArg;
                break;
            default:
                error = -(int)Errno.EINVAL;
                return false;
        }

        newValue = unchecked((uint)newSigned);
        wakeSecond = cmp switch
        {
            LinuxConstants.FUTEX_OP_CMP_EQ => oldSigned == cmpArg,
            LinuxConstants.FUTEX_OP_CMP_NE => oldSigned != cmpArg,
            LinuxConstants.FUTEX_OP_CMP_LT => oldSigned < cmpArg,
            LinuxConstants.FUTEX_OP_CMP_LE => oldSigned <= cmpArg,
            LinuxConstants.FUTEX_OP_CMP_GT => oldSigned > cmpArg,
            LinuxConstants.FUTEX_OP_CMP_GE => oldSigned >= cmpArg,
            _ => false
        };

        if (cmp is < LinuxConstants.FUTEX_OP_CMP_EQ or > LinuxConstants.FUTEX_OP_CMP_GE)
        {
            error = -(int)Errno.EINVAL;
            return false;
        }

        return true;
    }

    private static int SignExtend12Bit(int value)
    {
        return (value & 0x800) != 0 ? value | unchecked((int)0xFFFFF000) : value;
    }

    private static bool TryReadUserUInt32(Engine engine, uint uaddr, out uint value)
    {
        Span<byte> buf = stackalloc byte[4];
        if (!engine.CopyFromUser(uaddr, buf))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(buf);
        return true;
    }

    private static bool TryWriteUserUInt32(Engine engine, uint uaddr, uint value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, value);
        return engine.CopyToUser(uaddr, buf);
    }

    private static bool TryProbeUserAddress(Engine engine, uint uaddr)
    {
        Span<byte> buf = stackalloc byte[1];
        return engine.CopyFromUser(uaddr, buf);
    }

    private bool TryResolveFutexKey(Engine engine, uint uaddr, bool fshared, out FutexKey key, out int error)
    {
        return TryResolveFutexKey(engine, uaddr, fshared, false, out key, out error);
    }

    private bool TryResolveFutexKey(Engine engine, uint uaddr, bool fshared, bool addressValidated,
        out FutexKey key, out int error)
    {
        key = default;
        error = 0;

        if (!addressValidated && !TryProbeUserAddress(engine, uaddr))
        {
            error = -(int)Errno.EFAULT;
            return false;
        }

        if (fshared)
        {
            if (TryResolveSharedFutexKey(engine, uaddr, true, out key, out error))
                return true;

            if (error == -(int)Errno.EINVAL)
                return false;

            // Linux allows non-private futex ops on private mappings too. When
            // the address is not actually backed by shared memory, fall back to
            // an mm-scoped private key instead of failing with EFAULT.
            key = ResolvePrivateFutexKeyUnchecked(uaddr);
            error = 0;
            return true;
        }

        key = ResolvePrivateFutexKeyUnchecked(uaddr);
        return true;
    }

    private FutexKey ResolvePrivateFutexKeyUnchecked(uint uaddr)
    {
        return FutexKey.Private(Mem, uaddr & LinuxConstants.PageMask,
            (ushort)(uaddr & LinuxConstants.PageOffsetMask));
    }

    private bool TryResolvePrivateFutexKey(Engine engine, uint uaddr, out FutexKey key)
    {
        key = default;
        if (!TryProbeUserAddress(engine, uaddr))
            return false;

        key = ResolvePrivateFutexKeyUnchecked(uaddr);
        return true;
    }

    private bool TryResolveSharedFutexKey(Engine engine, uint uaddr, out FutexKey key)
    {
        return TryResolveSharedFutexKey(engine, uaddr, false, out key, out _);
    }

    private bool TryResolveSharedFutexKey(Engine engine, uint uaddr, out FutexKey key, out int error)
    {
        return TryResolveSharedFutexKey(engine, uaddr, false, out key, out error);
    }

    private bool TryResolveSharedFutexKey(Engine engine, uint uaddr, bool addressValidated, out FutexKey key,
        out int error)
    {
        key = default;
        error = -(int)Errno.EFAULT;

        if (!addressValidated && !TryProbeUserAddress(engine, uaddr))
            return false;

        var vma = Mem.FindVmArea(uaddr);
        if (vma == null || (vma.Flags & MapFlags.Shared) == 0)
            return false;

        var pageStart = uaddr & LinuxConstants.PageMask;
        var pageIndex = vma.GetPageIndex(pageStart);
        var offsetWithinPage = (ushort)(uaddr & LinuxConstants.PageOffsetMask);

        if (vma.IsFileBacked && vma.File?.OpenedInode != null)
        {
            key = FutexKey.SharedFile(vma.File.OpenedInode, pageIndex, offsetWithinPage);
            error = 0;
            return true;
        }

        VfsDebugTrace.FailInvariant(
            $"Shared futex VMA missing file backing start=0x{vma.Start:X8} end=0x{vma.End:X8} flags={vma.Flags} perms={vma.Perms}");
        error = -(int)Errno.EINVAL;

        return false;
    }

    private readonly struct FutexAwaiter : INotifyCompletion
    {
        private readonly Waiter _waiter;
        private readonly ITaskAsyncRegistration _registration;
        private readonly FiberTask _task;
        private readonly int? _timeoutMs;
        private readonly FiberTask.WaitToken _token;

        public FutexAwaiter(Waiter waiter, FiberTask task, ITaskAsyncRegistration registration, int? timeoutMs)
        {
            _waiter = waiter;
            _task = task;
            _registration = registration;
            _timeoutMs = timeoutMs;
            _token = task.BeginWaitToken();
        }

        public bool IsCompleted => _waiter.IsCompleted;

        public void OnCompleted(Action continuation)
        {
            if (!_task.TryEnterAsyncOperation(_token, out var operation) || operation == null)
            {
                _registration.Cancel();
                return;
            }

            if (!operation.TryAddRegistration(_registration))
                return;

            var handler = new FutexCompletionHandler(_waiter, _task, continuation, operation, _timeoutMs);
            if (operation.IsActive)
                _task.ArmInterruptingSignalSafetyNet(_token, handler.OnSignal);
        }

        public FutexWaitOutcome GetResult()
        {
            var reason = _task.CompleteWaitToken(_token);
            if (reason == WakeReason.Timer) return FutexWaitOutcome.TimedOut;

            if (reason != WakeReason.Event && reason != WakeReason.None)
                return FutexWaitOutcome.Interrupted;

            return _waiter.Result ? FutexWaitOutcome.Completed : FutexWaitOutcome.Interrupted;
        }

        private sealed class FutexCompletionHandler
        {
            private readonly TaskAsyncOperationHandle _operation;
            private readonly Timer? _timer;

            public FutexCompletionHandler(Waiter waiter, FiberTask task, Action continuation,
                TaskAsyncOperationHandle operation, int? timeoutMs)
            {
                _operation = operation;
                _operation.TryInitialize(continuation);
                waiter.BindSuccessCallback(OnWaitCompleted);
                if (timeoutMs.HasValue)
                {
                    _timer = task.CommonKernel.ScheduleTimer(timeoutMs.Value, OnTimeout);
                    _operation.TryAddRegistration(TaskAsyncRegistration.From(_timer));
                }
            }

            public void OnWaitCompleted()
            {
                _operation.TryComplete(WakeReason.Event);
            }

            public void OnSignal()
            {
                _operation.TryComplete(WakeReason.Signal);
            }

            private void OnTimeout()
            {
                _operation.TryComplete(WakeReason.Timer);
            }
        }
    }

    private enum FutexWaitOutcome
    {
        Completed,
        Interrupted,
        TimedOut
    }

    private async ValueTask<int> SysSetThreadArea(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var uInfoAddr = a1;
        var buf = new byte[16];
        if (!engine.CopyFromUser(uInfoAddr, buf)) return -(int)Errno.EFAULT;

        var entry = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(0, 4));
        var baseAddr = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4, 4));

        LogSetThreadArea(entry, baseAddr);

        engine.SetSegBase(Seg.GS, baseAddr);

        if (entry == 0xFFFFFFFF)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), 12);
            if (!engine.CopyToUser(uInfoAddr, buf.AsSpan(0, 4))) return -(int)Errno.EFAULT;
        }

        return 0;
    }

    private async ValueTask<int> SysSetTidAddress(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        if (engine.Owner is FiberTask task)
        {
            task.ChildClearTidPtr = a1;
            return task.TID;
        }

        if (GetTID != null) return GetTID(engine);
        return 1;
    }

    private async ValueTask<int> SysSetRobustList(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        if (engine.Owner is not FiberTask task) return -(int)Errno.EPERM;

        var head = a1;
        var len = a2;

        if (len != 12) return -(int)Errno.EINVAL; // 32-bit robust_list_head is exactly 12 bytes

        task.RobustListHead = head;
        task.RobustListSize = len;

        return 0;
    }

    private async ValueTask<int> SysGetRobustList(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var pid = (int)a1;
        var headPtr = a2;
        var lenPtr = a3;

        FiberTask? targetTask;
        if (pid == 0)
        {
            targetTask = engine.Owner as FiberTask;
        }
        else
        {
            targetTask = (engine.Owner as FiberTask)?.CommonKernel.GetTask(pid);
            if (targetTask == null) return -(int)Errno.ESRCH;
        }

        if (targetTask == null) return -(int)Errno.ESRCH;

        Span<byte> headBuf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(headBuf, targetTask.RobustListHead);
        if (!engine.CopyToUser(headPtr, headBuf)) return -(int)Errno.EFAULT;

        if (lenPtr != 0)
        {
            Span<byte> lenBuf = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(lenBuf, targetTask.RobustListSize);
            if (!engine.CopyToUser(lenPtr, lenBuf)) return -(int)Errno.EFAULT;
        }

        return 0;
    }
}
