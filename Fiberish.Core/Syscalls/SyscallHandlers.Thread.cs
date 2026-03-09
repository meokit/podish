using System.Buffers.Binary;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.X86.Native;
using Microsoft.Extensions.Logging;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
#pragma warning disable CS1998 // Async method lacks await operators - syscall handlers require async signature
    private static async ValueTask<int> SysFutex(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.ENOSYS;

        var uaddr = a1;
        var op = (int)a2;
        var val = a3;

        var opCode = op & 0x7F;
        var isPrivate = (op & 0x80) != 0; // FUTEX_PRIVATE_FLAG = 128

        if (opCode == 0) // WAIT
        {
            // Read current value first — this also faults in the page
            var tidBuf = new byte[4];
            if (!sm.Engine.CopyFromUser(uaddr, tidBuf)) return -(int)Errno.EFAULT;
            var currentVal = BinaryPrimitives.ReadUInt32LittleEndian(tidBuf);
            if (currentVal != val) return -(int)Errno.EAGAIN; // EWOULDBLOCK

            // Resolve the futex key AFTER the page is faulted in
            nint physKey = 0;
            if (!isPrivate)
            {
                var hostPtr = sm.Engine.GetPhysicalAddressSafe(uaddr, false);
                if (hostPtr == IntPtr.Zero) return -(int)Errno.EFAULT;
                physKey = (nint)hostPtr;
            }

            var waiter = isPrivate
                ? sm.Futex.PrepareWait(uaddr)
                : sm.Futex.PrepareWaitShared(physKey);

            var task = sm.Engine.Owner as FiberTask;
            if (task == null)
            {
                if (isPrivate)
                    sm.Futex.CancelWait(uaddr, waiter);
                else
                    sm.Futex.CancelWaitShared(physKey, waiter);
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
                // Cancel the waiter to avoid leaking it in the queue
                if (isPrivate)
                    sm.Futex.CancelWait(uaddr, waiter);
                else
                    sm.Futex.CancelWaitShared(physKey, waiter);
                return -(int)Errno.ERESTARTSYS;
            }

            return 0;
        }

        // Resolve key for non-WAIT ops
        nint sharedKey = 0;
        if (!isPrivate)
        {
            if (opCode == 1) // WAKE
            {
                // Force a page fault to ensure the page is mapped in this process's engine,
                // so we can reliably get its physical address for the cross-process shared key.
                sm.Engine.CopyFromUser(uaddr, new byte[1]);
            }

            var hostPtr = sm.Engine.GetPhysicalAddressSafe(uaddr, false);
            // Fall back to virtual address key if not mapped (best-effort)
            sharedKey = hostPtr != IntPtr.Zero ? (nint)hostPtr : 0;
        }

        if (opCode == 1) // WAKE
        {
            var count = (int)val;
            if (isPrivate) return sm.Futex.Wake(uaddr, count);
            return sharedKey != 0
                ? sm.Futex.WakeShared(sharedKey, count)
                : sm.Futex.Wake(uaddr, count); // fallback: page not mapped here
        }

        return -(int)Errno.ENOSYS;
    }

    private readonly struct FutexAwaitable
    {
        private readonly Fiberish.Core.Waiter _waiter;
        private readonly FiberTask _task;

        public FutexAwaitable(Fiberish.Core.Waiter waiter, FiberTask task)
        {
            _waiter = waiter;
            _task = task;
        }

        public FutexAwaiter GetAwaiter() => new(_waiter, _task);
    }

    private readonly struct FutexAwaiter : System.Runtime.CompilerServices.INotifyCompletion
    {
        private readonly Fiberish.Core.Waiter _waiter;
        private readonly FiberTask _task;
        private readonly FiberTask.WaitToken _token;

        public FutexAwaiter(Fiberish.Core.Waiter waiter, FiberTask task)
        {
            _waiter = waiter;
            _task = task;
            _token = task.BeginWaitToken();
        }

        public bool IsCompleted => _waiter.Tcs.Task.IsCompleted;

        public void OnCompleted(Action continuation)
        {
            var task = _task;
            var token = _token;
            var runOnce = new RunOnceAction(continuation, _task);

            var waiterAwaiter = _waiter.Tcs.Task.GetAwaiter();
            waiterAwaiter.OnCompleted(() =>
            {
                if (task.GetWaitReason(token) == WakeReason.None)
                {
                    task.TrySetWaitReason(token, WakeReason.Event);
                }

                runOnce.Invoke();
            });

            task.ArmSignalSafetyNet(token, () => runOnce.Invoke());
        }

        public AwaitResult GetResult()
        {
            var reason = _task.CompleteWaitToken(_token);
            if (reason != WakeReason.Event && reason != WakeReason.None)
            {
                _waiter.Tcs.TrySetResult(false);
                return AwaitResult.Interrupted;
            }

            if (_waiter.Tcs.Task.IsCompleted && !_waiter.Tcs.Task.Result)
            {
                return AwaitResult.Interrupted;
            }

            return AwaitResult.Completed;
        }

        private sealed class RunOnceAction
        {
            private readonly Action _action;
            private readonly FiberTask _task;
            private int _called;

            public RunOnceAction(Action action, FiberTask task)
            {
                _action = action;
                _task = task;
            }

            public void Invoke()
            {
                if (Interlocked.Exchange(ref _called, 1) == 0)
                {
                    _task.Continuation = _action;
                    _task.CommonKernel.Schedule(_task);
                }
            }
        }
    }

    private static async ValueTask<int> SysSetThreadArea(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var uInfoAddr = a1;
        var buf = new byte[16];
        if (!sm.Engine.CopyFromUser(uInfoAddr, buf)) return -(int)Errno.EFAULT;

        var entry = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(0, 4));
        var baseAddr = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4, 4));

        Logger.LogInformation($"[SysSetThreadArea] Entry={entry} Base={baseAddr:X}");

        sm.Engine.SetSegBase(Seg.GS, baseAddr);

        if (entry == 0xFFFFFFFF)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), 12);
            if (!sm.Engine.CopyToUser(uInfoAddr, buf.AsSpan(0, 4))) return -(int)Errno.EFAULT;
        }

        return 0;
    }

    private static async ValueTask<int> SysSetTidAddress(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        if (sm.Engine.Owner is FiberTask task)
        {
            task.ChildClearTidPtr = a1;
            return task.TID;
        }

        if (sm.GetTID != null) return sm.GetTID(sm.Engine);
        return 1;
    }

    private static async ValueTask<int> SysSetRobustList(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        if (sm.Engine.Owner is not FiberTask task) return -(int)Errno.EPERM;

        var head = a1;
        var len = a2;

        if (len != 12) return -(int)Errno.EINVAL; // 32-bit robust_list_head is exactly 12 bytes

        task.RobustListHead = head;
        task.RobustListSize = len;

        return 0;
    }

    private static async ValueTask<int> SysGetRobustList(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;

        var pid = (int)a1;
        var headPtr = a2;
        var lenPtr = a3;

        FiberTask? targetTask;
        if (pid == 0)
        {
            targetTask = sm.Engine.Owner as FiberTask;
        }
        else
        {
            targetTask = KernelScheduler.Current?.GetTask(pid);
            if (targetTask == null) return -(int)Errno.ESRCH;
        }

        if (targetTask == null) return -(int)Errno.ESRCH;

        var headBuf = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(headBuf, targetTask.RobustListHead);
        if (!sm.Engine.CopyToUser(headPtr, headBuf)) return -(int)Errno.EFAULT;

        if (lenPtr != 0)
        {
            var lenBuf = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(lenBuf, targetTask.RobustListSize);
            if (!sm.Engine.CopyToUser(lenPtr, lenBuf)) return -(int)Errno.EFAULT;
        }

        return 0;
    }
}
