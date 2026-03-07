using System.Net;

namespace Fiberish.Core.Net;

public sealed class HostNetworkBackend : INetworkBackend
{
    public NetworkMode Mode => NetworkMode.Host;

    public ContainerNetworkContext CreateContainerNetwork(ContainerNetworkSpec spec)
    {
        // Host network doesn't need a real netstack for now in this context, 
        // but we return a dummy context if needed. 
        // Actually, for Host mode, we might not حتى create a context or it should be different.
        // Based on the design, StartPublishedPorts takes a context.
        throw new NotSupportedException("Published ports are not supported in Host network mode.");
    }

    public void StartPublishedPorts(ContainerNetworkContext context, IReadOnlyList<PublishedPortSpec> ports) { }
    public void DestroyContainerNetwork(ContainerNetworkContext context) { }

    public void Dispose() { }
}
