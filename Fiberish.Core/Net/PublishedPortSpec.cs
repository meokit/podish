namespace Fiberish.Core.Net;

public sealed class PublishedPortSpec
{
    public int HostPort { get; init; }
    public int ContainerPort { get; init; }
    public TransportProtocol Protocol { get; init; } = TransportProtocol.Tcp;
    public string BindAddress { get; init; } = "0.0.0.0";
}