using System.Collections.Concurrent;
using Bifrost.Memory;
using Bifrost.Syscalls;
using Bifrost.Native;
using System.Buffers.Binary;
using Microsoft.Extensions.Logging;

namespace Bifrost.Core;

public enum FiberTaskStatus
{
    Ready,
    Running,
    Waiting,
    Zombie,
    Terminated
}

public class FiberTask
{
    private static int _tidCounter = 1000;
    public static int NextTID() => Interlocked.Increment(ref _tidCounter);

    public int TID { get; }
    public int PID { get; }
    public Engine CPU { get; }
    public KernelScheduler CommonKernel { get; }
    public Action? Continuation { get; set; }

    public FiberTaskStatus Status { get; set; } = FiberTaskStatus.Ready;

    public Process Process { get; }

    // Signal System
    public ulong SignalMask { get; set; }
    public ulong PendingSignals { get; set; } // Bitmask
    public uint AltStackSp { get; set; }
    public uint AltStackSize { get; set; }
    public int AltStackFlags { get; set; }

    public bool Exited { get; set; }

    public int ExitStatus { get; set; }

    public uint ChildClearTidPtr { get; set; }

    // Interrupt handling for blocking syscalls
    private Action? _interruptHandler;
    public bool WasInterrupted { get; private set; }

    public Microsoft.Extensions.Logging.ILogger Logger { get; }

    public FiberTask(int tid, Process process, Engine cpu, KernelScheduler kernel)
    {
        TID = tid;
        PID = process.TGID;
        Process = process;
        lock (Process.Threads) Process.Threads.Add(this);

        CPU = cpu;
        CPU.Owner = this;
        CommonKernel = kernel;

        Logger = kernel.LoggerFactory.CreateLogger($"Bifrost.Task.{TID}");
        CPU.LogHandler = (engine, level, msg) => 
        {
            Logger.Log((Microsoft.Extensions.Logging.LogLevel)level, "[Native] {Message}", msg);
        };

        kernel.RegisterTask(this);

        CPU.PageFaultResolver = HandlePageFault;

        CPU.InterruptHandler = HandleInterrupt;
        CPU.FaultHandler = HandleCpuFault;
    }

    private bool HandlePageFault(uint addr, bool isWrite)
    {
        return Process.Mem.HandleFault(addr, isWrite, CPU);
    }

    private bool HandleInterrupt(Engine engine, uint vector)
    {
        // 0x80 is Syscall
        if (vector == 0x80)
        {
            return Process.Syscalls.Handle(engine, vector);
        }
        return false;
    }

    private bool HandleCpuFault(Engine engine, uint addr, bool isWrite)
    {
        // This is usually for unhandled page faults or other CPU exceptions
        // For now, delegate to PageFaultResolver if it was a memory issue
        return HandlePageFault(addr, isWrite);
    }

    public void RegisterBlockingSyscall(Action onInterrupt)
    {
        _interruptHandler = onInterrupt;
        WasInterrupted = false;
    }

    public bool TryInterrupt()
    {
        if (_interruptHandler != null)
        {
            var handler = _interruptHandler;
            _interruptHandler = null;
            WasInterrupted = true;
            handler(); // Execute cancellation logic (e.g., remove from timer)
            return true;
        }
        return false;
    }

    public void ClearInterrupt()
    {
        _interruptHandler = null;
        WasInterrupted = false;
    }

    // Ported from Task.cs
    public void HandleSignal(int sig)
    {
        // 1. Check if ignored
        if (Process.SignalActions.TryGetValue(sig, out var action))
        {
            if (action.Handler == 1) // SIG_IGN
            {
                return;
            }
            if (action.Handler == 0) // SIG_DFL
            {
                // Default actions (simplified)
                if (sig == 9 || sig == 15 || sig == 2 || sig == 3 || sig == 6 || sig == 11) // KILL, TERM, INT, QUIT, ABRT, SEGV
                {
                    Exited = true;
                    ExitStatus = 128 + sig;
                    return;
                }
                // Ignore others for now
                return;
            }

            // 2. Setup frame for handler
            // Push SigContext/StackFrame
            uint sp = CPU.RegRead(Reg.ESP);

            // Check altstack
            if ((AltStackFlags & 1) == 0 && AltStackSp != 0 && (action.Flags & 0x08000000) != 0) // ONSTACK
            {
                sp = AltStackSp + AltStackSize;
            }

            // Return address (restorer or some trampoline)
            uint retAddr = Process.Syscalls.RtSigReturnAddr;
            uint frameEsp = sp; // Initialize frameEsp here

            // Setup Stack with SA_SIGINFO support
            SetupSigContext(sp, ref frameEsp, sig, action, retAddr);
            CPU.RegWrite(Reg.ESP, frameEsp);
            CPU.Eip = action.Handler;

            // SA_RESTART Logic
            int eax = (int)CPU.RegRead(Reg.EAX);
            if (eax == -512) // -ERESTARTSYS
            {
                if ((action.Flags & LinuxConstants.SA_RESTART) != 0)
                {
                    // Restart: rewind EIP to re-execute 'int 0x80' (CD 80)
                    CPU.Eip -= 2;
                    // TODO: Need original syscall number to fully restart?
                    // For now fallback to EINTR if we can't fully restore
                    CPU.RegWrite(Reg.EAX, unchecked((uint)-(int)Errno.EINTR));
                }
                else
                {
                    // Interrupted: return -EINTR
                    CPU.RegWrite(Reg.EAX, unchecked((uint)-(int)Errno.EINTR));
                }
            }
        }
    }

