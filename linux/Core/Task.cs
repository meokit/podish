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

    public int ExitCode { get; set; }
    public bool Exited { get; set; }
    public ManualResetEventSlim WaitEvent { get; } = new(false);
    public uint ChildClearTidPtr { get; set; }
    public Task? VforkParent { get; set; } = null;  // For CLONE_VFORK
    
    // Async support: the task that is currently blocking this emulator task
    public System.Threading.Tasks.Task? BlockingTask { get; set; }
    
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
                    
                    CPU.Run(0, 1000);
                    
                    var status = CPU.Status;
                    if (status == EmuStatus.Fault)
                    {
                        Logger.LogError("[Task {TID}] Fatal Fault at 0x{Eip:x}", TID, CPU.Eip);
                        Exited = true;
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
                            await System.Threading.Tasks.Task.Yield();
                        }
                    }
                    else if (status == EmuStatus.Stopped)
                    {
                        // Some normal stops (like voluntary yield)
                        if (Process.Syscalls.Strace) Logger.LogTrace("[Task {TID}] Stopped.", TID);
                        await System.Threading.Tasks.Task.Yield();
                    }
                    else
                    {
                        if (Process.Syscalls.Strace) Logger.LogTrace("[Task {TID}] Unhandled status {Status}, exiting.", TID, status);
                        Exited = true;
                    }
                }
                finally
                {
                    Scheduler.CurrentTask = null;
                    GIL.Release();
                }
                
                // Cooperative yielding
                await System.Threading.Tasks.Task.Yield();
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
}

public static class Scheduler
{
    private static readonly Dictionary<int, Task> _tasks = new();
    private static readonly Dictionary<int, Process> _processes = new();
    private static readonly Dictionary<IntPtr, Task> _engineToTask = new();
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
            _processes[t.Process.TGID] = t.Process;
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
