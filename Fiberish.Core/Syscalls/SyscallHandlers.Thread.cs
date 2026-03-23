using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.X86.Native;
using Microsoft.Extensions.Logging;

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
        var isPrivate = (op & 0x80) != 0; // FUTEX_PRIVATE_FLAG = 128

        if (opCode == 0) // WAIT
        {
            var tidBuf = new byte[4];
            if (!engine.CopyFromUser(uaddr, tidBuf)) return -(int)Errno.EFAULT;
            var currentVal = BinaryPrimitives.ReadUInt32LittleEndian(tidBuf);
            if (currentVal != val) return -(int)Errno.EAGAIN;

            if (!TryResolveFutexKey(engine, uaddr, fshared: !isPrivate, out var waitKey, out var error))
                return error;

            var waiter = Futex.PrepareWait(waitKey);
            var registration = Futex.CreateWaitRegistration(waitKey, waiter);

            var task = engine.Owner as FiberTask;
            if (task == null)
            {
                registration.Cancel();
                return -(int)Errno.EINVAL;
            }

            Logger.LogInformation(
                "[SysFutex WAIT] TID={TID} uaddr=0x{Uaddr:x} val={Val} isPrivate={IsPrivate} key={KeyKind}:{PageValue:x}:{Offset} WakeReason={WR} PendingSig=0x{PS:x}",
                task.TID, uaddr, val, isPrivate, waitKey.Kind, waitKey.PageValue, waitKey.OffsetWithinPage, task.WakeReason, task.PendingSignals);
            var result = await new FutexAwaitable(waiter, task, registration);
            Logger.LogInformation(
                "[SysFutex WAIT] TID={TID} awaiter result={Result} WakeReason={WR} PendingSig=0x{PS:x}",
                task.TID, result, task.WakeReason, task.PendingSignals);
            if (result == AwaitResult.Interrupted)
            {
                Futex.CancelWait(waitKey, waiter);
                return -(int)Errno.ERESTARTSYS;
            }

            return 0;
        }

        if (opCode == 1) // WAKE
        {
            var count = (int)val;
            if (!TryResolveFutexKey(engine, uaddr, fshared: !isPrivate, out var wakeKey, out var error))
                return error;
            return Futex.Wake(wakeKey, count);
        }

        if (opCode is LinuxConstants.FUTEX_LOCK_PI or LinuxConstants.FUTEX_TRYLOCK_PI)
        {
            var task = engine.Owner as FiberTask;
            if (task == null) return -(int)Errno.EINVAL;

            if (!TryResolveFutexKey(engine, uaddr, fshared: !isPrivate, out var lockKey, out var error))
                return error;

            while (true)
            {
                var futexWord = new byte[4];
                if (!engine.CopyFromUser(uaddr, futexWord)) return -(int)Errno.EFAULT;

                var currentVal = BinaryPrimitives.ReadUInt32LittleEndian(futexWord);
                var owner = currentVal & LinuxConstants.FUTEX_TID_MASK;
                var hasWaiters = (currentVal & LinuxConstants.FUTEX_WAITERS) != 0;

                if (owner == 0)
                {
                    var queuedWaiters = Futex.GetWaiterCount(lockKey);
                    var nextVal = (uint)task.TID;
                    if (hasWaiters || queuedWaiters > 0) nextVal |= LinuxConstants.FUTEX_WAITERS;
                    BinaryPrimitives.WriteUInt32LittleEndian(futexWord, nextVal);
                    if (!engine.CopyToUser(uaddr, futexWord)) return -(int)Errno.EFAULT;

                    Logger.LogTrace("[SysFutex {Op}] TID={TID} acquired uaddr=0x{Uaddr:x} waiters={Waiters}",
                        opCode == LinuxConstants.FUTEX_LOCK_PI ? "LOCK_PI" : "TRYLOCK_PI",
                        task.TID, uaddr, queuedWaiters);
                    return 0;
                }

                if (owner == (uint)task.TID) return -(int)Errno.EDEADLK;
                if (opCode == LinuxConstants.FUTEX_TRYLOCK_PI) return -(int)Errno.EBUSY;

                if (!hasWaiters)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(futexWord, currentVal | LinuxConstants.FUTEX_WAITERS);
                    if (!engine.CopyToUser(uaddr, futexWord)) return -(int)Errno.EFAULT;
                }

                var waiter = Futex.PrepareWait(lockKey);
                var registration = Futex.CreateWaitRegistration(lockKey, waiter);

                Logger.LogTrace("[SysFutex LOCK_PI] TID={TID} waiting uaddr=0x{Uaddr:x} owner={Owner} isPrivate={IsPrivate} key={KeyKind}:{PageValue:x}:{Offset}",
                    task.TID, uaddr, owner, isPrivate, lockKey.Kind, lockKey.PageValue, lockKey.OffsetWithinPage);
                var result = await new FutexAwaitable(waiter, task, registration);
                if (result == AwaitResult.Interrupted)
                {
                    Futex.CancelWait(lockKey, waiter);
                    return -(int)Errno.ERESTARTSYS;
                }
            }
        }

        if (opCode == LinuxConstants.FUTEX_UNLOCK_PI)
        {
            var task = engine.Owner as FiberTask;
            if (task == null) return -(int)Errno.EINVAL;

            var futexWord = new byte[4];
            if (!engine.CopyFromUser(uaddr, futexWord)) return -(int)Errno.EFAULT;

            var currentVal = BinaryPrimitives.ReadUInt32LittleEndian(futexWord);
            var owner = currentVal & LinuxConstants.FUTEX_TID_MASK;
            if (owner != (uint)task.TID) return -(int)Errno.EPERM;

            if (!TryResolveFutexKey(engine, uaddr, fshared: !isPrivate, out var unlockKey, out var error))
                return error;

            var queuedWaiters = Futex.GetWaiterCount(unlockKey);
            var nextVal = queuedWaiters > 0 ? LinuxConstants.FUTEX_WAITERS : 0u;
            BinaryPrimitives.WriteUInt32LittleEndian(futexWord, nextVal);
            if (!engine.CopyToUser(uaddr, futexWord)) return -(int)Errno.EFAULT;

            var woke = Futex.Wake(unlockKey, 1);

            Logger.LogTrace("[SysFutex UNLOCK_PI] TID={TID} released uaddr=0x{Uaddr:x} queuedWaiters={Waiters} woke={Woke}",
                task.TID, uaddr, queuedWaiters, woke);
            return 0;
        }

        return -(int)Errno.ENOSYS;
    }

    private readonly struct FutexAwaitable
    {
        private readonly Waiter _waiter;
        private readonly ITaskAsyncRegistration _registration;
        private readonly FiberTask _task;

        public FutexAwaitable(Waiter waiter, FiberTask task, ITaskAsyncRegistration registration)
        {
            _waiter = waiter;
            _task = task;
            _registration = registration;
        }

        public FutexAwaiter GetAwaiter()
        {
            return new FutexAwaiter(_waiter, _task, _registration);
        }
    }

    internal int WakeFutexAddress(Engine engine, uint uaddr, int count, bool includePrivate = true, bool includeShared = true)
    {
        var woke = 0;
        if (includePrivate && TryResolvePrivateFutexKey(engine, uaddr, out var privateKey))
            woke += Futex.Wake(privateKey, count);
        if (includeShared && TryResolveSharedFutexKey(engine, uaddr, out var sharedKey))
            woke += Futex.Wake(sharedKey, count);
        return woke;
    }

    private bool TryResolveFutexKey(Engine engine, uint uaddr, bool fshared, out FutexKey key, out int error)
    {
        if (fshared)
        {
            if (TryResolveSharedFutexKey(engine, uaddr, out key, out error))
                return true;

            // Linux allows non-private futex ops on private mappings too. When
            // the address is not actually backed by shared memory, fall back to
            // an mm-scoped private key instead of failing with EFAULT.
            if (TryResolvePrivateFutexKey(engine, uaddr, out key))
            {
                error = 0;
                return true;
            }

            return false;
        }

        if (TryResolvePrivateFutexKey(engine, uaddr, out key))
        {
            error = 0;
            return true;
        }

        error = -(int)Errno.EFAULT;
        return false;
    }

    private bool TryResolvePrivateFutexKey(Engine engine, uint uaddr, out FutexKey key)
    {
        key = default;
        if (!engine.CopyFromUser(uaddr, new byte[1]))
            return false;

        key = FutexKey.Private(Mem, uaddr & LinuxConstants.PageMask, (ushort)(uaddr & LinuxConstants.PageOffsetMask));
        return true;
    }

    private bool TryResolveSharedFutexKey(Engine engine, uint uaddr, out FutexKey key)
    {
        return TryResolveSharedFutexKey(engine, uaddr, out key, out _);
    }

    private bool TryResolveSharedFutexKey(Engine engine, uint uaddr, out FutexKey key, out int error)
    {
        key = default;
        error = -(int)Errno.EFAULT;

        if (!engine.CopyFromUser(uaddr, new byte[1]))
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

        if (vma.VmMapping != null)
        {
            key = FutexKey.SharedAnonymous(vma.VmMapping, pageIndex, offsetWithinPage);
            error = 0;
            return true;
        }

        return false;
    }

    private readonly struct FutexAwaiter : INotifyCompletion
    {
        private readonly Waiter _waiter;
        private readonly ITaskAsyncRegistration _registration;
        private readonly FiberTask _task;
        private readonly FiberTask.WaitToken _token;

        public FutexAwaiter(Waiter waiter, FiberTask task, ITaskAsyncRegistration registration)
        {
            _waiter = waiter;
            _task = task;
            _registration = registration;
            _token = task.BeginWaitToken();
        }

        public bool IsCompleted => _waiter.Tcs.Task.IsCompleted;

        public void OnCompleted(Action continuation)
        {
            if (!_task.TryEnterAsyncOperation(_token, out var operation) || operation == null)
            {
                _registration.Cancel();
                return;
            }

            if (!operation.TryAddRegistration(_registration))
                return;

            var handler = new FutexCompletionHandler(_task, _token, continuation, operation);
            var waiterAwaiter = _waiter.Tcs.Task.GetAwaiter();
            waiterAwaiter.OnCompleted(handler.OnWaitCompleted);
            _task.ArmInterruptingSignalSafetyNet(_token, handler.OnSignal);
        }

        public AwaitResult GetResult()
        {
            var reason = _task.CompleteWaitToken(_token);
            if (reason != WakeReason.Event && reason != WakeReason.None)
            {
                _waiter.Tcs.TrySetResult(false);
                return AwaitResult.Interrupted;
            }

            if (_waiter.Tcs.Task.IsCompleted && !_waiter.Tcs.Task.Result) return AwaitResult.Interrupted;

            return AwaitResult.Completed;
        }

        private sealed class FutexCompletionHandler
        {
            private readonly TaskAsyncOperationHandle _operation;
            private readonly FiberTask _task;
            private readonly FiberTask.WaitToken _token;

            public FutexCompletionHandler(FiberTask task, FiberTask.WaitToken token, Action continuation,
                TaskAsyncOperationHandle operation)
            {
                _task = task;
                _token = token;
                _operation = operation;
                _operation.TryInitialize(continuation, WaitContinuationMode.ResumeTask);
            }

            public void OnWaitCompleted()
            {
                if (_task.GetWaitReason(_token) == WakeReason.None)
                    _task.TrySetWaitReason(_token, WakeReason.Event);

                _operation.TryComplete(WakeReason.Event);
            }

            public void OnSignal()
            {
                _operation.TryComplete(WakeReason.Signal);
            }
        }
    }

    private async ValueTask<int> SysSetThreadArea(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var uInfoAddr = a1;
        var buf = new byte[16];
        if (!engine.CopyFromUser(uInfoAddr, buf)) return -(int)Errno.EFAULT;

        var entry = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(0, 4));
        var baseAddr = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4, 4));

        Logger.LogInformation($"[SysSetThreadArea] Entry={entry} Base={baseAddr:X}");

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

        var headBuf = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(headBuf, targetTask.RobustListHead);
        if (!engine.CopyToUser(headPtr, headBuf)) return -(int)Errno.EFAULT;

        if (lenPtr != 0)
        {
            var lenBuf = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(lenBuf, targetTask.RobustListSize);
            if (!engine.CopyToUser(lenPtr, lenBuf)) return -(int)Errno.EFAULT;
        }

        return 0;
    }
}
