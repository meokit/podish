using System.Runtime.InteropServices;
using Bifrost.Core;
using Bifrost.Memory;
using Bifrost.Native;

namespace Bifrost.Syscalls;

public unsafe partial class SyscallManager
{
    private static readonly Dictionary<IntPtr, SyscallManager> _registry = new();
    private static readonly object _registryLock = new();

    // The current engine executing a syscall (protected by GIL)
    public Engine Engine { get; set; } = null!;
    
    public VMAManager Mem { get; set; }

    public string RootFS { get; set; } = "/";
    public string Cwd { get; set; } = "/";

    // File Descriptors (Shared if CLONE_FILES)
    public Dictionary<int, LinuxFile> FDs { get; private set; } = new();
    
    public FutexManager Futex { get; private set; }

    public uint BrkAddr { get; set; }
    public bool Strace { get; set; }

    public delegate int SyscallHandler(IntPtr state, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6);
    private SyscallHandler?[] _syscallHandlers = new SyscallHandler?[MaxSyscalls];
    private const int MaxSyscalls = 512;

    public SyscallManager(Engine engine, VMAManager mem, uint brk)
    {
        Engine = engine;
        Mem = mem;
        BrkAddr = brk;
        Futex = new FutexManager();

        RegisterEngine(engine);

        RegisterHandlers();
    }
    
    private SyscallManager(VMAManager mem, Dictionary<int, LinuxFile> fds, FutexManager futex, uint brk, bool strace)
    {
        Mem = mem;
        FDs = fds;
        Futex = futex;
        BrkAddr = brk;
        Strace = strace;
        
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
        Dictionary<int, LinuxFile> newFds;
        if (shareFiles)
        {
            newFds = FDs;
        }
        else
        {
            newFds = new Dictionary<int, LinuxFile>(FDs); 
        }
        
        return new SyscallManager(newMem, newFds, Futex, BrkAddr, Strace);
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
