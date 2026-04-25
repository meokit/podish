using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Fiberish.Core.Native;

internal static partial class NetstackNative
{
    private const string LibName = "fiberish_netstack";

#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void Initialize()
    {
        NativeLibraryResolver.Register(typeof(NetstackNative), LibName);
    }
#pragma warning restore CA2255

    [LibraryImport(LibName, EntryPoint = "fiber_netns_set_notify_callback")]
    internal static partial int SetNotifyCallback(ulong handle, nint cb, nint userdata);

    [LibraryImport(LibName, EntryPoint = "fiber_netns_clear_notify")]
    internal static partial int ClearNotify(ulong handle);

    [LibraryImport(LibName, EntryPoint = "fiber_netns_create_loopback")]
    internal static partial ulong CreateLoopback(uint ipv4Be, byte prefixLen);

    [LibraryImport(LibName, EntryPoint = "fiber_netns_destroy")]
    internal static partial int Destroy(ulong handle);

    [LibraryImport(LibName, EntryPoint = "fiber_netns_get_ipv4")]
    internal static partial int GetIpv4(ulong handle, out uint ipv4Be, out byte prefixLen);

    [LibraryImport(LibName, EntryPoint = "fiber_netns_poll")]
    internal static partial int Poll(ulong handle, long nowMillis, out long nextPollMillis);

    [LibraryImport(LibName, EntryPoint = "fiber_tcp_stream_create")]
    internal static partial ulong CreateTcpStream(ulong netnsHandle);

    [LibraryImport(LibName, EntryPoint = "fiber_tcp_listener_create")]
    internal static partial ulong CreateTcpListener(ulong netnsHandle);

    [LibraryImport(LibName, EntryPoint = "fiber_udp_socket_create")]
    internal static partial ulong CreateUdpSocket(ulong netnsHandle);

    [LibraryImport(LibName, EntryPoint = "fiber_tcp_listener_listen")]
    internal static partial int
        TcpListenerListen(ulong netnsHandle, ulong socketHandle, ushort localPort, uint backlog);

    [LibraryImport(LibName, EntryPoint = "fiber_tcp_stream_connect")]
    internal static partial int TcpStreamConnect(ulong netnsHandle, ulong socketHandle, uint remoteIpv4Be,
        ushort remotePort);

    [LibraryImport(LibName, EntryPoint = "fiber_tcp_listener_accept")]
    internal static partial int
        TcpListenerAccept(ulong netnsHandle, ulong socketHandle, out ulong acceptedSocketHandle);

    [LibraryImport(LibName, EntryPoint = "fiber_tcp_stream_send")]
    internal static unsafe partial int TcpStreamSend(ulong netnsHandle, ulong socketHandle, byte* data, nuint len,
        out nuint written);

    [LibraryImport(LibName, EntryPoint = "fiber_tcp_stream_recv")]
    internal static unsafe partial int TcpStreamRecv(ulong netnsHandle, ulong socketHandle, byte* buffer, nuint len,
        out nuint read);

    [LibraryImport(LibName, EntryPoint = "fiber_tcp_stream_can_read")]
    internal static partial int TcpStreamCanRead(ulong netnsHandle, ulong socketHandle);

    [LibraryImport(LibName, EntryPoint = "fiber_tcp_stream_can_write")]
    internal static partial int TcpStreamCanWrite(ulong netnsHandle, ulong socketHandle);

    [LibraryImport(LibName, EntryPoint = "fiber_tcp_stream_may_read")]
    internal static partial int TcpStreamMayRead(ulong netnsHandle, ulong socketHandle);

    [LibraryImport(LibName, EntryPoint = "fiber_tcp_stream_may_write")]
    internal static partial int TcpStreamMayWrite(ulong netnsHandle, ulong socketHandle);

    [LibraryImport(LibName, EntryPoint = "fiber_tcp_stream_close")]
    internal static partial int TcpStreamClose(ulong netnsHandle, ulong socketHandle);

    [LibraryImport(LibName, EntryPoint = "fiber_tcp_listener_accept_pending")]
    internal static partial int TcpListenerAcceptPending(ulong netnsHandle, ulong socketHandle);

    [LibraryImport(LibName, EntryPoint = "fiber_socket_close")]
    internal static partial int CloseSocket(ulong netnsHandle, ulong socketHandle);

    [LibraryImport(LibName, EntryPoint = "fiber_tcp_stream_state")]
    internal static partial int TcpStreamState(ulong netnsHandle, ulong socketHandle);

    [LibraryImport(LibName, EntryPoint = "fiber_tcp_stream_get_local_endpoint")]
    internal static partial int TcpStreamGetLocalEndpoint(ulong netnsHandle, ulong socketHandle, out uint ipv4Be,
        out ushort port);

    [LibraryImport(LibName, EntryPoint = "fiber_tcp_stream_get_remote_endpoint")]
    internal static partial int TcpStreamGetRemoteEndpoint(ulong netnsHandle, ulong socketHandle, out uint ipv4Be,
        out ushort port);

    [LibraryImport(LibName, EntryPoint = "fiber_udp_socket_bind")]
    internal static partial int UdpSocketBind(ulong netnsHandle, ulong socketHandle, ushort localPort);

    [LibraryImport(LibName, EntryPoint = "fiber_udp_socket_send_to")]
    internal static unsafe partial int UdpSocketSendTo(ulong netnsHandle, ulong socketHandle, uint remoteIpv4Be,
        ushort remotePort, byte* data, nuint len, out nuint written);

    [LibraryImport(LibName, EntryPoint = "fiber_udp_socket_recv_from")]
    internal static unsafe partial int UdpSocketRecvFrom(ulong netnsHandle, ulong socketHandle, byte* buffer, nuint len,
        out nuint read, out uint ipv4Be, out ushort port);

    [LibraryImport(LibName, EntryPoint = "fiber_udp_socket_can_read")]
    internal static partial int UdpSocketCanRead(ulong netnsHandle, ulong socketHandle);

    [LibraryImport(LibName, EntryPoint = "fiber_udp_socket_can_write")]
    internal static partial int UdpSocketCanWrite(ulong netnsHandle, ulong socketHandle);

    [LibraryImport(LibName, EntryPoint = "fiber_udp_socket_get_local_endpoint")]
    internal static partial int UdpSocketGetLocalEndpoint(ulong netnsHandle, ulong socketHandle, out uint ipv4Be,
        out ushort port);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void NetnsNotifyCallback(nint userdata);
}