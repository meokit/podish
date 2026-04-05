using Fiberish.Syscalls;
using Fiberish.VFS;

namespace Fiberish.Core;

public sealed class VirtualDaemonContext
{
    private readonly VirtualDaemonRuntime _runtime;

    internal VirtualDaemonContext(VirtualDaemonRuntime runtime, FiberTask task)
    {
        _runtime = runtime;
        Task = task;
    }

    public Process Process => _runtime.Process;
    public FiberTask Task { get; }

    public KernelScheduler Scheduler => _runtime.Scheduler;
    public SyscallManager Syscalls => _runtime.Syscalls;
    public LinuxFile? ListenFile => _runtime.ListenFile;
    public string UnixPath => _runtime.Daemon.UnixPath;

    public LinuxFile EnsureUnixListener(int backlog = 16)
    {
        return _runtime.EnsureUnixListener(backlog);
    }

    public void Schedule(Action<VirtualDaemonContext> action)
    {
        _runtime.Schedule(action, Task);
    }

    public void Schedule(Func<VirtualDaemonContext, ValueTask> action)
    {
        _runtime.Schedule(action, Task);
    }

    public void ScheduleChild(Func<VirtualDaemonContext, ValueTask> action)
    {
        _runtime.ScheduleChild(action);
    }

    public ValueTask<(int Rc, VirtualDaemonConnection? Connection)> AcceptAsync(int flags = 0)
    {
        return _runtime.AcceptAsync(Task, flags);
    }

    public void Exit(int exitCode = 0)
    {
        _runtime.Exit(exitCode);
    }
}