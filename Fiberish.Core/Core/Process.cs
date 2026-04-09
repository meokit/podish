using System.Text;
using Fiberish.Core.Utils;
using Fiberish.Core.VFS.TTY;
using Fiberish.Loader;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
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

public enum ProcessKind
{
    Normal,
    VirtualDaemon
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
    public const int CapabilitySysAdmin = 21;
    private static readonly byte[] EmptyCmdline = [];

    // Event signaled when process state changes (exit, stop, continue)
    // Used by parent's wait4() to avoid busy-polling
    private AsyncWaitQueue? _stateChangeEvent;

    public Process(int tgid, VMAManager mem, SyscallManager syscalls, UTSNamespace? uts = null)
    {
        TGID = tgid;
        Mem = mem;
        Syscalls = syscalls;
        UTS = uts ?? new UTSNamespace();

        // Default to root
        UID = GID = EUID = EGID = SUID = SGID = FSUID = FSGID = 0;
        // Minimal capability model: root starts with CAP_SYS_ADMIN effective+permitted.
        SetCapability(CapabilitySysAdmin, true, true, false);
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
    public List<int> SupplementaryGroups { get; } = [];
    public uint[] CapEffective { get; } = [0U, 0U];
    public uint[] CapPermitted { get; } = [0U, 0U];
    public uint[] CapInheritable { get; } = [0U, 0U];

    // Namespaces
    public UTSNamespace UTS { get; set; }

    // Controlling terminal (set by TIOCSCTTY)
    public TtyDiscipline? ControllingTty { get; set; }

    // Other process state
    public int Umask { get; set; } = 18; // Default 022 octal is 18 decimal
    public Timer? AlarmTimer { get; set; }
    public long ItimerRealIntervalMs { get; set; }

    // POSIX Timers
    public Dictionary<int, PosixTimer> PosixTimers { get; } = [];
    public int NextPosixTimerId { get; set; } = 0;

    // Parent-child relationship
    public int PPID { get; set; } = 0; // Parent Process ID
    public List<int> Children { get; } = []; // Child Process IDs
    public ProcessState State { get; set; } = ProcessState.Running;
    public ProcessKind Kind { get; set; } = ProcessKind.Normal;
    public string? VirtualDaemonName { get; set; }
    public int ExitStatus { get; set; } = 0;
    public bool ExitedBySignal { get; set; }
    public int TermSignal { get; set; }
    public bool CoreDumped { get; set; }
    public bool HasWaitableStop { get; set; }
    public int StopSignal { get; set; }
    public bool HasWaitableContinue { get; set; }
    public bool MemoryReleased { get; set; }
    public int MembarrierRegisteredCommands { get; set; }
    public string ExecutablePath { get; private set; } = string.Empty;
    public string[] CommandLineArguments { get; private set; } = [];
    public byte[] CommandLineRaw { get; private set; } = EmptyCmdline;
    public string Comm { get; private set; } = "process";

    public string Name
    {
        get => Comm;
        set => Comm = value;
    }

    public AsyncWaitQueue StateChangeEvent => _stateChangeEvent
                                              ?? throw new InvalidOperationException(
                                                  $"Process {TGID} state-change queue is not bound to a scheduler.");

    public Dictionary<int, SigAction> SignalActions { get; } = [];
    public ulong PendingProcessSignals { get; set; }
    public Locked<List<SigInfo>> PendingProcessSignalQueue { get; } = new(new List<SigInfo>());

    internal void BindScheduler(KernelScheduler scheduler)
    {
        _stateChangeEvent ??= new AsyncWaitQueue(scheduler);
    }

    public bool EnqueueProcessSignal(SigInfo info)
    {
        var sig = info.Signo;
        if (sig < 1 || sig > 64) return false;

        var mask = 1UL << (sig - 1);
        PendingProcessSignals |= mask;

        PendingProcessSignalQueue.Lock(q =>
        {
            if (sig < 32)
                foreach (var queued in q)
                    if (queued.Signo == sig)
                        return;

            q.Add(info);
        });

        return true;
    }

    public SigInfo? DequeueProcessSignalUnsafe(int sig)
    {
        return PendingProcessSignalQueue.Lock(list =>
        {
            for (var i = 0; i < list.Count; i++)
                if (list[i].Signo == sig)
                {
                    var info = list[i];
                    list.RemoveAt(i);

                    if (sig >= 32)
                    {
                        var stillPending = false;
                        foreach (var queued in list)
                            if (queued.Signo == sig)
                            {
                                stillPending = true;
                                break;
                            }

                        if (!stillPending) PendingProcessSignals &= ~(1UL << (sig - 1));
                    }
                    else
                    {
                        PendingProcessSignals &= ~(1UL << (sig - 1));
                    }

                    return (SigInfo?)info;
                }

            return null;
        });
    }

    public void ClearPendingProcessSignals()
    {
        PendingProcessSignals = 0;
        PendingProcessSignalQueue.Lock(q => q.Clear());
    }

    public void SetCapability(int capability, bool effective, bool permitted, bool inheritable)
    {
        SetCapabilityBit(CapEffective, capability, effective);
        SetCapabilityBit(CapPermitted, capability, permitted);
        SetCapabilityBit(CapInheritable, capability, inheritable);
    }

    public bool HasEffectiveCapability(int capability)
    {
        if (capability < 0) return false;
        var word = capability / 32;
        if ((uint)word >= (uint)CapEffective.Length) return false;
        var bit = capability % 32;
        return (CapEffective[word] & (1u << bit)) != 0;
    }

    public bool HasEffectiveCapabilityOrRoot(int capability)
    {
        return EUID == 0 || HasEffectiveCapability(capability);
    }

    private static void SetCapabilityBit(uint[] target, int capability, bool enabled)
    {
        if (capability < 0) return;
        var word = capability / 32;
        if ((uint)word >= (uint)target.Length) return;
        var bit = capability % 32;
        var mask = 1u << bit;
        if (enabled)
            target[word] |= mask;
        else
            target[word] &= ~mask;
    }

    public void LoadExecutable(Dentry dentry, string guestPath, string[] args, string[] envs, Mount mount)
    {
        var resolved = ResolveExecutableImage(dentry, guestPath, args, mount);
        UpdateProcessImage(resolved.GuestPath, resolved.Args);

        var res = ElfLoader.Load(resolved.Dentry, resolved.GuestPath, Syscalls, resolved.Args, envs, resolved.Mount);
        Syscalls.BrkAddr = res.BrkAddr;

        // Setup CPU State
        var engine = Syscalls.CurrentSyscallEngine;
        engine.Eip = res.Entry;
        engine.RegWrite(Reg.ESP, res.SP);
        engine.Eflags = 0x202;

        // Setup Stack
        var spBase = res.SP;
        var stackData = res.InitialStack;
        var stackStart = spBase & LinuxConstants.PageMask;
        var stackLength = ((spBase + (uint)stackData.Length + LinuxConstants.PageSize - 1) & LinuxConstants.PageMask) -
                          stackStart;
        if (!Syscalls.Mem.PrefaultRange(stackStart, stackLength, engine, true))
            throw new OutOfMemoryException($"Failed to allocate initial stack pages at 0x{stackStart:x}");

        if (!engine.CopyToUser(spBase, stackData))
            throw new InvalidOperationException("Failed to write initial stack content to guest memory");
    }

    private (Dentry Dentry, string GuestPath, string[] Args, Mount Mount) ResolveExecutableImage(
        Dentry dentry,
        string guestPath,
        string[] args,
        Mount mount)
    {
        const int maxShebangDepth = 4;

        for (var depth = 0; depth < maxShebangDepth; depth++)
        {
            var headerBuf = new byte[256];
            var headerLen = 0;
            if (dentry.Inode != null)
            {
                using var file = new LinuxFile(dentry, FileFlags.O_RDONLY, mount);
                headerLen = dentry.Inode.ReadToHost(null, file, headerBuf.AsSpan(), 0);
                if (headerLen < 0) headerLen = 0;
            }

            if (headerLen < 2 || headerBuf[0] != '#' || headerBuf[1] != '!')
                return (dentry, guestPath, args, mount);

            var lineEnd = Array.IndexOf(headerBuf, (byte)'\n', 2);
            if (lineEnd < 0) lineEnd = headerLen;

            var shebangLine = Encoding.UTF8.GetString(headerBuf, 2, lineEnd - 2).Trim();
            if (string.IsNullOrWhiteSpace(shebangLine))
                throw new InvalidDataException($"Invalid shebang line in '{guestPath}'.");

            var interpPath = shebangLine;
            string? interpArg = null;
            var splitIdx = shebangLine.IndexOfAny([' ', '\t']);
            if (splitIdx >= 0)
            {
                interpPath = shebangLine[..splitIdx];
                interpArg = shebangLine[(splitIdx + 1)..].Trim();
                if (string.IsNullOrEmpty(interpArg))
                    interpArg = null;
            }

            var (interpLoc, interpGuestPath) = Syscalls.ResolvePath(interpPath);
            if (!interpLoc.IsValid)
                throw new FileNotFoundException($"Interpreter not found in VFS: {interpPath}");

            List<string> newArgs = [interpPath];
            if (interpArg != null)
                newArgs.Add(interpArg);
            newArgs.Add(guestPath);
            if (args.Length > 1)
                newArgs.AddRange(args.Skip(1));

            dentry = interpLoc.Dentry!;
            guestPath = interpGuestPath;
            args = [.. newArgs];
            mount = interpLoc.Mount!;
        }

        throw new InvalidDataException($"Shebang recursion too deep for '{guestPath}'.");
    }

    internal void Exec(Dentry dentry, string guestPath, string[] args, string[] envs, Mount mount)
    {
        var oldMem = Mem;
        var oldEngine = Syscalls.CurrentSyscallEngine;
        var hadSharedAddressSpace = oldMem.GetSharedRefCount() > 1;
        var shouldDetachSysVShm = oldMem.GetSharedRefCount() == 1;

        // Linux execve semantics: detach SysV SHM segments from the old address space.
        // For shared address spaces, defer detach to the final owner.
        if (shouldDetachSysVShm)
            Syscalls.SysVShm.OnProcessExit(TGID, oldMem, oldEngine, this);

        // 1. Replace memory with a fresh VMAManager.
        // This is critical for vfork+execve: the child may share the parent's VMAManager
        // via CLONE_VM. We must NOT clear the shared memory — instead, create a private
        // VMAManager for this process before proceeding.
        var freshMem = new VMAManager();
        if (hadSharedAddressSpace)
            freshMem.BindAddressSpaceHandle(ProcessAddressSpaceHandle.DetachFromSharedEngine(oldEngine));
        else
            freshMem.BindAddressSpaceHandle(ProcessAddressSpaceHandle.CaptureAttachedEngine(oldEngine));
        Mem = freshMem;
        Syscalls.Mem = freshMem;
        ProcessAddressSpaceSync.RebindEngineAddressSpace(oldMem, freshMem, oldEngine);
        MemoryReleased = false;
        oldMem.ReleaseSharedRef(oldEngine);

        // Clear old native MMU page tables + translated block cache.
        // ResetAllCodeCache only clears cached translated blocks; ResetMemory also resets the native page directory
        // so the new binary's pages are demand-faulted fresh (not stale from old image).
        Syscalls.CurrentSyscallEngine.ResetMemory();

        // 1.1 Linux exec semantics: handlers set to user function are reset to SIG_DFL.
        // Dispositions explicitly set to SIG_IGN stay ignored.
        ResetSignalDispositionsForExec();

        // 2. Re-setup vDSO (Important! execve clears memory map including vDSO)
        Syscalls.SetupVDSO();

        // 3. Load Executable
        // LoadExecutable handles ELF loading, stack setup and CPU state reset
        LoadExecutable(dentry, guestPath, args, envs, mount);
    }

    private void ResetSignalDispositionsForExec()
    {
        if (SignalActions.Count == 0) return;

        var reset = new List<int>();
        foreach (var (sig, action) in SignalActions)
            // Keep SIG_IGN; reset user handlers to default.
            if (action.Handler > 1)
                reset.Add(sig);

        foreach (var sig in reset) SignalActions.Remove(sig);
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