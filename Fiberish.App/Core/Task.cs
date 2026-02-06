using System.Buffers.Binary;
using Bifrost.Memory;
using Bifrost.Syscalls;
using Bifrost.Native;
using Microsoft.Extensions.Logging;
using Bifrost.Diagnostics;

namespace Bifrost.Core;

public enum ProcessState
{
    Running,
    Sleeping,
    Stopped,     // Suspended by signal
    Continued,   // Resumed from stopped state
    Zombie,      // Exited but not reaped by parent
    Dead         // Reaped by parent
}

public class UTSNamespace
{
    public string SysName { get; set; } = "Linux";
    public string NodeName { get; set; } = "x86emu";
    public string Release { get; set; } = "6.1.0";
    public string Version { get; set; } = "#1 SMP PREEMPT";
    public string Machine { get; set; } = "i686";
    public string DomainName { get; set; } = "(none)";

    public UTSNamespace Clone() => (UTSNamespace)MemberwiseClone();
}

public struct SigAction
{
    public uint Handler;
    public uint Flags;
    public uint Restorer;
    public ulong Mask;
}

public class Process
{
    public int TGID { get; set; }
    public VMAManager Mem { get; set; }
    public SyscallManager Syscalls { get; set; }

    // Credentials
    public int UID { get; set; }
    public int GID { get; set; }
    public int EUID { get; set; }
    public int EGID { get; set; }
    public int SUID { get; set; }
    public int SGID { get; set; }
    public int FSUID { get; set; }
    public int FSGID { get; set; }

    // Namespaces
    public UTSNamespace UTS { get; set; }

    // Other process state
    public int Umask { get; set; } = 18; // Default 022 octal is 18 decimal

    // Parent-child relationship
    public int PPID { get; set; } = 0;  // Parent Process ID
    public List<int> Children { get; } = new List<int>();  // Child Process IDs
    public ProcessState State { get; set; } = ProcessState.Running;
    public int ExitStatus { get; set; } = 0;
    public ManualResetEventSlim ZombieEvent { get; } = new(false);  // Signaled when process becomes zombie

    public Dictionary<int, SigAction> SignalActions { get; } = new();

