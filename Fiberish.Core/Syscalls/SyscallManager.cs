using Fiberish.Core;
using Fiberish.Core.VFS.TTY;
using Fiberish.Diagnostics;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.VFS;
using Fiberish.X86.Native;
using Microsoft.Extensions.Logging;
using System.Threading;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
    public delegate ValueTask<int> SyscallHandler(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6);

    private const int MaxSyscalls = 512;
    private static readonly ILogger Logger = Logging.CreateLogger<SyscallManager>();
    private static readonly Dictionary<IntPtr, SyscallManager> _registry = [];
    private static readonly object _registryLock = new();
    private static readonly AsyncLocal<SyscallManager?> _activeSyscallManager = new();
    private readonly SyscallHandler?[] _syscallHandlers = new SyscallHandler?[MaxSyscalls];

    internal static SyscallManager? ActiveSyscallManager => _activeSyscallManager.Value;

    public SyscallManager(Engine engine, VMAManager mem, uint brk, TtyDiscipline? tty = null)
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

        // Register default filesystems
        FileSystemRegistry.TryRegister(new FileSystemType { Name = "hostfs", FileSystem = new Hostfs() });
        FileSystemRegistry.TryRegister(new FileSystemType { Name = "tmpfs", FileSystem = new Tmpfs() });
        FileSystemRegistry.TryRegister(new FileSystemType { Name = "devtmpfs", FileSystem = new Tmpfs() });
        FileSystemRegistry.TryRegister(new FileSystemType { Name = "overlay", FileSystem = new OverlayFileSystem() });
        FileSystemRegistry.TryRegister(new FileSystemType { Name = "proc", FileSystem = new ProcFileSystem() });

        PtyManager = new PtyManager(Logger);
        var signalBroadcaster = new SignalBroadcasterImpl(this);
        FileSystemRegistry.TryRegister(new FileSystemType
            { Name = "devpts", FileSystem = new DevPtsFileSystem(PtyManager, signalBroadcaster, Logger) });

        // Default memfd superblock
        var tmpFsType = FileSystemRegistry.Get("tmpfs")!;
        MemfdSuperBlock = tmpFsType.FileSystem.ReadSuper(tmpFsType, 0, "memfd", null);

        // Anonymous inode mount (like Linux's anon_inodefs)
        // Used for timerfd, eventfd, epoll, socket, etc.
        AnonMount = new Mount(MemfdSuperBlock, MemfdSuperBlock.Root)
        {
            Source = "none",
            FsType = "anon_inodefs",
            Options = "rw"
        };

        SetupVDSO();
    }

    private SyscallManager(VMAManager mem, Dictionary<int, LinuxFile> fds, FutexManager futex,
        SysVShmManager sysvShm, SysVSemManager sysvSem, uint brk,
        uint brkBase,
        bool strace,
        PathLocation root, PathLocation cwd, PathLocation procRoot, Dentry devShmRoot, SuperBlock memfdSuperBlock,
        TtyDiscipline? tty,
        PtyManager ptyManager,
        MountNamespace mountNamespace)
    {
        Mem = mem;
        FDs = fds;
        Futex = futex;
        SysVShm = sysvShm;
        SysVSem = sysvSem;
        BrkAddr = brk;
        BrkBase = brkBase;
        Strace = strace;
        Root = root;
        CurrentWorkingDirectory = cwd;
        ProcessRoot = procRoot;
        DevShmRoot = devShmRoot;
        MemfdSuperBlock = memfdSuperBlock;
        Tty = tty;
        PtyManager = ptyManager;

        // Share mount namespace
        _mountNamespace = mountNamespace;

        Root.Dentry!.Inode!.Get();
        CurrentWorkingDirectory.Dentry!.Inode!.Get();
        ProcessRoot.Dentry!.Inode!.Get();

        RegisterHandlers();
    }

    public TtyDiscipline? Tty { get; }

    // PTY Manager for /dev/ptmx and /dev/pts/N
    public PtyManager PtyManager { get; } = null!;

    // The current engine executing a syscall (protected by GIL)
    public Engine Engine { get; set; } = null!;

    public VMAManager Mem { get; set; }

    public PosixTimerManager PosixTimers { get; } = new();

    public PathLocation Root { get; set; } = PathLocation.None;
    public PathLocation CurrentWorkingDirectory { get; set; } = PathLocation.None;

    // For chroot tracking, we keep a PathLocation to the process root
    public PathLocation ProcessRoot { get; set; } = PathLocation.None;
    public Dentry DevShmRoot { get; set; } = null!;
    public SuperBlock MemfdSuperBlock { get; set; } = null!;

    /// <summary>
    ///     Mount for anonymous inode files (timerfd, eventfd, epoll, socket, etc.).
    ///     Similar to Linux's anon_inodefs.
    /// </summary>
    public Mount AnonMount { get; } = null!;

    // System V Shared Memory (Global IPC namespace)
    public SysVShmManager SysVShm { get; }
    public SysVSemManager SysVSem { get; }

    // File Descriptors (Shared if CLONE_FILES)
    public Dictionary<int, LinuxFile> FDs { get; } = [];

    public FutexManager Futex { get; }

    public uint BrkAddr { get; set; }
    public uint BrkBase { get; }
    public bool Strace { get; set; }

    /// <summary>
    ///     Mount namespace containing all mounts and lookup hash.
    /// </summary>
    private readonly MountNamespace _mountNamespace = new();

    /// <summary>
    ///     Gets mount information for /proc/mounts.
    ///     Dynamically generated from the current mount namespace state.
    /// </summary>
    public IEnumerable<(string Source, string Target, string FsType, string Options)> MountInfos =>
        _mountNamespace.GetMountInfos();

    public IEnumerable<MountNamespace.MountInfoEntry> MountInfoEntries =>
        _mountNamespace.GetMountInfoEntries();

    /// <summary>
    ///     The root mount (for the filesystem namespace).
    /// </summary>
    public Mount? RootMount
    {
        get => _mountNamespace.RootMount;
        private set => _mountNamespace.RootMount = value;
    }

    public uint SigReturnAddr { get; private set; }
    public uint RtSigReturnAddr { get; private set; }

    public void InitializeRoot(Dentry root, Mount rootMount)
    {
        Root = new PathLocation(root, rootMount);
        RootMount = rootMount;
        ProcessRoot = Root;
        CurrentWorkingDirectory = Root;

        Root.Dentry!.Inode!.Get();
        ProcessRoot.Dentry!.Inode!.Get();
        CurrentWorkingDirectory.Dentry!.Inode!.Get();

        RegisterMount(RootMount, null, root);
    }

    public void MountRootHostfs(string hostPath, string options = "rw,relatime")
    {
        var hostFsType = FileSystemRegistry.Get("hostfs")!;
        var sb = hostFsType.FileSystem.ReadSuper(hostFsType, 0, hostPath, null);
        var mount = new Mount(sb, sb.Root)
        {
            Source = hostPath,
            FsType = "hostfs",
            Options = options
        };
        InitializeRoot(sb.Root, mount);
    }

    public void MountRootOverlay(string hostRoot, string upperName = "overlay_upper",
        string options = "rw,relatime,lowerdir=/,upperdir=/overlay_upper,workdir=/work")
    {
        var hostFsType = FileSystemRegistry.Get("hostfs")!;
        var tmpFsType = FileSystemRegistry.Get("tmpfs")!;
        var overlayFsType = FileSystemRegistry.Get("overlay")!;

        var lowerSb = hostFsType.FileSystem.ReadSuper(hostFsType, 0, hostRoot, null);
        var upperSb = tmpFsType.FileSystem.ReadSuper(tmpFsType, 0, upperName, null);

        var overlayOptions = new OverlayMountOptions { Lower = lowerSb, Upper = upperSb };
        var overlaySb = overlayFsType.FileSystem.ReadSuper(overlayFsType, 0, "root_overlay", overlayOptions);

        var mount = new Mount(overlaySb, overlaySb.Root)
        {
            Source = "overlay",
            FsType = "overlay",
            Options = options
        };
        InitializeRoot(overlaySb.Root, mount);
    }

    public void MountStandardDev(TtyDiscipline? tty = null, bool ensureMountPoint = true)
    {
        if (ensureMountPoint) EnsureDirectory(Root, "dev");

        var devFsType = FileSystemRegistry.Get("devtmpfs")!;
        var devSb = devFsType.FileSystem.ReadSuper(devFsType, 0, "dev", null);

        var devDentry = Root.Dentry!.Inode?.Lookup("dev");
        if (devDentry != null && devDentry.Inode?.Type == InodeType.Directory)
            Mount(Root, "dev", devSb, "devtmpfs", "devtmpfs", "rw,relatime", "/dev");
        else
            Logger.LogWarning("/dev not found in rootfs, skipping devtmpfs mount.");

        // Initialize stdio FDs (uses devSb which might or might not be mounted)
        InitStdio(devSb, tty ?? Tty);

        // Mount devpts through the mounted /dev to avoid creating pts under a detached devtmpfs root.
        var devLoc = PathWalk("/dev");
        if (devLoc.IsValid && devLoc.Dentry!.Inode?.Type == InodeType.Directory)
        {
            EnsureDirectory(devLoc, "pts");
            var devptsFsType = FileSystemRegistry.Get("devpts")!;
            var devptsSb = devptsFsType.FileSystem.ReadSuper(devptsFsType, 0, "devpts", null);
            Mount(devLoc, "pts", devptsSb, "devpts", "devpts", "rw,relatime,gid=5,mode=620", "/dev/pts");
        }
        else
        {
            Logger.LogWarning("/dev not mounted, skipping /dev/pts mount.");
        }
    }

    public void MountStandardProc(bool ensureMountPoint = true)
    {
        if (ensureMountPoint) EnsureDirectory(Root, "proc");

        var procDentry = Root.Dentry!.Inode?.Lookup("proc");
        if (procDentry != null && procDentry.Inode?.Type == InodeType.Directory)
        {
            var procFsType = FileSystemRegistry.Get("proc")!;
            var procSb = procFsType.FileSystem.ReadSuper(procFsType, 0, "proc", this);
            Mount(Root, "proc", procSb, "proc", "proc", "rw,relatime", "/proc");
        }
        else
        {
            Logger.LogWarning("/proc not found in rootfs, skipping procfs mount.");
        }
    }

    public void MountStandardShm()
    {
        var tmpFsType = FileSystemRegistry.Get("tmpfs")!;
        var shmSb = tmpFsType.FileSystem.ReadSuper(tmpFsType, 0, "shm", null);

        // Resolve through the mounted /dev to avoid creating shm under a detached devtmpfs root.
        var devLoc = PathWalk("/dev");
        if (devLoc.IsValid && devLoc.Dentry!.Inode?.Type == InodeType.Directory)
        {
            EnsureDirectory(devLoc, "shm");
            Mount(devLoc, "shm", shmSb, "tmpfs", "tmpfs", "rw,nosuid,nodev", "/dev/shm");
        }
        else
        {
            Logger.LogWarning("/dev not mounted, skipping /dev/shm mount.");
        }

        DevShmRoot = shmSb.Root; // Always init DevShmRoot
    }

    public void CreateStandardTmp(bool ensureMountPoint = true)
    {
        if (ensureMountPoint) EnsureDirectory(Root, "tmp");
    }

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

    private static PathLocation EnsureDirectory(PathLocation parent, string name)
    {
        var parentDentry = parent.Dentry!;

        if (parentDentry.Children.TryGetValue(name, out var cached)) return new PathLocation(cached, parent.Mount);

        var dentry = parentDentry.Inode!.Lookup(name);
        if (dentry == null)
        {
            dentry = new Dentry(name, null, parentDentry, parentDentry.SuperBlock);
            try
            {
                parentDentry.Inode.Mkdir(dentry, 0x1FF, 0, 0); // 777
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Failed to create directory {Name}: {Error}", name, ex.Message);
                throw;
            }
        }
        else if (dentry.Inode?.Type != InodeType.Directory)
        {
            throw new Exception($"Path /{name} exists but is not a directory");
        }

        parentDentry.Children[name] = dentry;
        return new PathLocation(dentry, parent.Mount);
    }

    public void MountHostfs(string hostPath, string guestPath, bool readOnly = false)
    {
        var parts = guestPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new ArgumentException("Cannot mount at root");

        var current = Root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            current = EnsureDirectory(current, parts[i]);
        }

        var name = parts[^1];
        var isFile = File.Exists(hostPath);
        var isDir = Directory.Exists(hostPath);

        // Create the mount point if it doesn't exist
        Dentry? finalDentry = null;
        if (isDir)
        {
            finalDentry = EnsureDirectory(current, name).Dentry;
        }
        else if (isFile)
        {
            // For file mounts, create an empty file as mount point if it doesn't exist
            if (current.Dentry!.Children.TryGetValue(name, out var cachedDentry))
            {
                finalDentry = cachedDentry;
                if (finalDentry.Inode?.Type != InodeType.File)
                    throw new Exception($"Path /{name} exists but is not a file");
            }
            else
            {
                finalDentry = current.Dentry.Inode!.Lookup(name);
                if (finalDentry == null)
                {
                    finalDentry = new Dentry(name, null, current.Dentry!, current.Dentry!.SuperBlock);
                    try
                    {
                        current.Dentry!.Inode.Create(finalDentry, 0x1FF, 0, 0); // 777
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning("Failed to create file mount point {Name}: {Error}", name, ex.Message);
                        // If Create fails, try using the dentry anyway - the mount will provide the content
                        finalDentry.Instantiate(new PlaceholderInode(current.Dentry!.SuperBlock));
                    }
                }
                else if (finalDentry.Inode?.Type != InodeType.File)
                {
                    throw new Exception($"Path /{name} exists but is not a file");
                }

                current.Dentry!.Children[name] = finalDentry;
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

    private void Mount(PathLocation parent, string name, SuperBlock sb, string source, string fstype, string options,
        string? targetOverride = null)
    {
        // Use cached dentry if available (PathWalk checks Children first, so we must mount on the same object)
        Dentry dentry;
        if (parent.Dentry!.Children.TryGetValue(name, out var cached))
        {
            dentry = cached;
        }
        else
        {
            dentry = parent.Dentry.Inode!.Lookup(name) ?? throw new Exception($"Mount point {name} not found");
            parent.Dentry.Children[name] = dentry; // Cache it so PathWalk finds the mounted dentry
        }

        // Determine parent mount
        var parentMount = parent.Mount;

        // Create new Mount object and attach it
        var mount = new Mount(sb, sb.Root)
        {
            Source = source,
            FsType = fstype,
            Options = options
        };

        RegisterMount(mount, parentMount, dentry);

        var targetPath = targetOverride ?? BuildPath(dentry);
    }

    /// <summary>
    ///     Registers a mount in the system and attaches it to a mount point.
    /// </summary>
    public void RegisterMount(Mount mount, Mount? parent, Dentry mountPoint)
    {
        _mountNamespace.RegisterMount(mount, parent, mountPoint);
    }

    /// <summary>
    ///     Unregisters a mount from the system and detaches it.
    /// </summary>
    public void UnregisterMount(Mount mount)
    {
        _mountNamespace.UnregisterMount(mount);
    }

    /// <summary>
    ///     Find a child mount at the given dentry in the parent mount.
    /// </summary>
    public Mount? FindMount(Mount? parent, Dentry dentry)
    {
        return _mountNamespace.FindMount(parent, dentry);
    }

    public string GetAbsolutePath(PathLocation loc)
    {
        var parts = new List<string>();
        var current = loc.Dentry;
        var currentMount = loc.Mount;

        while (current != null)
        {
            if (current == ProcessRoot.Dentry && (currentMount == null || currentMount == ProcessRoot.Mount))
                break;

            if (currentMount != null && current == currentMount.Root && currentMount.Parent != null)
            {
                current = currentMount.MountPoint!;
                currentMount = currentMount.Parent;
                continue;
            }

            if (current.Name != "/") parts.Add(current.Name);

            if (current.Parent == null || current.Parent == current) break;
            current = current.Parent;
        }

        parts.Reverse();
        return "/" + string.Join("/", parts);
    }

    private static string BuildPath(Dentry dentry)
    {
        var parts = new List<string>();
        var cur = dentry;
        while (cur != null)
        {
            if (cur.Name != "/") parts.Add(cur.Name);

            if (cur.Parent == null || cur.Parent == cur) break;
            cur = cur.Parent;
        }

        parts.Reverse();
        return "/" + string.Join("/", parts);
    }

    private void InitStdio(SuperBlock devSb, TtyDiscipline? tty)
    {
        // 0: Stdin, 1: Stdout, 2: Stderr
        // Use AnonMount for console devices (they are not backed by a real filesystem)
        var stdinInode = new ConsoleInode(devSb, true, tty);
        var stdinDentry = new Dentry("stdin", stdinInode, devSb.Root, devSb);
        FDs[0] = new LinuxFile(stdinDentry, FileFlags.O_RDONLY, AnonMount);

        var stdoutInode = new ConsoleInode(devSb, false, tty);
        var stdoutDentry = new Dentry("stdout", stdoutInode, devSb.Root, devSb);
        FDs[1] = new LinuxFile(stdoutDentry, FileFlags.O_WRONLY, AnonMount);
        FDs[2] = new LinuxFile(stdoutDentry, FileFlags.O_WRONLY, AnonMount);

        // Resolve the mounted /dev dentry (goes through OverlayFS -> mount -> devtmpfs root)
        var devRoot = devSb.Root;
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
        var nullInode = new NullInode(devSb);
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
        Dictionary<int, LinuxFile> newFds;
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

        // Share the mount namespace (mounts are shared across fork/clone by default)
        var sharedNamespace = _mountNamespace.Share();

        var newSys = new SyscallManager(newMem, newFds, Futex, SysVShm, SysVSem, BrkAddr, BrkBase, Strace, Root,
            CurrentWorkingDirectory,
            ProcessRoot, DevShmRoot, MemfdSuperBlock, Tty, PtyManager,
            sharedNamespace)
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
        var previous = _activeSyscallManager.Value;
        _activeSyscallManager.Value = this;
        try
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

        ValueTask<int> retTask = new(-(int)Errno.ENOSYS);

        if (eax < MaxSyscalls && _syscallHandlers[eax] != null)
            retTask = _syscallHandlers[eax]!(engine.State, ebx, ecx, edx, esi, edi, ebp);
        else if (!Strace) Logger.LogWarning("Unimplemented Syscall: {Eax}", eax);

        // --- Handling Async Syscalls ---
        if (retTask.IsCompleted && retTask.Result != -(int)Errno.ERESTARTSYS)
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
            // Async completion (Blocking) OR requires HandleAsyncSyscall (-ERESTARTSYS handling)
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
        finally
        {
            _activeSyscallManager.Value = previous;
        }
    }

    public void Close()
    {
        lock (_registryLock)
        {
            if (Engine != null)
                _registry.Remove(Engine.State);
        }

        Root.Dentry?.Inode?.Put();
        CurrentWorkingDirectory.Dentry?.Inode?.Put();
        ProcessRoot.Dentry?.Inode?.Put();

        foreach (var fd in FDs.Values)
            // Note: If shareFiles is true, this might be dangerous if multiple tasks close the same FDs
            // But usually SyscallManager.Close is called when the task/process actually dies.
            fd.Close();
        FDs.Clear();
    }

    private class PlaceholderInode : Inode
    {
        public PlaceholderInode(SuperBlock sb)
        {
            SuperBlock = sb;
            Type = InodeType.File;
            Mode = 0;
            MTime = ATime = CTime = DateTime.Now;
        }
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