    private void SetupSigContext(uint sp, ref uint esp, int sig, SigAction action, uint retAddr)
    {

        // Push UContext (approx 400 bytes inc fpstate)
        // Align to 16 bytes
        esp = (esp - 512u) & ~0xFu;
        // We always setup UContext and SigInfo because sys_rt_sigreturn expects them.

        uint ucontextAddr = esp;

        // Populate UContext
        // uc_flags, uc_link, uc_stack, uc_mcontext, uc_sigmask
        uint mcontextOffset = 4 + 4 + 12;

        // Save registers to mcontext
        WriteSigContext(esp + mcontextOffset);

        // Push SigInfo (128 bytes)
        esp = (esp - 128u) & ~0xFu;
        uint siginfoAddr = esp;

        // Populate SigInfo
        // si_signo, si_errno, si_code
        byte[] siBuf = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(siBuf.AsSpan(0, 4), sig);
        BinaryPrimitives.WriteInt32LittleEndian(siBuf.AsSpan(4, 4), 0); // errno
        BinaryPrimitives.WriteInt32LittleEndian(siBuf.AsSpan(8, 4), 0); // code (SI_USER)
        if (!CPU.CopyToUser(siginfoAddr, siBuf)) return;

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
        try
        {
            byte[] buf = new byte[80];
            if (!CPU.CopyFromUser(addr, buf)) return;
            var s = new ReadOnlySpan<byte>(buf);

            // Restore segments (optional, but good for threads/TLS)
            // GS, FS, ES, DS
            // CPU.SetSeg(Seg.GS, BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(0)));
            // CPU.SetSeg(Seg.FS, BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(4)));
            // ...

            // General Registers
            CPU.RegWrite(Reg.EDI, BinaryPrimitives.ReadUInt32LittleEndian(s[16..]));
            CPU.RegWrite(Reg.ESI, BinaryPrimitives.ReadUInt32LittleEndian(s[20..]));
            CPU.RegWrite(Reg.EBP, BinaryPrimitives.ReadUInt32LittleEndian(s[24..]));
            CPU.RegWrite(Reg.ESP, BinaryPrimitives.ReadUInt32LittleEndian(s[28..])); // This is "OS ESP" inside sigcontext? 
                                                                                         // Actually, sigcontext has "old esp" at offset 68 (UESP).
                                                                                         // But offset 28 is also ESP (from pusha?). 
                                                                                         // Linux sigreturn restores from UESP (offset 68) usually.

            CPU.RegWrite(Reg.EBX, BinaryPrimitives.ReadUInt32LittleEndian(s[32..]));
            CPU.RegWrite(Reg.EDX, BinaryPrimitives.ReadUInt32LittleEndian(s[36..]));
            CPU.RegWrite(Reg.ECX, BinaryPrimitives.ReadUInt32LittleEndian(s[40..]));
            CPU.RegWrite(Reg.EAX, BinaryPrimitives.ReadUInt32LittleEndian(s[44..]));

            // IP, Flags, SP
            CPU.Eip = BinaryPrimitives.ReadUInt32LittleEndian(s[56..]);
            CPU.Eflags = BinaryPrimitives.ReadUInt32LittleEndian(s[64..]);
            CPU.RegWrite(Reg.ESP, BinaryPrimitives.ReadUInt32LittleEndian(s[68..])); // UESP
        }
        catch { }
    }

    private void WriteSigContext(uint addr)
    {
        // offset 0: gs, fs, es, ds
        // offset 16: edi, esi, ebp, esp, ebx, edx, ecx, eax
        // offset 48: trapno, err
        // offset 56: eip, cs, efl, uesp, ss
        try
        {
            byte[] buf = new byte[80];
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

            BinaryPrimitives.WriteUInt32LittleEndian(s[56..], CPU.Eip);
            BinaryPrimitives.WriteUInt32LittleEndian(s[60..], 0x23); // CS (user code)
            BinaryPrimitives.WriteUInt32LittleEndian(s[64..], CPU.Eflags);
            BinaryPrimitives.WriteUInt32LittleEndian(s[68..], CPU.RegRead(Reg.ESP));
            BinaryPrimitives.WriteUInt32LittleEndian(s[72..], 0x2B); // SS

            if (!CPU.CopyToUser(addr, buf)) { }
        }
        catch { }
    }

    // Blocking Syscall Support
    public Func<ValueTask<int>>? PendingSyscall { get; set; }