    public Process(int tgid, VMAManager mem, SyscallManager syscalls, UTSNamespace? uts = null)
    {
        TGID = tgid;
        Mem = mem;
        Syscalls = syscalls;
        UTS = uts ?? new UTSNamespace();

        // Default to root
        UID = GID = EUID = EGID = SUID = SGID = FSUID = FSGID = 0;
    }
}

    public class Task
    {
    private static readonly ILogger Logger = Logging.CreateLogger<Task>();

    // Global Lock for serialization (GIL) - Now a SemaphoreSlim for async Support
    public static readonly SemaphoreSlim GIL = new(1, 1);

    private static int _pidCounter = 1000;
    public static int NextPID() => Interlocked.Increment(ref _pidCounter);

    public int TID { get; set; }
    public Process Process { get; set; }
    public Engine CPU { get; set; }
    private readonly Queue<string> _traceBuffer = new(1024);

    public int ExitCode { get; set; }
    public bool Exited { get; set; }
    public ManualResetEventSlim WaitEvent { get; } = new(false);
    public uint ChildClearTidPtr { get; set; }
    public Task? VforkParent { get; set; } = null;  // For CLONE_VFORK

    // Async support: the task that is currently blocking this emulator task
    public System.Threading.Tasks.Task? BlockingTask { get; set; }

    // Cooperative scheduling: a TCS that the scheduler will complete to resume this task
    // Cooperative scheduling: a TCS that the scheduler will complete to resume this task
    public TaskCompletionSource<bool>? ResumeTcs { get; set; }

    // Signals
    public ulong SignalMask { get; set; }
    public ulong PendingSignals { get; set; }
    public uint AltStackSp { get; set; }
    public uint AltStackSize { get; set; }
    public int AltStackFlags { get; set; }

    public Task(int tid, Process process, Engine cpu)
    {
        TID = tid;
        Process = process;
        CPU = cpu;
    }

    public Task Clone(int flags, uint stackPtr, uint ptidPtr, uint tlsPtr, uint ctidPtr)
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
            newProc = Process;
        }
        else
        {
            var newMem = cloneVm ? Process.Mem : Process.Mem.Clone();
            var newSys = Process.Syscalls.Clone(newMem, cloneFiles);
            // UTS namespace is shared by default in fork/clone unless CLONE_NEWUTS is specified.
            // For now, always share the reference.
            newProc = new Process(NextPID(), newMem, newSys, Process.UTS);
        }

        int newTid = cloneThread ? NextPID() : newProc.TGID;
        var child = new Task(newTid, newProc, newCpu);
        child.Process.Syscalls.RegisterEngine(newCpu);

        if (!cloneThread)
        {
            child.Process.PPID = Process.TGID;
            lock (Process.Children)
            {
                Process.Children.Add(child.Process.TGID);
            }
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
            var buf = child.CPU.MemRead(tlsPtr + 4, 4);
            uint baseAddr = BinaryPrimitives.ReadUInt32LittleEndian(buf);
            child.CPU.SetSegBase(Seg.GS, baseAddr);
        }

        // TID Pointers
        if (cloneParentSetTid && ptidPtr != 0)
        {
            byte[] tidBuf = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(tidBuf, child.TID);
            CPU.MemWrite(ptidPtr, tidBuf);
        }

        if (cloneChildSetTid && ctidPtr != 0)
        {
            byte[] tidBuf = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(tidBuf, child.TID);
            child.CPU.MemWrite(ctidPtr, tidBuf);
        }

        if (cloneChildClearTid)
        {
            child.ChildClearTidPtr = ctidPtr;
        }

        // CLONE_VFORK: child will wake parent on exit/exec
        if (cloneVfork)
        {
            child.VforkParent = this;
        }

        Scheduler.Add(child);
        return child;
    }

    public async System.Threading.Tasks.Task RunLoopAsync()
    {
        try
        {
            while (!Exited)
            {
                await GIL.WaitAsync();
                try
                {
                    // Update current task context
                    Scheduler.CurrentTask = this;

                    // Inner hot-loop: keep running as long as we're the only task or quantum not hit
                    // Note: We don't want to stay in here FOREVER if we're doing syscalls, 
                    // but for compute-heavy tasks like CoreMark, we want to minimize GIL churn.
                    while (!Exited)
                    {
                        // Increase instruction quantum to amortize context switch cost
                        CPU.Run(0, 1000000);

                        var status = CPU.Status;
                        if (status == EmuStatus.Fault)
                        {
                            Logger.LogError("[Task {TID}] Fatal Fault at 0x{Eip:x} (Vector: {Vector})", TID, CPU.Eip, CPU.FaultVector);
                            Logger.LogError("[Task {TID}] Last 10 instructions:", TID);
                            lock (_traceBuffer)
                            {
                                foreach (var line in _traceBuffer) Logger.LogError("  {TraceLine}", line);
                            }
                            Logger.LogError("[Task {TID}] Current: {Registers}", TID, CPU.ToString());
                            Process.Mem.LogVMAs();
                            Exited = true;
                            break;
                        }
                        else if (status == EmuStatus.Yield)
                        {
                            if (BlockingTask != null)
                            {
                                var blockingTask = BlockingTask;
                                BlockingTask = null; // Clear it

                                GIL.Release();
                                try
                                {
                                    await blockingTask;
                                }
                                finally
                                {
                                    await GIL.WaitAsync();

                                    // Write result to EAX if it was a Task<int>
                                    if (blockingTask is System.Threading.Tasks.Task<int> intTask)
                                    {
                                        CPU.RegWrite(Reg.EAX, (uint)intTask.Result);
                                    }
                                }
                            }
                            else
                            {
                                // Cooperative handoff: only yield if others are waiting
                                if (Scheduler.ReadyCount > 0)
                                {
                                    await PauseAsync();
                                }
                            }
                        }
                        else if (status == EmuStatus.Stopped || status == EmuStatus.Running)
                        {
                            if (Scheduler.ReadyCount > 0)
                            {
                                await PauseAsync();
                            }
                        }
                        else
                        {
                            Exited = true;
                            break;
                        }

                        // Check for signals
                        if (!Exited && (PendingSignals & ~SignalMask) != 0)
                        {
                            // Find the first unblocked pending signal
                            int sig = -1;
                            ulong pending = PendingSignals & ~SignalMask;
                            for (int i = 1; i < 64; i++)
                            {
                                if ((pending & (1UL << (i - 1))) != 0)
                                {
                                    sig = i;
                                    break;
                                }
                            }

                            if (sig != -1)
                            {
                                // Clear pending bit
                                PendingSignals &= ~(1UL << (sig - 1));
                                
                                // Reset Stopped state if SIGCONT
                                if (sig == 18) // SIGCONT
                                {
                                    if(Process.State == ProcessState.Stopped) Process.State = ProcessState.Running;
                                }
                                
                                // Handle it
                                HandleSignal(sig);
                            }
                        }

                        // AGGRESSIVE OPTIMIZATION:
                        // Only release the GIL and yield if there are other tasks waiting.
                        if (Exited || Scheduler.ReadyCount > 0)
                        {
                            break;
                        }
                    }
                }
                finally
                {
                    Scheduler.CurrentTask = null;
                    GIL.Release();
                }

                // If we're the only task, yielding here would just put us back in the same loop.
                // But we must yield to the runtime at least occasionally.
                if (!Exited && Scheduler.ReadyCount > 0)
                {
                    await System.Threading.Tasks.Task.Yield();
                }
            }
        }
        finally
        {
            Exited = true;

            // Handle CLONE_CHILD_CLEARTID
            if (ChildClearTidPtr != 0)
            {
                await GIL.WaitAsync();
                try
                {
                    byte[] zero = new byte[4];
                    CPU.MemWrite(ChildClearTidPtr, zero);
                    Process.Syscalls.Futex.Wake(ChildClearTidPtr, 1);
                }
                catch { }
                finally { GIL.Release(); }
            }

            WaitEvent.Set();
            Scheduler.Remove(this);
            CPU.Dispose();
        }
    }

    // Pause this emulator task — scheduler will resume it by completing ResumeTcs
    private async System.Threading.Tasks.Task PauseAsync()
    {
        // Must be called with GIL held.
        
        // Optimize for the common case: only one task is ready
        if (Scheduler.ReadyCount == 0)
        {
            // If we're the only task, we don't even need to yield to the runtime 
            // most of the time. The inner-loop in RunLoopAsync will keep us here.
            // But if we're called explicitly, we can just return.
            return;
        }

        // Prepare a new TCS for resumption
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ResumeTcs = tcs;

        // Enqueue self as ready so scheduler can pick it later
        Scheduler.Enqueue(this);

        // Release GIL so other tasks can run while we're paused
        GIL.Release();
        
        try
        {
            // Pick next task to run
            Scheduler.ScheduleNext();

            // Await the resume signal
            await tcs.Task;
        }
        finally
        {
            // Re-acquire GIL now that we're being resumed
            await GIL.WaitAsync();
            
            // Clear the ResumeTcs now that we've been resumed
            ResumeTcs = null;
        }
    }

    public void DumpTrace()
    {
        Logger.LogError("[Task {TID}] Trace Dump (Last 1000 instructions):", TID);
        lock (_traceBuffer)
        {
            foreach (var line in _traceBuffer) Logger.LogError("  {TraceLine}", line);
        }
    }

    private void HandleSignal(int sig)
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
                     ExitCode = 128 + sig;
                     Console.WriteLine($"[Task {TID}] Terminated by signal {sig}");
                     return;
                }
                // Ignore others for now
                return;
            }

            // 2. Setup frame for handler
            // Push SigContext/StackFrame
            uint sp = CPU.RegRead(Reg.ESP);

            // Redzone check? x86 doesn't strictly have one like x64, but Sys V ABI aligns stack.
            // Check altstack
            if ((AltStackFlags & 1) == 0 && AltStackSp != 0 && (action.Flags & 0x08000000) != 0) // ONSTACK
            {
                 sp = AltStackSp + AltStackSize;
            }

            // Return address (restorer or some trampoline)
            // If SA_RESTORER is set, we push that as return address.
            // If not, we must provide a trampoline. Linux kernel puts one on stack (vdso or legacy stack).
            // We will inject a legacy-style trampoline on the stack if Restorer is 0.
            uint retAddr = action.Restorer;
            uint frameEsp = sp; // Initialize frameEsp here
            if (retAddr == 0)
            {
                 // Align 
                 frameEsp = (frameEsp - 4u) & ~0xFu;
                 
                 // Allocate space for trampoline (mov eax, 173; int 0x80) -> 7 bytes
                 frameEsp -= 8u; 
                 // 0xB8 0xAD 0x00 0x00 0x00 (mov eax, 173)
                 // 0xCD 0x80 (int 0x80)
                 byte[] trampoline = { 0xB8, 0xAD, 0x00, 0x00, 0x00, 0xCD, 0x80 };
                 CPU.MemWrite(frameEsp, trampoline);
                 retAddr = frameEsp;
            }

            // Setup Stack with SA_SIGINFO support
            SetupSigContext(sp, ref frameEsp, sig, action, retAddr);
            CPU.RegWrite(Reg.ESP, frameEsp);
            CPU.Eip = action.Handler;
            
            // SA_RESTART Logic
            // If the process was interrupted in a syscall that supports restart, EAX will be -ERESTARTSYS.
            int eax = (int)CPU.RegRead(Reg.EAX);
            if (eax == -512) // -ERESTARTSYS
            {
                 if ((action.Flags & LinuxConstants.SA_RESTART) != 0)
                 {
                     // Restart: rewind EIP to re-execute 'int 0x80' (CD 80)
                     CPU.Eip -= 2; // Assuming int 0x80 sequence
                     // Restore EAX to the syscall number?
                     // Linux kernel does this. But we don't track original syscall number in EAX easily here
                     // unless SyscallManager preserved it?
                     // Or we assume the guest saved it?
                     // For now, let's just implement EINTR conversion which is safer if we can't fully restart.
                     // IF we can't restore EAX, we can't restart.
                     
                     // Fallback: convert to EINTR even if SA_RESTART, unless we can recover syscall nr.
                     // TODO: Implement full restart support by saving syscall number.
                     CPU.RegWrite(Reg.EAX, unchecked((uint)-(int)Errno.EINTR));
                 }
                 else
                 {
                     // Interrupted: return -EINTR
                     CPU.RegWrite(Reg.EAX, unchecked((uint)-(int)Errno.EINTR));
                 }
            }
            
            // Mask signals during handler execution if SA_NODEFER not set?
            // (Assuming Block blocked signals, simplified)
        }
    }

    private void SetupSigContext(uint sp, ref uint esp, int sig, SigAction action, uint retAddr)
    {
         // If SA_SIGINFO is set, we need to setup ucontext and siginfo
         bool useSigInfo = (action.Flags & LinuxConstants.SA_SIGINFO) != 0;

         // Layout:
         // [Arguments (sig, ptr, ptr)] (if SA_SIGINFO)
         // [Arguments (sig)]           (if !SA_SIGINFO)
         // [RetAddr]
         // [SigInfo] (always for rt_sigaction)
         // [UContext] (always for rt_sigaction)

         // We always setup UContext and SigInfo because sys_rt_sigreturn expects them.
         // (Linux kernel always pushes rt_sigframe for rt_sigaction)
         
         uint ucontextAddr = 0;
         uint siginfoAddr = 0;

         // Push UContext (approx 400 bytes inc fpstate)
         // Align to 16 bytes
         esp = (esp - 512u) & ~0xFu; 
         ucontextAddr = esp;
         
         // Populate UContext
         // uc_flags, uc_link, uc_stack, uc_mcontext, uc_sigmask
         // mcontext is at offset 20 (4+4+12)
         uint mcontextOffset = 4 + 4 + 12;
         
         // Save registers to mcontext
         // GS, FS, ES, DS, EDI, ESI, EBP, ESP, EBX, EDX, ECX, EAX, TRAPNO, ERR, EIP, CS, EFL, UESP, SS
         // Registers are at mcontextOffset usually.
         // We use a simplified write.
         WriteSigContext(esp + mcontextOffset);
         
         // Push SigInfo (128 bytes)
         esp = (esp - 128u) & ~0xFu;
         siginfoAddr = esp;
         
         // Populate SigInfo
         // si_signo, si_errno, si_code
         byte[] siBuf = new byte[12];
         BinaryPrimitives.WriteInt32LittleEndian(siBuf.AsSpan(0, 4), sig);
         BinaryPrimitives.WriteInt32LittleEndian(siBuf.AsSpan(4, 4), 0); // errno
         BinaryPrimitives.WriteInt32LittleEndian(siBuf.AsSpan(8, 4), 0); // code (SI_USER)
         CPU.MemWrite(siginfoAddr, siBuf);

         // Align stack for arguments
         esp = (esp - 4u) & ~0xFu;
         
         // Check if we force 3 args?
         // Push 3 args: sig, siginfo_ptr, ucontext_ptr
         esp -= 4u; CPU.MemWrite(esp, BitConverter.GetBytes(ucontextAddr));
         esp -= 4u; CPU.MemWrite(esp, BitConverter.GetBytes(siginfoAddr));
         esp -= 4u; CPU.MemWrite(esp, BitConverter.GetBytes(sig));
         
         // Push Return Address
         // retAddr points to trampoline or restorer that calls sys_rt_sigreturn
         esp -= 4u;
         CPU.MemWrite(esp, BitConverter.GetBytes(retAddr));
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
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(0), 0); // GS
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(4), 0); // FS
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(8), 0); // ES
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(12), 0x2B); // DS (user data)
            
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(16), CPU.RegRead(Reg.EDI));
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(20), CPU.RegRead(Reg.ESI));
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(24), CPU.RegRead(Reg.EBP));
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(28), CPU.RegRead(Reg.ESP));
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(32), CPU.RegRead(Reg.EBX));
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(36), CPU.RegRead(Reg.EDX));
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(40), CPU.RegRead(Reg.ECX));
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(44), CPU.RegRead(Reg.EAX));
            
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(56), CPU.Eip);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(60), 0x23); // CS (user code)
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(64), CPU.Eflags);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(68), CPU.RegRead(Reg.ESP));
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(72), 0x2B); // SS
            
            CPU.MemWrite(addr, buf);
        }
        catch { }
    }

    public void RestoreSigContext(uint addr)
    {
        // Reverse of WriteSigContext
        // offset 16: edi, esi, ebp, esp, ebx, edx, ecx, eax
        // offset 56: eip, cs, efl, uesp, ss
        try
        {
            byte[] buf = CPU.MemRead(addr, 80);
            var s = buf.AsSpan();
            
            CPU.RegWrite(Reg.EDI, BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(16)));
            CPU.RegWrite(Reg.ESI, BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(20)));
            CPU.RegWrite(Reg.EBP, BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(24)));
            CPU.RegWrite(Reg.ESP, BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(28))); // This overwrites current ESP
            CPU.RegWrite(Reg.EBX, BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(32)));
            CPU.RegWrite(Reg.EDX, BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(36)));
            CPU.RegWrite(Reg.ECX, BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(40)));
            CPU.RegWrite(Reg.EAX, BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(44)));
            
            CPU.Eip = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(56));
            CPU.Eflags = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(64));
        }
        catch { }
    }
}

