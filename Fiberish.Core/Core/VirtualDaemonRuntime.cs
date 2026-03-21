using System.Text;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;

namespace Fiberish.Core;

public sealed class VirtualDaemonRuntime
{
    private readonly VirtualDaemonContext _context;
    private int _exiting;
    private int _listenFd = -1;
    private UnixSocketInode? _listenInode;

    internal VirtualDaemonRuntime(KernelScheduler scheduler, FiberTask task, IVirtualDaemon daemon)
    {
        Scheduler = scheduler;
        Task = task;
        Process = task.Process;
        Syscalls = task.Process.Syscalls;
        Daemon = daemon;
        _context = new VirtualDaemonContext(this);
        Task.SignalPosted += HandleSignalPosted;
    }

    public KernelScheduler Scheduler { get; }
    public FiberTask Task { get; }
    public Process Process { get; }
    public SyscallManager Syscalls { get; }
    public IVirtualDaemon Daemon { get; }
    public LinuxFile? ListenFile => _listenFd >= 0 ? Syscalls.GetFD(_listenFd) : null;

    public void Start(int backlog = 16)
    {
        Scheduler.AssertSchedulerThread();
        EnsureUnixListener(backlog);
        Daemon.OnStart(_context);
    }

    public LinuxFile EnsureUnixListener(int backlog = 16)
    {
        Scheduler.AssertSchedulerThread();

        if (ListenFile != null) return ListenFile;

        var inode = new UnixSocketInode(0, Syscalls.MemfdSuperBlock, System.Net.Sockets.SocketType.Stream, Scheduler);
        var dentry = new Dentry($"socket:[{inode.Ino}]", inode, null, Syscalls.MemfdSuperBlock);
        var file = new LinuxFile(dentry, FileFlags.O_RDWR, Syscalls.AnonMount);
        var bindRc = inode.Bind(file, Task, new UnixSockaddrInfo
        {
            IsAbstract = false,
            Path = Daemon.UnixPath,
            SunPathRaw = Encoding.UTF8.GetBytes(Daemon.UnixPath + "\0")
        });
        if (bindRc != 0)
            throw new InvalidOperationException(
                $"Failed to bind virtual daemon '{Daemon.Name}' to '{Daemon.UnixPath}': rc={bindRc}");

        var listenRc = inode.Listen(backlog);
        if (listenRc != 0)
            throw new InvalidOperationException(
                $"Failed to listen virtual daemon '{Daemon.Name}' on '{Daemon.UnixPath}': rc={listenRc}");

        _listenFd = Syscalls.AllocFD(file);
        if (_listenFd < 0)
            throw new InvalidOperationException(
                $"Failed to allocate fd for virtual daemon '{Daemon.Name}' listener: rc={_listenFd}");

        _listenInode = inode;
        return file;
    }

    public void Schedule(Action<VirtualDaemonContext> action)
    {
        Scheduler.Schedule(() => action(_context), Task);
    }

    public void Schedule(Func<VirtualDaemonContext, ValueTask> action)
    {
        Scheduler.Schedule(() => StartScheduledAction(action), Task);
    }

    public async ValueTask<(int Rc, VirtualDaemonConnection? Connection)> AcceptAsync(int flags = 0)
    {
        Scheduler.AssertSchedulerThread();

        var listenFile = ListenFile;
        var listenInode = _listenInode;
        if (listenFile == null || listenInode == null)
            return (-(int)Errno.ENOTSOCK, null);

        var accepted = await ((ISocketEndpointOps)listenInode).AcceptAsync(listenFile, Task, flags);
        if (accepted.Rc != 0 || accepted.Inode == null)
            return (accepted.Rc, null);

        var dentry = new Dentry($"socket:[{accepted.Inode.Ino}]", accepted.Inode, null, Syscalls.MemfdSuperBlock);
        var file = new LinuxFile(dentry, FileFlags.O_RDWR, Syscalls.AnonMount);
        return (0, new VirtualDaemonConnection(this, file, listenFile));
    }

    public void Exit(int exitCode = 0)
    {
        if (Interlocked.Exchange(ref _exiting, 1) != 0) return;

        Scheduler.Schedule(() =>
        {
            try
            {
                Daemon.OnStop(_context);
            }
            finally
            {
                SyscallManager.FinalizeProcessExit(Task, exitCode, false, 0, false);
            }
        }, Task);
    }

    private void HandleSignalPosted(int signo)
    {
        Scheduler.Schedule(() =>
        {
            if (Task.Exited) return;

            switch ((Signal)signo)
            {
                case Signal.SIGKILL:
                    if (Interlocked.Exchange(ref _exiting, 1) == 0)
                        SyscallManager.FinalizeProcessExit(Task, 0, true, signo, false);
                    return;
                case Signal.SIGTERM:
                case Signal.SIGINT:
                    Daemon.OnSignal(_context, signo);
                    return;
                default:
                    Daemon.OnSignal(_context, signo);
                    return;
            }
        }, Task);
    }

    private void StartScheduledAction(Func<VirtualDaemonContext, ValueTask> action)
    {
        ValueTask pending;
        try
        {
            pending = action(_context);
        }
        catch
        {
            Exit(1);
            throw;
        }

        if (pending.IsCompletedSuccessfully)
        {
            pending.GetAwaiter().GetResult();
            return;
        }

        _ = CompleteScheduledActionAsync(pending);
    }

    private async Task CompleteScheduledActionAsync(ValueTask pending)
    {
        try
        {
            await pending;
        }
        catch
        {
            Exit(1);
            throw;
        }
    }
}
