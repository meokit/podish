using Fiberish.Loader;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.X86.Native;
using System.Text;

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
    private static readonly byte[] EmptyCmdline = [];

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

    // Controlling terminal (set by TIOCSCTTY)
    public VFS.TTY.TtyDiscipline? ControllingTty { get; set; }

    // Other process state
    public int Umask { get; set; } = 18; // Default 022 octal is 18 decimal
    public Fiberish.Core.Timer? AlarmTimer { get; set; }

    // POSIX Timers
    public Dictionary<int, PosixTimer> PosixTimers { get; } = [];
    public int NextPosixTimerId { get; set; } = 0;

    // Parent-child relationship
    public int PPID { get; set; } = 0; // Parent Process ID
    public List<int> Children { get; } = []; // Child Process IDs
    public ProcessState State { get; set; } = ProcessState.Running;
    public int ExitStatus { get; set; } = 0;
    public bool ExitedBySignal { get; set; }
    public int TermSignal { get; set; }
    public bool CoreDumped { get; set; }
    public bool HasWaitableStop { get; set; }
    public int StopSignal { get; set; }
    public bool HasWaitableContinue { get; set; }
    public string ExecutablePath { get; private set; } = string.Empty;
    public string[] CommandLineArguments { get; private set; } = [];
    public byte[] CommandLineRaw { get; private set; } = EmptyCmdline;
    public string Comm { get; private set; } = "process";
    public string Name { get => Comm; set => Comm = value; }

    // Event signaled when process state changes (exit, stop, continue)
    // Used by parent's wait4() to avoid busy-polling
    public AsyncWaitQueue StateChangeEvent { get; } = new();

    public Dictionary<int, SigAction> SignalActions { get; } = [];

    public void LoadExecutable(string exe, string[] args, string[] envs)
    {
        UpdateProcessImage(exe, args);

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
            if (!Syscalls.Mem.MapAnonymousPage(addr, engine, Protection.Read | Protection.Write))
                throw new InvalidOperationException($"Failed to allocate stack page at 0x{addr:x}");

        if (!engine.CopyToUser(spBase, stackData))
            throw new InvalidOperationException("Failed to write initial stack content to guest memory");
    }

    internal void Exec(string exePath, string[] args, string[] envs)
    {
        // 1. Clear Memory
        Syscalls.Mem.Clear(Syscalls.Engine);

        // 1.1 Linux exec semantics: handlers set to user function are reset to SIG_DFL.
        // Dispositions explicitly set to SIG_IGN stay ignored.
        ResetSignalDispositionsForExec();

        // 2. Re-setup vDSO (Important! execve clears memory map including vDSO)
        Syscalls.SetupVDSO();

        // 3. Load Executable
        // LoadExecutable handles ELF loading, stack setup and CPU state reset
        LoadExecutable(exePath, args, envs);
    }

    private void ResetSignalDispositionsForExec()
    {
        if (SignalActions.Count == 0) return;

        var reset = new List<int>();
        foreach (var (sig, action) in SignalActions)
        {
            // Keep SIG_IGN; reset user handlers to default.
            if (action.Handler > 1) reset.Add(sig);
        }

        foreach (var sig in reset)
        {
            SignalActions.Remove(sig);
        }
    }

    public void CopyImageFrom(Process src)
    {
        ExecutablePath = src.ExecutablePath;
        CommandLineArguments = [.. src.CommandLineArguments];
        CommandLineRaw = [.. src.CommandLineRaw];
        Comm = src.Comm;
    }

    private void UpdateProcessImage(string exe, string[] args)
    {
        ExecutablePath = exe;
        CommandLineArguments = [.. args];

        var commBase = args.Length > 0 ? args[0] : exe;
        Comm = Path.GetFileName(commBase);
        if (string.IsNullOrEmpty(Comm)) Comm = "process";

        if (args.Length == 0)
        {
            CommandLineRaw = EmptyCmdline;
            return;
        }

        var cmdline = string.Join('\0', args) + '\0';
        CommandLineRaw = Encoding.UTF8.GetBytes(cmdline);
    }
}