public static class Scheduler
{
    private static readonly Queue<Task> _readyQueue = new();
    private static readonly Dictionary<int, Task> _tasks = new();
    private static readonly Dictionary<int, Process> _processes = new();
    private static readonly Dictionary<IntPtr, Task> _engineToTask = new();
    public static int ReadyCount { get { lock (_lock) return _readyQueue.Count; } }
    private static readonly object _lock = new();

    public static readonly AsyncLocal<Task?> _currentTask = new();
    public static Task? CurrentTask
    {
        get => _currentTask.Value;
        set => _currentTask.Value = value;
    }
    
    public static void Add(Task t)
    {
        lock (_lock)
        {
            _tasks[t.TID] = t;
            _engineToTask[t.CPU.State] = t;
            Console.WriteLine($"[Scheduler] Registered Task {t.TID} with Engine 0x{t.CPU.State:x}");
            _processes[t.Process.TGID] = t.Process;
        }
    }

    // Enqueue a task that is ready to run.
    public static void Enqueue(Task t)
    {
        lock (_lock)
        {
            _readyQueue.Enqueue(t);
        }
    }

    // Pick the next task to run and signal it.
    public static void ScheduleNext()
    {
        lock (_lock)
        {
            if (_readyQueue.Count > 0)
            {
                var t = _readyQueue.Dequeue();
                try { t.ResumeTcs?.TrySetResult(true); } catch { }
            }
        }
    }

