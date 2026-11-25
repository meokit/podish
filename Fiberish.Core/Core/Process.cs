using Fiberish.Auth.Cred;
using Fiberish.Auth.Permission;
using ExecIdentity = Fiberish.Auth.Cred.CredentialService.ExecIdentity;
using Fiberish.Core.Utils;
using Fiberish.Core.VFS.TTY;
using Fiberish.Loader;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;

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

public sealed class TextFileBusyException(string message) : IOException(message);

public readonly record struct CpuTimeSnapshot(long UserNs, long SystemNs)
{
    public static CpuTimeSnapshot operator +(CpuTimeSnapshot left, CpuTimeSnapshot right)
    {
        return new CpuTimeSnapshot(left.UserNs + right.UserNs, left.SystemNs + right.SystemNs);
    }
}

/// <summary>State of a CPU-time interval timer (ITIMER_VIRTUAL / ITIMER_PROF).</summary>
public sealed class CpuItimerState
{
    public long ValueMs;
    public long IntervalMs;
    public long NextFireCpuMs;
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
    public const int CapabilityKill = 5;
    public const int CapabilitySetPcap = 8;
    public const int CapabilityNetBindService = 10;
    public const int CapabilityNetAdmin = 12;
    public const int CapabilitySysAdmin = 21;
    public const int CapabilitySysNice = 23;
    public const int CapabilitySysResource = 24;
    public const int CapabilityLast = 40; // CAP_CHECKPOINT_RESTORE
    private static readonly byte[] EmptyCmdline = [];
    private static readonly ResourceLimit[] DefaultResourceLimits = CreateDefaultResourceLimits();

    // Null in production: deterministic commit-failure coverage without fault
    // objects, checkpoints collections, or delegates allocated during exec.
    internal Action<ExecCommitCheckpoint>? ExecCommitFaultForTesting { get; set; }

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
        SetCapability(CapabilitySysResource, true, true, false);
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
    public Dictionary<(int Semid, int SemNum), int> SemUndo { get; } = new();
    public uint[] CapEffective { get; } = [0U, 0U];
    public uint[] CapPermitted { get; } = [0U, 0U];
    public uint[] CapInheritable { get; } = [0U, 0U];
    public uint[] CapBounding { get; } = [uint.MaxValue, (1U << (CapabilityLast - 31)) - 1];

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
    public int ParentDeathSignal { get; set; }
    public bool IsChildSubreaper { get; set; }
    public int Dumpable { get; set; } = 1; // SUID_DUMP_USER
    public bool NoNewPrivs { get; set; }
    public int TscMode { get; set; } = LinuxConstants.PR_TSC_ENABLE;
    public bool ThpDisabled { get; set; }
    public long TimerSlackNs { get; set; } = 50_000;
    public long DefaultTimerSlackNs { get; set; } = 50_000;
    public long LastCpuSoftWarningSec { get; set; } = -1;
    public bool HasExeced { get; set; }
    public CpuItimerState?[] CpuItimers { get; } = new CpuItimerState?[2];

    /// <summary>Cheap guard so the scheduler skips CPU accounting on the common path.</summary>
    public bool HasArmedCpuItimer => CpuItimers[0] != null || CpuItimers[1] != null;

    public bool HasFiniteCpuLimit
    {
        get
        {
            if (!TryGetResourceLimit(LinuxConstants.RLIMIT_CPU, out var limit))
                return false;
            return limit.Soft != LinuxConstants.RLIM64_INFINITY ||
                   limit.Hard != LinuxConstants.RLIM64_INFINITY;
        }
    }

    public bool NeedsCpuTimeAccounting => HasFiniteCpuLimit || HasArmedCpuItimer;

    // The scheduler refreshes at most once per real millisecond and shares the result
    // between the rlimit check and the CPU itimers; otherwise every slice walked every
    // thread twice for a process with many threads.
    internal long CpuAccountingSnapshotMs = -1;
    internal CpuTimeSnapshot CpuAccountingSnapshot;
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
    private long _accumulatedLiveUserCpuNs;
    private long _accumulatedLiveSystemCpuNs;

    /// <summary>The thread currently executing guest code for this process (scheduler-owned).</summary>
    internal FiberTask? RunningTask { get; set; }
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

