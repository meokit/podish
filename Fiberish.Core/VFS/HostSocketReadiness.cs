using System.Net.Sockets;
using Fiberish.Core;
using Microsoft.Extensions.Logging;

namespace Fiberish.VFS;

internal sealed class HostSocketReadiness : IDisposable
{
    private readonly HostSocketProbeEngine _probeEngine;
    private readonly ReadinessWaiter _waiter;

    public HostSocketReadiness(HostSocketInode owner, Socket socket, ILogger logger)
    {
        _probeEngine = new HostSocketProbeEngine(owner, socket, logger);
        _waiter = new ReadinessWaiter(
            (file, events) => _probeEngine.Poll(events),
            (file, dispatcher, callback, events) =>
                _probeEngine.RegisterWaitHandle(file, dispatcher, callback, events));
    }

    public void Dispose()
    {
        _probeEngine.Dispose();
    }

    public short Poll(LinuxFile file, short events)
    {
        return _probeEngine.Poll(events);
    }

    public bool RegisterWait(LinuxFile file, IReadyDispatcher dispatcher, Action callback, short events)
    {
        return _probeEngine.RegisterWaitHandle(file, dispatcher, callback, events) != null;
    }

    public IDisposable? RegisterWaitHandle(LinuxFile file, IReadyDispatcher dispatcher, Action callback, short events)
    {
        return _probeEngine.RegisterWaitHandle(file, dispatcher, callback, events);
    }

    public ValueTask<bool> WaitForSocketEventAsync(LinuxFile file, FiberTask task, short events)
    {
        var dispatcher = new SchedulerReadyDispatcher(task.CommonKernel);
        return _waiter.WaitAsync(file, dispatcher, task, events);
    }

    public void ClearReadyBits(short bits)
    {
        _probeEngine.ClearReadyBits(bits);
    }

    public void NotifyManagedConnectCompleted(SocketError error)
    {
        _probeEngine.NotifyManagedConnectCompleted(error);
    }

    public bool TryDequeueAcceptedSocket(out Socket socket)
    {
        return _probeEngine.TryDequeueAcceptedSocket(out socket);
    }

    public bool HasBufferedAcceptedSocket()
    {
        return _probeEngine.HasBufferedAcceptedSocket();
    }
}
