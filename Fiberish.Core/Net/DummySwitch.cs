using System.Net;

namespace Fiberish.Core.Net;

public sealed class DummySwitch : INetworkSwitch
{
    public string Name => "dummy";

    public void Attach(ContainerNetworkContext context)
    {
    }

    public void Detach(ContainerNetworkContext context)
    {
    }

    public ConnectTarget ResolvePublishedPortTarget(ContainerNetworkContext context, int containerPort,
        TransportProtocol protocol)
    {
        // For DummySwitch, we always resolve to the container's private IP (which happens to be the loopback address in current single-container implementation)
        return new ConnectTarget(context.PrivateIpv4, containerPort, protocol);
    }

    public bool IsLocalAddress(ContainerNetworkContext context, IPAddress address)
    {
        return address.Equals(IPAddress.Loopback) || address.Equals(context.PrivateIpv4);
    }
}