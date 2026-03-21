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

            var task = engine.Owner as FiberTask;
            if (task == null)
            {
                if (isPrivate)
                    Futex.CancelWait(uaddr, waiter);
                else
                    Futex.CancelWaitShared(physKey, waiter);
                return -(int)Errno.EINVAL;
            }

            Logger.LogInformation(
                "[SysFutex WAIT] TID={TID} uaddr=0x{Uaddr:x} val={Val} isPrivate={IsPrivate} physKey=0x{PhysKey:x} WakeReason={WR} PendingSig=0x{PS:x}",
                task.TID, uaddr, val, isPrivate, physKey, task.WakeReason, task.PendingSignals);
            var result = await new FutexAwaitable(waiter, task);
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

        return -(int)Errno.ENOSYS;
    }

    private readonly struct FutexAwaitable
    {
        private readonly Waiter _waiter;
        private readonly FiberTask _task;

        public FutexAwaitable(Waiter waiter, FiberTask task)
        {
            _waiter = waiter;
            _task = task;
        }

        public FutexAwaiter GetAwaiter()
        {
            return new FutexAwaiter(_waiter, _task);
        }
    }

    private readonly struct FutexAwaiter : INotifyCompletion
    {
        private readonly Waiter _waiter;
        private readonly FiberTask _task;
        private readonly FiberTask.WaitToken _token;

        public FutexAwaiter(Waiter waiter, FiberTask task)
        {
            _waiter = waiter;
            _task = task;
            _token = task.BeginWaitToken();
        }

        public bool IsCompleted => _waiter.Tcs.Task.IsCompleted;

        public void OnCompleted(Action continuation)
        {
            var handler = new FutexCompletionHandler(_task, _token, continuation);
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
            private readonly Action _continuation;
            private readonly FiberTask _task;
            private readonly FiberTask.WaitToken _token;
            private int _called;

            public FutexCompletionHandler(FiberTask task, FiberTask.WaitToken token, Action continuation)
            {
                _task = task;
                _token = token;
                _continuation = continuation;
            }

            public void OnWaitCompleted()
            {
                if (Interlocked.Exchange(ref _called, 1) == 0)
                {
                    if (_task.GetWaitReason(_token) == WakeReason.None)
                        _task.TrySetWaitReason(_token, WakeReason.Event);

                    _task.CommonKernel.ScheduleContinuation(_continuation, _task, WaitContinuationMode.ResumeTask);
                }
            }

            public void OnSignal()
            {
                if (Interlocked.Exchange(ref _called, 1) == 0)
                    _task.CommonKernel.ScheduleContinuation(_continuation, _task, WaitContinuationMode.ResumeTask);
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
