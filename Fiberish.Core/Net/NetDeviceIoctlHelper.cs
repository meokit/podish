using System.Buffers.Binary;
using System.Net;
using System.Text;
using Fiberish.Native;
using Fiberish.Syscalls;

namespace Fiberish.Core.Net;

public static class NetDeviceIoctlHelper
{
    public static int Handle(Engine engine, uint request, uint arg)
    {
        var sm = SyscallManager.Get(engine.State);
        if (sm == null)
            return -(int)Errno.EPERM;

        var snapshot = NetDeviceSnapshotProvider.Capture(sm.NetworkMode, sm.TryGetPrivateNetNamespace());
        return request switch
        {
            LinuxConstants.SIOCGIFCONF => HandleGetIfConf(engine, arg, snapshot),
            LinuxConstants.SIOCGIFFLAGS => HandleGetIfFlags(engine, arg, snapshot),
            LinuxConstants.SIOCGIFADDR => HandleGetIfAddr(engine, arg, snapshot),
            LinuxConstants.SIOCGIFNETMASK => HandleGetIfNetmask(engine, arg, snapshot),
            LinuxConstants.SIOCGIFMTU => HandleGetIfMtu(engine, arg, snapshot),
            LinuxConstants.SIOCGIFTXQLEN => HandleGetIfTxQueueLen(engine, arg, snapshot),
            _ => -(int)Errno.ENOTTY
        };
    }

    private static int HandleGetIfConf(Engine engine, uint arg, NetDeviceSetSnapshot snapshot)
    {
        Span<byte> ifconf = stackalloc byte[8];
        if (!engine.CopyFromUser(arg, ifconf))
            return -(int)Errno.EFAULT;

        var maxLen = BinaryPrimitives.ReadInt32LittleEndian(ifconf[..4]);
        var ifreqPtr = BinaryPrimitives.ReadUInt32LittleEndian(ifconf.Slice(4, 4));
        if (ifreqPtr == 0)
            return -(int)Errno.EFAULT;

        var entries = snapshot.Devices.Where(d => d.Ipv4Address != null).ToList();
        var required = entries.Count * 32;
        var writable = Math.Max(0, Math.Min(maxLen, required));
        var writeCount = writable / 32;

        for (var i = 0; i < writeCount; i++)
        {
            var device = entries[i];
            var ifreq = new byte[32];
            WriteName(ifreq, device.Name);
            WriteSockaddrIn(ifreq.AsSpan(16, 16), device.Ipv4Address!);
            if (!engine.CopyToUser(ifreqPtr + (uint)(i * 32), ifreq))
                return -(int)Errno.EFAULT;
        }

        BinaryPrimitives.WriteInt32LittleEndian(ifconf[..4], writeCount * 32);
        if (!engine.CopyToUser(arg, ifconf))
            return -(int)Errno.EFAULT;
        return 0;
    }

    private static int HandleGetIfFlags(Engine engine, uint arg, NetDeviceSetSnapshot snapshot)
    {
        var ifreq = new byte[32];
        if (!engine.CopyFromUser(arg, ifreq))
            return -(int)Errno.EFAULT;
        var dev = FindDevice(ifreq, snapshot);
        if (dev == null)
            return -(int)Errno.ENODEV;

        BinaryPrimitives.WriteUInt16LittleEndian(ifreq.AsSpan(16, 2), (ushort)(dev.Flags & 0xffff));
        return engine.CopyToUser(arg, ifreq) ? 0 : -(int)Errno.EFAULT;
    }

    private static int HandleGetIfAddr(Engine engine, uint arg, NetDeviceSetSnapshot snapshot)
    {
        var ifreq = new byte[32];
        if (!engine.CopyFromUser(arg, ifreq))
            return -(int)Errno.EFAULT;
        var dev = FindDevice(ifreq, snapshot);
        if (dev == null)
            return -(int)Errno.ENODEV;
        if (dev.Ipv4Address == null)
            return -(int)Errno.EADDRNOTAVAIL;

        WriteSockaddrIn(ifreq.AsSpan(16, 16), dev.Ipv4Address);
        return engine.CopyToUser(arg, ifreq) ? 0 : -(int)Errno.EFAULT;
    }

    private static int HandleGetIfNetmask(Engine engine, uint arg, NetDeviceSetSnapshot snapshot)
    {
        var ifreq = new byte[32];
        if (!engine.CopyFromUser(arg, ifreq))
            return -(int)Errno.EFAULT;
        var dev = FindDevice(ifreq, snapshot);
        if (dev == null)
            return -(int)Errno.ENODEV;
        if (dev.Ipv4Address == null)
            return -(int)Errno.EADDRNOTAVAIL;

        var mask = NetDeviceSnapshotProvider.PrefixToNetmask(dev.Ipv4PrefixLength);
        WriteSockaddrIn(ifreq.AsSpan(16, 16), mask);
        return engine.CopyToUser(arg, ifreq) ? 0 : -(int)Errno.EFAULT;
    }

    private static int HandleGetIfMtu(Engine engine, uint arg, NetDeviceSetSnapshot snapshot)
    {
        var ifreq = new byte[32];
        if (!engine.CopyFromUser(arg, ifreq))
            return -(int)Errno.EFAULT;
        var dev = FindDevice(ifreq, snapshot);
        if (dev == null)
            return -(int)Errno.ENODEV;

        BinaryPrimitives.WriteInt32LittleEndian(ifreq.AsSpan(16, 4), dev.Mtu);
        return engine.CopyToUser(arg, ifreq) ? 0 : -(int)Errno.EFAULT;
    }

    private static int HandleGetIfTxQueueLen(Engine engine, uint arg, NetDeviceSetSnapshot snapshot)
    {
        var ifreq = new byte[32];
        if (!engine.CopyFromUser(arg, ifreq))
            return -(int)Errno.EFAULT;
        var dev = FindDevice(ifreq, snapshot);
        if (dev == null)
            return -(int)Errno.ENODEV;

        BinaryPrimitives.WriteInt32LittleEndian(ifreq.AsSpan(16, 4), dev.TxQueueLen);
        return engine.CopyToUser(arg, ifreq) ? 0 : -(int)Errno.EFAULT;
    }

    private static NetDeviceSnapshot? FindDevice(byte[] ifreq, NetDeviceSetSnapshot snapshot)
    {
        var name = ReadName(ifreq);
        return snapshot.Devices.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.Ordinal));
    }

    private static string ReadName(byte[] ifreq)
    {
        var n = Array.IndexOf(ifreq, (byte)0, 0, LinuxConstants.IFNAMSIZ);
        if (n < 0) n = LinuxConstants.IFNAMSIZ;
        return Encoding.ASCII.GetString(ifreq, 0, n);
    }

    private static void WriteName(byte[] buffer, string name)
    {
        var bytes = Encoding.ASCII.GetBytes(name);
        Array.Clear(buffer, 0, LinuxConstants.IFNAMSIZ);
        Array.Copy(bytes, 0, buffer, 0, Math.Min(bytes.Length, LinuxConstants.IFNAMSIZ - 1));
    }

    private static void WriteSockaddrIn(Span<byte> sockaddr, IPAddress addr)
    {
        sockaddr.Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(sockaddr.Slice(0, 2), LinuxConstants.AF_INET);
        var ip = addr.GetAddressBytes();
        ip.AsSpan(0, 4).CopyTo(sockaddr.Slice(4, 4));
    }
}