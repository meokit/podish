using Fiberish.Core.Net;
using Xunit;

namespace Fiberish.Tests.VFS;

public class NetstackSocketOptionTests
{
    private const uint LoopbackAddress = 0x7F000001;

    [Fact]
    public void SocketBuffers_ApplyRealNativeCapacityAndRefuseResizeAfterConnect()
    {
        using var ns = LoopbackNetNamespace.Create(LoopbackAddress, 8);
        using var listener = ns.CreateTcpListener();
        Assert.Equal(0, listener.TryListen(0, 4));
        using var stream = ns.CreateTcpStream();
        Assert.Equal((16 * 1024, 16 * 1024), stream.GetBufferSizes());

        Assert.Equal(0, stream.TrySetBuffers(131072, 65536));
        Assert.Equal((131072, 65536), stream.GetBufferSizes());

        Assert.Equal(0, stream.TryConnect(LoopbackAddress, listener.LocalPort));
        // A connected smoltcp socket owns fixed buffers; the caller must not receive a
        // success that silently leaves the old capacity in place.
        Assert.NotEqual(0, stream.TrySetBuffers(32768, 32768));
        Assert.Equal((131072, 65536), stream.GetBufferSizes());
    }

    [Fact]
    public void UdpBuffers_ApplyBeforeBindAndRefuseAfterBind()
    {
        using var ns = LoopbackNetNamespace.Create(LoopbackAddress, 8);
        using var udp = ns.CreateUdpSocket();
        Assert.Equal(0, udp.TrySetBuffers(65536, 32768));
        Assert.Equal((65536, 32768), udp.GetBufferSizes());
        Assert.Equal(0, udp.TryBind(0));
        Assert.NotEqual(0, udp.TrySetBuffers(16384, 16384));
        Assert.Equal((65536, 32768), udp.GetBufferSizes());
    }

    [Fact]
    public void TcpOptions_ChangeTheSmoltcpSocketState()
    {
        using var ns = LoopbackNetNamespace.Create(LoopbackAddress, 8);
        using var stream = ns.CreateTcpStream();
        Assert.False(stream.NoDelay);
        Assert.Equal(0, stream.TrySetNoDelay(true));
        Assert.True(stream.NoDelay);
        Assert.Equal(0, stream.TrySetNoDelay(false));
        Assert.False(stream.NoDelay);

        Assert.Equal(0, stream.TrySetKeepAlive(9000));
        Assert.Equal(9000, stream.KeepAliveIntervalMillis);
        Assert.Equal(0, stream.TrySetKeepAlive(0));
        Assert.Equal(0, stream.KeepAliveIntervalMillis);
    }

    [Fact]
    public void MulticastMembershipControlsLoopbackDelivery()
    {
        const uint group = 0xEFFF0001; // 239.255.0.1
        using var ns = LoopbackNetNamespace.Create(LoopbackAddress, 8);
        using var receiver = ns.CreateUdpSocket();
        using var sender = ns.CreateUdpSocket();
        Assert.Equal(0, receiver.TryBind(0));
        Assert.Equal(0, sender.TryBind(0));
        var port = (ushort)receiver.LocalEndPoint.Port;
        var payload = new byte[] { 0x51, 0x52 };
        Assert.Equal(0, sender.TrySendTo(group, port, payload, out var written));
        Assert.Equal(payload.Length, written);
        ns.Poll(1);
        Assert.False(receiver.CanRead); // Not a member yet: the interface filters it.

        Assert.Equal(0, ns.JoinMulticast(group));
        Assert.Equal(0, sender.TrySendTo(group, port, payload, out written));
        ns.Poll(2);
        Assert.True(receiver.CanRead);
        var buffer = new byte[8];
        Assert.Equal(0, receiver.TryReceiveFrom(buffer, out var read, out var remote));
        Assert.Equal(payload.Length, read);
        Assert.Equal(payload, buffer.AsSpan(0, read).ToArray());
        Assert.NotEqual((ushort)0, remote.Port); // The sender's ephemeral source port.

        Assert.Equal(0, ns.LeaveMulticast(group));
        Assert.Equal(0, sender.TrySendTo(group, port, payload, out written));
        ns.Poll(3);
        Assert.False(receiver.CanRead);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 7)]
    public void LingerAbortResetsThePeerWhileGracefulCloseReportsCloseWait(bool abort, int expectedPeerState)
    {
        using var ns = LoopbackNetNamespace.Create(LoopbackAddress, 8);
        using var listener = ns.CreateTcpListener();
        Assert.Equal(0, listener.TryListen(0, 4));
        using var client = ns.CreateTcpStream();
        Assert.Equal(0, client.TryConnect(LoopbackAddress, listener.LocalPort));

        var clock = 1L;
        while (!listener.AcceptPending && clock < 100)
            ns.Poll(clock++);
        Assert.True(listener.AcceptPending, "loopback TCP handshake did not complete");
        using var accepted = listener.Accept();
        while (client.State != 4 && clock < 200)
            ns.Poll(clock++);
        Assert.Equal(4, client.State); // Established

        if (abort) client.Abort();
        else client.Dispose(); // Graceful FIN.

        while (accepted.State != expectedPeerState && clock < 200)
            ns.Poll(clock++);
        Assert.Equal(expectedPeerState, accepted.State);
    }
}
