using System.Buffers.Binary;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.X86.Native;
using Fiberish.Core.Utils;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;

namespace Fiberish.Core;

public enum FiberTaskStatus
{
    Ready,
    Running,
    Waiting,
    Zombie,
    Terminated
}

public enum AwaitResult
{
    Completed,
    Interrupted
}

public enum TaskExecutionMode
{
    RunningGuest,
    WaitingAsyncSyscall,
    WaitingContinuation,
    Stopped,
    Terminated
}

public enum WakeReason
{
    None,
    Signal,
    Timer,
    IO,
    Event
}

public class FiberTask
{
    private enum DefaultSignalAction
    {
        Ignore,
        Terminate,
        Core,
        Stop,
        Continue
    }

    private static int _tidCounter = 1000;

    public uint SyscallArg1;
    public uint SyscallArg2;
    public uint SyscallArg3;

    // Saved Syscall Arguments for Async Tracing
    public uint SyscallNr;

    public FiberTask(int tid, Process process, Engine cpu, KernelScheduler kernel)
    {
        TID = tid;
        PID = process.TGID;
        Process = process;
        lock (Process.Threads)
        {
            Process.Threads.Add(this);
        }

        CPU = cpu;
        CPU.Owner = this;
        CommonKernel = kernel;

        Logger = kernel.LoggerFactory.CreateLogger($"Fiberish.Task.{TID}");
        CPU.LogHandler = EngineLogHandler;

        kernel.RegisterTask(this);

        CPU.PageFaultResolver = HandlePageFault;

        CPU.InterruptHandler = HandleInterrupt;
        CPU.FaultHandler = HandleNativeFault;
    }

    private bool _pendingFaultFromInterrupt;

    public int TID { get; }
    public int PID { get; }
    public Engine CPU { get; }
    public KernelScheduler CommonKernel { get; }
    public Action? Continuation { get; set; }

    public FiberTaskStatus Status { get; set; } = FiberTaskStatus.Ready;

    public Process Process { get; }

    // Signal System
    public ulong SignalMask { get; set; }
    public ulong PendingSignals { get; set; } // Bitmask - Access should be synchronized
    public Locked<List<SigInfo>> PendingSignalQueue { get; } = new(new());
    public uint AltStackSp { get; set; }
    public uint AltStackSize { get; set; }
    public int AltStackFlags { get; set; }

    public bool Exited { get; set; }

    public int ExitStatus { get; set; }

    public uint ChildClearTidPtr { get; set; }

    public uint RobustListHead { get; set; }
    public uint RobustListSize { get; set; }

    // vfork support: parent awaits this event; child signals it on exec/exit
    public AsyncWaitQueue? VforkDoneEvent { get; set; }

    // vfork support: reference to the parent task that is blocked waiting for us
    public FiberTask? VforkParent { get; set; }

    public TaskExecutionMode ExecutionMode { get; set; } = TaskExecutionMode.RunningGuest;
    public WakeReason WakeReason { get; set; } = WakeReason.None;

    // Signal that interrupted the current blocking syscall (if any)
    // Used by syscall handlers to determine SA_RESTART behavior
    public int? InterruptingSignal { get; private set; }

    // Syscall restart support (SA_RESTART)
    // Stores the EIP of the syscall instruction for potential restart
    public uint SyscallEip { get; set; }

    public ILogger Logger { get; }

    // Blocking Syscall Support
    public Func<ValueTask<int>>? PendingSyscall { get; set; }
    public Timer? BlockingTimer { get; set; }

    public static int NextTID()
    {
        return Interlocked.Increment(ref _tidCounter);
    }

    private void EngineLogHandler(Engine engine, int level, string msg)
    {
        Logger.Log((LogLevel)level, "[Native] {Message}", msg);
    }

    private bool HandlePageFault(uint addr, bool isWrite)
    {
        if (Process.Mem.HandleFault(addr, isWrite, CPU))
            return true;

        // Not handled by VMAManager (no VMA or permission error)
        Logger.LogInformation("Page Fault at 0x{Addr:X} ({Mode}) could not be resolved. Posting SIGSEGV.",
            addr, isWrite ? "Write" : "Read");

        // Dump debug info
        var stats = CPU.DumpStats();
        Logger.LogInformation("CPU State: {CPU}", CPU.ToString());
        if (!string.IsNullOrEmpty(stats)) Logger.LogInformation("Native Stats:\n{Stats}", stats);

        var esp = CPU.RegRead(Reg.ESP);
        var stackBuf = new byte[16];
        if (CPU.CopyFromUser(esp, stackBuf))
        {
            var v0 = BinaryPrimitives.ReadUInt32LittleEndian(stackBuf.AsSpan(0, 4));
            var v1 = BinaryPrimitives.ReadUInt32LittleEndian(stackBuf.AsSpan(4, 4));
            var v2 = BinaryPrimitives.ReadUInt32LittleEndian(stackBuf.AsSpan(8, 4));
            var v3 = BinaryPrimitives.ReadUInt32LittleEndian(stackBuf.AsSpan(12, 4));
            Logger.LogInformation("Stack Dump at ESP=0x{Esp:X}: [0x{V0:X8}, 0x{V1:X8}, 0x{V2:X8}, 0x{V3:X8}]", esp, v0,
                v1, v2, v3);
        }
        else
        {
            Logger.LogInformation("Stack Dump at ESP=0x{Esp:X}: <Could not read stack>", esp);
        }

        Process.Mem.LogVMAs();

        // Deliver SIGSEGV and yield
        PostSignal((int)Signal.SIGSEGV);
        _pendingFaultFromInterrupt = true;
        CPU.Yield();
        return true; // Return true to C++ so it stops with Yield status instead of Fault status
    }

    private bool HandleInterrupt(Engine engine, uint vector)
    {
        Logger.LogTrace("[HandleInterrupt] vector={Vector}", vector);

        // 0x80 is Syscall
        if (vector == 0x80) return Process.Syscalls.Handle(engine, vector);

        // #DE (Divide Error) and #UD (Invalid Opcode)
        if (vector == 0 || vector == 6)
        {
            Logger.LogTrace("[HandleInterrupt] Caught fault vector {Vector}, yielding...", vector);
            _pendingFaultFromInterrupt = true;
            engine.Yield();
            return true;
        }

        return false;
    }

    private bool HandleNativeFault(Engine engine, uint addr, bool isWrite)
    {
        return HandlePageFault(addr, isWrite);
    }

    // RegisterBlockingSyscall removed

    public void PostSignal(int sig)
    {
        PostSignalInfo(new SigInfo { Signo = sig, Code = 0 /* SI_USER */, Pid = Process.TGID, Uid = 0 });
    }

