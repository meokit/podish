using Fiberish.Core.VFS.TTY;
using Fiberish.Memory;
using Fiberish.Syscalls;

namespace Fiberish.Core;

/// <summary>
/// Global runtime objects that should be initialized once for the first process.
/// </summary>
public sealed class KernelRuntime
{
    private KernelRuntime(Engine engine, VMAManager memory, SyscallManager syscalls)
    {
        Engine = engine;
        Memory = memory;
        Syscalls = syscalls;
    }

    public Engine Engine { get; }
    public VMAManager Memory { get; }
    public SyscallManager Syscalls { get; }

    public static KernelRuntime Bootstrap(string rootRes, bool strace, bool useOverlay, TtyDiscipline? tty = null)
    {
        var engine = new Engine();
        var mm = new VMAManager();

        var sys = new SyscallManager(engine, mm, 0, rootRes, useOverlay, tty)
        {
            Strace = strace
        };

        ProcFsManager.Init(sys);
        return new KernelRuntime(engine, mm, sys);
    }
}
