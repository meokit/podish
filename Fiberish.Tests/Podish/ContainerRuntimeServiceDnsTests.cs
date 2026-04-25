using Microsoft.Extensions.Logging.Abstractions;
using Podish.Core;
using Xunit;

namespace Fiberish.Tests.Podish;

public sealed class ContainerRuntimeServiceDnsTests
{
    [Fact]
    public void BuildResolvConfContent_UsesExplicitDnsServersWhenProvided()
    {
        var service = new ContainerRuntimeService(NullLogger.Instance, NullLoggerFactory.Instance);

        var resolvConf = service.BuildResolvConfContent(
            ["1.1.1.1", "9.9.9.9"],
            ["nameserver 127.0.0.1", "search local.test"],
            ["192.168.1.1"]);

        Assert.Contains("nameserver 1.1.1.1", resolvConf);
        Assert.Contains("nameserver 9.9.9.9", resolvConf);
        Assert.DoesNotContain("192.168.1.1", resolvConf);
        Assert.DoesNotContain("search local.test", resolvConf);
    }

    [Fact]
    public void BuildResolvConfContent_PrefersInterfaceDnsAndPreservesSearchOptions()
    {
        var service = new ContainerRuntimeService(NullLogger.Instance, NullLoggerFactory.Instance);

        var resolvConf = service.BuildResolvConfContent(
            [],
            [
                "nameserver 127.0.0.1",
                "search corp.example",
                "options ndots:5"
            ],
            ["10.0.0.53", "10.0.0.54"]);

        Assert.Contains("nameserver 10.0.0.53", resolvConf);
        Assert.Contains("nameserver 10.0.0.54", resolvConf);
        Assert.DoesNotContain("nameserver 127.0.0.1", resolvConf);
        Assert.Contains("search corp.example", resolvConf);
        Assert.Contains("options ndots:5", resolvConf);
    }

    [Fact]
    public void BuildResolvConfContent_FallsBackToPublicDnsOnlyWhenNoUsableHostDnsExists()
    {
        var service = new ContainerRuntimeService(NullLogger.Instance, NullLoggerFactory.Instance);

        var resolvConf = service.BuildResolvConfContent(
            [],
            [
                "nameserver 127.0.0.1",
                "nameserver ::1",
                "search fallback.example"
            ],
            []);

        Assert.Contains("nameserver 8.8.8.8", resolvConf);
        Assert.DoesNotContain("nameserver 127.0.0.1", resolvConf);
        Assert.DoesNotContain("nameserver ::1", resolvConf);
        Assert.Contains("search fallback.example", resolvConf);
    }
}
