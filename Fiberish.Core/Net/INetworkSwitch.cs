using System.Net;

namespace Fiberish.Core.Net;

public interface INetworkSwitch
{
    string Name { get; }

    void Attach(ContainerNetworkContext context);
    void Detach(ContainerNetworkContext context);

    ConnectTarget ResolvePublishedPortTarget(ContainerNetworkContext context, int containerPort,
        TransportProtocol protocol);

    bool IsLocalAddress(ContainerNetworkContext context, IPAddress address);
}