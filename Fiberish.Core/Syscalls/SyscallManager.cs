using Bifrost.Core;
using Bifrost.Memory;
using Bifrost.Native;
using Bifrost.VFS;
using Microsoft.Extensions.Logging;
using Bifrost.Diagnostics;

namespace Bifrost.Syscalls;

public partial class SyscallManager
{
    private static readonly ILogger Logger = Logging.CreateLogger<SyscallManager>();
    private static readonly Dictionary<IntPtr, SyscallManager> _registry = [];
    private static readonly object _registryLock = new();

    // The current engine executing a syscall (protected by GIL)
    public Engine Engine { get; set; } = null!;

    public VMAManager Mem { get; set; }

    public Dentry Root { get; set; } = null!;
    public Dentry CurrentWorkingDirectory { get; set; } = null!; // Renamed to avoid confusion with string Cwd

    // For chroot tracking, we keep a Dentry pointer to the process root
    public Dentry ProcessRoot { get; set; } = null!;

    // File Descriptors (Shared if CLONE_FILES)
    public Dictionary<int, Bifrost.VFS.File> FDs { get; private set; } = [];

    public FutexManager Futex { get; private set; }

    public uint BrkAddr { get; set; }
    public bool Strace { get; set; }

    public class MountInfo
    {
        public string Source { get; set; } = "none";
        public string Target { get; set; } = "/";
        public string FsType { get; set; } = "unknown";
        public string Options { get; set; } = "";
    }
    public List<MountInfo> MountList { get; } = [];

    public delegate ValueTask<int> SyscallHandler(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6);
    private readonly SyscallHandler?[] _syscallHandlers = new SyscallHandler?[MaxSyscalls];
    private const int MaxSyscalls = 512;

    public SyscallManager(Engine engine, VMAManager mem, uint brk, string hostRoot)
    {
        Engine = engine;
        Mem = mem;
        BrkAddr = brk;
        Futex = new FutexManager();

        RegisterEngine(engine);
        RegisterHandlers();

        // 1. Initialize Registry and Register Filesystems
        // 1. Initialize Registry and Register Filesystems
        FileSystemRegistry.TryRegister(new FileSystemType { Name = "hostfs", FileSystem = new Hostfs() });
        FileSystemRegistry.TryRegister(new FileSystemType { Name = "tmpfs", FileSystem = new Tmpfs() });
        FileSystemRegistry.TryRegister(new FileSystemType { Name = "devtmpfs", FileSystem = new Tmpfs() });
        FileSystemRegistry.TryRegister(new FileSystemType { Name = "overlay", FileSystem = new OverlayFileSystem() });
        FileSystemRegistry.TryRegister(new FileSystemType { Name = "proc", FileSystem = new Tmpfs() });

        // 2. Setup Rootfs (OverlayFS: Lower=Hostfs, Upper=Tmpfs)
        var hostFsType = FileSystemRegistry.Get("hostfs")!;
        var tmpFsType = FileSystemRegistry.Get("tmpfs")!;
        var overlayFsType = FileSystemRegistry.Get("overlay")!;

        // Lower: Hostfs (Read-Only access to hostRoot)
        var lowerSb = hostFsType.FileSystem.ReadSuper(hostFsType, 0, hostRoot, null);

        // Upper: Tmpfs (Read-Write layer)
        var upperSb = tmpFsType.FileSystem.ReadSuper(tmpFsType, 0, "overlay_upper", null);

        // Overlay: Combine them
        var options = new OverlayMountOptions { Lower = lowerSb, Upper = upperSb };
        var overlaySb = overlayFsType.FileSystem.ReadSuper(overlayFsType, 0, "root_overlay", options);

        Root = overlaySb.Root;
        MountList.Add(new MountInfo { Source = "overlay", Target = "/", FsType = "overlay", Options = "rw,relatime,lowerdir=/,upperdir=/overlay_upper,workdir=/work" });

        ProcessRoot = Root;
        CurrentWorkingDirectory = Root;

        Root.Inode!.Get();
        ProcessRoot.Inode!.Get();
        CurrentWorkingDirectory.Inode!.Get();

        // 3. Mount /dev and /proc
        // Ensure /dev and /proc exist in the overlay (will be created in Upper if missing)
        EnsureDirectory(Root, "dev");
        EnsureDirectory(Root, "proc");
        EnsureDirectory(Root, "tmp");

        // Mount devtmpfs to /dev
        var devFsType = FileSystemRegistry.Get("devtmpfs")!;
        var devSb = devFsType.FileSystem.ReadSuper(devFsType, 0, "dev", null);
        Mount(Root, "dev", devSb, "devtmpfs", "devtmpfs", "rw,relatime");

        // Mount procfs to /proc
        var procFsType = FileSystemRegistry.Get("proc")!;
        var procSb = procFsType.FileSystem.ReadSuper(procFsType, 0, "proc", null);
        Mount(Root, "proc", procSb, "proc", "proc", "rw,relatime");

        // Add console FDs
        InitStdio(devSb);

        SetupVDSO();
    }

