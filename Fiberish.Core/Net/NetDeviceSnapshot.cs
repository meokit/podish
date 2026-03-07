using System.Net;
using System.Collections.Generic;

namespace Fiberish.Core.Net;

public sealed class NetDeviceSnapshot
{
    public required string Name { get; init; }
    public required int IfIndex { get; init; }
    public required uint Flags { get; init; }
    public required int Mtu { get; init; }
    public required int TxQueueLen { get; init; }
    public required byte[] MacAddress { get; init; }
    public required IPAddress? Ipv4Address { get; init; }
    public required byte Ipv4PrefixLength { get; init; }
}

public sealed class NetDeviceSetSnapshot
{
    public required IReadOnlyList<NetDeviceSnapshot> Devices { get; init; }
}
