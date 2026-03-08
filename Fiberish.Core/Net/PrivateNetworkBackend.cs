using System.Net;

namespace Fiberish.Core.Net;

public sealed class PrivateNetworkBackend : INetworkBackend
{
    private readonly INetworkSwitch _switch;
    private readonly object _ipLock = new();
    private readonly bool[] _allocatedIps = new bool[256];

    public PrivateNetworkBackend(INetworkSwitch @switch)
    {
        _switch = @switch;
    }

    public NetworkMode Mode => NetworkMode.Private;

    public ContainerNetworkContext CreateContainerNetwork(ContainerNetworkSpec spec)
    {
        int octet = -1;
        lock (_ipLock)
        {
            for (int i = 2; i <= 254; i++)
            {
                if (!_allocatedIps[i])
                {
                    _allocatedIps[i] = true;
                    octet = i;
                    break;
                }
            }
        }

        if (octet == -1) throw new InvalidOperationException("Private network IP pool exhausted");
        
        uint ipBe = (uint)(0x0A580000 | octet);
        var ns = LoopbackNetNamespace.Create(ipBe, 24);
        var ctx = new ContainerNetworkContext(spec.ContainerId, NetworkMode.Private, ns.PrivateIpv4Address, ns, _switch);
        _switch.Attach(ctx);
        return ctx;
    }

    public void StartPublishedPorts(ContainerNetworkContext context, IReadOnlyList<PublishedPortSpec> ports)
    {
        // This will be handled by PortForwardManager at the runtime level, 
        // but the interface allows backend-specific logic if needed.
        // For now, we leave it to the caller (ContainerRuntimeService) to use PortForwardManager.
    }

    public void DestroyContainerNetwork(ContainerNetworkContext context)
    {
        _switch.Detach(context);

        var ipBytes = context.PrivateIpv4.GetAddressBytes();
        if (ipBytes.Length == 4 && ipBytes[0] == 10 && ipBytes[1] == 88 && ipBytes[2] == 0)
        {
            int octet = ipBytes[3];
            if (octet >= 2 && octet <= 254)
            {
                lock (_ipLock)
                {
                    _allocatedIps[octet] = false;
                }
            }
        }
    }

    public void Dispose() { }
}
