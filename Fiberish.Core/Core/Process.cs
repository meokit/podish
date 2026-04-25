using Fiberish.Auth.Permission;
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

public readonly record struct ResourceLimit(ulong Soft, ulong Hard);

public readonly record struct CpuTimeSnapshot(long UserNs, long SystemNs)
{
    public static CpuTimeSnapshot operator +(CpuTimeSnapshot left, CpuTimeSnapshot right)
    {
        return new CpuTimeSnapshot(left.UserNs + right.UserNs, left.SystemNs + right.SystemNs);
    }
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
    private static readonly ResourceLimit[] DefaultResourceLimits = CreateDefaultResourceLimits();

    // Event signaled when process state changes (exit, stop, continue)
    // Used by parent's wait4() to avoid busy-polling
    private AsyncWaitQueue? _stateChangeEvent;

    public Process(int tgid, VMAManager mem, SyscallManager syscalls, UTSNamespace? uts = null)
    {
        ArgumentNullException.ThrowIfNull(mem);
        TGID = tgid;
        Mem = mem;
        Syscalls = syscalls;
        if (syscalls != null && !ReferenceEquals(mem.MemoryContext, syscalls.MemoryContext))
            throw new InvalidOperationException(
                "Process VMAManager and SyscallManager must share the same MemoryRuntimeContext.");
        UTS = uts ?? new UTSNamespace();

        // Default to root
        UID = GID = EUID = EGID = SUID = SGID = FSUID = FSGID = 0;
        // Minimal capability model: root starts with CAP_SYS_ADMIN effective+permitted.
        SetCapability(CapabilitySysAdmin, true, true, false);
        ResourceLimits = (ResourceLimit[])DefaultResourceLimits.Clone();
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
    public byte[] ExecutablePathRaw { get; private set; } = [];
    public string ExecutablePath => FsEncoding.DecodeUtf8Lossy(ExecutablePathRaw);
    public byte[][] CommandLineArgumentBytes { get; private set; } = [];

    public string[] CommandLineArguments =>
        CommandLineArgumentBytes.Select(a => FsEncoding.DecodeUtf8Lossy(a)).ToArray();

    public byte[] CommandLineRaw { get; private set; } = EmptyCmdline;
    public string Comm { get; private set; } = "process";
    public ResourceLimit[] ResourceLimits { get; }

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
    public long ExitedThreadsUserCpuTimeNs { get; private set; }
    public long ExitedThreadsSystemCpuTimeNs { get; private set; }
    public long ChildrenUserCpuTimeNs { get; private set; }
    public long ChildrenSystemCpuTimeNs { get; private set; }

    private bool HasFrozenCpuTimeSnapshot { get; set; }
    private long FrozenUserCpuTimeNs { get; set; }
    private long FrozenSystemCpuTimeNs { get; set; }
    private long FrozenChildrenUserCpuTimeNs { get; set; }
    private long FrozenChildrenSystemCpuTimeNs { get; set; }

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

    public CpuTimeSnapshot GetSelfCpuTimeSnapshot()
    {
        if (HasFrozenCpuTimeSnapshot)
            return new CpuTimeSnapshot(FrozenUserCpuTimeNs, FrozenSystemCpuTimeNs);

        return GetLiveSelfCpuTimeSnapshot();
    }

    public CpuTimeSnapshot GetChildrenCpuTimeSnapshot()
    {
        return HasFrozenCpuTimeSnapshot
            ? new CpuTimeSnapshot(FrozenChildrenUserCpuTimeNs, FrozenChildrenSystemCpuTimeNs)
            : new CpuTimeSnapshot(ChildrenUserCpuTimeNs, ChildrenSystemCpuTimeNs);
    }

    public CpuTimeSnapshot GetReapedCpuTimeSnapshot()
    {
        return GetSelfCpuTimeSnapshot() + GetChildrenCpuTimeSnapshot();
    }

    internal void AccumulateExitedThreadCpuTime(CpuTimeSnapshot snapshot)
    {
        ExitedThreadsUserCpuTimeNs += snapshot.UserNs;
        ExitedThreadsSystemCpuTimeNs += snapshot.SystemNs;
    }

    internal void AccumulateChildrenCpuTime(CpuTimeSnapshot snapshot)
    {
        ChildrenUserCpuTimeNs += snapshot.UserNs;
        ChildrenSystemCpuTimeNs += snapshot.SystemNs;
    }

    internal void FreezeCpuTimeSnapshot()
    {
        if (HasFrozenCpuTimeSnapshot)
            return;

        var self = GetLiveSelfCpuTimeSnapshot();
        FrozenUserCpuTimeNs = self.UserNs;
        FrozenSystemCpuTimeNs = self.SystemNs;
        FrozenChildrenUserCpuTimeNs = ChildrenUserCpuTimeNs;
        FrozenChildrenSystemCpuTimeNs = ChildrenSystemCpuTimeNs;
        HasFrozenCpuTimeSnapshot = true;
    }

    private CpuTimeSnapshot GetLiveSelfCpuTimeSnapshot()
    {
        var snapshot = new CpuTimeSnapshot(ExitedThreadsUserCpuTimeNs, ExitedThreadsSystemCpuTimeNs);
        foreach (var task in Threads)
            snapshot += task.SnapshotThreadCpuTime();
        return snapshot;
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
        var argsRaw = args.Select(FsEncoding.EncodeUtf8).ToArray();
        LoadExecutable(dentry, FsEncoding.EncodeUtf8(guestPath), argsRaw, envs.Select(FsEncoding.EncodeUtf8).ToArray(),
            mount);
    }

    public void LoadExecutable(Dentry dentry, byte[] guestPathRaw, byte[][] argsRaw, byte[][] envsRaw, Mount mount)
    {
        var resolved = ResolveExecutableImage(dentry, guestPathRaw, argsRaw, mount);
        UpdateProcessImage(resolved.ExecutablePathRaw, resolved.ArgvRaw);

        var stackLimit = ResourceLimits[LinuxConstants.RLIMIT_STACK];
        var randBytes = Mem.MemoryContext.CreateExecRandomBytes(32, resolved.ExecutablePathRaw.AsSpan());
        var layout = GuestAddressSpaceLayout.CreateCompat32(stackLimit, randBytes);
        Syscalls.Mem.Layout = layout;
        Syscalls.SetupVDSO();

        var res = ElfLoader.Load(resolved.Dentry, resolved.ExecutablePathRaw, Syscalls, resolved.ArgvRaw, envsRaw,
            resolved.Mount, layout);
        Syscalls.BrkAddr = res.BrkAddr;

        var engine = Syscalls.CurrentSyscallEngine;
        engine.Eip = res.Entry;
        engine.RegWrite(Reg.ESP, res.SP);
        engine.Eflags = 0x202;

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

    private ResolvedImage ResolveExecutableImage(
        Dentry dentry,
        byte[] guestPathRaw,
        byte[][] argsRaw,
        Mount mount)
    {
        const int maxShebangDepth = 4;

        for (var depth = 0; depth < maxShebangDepth; depth++)
        {
            var displayPath = FsEncoding.DecodeUtf8Lossy(guestPathRaw);
            EnsureExecutableAccess(dentry, displayPath);

            var headerBuf = new byte[256];
            var headerLen = 0;
            if (dentry.Inode != null)
            {
                using var file = new LinuxFile(dentry, FileFlags.O_RDONLY, mount);
                headerLen = dentry.Inode.ReadToHost(null, file, headerBuf.AsSpan(), 0);
                if (headerLen < 0) headerLen = 0;
            }

            if (!ShebangParser.TryParse(headerBuf.AsSpan(0, headerLen), out var interpPathRaw, out var interpArgRaw))
                return new ResolvedImage(dentry, mount, guestPathRaw, displayPath, argsRaw);

            var (interpLoc, resolvedInterpPathRaw, _) = Syscalls.ResolvePathBytes(interpPathRaw);
            if (!interpLoc.IsValid)
                throw new FileNotFoundException(
                    $"Interpreter not found in VFS: {FsEncoding.DecodeUtf8Lossy(interpPathRaw)}");

            var newArgs = new List<byte[]> { resolvedInterpPathRaw };
            if (interpArgRaw != null)
                newArgs.Add(interpArgRaw);
            newArgs.Add(guestPathRaw);
            if (argsRaw.Length > 1)
                newArgs.AddRange(argsRaw.Skip(1));

            dentry = interpLoc.Dentry!;
            guestPathRaw = resolvedInterpPathRaw;
            argsRaw = [.. newArgs];
            mount = interpLoc.Mount!;
        }

        throw new InvalidDataException($"Shebang recursion too deep for '{FsEncoding.DecodeUtf8Lossy(guestPathRaw)}'.");
    }

    private void EnsureExecutableAccess(Dentry dentry, string guestPath)
    {
        if (dentry.Inode == null)
            throw new FileNotFoundException($"Executable not found in VFS: {guestPath}");
        if (dentry.Inode is HostInode hostInode)
            hostInode.RefreshProjectedMetadata(EUID, EGID);

        var accessRc = DacPolicy.CheckPathAccess(this, dentry.Inode, AccessMode.MayExec, true);
        if (accessRc < 0)
            throw new UnauthorizedAccessException($"Execute access denied for '{guestPath}'.");
    }

    internal void Exec(Dentry dentry, string guestPath, string[] args, string[] envs, Mount mount)
    {
        Exec(dentry, FsEncoding.EncodeUtf8(guestPath), args.Select(FsEncoding.EncodeUtf8).ToArray(),
            envs.Select(FsEncoding.EncodeUtf8).ToArray(), mount);
    }

    internal void Exec(Dentry dentry, byte[] guestPathRaw, byte[][] argsRaw, byte[][] envsRaw, Mount mount)
    {
        var oldMem = Mem;
        var oldEngine = Syscalls.CurrentSyscallEngine;
        var hadSharedAddressSpace = oldMem.GetSharedRefCount() > 1;
        var shouldDetachSysVShm = oldMem.GetSharedRefCount() == 1;

        if (shouldDetachSysVShm)
            Syscalls.SysVShm.OnProcessExit(TGID, oldMem, oldEngine, this);

        var freshMem = new VMAManager(oldMem.MemoryContext);
        if (hadSharedAddressSpace)
            freshMem.BindAddressSpaceHandle(ProcessAddressSpaceHandle.DetachFromSharedEngine(oldEngine));
        else
            freshMem.BindAddressSpaceHandle(ProcessAddressSpaceHandle.CaptureAttachedEngine(oldEngine));
        Mem = freshMem;
        Syscalls.Mem = freshMem;
        ProcessAddressSpaceSync.RebindEngineAddressSpace(oldMem, freshMem, oldEngine);
        MemoryReleased = false;
        oldMem.ReleaseSharedRef(oldEngine);

        Syscalls.CurrentSyscallEngine.ResetMemory();

        ResetSignalDispositionsForExec();

        LoadExecutable(dentry, guestPathRaw, argsRaw, envsRaw, mount);
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
        ExecutablePathRaw = [.. src.ExecutablePathRaw];
        CommandLineArgumentBytes = src.CommandLineArgumentBytes.Select(a => a.ToArray()).ToArray();
        CommandLineRaw = [.. src.CommandLineRaw];
        Comm = src.Comm;
    }

    public bool TryGetResourceLimit(int resource, out ResourceLimit limit)
    {
        if ((uint)resource >= (uint)ResourceLimits.Length)
        {
            limit = default;
            return false;
        }

        limit = ResourceLimits[resource];
        return true;
    }

    public bool TrySetResourceLimit(int resource, ResourceLimit limit)
    {
        if ((uint)resource >= (uint)ResourceLimits.Length || limit.Soft > limit.Hard)
            return false;

        ResourceLimits[resource] = limit;
        return true;
    }

    public void CopyResourceLimitsFrom(Process src)
    {
        ArgumentNullException.ThrowIfNull(src);
        Array.Copy(src.ResourceLimits, ResourceLimits, ResourceLimits.Length);
    }

    private void UpdateProcessImage(byte[] exeRaw, byte[][] argsRaw)
    {
        ExecutablePathRaw = [.. exeRaw];
        CommandLineArgumentBytes = argsRaw.Select(a => a.ToArray()).ToArray();

        var commBase = CommandLineArgumentBytes.Length > 0 ? CommandLineArgumentBytes[0] : ExecutablePathRaw;
        Comm = GetByteBasename(commBase);
        if (string.IsNullOrEmpty(Comm)) Comm = "process";

        if (CommandLineArgumentBytes.Length == 0)
        {
            CommandLineRaw = EmptyCmdline;
            return;
        }

        var totalLen = CommandLineArgumentBytes.Sum(a => a.Length) + CommandLineArgumentBytes.Length;
        var raw = new byte[totalLen];
        var offset = 0;
        for (var i = 0; i < CommandLineArgumentBytes.Length; i++)
        {
            CommandLineArgumentBytes[i].CopyTo(raw, offset);
            offset += CommandLineArgumentBytes[i].Length + 1;
        }

        CommandLineRaw = raw;
    }

    private static string GetByteBasename(byte[] path)
    {
        if (path.Length == 0) return "";
        var lastSlash = path.AsSpan().LastIndexOf((byte)'/');
        var name = lastSlash >= 0 ? path.AsSpan(lastSlash + 1) : path.AsSpan();
        return FsEncoding.DecodeUtf8Lossy(name);
    }

    private static ResourceLimit[] CreateDefaultResourceLimits()
    {
        var limits = new ResourceLimit[LinuxConstants.RLIMIT_NLIMITS];

        var addressSpaceLimit = (ulong)LinuxConstants.TaskSize32;
        limits[LinuxConstants.RLIMIT_CPU] = Unlimited();
        limits[LinuxConstants.RLIMIT_FSIZE] = Unlimited();
        limits[LinuxConstants.RLIMIT_DATA] = new ResourceLimit(addressSpaceLimit, addressSpaceLimit);
        limits[LinuxConstants.RLIMIT_STACK] =
            new ResourceLimit(8UL * 1024 * 1024, LinuxConstants.RLIM64_INFINITY);
        limits[LinuxConstants.RLIMIT_CORE] = new ResourceLimit(0, 0);
        limits[LinuxConstants.RLIMIT_RSS] = new ResourceLimit(addressSpaceLimit, addressSpaceLimit);
        limits[LinuxConstants.RLIMIT_NPROC] = new ResourceLimit(1024, 1024);
        limits[LinuxConstants.RLIMIT_NOFILE] = new ResourceLimit(1024, 4096);
        limits[LinuxConstants.RLIMIT_MEMLOCK] = new ResourceLimit(64UL * 1024, 64UL * 1024);
        limits[LinuxConstants.RLIMIT_AS] = new ResourceLimit(addressSpaceLimit, addressSpaceLimit);
        limits[LinuxConstants.RLIMIT_LOCKS] = Unlimited();
        limits[LinuxConstants.RLIMIT_SIGPENDING] = new ResourceLimit(1024, 1024);
        limits[LinuxConstants.RLIMIT_MSGQUEUE] = new ResourceLimit(819_200, 819_200);
        limits[LinuxConstants.RLIMIT_NICE] = new ResourceLimit(0, 0);
        limits[LinuxConstants.RLIMIT_RTPRIO] = new ResourceLimit(0, 0);
        limits[LinuxConstants.RLIMIT_RTTIME] = Unlimited();
        return limits;

        static ResourceLimit Unlimited()
        {
            return new ResourceLimit(LinuxConstants.RLIM64_INFINITY, LinuxConstants.RLIM64_INFINITY);
        }
    }

    private record struct ResolvedImage(
        Dentry Dentry,
        Mount Mount,
        byte[] ExecutablePathRaw,
        string ExecutableDisplayPath,
        byte[][] ArgvRaw);
}
