using Fiberish.Core;
using Fiberish.Core.VFS.TTY;
using Fiberish.Diagnostics;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.VFS;
using Fiberish.X86.Native;
using Microsoft.Extensions.Logging;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
    public delegate ValueTask<int> SyscallHandler(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6);

    private const int MaxSyscalls = 512;
    private static readonly ILogger Logger = Logging.CreateLogger<SyscallManager>();
    private static readonly Dictionary<IntPtr, SyscallManager> _registry = [];
    private static readonly object _registryLock = new();
    private readonly SyscallHandler?[] _syscallHandlers = new SyscallHandler?[MaxSyscalls];

    public SyscallManager(Engine engine, VMAManager mem, uint brk, string hostRoot, bool useOverlay,
        TtyDiscipline? tty = null)
    {
        Engine = engine;
        Mem = mem;
        BrkAddr = brk;
        BrkBase = brk;
        Tty = tty;
        Futex = new FutexManager();
        SysVShm = new SysVShmManager();
        SysVSem = new SysVSemManager();

        RegisterEngine(engine);
        RegisterHandlers();

        // 1. Initialize Registry and Register Filesystems
        FileSystemRegistry.TryRegister(new FileSystemType { Name = "hostfs", FileSystem = new Hostfs() });
        FileSystemRegistry.TryRegister(new FileSystemType { Name = "tmpfs", FileSystem = new Tmpfs() });
        FileSystemRegistry.TryRegister(new FileSystemType { Name = "devtmpfs", FileSystem = new Tmpfs() });
        FileSystemRegistry.TryRegister(new FileSystemType { Name = "overlay", FileSystem = new OverlayFileSystem() });
        FileSystemRegistry.TryRegister(new FileSystemType { Name = "proc", FileSystem = new Tmpfs() });

        // Initialize PTY manager and devpts filesystem
        PtyManager = new PtyManager(Logger);
        var signalBroadcaster = new SignalBroadcasterImpl(this);
        FileSystemRegistry.TryRegister(new FileSystemType
            { Name = "devpts", FileSystem = new DevPtsFileSystem(PtyManager, signalBroadcaster, Logger) });

        // 2. Setup Rootfs (OverlayFS: Lower=Hostfs, Upper=Tmpfs)
        var hostFsType = FileSystemRegistry.Get("hostfs")!;
        var tmpFsType = FileSystemRegistry.Get("tmpfs")!;
        var overlayFsType = FileSystemRegistry.Get("overlay")!;

        // Lower: Hostfs (Read-Only access to hostRoot)
        var lowerSb = hostFsType.FileSystem.ReadSuper(hostFsType, 0, hostRoot, null);

        if (useOverlay)
        {
            // Upper: Tmpfs (Read-Write layer)
            var upperSb = tmpFsType.FileSystem.ReadSuper(tmpFsType, 0, "overlay_upper", null);

            // Overlay: Combine them
            var options = new OverlayMountOptions { Lower = lowerSb, Upper = upperSb };
            var overlaySb = overlayFsType.FileSystem.ReadSuper(overlayFsType, 0, "root_overlay", options);

            Root = overlaySb.Root;
            MountList.Add(new MountInfo
            {
                Source = "overlay", Target = "/", FsType = "overlay",
                Options = "rw,relatime,lowerdir=/,upperdir=/overlay_upper,workdir=/work"
            });
        }
        else
        {
            Root = lowerSb.Root;
            MountList.Add(new MountInfo
            {
                Source = hostRoot, Target = "/", FsType = "hostfs",
                Options = "rw,relatime"
            });
        }

        ProcessRoot = Root;
        CurrentWorkingDirectory = Root;

        Root.Inode!.Get();
        ProcessRoot.Inode!.Get();
        CurrentWorkingDirectory.Inode!.Get();

        // 3. Mount /dev and /proc
        if (useOverlay)
        {
            // Ensure /dev and /proc exist in the overlay (will be created in Upper if missing)
            EnsureDirectory(Root, "dev");
            EnsureDirectory(Root, "proc");
            EnsureDirectory(Root, "tmp");
        }

        // 3. Setup devtmpfs and stdio FDs (always needed)
        var devFsType = FileSystemRegistry.Get("devtmpfs")!;
        var devSb = devFsType.FileSystem.ReadSuper(devFsType, 0, "dev", null);

        // Mount devtmpfs to /dev if it exists
        var devDentry = Root.Inode.Lookup("dev");
        if (devDentry != null && devDentry.Inode?.Type == InodeType.Directory)
        {
            Mount(Root, "dev", devSb, "devtmpfs", "devtmpfs", "rw,relatime", "/dev");
        }
        else
        {
            Logger.LogWarning("/dev not found in rootfs, skipping devtmpfs mount.");
        }

        // Add console FDs (uses devSb which might or might not be mounted)
        InitStdio(devSb, tty);

        // 4. Mount procfs to /proc if it exists
        var procDentry = Root.Inode.Lookup("proc");
        if (procDentry != null && procDentry.Inode?.Type == InodeType.Directory)
        {
            var procFsType = FileSystemRegistry.Get("proc")!;
            var procSb = procFsType.FileSystem.ReadSuper(procFsType, 0, "proc", null);
            Mount(Root, "proc", procSb, "proc", "proc", "rw,relatime", "/proc");
        }
        else
        {
            Logger.LogWarning("/proc not found in rootfs, skipping procfs mount.");
        }

        // 5. Mount tmpfs to /dev/shm for POSIX shm_open userspace ABI.
        // Resolve through the mounted /dev dentry to avoid creating shm under a detached devtmpfs root.
        var devRoot = PathWalk("/dev") ?? devSb.Root;
        if (useOverlay)
        {
            EnsureDirectory(devRoot, "shm");
        }

        var shmDentry = devRoot.Inode!.Lookup("shm");
        if (shmDentry != null && shmDentry.Inode?.Type == InodeType.Directory)
        {
            var shmSb = tmpFsType.FileSystem.ReadSuper(tmpFsType, 0, "shm", null);
            Mount(devRoot, "shm", shmSb, "tmpfs", "tmpfs", "rw,nosuid,nodev", "/dev/shm");
            DevShmRoot = shmSb.Root;
        }

        // Separate tmpfs namespace for memfd_create (unnamed file descriptors).
        MemfdSuperBlock = tmpFsType.FileSystem.ReadSuper(tmpFsType, 0, "memfd", null);

        SetupVDSO();
    }

    private SyscallManager(VMAManager mem, Dictionary<int, VFS.LinuxFile> fds, FutexManager futex,
        SysVShmManager sysvShm, SysVSemManager sysvSem, uint brk,
        uint brkBase,
        bool strace,
        Dentry root, Dentry cwd, Dentry procRoot, Dentry devShmRoot, SuperBlock memfdSuperBlock, TtyDiscipline? tty)
    {
        Mem = mem;
        FDs = fds;
        Futex = futex;
        SysVShm = sysvShm;
        SysVSem = sysvSem;
        BrkAddr = brk;
        BrkBase = brkBase;
        Strace = strace;
        Root = root; // Global root (shared)
        CurrentWorkingDirectory = cwd;
        ProcessRoot = procRoot;
        DevShmRoot = devShmRoot;
        MemfdSuperBlock = memfdSuperBlock;
        Tty = tty;

        Root.Inode!.Get();
        CurrentWorkingDirectory.Inode!.Get();
        ProcessRoot.Inode!.Get();

        RegisterHandlers();
    }

    public TtyDiscipline? Tty { get; }

    // PTY Manager for /dev/ptmx and /dev/pts/N
    public PtyManager PtyManager { get; } = null!;

    // The current engine executing a syscall (protected by GIL)
    public Engine Engine { get; set; } = null!;

    public VMAManager Mem { get; set; }

    public PosixTimerManager PosixTimers { get; } = new();

    public Dentry Root { get; set; } = null!;
    public Dentry CurrentWorkingDirectory { get; set; } = null!; // Renamed to avoid confusion with string Cwd

    // For chroot tracking, we keep a Dentry pointer to the process root
    public Dentry ProcessRoot { get; set; } = null!;
    public Dentry DevShmRoot { get; set; } = null!;
    public SuperBlock MemfdSuperBlock { get; set; } = null!;

    // System V Shared Memory (Global IPC namespace)
    public SysVShmManager SysVShm { get; }
    public SysVSemManager SysVSem { get; }

    // File Descriptors (Shared if CLONE_FILES)
    public Dictionary<int, VFS.LinuxFile> FDs { get; } = [];

    public FutexManager Futex { get; }

    public uint BrkAddr { get; set; }
    public uint BrkBase { get; }
    public bool Strace { get; set; }
    public List<MountInfo> MountList { get; } = [];

    public uint SigReturnAddr { get; private set; }
    public uint RtSigReturnAddr { get; private set; }

    internal void SetupVDSO()
    {
        // Map vDSO page (RX) at a fixed high address to avoid overlap
        uint vdsoAddr = 0x7FFF0000;
        Mem.Mmap(vdsoAddr, 4096, Protection.Read | Protection.Exec,
            MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, 0, "[vdso]", Engine);

        // Directly allocate the page in the engine with RW permissions for initial setup
        if (!Mem.MapAnonymousPage(vdsoAddr, Engine, Protection.Read | Protection.Write))
            throw new Exception("Failed to allocate vDSO page");

        // Write trampolines
        // __kernel_sigreturn: pop eax; mov eax, 119; int 0x80
        byte[] sigret = [0x58, 0xB8, 0x77, 0x00, 0x00, 0x00, 0xCD, 0x80];
        if (!Engine.CopyToUser(vdsoAddr, sigret)) Logger.LogError("Failed to write sigreturn trampoline to vDSO");
        SigReturnAddr = vdsoAddr;

        // __kernel_rt_sigreturn: mov eax, 173; int 0x80
        byte[] rtsigret = [0xB8, 0xAD, 0x00, 0x00, 0x00, 0xCD, 0x80];
        if (!Engine.CopyToUser(vdsoAddr + 16, rtsigret))
            Logger.LogError("Failed to write rt_sigreturn trampoline to vDSO");
        RtSigReturnAddr = vdsoAddr + 16;

        // Set final RX permissions in the engine
        Engine.MemMap(vdsoAddr, 4096, (byte)(Protection.Read | Protection.Exec));

        Logger.LogInformation("vDSO mapped at 0x{Addr:x}, sigreturn=0x{S:x}, rt_sigreturn=0x{R:x}", vdsoAddr,
            SigReturnAddr, RtSigReturnAddr);
    }

    private static void EnsureDirectory(Dentry parent, string name)
    {
        var dentry = parent.Inode!.Lookup(name);
        if (dentry == null)
        {
            dentry = new Dentry(name, null, parent, parent.SuperBlock);
            parent.Inode.Mkdir(dentry, 0x1FF, 0, 0); // 777
        }
        else if (dentry.Inode?.Type != InodeType.Directory)
        {
            throw new Exception($"Path /{name} exists but is not a directory");
        }
    }

    public void MountHostfs(string hostPath, string guestPath, bool readOnly = false)
    {
        var parts = guestPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new ArgumentException("Cannot mount at root");

        var current = Root;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            EnsureDirectory(current, parts[i]);
            current = current.Inode!.Lookup(parts[i])!;
        }

        var name = parts[^1];
        // Create the mount point (file or directory) based on hostPath type
        if (Directory.Exists(hostPath))
        {
            EnsureDirectory(current, name);
        }
        else if (File.Exists(hostPath))
        {
            var dentry = current.Inode!.Lookup(name);
            if (dentry == null)
            {
                dentry = new Dentry(name, null, current, current.SuperBlock);
                current.Inode.Create(dentry, 0x1FF, 0, 0); // 777
            }
            else if (dentry.Inode?.Type != InodeType.File)
            {
                throw new Exception($"Path /{name} exists but is not a file");
            }
        }
        else
        {
            throw new FileNotFoundException("Host path not found", hostPath);
        }

        var fsType = FileSystemRegistry.Get("hostfs") ?? throw new Exception("hostfs not registered");
        var optionsText = readOnly ? "ro" : "rw";
        var options = HostfsMountOptions.Parse(optionsText);
        var sb = new HostSuperBlock(fsType, hostPath, options);
        var rootDentry = sb.GetDentry(hostPath, "/", null) ??
                         throw new FileNotFoundException("Root path not found", hostPath);
        sb.Root = rootDentry;
        sb.Root.Parent = sb.Root;

        Mount(current, name, sb, hostPath, "hostfs", optionsText, guestPath);
    }

    private void Mount(Dentry parent, string name, SuperBlock sb, string source, string fstype, string options,
        string? targetOverride = null)
    {
        // Use cached dentry if available (PathWalk checks Children first, so we must mount on the same object)
        Dentry dentry;
        if (parent.Children.TryGetValue(name, out var cached))
        {
            dentry = cached;
        }
        else
        {
            dentry = parent.Inode!.Lookup(name) ?? throw new Exception($"Mount point {name} not found");
            parent.Children[name] = dentry; // Cache it so PathWalk finds the mounted dentry
        }

        // Mount it
        dentry.IsMounted = true;
        dentry.MountRoot = sb.Root;
        sb.Root.MountedAt = dentry;

        // Track mount
        var targetPath = targetOverride ?? BuildPath(dentry);
        AddMountInfo(source, targetPath, fstype, options);
    }

    internal void AddMountInfo(string source, string target, string fsType, string options)
    {
        MountList.RemoveAll(m => m.Target == target);
        MountList.Add(new MountInfo { Source = source, Target = target, FsType = fsType, Options = options });
    }

    internal void RemoveMountInfo(string target)
    {
        MountList.RemoveAll(m => m.Target == target);
    }

    private static string BuildPath(Dentry dentry)
    {
        var parts = new List<string>();
        var cur = dentry;
        while (cur != null)
        {
            if (cur.Name != "/") parts.Add(cur.Name);

            // Root of a mounted filesystem: continue from mountpoint in parent namespace.
            if (cur.MountedAt != null)
            {
                cur = cur.MountedAt;
                continue;
            }

            if (cur.Parent == null || cur.Parent == cur) break;
            cur = cur.Parent;
        }

        parts.Reverse();
        return "/" + string.Join("/", parts);
    }

    private void InitStdio(SuperBlock devSb, TtyDiscipline? tty)
    {
        // 0: Stdin, 1: Stdout, 2: Stderr
        var stdinInode = new ConsoleInode(devSb, true, tty);
        var stdinDentry = new Dentry("stdin", stdinInode, devSb.Root, devSb);
        FDs[0] = new VFS.LinuxFile(stdinDentry, FileFlags.O_RDONLY);

        var stdoutInode = new ConsoleInode(devSb, false, tty);
        var stdoutDentry = new Dentry("stdout", stdoutInode, devSb.Root, devSb);
        FDs[1] = new VFS.LinuxFile(stdoutDentry, FileFlags.O_WRONLY);
        FDs[2] = new VFS.LinuxFile(stdoutDentry, FileFlags.O_WRONLY);

        // Resolve the mounted /dev dentry (goes through OverlayFS -> mount -> devtmpfs root)
        var devRoot = PathWalk("/dev") ?? devSb.Root;
        var devRootInode = devRoot.Inode as TmpfsInode;

        // Helper to register a device entry properly
        void RegisterDev(string name, Dentry dentry)
        {
            if (devRootInode != null)
                devRootInode.RegisterChild(devRoot, name, dentry);
            else
                devRoot.Children[name] = dentry;
        }

        // Create /dev/null
        var nullInode = new ConsoleInode(devSb, true); // sink/source
        nullInode.Rdev = 0x0103; // Major 1, Minor 3
        var nullDentry = new Dentry("null", nullInode, devRoot, devSb);
        RegisterDev("null", nullDentry);

        // Create /dev/ptmx (PTY multiplexer)
        var signalBroadcaster = new SignalBroadcasterImpl(this);
        var ptmxInode = new PtmxInode(devSb, PtyManager, signalBroadcaster, Logger);
        ptmxInode.Rdev = PtyManager.GetPtmxRdev();
        var ptmxDentry = new Dentry("ptmx", ptmxInode, devRoot, devSb);
        RegisterDev("ptmx", ptmxDentry);

        // Create /dev/tty (controlling terminal)
        var ttyInode = new ConsoleInode(devSb, true, tty);
        ttyInode.Rdev = 0x0500; // TTY major 5, minor 0
        var ttyDentry = new Dentry("tty", ttyInode, devRoot, devSb);
        RegisterDev("tty", ttyDentry);

        // Create /dev/pts directory and mount devpts
        EnsureDirectory(devRoot, "pts");
        var devptsFsType = FileSystemRegistry.Get("devpts")!;
        var devptsSb = devptsFsType.FileSystem.ReadSuper(devptsFsType, 0, "devpts", null);
        Mount(devRoot, "pts", devptsSb, "devpts", "devpts", "rw,relatime,gid=5,mode=620", "/dev/pts");

        // Create /dev/urandom and /dev/random
        var randomInode = new RandomInode(devSb);
        randomInode.Rdev = 0x0109; // Major 1, Minor 9 (urandom)
        var urandomDentry = new Dentry("urandom", randomInode, devRoot, devSb);
        RegisterDev("urandom", urandomDentry);

        var randomDentry = new Dentry("random", randomInode, devRoot, devSb); // Reuse inode
        RegisterDev("random", randomDentry);
    }

    public void RegisterEngine(Engine engine)
    {
        lock (_registryLock)
        {
            _registry[engine.State] = this;
        }
    }

    public SyscallManager Clone(VMAManager newMem, bool shareFiles)
    {
        Dictionary<int, VFS.LinuxFile> newFds;
        if (shareFiles)
        {
            newFds = FDs;
        }
        else
        {
            newFds = [];
            foreach (var kv in FDs)
            {
                // fork/clone (without CLONE_FILES) should duplicate fd table entries,
                // but still reference the same open file description.
                kv.Value.Get();
                newFds[kv.Key] = kv.Value;
            }
        }

        var newSys = new SyscallManager(newMem, newFds, Futex, SysVShm, SysVSem, BrkAddr, BrkBase, Strace, Root,
            CurrentWorkingDirectory,
            ProcessRoot, DevShmRoot, MemfdSuperBlock, Tty)
        {
            CloneHandler = CloneHandler,
            ExitHandler = ExitHandler,
            GetTID = GetTID,
            GetTGID = GetTGID
        };
        return newSys;
    }

    public static SyscallManager? Get(IntPtr state)
    {
        lock (_registryLock)
        {
            return _registry.TryGetValue(state, out var sm) ? sm : null;
        }
    }

    private void Register(uint nr, SyscallHandler handler)
    {
        if (nr < MaxSyscalls) _syscallHandlers[nr] = handler;
    }

    public bool Handle(Engine engine, uint vector)
    {
        // Handle Breakpoint (INT 3)
        if (vector == 3)
        {
            if (engine.Owner is FiberTask t) t.PendingSignals |= 1UL << (5 - 1); // SIGTRAP = 5
            return true;
        }

        if (vector != 0x80) return false;

        // Update current engine context (GIL ensures safety)
        Engine = engine;

        // Get current FiberTask (New Model Only) via Engine.Owner
        var fiberTask = engine.Owner as FiberTask;

        // Save EIP for potential SA_RESTART before executing syscall
        // EIP points after 'int 0x80' (CD 80), so subtract 2 to get the syscall instruction
        if (fiberTask != null) fiberTask.SyscallEip = engine.Eip - 2;

        var eax = engine.RegRead(Reg.EAX);
        var ebx = engine.RegRead(Reg.EBX);
        var ecx = engine.RegRead(Reg.ECX);
        var edx = engine.RegRead(Reg.EDX);
        var esi = engine.RegRead(Reg.ESI);
        var edi = engine.RegRead(Reg.EDI);
        var ebp = engine.RegRead(Reg.EBP);

        if (Strace)
            SyscallTracer.TraceEntry(Logger, this, fiberTask?.TID ?? 0, eax, ebx, ecx, edx, esi, edi, ebp);

        ValueTask<int> retTask = new(-38); // ENOSYS

        if (eax < MaxSyscalls && _syscallHandlers[eax] != null)
            retTask = _syscallHandlers[eax]!(engine.State, ebx, ecx, edx, esi, edi, ebp);
        else if (!Strace) Logger.LogWarning("Unimplemented Syscall: {Eax}", eax);

        // --- Handling Async Syscalls ---
        if (retTask.IsCompleted)
        {
            var ret = retTask.Result;

            // Special handling for context-restoring syscalls
            var isSigReturn = eax == X86SyscallNumbers.rt_sigreturn || eax == X86SyscallNumbers.sigreturn;


            if (!isSigReturn) engine.RegWrite(Reg.EAX, (uint)ret);

            if (Strace)
                SyscallTracer.TraceExit(Logger, this, fiberTask?.TID ?? 0, eax, ret, ebx, ecx, edx);
        }
        else
        {
            // Async completion (Blocking)
            if (fiberTask != null)
            {
                // Save context for TraceExit
                fiberTask.SyscallNr = eax;
                fiberTask.SyscallArg1 = ebx;
                fiberTask.SyscallArg2 = ecx;
                fiberTask.SyscallArg3 = edx;

                // Suspend the task
                fiberTask.PendingSyscall = () => retTask;
                fiberTask.Status = FiberTaskStatus.Waiting;

                // Tracing
                if (Strace) Logger.LogTrace(" [Suspended]");

                // Force yield
                engine.Yield();
                return true;
            }

            // Should not happen in new model
            Logger.LogError("Async syscall initiated but no FiberTask attached!");
            engine.RegWrite(Reg.EAX, unchecked((uint)-(int)Errno.ENOSYS));
        }

        // Determine if we should yield
        var shouldYield = false;

        if (fiberTask != null)
        {
            if (fiberTask.PendingSyscall != null) shouldYield = true;
            if (fiberTask.Exited) shouldYield = true;
            if ((fiberTask.PendingSignals & ~fiberTask.SignalMask) != 0) shouldYield = true;

            // Force Yield if specific syscalls
            switch ((int)eax)
            {
                case X86SyscallNumbers.sched_yield:
                case X86SyscallNumbers.nanosleep:
                case X86SyscallNumbers.pause:
                case X86SyscallNumbers.rt_sigsuspend:
                case X86SyscallNumbers.select:
                case X86SyscallNumbers._newselect:
                case X86SyscallNumbers.poll:
                case X86SyscallNumbers.exit:
                case X86SyscallNumbers.exit_group:
                case X86SyscallNumbers.execve:
                case X86SyscallNumbers.kill:
                case X86SyscallNumbers.tkill:
                case X86SyscallNumbers.tgkill:
                case X86SyscallNumbers.wait4:
                case X86SyscallNumbers.waitpid:
                case X86SyscallNumbers.waitid:
                    shouldYield = true;
                    break;
            }
        }

        if (shouldYield) engine.Yield();

        return true;
    }

    public void Close()
    {
        lock (_registryLock)
        {
            if (Engine != null)
                _registry.Remove(Engine.State);
        }

        Root?.Inode?.Put();
        CurrentWorkingDirectory?.Inode?.Put();
        ProcessRoot?.Inode?.Put();

        foreach (var fd in FDs.Values)
            // Note: If shareFiles is true, this might be dangerous if multiple tasks close the same FDs
            // But usually SyscallManager.Close is called when the task/process actually dies.
            fd.Close();
        FDs.Clear();
    }

    public class MountInfo
    {
        public string Source { get; set; } = "none";
        public string Target { get; set; } = "/";
        public string FsType { get; set; } = "unknown";
        public string Options { get; set; } = "";
    }
}

/// <summary>
///     Implementation of ISignalBroadcaster that delegates to the KernelScheduler.
/// </summary>
file class SignalBroadcasterImpl : ISignalBroadcaster
{
    private readonly SyscallManager _sm;

    public SignalBroadcasterImpl(SyscallManager sm)
    {
        _sm = sm;
    }

    public void SignalProcessGroup(int pgid, int signal)
    {
        KernelScheduler.Current?.SignalProcessGroup(pgid, signal);
    }

    public void SignalForegroundTask(int signal)
    {
        // Signal the current task
        var task = KernelScheduler.Current?.CurrentTask;
        if (task != null) task.PendingSignals |= 1UL << (signal - 1);
    }
}
