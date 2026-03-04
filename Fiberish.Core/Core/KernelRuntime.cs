using Fiberish.Core.VFS.TTY;
using Fiberish.Memory;
using Fiberish.Syscalls;

namespace Fiberish.Core;

/// <summary>
///     Global runtime objects that should be initialized once for the first process.
/// </summary>
public sealed class KernelRuntime
{
    private KernelRuntime(Engine engine, VMAManager memory, SyscallManager syscalls, Configuration configuration)
    {
        Engine = engine;
        Memory = memory;
        Syscalls = syscalls;
        Configuration = configuration;
    }

    public Engine Engine { get; }
    public VMAManager Memory { get; }
    public SyscallManager Syscalls { get; }
    public Configuration Configuration { get; }

    public static KernelRuntime BootstrapBare(bool strace, TtyDiscipline? tty = null)
    {
        var configuration = new Configuration();
        var engine = new Engine();
        var mm = new VMAManager();

        var sys = new SyscallManager(engine, mm, 0, tty)
        {
            Strace = strace
        };

        return new KernelRuntime(engine, mm, sys, configuration);
    }

    public static KernelRuntime Bootstrap(string rootRes, bool strace, bool useOverlay, TtyDiscipline? tty = null)
    {
        return BootstrapWithRoot(strace, sys =>
        {
            if (useOverlay)
            {
                sys.MountRootOverlay(rootRes);
                sys.MountStandardDev(tty);
                sys.MountStandardProc();
                sys.MountStandardShm();
                sys.CreateStandardTmp();
            }
            else
            {
                sys.MountRootHostfs(rootRes);
                sys.MountStandardDev(tty);
                sys.MountStandardProc();
                sys.MountStandardShm();
            }
        }, tty);
    }

    public static KernelRuntime BootstrapWithRoot(bool strace, Action<SyscallManager> mountRoot,
        TtyDiscipline? tty = null)
    {
        var runtime = BootstrapBare(strace, tty);
        mountRoot(runtime.Syscalls);
        return runtime;
    }
}