    /// <summary>
    ///     Post a signal with full SigInfo payload to this task. Safe to call from any thread.
    ///     This sets the pending flag, queues the payload, and interrupts any blocking syscall.
    /// </summary>
    public void PostSignalInfo(SigInfo info)
    {
        int sig = info.Signo;
        if (sig < 1 || sig > 64) return;

        Logger.LogInformation("[PostSignalInfo] Posting signal {Sig}", sig);

        var mask = 1UL << (sig - 1);
        bool isIgnored = false;
        lock (this)
        {
            if (sig != 9 && sig != 19)
            {
                if (Process.SignalActions.TryGetValue(sig, out var action))
                {
                    if (action.Handler == 1) isIgnored = true; // SIG_IGN
                }
                else
                {
                    if (GetDefaultSignalAction(sig) == DefaultSignalAction.Ignore) isIgnored = true;
                }
            }

            // Immediately discard ignored signals if they shouldn't trigger wakeups
            if (isIgnored)
            {
                Logger.LogDebug("[PostSignalInfo] Signal {Sig} is ignored. Discarding.", sig);
                return;
            }

            Logger.LogDebug("[PostSignalInfo] Setting PendingSignals to {1} from {2}", PendingSignals | mask,
                PendingSignals);
            PendingSignals |= mask;

            PendingSignalQueue.Lock(q =>
            {
                if (sig < 32)
                {
                    bool found = false;
                    foreach (var s in q)
                    {
                        if (s.Signo == sig)
                        {
                            found = true;
                            break;
                        }
                    }

                    if (!found) q.Add(info);
                }
                else
                {
                    q.Add(info);
                }
            });
        }

        // Check if we should interrupt syscall
        // SIGKILL(9) and SIGSTOP(19) cannot be blocked
        var isBlocked = (SignalMask & mask) != 0;
        if (sig == (int)Signal.SIGKILL || sig == (int)Signal.SIGSTOP) isBlocked = false;

        if (!isBlocked && !isIgnored)
        {
            InterruptingSignal = sig;
            WakeReason = WakeReason.Signal;
        }
        else
        {
            Logger.LogDebug(
                "[PostSignal] Signal {Sig} received but currently masked by SignalMask (0x{Mask:X}). Added to pending.",
                sig, SignalMask);
        }

        if (Status == FiberTaskStatus.Waiting || Process.State == ProcessState.Stopped)
        {
            CommonKernel.Schedule(this);
        }
    }

    private void ProcessPendingSignals()
    {
        if (PendingSignals == 0) return;

        for (var i = 1; i <= 64; i++)
        {
            var mask = 1UL << (i - 1);
            SigAction action = default;
            bool hasAction = false;
            ulong oldMask = 0;

            SigInfo dequeuedInfo = default;

            lock (this)
            {
                if ((PendingSignals & mask) == 0) continue;

                // Check blocked
                if ((SignalMask & mask) != 0 && i != 9 && i != 19)
                {
                    Logger.LogDebug(
                        "[CheckPendingSignals] Signal {Sig} is currently blocked by SignalMask (0x{Mask:X})", i,
                        SignalMask);
                    continue;
                }

                // POP SIGNAL
                var nullableInfo = DequeueSignalUnsafe(i);
                if (nullableInfo.HasValue)
                {
                    dequeuedInfo = nullableInfo.Value;
                }
                else
                {
                    // Fallback if queue desynced
                    dequeuedInfo = new SigInfo { Signo = i, Code = 0 };
                    PendingSignals &= ~mask;
                }

                oldMask = SignalMask;
                hasAction = Process.SignalActions.TryGetValue(i, out action);

                // ATOMIC MASKING: Apply mask before delivering to guest
                // This prevents reentrancy if another signal arrives during stack setup
                if (hasAction && action.Handler > 1) // Not SIG_IGN (1) or SIG_DFL (0)
                {
                    var handlerMask = action.Mask;
                    if ((action.Flags & LinuxConstants.SA_NODEFER) == 0)
                    {
                        handlerMask |= mask;
                    }

                    Logger.LogDebug("[ProcessPendingSignals] Setting SignalMask to {Mask} from {SignalMask}",
                        SignalMask, SignalMask | handlerMask);
                    SignalMask |= handlerMask;
                }
            }

            DeliverSignal(i, action, hasAction, oldMask, dequeuedInfo);
            break; // Only one per slice
        }
    }

    public bool HasUnblockedPendingSignal()
    {
        lock (this)
        {
            // SIGKILL (9) and SIGSTOP (19) are never blocked
            var unblocked = PendingSignals & ~SignalMask;
            var hasUnmaskable = (PendingSignals & (1UL << 8)) != 0 || (PendingSignals & (1UL << 18)) != 0;
            return unblocked != 0 || hasUnmaskable;
        }
    }

    public bool IsSignalIgnoredOrBlocked(int sig)
    {
        if (sig < 1 || sig > 64) return false;
        // SIGKILL(9) and SIGSTOP(19) cannot be caught, blocked, or ignored
        if (sig == 9 || sig == 19) return false;

        var mask = 1UL << (sig - 1);
        lock (this)
        {
            if ((SignalMask & mask) != 0) return true; // Blocked
            if (Process.SignalActions.TryGetValue(sig, out var action))
            {
                if (action.Handler == 1) // SIG_IGN
                    return true;
            }
            else
            {
                if (GetDefaultSignalAction(sig) == DefaultSignalAction.Ignore)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Must be called under lock(this) to maintain atomic mask sync.
    /// </summary>
    public SigInfo? DequeueSignalUnsafe(int sig)
    {
        return PendingSignalQueue.Lock(list =>
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Signo == sig)
                {
                    var info = list[i];
                    list.RemoveAt(i);

                    // Update PendingSignals mask if no more RT signals of this type
                    if (sig >= 32)
                    {
                        bool stillPending = false;
                        foreach (var s in list)
                            if (s.Signo == sig)
                            {
                                stillPending = true;
                                break;
                            }

                        if (!stillPending) PendingSignals &= ~(1UL << (sig - 1));
                    }
                    else
                    {
                        PendingSignals &= ~(1UL << (sig - 1));
                    }

                    return (SigInfo?)info;
                }
            }

            return null;
        });
    }

