using System.Net;
using Fiberish.Core.Net;
using Xunit;

namespace Fiberish.Tests.Core;

public class DummySwitchTests
{
    [Fact]
    public void ResolvePublishedPortTarget_MapsToContainerIpAndPort()
    {
        // Arrange
        var sw = new DummySwitch();
        var context = new ContainerNetworkContext(
            "test-container",
            NetworkMode.Private,
            new IPAddress([10, 88, 0, 1]), // 10.88.0.1
            (LoopbackNetNamespace)null!,
            sw);

        var spec = new PublishedPortSpec
        {
            HostPort = 8080,
            ContainerPort = 80,
            Protocol = TransportProtocol.Tcp
        };

        // Act
        var target = sw.ResolvePublishedPortTarget(context, spec.ContainerPort, spec.Protocol);

        // Assert
        Assert.Equal(new IPAddress([10, 88, 0, 1]), target.Address);
        Assert.Equal(80, target.Port);
    }
}