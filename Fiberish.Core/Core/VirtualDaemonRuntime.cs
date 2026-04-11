using System.Net.Sockets;
using System.Text;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;

namespace Fiberish.Core;

public sealed class VirtualDaemonRuntime
{
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
        Task.SignalPosted += HandleSignalPosted;
    }

    public KernelScheduler Scheduler { get; }
    public FiberTask Task { get; }
    public Process Process { get; }
    public SyscallManager Syscalls { get; }
    public IVirtualDaemon Daemon { get; }
    public LinuxFile? ListenFile => _listenFd >= 0 ? Syscalls.GetFD(_listenFd) : null;
    internal Exception? LastScheduledFailure { get; private set; }

    public void Start(int backlog = 16)
    {
        Scheduler.AssertSchedulerThread();
        EnsureUnixListener(backlog);
        Daemon.OnStart(CreateContext(Task));
    }

    public LinuxFile EnsureUnixListener(int backlog = 16)
    {
        Scheduler.AssertSchedulerThread();

        if (ListenFile != null) return ListenFile;

        EnsureParentDirectories(Daemon.UnixPath);

        var inode = new UnixSocketInode(0, Syscalls.MemfdSuperBlock, SocketType.Stream, Scheduler);
        var dentry = new Dentry($"socket:[{inode.Ino}]", inode, null, Syscalls.MemfdSuperBlock);
        var file = new LinuxFile(dentry, FileFlags.O_RDWR, Syscalls.AnonMount);
        var bindRc = inode.Bind(file, Task, new UnixSockaddrInfo
        {
            IsAbstract = false,
            PathBytes = Encoding.UTF8.GetBytes(Daemon.UnixPath),
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

    private void EnsureParentDirectories(string unixPath)
    {
        if (string.IsNullOrWhiteSpace(unixPath) || unixPath[0] != '/')
            return;

        var slash = unixPath.LastIndexOf('/');
        if (slash <= 0)
            return;

        var directoryPath = unixPath[..slash];
        var parts = directoryPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return;

        var current = Syscalls.Root;
        foreach (var part in parts)
            current = EnsureDirectory(current, FsName.FromString(part));
    }

    private static PathLocation EnsureDirectory(PathLocation parent, FsName name)
    {
        var parentDentry = parent.Dentry ?? throw new InvalidOperationException("Parent dentry is missing");

        if (parentDentry.TryGetCachedChild(name, out var cached))
            return new PathLocation(cached, parent.Mount);

        var dentry = parentDentry.Inode!.Lookup(name);
        if (dentry == null)
        {
            dentry = new Dentry(name, null, parentDentry, parentDentry.SuperBlock);
            parentDentry.Inode.Mkdir(dentry, 0x1FF, 0, 0);
        }
        else if (dentry.Inode?.Type != InodeType.Directory)
        {
            throw new InvalidOperationException(
                $"Path component '{name.ToDebugString()}' exists but is not a directory");
        }

        parentDentry.CacheChild(dentry, "VirtualDaemonRuntime.EnsureDirectory");
        return new PathLocation(dentry, parent.Mount);
    }

    public void Schedule(Action<VirtualDaemonContext> action, FiberTask? task = null)
    {
        var contextTask = task ?? Task;
        var context = CreateContext(contextTask);
        Scheduler.Schedule(() => action(context), contextTask);
    }

    public void Schedule(Func<VirtualDaemonContext, ValueTask> action, FiberTask? task = null)
    {
        var contextTask = task ?? Task;
        var context = CreateContext(contextTask);
        Scheduler.Schedule(() => StartScheduledAction(context, action), contextTask);
    }

    public void ScheduleChild(Func<VirtualDaemonContext, ValueTask> action)
    {
        Scheduler.AssertSchedulerThread();
        var childTask = CreateChildTask();
        var childContext = CreateContext(childTask);
        Scheduler.Schedule(() => StartScheduledAction(childContext, action, childTask), childTask);
    }

    public async ValueTask<(int Rc, VirtualDaemonConnection? Connection)> AcceptAsync(FiberTask task, int flags = 0)
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
        return (0, new VirtualDaemonConnection(this, task, file, listenFile));
    }

    public void Exit(int exitCode = 0)
    {
        if (Interlocked.Exchange(ref _exiting, 1) != 0) return;

        Scheduler.Schedule(() =>
        {
            var logger = Scheduler.LoggerFactory.CreateLogger<VirtualDaemonRuntime>();
            try
            {
                logger.LogInformation(
                    "Virtual daemon exiting name={Name} pid={Pid} ppid={ParentPid} exitCode={ExitCode}",
                    Daemon.Name, Process.TGID, Process.PPID, exitCode);
                Daemon.OnStop(CreateContext(Task));
            }
            finally
            {
                logger.LogDebug("Virtual daemon finalize exit name={Name} pid={Pid}", Daemon.Name, Process.TGID);
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
                    Daemon.OnSignal(CreateContext(Task), signo);
                    return;
                default:
                    Daemon.OnSignal(CreateContext(Task), signo);
                    return;
            }
        }, Task);
    }

    private VirtualDaemonContext CreateContext(FiberTask task)
    {
        return new VirtualDaemonContext(this, task);
    }

    private FiberTask CreateChildTask()
    {
        Scheduler.AssertSchedulerThread();

        var engine = new Engine(Syscalls.MemoryContext)
        {
            CurrentSyscallManager = Syscalls
        };
        engine.ShareMmuFrom(Task.CPU);
        Syscalls.RegisterEngine(engine);

        var tid = Scheduler.AllocateTaskId();
        var task = new FiberTask(tid, Process, engine, Scheduler)
        {
            ExecutionMode = TaskExecutionMode.HostService,
            Status = FiberTaskStatus.Waiting
        };
        engine.Owner = task;
        return task;
    }

    private void StartScheduledAction(VirtualDaemonContext context, Func<VirtualDaemonContext, ValueTask> action,
        FiberTask? completionTask = null)
    {
        ValueTask pending;
        try
        {
            pending = action(context);
        }
        catch (Exception ex)
        {
            LastScheduledFailure = ex;
            if (completionTask != null)
                RetireChildTask(completionTask);
            Exit(1);
            throw;
        }

        if (pending.IsCompletedSuccessfully)
        {
            pending.GetAwaiter().GetResult();
            if (completionTask != null)
                RetireChildTask(completionTask);
            return;
        }

        _ = CompleteScheduledActionAsync(pending, completionTask);
    }

    private async Task CompleteScheduledActionAsync(ValueTask pending, FiberTask? completionTask)
    {
        try
        {
            await pending;
        }
        catch (Exception ex)
        {
            LastScheduledFailure = ex;
            if (completionTask != null)
                RetireChildTask(completionTask);
            Exit(1);
            throw;
        }

        if (completionTask != null)
            RetireChildTask(completionTask);
    }

    private void RetireChildTask(FiberTask task)
    {
        if (task.Exited || task.Status == FiberTaskStatus.Terminated)
            return;

        Scheduler.Schedule(() =>
        {
            if (task.Exited || task.Status == FiberTaskStatus.Terminated)
                return;

            Scheduler.DetachTask(task);
        }, task);
    }
}
