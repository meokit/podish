using System.Buffers.Binary;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.X86.Native;
using Microsoft.Extensions.Logging;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
#pragma warning disable CS1998 // Async method lacks await operators - syscall handlers require async signature
    private async ValueTask<int> SysSignal(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        if (engine.Owner is not FiberTask task) return -(int)Errno.EPERM;

        var sig = (int)a1;
        var handler = a2;
        if (sig < 1 || sig > 64) return -(int)Errno.EINVAL;
        if (sig == (int)Signal.SIGKILL || sig == (int)Signal.SIGSTOP) return -(int)Errno.EINVAL;

        task.Process.SignalActions.TryGetValue(sig, out var oldAction);

        // signal(2) compatibility shim: map to our sigaction storage.
        // Keep restart-friendly behavior for legacy callers.
        var newAction = new SigAction
        {
            Handler = handler,
            Flags = LinuxConstants.SA_RESTART,
            Restorer = 0,
            Mask = 0
        };
        task.Process.SignalActions[sig] = newAction;

        return unchecked((int)oldAction.Handler);
    }

    private async ValueTask<int> SysRtSigAction(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        // a1: sig, a2: new_sa, a3: old_sa, a4: sigsetsize
        var sig = (int)a1;
        var newSaPtr = a2;
        var oldSaPtr = a3;
        var sigsetsize = a4;

        if (sigsetsize != 8) return -(int)Errno.EINVAL;

        var task = engine.Owner as FiberTask;
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
                if (!engine.CopyToUser(oldSaPtr, buf)) return -(int)Errno.EFAULT;
            }
            else
            {
                if (!engine.CopyToUser(oldSaPtr, new byte[20])) return -(int)Errno.EFAULT;
            }
        }

        if (newSaPtr != 0)
        {
            if (sig == (int)Signal.SIGKILL || sig == (int)Signal.SIGSTOP)
                return -(int)Errno.EINVAL; // Cannot catch SIGKILL or SIGSTOP

            var buf = new byte[20];
            if (!engine.CopyFromUser(newSaPtr, buf)) return -(int)Errno.EFAULT;
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

    private async ValueTask<int> SysRtSigProcMask(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        // a1: how, a2: set, a3: oldset, a4: sigsetsize
        var how = (int)a1;
        var setPtr = a2;
        var oldSetPtr = a3;
        var sigsetsize = a4;

        if (sigsetsize != 8) return -(int)Errno.EINVAL;

        var task = engine.Owner as FiberTask;
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
                    // After unblocking, re-check if any pending signals became deliverable
                    CheckAndTriggerPendingSignals(task);
                    break;
                case (int)SigProcMaskAction.SIG_SETMASK:
                    task.SignalMask = set;
                    // After changing mask, re-check if any pending signals became deliverable
                    CheckAndTriggerPendingSignals(task);
                    break;
                default:
                    return -(int)Errno.EINVAL;
            }
        }

        return 0;
    }

    private static void CheckAndTriggerPendingSignals(FiberTask task)
    {
        var unblocked = task.GetVisiblePendingSignals() & ~task.SignalMask;
        if (unblocked != 0)
            // Find the first unblocked pending signal and mark as interrupting
            // so the guest execution loop will pick it up after the syscall returns
            for (var i = 1; i <= 64; i++)
                if ((unblocked & (1UL << (i - 1))) != 0)
                {
                    task.NotifyPendingSignal(i);
                    break;
                }
    }

    private async ValueTask<int> SysSigPending(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        if (engine.Owner is not FiberTask task) return -(int)Errno.EPERM;

        var buf = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)task.GetVisiblePendingSignals());
        return engine.CopyToUser(a1, buf) ? 0 : -(int)Errno.EFAULT;
    }

    private async ValueTask<int> SysRtSigPending(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        if (engine.Owner is not FiberTask task) return -(int)Errno.EPERM;
        if (a2 != 8) return -(int)Errno.EINVAL;

        var buf = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buf, task.GetVisiblePendingSignals());
        return engine.CopyToUser(a1, buf) ? 0 : -(int)Errno.EFAULT;
    }

    private async ValueTask<int> SysKill(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var task = engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        var pid = (int)a1;
        var sig = (int)a2;

        if (sig < 0 || sig > 64) return -(int)Errno.EINVAL;
        var kernel = task.CommonKernel;
        var delivered = 0;

        if (pid > 0)
        {
            if (kernel.GetProcess(pid) == null) return -(int)Errno.ESRCH;
            if (sig != 0 && kernel.SignalProcess(pid, sig)) delivered = 1;
            return 0;
        }

        if (pid == 0)
        {
            if (!kernel.ProcessGroupExists(task.Process.PGID)) return -(int)Errno.ESRCH;
            if (sig != 0) delivered = kernel.SignalProcessGroupWithCount(task.Process.PGID, sig);
            return 0;
        }

        if (pid == -1)
        {
            if (sig != 0) delivered = kernel.SignalAllProcesses(sig, task.Process.TGID);
            if (sig != 0 && delivered == 0) return -(int)Errno.ESRCH;
            return 0;
        }

        var pgid = -pid;
        if (!kernel.ProcessGroupExists(pgid)) return -(int)Errno.ESRCH;
        if (sig != 0) delivered = kernel.SignalProcessGroupWithCount(pgid, sig);
        if (sig != 0 && delivered == 0) return -(int)Errno.ESRCH;
        return 0;
    }

    private async ValueTask<int> SysTkill(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        if (engine.Owner is not FiberTask task) return -(int)Errno.EPERM;

        var tid = (int)a1;
        var sig = (int)a2;

        if (tid <= 0) return -(int)Errno.EINVAL;
        if (sig < 0 || sig > 64) return -(int)Errno.EINVAL;

        var kernel = (engine.Owner as FiberTask)!.CommonKernel;
        var target = kernel.GetTask(tid);
        if (target == null) return -(int)Errno.ESRCH;

        if (sig != 0) target.PostSignal(sig);

        return 0;
    }

    private async ValueTask<int> SysTgkill(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        // int tgid = (int)a1; // Not used yet?
        // int tid = (int)a2;
        // int sig = (int)a3;

        var tgid = (int)a1;
        var tid = (int)a2;
        var sig = (int)a3;

        if (tgid <= 0 || tid <= 0) return -(int)Errno.EINVAL;
        if (sig < 0 || sig > 64) return -(int)Errno.EINVAL;

        var kernel = (engine.Owner as FiberTask)!.CommonKernel;
        var target = kernel.GetTask(tid);
        if (target == null) return -(int)Errno.ESRCH;
        if (target.Process.TGID != tgid) return -(int)Errno.ESRCH;

        if (sig != 0) target.PostSignal(sig);
        return 0;
    }

    private async ValueTask<int> SysSigReturn(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
    {
        var task = engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        var sp = task.CPU.RegRead(Reg.ESP);

        // On i386 sigreturn, ESP points to the saved sigcontext
        // (after popl %eax which was done in __restore)
        task.RestoreSigContext(sp);
        task.RestoreDeferredSignalMaskIfAny();

        return (int)task.CPU.RegRead(Reg.EAX);
    }

    private async ValueTask<int> SysRtSigReturn(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var task = engine.Owner as FiberTask;
        if (task == null) return -(int)Errno.EPERM;

        var sp = task.CPU.RegRead(Reg.ESP);

        uint ucontextAddr;
        // Standard i386 rt frame: [esp]=sig, [esp+4]=siginfo*, [esp+8]=ucontext*
        var ptrBuf = new byte[4];
        if (!task.CPU.CopyFromUser(sp + 8, ptrBuf)) return -(int)Errno.EFAULT;
        ucontextAddr = BinaryPrimitives.ReadUInt32LittleEndian(ptrBuf);

        // Compatibility fallback for legacy handlers that accidentally shifted stack by one arg.
        if (ucontextAddr < 0x1000)
        {
            if (!task.CPU.CopyFromUser(sp + 4, ptrBuf)) return -(int)Errno.EFAULT;
            ucontextAddr = BinaryPrimitives.ReadUInt32LittleEndian(ptrBuf);
        }

        // Restore
        // ucontext.mcontext is at offset 20
        task.RestoreSigContext(ucontextAddr + 20);

        // RT signals have full 64-bit signal mask in ucontext (offset 20 + 88 roughly, wait: ucontext layout is different)
        // Normal i386 ucontext:
        // uc_flags (4)
        // uc_link (4)
        // uc_stack (12)
        // uc_mcontext (88)
        // uc_sigmask (128 bytes, but we only use 8) -> offset 108
        var maskBuf = new byte[8];
        if (task.CPU.CopyFromUser(ucontextAddr + 108, maskBuf))
            task.SignalMask = BinaryPrimitives.ReadUInt64LittleEndian(maskBuf);
        task.RestoreDeferredSignalMaskIfAny();

        return (int)task.CPU.RegRead(Reg.EAX);
    }

    private async ValueTask<int> SysRtSigSuspend(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var task = engine.Owner as FiberTask;
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
        task.DeferSignalMaskRestore(oldMask);

        // Log
        if (Strace) Logger.LogTrace(" [rt_sigsuspend] Mask set to {Mask:X}, waiting...", mask);

        // Pre-set EAX to -EINTR so if signal handler runs (even with SA_RESTART), it saves -EINTR
        task.CPU.RegWrite(Reg.EAX, unchecked((uint)-(int)Errno.EINTR));

        var rc = await SysPause(engine, 0, 0, 0, 0, 0, 0); // Reuse SysPause which now uses PauseAwaiter
        if (rc >= 0) task.RestoreDeferredSignalMaskIfAny();
        return rc;
    }

    private async ValueTask<int> SysRtSigQueueInfo(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var task = engine.Owner as FiberTask;

        var pid = (int)a1;
        var sig = (int)a2;
        var uinfoPtr = a3;

        if (sig < 0 || sig > 64) return -(int)Errno.EINVAL;

        var info = ReadSigInfo(engine, uinfoPtr, sig);

        var kernel = task?.CommonKernel !;
        if (sig != 0 && !kernel.SignalProcessInfo(pid, sig, info)) return -(int)Errno.ESRCH;
        return 0;
    }

    private async ValueTask<int> SysRtTgSigQueueInfo(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6)
    {
        var tgid = (int)a1;
        var tid = (int)a2;
        var sig = (int)a3;
        var uinfoPtr = a4;

        if (sig < 0 || sig > 64) return -(int)Errno.EINVAL;

        var info = ReadSigInfo(engine, uinfoPtr, sig);

        var task = (FiberTask)engine.Owner!;
        var kernel = task.CommonKernel;
        var targetTask = kernel.GetTask(tid);
        if (targetTask == null) return -(int)Errno.ESRCH;
        if (targetTask.Process.TGID != tgid) return -(int)Errno.ESRCH;

        if (sig != 0) targetTask.PostSignalInfo(info);
        return 0;
    }

    private static SigInfo ReadSigInfo(Engine engine, uint ptr, int defaultSig)
    {
        if (ptr == 0) return new SigInfo { Signo = defaultSig, Code = 0 };

        var buf = new byte[24];
        if (!engine.CopyFromUser(ptr, buf))
            return new SigInfo { Signo = defaultSig, Code = 0 };

        var signo = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(0, 4));
        var errno = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(4, 4));
        var code = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(8, 4));
        var pid = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(12, 4));
        var uid = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(16, 4));
        var value = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(20, 4));

        return new SigInfo
        {
            Signo = signo != 0 ? signo : defaultSig,
            Errno = errno,
            Code = code,
            Pid = pid,
            Uid = uid,
            Value = (ulong)value
        };
    }
#pragma warning restore CS1998
}