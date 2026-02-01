using System.Buffers.Binary;
using Bifrost.Memory;
using Bifrost.Syscalls;
using Bifrost.Native;

namespace Bifrost.Core;

public class Process
{
    public int TGID { get; set; }
    public VMAManager Mem { get; set; }
    public SyscallManager Syscalls { get; set; }

    public Process(int tgid, VMAManager mem, SyscallManager syscalls)
    {
        TGID = tgid;
        Mem = mem;
        Syscalls = syscalls;
    }
}

public class Task
{
    // Global Lock for serialization (GIL)
    public static readonly object GIL = new();

    private static int _pidCounter = 1000;
    public static int NextPID() => Interlocked.Increment(ref _pidCounter);

    public int TID { get; set; }
    public Process Process { get; set; }
    public Engine CPU { get; set; }

    public int ExitCode { get; set; }
    public bool Exited { get; set; }
    public ManualResetEventSlim WaitEvent { get; } = new(false);

    public Task(int tid, Process process, Engine cpu)
    {
        TID = tid;
        Process = process;
        CPU = cpu;
    }

    public Task Clone(int flags, uint stackPtr, uint ptidPtr, uint tlsPtr, uint ctidPtr)
    {
        bool cloneVm = (flags & 0x00000100) != 0;
        bool cloneFiles = (flags & 0x00000400) != 0;
        bool cloneThread = (flags & 0x00010000) != 0;
        bool cloneSetTls = (flags & 0x00080000) != 0;

        // 1. Clone CPU
        // If cloneVm is true, C++ Engine clones the state sharing the memory.
        var newCpu = CPU.Clone(cloneVm);

        // 2. Resource Management
        Process newProc;

        if (cloneThread)
        {
            // Thread: Share Process
            newProc = Process;
        }
        else
        {
            // Fork/New Process
            var newMem = cloneVm ? Process.Mem : Process.Mem.Clone();
            
            // For SyscallManager, we need a way to clone it or create a new one sharing FDs?
            // If cloneFiles is true, we share FDs. 
            // Current SyscallManager implementation couples FDs and Logic.
            // For now, we will use a helper to Clone the SyscallManager wrapper
            // that might share the underlying FD table.
            var newSys = Process.Syscalls.Clone(newMem, cloneFiles);

            newProc = new Process(NextPID(), newMem, newSys);
        }

        int newTid = cloneThread ? NextPID() : newProc.TGID;

        var child = new Task(newTid, newProc, newCpu);
        
        // Register the new engine to the (possibly new) SyscallManager
        // If it's a thread, it shares the SyscallManager, so we register the new CPU to it.
        child.Process.Syscalls.RegisterEngine(newCpu);

        // 3. Setup Child State
        if (stackPtr != 0)
        {
            child.CPU.RegWrite(Reg.ESP, stackPtr);
        }
        child.CPU.RegWrite(Reg.EAX, 0); // Return 0 to child

        // TLS
        if (cloneSetTls && tlsPtr != 0)
        {
            // struct user_desc { uint32 entry; uint32 base; ... }
            // base is at offset 4
            var buf = child.CPU.MemRead(tlsPtr + 4, 4);
            uint baseAddr = BinaryPrimitives.ReadUInt32LittleEndian(buf);
            child.CPU.SetSegBase(Seg.GS, baseAddr);
        }
        
        // TODO: Handle ptidPtr (write child TID to parent memory)
        // TODO: Handle ctidPtr (write child TID to child memory)

        Scheduler.Add(child);
        return child;
    }

    public void RunLoop()
    {
        try
        {
            while (!Exited)
            {
                lock (GIL)
                {
                    // Update current task context if we had one
                    Scheduler.CurrentTask = this;
                    
                    CPU.Run(0, 1000);
                    
                    var status = CPU.Status;
                    if (status == EmuStatus.Fault)
                    {
                        // Double check if fault was handled?
                        // The engine stops on fault.
                        // If we are here, it means it stopped.
                        // We can check if it was handled by checking if status is back to Running? 
                        // No, Native Run returns.
                        // Actually, if FaultHandler recovers, it might set status?
                        // But usually we just break.
                        // Let's assume if it returns with Fault, it's fatal unless handled.
                        // For now, simple exit.
                        Console.WriteLine($"[Task {TID}] Fatal Fault at 0x{CPU.Eip:x}");
                        Exited = true;
                    }
                    
                    Scheduler.CurrentTask = null;
                }
                
                // Yield to let other threads acquire GIL
                Thread.Yield();
            }
        }
        finally
        {
            Exited = true;
            WaitEvent.Set();
            Scheduler.Remove(TID);
            
            // Cleanup
            CPU.Dispose();
        }
    }
}

public static class Scheduler
{
    private static readonly Dictionary<int, Task> _tasks = new();
    private static readonly object _lock = new();
    
    [ThreadStatic]
    public static Task? CurrentTask;

    public static void Add(Task t)
    {
        lock (_lock) _tasks[t.TID] = t;
    }

    public static void Remove(int tid)
    {
        lock (_lock) _tasks.Remove(tid);
    }

    public static Task? Get(int tid)
    {
        lock (_lock) return _tasks.TryGetValue(tid, out var t) ? t : null;
    }
    
    // Helper to get task from Engine (via Registry in SyscallManager or here)
    // For now we rely on SyscallManager's registry or ThreadStatic if we are in the thread.
}
