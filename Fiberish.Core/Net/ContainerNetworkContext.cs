using System.Net;

namespace Fiberish.Core.Net;

public sealed class ContainerNetworkContext : IDisposable
{
    public ContainerNetworkContext(string containerId, NetworkMode mode, IPAddress privateIpv4, SharedLoopbackNetNamespace sharedNamespace, INetworkSwitch @switch)
    {
        ContainerId = containerId;
        Mode = mode;
        PrivateIpv4 = privateIpv4;
        SharedNamespace = sharedNamespace;
        Switch = @switch;
    }

    public ContainerNetworkContext(string containerId, NetworkMode mode, IPAddress privateIpv4, LoopbackNetNamespace ns, INetworkSwitch @switch)
        : this(containerId, mode, privateIpv4, new SharedLoopbackNetNamespace(ns), @switch)
    {
    }

    public string ContainerId { get; }
    public NetworkMode Mode { get; }
    public IPAddress PrivateIpv4 { get; }
    public SharedLoopbackNetNamespace SharedNamespace { get; }
    public LoopbackNetNamespace Namespace => SharedNamespace.Namespace;
    public INetworkSwitch Switch { get; }

    public void Dispose()
    {
        SharedNamespace.Release();
    }
}
