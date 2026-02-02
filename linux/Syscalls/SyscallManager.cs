using System.Runtime.InteropServices;
using Bifrost.Core;
using Bifrost.Memory;
using Bifrost.Native;
using Bifrost.VFS;

namespace Bifrost.Syscalls;

public unsafe partial class SyscallManager
{
    private static readonly Dictionary<IntPtr, SyscallManager> _registry = new();
    private static readonly object _registryLock = new();

    // The current engine executing a syscall (protected by GIL)
    public Engine Engine { get; set; } = null!;
    
    public VMAManager Mem { get; set; }

    public Dentry Root { get; set; } = null!;
    public Dentry CurrentWorkingDirectory { get; set; } = null!; // Renamed to avoid confusion with string Cwd
    
    // For chroot tracking, we keep a Dentry pointer to the process root
    public Dentry ProcessRoot { get; set; } = null!;

    // File Descriptors (Shared if CLONE_FILES)
    public Dictionary<int, Bifrost.VFS.File> FDs { get; private set; } = new();
    
    public FutexManager Futex { get; private set; }

    public uint BrkAddr { get; set; }
    public bool Strace { get; set; }

    // Async support: the task that is currently blocking this engine
    public System.Threading.Tasks.Task? BlockingTask { get; set; }

    public delegate int SyscallHandler(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6);
    private SyscallHandler?[] _syscallHandlers = new SyscallHandler?[MaxSyscalls];
    private const int MaxSyscalls = 512;

    public SyscallManager(Engine engine, VMAManager mem, uint brk, string hostRoot)
    {
        Engine = engine;
        Mem = mem;
        BrkAddr = brk;
        Futex = new FutexManager();

        RegisterEngine(engine);
        RegisterHandlers();
        
        // Init VFS
        var hostFsType = new FileSystemType { Name = "hostfs", FileSystem = new Hostfs() };
        
        var sb = hostFsType.FileSystem.ReadSuper(hostFsType, 0, hostRoot, null);
        Root = sb.Root;
        ProcessRoot = Root;
        CurrentWorkingDirectory = Root;
        
        // Add console FDs
        InitStdio();
    }
    
    private void InitStdio()
    {
        // 0: Stdin
        // We need a dummy SuperBlock for devices
        var devSb = new TmpfsSuperBlock(new FileSystemType { Name = "devtmpfs", FileSystem = new Tmpfs() });
        var stdinInode = new ConsoleInode(devSb, true);
        var stdinDentry = new Dentry("stdin", stdinInode, Root, devSb);
        FDs[0] = new Bifrost.VFS.File(stdinDentry, FileFlags.O_RDONLY);
        
        var stdoutInode = new ConsoleInode(devSb, false);
        var stdoutDentry = new Dentry("stdout", stdoutInode, Root, devSb);
        FDs[1] = new Bifrost.VFS.File(stdoutDentry, FileFlags.O_WRONLY);
        FDs[2] = new Bifrost.VFS.File(stdoutDentry, FileFlags.O_WRONLY); // stderr uses same
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
            newFds = new Dictionary<int, Bifrost.VFS.File>(FDs); 
        }
        
        return new SyscallManager(newMem, newFds, Futex, BrkAddr, Strace, Root, CurrentWorkingDirectory, ProcessRoot);
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
        if (vector != 0x80) return false;

        // Update current engine context (GIL ensures safety)
        Engine = engine;
        BlockingTask = null; // Clear previous blocking task

        uint eax = engine.RegRead(Reg.EAX);
        uint ebx = engine.RegRead(Reg.EBX);
        uint ecx = engine.RegRead(Reg.ECX);
        uint edx = engine.RegRead(Reg.EDX);
        uint esi = engine.RegRead(Reg.ESI);
        uint edi = engine.RegRead(Reg.EDI);
        uint ebp = engine.RegRead(Reg.EBP);

        if (Strace)
        {
            Console.Write($"[{Bifrost.Core.Task.GIL.GetHashCode()}] syscall({eax}, 0x{ebx:x}, 0x{ecx:x}, 0x{edx:x}, 0x{esi:x}, 0x{edi:x}, 0x{ebp:x})");
        }

        int ret = -38; // ENOSYS
        if (eax < MaxSyscalls && _syscallHandlers[eax] != null)
        {
            ret = _syscallHandlers[eax]!(engine.State, ebx, ecx, edx, esi, edi, ebp);
        }
        else if (!Strace)
        {
            Console.WriteLine($"Unimplemented Syscall: {eax}");
        }

        if (BlockingTask != null)
        {
            // If Syscall handler set a blocking task, it should also have set status to Yield.
            if (Strace) Console.WriteLine(" [Blocked]");
            return true;
        }

        if (Strace)
        {
            Console.WriteLine($" = {ret}");
        }

        engine.RegWrite(Reg.EAX, (uint)ret);
        return true;
    }

    public void Close()
    {
        lock (_registryLock)
        {
            if (Engine != null)
                _registry.Remove(Engine.State);
        }
    }
}
