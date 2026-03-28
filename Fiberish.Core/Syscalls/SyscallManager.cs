using System.Runtime.CompilerServices;
using Fiberish.Core;
using Fiberish.Core.Net;
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
    public delegate ValueTask<int> SyscallHandler(Engine engine, uint a1, uint a2, uint a3, uint a4, uint a5,
        uint a6);

    public const uint MountFlagMask = LinuxConstants.MS_RDONLY | LinuxConstants.MS_NOSUID |
                                      LinuxConstants.MS_NODEV | LinuxConstants.MS_NOEXEC;

    private const int MaxSyscalls = 512;
    private static readonly ILogger Logger = Logging.CreateLogger<SyscallManager>();
    private readonly List<Mount> _containerOwnedMounts = [];
    private readonly FileSystemType _devptsFsType;

    /// <summary>
    ///     Mount namespace containing all mounts and lookup hash.
    /// </summary>
    private readonly MountNamespace _mountNamespace;

    private readonly SharedFdTable _sharedFdTable;

    private readonly SharedFsState _sharedFsState;
    private readonly SharedUnixSocketNamespace _sharedUnixSocketNamespace;
    private readonly SyscallHandler?[] _syscallHandlers = new SyscallHandler?[MaxSyscalls];

    private int _closed;
    private SharedLoopbackNetNamespace? _privateNetNamespace;

    public SyscallManager(Engine engine, VMAManager mem, uint brk, TtyDiscipline? tty = null,
        DeviceNumberManager? deviceNumbers = null)
    {
        _mountNamespace = new MountNamespace();
        _sharedFdTable = new SharedFdTable();
        _sharedUnixSocketNamespace = new SharedUnixSocketNamespace();
        _sharedFsState = new SharedFsState();
        DeviceNumbers = deviceNumbers ?? new DeviceNumberManager();
        CurrentSyscallEngine = engine;
        Mem = mem;
        BrkAddr = brk;
        BrkBase = brk;
        Tty = tty;
        Futex = new FutexManager();
        SysVShm = new SysVShmManager(mem.Backings);
        SysVSem = new SysVSemManager();

        RegisterEngine(engine);
        RegisterHandlers();

        // Register default filesystems
        FileSystemRegistry.TryRegister(new FileSystemType { Name = "hostfs", Factory = devMgr => new Hostfs(devMgr) });
        FileSystemRegistry.TryRegister(new FileSystemType { Name = "tmpfs", Factory = devMgr => new Tmpfs(devMgr) });
        FileSystemRegistry.TryRegister(new FileSystemType { Name = "devtmpfs", Factory = devMgr => new Tmpfs(devMgr) });
        FileSystemRegistry.TryRegister(new FileSystemType
            { Name = "overlay", Factory = devMgr => new OverlayFileSystem(devMgr) });
        FileSystemRegistry.TryRegister(new FileSystemType
            { Name = "layerfs", Factory = devMgr => new LayerFileSystem(devMgr) });
        FileSystemRegistry.TryRegister(new FileSystemType
            { Name = "silkfs", Factory = devMgr => new SilkFileSystem(devMgr) });
        FileSystemRegistry.TryRegister(
            new FileSystemType { Name = "proc", Factory = devMgr => new ProcFileSystem(devMgr) });

        PtyManager = new PtyManager(Logger);
        var signalBroadcaster = new SignalBroadcasterImpl();
        _devptsFsType = CreateDevPtsFileSystemType(signalBroadcaster);

        // Default memfd superblock
        var tmpFsType = FileSystemRegistry.Get("tmpfs")!;
        MemfdSuperBlock = tmpFsType.CreateFileSystem(DeviceNumbers).ReadSuper(tmpFsType, 0, "memfd", null);
        MemfdSuperBlock.MemoryContext = CurrentSyscallEngine.MemoryContext;

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

    private SyscallManager(VMAManager mem, SharedFdTable sharedFdTable,
        SharedUnixSocketNamespace sharedUnixSocketNamespace,
        SharedFsState sharedFsState,
        FutexManager futex,
        SysVShmManager sysvShm, SysVSemManager sysvSem, uint brk,
        uint brkBase,
        bool strace, Dentry devShmRoot, SuperBlock memfdSuperBlock,
        Mount anonMount,
        TtyDiscipline? tty,
        PtyManager ptyManager,
        MountNamespace mountNamespace,
        SharedLoopbackNetNamespace? privateNetNamespace,
        DeviceNumberManager deviceNumbers)
    {
        Mem = mem;
        _sharedFdTable = sharedFdTable;
        _sharedUnixSocketNamespace = sharedUnixSocketNamespace;
        DeviceNumbers = deviceNumbers;
        Futex = futex;
        SysVShm = sysvShm;
        SysVSem = sysvSem;
        BrkAddr = brk;
        BrkBase = brkBase;
        Strace = strace;
        _sharedFsState = sharedFsState;
        DevShmRoot = devShmRoot;
        MemfdSuperBlock = memfdSuperBlock;
        AnonMount = anonMount;
        Tty = tty;
        PtyManager = ptyManager;

        // Share mount namespace
        _mountNamespace = mountNamespace;
        _devptsFsType = CreateDevPtsFileSystemType(new SignalBroadcasterImpl());

        RegisterHandlers();

        _privateNetNamespace = privateNetNamespace;
    }

    internal DeviceNumberManager DeviceNumbers { get; }

    public TtyDiscipline? Tty { get; }

    // PTY Manager for /dev/ptmx and /dev/pts/N
    public PtyManager PtyManager { get; } = null!;

    // Ambient engine for the currently executing synchronous syscall only.
    // Do not capture or depend on this across await/callback boundaries.
    public Engine CurrentSyscallEngine { get; set; } = null!;
    internal FiberTask? CurrentTask => CurrentSyscallEngine.Owner as FiberTask;
    internal Process? CurrentProcess => CurrentTask?.Process;

    public VMAManager Mem { get; set; }

    public PosixTimerManager PosixTimers { get; } = new();

    public PathLocation Root => _sharedFsState.Root;
    public PathLocation CurrentWorkingDirectory => _sharedFsState.CurrentWorkingDirectory;

    // For chroot tracking, we keep a PathLocation to the process root.
    public PathLocation ProcessRoot => _sharedFsState.ProcessRoot;
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
    public Dictionary<int, LinuxFile> FDs => _sharedFdTable.Fds;

    public FutexManager Futex { get; }

    public uint BrkAddr { get; set; }
    public uint BrkBase { get; }
    public bool Strace { get; set; }
    public NetworkMode NetworkMode { get; set; } = NetworkMode.Host;

    /// <summary>
    ///     Gets mount information for /proc/mounts.
    ///     Dynamically generated from the current mount namespace state.
    /// </summary>
    public IEnumerable<(string Source, string Target, string FsType, string Options)> MountInfos =>
        _mountNamespace.GetMountInfos();

    public IEnumerable<MountNamespace.MountInfoEntry> MountInfoEntries =>
        _mountNamespace.GetMountInfoEntries();

    public IReadOnlyList<Mount> Mounts => _mountNamespace.Mounts;

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

    private HashSet<int> FdCloseOnExecSet => _sharedFdTable.CloseOnExec;

    public LoopbackNetNamespace GetOrCreatePrivateNetNamespace()
    {
        if (OperatingSystem.IsBrowser())
            throw new PlatformNotSupportedException("Private netstack is not supported on browser-wasm.");

        return (_privateNetNamespace ??= new SharedLoopbackNetNamespace(LoopbackNetNamespace.Create(0x0A590002u, 24)))
            .Namespace;
    }

    public LoopbackNetNamespace? TryGetPrivateNetNamespace()
    {
        return _privateNetNamespace?.Namespace;
    }

    public void SetPrivateNetNamespace(SharedLoopbackNetNamespace sharedNamespace)
    {
        _privateNetNamespace?.Release();
        _privateNetNamespace = sharedNamespace.AddRef();
    }

    private static void PinPathLocation(PathLocation loc, string reason)
    {
        loc.Mount?.Get();
        loc.Dentry?.Get(reason);
        loc.Dentry?.Inode?.AcquireRef(InodeRefKind.PathPin, reason);
    }

    private static void UnpinPathLocation(PathLocation loc, string reason)
    {
        loc.Dentry?.Inode?.ReleaseRef(InodeRefKind.PathPin, reason);
        loc.Dentry?.Put(reason);
        loc.Mount?.Put();
    }

    internal void UpdateCurrentWorkingDirectory(PathLocation next, string reason)
    {
        _sharedFsState.UpdateCurrentWorkingDirectory(next, reason);
    }

    internal void UpdateProcessRoot(PathLocation next, string reason)
    {
        _sharedFsState.UpdateProcessRoot(next, reason);
    }

    public void InitializeRoot(Dentry root, Mount rootMount)
    {
        var newRoot = new PathLocation(root, rootMount);
        _sharedFsState.ReplaceAll(newRoot, newRoot, newRoot, "initialize");
        RootMount = rootMount;

        RegisterMount(RootMount, null, root);
    }

    public Mount CreateRootMount(SuperBlock sb, RootMountOptions? options = null)
    {
        options ??= new RootMountOptions();
        var flags = options.Flags ?? ParseMountFlagsFromOptions(options.Options);
        var mountRoot = options.Root ?? sb.Root;
        var mount = new Mount(sb, mountRoot)
        {
            Flags = flags,
            Source = options.Source,
            FsType = options.FsType,
            Options = options.Options
        };
        return mount;
    }

    public void MountRoot(Mount rootMount)
    {
        if (rootMount == null) throw new ArgumentNullException(nameof(rootMount));
        InitializeRoot(rootMount.Root, rootMount);
    }

    public void MountRoot(SuperBlock sb, RootMountOptions? options = null)
    {
        sb.MemoryContext = CurrentSyscallEngine.MemoryContext;
        MountRoot(CreateRootMount(sb, options));
    }

    public void MountRootHostfs(string hostPath, string options = "rw,relatime")
    {
        var hostFsType = FileSystemRegistry.Get("hostfs")!;
        var sb = hostFsType.CreateFileSystem(DeviceNumbers).ReadSuper(hostFsType, 0, hostPath, options);
        sb.MemoryContext = CurrentSyscallEngine.MemoryContext;
        MountRoot(sb, new RootMountOptions
        {
            Source = hostPath,
            FsType = "hostfs",
            Options = options
        });
    }

    public void MountRootOverlay(string hostRoot, string upperName = "overlay_upper",
        string options = "rw,relatime,lowerdir=/,upperdir=/overlay_upper,workdir=/work")
    {
        MountRootOverlayWithUpper(hostRoot, "tmpfs", upperName, options);
    }

    public void MountRootOverlayWithUpper(string hostRoot, string upperFsType, string upperSource,
        string options = "rw,relatime,lowerdir=/,upperdir=/overlay_upper,workdir=/work")
    {
        var hostFsType = FileSystemRegistry.Get("hostfs")!;
        var lowerSb = hostFsType.CreateFileSystem(DeviceNumbers).ReadSuper(hostFsType, 0, hostRoot, null);
        lowerSb.MemoryContext = CurrentSyscallEngine.MemoryContext;
        MountRootOverlayWithLower(lowerSb, upperFsType, upperSource, options);
    }

    public void MountRootOverlayWithLower(SuperBlock lowerSb, string upperFsType, string upperSource,
        string options = "rw,relatime,lowerdir=/,upperdir=/overlay_upper,workdir=/work")
    {
        var upperType = FileSystemRegistry.Get(upperFsType) ??
                        throw new Exception($"Upper filesystem not registered: {upperFsType}");
        var overlayFsType = FileSystemRegistry.Get("overlay")!;
        var upperSb = upperType.CreateFileSystem(DeviceNumbers).ReadSuper(upperType, 0, upperSource, null);
        upperSb.MemoryContext = CurrentSyscallEngine.MemoryContext;

        var overlayOptions = new OverlayMountOptions { Lower = lowerSb, Upper = upperSb };
        var overlaySb = overlayFsType.CreateFileSystem(DeviceNumbers)
            .ReadSuper(overlayFsType, 0, "root_overlay", overlayOptions);
        overlaySb.MemoryContext = CurrentSyscallEngine.MemoryContext;

        MountRoot(overlaySb, new RootMountOptions
        {
            Source = "overlay",
            FsType = "overlay",
            Options = options
        });
    }

    public void MountStandardDev(TtyDiscipline? tty = null, bool ensureMountPoint = true)
    {
        var devLoc = ensureMountPoint ? EnsureDirectory(Root, "dev") : PathWalk("/dev");
        var devFsType = FileSystemRegistry.Get("devtmpfs")!;
        var devSb = devFsType.CreateFileSystem(DeviceNumbers).ReadSuper(devFsType, 0, "dev", null);
        devSb.MemoryContext = CurrentSyscallEngine.MemoryContext;

        if (devLoc.IsValid && devLoc.Dentry!.Inode?.Type == InodeType.Directory)
        {
            var devMount = CreateDetachedMount(devSb, "devtmpfs", "devtmpfs", 0);
            var attachRc = AttachDetachedMount(devMount, devLoc);
            if (attachRc != 0)
                Logger.LogWarning("Failed to mount devtmpfs at /dev: rc={Rc}", attachRc);
        }
        else
        {
            Logger.LogWarning("/dev not found in rootfs, skipping devtmpfs mount.");
        }

        // Initialize stdio FDs (uses devSb which might or might not be mounted)
        InitStdio(devSb, tty ?? Tty);

        // Mount devpts through the mounted /dev to avoid creating pts under a detached devtmpfs root.
        var mountedDevLoc = PathWalk("/dev");
        if (mountedDevLoc.IsValid && mountedDevLoc.Dentry!.Inode?.Type == InodeType.Directory)
        {
            var ptsLoc = EnsureDirectory(mountedDevLoc, "pts");
            var devptsSb = _devptsFsType.CreateFileSystem(DeviceNumbers)
                .ReadSuper(_devptsFsType, 0, "devpts", null);
            devptsSb.MemoryContext = CurrentSyscallEngine.MemoryContext;
            var devptsMount = CreateDetachedMount(devptsSb, "devpts", "devpts", 0, "gid=5,mode=620");
            var attachRc = AttachDetachedMount(devptsMount, ptsLoc);
            if (attachRc != 0)
                Logger.LogWarning("Failed to mount devpts at /dev/pts: rc={Rc}", attachRc);
        }
        else
        {
            Logger.LogWarning("/dev not mounted, skipping /dev/pts mount.");
        }
    }

    private FileSystemType CreateDevPtsFileSystemType(ISignalBroadcaster signalBroadcaster)
    {
        return new FileSystemType
        {
            Name = "devpts",
            Factory = devMgr => new DevPtsFileSystem(devMgr, PtyManager, signalBroadcaster, Logger)
        };
    }

    public void MountStandardProc(bool ensureMountPoint = true)
    {
        var procLoc = ensureMountPoint ? EnsureDirectory(Root, "proc") : PathWalk("/proc");
        if (procLoc.IsValid && procLoc.Dentry!.Inode?.Type == InodeType.Directory)
        {
            var procFsType = FileSystemRegistry.Get("proc")!;
            var procSb = procFsType.CreateFileSystem(DeviceNumbers).ReadSuper(procFsType, 0, "proc", this);
            procSb.MemoryContext = CurrentSyscallEngine.MemoryContext;
            var procMount = CreateDetachedMount(procSb, "proc", "proc", 0);
            var attachRc = AttachDetachedMount(procMount, procLoc);
            if (attachRc != 0)
                Logger.LogWarning("Failed to mount procfs at /proc: rc={Rc}", attachRc);
        }
        else
        {
            Logger.LogWarning("/proc not found in rootfs, skipping procfs mount.");
        }
    }

    public void MountStandardShm()
    {
        var tmpFsType = FileSystemRegistry.Get("tmpfs")!;
        var shmSb = tmpFsType.CreateFileSystem(DeviceNumbers).ReadSuper(tmpFsType, 0, "shm", null);
        shmSb.MemoryContext = CurrentSyscallEngine.MemoryContext;

        // Resolve through the mounted /dev to avoid creating shm under a detached devtmpfs root.
        var devLoc = PathWalk("/dev");
        if (devLoc.IsValid && devLoc.Dentry!.Inode?.Type == InodeType.Directory)
        {
            var shmLoc = EnsureDirectory(devLoc, "shm");
            var shmMount = CreateDetachedMount(shmSb, "tmpfs", "tmpfs",
                LinuxConstants.MS_NOSUID | LinuxConstants.MS_NODEV);
            var attachRc = AttachDetachedMount(shmMount, shmLoc);
            if (attachRc != 0)
                Logger.LogWarning("Failed to mount tmpfs at /dev/shm: rc={Rc}", attachRc);
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
        // Map vDSO page writable for setup first, then reprotect it RX after the trampolines are installed.
        uint vdsoAddr = 0x7FFF0000;
        ProcessAddressSpaceSync.Mmap(Mem, CurrentSyscallEngine, vdsoAddr, 4096, Protection.Read | Protection.Write,
            MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[vdso]");

        // Prefault a writable private page for initial setup.
        if (!Mem.PrefaultRange(vdsoAddr, 4096, CurrentSyscallEngine, true))
            throw new OutOfMemoryException("Failed to allocate vDSO page");

        // Write trampolines
        // __kernel_sigreturn: pop eax; mov eax, 119; int 0x80
        byte[] sigret = [0x58, 0xB8, 0x77, 0x00, 0x00, 0x00, 0xCD, 0x80];
        if (!CurrentSyscallEngine.CopyToUser(vdsoAddr, sigret))
            Logger.LogError("Failed to write sigreturn trampoline to vDSO");
        SigReturnAddr = vdsoAddr;

        // __kernel_rt_sigreturn: mov eax, 173; int 0x80
        byte[] rtsigret = [0xB8, 0xAD, 0x00, 0x00, 0x00, 0xCD, 0x80];
        if (!CurrentSyscallEngine.CopyToUser(vdsoAddr + 16, rtsigret))
            Logger.LogError("Failed to write rt_sigreturn trampoline to vDSO");
        RtSigReturnAddr = vdsoAddr + 16;

        // Switch to the final RX permissions through mprotect so the VMA metadata stays in sync with the engine.
        var mprotectRc = ProcessAddressSpaceSync.Mprotect(Mem, CurrentSyscallEngine, vdsoAddr, 4096,
            Protection.Read | Protection.Exec);
        if (mprotectRc != 0)
            throw new InvalidOperationException($"Failed to reprotect vDSO page: rc={mprotectRc}");

        Logger.LogInformation("vDSO mapped at 0x{Addr:x}, sigreturn=0x{S:x}, rt_sigreturn=0x{R:x}", vdsoAddr,
            SigReturnAddr, RtSigReturnAddr);
    }

    private static PathLocation EnsureDirectory(PathLocation parent, string name)
    {
        var parentDentry = parent.Dentry!;

        if (parentDentry.TryGetCachedChild(name, out var cached)) return new PathLocation(cached, parent.Mount);

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

        parentDentry.CacheChild(dentry, "SyscallManager.EnsureDirectory");
        return new PathLocation(dentry, parent.Mount);
    }

    private static Dentry EnsureFileMountPoint(PathLocation parent, string name)
    {
        var parentDentry = parent.Dentry!;

        if (parentDentry.TryGetCachedChild(name, out var cachedDentry))
        {
            if (cachedDentry.Inode?.Type != InodeType.File)
                throw new Exception($"Path /{name} exists but is not a file");
            return cachedDentry;
        }

        var dentry = parentDentry.Inode!.Lookup(name);
        if (dentry == null)
        {
            dentry = new Dentry(name, null, parentDentry, parentDentry.SuperBlock);
            try
            {
                parentDentry.Inode.Create(dentry, 0x1FF, 0, 0); // 777
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Failed to create file mount point {Name}: {Error}", name, ex.Message);
                dentry.Instantiate(new PlaceholderInode(parentDentry.SuperBlock));
            }
        }
        else if (dentry.Inode?.Type != InodeType.File)
        {
            throw new Exception($"Path /{name} exists but is not a file");
        }

        parentDentry.CacheChild(dentry, "SyscallManager.EnsureFileMountPoint");
        return dentry;
    }

    public void MountHostfs(string hostPath, string guestPath, bool readOnly = false)
    {
        var parts = guestPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new ArgumentException("Cannot mount at root");

        var current = Root;
        for (var i = 0; i < parts.Length - 1; i++) current = EnsureDirectory(current, parts[i]);

        var name = parts[^1];
        var isFile = File.Exists(hostPath);
        var isDir = Directory.Exists(hostPath);

        // Create the mount point if it doesn't exist
        if (isDir)
            EnsureDirectory(current, name);
        else if (isFile)
            EnsureFileMountPoint(current, name);
        else
            throw new FileNotFoundException("Host path not found", hostPath);

        var fsCtx = new FsContextFile(AnonMount.Root, AnonMount, "hostfs");
        fsCtx.SetString("source", hostPath);
        if (readOnly)
            fsCtx.SetFlag("ro");
        else
            fsCtx.SetFlag("rw");

        fsCtx.State = FsContextState.Created;

        var mountRc = CreateDetachedMountFromFsContext(fsCtx, 0, out var detachedMount);
        if (mountRc != 0 || detachedMount == null)
            throw new IOException($"Failed to create detached hostfs mount: rc={mountRc}");

        var mountHandle = new MountFile(detachedMount);
        try
        {
            if (!current.Dentry!.TryGetCachedChild(name, out var mountPoint))
                throw new IOException($"Mount point not found after ensure: {name}");
            var targetLoc = new PathLocation(mountPoint, current.Mount);
            var attachRc = AttachDetachedMount(mountHandle.Mount, targetLoc);
            if (attachRc != 0)
                throw new IOException($"Failed to attach detached hostfs mount: rc={attachRc}");
        }
        finally
        {
            mountHandle.Close();
        }
    }

    public void MountDetachedTmpfsFile(string guestPath, string sourceName, ReadOnlySpan<byte> content,
        bool readOnly = true)
    {
        var parts = guestPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new ArgumentException("Cannot mount at root");

        var current = Root;
        for (var i = 0; i < parts.Length - 1; i++) current = EnsureDirectory(current, parts[i]);

        var name = parts[^1];
        EnsureFileMountPoint(current, name);

        var fsCtx = new FsContextFile(AnonMount.Root, AnonMount, "tmpfs");
        fsCtx.SetString("source", $"detached:{sourceName}");
        fsCtx.SetFlag("nosuid");
        fsCtx.SetFlag("nodev");
        if (readOnly) fsCtx.SetFlag("ro");
        fsCtx.State = FsContextState.Created;

        var resolveRc = ResolveFsContextMountPlan(fsCtx, 0, out var sb, out var mountSource, out var fsTypeName,
            out var mountFlags, out var extraOptions);
        if (resolveRc != 0 || sb == null || string.IsNullOrEmpty(mountSource) || string.IsNullOrEmpty(fsTypeName))
            throw new IOException($"Failed to resolve fs context: rc={resolveRc}");

        var fileDentry = new Dentry(sourceName, null, sb.Root, sb);
        sb.Root.Inode!.Create(fileDentry, 0x1A4, 0, 0); // 0644
        if (!content.IsEmpty)
        {
            var file = new LinuxFile(fileDentry, FileFlags.O_WRONLY, null!);
            try
            {
                fileDentry.Inode!.Open(file);
                var rc = fileDentry.Inode.Write(file, content, 0);
                if (rc < 0) throw new IOException($"Failed to write detached tmpfs file: rc={rc}");
            }
            finally
            {
                fileDentry.Inode!.Release(file);
            }
        }

        sb.Root = fileDentry;
        sb.Root.Parent = sb.Root;

        // Align with detached mount fd semantics:
        // 1) create detached mount, 2) hold via MountFile handle, 3) attach to target.
        var detachedMount = CreateDetachedMount(sb, mountSource!, fsTypeName!, mountFlags, extraOptions);
        var mountHandle = new MountFile(detachedMount);
        try
        {
            if (!current.Dentry!.TryGetCachedChild(name, out var mountPoint))
                throw new IOException($"Mount point not found after ensure: {name}");
            var targetLoc = new PathLocation(mountPoint, current.Mount);
            var attachRc = AttachDetachedMount(mountHandle.Mount, targetLoc);
            if (attachRc != 0) throw new IOException($"Failed to attach detached tmpfs mount: rc={attachRc}");
            PinContainerMount(mountHandle.Mount);
        }
        finally
        {
            mountHandle.Close();
        }
    }

    public void PinContainerMount(Mount mount)
    {
        mount.Get();
        _containerOwnedMounts.Add(mount);
    }

    public void ReleaseContainerPins()
    {
        for (var i = _containerOwnedMounts.Count - 1; i >= 0; i--)
            _containerOwnedMounts[i].Put();
        _containerOwnedMounts.Clear();
    }

    public Mount CreateDetachedMount(SuperBlock sb, string source, string fsType, uint flags,
        string? extraOptions = null)
    {
        return new Mount(sb, sb.Root)
        {
            Source = source,
            FsType = fsType,
            Flags = flags,
            Options = BuildMountOptions(flags, extraOptions)
        };
    }

    public static string BuildMountOptions(uint flags, string? extraOptions = null)
    {
        var opts = new List<string> { (flags & LinuxConstants.MS_RDONLY) != 0 ? "ro" : "rw" };
        if ((flags & LinuxConstants.MS_NOSUID) != 0) opts.Add("nosuid");
        if ((flags & LinuxConstants.MS_NODEV) != 0) opts.Add("nodev");
        if ((flags & LinuxConstants.MS_NOEXEC) != 0) opts.Add("noexec");
        opts.Add("relatime");
        if (!string.IsNullOrWhiteSpace(extraOptions)) opts.Add(extraOptions!);
        return string.Join(",", opts);
    }

    public static uint ApplyMountFlagUpdate(uint currentFlags, uint setMask, uint clearMask)
    {
        currentFlags &= ~clearMask;
        currentFlags |= setMask;
        return currentFlags;
    }

    public static uint MapMountAttrToMountFlags(ulong attrMask)
    {
        uint flags = 0;
        const ulong MOUNT_ATTR_RDONLY = 0x00000001;
        const ulong MOUNT_ATTR_NOSUID = 0x00000002;
        const ulong MOUNT_ATTR_NODEV = 0x00000004;
        const ulong MOUNT_ATTR_NOEXEC = 0x00000008;
        if ((attrMask & MOUNT_ATTR_RDONLY) != 0) flags |= LinuxConstants.MS_RDONLY;
        if ((attrMask & MOUNT_ATTR_NOSUID) != 0) flags |= LinuxConstants.MS_NOSUID;
        if ((attrMask & MOUNT_ATTR_NODEV) != 0) flags |= LinuxConstants.MS_NODEV;
        if ((attrMask & MOUNT_ATTR_NOEXEC) != 0) flags |= LinuxConstants.MS_NOEXEC;
        return flags;
    }

    public static void RefreshMountOptions(Mount mount)
    {
        mount.Options = BuildMountOptions(mount.Flags);
    }

    public static uint ParseMountFlagsFromOptions(string? options)
    {
        if (string.IsNullOrWhiteSpace(options)) return 0;
        uint flags = 0;
        var tokens = options.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
            switch (token)
            {
                case "ro":
                    flags |= LinuxConstants.MS_RDONLY;
                    break;
                case "rw":
                    flags &= ~LinuxConstants.MS_RDONLY;
                    break;
                case "nosuid":
                    flags |= LinuxConstants.MS_NOSUID;
                    break;
                case "nodev":
                    flags |= LinuxConstants.MS_NODEV;
                    break;
                case "noexec":
                    flags |= LinuxConstants.MS_NOEXEC;
                    break;
            }

        return flags;
    }

    public int CreateDetachedMountFromFsContext(FsContextFile fsCtx, uint mountAttrs, out Mount? detachedMount,
        int readSuperFlags = 0)
    {
        detachedMount = null;
        var rc = ResolveFsContextMountPlan(fsCtx, mountAttrs, out var sb, out var source, out var fsType, out var flags,
            out var extraOptions, readSuperFlags);
        if (rc != 0) return rc;
        detachedMount = CreateDetachedMount(sb!, source!, fsType!, flags, extraOptions);
        return 0;
    }

    public int ResolveFsContextMountPlan(FsContextFile fsCtx, uint mountAttrs, out SuperBlock? sb, out string? source,
        out string? fsTypeName, out uint flags, out string? extraOptions, int readSuperFlags = 0)
    {
        sb = null;
        source = null;
        fsTypeName = null;
        flags = 0;
        extraOptions = null;

        var fsType = FileSystemRegistry.Get(fsCtx.FsType);
        if (fsType == null) return -(int)Errno.ENODEV;

        extraOptions = fsCtx.BuildMountDataString();
        source = string.IsNullOrEmpty(fsCtx.Source) ? fsCtx.FsType : fsCtx.Source;
        fsTypeName = fsCtx.FsType;

        var readSuperDataRc = BuildReadSuperData(fsCtx, extraOptions, out var readSuperData);
        if (readSuperDataRc != 0) return readSuperDataRc;

        try
        {
            sb = fsType.CreateFileSystem(DeviceNumbers).ReadSuper(fsType, readSuperFlags, source!, readSuperData);
            if (sb != null)
                sb.MemoryContext = CurrentSyscallEngine.MemoryContext;
        }
        catch
        {
            return -(int)Errno.EINVAL;
        }

        if (fsCtx.FlagOptions.Contains("ro")) flags |= LinuxConstants.MS_RDONLY;
        if (fsCtx.FlagOptions.Contains("nosuid")) flags |= LinuxConstants.MS_NOSUID;
        if (fsCtx.FlagOptions.Contains("nodev")) flags |= LinuxConstants.MS_NODEV;
        if (fsCtx.FlagOptions.Contains("noexec")) flags |= LinuxConstants.MS_NOEXEC;

        flags |= MapMountAttrToMountFlags(mountAttrs);
        return 0;
    }

    public FsContextFile BuildFsContextFromLegacyMount(string fsType, string source, uint flags, string? dataString)
    {
        var fsCtx = new FsContextFile(AnonMount.Root, AnonMount, fsType);
        if (!string.IsNullOrEmpty(source)) fsCtx.SetString("source", source);

        if ((flags & LinuxConstants.MS_RDONLY) != 0) fsCtx.SetFlag("ro");
        else fsCtx.SetFlag("rw");
        if ((flags & LinuxConstants.MS_NOSUID) != 0) fsCtx.SetFlag("nosuid");
        if ((flags & LinuxConstants.MS_NODEV) != 0) fsCtx.SetFlag("nodev");
        if ((flags & LinuxConstants.MS_NOEXEC) != 0) fsCtx.SetFlag("noexec");

        if (!string.IsNullOrWhiteSpace(dataString))
        {
            var tokens = dataString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var token in tokens)
            {
                var eq = token.IndexOf('=');
                if (eq <= 0)
                {
                    fsCtx.SetFlag(token);
                    continue;
                }

                var key = token[..eq];
                var value = token[(eq + 1)..];
                if (!string.IsNullOrEmpty(key))
                    fsCtx.SetString(key, value);
            }
        }

        fsCtx.State = FsContextState.Created;
        return fsCtx;
    }

    private int BuildReadSuperData(FsContextFile fsCtx, string? extraOptions, out object? data)
    {
        data = extraOptions;
        if (!string.Equals(fsCtx.FsType, "overlay", StringComparison.Ordinal))
            return 0;

        return BuildOverlayMountOptions(extraOptions, out data);
    }

    private int BuildOverlayMountOptions(string? optionString, out object? data)
    {
        data = null;
        if (string.IsNullOrWhiteSpace(optionString))
            return -(int)Errno.EINVAL;

        string? lowerdir = null;
        string? upperdir = null;
        var tokens = optionString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
        {
            var eq = token.IndexOf('=');
            if (eq <= 0 || eq == token.Length - 1) continue;
            var key = token[..eq];
            var value = token[(eq + 1)..];
            if (string.Equals(key, "lowerdir", StringComparison.Ordinal))
                lowerdir = value;
            else if (string.Equals(key, "upperdir", StringComparison.Ordinal))
                upperdir = value;
        }

        if (string.IsNullOrEmpty(lowerdir) || string.IsNullOrEmpty(upperdir))
            return -(int)Errno.EINVAL;

        var lowerRoots = new List<Dentry>();
        var lowerSbs = new List<SuperBlock>();
        var lowerParts = lowerdir.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var lowerPath in lowerParts)
        {
            var lowerLoc = PathWalkWithFlags(lowerPath, LookupFlags.FollowSymlink);
            if (!lowerLoc.IsValid || lowerLoc.Dentry?.Inode?.Type != InodeType.Directory || lowerLoc.Mount == null)
                return -(int)Errno.EINVAL;
            lowerRoots.Add(lowerLoc.Dentry);
            lowerSbs.Add(lowerLoc.Mount.SB);
        }

        var upperLoc = PathWalkWithFlags(upperdir, LookupFlags.FollowSymlink);
        if (!upperLoc.IsValid || upperLoc.Dentry?.Inode?.Type != InodeType.Directory || upperLoc.Mount == null)
            return -(int)Errno.EINVAL;

        data = new OverlayMountOptions
        {
            LowerRoots = lowerRoots,
            Lowers = lowerSbs,
            UpperRoot = upperLoc.Dentry,
            Upper = upperLoc.Mount.SB
        };
        return 0;
    }

    public int AttachDetachedMount(Mount mount, PathLocation toLoc)
    {
        if (!toLoc.IsValid || toLoc.Dentry?.Inode == null || toLoc.Mount == null) return -(int)Errno.ENOENT;
        if (toLoc.Dentry.IsMounted) return -(int)Errno.EBUSY;

        var toDentry = toLoc.Dentry;
        var toMount = toLoc.Mount;

        var isTargetDir = toDentry.Inode.Type == InodeType.Directory;
        var isSourceDir = mount.Root.Inode?.Type == InodeType.Directory;
        if (isTargetDir && !isSourceDir) return -(int)Errno.ENOTDIR;
        if (!isTargetDir && isSourceDir) return -(int)Errno.ENOTDIR;

        RegisterMount(mount, toMount, toDentry);
        return 0;
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
                devRoot.CacheChild(dentry, "SyscallManager.InitStdio.RegisterDev");
        }

        // Create /dev/null
        var nullInode = new NullInode(devSb);
        nullInode.Rdev = 0x0103; // Major 1, Minor 3
        var nullDentry = new Dentry("null", nullInode, devRoot, devSb);
        RegisterDev("null", nullDentry);

        // Create /dev/ptmx (PTY multiplexer)
        var signalBroadcaster = new SignalBroadcasterImpl();
        var ptmxInode = new PtmxInode(devSb, PtyManager, signalBroadcaster, Logger);
        ptmxInode.Rdev = PtyManager.GetPtmxRdev();
        var ptmxDentry = new Dentry("ptmx", ptmxInode, devRoot, devSb);
        RegisterDev("ptmx", ptmxDentry);

        // Create /dev/tty (dynamic controlling terminal for the calling process)
        var ttyInode = new ControllingTtyInode(devSb);
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
        engine.PageFaultResolver ??= (addr, isWrite) => Mem.HandleFault(addr, isWrite, engine);
        ProcessAddressSpaceSync.RegisterEngineAddressSpace(Mem, engine);
    }

    public void UnregisterEngine(Engine engine)
    {
        ProcessAddressSpaceSync.UnregisterEngineAddressSpace(Mem, engine);
    }

    public SyscallManager Clone(VMAManager newMem, bool shareFiles, bool shareFs)
    {
        SharedFdTable newSharedFdTable;
        if (shareFiles)
        {
            newSharedFdTable = _sharedFdTable.AddRef();
        }
        else
        {
            newSharedFdTable = new SharedFdTable();
            foreach (var kv in FDs)
            {
                // fork/clone (without CLONE_FILES) should duplicate fd table entries,
                // but still reference the same open file description.
                kv.Value.Get();
                newSharedFdTable.Fds[kv.Key] = kv.Value;
            }

            foreach (var fd in FdCloseOnExecSet)
                newSharedFdTable.CloseOnExec.Add(fd);
        }

        // Share the mount namespace (mounts are shared across fork/clone by default)
        var sharedNamespace = _mountNamespace.Share();
        var sharedFsState = shareFs ? _sharedFsState.AddRef() : _sharedFsState.CloneIsolated("clone");

        var newSys = new SyscallManager(newMem, newSharedFdTable, _sharedUnixSocketNamespace.AddRef(), sharedFsState,
            Futex, SysVShm, SysVSem, BrkAddr, BrkBase,
            Strace, DevShmRoot, MemfdSuperBlock, AnonMount, Tty, PtyManager,
            sharedNamespace, _privateNetNamespace?.AddRef(), DeviceNumbers)
        {
            NetworkMode = NetworkMode,
            CloneHandler = CloneHandler,
            ExitHandler = ExitHandler,
            GetTID = GetTID,
            GetTGID = GetTGID
        };
        return newSys;
    }

    private void Register(uint nr, SyscallHandler handler)
    {
        if (nr < MaxSyscalls) _syscallHandlers[nr] = handler;
    }

    internal static int MapSyscallExceptionToErrno(Exception ex)
    {
        return ex switch
        {
            OutOfMemoryException => -(int)Errno.ENOMEM,
            PlatformNotSupportedException => -(int)Errno.ENOSYS,
            NotImplementedException => -(int)Errno.ENOSYS,
            UnauthorizedAccessException => -(int)Errno.EPERM,
            ArgumentException => -(int)Errno.EINVAL,
            IOException => -(int)Errno.EIO,
            _ => -(int)Errno.EFAULT
        };
    }

    public bool Handle(Engine engine, uint vector)
    {
        var previous = engine.CurrentSyscallManager;
        engine.CurrentSyscallManager = this;
        try
        {
            // Handle Breakpoint (INT 3)
            if (vector == 3)
            {
                if (engine.Owner is FiberTask t) t.PostSignal((int)Signal.SIGTRAP);
                return true;
            }

            if (vector != 0x80) return false;

            // Update current engine context (GIL ensures safety)
            CurrentSyscallEngine = engine;
            GlobalAddressSpaceCacheManager.MaybeRunMaintenance(Mem, engine);

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
                try
                {
                    retTask = _syscallHandlers[eax]!(engine, ebx, ecx, edx, esi, edi, ebp);
                }
                catch (Exception ex)
                {
                    var ret = MapSyscallExceptionToErrno(ex);
                    Logger.LogError(ex, "Syscall handler threw before returning task. nr={Nr} tid={Tid} ret={Ret}",
                        eax, fiberTask?.TID ?? 0, ret);
                    engine.RegWrite(Reg.EAX, unchecked((uint)ret));
                    if (Strace)
                        SyscallTracer.TraceExit(Logger, this, fiberTask?.TID ?? 0, eax, ret, ebx, ecx, edx);
                    return true;
                }
            else if (!Strace) Logger.LogWarning("Unimplemented Syscall: {Eax}", eax);

            // --- Handling Async Syscalls ---
            int completedRet;
            if (retTask.IsCompleted)
                try
                {
                    completedRet = retTask.Result;
                }
                catch (Exception ex)
                {
                    var ret = MapSyscallExceptionToErrno(ex);
                    Logger.LogError(ex, "Syscall task completed with exception. nr={Nr} tid={Tid} ret={Ret}",
                        eax, fiberTask?.TID ?? 0, ret);
                    engine.RegWrite(Reg.EAX, unchecked((uint)ret));
                    if (Strace)
                        SyscallTracer.TraceExit(Logger, this, fiberTask?.TID ?? 0, eax, ret, ebx, ecx, edx);
                    return true;
                }
            else
                completedRet = 0;

            if (retTask.IsCompleted && completedRet != -(int)Errno.ERESTARTSYS)
            {
                var ret = completedRet;

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
                    Logger.LogTrace(
                        "[Syscall] Async suspend TID={Tid} NR={Nr} IsCompleted={IsCompleted} AwaitRestart={AwaitRestart}",
                        fiberTask.TID, eax, retTask.IsCompleted,
                        retTask.IsCompleted && retTask.Result == -(int)Errno.ERESTARTSYS);

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
                if ((fiberTask.GetVisiblePendingSignals() & ~fiberTask.SignalMask) != 0) shouldYield = true;

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
            engine.CurrentSyscallManager = previous;
        }
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;

        if (CurrentSyscallEngine is not null)
            UnregisterEngine(CurrentSyscallEngine);

        // Release explicit container-owned mount pins (e.g. resolv.conf detached mount).
        ReleaseContainerPins();
        _privateNetNamespace?.Release();
        _privateNetNamespace = null;

        // Release this process' reference to the mount namespace.
        // Mount detach/unmount is controlled by umount(2), not by process exit.
        _mountNamespace.Put();

        _sharedFsState.Release("close");

        if (_sharedFdTable.ReleaseRef())
        {
            foreach (var fd in FDs.Values)
                fd.Close();
            FDs.Clear();
            FdCloseOnExecSet.Clear();
        }

        if (_sharedUnixSocketNamespace.ReleaseRef())
            _sharedUnixSocketNamespace.Clear();
    }

    internal bool TryBindUnixPathSocket(Inode pathInode, UnixSocketInode inode)
    {
        return _sharedUnixSocketNamespace.TryBindPath(pathInode, inode);
    }

    internal bool TryBindUnixAbstractSocket(string key, UnixSocketInode inode)
    {
        return _sharedUnixSocketNamespace.TryBindAbstract(key, inode);
    }

    internal UnixSocketInode? LookupUnixPathSocket(Inode pathInode)
    {
        return _sharedUnixSocketNamespace.LookupPath(pathInode);
    }

    internal UnixSocketInode? LookupUnixAbstractSocket(string key)
    {
        return _sharedUnixSocketNamespace.LookupAbstract(key);
    }

    internal void UnbindUnixSocket(UnixSocketInode inode)
    {
        _sharedUnixSocketNamespace.Unbind(inode);
    }

    public sealed class RootMountOptions
    {
        public string Source { get; init; } = "none";
        public string FsType { get; init; } = "none";
        public string Options { get; init; } = "rw";
        public uint? Flags { get; init; }
        public Dentry? Root { get; init; }
    }

    private sealed class SharedFdTable
    {
        private int _refCount = 1;

        public Dictionary<int, LinuxFile> Fds { get; } = [];
        public HashSet<int> CloseOnExec { get; } = [];

        public SharedFdTable AddRef()
        {
            Interlocked.Increment(ref _refCount);
            return this;
        }

        public bool ReleaseRef()
        {
            return Interlocked.Decrement(ref _refCount) == 0;
        }
    }

    private sealed class SharedUnixSocketNamespace
    {
        private readonly Dictionary<UnixSocketInode, string> _abstractKeysBySocket = [];
        private readonly Dictionary<string, UnixSocketInode> _abstractSockets = new(StringComparer.Ordinal);
        private readonly Dictionary<UnixSocketInode, (uint Dev, ulong Ino)> _pathInodesBySocket = [];
        private readonly Dictionary<(uint Dev, ulong Ino), UnixSocketInode> _pathSocketsByInode = [];
        private int _refCount = 1;

        private NamespaceScope EnterNamespaceScope([CallerMemberName] string? caller = null)
        {
            return default;
        }

        public SharedUnixSocketNamespace AddRef()
        {
            Interlocked.Increment(ref _refCount);
            return this;
        }

        public bool ReleaseRef()
        {
            return Interlocked.Decrement(ref _refCount) == 0;
        }

        public bool TryBindPath(Inode pathInode, UnixSocketInode socketInode)
        {
            using (EnterNamespaceScope())
            {
                var key = (pathInode.Dev, pathInode.Ino);
                if (_pathSocketsByInode.ContainsKey(key)) return false;
                if (_pathInodesBySocket.ContainsKey(socketInode)) return false;
                _pathSocketsByInode[key] = socketInode;
                _pathInodesBySocket[socketInode] = key;
                return true;
            }
        }

        public bool TryBindAbstract(string key, UnixSocketInode socketInode)
        {
            using (EnterNamespaceScope())
            {
                if (_abstractSockets.ContainsKey(key)) return false;
                if (_abstractKeysBySocket.ContainsKey(socketInode)) return false;
                _abstractSockets[key] = socketInode;
                _abstractKeysBySocket[socketInode] = key;
                return true;
            }
        }

        public UnixSocketInode? LookupPath(Inode pathInode)
        {
            using (EnterNamespaceScope())
            {
                return _pathSocketsByInode.GetValueOrDefault((pathInode.Dev, pathInode.Ino));
            }
        }

        public UnixSocketInode? LookupAbstract(string key)
        {
            using (EnterNamespaceScope())
            {
                return _abstractSockets.GetValueOrDefault(key);
            }
        }

        public void Unbind(UnixSocketInode inode)
        {
            using (EnterNamespaceScope())
            {
                if (_pathInodesBySocket.Remove(inode, out var key))
                    _pathSocketsByInode.Remove(key);

                if (_abstractKeysBySocket.Remove(inode, out var abstractKey))
                    _abstractSockets.Remove(abstractKey);
            }
        }

        public void Clear()
        {
            using (EnterNamespaceScope())
            {
                _pathSocketsByInode.Clear();
                _pathInodesBySocket.Clear();
                _abstractSockets.Clear();
                _abstractKeysBySocket.Clear();
            }
        }

        private readonly struct NamespaceScope : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private sealed class SharedFsState
    {
        private int _refCount = 1;

        public PathLocation Root { get; private set; } = PathLocation.None;
        public PathLocation CurrentWorkingDirectory { get; private set; } = PathLocation.None;
        public PathLocation ProcessRoot { get; private set; } = PathLocation.None;

        public SharedFsState AddRef()
        {
            Interlocked.Increment(ref _refCount);
            return this;
        }

        public void Release(string reason)
        {
            if (Interlocked.Decrement(ref _refCount) > 0)
                return;

            UnpinPathLocation(Root, $"root:{reason}");
            UnpinPathLocation(CurrentWorkingDirectory, $"cwd:{reason}");
            UnpinPathLocation(ProcessRoot, $"procroot:{reason}");
            Root = PathLocation.None;
            CurrentWorkingDirectory = PathLocation.None;
            ProcessRoot = PathLocation.None;
        }

        public SharedFsState CloneIsolated(string reason)
        {
            var clone = new SharedFsState();
            clone.ReplaceAll(Root, CurrentWorkingDirectory, ProcessRoot, reason);
            return clone;
        }

        public void ReplaceAll(PathLocation root, PathLocation cwd, PathLocation procRoot, string reason)
        {
            var oldRoot = Root;
            var oldCwd = CurrentWorkingDirectory;
            var oldProcRoot = ProcessRoot;

            Root = root;
            CurrentWorkingDirectory = cwd;
            ProcessRoot = procRoot;

            PinPathLocation(Root, $"root:{reason}");
            PinPathLocation(CurrentWorkingDirectory, $"cwd:{reason}");
            PinPathLocation(ProcessRoot, $"procroot:{reason}");

            UnpinPathLocation(oldRoot, $"root:{reason}-old");
            UnpinPathLocation(oldCwd, $"cwd:{reason}-old");
            UnpinPathLocation(oldProcRoot, $"procroot:{reason}-old");
        }

        public void UpdateCurrentWorkingDirectory(PathLocation next, string reason)
        {
            var old = CurrentWorkingDirectory;
            CurrentWorkingDirectory = next;
            PinPathLocation(next, $"cwd:{reason}");
            UnpinPathLocation(old, $"cwd:{reason}");
        }

        public void UpdateProcessRoot(PathLocation next, string reason)
        {
            var old = ProcessRoot;
            ProcessRoot = next;
            PinPathLocation(next, $"procroot:{reason}");
            UnpinPathLocation(old, $"procroot:{reason}");
        }
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
    public void SignalProcessGroup(FiberTask? task, int pgid, int signal)
    {
        var scheduler = ResolveScheduler(task);
        if (scheduler == null) return;

        if (scheduler.IsSchedulerThread)
            scheduler.SignalProcessGroup(pgid, signal);
        else
            scheduler.SignalProcessGroupFromAnyThread(pgid, signal);
    }

    public void SignalForegroundTask(FiberTask? task, int signal)
    {
        var scheduler = ResolveScheduler(task);
        if (scheduler == null) return;

        void Deliver()
        {
            var targetTask = task ?? scheduler.CurrentTask;
            targetTask?.PostSignal(signal);
        }

        if (scheduler.IsSchedulerThread)
            Deliver();
        else
            scheduler.RunIngress(Deliver, task);
    }

    private static KernelScheduler? ResolveScheduler(FiberTask? task)
    {
        if (task != null)
            return task.CommonKernel;

        return SynchronizationContext.Current is KernelSyncContext context
            ? context.Scheduler
            : null;
    }
}
