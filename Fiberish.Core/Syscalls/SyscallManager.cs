using Fiberish.Core;
using Fiberish.Core.VFS.TTY;
using Fiberish.Diagnostics;
using Fiberish.Core.Net;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.VFS;
using Fiberish.X86.Native;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Threading;

namespace Fiberish.Syscalls;

public partial class SyscallManager
{
    public sealed class RootMountOptions
    {
        public string Source { get; init; } = "none";
        public string FsType { get; init; } = "none";
        public string Options { get; init; } = "rw";
        public uint? Flags { get; init; }
        public Dentry? Root { get; init; }
    }

    public const uint MountFlagMask = LinuxConstants.MS_RDONLY | LinuxConstants.MS_NOSUID |
                                      LinuxConstants.MS_NODEV | LinuxConstants.MS_NOEXEC;

    public delegate ValueTask<int> SyscallHandler(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6);

    private const int MaxSyscalls = 512;
    private static readonly ILogger Logger = Logging.CreateLogger<SyscallManager>();
    private static readonly Dictionary<IntPtr, SyscallManager> _registry = [];
    private static readonly object _registryLock = new();
    private static readonly AsyncLocal<SyscallManager?> _activeSyscallManager = new();
    private readonly SyscallHandler?[] _syscallHandlers = new SyscallHandler?[MaxSyscalls];
    private readonly List<Mount> _containerOwnedMounts = [];
    private readonly FileSystemType _devptsFsType;
    private readonly DeviceNumberManager _devNumberManager = new();
    private SharedLoopbackNetNamespace? _privateNetNamespace;

    internal static SyscallManager? ActiveSyscallManager => _activeSyscallManager.Value;
    internal DeviceNumberManager DeviceNumbers => _devNumberManager;

    public SyscallManager(Engine engine, VMAManager mem, uint brk, TtyDiscipline? tty = null)
    {
        _mountNamespace = new MountNamespace();
        Engine = engine;
        Mem = mem;
        BrkAddr = brk;
        BrkBase = brk;
        Tty = tty;
        Futex = new FutexManager();
        SysVShm = new SysVShmManager(mem.MemoryObjects);
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
        var signalBroadcaster = new SignalBroadcasterImpl(this);
        _devptsFsType = CreateDevPtsFileSystemType(signalBroadcaster);

        // Default memfd superblock
        var tmpFsType = FileSystemRegistry.Get("tmpfs")!;
        MemfdSuperBlock = tmpFsType.CreateFileSystem(_devNumberManager).ReadSuper(tmpFsType, 0, "memfd", null);

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
        Mount anonMount,
        TtyDiscipline? tty,
        PtyManager ptyManager,
        MountNamespace mountNamespace,
        SharedLoopbackNetNamespace? privateNetNamespace)
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
        AnonMount = anonMount;
        Tty = tty;
        PtyManager = ptyManager;

        // Share mount namespace
        _mountNamespace = mountNamespace;
        _devptsFsType = CreateDevPtsFileSystemType(new SignalBroadcasterImpl(this));

        Root.Dentry!.Inode!.Get();
        CurrentWorkingDirectory.Dentry!.Inode!.Get();
        ProcessRoot.Dentry!.Inode!.Get();

        RegisterHandlers();

        _privateNetNamespace = privateNetNamespace;
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
    public Mount AnonMount { get; private set; } = null!;

    // System V Shared Memory (Global IPC namespace)
    public SysVShmManager SysVShm { get; }
    public SysVSemManager SysVSem { get; }

    // File Descriptors (Shared if CLONE_FILES)
    public Dictionary<int, LinuxFile> FDs { get; } = [];

    public FutexManager Futex { get; }

    public uint BrkAddr { get; set; }
    public uint BrkBase { get; }
    public bool Strace { get; set; }
    public NetworkMode NetworkMode { get; set; } = NetworkMode.Host;

    public LoopbackNetNamespace GetOrCreatePrivateNetNamespace()
    {
        return (_privateNetNamespace ??= new SharedLoopbackNetNamespace(LoopbackNetNamespace.Create(0x0A590002u, 24))).Namespace;
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

    /// <summary>
    ///     Mount namespace containing all mounts and lookup hash.
    /// </summary>
    private readonly MountNamespace _mountNamespace;

    private int _closed;

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
        MountRoot(CreateRootMount(sb, options));
    }

    public void MountRootHostfs(string hostPath, string options = "rw,relatime")
    {
        var hostFsType = FileSystemRegistry.Get("hostfs")!;
        var sb = hostFsType.CreateFileSystem(_devNumberManager).ReadSuper(hostFsType, 0, hostPath, options);
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
        var lowerSb = hostFsType.CreateFileSystem(_devNumberManager).ReadSuper(hostFsType, 0, hostRoot, null);
        MountRootOverlayWithLower(lowerSb, upperFsType, upperSource, options);
    }