    // Prepare stack frame for signal handler
    private void DeliverSignal(int sig, SigAction action, bool hasAction, ulong oldMask, SigInfo info)
    {
        Logger.LogInformation("[DeliverSignal] Delivering Signal {Sig}, EIP=0x{Eip:X}, ESP=0x{Esp:X}", sig, CPU.Eip,
            CPU.RegRead(Reg.ESP));

        // 1. Check if ignored
        if (hasAction)
        {
            if (action.Handler == 1) // SIG_IGN
            {
                Logger.LogInformation("[DeliverSignal] Signal {Sig} is ignored (SIG_IGN)", sig);
                return;
            }

            if (action.Handler == 0) // SIG_DFL
            {
                ApplyDefaultSignalAction(sig, GetDefaultSignalAction(sig));
                return;
            }

            // 2. Setup frame for handler
            Logger.LogInformation("[DeliverSignal] Setting up signal frame for signal {Sig}, handler=0x{Handler:X}",
                sig, action.Handler);

            // Push SigContext/StackFrame
            var sp = CPU.RegRead(Reg.ESP);

            // Check altstack
            if ((AltStackFlags & 1) == 0 && AltStackSp != 0 && (action.Flags & 0x08000000) != 0) // ONSTACK
            {
                Logger.LogInformation("[DeliverSignal] Using altstack: sp=0x{AltStackSp:X}+0x{AltStackSize:X}",
                    AltStackSp, AltStackSize);
                sp = AltStackSp + AltStackSize;
            }

            // Return address (restorer or some trampoline)
            var retAddr = Process.Syscalls.RtSigReturnAddr;
            if ((action.Flags & 0x04000000) != 0) // SA_RESTORER
            {
                retAddr = action.Restorer;
            }

            var frameEsp = sp;

            // SignalMask already updated in ProcessPendingSignals

            // Setup Stack based on SA_SIGINFO
            if ((action.Flags & 0x00000004) != 0) // SA_SIGINFO
            {
                if ((action.Flags & 0x04000000) == 0) retAddr = Process.Syscalls.RtSigReturnAddr;
                SetupSigContext(sp, ref frameEsp, sig, action, retAddr, oldMask, info);
            }
            else
            {
                if ((action.Flags & 0x04000000) == 0) retAddr = Process.Syscalls.SigReturnAddr;
                SetupOldSigFrame(sp, ref frameEsp, sig, action, retAddr, oldMask);
            }

            Logger.LogInformation(
                "[DeliverSignal] Signal frame setup complete: ESP changed from 0x{OldSp:X} to 0x{NewSp:X}, EIP set to 0x{Handler:X}",
                sp, frameEsp, action.Handler);
            CPU.RegWrite(Reg.ESP, frameEsp);
            CPU.Eip = action.Handler;
        }
        else
        {
            // No action registered = SIG_DFL
            Logger.LogInformation("[DeliverSignal] Signal {Sig} has no registered action (SIG_DFL)", sig);
            ApplyDefaultSignalAction(sig, GetDefaultSignalAction(sig));
        }
    }

    /// <summary>
    /// Consume a pending signal and deliver it. Used by HandleAsyncSyscall to
    /// deliver signals atomically with -ERESTARTSYS processing, mirroring Linux's
    /// do_signal(). Registers should already be adjusted (EIP rewound, EAX set)
    /// BEFORE calling this method, so the sigcontext captures the restart-ready state.
    /// </summary>
    private void DeliverSignalForRestart(int sig, SigAction action)
    {
        var mask = 1UL << (sig - 1);
        ulong oldMask;
        SigInfo info;

        lock (this)
        {
            oldMask = SignalMask;

            // Dequeue from pending queue (prevents ProcessPendingSignals from re-delivering)
            var dequeued = DequeueSignalUnsafe(sig);
            info = dequeued ?? new SigInfo { Signo = sig };

            // Apply handler mask (same logic as ProcessPendingSignals)
            if (action.Handler > 1)
            {
                var handlerMask = action.Mask;
                if ((action.Flags & LinuxConstants.SA_NODEFER) == 0)
                    handlerMask |= mask;
                SignalMask |= handlerMask;
            }
        }

        DeliverSignal(sig, action, hasAction: true, oldMask, info);
    }

    // Kept for compatibility if called externally (removed HandleSignal method name to avoid confusion, 
    // but KernelScheduler currently calls HandleSignal. We will update KernelScheduler next.)
    // Note: I renamed HandleSignal to DeliverSignal and made it private.
    // I added Signal(sig) as the public API.

    private static DefaultSignalAction GetDefaultSignalAction(int sig)
    {
        return sig switch
        {
            (int)Signal.SIGCHLD or (int)Signal.SIGURG or (int)Signal.SIGWINCH => DefaultSignalAction.Ignore,
            (int)Signal.SIGSTOP or (int)Signal.SIGTSTP or (int)Signal.SIGTTIN or (int)Signal.SIGTTOU =>
                DefaultSignalAction.Stop,
            (int)Signal.SIGCONT => DefaultSignalAction.Continue,
            (int)Signal.SIGQUIT or (int)Signal.SIGILL or (int)Signal.SIGTRAP or
                (int)Signal.SIGABRT or (int)Signal.SIGBUS or (int)Signal.SIGFPE or
                (int)Signal.SIGSEGV or (int)Signal.SIGXCPU or (int)Signal.SIGXFSZ or
                (int)Signal.SIGSYS => DefaultSignalAction.Core,
            _ => DefaultSignalAction.Terminate
        };
    }

    private void ApplyDefaultSignalAction(int sig, DefaultSignalAction action)
    {
        switch (action)
        {
            case DefaultSignalAction.Ignore:
                Logger.LogInformation("[DeliverSignal] Signal {Sig} default action: ignore", sig);
                break;
            case DefaultSignalAction.Terminate:
                Logger.LogInformation("[DeliverSignal] Signal {Sig} default action: terminate", sig);
                TerminateBySignal(sig, coreDumped: false);
                break;
            case DefaultSignalAction.Core:
                Logger.LogInformation("[DeliverSignal] Signal {Sig} default action: core/terminate", sig);
                TerminateBySignal(sig, coreDumped: true);
                break;
            case DefaultSignalAction.Stop:
                Logger.LogInformation("[DeliverSignal] Signal {Sig} default action: stop", sig);
                StopBySignal(sig);
                break;
            case DefaultSignalAction.Continue:
                Logger.LogInformation("[DeliverSignal] Signal {Sig} default action: continue", sig);
                ContinueBySignal();
                break;
        }
    }

