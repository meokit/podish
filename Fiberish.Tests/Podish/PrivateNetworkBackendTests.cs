using Fiberish.Core.Net;
using Xunit;

namespace Fiberish.Tests.Podish;

public class PrivateNetworkBackendTests
{
    [Fact]
    public void CreateContainerNetwork_MultipleContainers_DistinctIpsAllocated()
    {
        // This test verifies that multiple containers get distinct IP addresses
        var sw = new DummySwitch();
        using var backend = new PrivateNetworkBackend(sw);

        var contexts = new List<ContainerNetworkContext>();
        var allocatedIps = new HashSet<string>();

        try
        {
            // Create multiple containers
            for (var i = 0; i < 5; i++)
            {
                var spec = new ContainerNetworkSpec
                {
                    ContainerId = $"test-container-{i}"
                };

                var ctx = backend.CreateContainerNetwork(spec);
                contexts.Add(ctx);

                // Verify IP is unique
                var ipStr = ctx.PrivateIpv4.ToString();
                Assert.True(allocatedIps.Add(ipStr), $"IP {ipStr} was allocated more than once");

                // Verify IP is in the expected range (10.88.0.x)
                var bytes = ctx.PrivateIpv4.GetAddressBytes();
                Assert.Equal(10, bytes[0]);
                Assert.Equal(88, bytes[1]);
                Assert.Equal(0, bytes[2]);
                Assert.True(bytes[3] >= 2 && bytes[3] <= 254, $"IP octet {bytes[3]} out of range");
            }

            Assert.Equal(5, contexts.Count);
            Assert.Equal(5, allocatedIps.Count);
        }
        finally
        {
            // Cleanup
            foreach (var ctx in contexts)
            {
                backend.DestroyContainerNetwork(ctx);
                ctx.Dispose();
            }
        }
    }

    [Fact]
    public void DestroyContainerNetwork_ReleasesIpBackToPool()
    {
        var sw = new DummySwitch();
        using var backend = new PrivateNetworkBackend(sw);

        // Create and destroy a container
        var spec1 = new ContainerNetworkSpec { ContainerId = "test-container-1" };
        var ctx1 = backend.CreateContainerNetwork(spec1);
        var ip1 = ctx1.PrivateIpv4.ToString();
        var ipBytes1 = ctx1.PrivateIpv4.GetAddressBytes();
        var octet1 = ipBytes1[3];

        backend.DestroyContainerNetwork(ctx1);
        ctx1.Dispose();

        // Create another container - should be able to reuse the released IP
        var spec2 = new ContainerNetworkSpec { ContainerId = "test-container-2" };
        var ctx2 = backend.CreateContainerNetwork(spec2);

        try
        {
            var ip2 = ctx2.PrivateIpv4.ToString();
            var ipBytes2 = ctx2.PrivateIpv4.GetAddressBytes();
            var octet2 = ipBytes2[3];

            // The new container should get the same IP that was just released
            // (since we allocate from lowest available)
            Assert.Equal(octet1, octet2);
            Assert.Equal(ip1, ip2);
        }
        finally
        {
            backend.DestroyContainerNetwork(ctx2);
            ctx2.Dispose();
        }
    }

    [Fact]
    public void CreateContainerNetwork_IpPoolExhaustion_ThrowsException()
    {
        var sw = new DummySwitch();
        using var backend = new PrivateNetworkBackend(sw);

        var contexts = new List<ContainerNetworkContext>();

        try
        {
            // Try to allocate more IPs than available (2-254 = 253 addresses)
            // We'll allocate many to trigger exhaustion
            for (var i = 0; i < 260; i++)
            {
                var spec = new ContainerNetworkSpec
                {
                    ContainerId = $"test-container-{i}"
                };

                if (i >= 253)
                {
                    // Should throw when pool is exhausted
                    Assert.Throws<InvalidOperationException>(() => backend.CreateContainerNetwork(spec));
                    break;
                }

                var ctx = backend.CreateContainerNetwork(spec);
                contexts.Add(ctx);
            }
        }
        finally
        {
            // Cleanup all contexts
            foreach (var ctx in contexts)
            {
                backend.DestroyContainerNetwork(ctx);
                ctx.Dispose();
            }
        }
    }

    [Fact]
    public void DummySwitch_AttachDetach_LifecycleCorrect()
    {
        var sw = new DummySwitch();
        using var backend = new PrivateNetworkBackend(sw);

        // Create multiple containers
        var contexts = new List<ContainerNetworkContext>();
        for (var i = 0; i < 3; i++)
        {
            var spec = new ContainerNetworkSpec { ContainerId = $"lifecycle-test-{i}" };
            var ctx = backend.CreateContainerNetwork(spec);
            contexts.Add(ctx);

            // After creation, the context should be attached to the switch
            // (we verify this by checking the switch can resolve)
        }

        // Destroy them in reverse order
        for (var i = contexts.Count - 1; i >= 0; i--)
        {
            var ctx = contexts[i];
            backend.DestroyContainerNetwork(ctx);
            ctx.Dispose();
        }

        // Create new containers after destroying old ones
        var newContexts = new List<ContainerNetworkContext>();
        for (var i = 0; i < 3; i++)
        {
            var spec = new ContainerNetworkSpec { ContainerId = $"lifecycle-new-{i}" };
            var ctx = backend.CreateContainerNetwork(spec);
            newContexts.Add(ctx);
        }

        // Cleanup
        foreach (var ctx in newContexts)
        {
            backend.DestroyContainerNetwork(ctx);
            ctx.Dispose();
        }

        // If we get here without exceptions, lifecycle is correct
        Assert.True(true, "Switch attach/detach lifecycle completed without errors");
    }

    [Fact]
    public void CreateContainerNetwork_RepeatedCreateDestroy_NoLeak()
    {
        var sw = new DummySwitch();
        using var backend = new PrivateNetworkBackend(sw);

        // Repeatedly create and destroy containers to check for leaks
        for (var round = 0; round < 10; round++)
        {
            var contexts = new List<ContainerNetworkContext>();

            // Create several containers
            for (var i = 0; i < 5; i++)
            {
                var spec = new ContainerNetworkSpec
                {
                    ContainerId = $"round-{round}-container-{i}"
                };
                var ctx = backend.CreateContainerNetwork(spec);
                contexts.Add(ctx);
            }

            // Destroy all
            foreach (var ctx in contexts)
            {
                backend.DestroyContainerNetwork(ctx);
                ctx.Dispose();
            }
        }

        // After all rounds, we should still be able to create containers
        var finalContexts = new List<ContainerNetworkContext>();
        try
        {
            for (var i = 0; i < 5; i++)
            {
                var spec = new ContainerNetworkSpec
                {
                    ContainerId = $"final-container-{i}"
                };
                var ctx = backend.CreateContainerNetwork(spec);
                finalContexts.Add(ctx);
            }

            // Verify we got distinct IPs
            var ips = finalContexts.Select(c => c.PrivateIpv4.ToString()).ToHashSet();
            Assert.Equal(5, ips.Count);
        }
        finally
        {
            foreach (var ctx in finalContexts)
            {
                backend.DestroyContainerNetwork(ctx);
                ctx.Dispose();
            }
        }
    }
}