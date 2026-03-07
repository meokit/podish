using Fiberish.Core.Net;
using Microsoft.Extensions.Logging;

namespace Podish.Core.Networking;

public sealed class PortForwardManager : IDisposable
{
    private readonly PortForwardLoop _loop;
    private readonly ILogger<PortForwardManager> _logger;

    public PortForwardManager(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<PortForwardManager>();
        _loop = new PortForwardLoop(loggerFactory.CreateLogger<PortForwardLoop>());
    }

    public void Start(ContainerNetworkContext context, IReadOnlyList<PublishedPortSpec> ports)
    {
        if (ports.Count == 0) return;
        _loop.StartPublishedPorts(context, ports);
    }

    public bool Stop(ContainerNetworkContext context)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _loop.StopPublishedPorts(context, tcs);
        // Wait with timeout to prevent deadlock if the event loop is stuck
        if (!tcs.Task.Wait(TimeSpan.FromSeconds(5)))
        {
            _logger.LogWarning("Timed out waiting for port forward loop to acknowledge stop for container {Id}", context.ContainerId);
            return false;
        }
        return true;
    }

    public void Dispose()
    {
        _loop.Dispose();
    }
}
