using Fiberish.Core.VFS.TTY;
using Fiberish.Loader;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.X86.Native;

namespace Fiberish.Core;

public enum IdType
{
    P_ALL = 0, // Wait for any child
    P_PID = 1, // Wait for specific PID
    P_PGID = 2 // Wait for process group (not implemented)
}

public enum ProcessState
{
    Running,
    Sleeping,
    Stopped, // Suspended by signal
    Continued, // Resumed from stopped state
    Zombie, // Exited but not reaped by parent
    Dead // Reaped by parent
}

public class SigInfo
{
    public int si_code;
    public int si_errno;
    public int si_pid;
    public int si_signo;
    public int si_status;
    public int si_uid;
}

public class UTSNamespace
{
    public string SysName { get; set; } = "Linux";
    public string NodeName { get; set; } = "x86emu";
    public string Release { get; set; } = "6.1.0";
    public string Version { get; set; } = "#1 SMP PREEMPT";
    public string Machine { get; set; } = "i686";
    public string DomainName { get; set; } = "(none)";

    public UTSNamespace Clone()
    {
        return (UTSNamespace)MemberwiseClone();
    }
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
    // Interrupt handling for blocking syscalls
    // When a signal arrives, if a task is blocked in a syscall (Sleeping/Waiting),
    // we need to interrupt it.
    private Action? _interruptHandler;

    public bool traceInstruction;

    public Process(int tgid, VMAManager mem, SyscallManager syscalls, UTSNamespace? uts = null)
    {
        TGID = tgid;
        Mem = mem;
        Syscalls = syscalls;
        UTS = uts ?? new UTSNamespace();

        // Default to root
        UID = GID = EUID = EGID = SUID = SGID = FSUID = FSGID = 0;
    }

    public int TGID { get; set; }
    public VMAManager Mem { get; set; }
    public SyscallManager Syscalls { get; set; }

    // Thread Management
    public List<FiberTask> Threads { get; } = [];

    // Credentials
    public int SID { get; set; } // Session ID
    public int PGID { get; set; }
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
    public int PPID { get; set; } = 0; // Parent Process ID
    public List<int> Children { get; } = []; // Child Process IDs
    public ProcessState State { get; set; } = ProcessState.Running;
    public int ExitStatus { get; set; } = 0;

    // Use our new single-threaded compatible synchronization primitive
    public AsyncWaitQueue ZombieEvent { get; } = new();

    public Dictionary<int, SigAction> SignalActions { get; } = [];

    // vDSO addresses
    public uint SigReturnAddr { get; set; }
    public uint RtSigReturnAddr { get; set; }
    public bool WasInterrupted { get; private set; }

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

    public void LoadExecutable(string exe, string[] args, string[] envs)
    {
        var res = ElfLoader.Load(exe, Syscalls, args, envs);
        Syscalls.BrkAddr = res.BrkAddr;

        // Setup CPU State
        var engine = Syscalls.Engine;
        engine.Eip = res.Entry;
        engine.RegWrite(Reg.ESP, res.SP);
        engine.Eflags = 0x202;

        // Setup Stack
        var spBase = res.SP;
        var stackData = res.InitialStack;
        for (var addr = spBase & LinuxConstants.PageMask;
             addr < ((spBase + (uint)stackData.Length + LinuxConstants.PageSize - 1) & LinuxConstants.PageMask);
             addr += LinuxConstants.PageSize)
            if (engine.AllocatePage(addr, (byte)(Protection.Read | Protection.Write)) == IntPtr.Zero)
                throw new InvalidOperationException($"Failed to allocate stack page at 0x{addr:x}");

        if (!engine.CopyToUser(spBase, stackData))
            throw new InvalidOperationException("Failed to write initial stack content to guest memory");
    }

    internal void Exec(string exePath, string[] args, string[] envs)
    {
        // 1. Clear Memory
        Syscalls.Mem.Clear(Syscalls.Engine);

        // 2. Re-setup vDSO (Important! execve clears memory map including vDSO)
        Syscalls.SetupVDSO();

        // 3. Load Executable
        // LoadExecutable handles ELF loading, stack setup and CPU state reset
        LoadExecutable(exePath, args, envs);
    }

    public static FiberTask Spawn(string exePath, string[] args, string[] envs, string rootRes, bool traceInstructions,
        bool strace, KernelScheduler scheduler, TtyDiscipline? tty = null)
    {
        // 1. Init System Components
        var engine = new Engine();
        var mm = new VMAManager();

        // 2. Init Syscalls
        var sys = new SyscallManager(engine, mm, 0, rootRes, tty)
        {
            Strace = strace
        };
        ProcFsManager.Init(sys);

        // 3. Create Process
        var proc = new Process(FiberTask.NextTID(), mm, sys)
        {
            traceInstruction = traceInstructions
        };
        proc.PGID = proc.TGID; // Process Group Leader
        proc.SID = proc.TGID; // Session Leader

        if (tty != null)
        {
            // Initial process is the session leader and has the TTY as controlling terminal
            tty.SessionId = proc.SID;
            tty.ForegroundPgrp = proc.PGID;
        }

        scheduler.RegisterProcess(proc);

        // 4. Create Main Task
        var mainTask = new FiberTask(proc.TGID, proc, engine, scheduler);

        // 5. Register with Scheduler and ProcFs
        // FiberTask ctor already registers with KernelScheduler
        // ProcFsManager.OnProcessStart(sys, proc.TGID);

        // 6. Connect Engine Context
        engine.Owner = mainTask;

        // 7. Load Executable
        proc.LoadExecutable(exePath, args, envs);

        return mainTask;
    }
}