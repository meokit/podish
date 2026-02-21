using System.Buffers.Binary;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.X86.Native;
using Microsoft.Extensions.Logging;

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
        CPU.LogHandler = (engine, level, msg) => { Logger.Log((LogLevel)level, "[Native] {Message}", msg); };

        kernel.RegisterTask(this);

        CPU.PageFaultResolver = HandlePageFault;

        CPU.InterruptHandler = HandleInterrupt;
        CPU.FaultHandler = HandleCpuFault;
    }

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
    public uint AltStackSp { get; set; }
    public uint AltStackSize { get; set; }
    public int AltStackFlags { get; set; }

    public bool Exited { get; set; }

    public int ExitStatus { get; set; }

    public uint ChildClearTidPtr { get; set; }
    
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

    public static int NextTID()
    {
        return Interlocked.Increment(ref _tidCounter);
    }

    private bool HandlePageFault(uint addr, bool isWrite)
    {
        return Process.Mem.HandleFault(addr, isWrite, CPU);
    }

    private bool HandleInterrupt(Engine engine, uint vector)
    {
        // 0x80 is Syscall
        if (vector == 0x80) return Process.Syscalls.Handle(engine, vector);
        return false;
    }

    private bool HandleCpuFault(Engine engine, uint addr, bool isWrite)
    {
        // This is usually for unhandled page faults or other CPU exceptions
        // For now, delegate to PageFaultResolver if it was a memory issue
        return HandlePageFault(addr, isWrite);
    }

    // RegisterBlockingSyscall removed

    /// <summary>
    ///     Post a signal to this task. Safe to call from any thread.
    ///     This sets the pending flag and interrupts any blocking syscall.
    ///     Actual delivery happens in RunSlice via ProcessPendingSignals -> DeliverSignal.
    /// </summary>
    public void PostSignal(int sig)
    {
        if (sig < 1 || sig > 64) return;

        Logger.LogInformation("[PostSignal] Posting signal {Sig}", sig);

        var mask = 1UL << (sig - 1);
        lock (this)
        {
            Logger.LogDebug("[PostSignal] Setting PendingSignals to {1} from {2}", PendingSignals | mask, PendingSignals);
            PendingSignals |= mask;
        }

        // Check if we should interrupt syscall
        // SIGKILL(9) and SIGSTOP(19) cannot be blocked
        var isBlocked = (SignalMask & mask) != 0;
        if (sig == (int)Signal.SIGKILL || sig == (int)Signal.SIGSTOP) isBlocked = false;

        if (!isBlocked)
        {
            InterruptingSignal = sig;
            WakeReason = WakeReason.Signal;
        }
        else
        {
            Logger.LogDebug("[PostSignal] Signal {Sig} received but currently masked by SignalMask (0x{Mask:X}). Added to pending.", sig, SignalMask);
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

            lock (this)
            {
                if ((PendingSignals & mask) == 0) continue;

                // Check blocked
                if ((SignalMask & mask) != 0 && i != 9 && i != 19) 
                {
                    Logger.LogDebug("[CheckPendingSignals] Signal {Sig} is currently blocked by SignalMask (0x{Mask:X})", i, SignalMask);
                    continue;
                }

                // POP SIGNAL
                PendingSignals &= ~mask;
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

            DeliverSignal(i, action, hasAction, oldMask);
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

    // Prepare stack frame for signal handler
    private void DeliverSignal(int sig, SigAction action, bool hasAction, ulong oldMask)
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
                SetupSigContext(sp, ref frameEsp, sig, action, retAddr, oldMask);
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

    // Kept for compatibility if called externally (removed HandleSignal method name to avoid confusion, 
    // but KernelScheduler currently calls HandleSignal. We will update KernelScheduler next.)
    // Note: I renamed HandleSignal to DeliverSignal and made it private.
    // I added Signal(sig) as the public API.

    private static DefaultSignalAction GetDefaultSignalAction(int sig)
    {
        return sig switch
        {
            (int)Signal.SIGCHLD or (int)Signal.SIGURG or (int)Signal.SIGWINCH => DefaultSignalAction.Ignore,
            (int)Signal.SIGSTOP or (int)Signal.SIGTSTP or (int)Signal.SIGTTIN or (int)Signal.SIGTTOU => DefaultSignalAction.Stop,
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

        var ppid = Process.PPID;
        if (ppid > 0)
        {
            var parentTask = CommonKernel.GetTask(ppid);
            parentTask?.PostSignal((int)Signal.SIGCHLD);
        }
    }

    private void ContinueBySignal()
    {
        if (Process.State != ProcessState.Stopped) return;
        Process.State = ProcessState.Running;
        Process.HasWaitableContinue = true;
        Process.HasWaitableStop = false;
        Process.StateChangeEvent.Set();

        var ppid = Process.PPID;
        if (ppid > 0)
        {
            var parentTask = CommonKernel.GetTask(ppid);
            parentTask?.PostSignal((int)Signal.SIGCHLD);
        }
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

    private void SetupSigContext(uint sp, ref uint esp, int sig, SigAction action, uint retAddr, ulong oldMask)
    {
        // Push UContext (approx 400 bytes inc fpstate)
        // Align to 16 bytes
        esp = (esp - 512u) & ~0xFu;
        // We always setup UContext and SigInfo because sys_rt_sigreturn expects them.

        var ucontextAddr = esp;

        // Populate UContext
        // uc_flags, uc_link, uc_stack, uc_mcontext, uc_sigmask
        uint mcontextOffset = 4 + 4 + 12;

        // Save registers to mcontext
        WriteSigContext(esp + mcontextOffset, oldMask);

        // Push SigInfo (128 bytes)
        esp = (esp - 128u) & ~0xFu;
        var siginfoAddr = esp;

        // Populate SigInfo
        // si_signo, si_errno, si_code
        var siBuf = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(siBuf.AsSpan(0, 4), sig);
        BinaryPrimitives.WriteInt32LittleEndian(siBuf.AsSpan(4, 4), 0); // errno
        BinaryPrimitives.WriteInt32LittleEndian(siBuf.AsSpan(8, 4), 0); // code (SI_USER)
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
            Logger.LogDebug("[RestoreSigContext] Restored SignalMask {SignalMask}, before {OldSignalMask}", SignalMask, oldSignalMask);
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

    // Main execution slice called by KernelScheduler
    public void RunSlice(int instructionLimit = 1000000)
    {
        CommonKernel.CurrentTask = this;
        try
        {
            ProcessPendingSignals();

            if (Continuation != null)
            {
                ExecutionMode = TaskExecutionMode.WaitingContinuation;
                var c = Continuation;
                Continuation = null;
                c();
                
                if (Status == FiberTaskStatus.Running)
                {
                    Status = FiberTaskStatus.Waiting;
                }
                
                return;
            }

            if (PendingSyscall != null)
            {
                ExecutionMode = TaskExecutionMode.WaitingAsyncSyscall;
                Status = FiberTaskStatus.Waiting;
                if (!_handlingAsyncSyscall)
                    HandleAsyncSyscall();
                return;
            }

            if (!TryEnterGuestRun())
            {
                if (Process.State == ProcessState.Stopped)
                {
                    Status = FiberTaskStatus.Waiting;
                    ExecutionMode = TaskExecutionMode.Stopped;
                }
                else if (Exited)
                {
                    Status = FiberTaskStatus.Terminated;
                    ExecutionMode = TaskExecutionMode.Terminated;
                }
                else if (Status == FiberTaskStatus.Running)
                {
                    Status = FiberTaskStatus.Waiting;
                }
                return;
            }

            // 1. Run CPU execution
            if (Process.Syscalls?.Strace == true) Logger.LogTrace("[RunSlice] Enter Run, EIP=0x{Eip:X} EAX=0x{Eax:X}", CPU.Eip, CPU.RegRead(Reg.EAX));

            CPU.Run(maxInsts: (ulong)instructionLimit);
            
            if (Process.Syscalls?.Strace == true) Logger.LogTrace("[RunSlice] Exit Run, EIP=0x{Eip:X} EAX=0x{Eax:X}", CPU.Eip, CPU.RegRead(Reg.EAX));

            // 2. Check Exit
            if (Exited)
            {
                Status = FiberTaskStatus.Terminated;
                ExecutionMode = TaskExecutionMode.Terminated;
                return;
            }

            // 3. Check for Syscall Yield or Quanta exhaustion
            if (CPU.Status == EmuStatus.Yield)
            {
                if (PendingSyscall != null)
                {
                    ExecutionMode = TaskExecutionMode.WaitingAsyncSyscall;
                    Status = FiberTaskStatus.Waiting;
                    Logger.LogTrace("[RunSlice] Yielded with PendingSyscall, calling HandleAsyncSyscall");
                    if (!_handlingAsyncSyscall)
                        HandleAsyncSyscall();
                }
                else
                {
                    // Cooperative Yield (sched_yield or just manual yield)
                    // Re-schedule for next round
                    CommonKernel.Schedule(this);
                }
            }
            else if (CPU.Status == EmuStatus.Running)
            {
                // Quanta exhausted, reschedule
                CommonKernel.Schedule(this);
            }
            else if (CPU.Status == EmuStatus.Fault)
            {
                // Crash
                Logger.LogCritical("CPU Fault detected at EIP=0x{EIP:X}. Terminating task with SIGSEGV.", CPU.Eip);
                Logger.LogCritical("Registers: EAX=0x{EAX:X} EBX=0x{EBX:X} ECX=0x{ECX:X} EDX=0x{EDX:X}",
                    CPU.RegRead(Reg.EAX), CPU.RegRead(Reg.EBX), CPU.RegRead(Reg.ECX), CPU.RegRead(Reg.EDX));
                Logger.LogCritical("           ESI=0x{ESI:X} EDI=0x{EDI:X} EBP=0x{EBP:X} ESP=0x{ESP:X}",
                    CPU.RegRead(Reg.ESI), CPU.RegRead(Reg.EDI), CPU.RegRead(Reg.EBP), CPU.RegRead(Reg.ESP));
                Logger.LogCritical("           EFLAGS=0x{EFLAGS:X}", CPU.Eflags);

                TerminateBySignal((int)Signal.SIGSEGV, coreDumped: true);
                Status = FiberTaskStatus.Terminated;
                ExecutionMode = TaskExecutionMode.Terminated;
            }
            else
            {
                // Stopped/Unknown -> Reschedule?
                CommonKernel.Schedule(this);
            }
        }
        finally
        {
            CommonKernel.CurrentTask = null;
        }
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
            // TODO: Implement VFORK blocking.
        }

        return child;
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

            // Handle SA_RESTART for interrupted syscalls
            if (result == -512) // -ERESTARTSYS
            {
                var sig = InterruptingSignal;
                InterruptingSignal = null; // Clear after reading
                Logger.LogInformation("[HandleAsyncSyscall] Syscall interrupted with -ERESTARTSYS, signal={Sig}", sig);
                var defaultIgnored = false;
                if (sig.HasValue && Process.SignalActions.TryGetValue(sig.Value, out var action))
                {
                    var hasRestart = (action.Flags & LinuxConstants.SA_RESTART) != 0;
                    Logger.LogInformation(
                        "[HandleAsyncSyscall] Signal {Sig} handler flags=0x{Flags:X}, SA_RESTART={HasRestart}",
                        sig.Value, action.Flags, hasRestart);
                    if (action.Handler == 1) // SIG_IGN
                    {
                        defaultIgnored = true;
                    }
                    if (hasRestart)
                    {
                        // SA_RESTART: rewind EIP to re-execute 'int 0x80' (CD 80)
                        // SyscallEip was saved before syscall execution
                        Logger.LogInformation(
                            "[HandleAsyncSyscall] SA_RESTART enabled: rewinding EIP from 0x{OldEip:X} to 0x{SyscallEip:X}",
                            CPU.Eip, SyscallEip);

                        CPU.Eip = SyscallEip;
                        // Don't modify EAX - the syscall will be re-executed with original args
                        ExecutionMode = TaskExecutionMode.RunningGuest;
                        // Reschedule and return
                        CommonKernel.Schedule(this);
                        return;
                    }
                }
                else if (sig.HasValue)
                {
                    defaultIgnored = sig.Value == (int)Signal.SIGCHLD ||
                                     sig.Value == (int)Signal.SIGURG ||
                                     sig.Value == (int)Signal.SIGWINCH;
                }

                if (defaultIgnored)
                {
                    Logger.LogInformation(
                        "[HandleAsyncSyscall] Signal {Sig} is ignored by default; restarting syscall",
                        sig);
                    CPU.Eip = SyscallEip;
                    ExecutionMode = TaskExecutionMode.RunningGuest;
                    CommonKernel.Schedule(this);
                    return;
                }

                // No SA_RESTART or no handler: return -EINTR
                Logger.LogInformation("[HandleAsyncSyscall] No SA_RESTART: returning -EINTR");
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
}
