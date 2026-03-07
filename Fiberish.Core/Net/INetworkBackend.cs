namespace Fiberish.Core.Net;

public sealed class ContainerNetworkSpec
{
    public required string ContainerId { get; init; }
}

public interface INetworkBackend : IDisposable
{
    NetworkMode Mode { get; }

    ContainerNetworkContext CreateContainerNetwork(ContainerNetworkSpec spec);

    void StartPublishedPorts(ContainerNetworkContext context, IReadOnlyList<PublishedPortSpec> ports);
    void DestroyContainerNetwork(ContainerNetworkContext context);
}