        // kill(2) must still set the pending bit when allocation is limited.
        // Such notifications coalesce and carry no allocated siginfo record.
        if (info.PendingWithoutInfo) return true;
        info.QueueOwnerUid = UID;

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

            var mask = 1UL << (sig - 1);
            if ((PendingProcessSignals & mask) != 0)
            {
                PendingProcessSignals &= ~mask;
                return new SigInfo { Signo = sig, Code = 0 };
            }

            return null;
        });
    }

    public void ClearPendingProcessSignals()
    {
        PendingProcessSignals = 0;
        PendingProcessSignalQueue.Lock(q => q.Clear());
    }

    public int GetPendingSignalCount()
    {
        var count = PendingProcessSignalQueue.Lock(q => q.Count);
        foreach (var task in Threads)
            count += task.PendingSignalQueue.Lock(q => q.Count);
        return count;
    }

    internal long GetPendingSignalCount(int uid)
    {
        long count = PendingProcessSignalQueue.Lock(q => q.Count(info => info.QueueOwnerUid == uid));
        foreach (var task in Threads)
            count += task.PendingSignalQueue.Lock(q => q.Count(info => info.QueueOwnerUid == uid));
        return count;
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

    internal void AccumulateLiveCpuTime(long userNs, long systemNs)
    {
        _accumulatedLiveUserCpuNs += userNs;
        _accumulatedLiveSystemCpuNs += systemNs;
    }

    internal void RemoveAccumulatedLiveCpuTime(CpuTimeSnapshot snapshot)
    {
        _accumulatedLiveUserCpuNs -= snapshot.UserNs;
        _accumulatedLiveSystemCpuNs -= snapshot.SystemNs;
        if (_accumulatedLiveUserCpuNs < 0) _accumulatedLiveUserCpuNs = 0;
        if (_accumulatedLiveSystemCpuNs < 0) _accumulatedLiveSystemCpuNs = 0;
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
        // O(1): completed slices of every live thread are accumulated, and only the
        // currently running thread has an uncommitted live delta.
        var snapshot = new CpuTimeSnapshot(
            ExitedThreadsUserCpuTimeNs + _accumulatedLiveUserCpuNs,
            ExitedThreadsSystemCpuTimeNs + _accumulatedLiveSystemCpuNs);
        var running = RunningTask;
        if (running != null)
            snapshot += running.SnapshotLiveCpuDelta();
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

    public bool HasBoundingCapability(int capability)
    {
        return capability >= 0 && capability <= CapabilityLast &&
               (CapBounding[capability / 32] & (1U << (capability % 32))) != 0;
    }

    internal void DropBoundingCapability(int capability)
    {
        SetCapabilityBit(CapBounding, capability, false);
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
        var guestPathRaw = FsEncoding.EncodeUtf8(guestPath);
        var argsRaw = args.Select(FsEncoding.EncodeUtf8).ToArray();
        LoadExecutable(dentry, guestPathRaw, argsRaw, envs.Select(FsEncoding.EncodeUtf8).ToArray(),
            mount, guestPathRaw, takeArgvOwnership: true);
    }

    public void LoadExecutable(Dentry dentry, byte[] guestPathRaw, byte[][] argsRaw, byte[][] envsRaw, Mount mount,
        byte[]? execFnPathRaw = null, ExecIdentity? execIdentity = null, bool takeArgvOwnership = false)
    {
        LoadExecutableImage(dentry, guestPathRaw, argsRaw, envsRaw, mount,
            execFnPathRaw, execIdentity, takeArgvOwnership);
    }

    private ExecIdentity LoadExecutableImage(Dentry dentry, byte[] guestPathRaw, byte[][] argsRaw, byte[][] envsRaw,
        Mount mount, byte[]? execFnPathRaw = null, ExecIdentity? execIdentity = null, bool takeArgvOwnership = false,
        ElfLoader.PreparedImage? preparedMain = null, ElfLoader.PreparedImage? preparedInterpreter = null)
    {
        var resolved = preparedMain != null ? new ResolvedImage(dentry, mount, guestPathRaw, argsRaw) :
            ResolveExecutableImage(dentry, guestPathRaw, argsRaw, mount);
        var identity = execIdentity ?? CredentialService.ComputeExecIdentity(this,
            preparedMain?.File.OpenedInode ?? resolved.Dentry.Inode!,
            (resolved.Mount.Flags & LinuxConstants.MS_NOSUID) == 0);
        UpdateProcessImage(resolved.ExecutablePathRaw, execFnPathRaw ?? guestPathRaw, resolved.ArgvRaw,
            takeArgvOwnership);

        var stackLimit = ResourceLimits[LinuxConstants.RLIMIT_STACK];
        var randBytes = Mem.MemoryContext.CreateExecRandomBytes(32, resolved.ExecutablePathRaw.AsSpan());
        var layout = GuestAddressSpaceLayout.CreateCompat32(stackLimit, randBytes);
        Syscalls.Mem.Layout = layout;

        var res = ElfLoader.Load(resolved.Dentry, resolved.ExecutablePathRaw,
            execFnPathRaw ?? guestPathRaw, Syscalls, resolved.ArgvRaw, envsRaw,
            resolved.Mount, layout, identity, this, preparedMain, preparedInterpreter);
        Syscalls.BrkAddr = res.BrkAddr;

        var engine = Syscalls.CurrentSyscallEngine;
        engine.ResetForExec(engine.Architecture, res.Entry, res.SP);

        var spBase = res.SP;
        var stackData = res.InitialStack;
        var stackStart = spBase & LinuxConstants.PageMask;
        var stackLength = ((spBase + (uint)stackData.Count + LinuxConstants.PageSize - 1) &
                           LinuxConstants.PageMask) - stackStart;
        if (!Syscalls.Mem.PrefaultRange(stackStart, stackLength, engine, true))
            throw new OutOfMemoryException($"Failed to allocate initial stack pages at 0x{stackStart:x}");

        if (!engine.CopyToUser(spBase, stackData.AsSpan()))
            throw new InvalidOperationException("Failed to write initial stack content to guest memory");
        return identity;
    }

    private ResolvedImage ResolveExecutableImage(
        Dentry dentry,
        byte[] guestPathRaw,
        byte[][] argsRaw,
        Mount mount)
    {
        const int maxShebangDepth = 4;

        Span<byte> headerBuf = stackalloc byte[256];
        for (var depth = 0; depth < maxShebangDepth; depth++)
        {
            EnsureExecutableAccess(dentry, guestPathRaw, mount);

            var headerLen = 0;
            if (dentry.Inode != null)
            {
                using var file = new LinuxFile(dentry, FileFlags.O_RDONLY, mount);
                headerLen = dentry.Inode.ReadToHost(null, file, headerBuf, 0);
                if (headerLen < 0) headerLen = 0;
            }

            if (!ShebangParser.TryParse(headerBuf[..headerLen], out var interpPathRaw, out var interpArgRaw))
                return new ResolvedImage(dentry, mount, guestPathRaw, argsRaw);

            var (interpLoc, resolvedInterpPathRaw, _) = Syscalls.ResolvePathBytes(interpPathRaw);
            if (!interpLoc.IsValid)
                throw new FileNotFoundException(
                    $"Interpreter not found in VFS: {FsEncoding.DecodeUtf8Lossy(interpPathRaw)}");

            dentry = interpLoc.Dentry!;
            argsRaw = RewriteShebangArguments(resolvedInterpPathRaw, interpArgRaw, guestPathRaw, argsRaw);
            guestPathRaw = resolvedInterpPathRaw;
            mount = interpLoc.Mount!;
        }

        throw new InvalidDataException($"Shebang recursion too deep for '{FsEncoding.DecodeUtf8Lossy(guestPathRaw)}'.");
    }

    internal static byte[][] RewriteShebangArguments(byte[] interpreter, byte[]? option, byte[] script,
        byte[][] args)
    {
        var prefix = option == null ? 2 : 3;
        var tail = Math.Max(0, args.Length - 1);
        var result = new byte[prefix + tail][];
        result[0] = interpreter;
        if (option != null) result[1] = option;
        result[prefix - 1] = script;
        if (tail != 0) args.AsSpan(1).CopyTo(result.AsSpan(prefix));
        return result;
    }

    internal int CheckExecutableAccess(Dentry dentry, Mount mount) => CheckExecutableAccess(dentry.Inode, mount);

    internal int CheckExecutableAccess(Inode? inode, Mount mount)
    {
        if (inode == null) return -(int)Errno.ENOENT;
        if (inode is HostInode hostInode)
            hostInode.RefreshProjectedMetadata(EUID, EGID);
        if (inode.Type != InodeType.File) return -(int)Errno.EACCES;
        if ((mount.Flags & LinuxConstants.MS_NOEXEC) != 0)
            return -(int)Errno.EACCES;
        if (inode is TmpfsInode { IsMemfdNoExecSealed: true }) return -(int)Errno.EACCES;

        var rc = DacPolicy.CheckPathAccess(this, inode, AccessMode.MayExec, true);
        if (rc < 0) return rc;
        // Anonymous memfd remains executable while its creating O_RDWR fd is open.
        return inode is not TmpfsInode { IsMemfd: true } && inode.HasWritableOpenFile
            ? -(int)Errno.ETXTBSY : 0;
    }

    private void EnsureExecutableAccess(Dentry dentry, byte[] guestPath, Mount mount)
    {
        var rc = CheckExecutableAccess(dentry, mount);
        if (rc < 0)
            throw new ExecFileException((Errno)(-rc), $"Cannot execute '{FsEncoding.DecodeUtf8Lossy(guestPath)}'.");
    }

    internal ExecIdentity Exec(Dentry dentry, string guestPath, string[] args, string[] envs, Mount mount)
    {
        var guestPathRaw = FsEncoding.EncodeUtf8(guestPath);
        return Exec(dentry, guestPathRaw, args.Select(FsEncoding.EncodeUtf8).ToArray(),
            envs.Select(FsEncoding.EncodeUtf8).ToArray(), mount, guestPathRaw);
    }

    internal ExecIdentity Exec(Dentry dentry, byte[] guestPathRaw, byte[][] argsRaw, byte[][] envsRaw, Mount mount,
        byte[]? execFnPathRaw = null, Engine? callerEngine = null,
        ElfLoader.PreparedImage? preparedMain = null, ElfLoader.PreparedImage? preparedInterpreter = null)
    {
        var oldMem = Mem;
        var previousSyscallEngine = Syscalls.CurrentSyscallEngine;
        var oldEngine = callerEngine ?? previousSyscallEngine;
        var oldBrkAddr = Syscalls.BrkAddr;
        var oldLayout = oldMem.Layout;
        var oldSigReturnAddr = Syscalls.SigReturnAddr;
        var oldRtSigReturnAddr = Syscalls.RtSigReturnAddr;
        var oldExecutablePathRaw = ExecutablePathRaw;
        var oldCommandLineArgumentBytes = CommandLineArgumentBytes;
        var oldCommandLineRaw = CommandLineRaw;
        var oldComm = Comm;

        // Stage privately. All recoverable failures (including FD unshare) restore
        // image metadata as well as syscall state. Past thread teardown, failure is
        // fatal: it must never return errno into the partially changed old image.
        var stagedMem = new VMAManager(oldMem.MemoryContext);
        var stagedEngine = new Engine(oldMem.MemoryContext)
        {
            Owner = oldEngine.Owner,
            CurrentSyscallManager = Syscalls,
            LogHandler = oldEngine.LogHandler
        };
        var commitStarted = false;
        var installed = false;
        ExecIdentity identity = default;
        try
        {
            stagedMem.BindAddressSpaceHandle(ProcessAddressSpaceHandle.CaptureAttachedEngine(stagedEngine));
            Syscalls.Mem = stagedMem;
            Syscalls.CurrentSyscallEngine = stagedEngine;
            Syscalls.RestoreVdsoAddresses(0, 0);
            identity = LoadExecutableImage(dentry, guestPathRaw, argsRaw, envsRaw, mount,
                execFnPathRaw ?? guestPathRaw, takeArgvOwnership: true,
                preparedMain: preparedMain, preparedInterpreter: preparedInterpreter);

            Syscalls.Mem = oldMem;
            Syscalls.CurrentSyscallEngine = oldEngine;
            var currentTask = oldEngine.Owner as FiberTask;
            ExecCommitFaultForTesting?.Invoke(ExecCommitCheckpoint.BeforeFdUnshare);
            if (currentTask != null)
                Syscalls.UnshareFdTable(currentTask);

            commitStarted = true;
            ExecCommitFaultForTesting?.Invoke(ExecCommitCheckpoint.BeforeThreadTeardown);
            if (currentTask != null)
            {
                currentTask.CommonKernel.DetachSiblingThreads(currentTask);
                currentTask.ExitRobustList();
                if (oldMem.GetSharedRefCount() > 1)
                    currentTask.ClearChildTidAndWake();
                else
                    currentTask.ChildClearTidPtr = 0;
            }

            if (oldMem.GetSharedRefCount() == 1)
                Syscalls.SysVShm.OnProcessExit(TGID, oldMem, oldEngine, this);

            ExecCommitFaultForTesting?.Invoke(ExecCommitCheckpoint.BeforeAddressSpaceSwitch);
            ProcessAddressSpaceSync.RebindEngineAddressSpace(oldMem, stagedMem, oldEngine);
            oldMem.ReleaseSharedRef(oldEngine);
            stagedMem.AddressSpaceHandle!.AttachEngine(oldEngine, forExec: true);

            Mem = stagedMem;
            Syscalls.Mem = stagedMem;
            MemoryReleased = false;
            installed = true;
            ExecCommitFaultForTesting?.Invoke(ExecCommitCheckpoint.AfterAddressSpaceSwitch);

            oldEngine.ResetForExec(stagedEngine.Architecture, stagedEngine.InstructionPointer, stagedEngine.StackPointer);
        }
        catch (Exception ex)
        {
            if (!installed)
            {
                Syscalls.BrkAddr = oldBrkAddr;
                oldMem.Layout = oldLayout;
                Syscalls.RestoreVdsoAddresses(oldSigReturnAddr, oldRtSigReturnAddr);
                ExecutablePathRaw = oldExecutablePathRaw;
                CommandLineArgumentBytes = oldCommandLineArgumentBytes;
                CommandLineRaw = oldCommandLineRaw;
                Comm = oldComm;
            }
            commitStarted |= ex is FatalExecException;
            if (commitStarted && ex is not FatalExecException)
                throw new FatalExecException("Exec failed past the commit boundary.", ex);
            throw;
        }
        finally
        {
            Syscalls.CurrentSyscallEngine = previousSyscallEngine;
            Syscalls.Mem = installed ? stagedMem : oldMem;
            try
            {
                try
                {
                    ProcessAddressSpaceSync.UnregisterEngineAddressSpace(stagedMem, stagedEngine);
                }
                finally
                {
                    try
                    {
                        if (!installed)
                            stagedMem.ReleaseSharedRef(stagedEngine);
                    }
                    finally
                    {
                        stagedEngine.Dispose();
                    }
                }
            }
            catch (Exception ex) when (commitStarted)
            {
                throw new FatalExecException("Exec staging cleanup failed past the commit boundary.", ex);
            }
        }
        return identity;
    }

    internal void ResetSignalDispositionsForExec()
    {
        // Linux clears the parent-death signal in execve; it must be re-armed explicitly.
        ParentDeathSignal = 0;
        if (SignalActions.Count == 0) return;

        foreach (var (sig, action) in SignalActions)
            // Keep SIG_IGN; reset user handlers to default.
            // Dictionary.Remove does not invalidate enumeration on modern .NET.
            if (action.Handler > 1)
                SignalActions.Remove(sig);
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

    // When takeArgvOwnership is true, argsRaw is retained directly; callers must not mutate it.
    private void UpdateProcessImage(byte[] exeRaw, byte[] commPathRaw, byte[][] argsRaw, bool takeArgvOwnership)
    {
        ExecutablePathRaw = [.. exeRaw];
        CommandLineArgumentBytes = takeArgvOwnership
            ? argsRaw
            : argsRaw.Select(a => a.ToArray()).ToArray();

        // Linux sets comm from the executable filename (bprm->filename), not argv[0],
        // and truncates it to TASK_COMM_LEN-1 (15) bytes. For a script that is the
        // script path passed to execve, not the interpreter resolved for the image.
        var commBytes = commPathRaw.AsSpan();
        var lastSlash = commBytes.LastIndexOf((byte)'/');
        if (lastSlash >= 0) commBytes = commBytes[(lastSlash + 1)..];
        if (commBytes.Length > 15) commBytes = commBytes[..15];
        Comm = FsEncoding.DecodeUtf8Lossy(commBytes);
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
        byte[][] ArgvRaw);
}
