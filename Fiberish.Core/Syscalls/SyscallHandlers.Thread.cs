using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Fiberish.Core;
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

            nint physKey = 0;
            if (!isPrivate)
            {
                var hostPtr = engine.GetPhysicalAddressSafe(uaddr, false);
                if (hostPtr == IntPtr.Zero) return -(int)Errno.EFAULT;
                physKey = hostPtr;
            }

            var waiter = isPrivate
                ? Futex.PrepareWait(uaddr)
                : Futex.PrepareWaitShared(physKey);
            var registration = isPrivate
                ? Futex.CreatePrivateWaitRegistration(uaddr, waiter)
                : Futex.CreateSharedWaitRegistration(physKey, waiter);

            var task = engine.Owner as FiberTask;
            if (task == null)
            {
                registration.Cancel();
                if (isPrivate)
                    Futex.CancelWait(uaddr, waiter);
                else
                    Futex.CancelWaitShared(physKey, waiter);
                return -(int)Errno.EINVAL;
            }

            Logger.LogInformation(
                "[SysFutex WAIT] TID={TID} uaddr=0x{Uaddr:x} val={Val} isPrivate={IsPrivate} physKey=0x{PhysKey:x} WakeReason={WR} PendingSig=0x{PS:x}",
                task.TID, uaddr, val, isPrivate, physKey, task.WakeReason, task.PendingSignals);
            var result = await new FutexAwaitable(waiter, task, registration);
            Logger.LogInformation(
                "[SysFutex WAIT] TID={TID} awaiter result={Result} WakeReason={WR} PendingSig=0x{PS:x}",
                task.TID, result, task.WakeReason, task.PendingSignals);
            if (result == AwaitResult.Interrupted)
            {
                if (isPrivate)
                    Futex.CancelWait(uaddr, waiter);
                else
                    Futex.CancelWaitShared(physKey, waiter);
                return -(int)Errno.ERESTARTSYS;
            }

            return 0;
        }

        nint sharedKey = 0;
        if (!isPrivate)
        {
            if (opCode == 1)
                engine.CopyFromUser(uaddr, new byte[1]);

            var hostPtr = engine.GetPhysicalAddressSafe(uaddr, false);
            sharedKey = hostPtr != IntPtr.Zero ? hostPtr : 0;
        }

        if (opCode == 1) // WAKE
        {
            var count = (int)val;
            if (isPrivate) return Futex.Wake(uaddr, count);
            return sharedKey != 0
                ? Futex.WakeShared(sharedKey, count)
                : Futex.Wake(uaddr, count);
        }

        if (opCode is LinuxConstants.FUTEX_LOCK_PI or LinuxConstants.FUTEX_TRYLOCK_PI)
        {
            var task = engine.Owner as FiberTask;
            if (task == null) return -(int)Errno.EINVAL;

            while (true)
            {
                var futexWord = new byte[4];
                if (!engine.CopyFromUser(uaddr, futexWord)) return -(int)Errno.EFAULT;

                var currentVal = BinaryPrimitives.ReadUInt32LittleEndian(futexWord);
                var owner = currentVal & LinuxConstants.FUTEX_TID_MASK;
                var hasWaiters = (currentVal & LinuxConstants.FUTEX_WAITERS) != 0;

                if (owner == 0)
                {
                    var queuedWaiters = isPrivate
                        ? Futex.GetWaiterCount(uaddr)
                        : sharedKey != 0
                            ? Futex.GetWaiterCountShared(sharedKey)
                            : Futex.GetWaiterCount(uaddr);
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

                var waiter = isPrivate
                    ? Futex.PrepareWait(uaddr)
                    : sharedKey != 0
                        ? Futex.PrepareWaitShared(sharedKey)
                        : Futex.PrepareWait(uaddr);
                var registration = isPrivate
                    ? Futex.CreatePrivateWaitRegistration(uaddr, waiter)
                    : sharedKey != 0
                        ? Futex.CreateSharedWaitRegistration(sharedKey, waiter)
                        : Futex.CreatePrivateWaitRegistration(uaddr, waiter);

                Logger.LogTrace("[SysFutex LOCK_PI] TID={TID} waiting uaddr=0x{Uaddr:x} owner={Owner} isPrivate={IsPrivate} physKey=0x{PhysKey:x}",
                    task.TID, uaddr, owner, isPrivate, sharedKey);
                var result = await new FutexAwaitable(waiter, task, registration);
                if (result == AwaitResult.Interrupted)
                {
                    if (isPrivate)
                        Futex.CancelWait(uaddr, waiter);
                    else if (sharedKey != 0)
                        Futex.CancelWaitShared(sharedKey, waiter);
                    else
                        Futex.CancelWait(uaddr, waiter);
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

            var queuedWaiters = isPrivate
                ? Futex.GetWaiterCount(uaddr)
                : sharedKey != 0
                    ? Futex.GetWaiterCountShared(sharedKey)
                    : Futex.GetWaiterCount(uaddr);
            var nextVal = queuedWaiters > 0 ? LinuxConstants.FUTEX_WAITERS : 0u;
            BinaryPrimitives.WriteUInt32LittleEndian(futexWord, nextVal);
            if (!engine.CopyToUser(uaddr, futexWord)) return -(int)Errno.EFAULT;

            var woke = isPrivate
                ? Futex.Wake(uaddr, 1)
                : sharedKey != 0
                    ? Futex.WakeShared(sharedKey, 1)
                    : Futex.Wake(uaddr, 1);

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