    // Main execution slice called by KernelScheduler
    public void RunSlice(int instructionLimit = 1000)
    {
        CommonKernel.CurrentTask = this;
        try
        {
            // 0. Check for Continuation (Kernel Mode Resume)
            // This is used for Sleep/Yield awaitables and tests
            if (Continuation != null)
            {
                var c = Continuation;
                Continuation = null;
                c();
                // If the continuation suspended again (await), it will have re-scheduled itself
                // or set a timer. We return to scheduler loop.
                return;
            }

            // 1. Run CPU execution
            CPU.Run(maxInsts: (ulong)instructionLimit);

            // 2. Check Exit
            if (Exited)
            {
                Status = FiberTaskStatus.Terminated;
                return;
            }

            // 3. Check for Syscall Yield or Quanta exhaustion
            if (CPU.Status == EmuStatus.Yield)
            {
                if (PendingSyscall != null)
                {
                    // We are blocked on an async syscall
                    Status = FiberTaskStatus.Waiting;
                    HandleAsyncSyscall();
                    // We return here. The Scheduler will NOT reschedule us immediately 
                    // because Status is Waiting. HandleAsyncSyscall ensures we are 
                    // rescheduled when done.
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
                // Logger.LogError(...) // TODO: Access Logger
                Exited = true;
                ExitStatus = 139; // SIGSEGV
                Status = FiberTaskStatus.Terminated;
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

        bool cloneVm = (flags & CLONE_VM) != 0;
        bool cloneFiles = (flags & CLONE_FILES) != 0;
        bool cloneVfork = (flags & CLONE_VFORK) != 0;
        bool cloneThread = (flags & CLONE_THREAD) != 0;
        bool cloneSetTls = (flags & CLONE_SETTLS) != 0;
        bool cloneParentSetTid = (flags & CLONE_PARENT_SETTID) != 0;
        bool cloneChildClearTid = (flags & CLONE_CHILD_CLEARTID) != 0;
        bool cloneChildSetTid = (flags & CLONE_CHILD_SETTID) != 0;

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
            newProc = new Process(FiberTask.NextTID(), newMem, newSys, Process.UTS)
            {
                PPID = Process.TGID,
                PGID = Process.PGID
            };
            KernelScheduler.Current.RegisterProcess(newProc);
        }

        int newTid = cloneThread ? FiberTask.NextTID() : newProc.TGID;
        var child = new FiberTask(newTid, newProc, newCpu, KernelScheduler.Current);

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
        if (stackPtr != 0)
        {
            child.CPU.RegWrite(Reg.ESP, stackPtr);
        }
        child.CPU.RegWrite(Reg.EAX, 0); // Return 0 to child

        // TLS
        if (cloneSetTls && tlsPtr != 0)
        {
            byte[] tlsBuf = new byte[4];
            if (!child.CPU.CopyFromUser(tlsPtr + 4, tlsBuf))
                throw new InvalidOperationException("Failed to read TLS base from child address space");
            uint baseAddr = BinaryPrimitives.ReadUInt32LittleEndian(tlsBuf);
            child.CPU.SetSegBase(Seg.GS, baseAddr);
        }

        // TID Pointers
        if (cloneParentSetTid && ptidPtr != 0)
        {
            byte[] tidBuf = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(tidBuf, child.TID);
            if (!CPU.CopyToUser(ptidPtr, tidBuf))
                throw new InvalidOperationException("Failed to write TID to parent address space");
        }

        if (cloneChildSetTid && ctidPtr != 0)
        {
            byte[] tidBuf = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(tidBuf, child.TID);
            if (!child.CPU.CopyToUser(ctidPtr, tidBuf))
                throw new InvalidOperationException("Failed to write TID to child address space");
        }

        if (cloneChildClearTid)
        {
            child.ChildClearTidPtr = ctidPtr;
        }

        // CLONE_VFORK: child will wake parent on exit/exec
        // For FiberTask, we can use a WaitHandle or just block the parent?
        // Blocking parent: Parent awaits Child.ExecWaitHandle?
        // Or we just don't schedule Parent until Child signals?
        if (cloneVfork)
        {
            // TODO: Implement VFORK blocking.
            // Parent should yield and not be scheduled until child execs or exits.
            // Implementation: Set parent status to Waiting, register callback on Child.
            // But simpler for now: Just schedule child.
        }

        CommonKernel.Schedule(child);
        return child;
    }

    private async void HandleAsyncSyscall()
    {
        if (PendingSyscall == null) return;

        try
        {
            var task = PendingSyscall();
            PendingSyscall = null; // Clear immediately

            // Allow the async operation to complete (suspend this method)
            int result = await task;

            // Resume: write result to EAX
            CPU.RegWrite(Reg.EAX, (uint)result);

            // If the result was EINTR, we might need to handle signal dispatching loop here?
            // Usually the sycall implementation handles the EINTR return value.

            // Reschedule the task
            CommonKernel.Schedule(this);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FiberTask] Syscall failed async: {ex}");
            // Return EFAULT or similar?
            CPU.RegWrite(Reg.EAX, unchecked((uint)-(int)Errno.EFAULT));
            CommonKernel.Schedule(this);
        }
    }
}
