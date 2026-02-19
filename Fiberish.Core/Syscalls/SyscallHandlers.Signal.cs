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
    private static async ValueTask<int> SysSignal(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        return 0;
    }

    private static async ValueTask<int> SysRtSigAction(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        // a1: sig, a2: new_sa, a3: old_sa, a4: sigsetsize
        var sig = (int)a1;
        var newSaPtr = a2;
        var oldSaPtr = a3;
        var sigsetsize = a4;

        if (sigsetsize != 8) return -(int)Errno.EINVAL;

        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var task = sm.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        if (sig < 1 || sig > 64) return -(int)Errno.EINVAL;

        // Save old action
        if (oldSaPtr != 0)
        {
            if (task.Process.SignalActions.TryGetValue(sig, out var oldSa))
            {
                var buf = new byte[20];
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), oldSa.Handler);
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), oldSa.Flags);
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8, 4), oldSa.Restorer);
                BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(12, 8), oldSa.Mask);
                if (!sm.Engine.CopyToUser(oldSaPtr, buf)) return -(int)Errno.EFAULT;
            }
            else
            {
                if (!sm.Engine.CopyToUser(oldSaPtr, new byte[20])) return -(int)Errno.EFAULT;
            }
        }

        if (newSaPtr != 0)
        {
            if (sig == 9 || sig == 19) return -(int)Errno.EINVAL; // Cannot catch SIGKILL or SIGSTOP

            var buf = new byte[20];
            if (!sm.Engine.CopyFromUser(newSaPtr, buf)) return -(int)Errno.EFAULT;
            var sa = new SigAction
            {
                Handler = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(0, 4)),
                Flags = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4, 4)),
                Restorer = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(8, 4)),
                Mask = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(12, 8))
            };
            task.Process.SignalActions[sig] = sa;
        }

        return 0;
    }

    private static async ValueTask<int> SysRtSigProcMask(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        // a1: how, a2: set, a3: oldset, a4: sigsetsize
        var how = (int)a1;
        var setPtr = a2;
        var oldSetPtr = a3;
        var sigsetsize = a4;

        if (sigsetsize != 8) return -(int)Errno.EINVAL;

        var task = sm.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        if (oldSetPtr != 0)
        {
            var buf = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(buf, task.SignalMask);
            if (!task.CPU.CopyToUser(oldSetPtr, buf)) return -(int)Errno.EFAULT;
        }

        if (setPtr != 0)
        {
            var setBuf = new byte[8];
            if (!task.CPU.CopyFromUser(setPtr, setBuf)) return -(int)Errno.EFAULT;
            var set = BinaryPrimitives.ReadUInt64LittleEndian(setBuf);

            // SIGKILL and SIGSTOP cannot be blocked
            set &= ~(1UL << 8); // SIGKILL (9) - 1 bit shift
            set &= ~(1UL << 18); // SIGSTOP (19)

            switch (how)
            {
                case (int)SigProcMaskAction.SIG_BLOCK:
                    task.SignalMask |= set;
                    break;
                case (int)SigProcMaskAction.SIG_UNBLOCK:
                    task.SignalMask &= ~set;
                    break;
                case (int)SigProcMaskAction.SIG_SETMASK:
                    task.SignalMask = set;
                    break;
                default:
                    return -(int)Errno.EINVAL;
            }
        }

        return 0;
    }

    private static async ValueTask<int> SysKill(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        var task = sm.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        var pid = (int)a1;
        var sig = (int)a2;

        if (sig < 0 || sig > 64) return -(int)Errno.EINVAL;

        // Simplified: only support current process/group for now or direct PID match
        // Kill 0: current process group. Kill -1: all processes. Kill < -1: process group -pid.

        List<FiberTask> targets = [];

        if (pid > 0)
        {
            lock (task.Process.Threads)
            {
                if (task.Process.Threads.Count > 0) targets.Add(task.Process.Threads[0]);
            }
        }
        else if (pid == -1)
        {
            // All processes. Dangerous!
            return -(int)Errno.EPERM;
        }
        else // pid < -1: PGRP = -pid
        {
            // Not implemented PGRP signaling yet.
            return -(int)Errno.ESRCH;
        }

        if (targets.Count == 0) return -(int)Errno.ESRCH;

        foreach (var t in targets) t.PostSignal(sig);

        return 0;
    }

    private static async ValueTask<int> SysTkill(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        if (sm.Engine.Owner is not FiberTask task) return -(int)Errno.EPERM;

        var tid = (int)a1;
        var sig = (int)a2;

        if (sig < 0 || sig > 64) return -(int)Errno.EINVAL;

        var target = KernelScheduler.Current!.GetTask(tid);
        if (target == null) return -(int)Errno.ESRCH;

        if (sig != 0) target.PostSignal(sig);

        return 0;
    }

    private static async ValueTask<int> SysTgkill(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        if (sm == null) return -(int)Errno.EPERM;
        // int tgid = (int)a1; // Not used yet?
        // int tid = (int)a2;
        // int sig = (int)a3;

        var tgid = (int)a1;
        var tid = (int)a2;
        var sig = (int)a3;

        var target = KernelScheduler.Current!.GetTask(tid);
        if (target == null) return -(int)Errno.ESRCH;
        if (target.Process.TGID != tgid && tgid != -1) return -(int)Errno.ESRCH;

        if (sig != 0) target.PostSignal(sig);
        return 0;
    }

    private static async ValueTask<int> SysSigReturn(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var sm = Get(state);
        var task = sm?.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        var sp = task.CPU.RegRead(Reg.ESP);

        // On i386 sigreturn, ESP points to the saved sigcontext
        // (after popl %eax which was done in __restore)
        task.RestoreSigContext(sp);

        return (int)task.CPU.RegRead(Reg.EAX);
    }

    private static async ValueTask<int> SysRtSigReturn(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var sm = Get(state);
        var task = sm?.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        var sp = task.CPU.RegRead(Reg.ESP);

        // Heuristic to detect if Arg1 (sig) was popped by handler (e.g. legacy handler doing 'pop' or ret 4?)
        // Layout 1 (Standard): [ESP]=Sig, [ESP+4]=SigInfo*, [ESP+8]=UContext*
        // Layout 2 (Shifted):  [ESP]=SigInfo*, [ESP+4]=UContext*

        var spBuf = new byte[4];
        if (!task.CPU.CopyFromUser(sp, spBuf)) return -(int)Errno.EFAULT;
        var val0 = BinaryPrimitives.ReadUInt32LittleEndian(spBuf);
        uint ucontextAddr;

        if (val0 > 0x1000) // Likely a pointer (SigInfo*) -> Shifted stack
        {
            // ESP points to Arg2
            // Arg3 (UContext*) is at ESP+4
            var ptrBuf = new byte[4];
            if (!task.CPU.CopyFromUser(sp + 4, ptrBuf)) return -(int)Errno.EFAULT;
            ucontextAddr = BinaryPrimitives.ReadUInt32LittleEndian(ptrBuf);
        }
        else // Likely a small int (Sig) -> Standard stack
        {
            // ESP points to Arg1
            // Arg3 (UContext*) is at ESP+8
            var ptrBuf = new byte[4];
            if (!task.CPU.CopyFromUser(sp + 8, ptrBuf)) return -(int)Errno.EFAULT;
            ucontextAddr = BinaryPrimitives.ReadUInt32LittleEndian(ptrBuf);
        }

        // Restore
        // ucontext.mcontext is at offset 20
        task.RestoreSigContext(ucontextAddr + 20);

        return (int)task.CPU.RegRead(Reg.EAX);
    }

    private static async ValueTask<int> SysRtSigSuspend(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var sm = Get(state);
        var task = sm?.Engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        var maskPtr = a1;
        var sigsetsize = a2;

        if (sigsetsize != 8) return -(int)Errno.EINVAL;

        var maskBuf = new byte[8];
        if (!task.CPU.CopyFromUser(maskPtr, maskBuf)) return -(int)Errno.EFAULT;
        var mask = BinaryPrimitives.ReadUInt64LittleEndian(maskBuf);

        // SIGKILL and SIGSTOP cannot be blocked
        mask &= ~(1UL << 8); // SIGKILL (9)
        mask &= ~(1UL << 18); // SIGSTOP (19)

        var oldMask = task.SignalMask;
        task.SignalMask = mask;

        // Log
        if (sm is { Strace: true }) Logger.LogTrace(" [rt_sigsuspend] Mask set to {Mask:X}, waiting...", mask);

        // Pre-set EAX to -EINTR so if signal handler runs (even with SA_RESTART), it saves -EINTR
        task.CPU.RegWrite(Reg.EAX, unchecked((uint)-(int)Errno.EINTR));

        // Check for pending signals immediately (unblocked by new mask)
        bool hasPending = false;
        lock (task)
        {
            if ((task.PendingSignals & ~mask) != 0) hasPending = true;
        }

        if (hasPending)
        {
            task.SignalMask = oldMask;
            return -(int)Errno.EINTR;
        }

        var token = task.CreateSyscallToken();

        try
        {
            await Task.Delay(-1, token);
        }
        catch (OperationCanceledException)
        {
            // Expected interruption by signal
        }
        finally
        {
            task.SignalMask = oldMask;
            if (sm is { Strace: true }) Logger.LogTrace(" [rt_sigsuspend] Restored mask to {Mask:X}", oldMask);
            task.DisposeSyscallToken();
        }

        return -(int)Errno.EINTR;
    }
}