    public void MountRootOverlayWithLower(SuperBlock lowerSb, string upperFsType, string upperSource,
        string options = "rw,relatime,lowerdir=/,upperdir=/overlay_upper,workdir=/work")
    {
        var upperType = FileSystemRegistry.Get(upperFsType) ??
                        throw new Exception($"Upper filesystem not registered: {upperFsType}");
        var overlayFsType = FileSystemRegistry.Get("overlay")!;
        var upperSb = upperType.CreateFileSystem(_devNumberManager).ReadSuper(upperType, 0, upperSource, null);

        var overlayOptions = new OverlayMountOptions { Lower = lowerSb, Upper = upperSb };
        var overlaySb = overlayFsType.CreateFileSystem(_devNumberManager).ReadSuper(overlayFsType, 0, "root_overlay", overlayOptions);

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
        var devSb = devFsType.CreateFileSystem(_devNumberManager).ReadSuper(devFsType, 0, "dev", null);

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
            var devptsSb = _devptsFsType.CreateFileSystem(_devNumberManager).ReadSuper(_devptsFsType, 0, "devpts", null);
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
            var procSb = procFsType.CreateFileSystem(_devNumberManager).ReadSuper(procFsType, 0, "proc", this);
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
        var shmSb = tmpFsType.CreateFileSystem(_devNumberManager).ReadSuper(tmpFsType, 0, "shm", null);

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

    private static Dentry EnsureFileMountPoint(PathLocation parent, string name)
    {
        var parentDentry = parent.Dentry!;

        if (parentDentry.Children.TryGetValue(name, out var cachedDentry))
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

        parentDentry.Children[name] = dentry;
        return dentry;
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
        if (isDir)
        {
            EnsureDirectory(current, name);
        }
        else if (isFile)
        {
            EnsureFileMountPoint(current, name);
        }
        else
        {
            throw new FileNotFoundException("Host path not found", hostPath);
        }

        var fsCtx = new FsContextFile(AnonMount.Root, AnonMount, "hostfs");
        fsCtx.SetString("source", hostPath);
        if (readOnly)
        {
            fsCtx.SetFlag("ro");
            fsCtx.SetFlag("metadataless");
        }
        else
        {
            fsCtx.SetFlag("rw");
        }

        fsCtx.State = FsContextState.Created;

        var mountRc = CreateDetachedMountFromFsContext(fsCtx, 0, out var detachedMount);
        if (mountRc != 0 || detachedMount == null)
            throw new IOException($"Failed to create detached hostfs mount: rc={mountRc}");

        var mountHandle = new MountFile(detachedMount);
        try
        {
            var targetLoc = new PathLocation(current.Dentry!.Children[name], current.Mount);
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
        for (var i = 0; i < parts.Length - 1; i++)
        {
            current = EnsureDirectory(current, parts[i]);
        }

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
            var targetLoc = new PathLocation(current.Dentry!.Children[name], current.Mount);
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
        {
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
            sb = fsType.CreateFileSystem(_devNumberManager).ReadSuper(fsType, readSuperFlags, source!, readSuperData);
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
            ProcessRoot, DevShmRoot, MemfdSuperBlock, AnonMount, Tty, PtyManager,
            sharedNamespace, _privateNetNamespace?.AddRef())
        {
            NetworkMode = NetworkMode,
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

    private static int MapSyscallExceptionToErrno(Exception ex)
    {
        return ex switch
        {
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
            GlobalPageCacheManager.MaybeRunMaintenance(Mem, engine);

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
            {
                try
                {
                    retTask = _syscallHandlers[eax]!(engine.State, ebx, ecx, edx, esi, edi, ebp);
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
            }
            else if (!Strace) Logger.LogWarning("Unimplemented Syscall: {Eax}", eax);

            // --- Handling Async Syscalls ---
            int completedRet;
            if (retTask.IsCompleted)
            {
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
            }
            else
            {
                completedRet = 0;
            }

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
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;

        lock (_registryLock)
        {
            if (Engine != null)
                _registry.Remove(Engine.State);
        }

        // Release explicit container-owned mount pins (e.g. resolv.conf detached mount).
        ReleaseContainerPins();
        _privateNetNamespace?.Release();
        _privateNetNamespace = null;

        // Release this process' reference to the mount namespace.
        // Mount detach/unmount is controlled by umount(2), not by process exit.
        _mountNamespace.Put();

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