    private void TerminateBySignal(int sig, bool coreDumped)
    {
        if (Exited) return;

        Exited = true;
        ExitStatus = 128 + sig;

        // Notify parent
        var ppid = Process.PPID;
        if (ppid > 0)
        {
            var parentTask = CommonKernel.GetTask(ppid);
            parentTask?.PostSignal((int)Signal.SIGCHLD);
        }

        // If main thread exits, entire process becomes zombie
        if (TID == Process.TGID)
        {
            ProcFsManager.OnProcessExit(Process.Syscalls, Process.TGID);
            Process.State = ProcessState.Zombie;
            Process.ExitStatus = ExitStatus;
            Process.ExitedBySignal = true;
            Process.TermSignal = sig;
            Process.CoreDumped = coreDumped;
            Process.HasWaitableStop = false;
            Process.HasWaitableContinue = false;
            Process.StateChangeEvent.Set();
        }
    }

    private void StopBySignal(int sig)
    {
        if (Process.State == ProcessState.Zombie || Process.State == ProcessState.Stopped) return;

        Process.State = ProcessState.Stopped;
        Process.HasWaitableStop = true;
        Process.StopSignal = sig;
        Process.HasWaitableContinue = false;
        Process.StateChangeEvent.Set();

        // Put the task in stopped state
        ExecutionMode = TaskExecutionMode.Stopped;
        Status = FiberTaskStatus.Waiting;

        // Wake up parent's wait4
        var ppid = Process.PPID;
        if (ppid > 0)
        {
            var parentTask = CommonKernel.GetTask(ppid);
            // Send SIGCHLD to parent
            parentTask?.PostSignal((int)Signal.SIGCHLD);
        }
    }

    private void ContinueBySignal()
    {
        // Even if not fully stopped, we might need to clear pending stop signals
        // Clear pending stop signals (SIGSTOP, SIGTSTP, SIGTTIN, SIGTTOU)
        var stopMask = (1UL << 18) | (1UL << 19) | (1UL << 20) | (1UL << 21);
        lock (this)
        {
            PendingSignals &= ~stopMask;
        }

        if (Process.State != ProcessState.Stopped) return;

        Process.State = ProcessState.Running;
        Process.HasWaitableContinue = true;
        Process.HasWaitableStop = false;
        Process.StateChangeEvent.Set();

        // Restore execution mode
        ExecutionMode = TaskExecutionMode.RunningGuest;

        var ppid = Process.PPID;
        if (ppid > 0)
        {
            var parentTask = CommonKernel.GetTask(ppid);
            parentTask?.PostSignal((int)Signal.SIGCHLD);
        }

        // Reschedule task
        CommonKernel.Schedule(this);
    }

    private void SetupOldSigFrame(uint sp, ref uint esp, int sig, SigAction action, uint retAddr, ulong oldMask)
    {
        // Traditional sigframe:
        // esp+0: retAddr
        // esp+4: sig
        // esp+8: sigcontext
        // sizeof(sigframe) = 732

        esp = (esp - 736u) & ~0xFu;
        var sigcontextAddr = esp + 8;

        // Zero out the frame
        CPU.CopyToUser(esp, new byte[736]);

        WriteSigContext(sigcontextAddr, oldMask);

        if (!CPU.CopyToUser(esp + 4, BitConverter.GetBytes(sig))) return;
        if (!CPU.CopyToUser(esp, BitConverter.GetBytes(retAddr))) return;
    }

    private void SetupSigContext(uint sp, ref uint esp, int sig, SigAction action, uint retAddr, ulong oldMask,
        SigInfo info)
    {
        // ... (existing stack alignment)
        esp = (esp - 512u) & ~0xFu;

        var ucontextAddr = esp;
        uint mcontextOffset = 4 + 4 + 12;

        WriteSigContext(esp + mcontextOffset, oldMask);

        esp = (esp - 128u) & ~0xFu;
        var siginfoAddr = esp;

        // Populate SigInfo (128 bytes)
        var siBuf = new byte[128];
        BinaryPrimitives.WriteInt32LittleEndian(siBuf.AsSpan(0, 4), info.Signo);
        BinaryPrimitives.WriteInt32LittleEndian(siBuf.AsSpan(4, 4), info.Errno);
        BinaryPrimitives.WriteInt32LittleEndian(siBuf.AsSpan(8, 4), info.Code);

        // Payload (e.g. Pid/Uid for SI_USER, or TimerId for POSIX Timer)
        BinaryPrimitives.WriteInt32LittleEndian(siBuf.AsSpan(12, 4), info.Pid);
        BinaryPrimitives.WriteUInt32LittleEndian(siBuf.AsSpan(16, 4), info.Uid);

        // Write the unionized payload value (e.g. SIGRT)
        BinaryPrimitives.WriteUInt64LittleEndian(siBuf.AsSpan(20, 8), info.Value);

        if (!CPU.CopyToUser(siginfoAddr, siBuf)) return;

        // Populate UContext uc_sigmask (offset 108)
        var maskBuf = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(maskBuf, oldMask);
        if (!CPU.CopyToUser(ucontextAddr + 108, maskBuf)) return;

        // Align stack for arguments
        esp = (esp - 4u) & ~0xFu;

        // Push 3 args: sig, siginfo_ptr, ucontext_ptr
        if (!CPU.CopyToUser(esp - 4, BitConverter.GetBytes(ucontextAddr))) return;
        if (!CPU.CopyToUser(esp - 8, BitConverter.GetBytes(siginfoAddr))) return;
        if (!CPU.CopyToUser(esp - 12, BitConverter.GetBytes(sig))) return;
        esp -= 12u;

        // Push Return Address
        esp -= 4u;
        if (!CPU.CopyToUser(esp, BitConverter.GetBytes(retAddr))) return;
    }

