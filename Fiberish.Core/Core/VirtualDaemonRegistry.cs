using Fiberish.Syscalls;

namespace Fiberish.Core;

public sealed class VirtualDaemonRegistry
{
    private readonly Dictionary<int, VirtualDaemonRuntime> _byPid = [];
    private readonly Dictionary<string, VirtualDaemonRuntime> _byUnixPath = new(StringComparer.Ordinal);
    private readonly KernelScheduler _scheduler;
    private readonly SyscallManager _templateSyscalls;

    public VirtualDaemonRegistry(SyscallManager templateSyscalls, KernelScheduler scheduler)
    {
        _templateSyscalls = templateSyscalls;
        _scheduler = scheduler;
    }

    public IReadOnlyCollection<VirtualDaemonRuntime> All => _byPid.Values;

    public VirtualDaemonRuntime Spawn(IVirtualDaemon daemon, int backlog = 16, int parentPid = 0,
        UTSNamespace? uts = null)
    {
        _scheduler.AssertSchedulerThread();

        if (_byUnixPath.ContainsKey(daemon.UnixPath))
            throw new InvalidOperationException($"Virtual daemon path already registered: {daemon.UnixPath}");

        var actualParentPid = parentPid > 0 ? parentPid : _scheduler.InitPid;
        var task = ProcessFactory.CreateVirtualDaemonProcess(_templateSyscalls, _scheduler, daemon.Name, uts,
            actualParentPid);
        var runtime = new VirtualDaemonRuntime(_scheduler, task, daemon);
        runtime.Start(backlog);

        _byPid[runtime.Process.TGID] = runtime;
        _byUnixPath[daemon.UnixPath] = runtime;
        return runtime;
    }

    public VirtualDaemonRuntime? LookupByPid(int pid)
    {
        _scheduler.AssertSchedulerThread();
        return _byPid.GetValueOrDefault(pid);
    }

    public VirtualDaemonRuntime? LookupByUnixPath(string unixPath)
    {
        _scheduler.AssertSchedulerThread();
        return _byUnixPath.GetValueOrDefault(unixPath);
    }
}