    public static void Remove(Task t)
    {
        lock (_lock)
        {
            _tasks.Remove(t.TID);
            _engineToTask.Remove(t.CPU.State);
        }
    }

    public static void Remove(int tid)
    {
        lock (_lock)
        {
            if (_tasks.TryGetValue(tid, out var t))
            {
                _tasks.Remove(tid);
                _engineToTask.Remove(t.CPU.State);
            }
        }
    }

    public static Task? Get(int tid)
    {
        lock (_lock) return _tasks.TryGetValue(tid, out var t) ? t : null;
    }

    public static Task? GetByEngine(IntPtr state)
    {
        lock (_lock)
        {
            return _engineToTask.TryGetValue(state, out var t) ? t : null;
        }
    }

    // New methods for fork/wait support
    public static Process? GetProcessByPID(int pid)
    {
        lock (_lock)
        {
            return _processes.TryGetValue(pid, out var p) ? p : null;
        }
    }

    public static void RemoveProcess(int pid)
    {
        lock (_lock)
        {
            _processes.Remove(pid);

            // Also remove any remaining tasks for this process
            var toRemove = _tasks.Where(kv => kv.Value.Process.TGID == pid).ToList();
            foreach (var kv in toRemove)
            {
                _tasks.Remove(kv.Key);
                _engineToTask.Remove(kv.Value.CPU.State);
            }
        }
    }

    public static List<Process> GetAllProcesses()
    {
        lock (_lock) return _processes.Values.ToList();
    }

    public static Task? GetCurrent()
    {
        // Return current task from AsyncLocal
        return CurrentTask;
    }
}
