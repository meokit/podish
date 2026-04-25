using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Podish.Core;

internal static unsafe class AppleResolverNative
{
    private const string LibResolv = "libresolv.9";
    private const int MaxNameServers = 3;

    public static IReadOnlyList<string> GetDnsServers()
    {
        var state = default(Res9State);
        if (ResNInit(ref state) != 0)
            return [];

        try
        {
            var servers = new ResSockaddrUnion[MaxNameServers];
            var count = ResGetServers(ref state, servers, servers.Length);
            if (count <= 0)
                return [];

            var result = new List<string>(count);
            for (var i = 0; i < count && i < servers.Length; i++)
                if (TryFormatAddress(in servers[i], out var address))
                    result.Add(address);

            return result;
        }
        finally
        {
            ResNDestroy(ref state);
        }
    }

    private static bool TryFormatAddress(in ResSockaddrUnion server, out string address)
    {
        address = string.Empty;
        var family = (AddressFamily)server.V4.SinFamily;
        switch (family)
        {
            case AddressFamily.InterNetwork:
            {
                var bytes = BitConverter.GetBytes(server.V4.SinAddr.SAddr);
                address = new IPAddress(bytes).ToString();
                return true;
            }
            case AddressFamily.InterNetworkV6:
            {
                fixed (byte* ptr = server.V6.Sin6Addr.Bytes)
                {
                    var bytes = new byte[16];
                    Marshal.Copy((nint)ptr, bytes, 0, bytes.Length);
                    address = new IPAddress(bytes, server.V6.Sin6ScopeId).ToString();
                    return true;
                }
            }
            default:
                return false;
        }
    }

    [DllImport(LibResolv, EntryPoint = "res_9_ninit")]
    private static extern int ResNInit(ref Res9State state);

    [DllImport(LibResolv, EntryPoint = "res_9_ndestroy")]
    private static extern void ResNDestroy(ref Res9State state);

    [DllImport(LibResolv, EntryPoint = "res_9_getservers")]
    private static extern int ResGetServers(ref Res9State state, [Out] ResSockaddrUnion[] servers, int count);

    [StructLayout(LayoutKind.Sequential)]
    private struct Res9State
    {
        public int Retrans;
        public int Retry;
        public nuint Options;
        public int NameServerCount;
        public SockAddrIn NameServer0;
        public SockAddrIn NameServer1;
        public SockAddrIn NameServer2;
        public ushort Id;
        public nint DnsSearch0;
        public nint DnsSearch1;
        public nint DnsSearch2;
        public nint DnsSearch3;
        public nint DnsSearch4;
        public nint DnsSearch5;
        public nint DnsSearch6;
        public fixed byte DefaultDomainName[256];
        public nuint PfCode;
        public uint NdotsNsortUnused;
        public ResSortAddr Sort0;
        public ResSortAddr Sort1;
        public ResSortAddr Sort2;
        public ResSortAddr Sort3;
        public ResSortAddr Sort4;
        public ResSortAddr Sort5;
        public ResSortAddr Sort6;
        public ResSortAddr Sort7;
        public ResSortAddr Sort8;
        public ResSortAddr Sort9;
        public nint QueryHook;
        public nint ResponseHook;
        public int ResolverHErrno;
        public int VcSocket;
        public uint Flags;
        public uint Pad;
        public ResolverUnionStorage U;
        public nint RandomState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ResolverUnionStorage
    {
        public fixed byte Bytes[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ResSortAddr
    {
        public InAddr Address;
        public uint Mask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InAddr
    {
        public uint SAddr;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct In6Addr
    {
        public fixed byte Bytes[16];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SockAddrIn
    {
        public byte SinLen;
        public byte SinFamily;
        public ushort SinPort;
        public InAddr SinAddr;
        public fixed byte SinZero[8];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SockAddrIn6
    {
        public byte Sin6Len;
        public byte Sin6Family;
        public ushort Sin6Port;
        public uint Sin6FlowInfo;
        public In6Addr Sin6Addr;
        public uint Sin6ScopeId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct ResSockaddrUnion
    {
        [FieldOffset(0)] public SockAddrIn V4;
        [FieldOffset(0)] public SockAddrIn6 V6;
    }
}
