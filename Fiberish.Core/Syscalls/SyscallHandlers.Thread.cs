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

        if (opCode == 0) // WAIT
        {
            var tidBuf = new byte[4];
            if (!sm.Engine.CopyFromUser(uaddr, tidBuf)) return -(int)Errno.EFAULT;
            var currentVal = BinaryPrimitives.ReadUInt32LittleEndian(tidBuf);
            if (currentVal != val) return -(int)Errno.EAGAIN; // EWOULDBLOCK

            var waiter = sm.Futex.PrepareWait(uaddr);

            var task = sm.Engine.Owner as FiberTask;
            if (task != null)
            {
                if (await new FutexAwaiter(waiter, task) == AwaitResult.Interrupted)
                    return -(int)Errno.ERESTARTSYS;
            }

            return 0;
        }

        if (opCode == 1) // WAKE
        {
            var count = (int)val;
            return sm.Futex.Wake(uaddr, count);
        }

        return -(int)Errno.ENOSYS;
    }

    private sealed class FutexAwaiter : System.Runtime.CompilerServices.INotifyCompletion
    {
        private readonly Fiberish.Core.Waiter _waiter;
        private readonly FiberTask _task;

        public FutexAwaiter(Fiberish.Core.Waiter waiter, FiberTask task)
        {
            _waiter = waiter;
            _task = task;
        }

        public bool IsCompleted => _waiter.Tcs.Task.IsCompleted;

        public void OnCompleted(Action continuation)
        {
            if (_task.HasUnblockedPendingSignal())
            {
                _task.WakeReason = WakeReason.Signal;
                KernelScheduler.Current?.Schedule(continuation, _task);
                return;
            }

            var runOnce = new RunOnceAction(continuation, _task);

            _task.Continuation = runOnce.Invoke;

            _waiter.Tcs.Task.ContinueWith(_ =>
            {
                if (_task.WakeReason == WakeReason.None)
                {
                    _task.WakeReason = WakeReason.Event;
                }
                runOnce.Invoke();
            });
        }

        public AwaitResult GetResult()
        {
            if (_task.WakeReason != WakeReason.Event && _task.WakeReason != WakeReason.None)
            {
                _waiter.Tcs.TrySetResult(false);
                _task.WakeReason = WakeReason.None;
                return AwaitResult.Interrupted;
            }
            if (_waiter.Tcs.Task.IsCompleted && !_waiter.Tcs.Task.Result)
            {
                return AwaitResult.Interrupted;
            }

            _task.WakeReason = WakeReason.None;
            return AwaitResult.Completed;
        }

        public FutexAwaiter GetAwaiter() => this;

        private class RunOnceAction
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
                    KernelScheduler.Current?.Schedule(_task);
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
        if (sm.GetTID != null) return sm.GetTID(sm.Engine);
        return 1;
    }

    private static async ValueTask<int> SysSetRobustList(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
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

    private static async ValueTask<int> SysGetRobustList(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
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