    public void RestoreSigContext(uint addr)
    {
        /*
         * Reference: struct sigcontext_32 from Linux UAPI (arch/x86/include/uapi/asm/sigcontext.h)
         *
         * struct sigcontext_32 {
         *    __u16 gs, __gsh;          // 0
         *    __u16 fs, __fsh;          // 4
         *    __u16 es, __esh;          // 8
         *    __u16 ds, __dsh;          // 12
         *    __u32 di;                 // 16
         *    __u32 si;                 // 20
         *    __u32 bp;                 // 24
         *    __u32 sp;                 // 28
         *    __u32 bx;                 // 32
         *    __u32 dx;                 // 36
         *    __u32 cx;                 // 40
         *    __u32 ax;                 // 44
         *    __u32 trapno;             // 48
         *    __u32 err;                // 52
         *    __u32 ip;                 // 56
         *    __u16 cs, __csh;          // 60
         *    __u32 flags;              // 64
         *    __u32 sp_at_signal;       // 68 (UESP)
         *    __u16 ss, __ssh;          // 72
         *    __u32 fpstate;            // 76
         *    __u32 oldmask;            // 80
         *    __u32 cr2;                // 84
         * };
         */
        var buf = new byte[88];
        if (!CPU.CopyFromUser(addr, buf)) return;
        var s = new ReadOnlySpan<byte>(buf);

        // General Registers
        CPU.RegWrite(Reg.EDI, BinaryPrimitives.ReadUInt32LittleEndian(s[16..]));
        CPU.RegWrite(Reg.ESI, BinaryPrimitives.ReadUInt32LittleEndian(s[20..]));
        CPU.RegWrite(Reg.EBP, BinaryPrimitives.ReadUInt32LittleEndian(s[24..]));
        CPU.RegWrite(Reg.EBX, BinaryPrimitives.ReadUInt32LittleEndian(s[32..]));
        CPU.RegWrite(Reg.EDX, BinaryPrimitives.ReadUInt32LittleEndian(s[36..]));
        CPU.RegWrite(Reg.ECX, BinaryPrimitives.ReadUInt32LittleEndian(s[40..]));
        CPU.RegWrite(Reg.EAX, BinaryPrimitives.ReadUInt32LittleEndian(s[44..]));

        // IP, Flags, SP
        CPU.Eip = BinaryPrimitives.ReadUInt32LittleEndian(s[56..]);
        CPU.Eflags = BinaryPrimitives.ReadUInt32LittleEndian(s[64..]);
        CPU.RegWrite(Reg.ESP, BinaryPrimitives.ReadUInt32LittleEndian(s[68..])); // UESP

        // Restore signal mask
        if (buf.Length >= 88)
        {
            // Cheat: Restore full 64-bit mask from oldmask + cr2 area (80-87)
            // This is a convenient hack for our emulator to handle RT signals correctly in sigreturn.
            var oldSignalMask = SignalMask;
            SignalMask = BinaryPrimitives.ReadUInt64LittleEndian(s[80..]);
            Logger.LogDebug("[RestoreSigContext] Restored SignalMask {SignalMask}, before {OldSignalMask}", SignalMask,
                oldSignalMask);
        }
    }

    private void WriteSigContext(uint addr, ulong oldMask)
    {
        /*
         * Reference: struct sigcontext_32 from Linux UAPI (arch/x86/include/uapi/asm/sigcontext.h)
         * (See documentation in RestoreSigContext for full layout)
         */
        try
        {
            var buf = new byte[88];
            var s = buf.AsSpan();

            // Segments (dummy for now)
            BinaryPrimitives.WriteUInt32LittleEndian(s[..], 0); // GS
            BinaryPrimitives.WriteUInt32LittleEndian(s[4..], 0); // FS
            BinaryPrimitives.WriteUInt32LittleEndian(s[8..], 0); // ES
            BinaryPrimitives.WriteUInt32LittleEndian(s[12..], 0x2B); // DS (user data)

            BinaryPrimitives.WriteUInt32LittleEndian(s[16..], CPU.RegRead(Reg.EDI));
            BinaryPrimitives.WriteUInt32LittleEndian(s[20..], CPU.RegRead(Reg.ESI));
            BinaryPrimitives.WriteUInt32LittleEndian(s[24..], CPU.RegRead(Reg.EBP));
            BinaryPrimitives.WriteUInt32LittleEndian(s[28..], CPU.RegRead(Reg.ESP));
            BinaryPrimitives.WriteUInt32LittleEndian(s[32..], CPU.RegRead(Reg.EBX));
            BinaryPrimitives.WriteUInt32LittleEndian(s[36..], CPU.RegRead(Reg.EDX));
            BinaryPrimitives.WriteUInt32LittleEndian(s[40..], CPU.RegRead(Reg.ECX));
            BinaryPrimitives.WriteUInt32LittleEndian(s[44..], CPU.RegRead(Reg.EAX));

            BinaryPrimitives.WriteUInt32LittleEndian(s[48..], 0); // trapno
            BinaryPrimitives.WriteUInt32LittleEndian(s[52..], 0); // err
            BinaryPrimitives.WriteUInt32LittleEndian(s[56..], CPU.Eip);
            BinaryPrimitives.WriteUInt32LittleEndian(s[60..], 0x23); // CS (user code)
            BinaryPrimitives.WriteUInt32LittleEndian(s[64..], CPU.Eflags);
            BinaryPrimitives.WriteUInt32LittleEndian(s[68..], CPU.RegRead(Reg.ESP)); // sp_at_signal (UESP)
            BinaryPrimitives.WriteUInt32LittleEndian(s[72..], 0x2B); // SS

            // Cheat: Store full 64-bit mask in oldmask + cr2 area (8 bytes starting at 80)
            BinaryPrimitives.WriteUInt64LittleEndian(s[80..], oldMask);

            if (!CPU.CopyToUser(addr, buf))
            {
            }
        }
        catch
        {
        }
    }

    public bool TryEnterGuestRun()
    {
        return !Exited &&
               Process.State != ProcessState.Stopped &&
               PendingSyscall == null &&
               Continuation == null &&
               ExecutionMode == TaskExecutionMode.RunningGuest;
    }