    public uint SigReturnAddr { get; private set; }
    public uint RtSigReturnAddr { get; private set; }

    private void SetupVDSO()
    {
        // Map vDSO page (RX) at a fixed high address to avoid overlap
        uint vdsoAddr = 0x7FFF0000;
        Mem.Mmap(vdsoAddr, 4096, Protection.Read | Protection.Exec, MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, 0, "[vdso]", Engine);

        // Directly allocate the page in the engine with RW permissions for initial setup
        if (Engine.AllocatePage(vdsoAddr, (byte)(Protection.Read | Protection.Write)) == IntPtr.Zero)
            throw new Exception("Failed to allocate vDSO page");

        // Write trampolines
        // __kernel_sigreturn: pop eax; mov eax, 119; int 0x80
        byte[] sigret = [0x58, 0xB8, 0x77, 0x00, 0x00, 0x00, 0xCD, 0x80];
        if (!Engine.CopyToUser(vdsoAddr, sigret)) Logger.LogError("Failed to write sigreturn trampoline to vDSO");
        SigReturnAddr = vdsoAddr;

        // __kernel_rt_sigreturn: mov eax, 173; int 0x80
        byte[] rtsigret = [0xB8, 0xAD, 0x00, 0x00, 0x00, 0xCD, 0x80];
        if (!Engine.CopyToUser(vdsoAddr + 16, rtsigret)) Logger.LogError("Failed to write rt_sigreturn trampoline to vDSO");
        RtSigReturnAddr = vdsoAddr + 16;

        // Set final RX permissions in the engine
        Engine.MemMap(vdsoAddr, 4096, (byte)(Protection.Read | Protection.Exec));

        Logger.LogInformation("vDSO mapped at 0x{Addr:x}, sigreturn=0x{S:x}, rt_sigreturn=0x{R:x}", vdsoAddr, SigReturnAddr, RtSigReturnAddr);
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

    private void Mount(Dentry parent, string name, SuperBlock sb, string source, string fstype, string options)
    {
        var dentry = parent.Inode!.Lookup(name) ?? throw new Exception($"Mount point {name} not found");

        // Mount it
        dentry.IsMounted = true;
        dentry.MountRoot = sb.Root;
        sb.Root.MountedAt = dentry;

        // Track mount
        string targetPath = "/" + name; // Simplified for root mounts
        MountList.Add(new MountInfo { Source = source, Target = targetPath, FsType = fstype, Options = options });
    }

    private void InitStdio(SuperBlock devSb)
    {
        // 0: Stdin, 1: Stdout, 2: Stderr
        // In devtmpfs, we should create /dev/console, /dev/null, /dev/tty etc.
        // For simple Stdio, we just create virtual inodes or reuse what we had.

        var nullNode = new ConsoleInode(devSb, true); // reusing ConsoleInode logic for now
                                                      // Ideally we create nodes in devSb using mknod if we had it, or manual instantiation.

        // We need Dentry for these to support Fstat correctly if they are fully virtual.
        // Or we use the manually created ones.

        var stdinInode = new ConsoleInode(devSb, true);
        var stdinDentry = new Dentry("stdin", stdinInode, devSb.Root, devSb); // Parent should be /dev root really
        FDs[0] = new Bifrost.VFS.File(stdinDentry, FileFlags.O_RDONLY);

        var stdoutInode = new ConsoleInode(devSb, false);
        var stdoutDentry = new Dentry("stdout", stdoutInode, devSb.Root, devSb);
        FDs[1] = new Bifrost.VFS.File(stdoutDentry, FileFlags.O_WRONLY);
        FDs[2] = new Bifrost.VFS.File(stdoutDentry, FileFlags.O_WRONLY);
    }

    private SyscallManager(VMAManager mem, Dictionary<int, Bifrost.VFS.File> fds, FutexManager futex, uint brk, bool strace, Dentry root, Dentry cwd, Dentry procRoot)
    {
        Mem = mem;
        FDs = fds;
        Futex = futex;
        BrkAddr = brk;
        Strace = strace;
        Root = root; // Global root (shared)
        CurrentWorkingDirectory = cwd;
        ProcessRoot = procRoot;

        Root.Inode!.Get();
        CurrentWorkingDirectory.Inode!.Get();
        ProcessRoot.Inode!.Get();

        RegisterHandlers();
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
        Dictionary<int, Bifrost.VFS.File> newFds;
        if (shareFiles)
        {
            newFds = FDs;
        }
        else
        {
            newFds = [];
            foreach (var kv in FDs)
            {
                // We need to manually clone because File's constructor/Close manage refcounts
                newFds[kv.Key] = new Bifrost.VFS.File(kv.Value.Dentry, kv.Value.Flags) { Position = kv.Value.Position, PrivateData = kv.Value.PrivateData };
            }
        }

        var newSys = new SyscallManager(newMem, newFds, Futex, BrkAddr, Strace, Root, CurrentWorkingDirectory, ProcessRoot)
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
        if (nr < MaxSyscalls)
        {
            _syscallHandlers[nr] = handler;
        }
    }

    public bool Handle(Engine engine, uint vector)
    {
        // Handle Breakpoint (INT 3)
        if (vector == 3)
        {
            if (engine.Owner is FiberTask t)
            {
                t.PendingSignals |= (1UL << (5 - 1)); // SIGTRAP = 5
            }
            return true;
        }

        if (vector != 0x80) return false;

        // Update current engine context (GIL ensures safety)
        Engine = engine;

        // Get current FiberTask (New Model Only) via Engine.Owner
        var fiberTask = engine.Owner as FiberTask;

        uint eax = engine.RegRead(Reg.EAX);
        uint ebx = engine.RegRead(Reg.EBX);
        uint ecx = engine.RegRead(Reg.ECX);
        uint edx = engine.RegRead(Reg.EDX);
        uint esi = engine.RegRead(Reg.ESI);
        uint edi = engine.RegRead(Reg.EDI);
        uint ebp = engine.RegRead(Reg.EBP);

        if (Strace)
        {
            Logger.LogTrace("[Syscall] {eax} ({ebx:X}, {ecx:X}, {edx:X}, {esi:X}, {edi:X}, {ebp:X})",
                eax, ebx, ecx, edx, esi, edi, ebp);
        }

        ValueTask<int> retTask = new(-38); // ENOSYS

        if (eax < MaxSyscalls && _syscallHandlers[eax] != null)
        {
            retTask = _syscallHandlers[eax]!(engine.State, ebx, ecx, edx, esi, edi, ebp);
        }
        else if (!Strace)
        {
            Logger.LogWarning("Unimplemented Syscall: {Eax}", eax);
        }

        // --- Handling Async Syscalls ---
        if (retTask.IsCompleted)
        {
            int ret = retTask.Result;

            // Special handling for context-restoring syscalls
            bool isSigReturn = (eax == X86SyscallNumbers.rt_sigreturn || eax == X86SyscallNumbers.sigreturn);


            if (!isSigReturn)
            {
                engine.RegWrite(Reg.EAX, (uint)ret);
            }

            if (Strace)
            {
                Logger.LogTrace(" = {Ret}", ret);
            }
        }
        else
        {
            // Async completion (Blocking)
            if (fiberTask != null)
            {
                // Suspend the task
                fiberTask.PendingSyscall = () => retTask;
                fiberTask.Status = FiberTaskStatus.Waiting;

                // Tracing
                if (Strace) Logger.LogTrace(" [Suspended]");

                // Force yield
                engine.Yield();
                return true;
            }
            else
            {
                // Should not happen in new model
                Logger.LogError("Async syscall initiated but no FiberTask attached!");
                engine.RegWrite(Reg.EAX, unchecked((uint)-(int)Errno.ENOSYS));
            }
        }

        // Determine if we should yield
        bool shouldYield = false;

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

        if (shouldYield)
        {
            engine.Yield();
        }

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
        {
            // Note: If shareFiles is true, this might be dangerous if multiple tasks close the same FDs
            // But usually SyscallManager.Close is called when the task/process actually dies.
            fd.Close();
        }
        FDs.Clear();
    }
}
