using System.Net;

namespace Fiberish.Core.Net;

public sealed class ContainerNetworkContext : IDisposable
{
    public ContainerNetworkContext(string containerId, NetworkMode mode, IPAddress privateIpv4, LoopbackNetNamespace ns, INetworkSwitch @switch)
    {
        ContainerId = containerId;
        Mode = mode;
        PrivateIpv4 = privateIpv4;
        Namespace = ns;
        Switch = @switch;
    }

    public string ContainerId { get; }
    public NetworkMode Mode { get; }
    public IPAddress PrivateIpv4 { get; }
    public LoopbackNetNamespace Namespace { get; }
    public INetworkSwitch Switch { get; }

    public void Dispose()
    {
        Namespace.Dispose();
    }
}