    // Called by KernelScheduler once per time-slice.
    //
    // Design invariant: every path that sets task.Continuation or task.PendingSyscall
    // MUST eventually call CommonKernel.Schedule(this) to re-queue the task when it wants
    // to resume. RunSlice itself never re-queues — it just arranges state and returns.
    //
    // RunSlice is always called with Status == Running (enforced by the scheduler).
    // When it returns, the task must be in one of: Waiting, Ready, or Terminated.
    public void RunSlice(int instructionLimit = 1000000)
    {
        CommonKernel.CurrentTask = this;
        try
        {
            ProcessPendingSignals();

            // ── Phase 1: Resume a stored continuation ────────────────────────────────
            // A Continuation is set when an awaiter (e.g. EpollAwaiter, PollAwaiter)
            // has completed an async wait and wants the task to advance its syscall
            // state machine by one step. The continuation itself is responsible for
            // deciding what happens next:
            //   • If the syscall is done → it calls CommonKernel.Schedule(this)
            //     which sets Status = Ready and re-queues us.
            //   • If the syscall needs to wait again → it leaves Status = Waiting.
            // Either way we just return after invoking it.
            if (Continuation != null)
            {
                var c = Continuation;
                Continuation = null;
                ExecutionMode = TaskExecutionMode.WaitingContinuation;
                c();

                // Ensure we are not left in Running state — the continuation should
                // have set us to Ready (via Schedule) or Waiting. If it forgot, park.
                if (Status == FiberTaskStatus.Running)
                    Status = FiberTaskStatus.Waiting;
                return;
            }

            // ── Phase 2: Kick off a pending async syscall ─────────────────────────────
            // PendingSyscall is a Func<ValueTask<int>> installed by the syscall handler
            // (via Engine.Yield) before the CPU yields. HandleAsyncSyscall() drives the
            // ValueTask to completion on the scheduler thread, then calls
            // CommonKernel.Schedule(this). Until that happens the task stays Waiting.
            //
            // NOTE: HandleAsyncSyscall handles -ERESTARTSYS + signal delivery internally
            // (mirroring Linux's do_signal), so there is no race with ProcessPendingSignals.
            if (PendingSyscall != null)
            {
                ExecutionMode = TaskExecutionMode.WaitingAsyncSyscall;
                Status = FiberTaskStatus.Waiting;
                if (!_handlingAsyncSyscall)
                    HandleAsyncSyscall();
                return;
            }

            // ── Phase 3: Guard — verify we can actually run guest code ─────────────────
            if (!TryEnterGuestRun())
            {
                // TryEnterGuestRun returns false if any of the following holds:
                //   • task has exited
                //   • process is SIGSTOP-stopped
                //   • ExecutionMode != RunningGuest
                // Park appropriately; the something external (signal, resume) will
                // re-queue us when conditions change.
                if (Exited)
                {
                    Status = FiberTaskStatus.Terminated;
                    ExecutionMode = TaskExecutionMode.Terminated;
                }
                else if (Process.State == ProcessState.Stopped)
                {
                    Status = FiberTaskStatus.Waiting;
                    ExecutionMode = TaskExecutionMode.Stopped;
                }
                else if (Status == FiberTaskStatus.Running)
                {
                    Status = FiberTaskStatus.Waiting;
                }

                return;
            }

            // ── Phase 4: Execute guest instructions ───────────────────────────────────
            if (Process.Syscalls?.Strace == true)
                Logger.LogTrace("[RunSlice] Enter Run, EIP=0x{Eip:X} EAX=0x{Eax:X}", CPU.Eip, CPU.RegRead(Reg.EAX));

            CPU.Run(maxInsts: (ulong)instructionLimit);

            if (Process.Syscalls?.Strace == true)
                Logger.LogTrace("[RunSlice] Exit Run, EIP=0x{Eip:X} EAX=0x{Eax:X}", CPU.Eip, CPU.RegRead(Reg.EAX));

            if (Exited)
            {
                Status = FiberTaskStatus.Terminated;
                ExecutionMode = TaskExecutionMode.Terminated;
                return;
            }

            // ── Phase 5: Dispatch on CPU exit reason ──────────────────────────────────
            // CPU.Status tells us WHY Run() returned:
            //   Yield   — guest executed INT 0x80 (syscall) or called sched_yield.
            //   Running — instruction quota exhausted; yield to other tasks.
            //   Fault   — unrecoverable CPU exception.
            switch (CPU.Status)
            {
                case EmuStatus.Yield when PendingSyscall != null:
                    // Syscall handler installed a PendingSyscall before yielding.
                    // Start driving it asynchronously; task parks until done.
                    ExecutionMode = TaskExecutionMode.WaitingAsyncSyscall;
                    Status = FiberTaskStatus.Waiting;
                    Logger.LogTrace("[RunSlice] Yielded with PendingSyscall, calling HandleAsyncSyscall");
                    if (!_handlingAsyncSyscall)
                        HandleAsyncSyscall();
                    break;

                case EmuStatus.Yield when _pendingFaultFromInterrupt:
                    // Yielded due to a #UD or other fault interrupt handled in C#
                    _pendingFaultFromInterrupt = false;
                    HandleCpuFault();
                    break;

                case EmuStatus.Yield:
                case EmuStatus.Running: // instruction quota exhausted
                    // Normal yield (quota or explicit sched_yield if implemented)
                    // Re-queue so other tasks get a turn
                    Status = FiberTaskStatus.Ready;
                    CommonKernel.Schedule(this);
                    break;

                case EmuStatus.Fault:
                    HandleCpuFault();
                    break;

                default:
                    // Unknown status — re-queue defensively so we don't livelock.
                    CommonKernel.Schedule(this);
                    break;
            }
        }
        finally
        {
            CommonKernel.CurrentTask = null;
        }
    }

