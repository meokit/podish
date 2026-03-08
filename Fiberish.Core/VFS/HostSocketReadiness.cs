using System.Net.Sockets;
using Fiberish.Core;
using Fiberish.Syscalls;
using Microsoft.Extensions.Logging;

namespace Fiberish.VFS;

internal sealed class HostSocketReadiness : IDisposable
{
    private readonly IReadyDispatcher _dispatcher;
    private readonly HostSocketProbeEngine _probeEngine;
    private readonly ReadinessWaiter _waiter;

    public HostSocketReadiness(HostSocketInode owner, Socket socket, ILogger logger, IReadyDispatcher? dispatcher = null)
    {
        _dispatcher = dispatcher ?? SchedulerReadyDispatcher.FromCurrent();
        _probeEngine = new HostSocketProbeEngine(owner, socket, logger, _dispatcher);
        _waiter = new ReadinessWaiter(
            (file, events) => _probeEngine.Poll(events),
            (file, callback, events) => _probeEngine.RegisterWaitHandle(file, callback, events),
            () => _dispatcher.CurrentTask);
    }

    public short Poll(LinuxFile file, short events) => _probeEngine.Poll(events);

    public bool RegisterWait(LinuxFile file, Action callback, short events) =>
        _probeEngine.RegisterWaitHandle(file, callback, events) != null;

    public IDisposable? RegisterWaitHandle(LinuxFile file, Action callback, short events) =>
        _probeEngine.RegisterWaitHandle(file, callback, events);

    public ValueTask<bool> WaitForSocketEventAsync(LinuxFile file, short events) =>
        _waiter.WaitAsync(file, events);

    public void ClearReadyBits(short bits) => _probeEngine.ClearReadyBits(bits);

    public bool TryDequeueAcceptedSocket(out Socket socket) => _probeEngine.TryDequeueAcceptedSocket(out socket);

    public bool HasBufferedAcceptedSocket() => _probeEngine.HasBufferedAcceptedSocket();

    public void Dispose() => _probeEngine.Dispose();
}
