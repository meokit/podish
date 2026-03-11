using System.Net;
using Fiberish.Native;

namespace Fiberish.Core.Net;

public static class NetDeviceSnapshotProvider
{
    public static NetDeviceSetSnapshot Capture(NetworkMode mode, LoopbackNetNamespace? privateNamespace)
    {
        var devices = new List<NetDeviceSnapshot>(2)
        {
            new()
            {
                Name = "lo",
                IfIndex = 1,
                Flags = LinuxConstants.IFF_UP | LinuxConstants.IFF_RUNNING | LinuxConstants.IFF_LOOPBACK,
                Mtu = 65536,
                TxQueueLen = 1000,
                MacAddress = [0, 0, 0, 0, 0, 0],
                Ipv4Address = IPAddress.Loopback,
                Ipv4PrefixLength = 8
            }
        };

        if (mode == NetworkMode.Private && privateNamespace != null)
        {
            var ip = privateNamespace.PrivateIpv4Address;
            var bytes = ip.GetAddressBytes();
            devices.Add(new NetDeviceSnapshot
            {
                Name = "eth0",
                IfIndex = 2,
                Flags = LinuxConstants.IFF_UP | LinuxConstants.IFF_RUNNING,
                Mtu = 1500,
                TxQueueLen = 1000,
                MacAddress = [0x02, 0x42, bytes[0], bytes[1], bytes[2], bytes[3]],
                Ipv4Address = ip,
                Ipv4PrefixLength = privateNamespace.PrefixLength
            });
        }

        return new NetDeviceSetSnapshot { Devices = devices };
    }

    public static IPAddress PrefixToNetmask(byte prefixLen)
    {
        if (prefixLen == 0)
            return IPAddress.Any;

        var mask = uint.MaxValue << (32 - prefixLen);
        return new IPAddress(new[]
        {
            (byte)((mask >> 24) & 0xff),
            (byte)((mask >> 16) & 0xff),
            (byte)((mask >> 8) & 0xff),
            (byte)(mask & 0xff)
        });
    }
}