    // Handles an unrecoverable CPU fault (e.g. unmapped page, illegal memory access).
    // Logs register state, delivers SIGSEGV, and marks the task Terminated.
    private void HandleCpuFault()
    {
        var vector = CPU.FaultVector;
        var sig = vector switch
        {
            0 => Signal.SIGFPE,
            6 => Signal.SIGILL,
            11 => Signal.SIGBUS,
            12 => Signal.SIGBUS,
            13 => Signal.SIGSEGV,
            14 => Signal.SIGSEGV,
            _ => Signal.SIGILL
        };

        var faultName = vector switch
        {
            0 => "#DE (Divide Error)",
            6 => "#UD (Invalid Opcode)",
            13 => "#GP (General Protection Fault)",
            14 => "#PF (Page Fault)",
            _ => $"Vector {vector}"
        };

        // Log the fault for transparency, but at Debug/Trace level if it's expected to be handled?
        // Actually, hardware faults are serious, so Information level is a good middle ground.
        Logger.LogInformation("CPU Fault detected: {FaultName} at EIP=0x{EIP:X}. Delivering {Sig}.",
            faultName, CPU.Eip, sig);

        if (vector == 6)
        {
            var ip = CPU.Eip;
            var bytes = new byte[16];
            if (CPU.CopyFromUser(ip, bytes))
            {
                var hex = BitConverter.ToString(bytes).Replace("-", " ");
                var vma = Process.Mem.FindVMA(ip);
                Logger.LogInformation("#UD bytes @0x{EIP:X}: {Bytes} (VMA={Vma}, range=0x{Start:X}-0x{End:X})",
                    ip, hex, vma?.Name ?? "<unknown>", vma?.Start ?? 0, vma?.End ?? 0);
            }
            else
            {
                Logger.LogInformation("#UD bytes @0x{EIP:X}: <unreadable>", ip);
            }
        }

        // Deliver the signal!
        PostSignal((int)sig);

        // Re-schedule to allow signal processing (either handler or default action)
        Status = FiberTaskStatus.Ready;
        CommonKernel.Schedule(this);
    }

#pragma warning disable CS1998 // Async method lacks await operators
    public async ValueTask<FiberTask> Clone(int flags, uint stackPtr, uint ptidPtr, uint tlsPtr, uint ctidPtr)
    {
        const int CLONE_VM = 0x00000100;
        const int CLONE_FILES = 0x00000400;
        const int CLONE_VFORK = 0x00004000;
        const int CLONE_THREAD = 0x00010000;
        const int CLONE_SETTLS = 0x00080000;
        const int CLONE_PARENT_SETTID = 0x00001000;
        const int CLONE_CHILD_CLEARTID = 0x00200000;
        const int CLONE_CHILD_SETTID = 0x01000000;

        var cloneVm = (flags & CLONE_VM) != 0;
        var cloneFiles = (flags & CLONE_FILES) != 0;
        var cloneVfork = (flags & CLONE_VFORK) != 0;
        var cloneThread = (flags & CLONE_THREAD) != 0;
        var cloneSetTls = (flags & CLONE_SETTLS) != 0;
        var cloneParentSetTid = (flags & CLONE_PARENT_SETTID) != 0;
        var cloneChildClearTid = (flags & CLONE_CHILD_CLEARTID) != 0;
        var cloneChildSetTid = (flags & CLONE_CHILD_SETTID) != 0;

        // 1. Clone CPU
        var newCpu = CPU.Clone(cloneVm);

        // 2. Resource Management
        Process newProc;
        if (cloneThread)
        {
            newProc = Process; // Shared process
        }
        else
        {
            var newMem = cloneVm ? Process.Mem : Process.Mem.Clone();

            if (!cloneVm)
            {
                // Unmap all Shared VMAs from the native emulator engine so that they
                // will fault and be correctly linked back to their shared MemoryObjects.
                // Otherwise they remain as deep-copied private pages in the new emulator.
                foreach (var vma in newMem.VMAs)
                {
                    if ((vma.Flags & Fiberish.Memory.MapFlags.Shared) != 0)
                    {
                        newCpu.MemUnmap(vma.Start, vma.Length);
                    }
                }
            }

            var newSys = Process.Syscalls.Clone(newMem, cloneFiles);
            // UTS namespace is shared by default in fork/clone unless CLONE_NEWUTS
            newProc = new Process(NextTID(), newMem, newSys, Process.UTS)
            {
                PPID = Process.TGID,
                PGID = Process.PGID,
                SID = Process.SID,
                ControllingTty = Process.ControllingTty // Inherit controlling tty
            };
            newProc.CopyImageFrom(Process);

            // Inherit signal dispositions
            foreach (var kv in Process.SignalActions)
            {
                newProc.SignalActions[kv.Key] = kv.Value;
            }

            KernelScheduler.Current!.RegisterProcess(newProc);
        }

        var newTid = cloneThread ? NextTID() : newProc.TGID;
        var child = new FiberTask(newTid, newProc, newCpu, KernelScheduler.Current!);
        child.SignalMask = SignalMask;
        child.PendingSignals = 0;
        child.InterruptingSignal = null;

        // Register engine with SyscallManager
        if (!cloneThread)
        {
            child.Process.Syscalls.RegisterEngine(newCpu);
            Process.Children.Add(child.Process.TGID);
        }
        else
        {
            // Thread shares SyscallManager
            Process.Syscalls.RegisterEngine(newCpu);
        }

        // 3. Setup Child State
        if (stackPtr != 0) child.CPU.RegWrite(Reg.ESP, stackPtr);
        child.CPU.RegWrite(Reg.EAX, 0); // Return 0 to child

        // TLS
        if (cloneSetTls && tlsPtr != 0)
        {
            var tlsBuf = new byte[4];
            if (!child.CPU.CopyFromUser(tlsPtr + 4, tlsBuf))
                throw new InvalidOperationException("Failed to read TLS base from child address space");
            var baseAddr = BinaryPrimitives.ReadUInt32LittleEndian(tlsBuf);
            child.CPU.SetSegBase(Seg.GS, baseAddr);
        }

        // TID Pointers
        if (cloneParentSetTid && ptidPtr != 0)
        {
            var tidBuf = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(tidBuf, child.TID);
            if (!CPU.CopyToUser(ptidPtr, tidBuf))
                throw new InvalidOperationException("Failed to write TID to parent address space");
        }

        if (cloneChildSetTid && ctidPtr != 0)
        {
            var tidBuf = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(tidBuf, child.TID);
            if (!child.CPU.CopyToUser(ctidPtr, tidBuf))
                throw new InvalidOperationException("Failed to write TID to child address space");
        }

        if (cloneChildClearTid) child.ChildClearTidPtr = ctidPtr;

        if (cloneVfork)
        {
            // vfork semantics: parent is suspended until child calls exec or exit.
            // The child shares the parent's address space (CLONE_VM), so the parent
            // MUST NOT run until the child replaces the memory (exec) or exits.
            var vforkEvent = new AsyncWaitQueue();
            child.VforkDoneEvent = vforkEvent;
            child.VforkParent = this;
            Logger.LogInformation(
                "[Clone] CLONE_VFORK: parent TID={ParentTid} suspending until child TID={ChildTid} does exec/exit",
                TID, child.TID);
            await vforkEvent;
            Logger.LogInformation("[Clone] CLONE_VFORK: parent TID={ParentTid} resumed after child TID={ChildTid}",
                TID, child.TID);
        }

        return child;
    }


    // Signals the parent task that a vforked child has completed its exec/exit.
    public void SignalVforkDone()
    {
        if (VforkDoneEvent != null)
        {
            Logger.LogInformation(
                "[SignalVforkDone] Child TID={ChildTid} signaling parent TID={ParentTid} that vfork is done",
                TID, VforkParent?.TID);
            VforkDoneEvent.Set();
            VforkDoneEvent = null; // Clear the event after signaling
            VforkParent = null; // Clear parent reference
        }
    }

    private bool _handlingAsyncSyscall;

    private async void HandleAsyncSyscall()
    {
        if (_handlingAsyncSyscall)
        {
            Logger.LogWarning("[HandleAsyncSyscall] Re-entry detected! Ignoring.");
            return;
        }

        _handlingAsyncSyscall = true;
        try
        {
            if (PendingSyscall == null) return;

            var result = 0;
            try
            {
                var task = PendingSyscall();
                PendingSyscall = null; // Clear immediately

                // Allow the async operation to complete (suspend this method)
                result = await task;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[FiberTask] Syscall failed async: {ex}");
                // Return EFAULT or similar?
                CPU.RegWrite(Reg.EAX, unchecked((uint)-(int)Errno.EFAULT));
                CommonKernel.Schedule(this);
                return;
            }

            // Handle -ERESTARTSYS: decide restart vs EINTR, deliver signal if needed.
            // This mirrors Linux's do_signal(): we adjust registers FIRST, then set up
            // the signal frame. The sigcontext saves the adjusted state, so sigreturn
            // naturally does the right thing (restart or return EINTR).
            if (result == -512) // -ERESTARTSYS
            {
                var sig = InterruptingSignal;
                InterruptingSignal = null;
                Logger.LogInformation("[HandleAsyncSyscall] Syscall interrupted with -ERESTARTSYS, signal={Sig}", sig);

                if (sig.HasValue && Process.SignalActions.TryGetValue(sig.Value, out var action))
                {
                    var hasRestart = (action.Flags & LinuxConstants.SA_RESTART) != 0;
                    Logger.LogInformation(
                        "[HandleAsyncSyscall] Signal {Sig} handler flags=0x{Flags:X}, SA_RESTART={HasRestart}",
                        sig.Value, action.Flags, hasRestart);

                    if (action.Handler == 1) // SIG_IGN
                    {
                        // Ignored signal — just restart
                        CPU.Eip = SyscallEip;
                        ExecutionMode = TaskExecutionMode.RunningGuest;
                        CommonKernel.Schedule(this);
                        return;
                    }

                    if (action.Handler > 1) // Real handler
                    {
                        // Step 1: Adjust registers BEFORE setting up signal frame.
                        if (hasRestart)
                        {
                            // SA_RESTART: rewind so sigreturn will re-execute syscall
                            CPU.Eip = SyscallEip;
                            CPU.RegWrite(Reg.EAX, (uint)SyscallNr);
                            Logger.LogInformation(
                                "[HandleAsyncSyscall] SA_RESTART: set EIP=0x{Eip:X}, EAX={Nr} for restart after handler",
                                SyscallEip, SyscallNr);
                        }
                        else
                        {
                            // No SA_RESTART: userspace will see -EINTR after handler
                            CPU.RegWrite(Reg.EAX, unchecked((uint)-(int)Errno.EINTR));
                            Logger.LogInformation("[HandleAsyncSyscall] No SA_RESTART: EAX=-EINTR after handler");
                        }

                        // Step 2: Deliver the signal (sets up frame with adjusted registers)
                        DeliverSignalForRestart(sig.Value, action);

                        ExecutionMode = TaskExecutionMode.RunningGuest;
                        CommonKernel.Schedule(this);
                        return;
                    }

                    // SIG_DFL handler (handler == 0)
                    // Fall through — check if default-ignored
                }

                // Check default-ignored signals (no registered action or SIG_DFL)
                var defaultIgnored = sig.HasValue && (
                    sig.Value == (int)Signal.SIGCHLD ||
                    sig.Value == (int)Signal.SIGURG ||
                    sig.Value == (int)Signal.SIGWINCH);

                if (defaultIgnored)
                {
                    Logger.LogInformation(
                        "[HandleAsyncSyscall] Signal {Sig} is default-ignored; restarting syscall", sig);
                    CPU.Eip = SyscallEip;
                    ExecutionMode = TaskExecutionMode.RunningGuest;
                    CommonKernel.Schedule(this);
                    return;
                }

                // No SA_RESTART and no handler: return -EINTR
                Logger.LogInformation("[HandleAsyncSyscall] No handler, returning -EINTR");
                result = -(int)Errno.EINTR;
            }
            else
            {
                // Clear interrupting signal if syscall completed normally
                InterruptingSignal = null;
            }

            // Resume: write result to EAX
            CPU.RegWrite(Reg.EAX, (uint)result);

            if (Process.Syscalls.Strace)
                SyscallTracer.TraceExit(Logger, Process.Syscalls, TID, SyscallNr, result, SyscallArg1, SyscallArg2,
                    SyscallArg3);

            ExecutionMode = TaskExecutionMode.RunningGuest;
            // Reschedule the task
            CommonKernel.Schedule(this);
        }
        finally
        {
            _handlingAsyncSyscall = false;
        }
    }

    public void ExitRobustList()
    {
        if (RobustListHead == 0) return;

        var head = RobustListHead;
        var limit = 2048; // ROBUST_LIST_LIMIT

        // read futex_offset
        var futexOffsetBuf = new byte[4];
        if (!CPU.CopyFromUser(head + 4, futexOffsetBuf)) return;
        var futexOffset = BinaryPrimitives.ReadInt32LittleEndian(futexOffsetBuf);

        // read list_op_pending
        var pendingBuf = new byte[4];
        if (!CPU.CopyFromUser(head + 8, pendingBuf)) return;
        var pendingObj = BinaryPrimitives.ReadUInt32LittleEndian(pendingBuf);

        // read list next
        var entryBuf = new byte[4];
        if (!CPU.CopyFromUser(head, entryBuf)) return;
        var entry = BinaryPrimitives.ReadUInt32LittleEndian(entryBuf);

        Logger.LogTrace("[ExitRobustList] FutexOffset={Offset} PendingObj={Pending:X8} FirstEntry={Entry:X8}", futexOffset, pendingObj, entry);

        var sm = Process.Syscalls;

        while (entry != head && entry != 0)
        {
            var nextBuf = new byte[4];
            var rc = CPU.CopyFromUser(entry, nextBuf);
            if (!rc) break; // Error reading next pointer
            var nextEntry = BinaryPrimitives.ReadUInt32LittleEndian(nextBuf);
            
            if (entry != pendingObj)
            {
                HandleFutexDeath(sm, (uint)(entry + futexOffset), false);
            }

            entry = nextEntry;
            if (--limit == 0) break;
        }

        if (pendingObj != 0)
        {
            Logger.LogTrace("[ExitRobustList] Handling death for pending {Uaddr:X8}", pendingObj + futexOffset);
            HandleFutexDeath(sm, (uint)(pendingObj + futexOffset), true);
        }
    }

    private void HandleFutexDeath(SyscallManager sm, uint uaddr, bool pendingOp)
    {
        var uvalBuf = new byte[4];
        if (!CPU.CopyFromUser(uaddr, uvalBuf)) return;
        var uval = BinaryPrimitives.ReadUInt32LittleEndian(uvalBuf);

        var owner = uval & LinuxConstants.FUTEX_TID_MASK;

        // If this is a pending op, and no one owns it, we might just wake it
        if (pendingOp && owner == 0)
        {
            sm.Futex.Wake(uaddr, 1);
            return;
        }

        if (owner != TID) return;

        var mval = (uval & LinuxConstants.FUTEX_WAITERS) | LinuxConstants.FUTEX_OWNER_DIED;

        BinaryPrimitives.WriteUInt32LittleEndian(uvalBuf, mval);
        if (!CPU.CopyToUser(uaddr, uvalBuf)) return;

        if ((uval & LinuxConstants.FUTEX_WAITERS) != 0)
        {
            sm.Futex.Wake(uaddr, 1);
        }
    }
}