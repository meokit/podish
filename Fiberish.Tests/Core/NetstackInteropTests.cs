using System.Text;
using Fiberish.Core.Net;
using Xunit;

namespace Fiberish.Tests.Core;

public class NetstackInteropTests
{
    [Fact]
    public void LoopbackNamespace_Create_ReadbackAndPoll_Works()
    {
        using var netns = LoopbackNetNamespace.Create(0x0A590002u, 24);

        Assert.Equal(0x0A590002u, netns.Ipv4AddressBe);
        Assert.Equal(24, netns.PrefixLength);

        var next = netns.Poll(0);
        Assert.True(next >= 0);
    }

    [Fact]
    public void LoopbackNamespace_TcpSelfConnect_SendReceive_Works()
    {
        using var netns = LoopbackNetNamespace.Create(0x0A590002u, 24);
        using var listener = netns.CreateTcpListener();
        using var client = netns.CreateTcpStream();

        listener.Listen(8080);
        client.Connect(netns.Ipv4AddressBe, 8080);

        LoopUntil(netns, () => listener.AcceptPending && client.CanWrite, 2_000);

        using var accepted = listener.Accept();
        LoopUntil(netns, () => client.State == 4 && accepted.State == 4, 2_000);
        Assert.Equal(4, client.State);
        Assert.Equal(4, accepted.State);

        var payload = Encoding.ASCII.GetBytes("ping");
        Assert.Equal(payload.Length, client.Send(payload));

        LoopUntil(netns, () => accepted.CanRead, 2_000);

        Span<byte> buffer = stackalloc byte[16];
        var read = accepted.Receive(buffer);
        Assert.Equal(payload.Length, read);
        Assert.Equal("ping", Encoding.ASCII.GetString(buffer[..read]));
    }

    [Fact]
    public void LoopbackNamespace_TcpLoopbackAddress_SendReceive_Works()
    {
        using var netns = LoopbackNetNamespace.Create(0x0A590002u, 24);
        using var listener = netns.CreateTcpListener();
        using var client = netns.CreateTcpStream();

        listener.Listen(19090);
        client.Connect(0x7F000001u, 19090);

        LoopUntil(netns, () => listener.AcceptPending && client.CanWrite, 2_000);

        using var accepted = listener.Accept();
        LoopUntil(netns, () => client.State == 4 && accepted.State == 4, 2_000);

        var payload = Encoding.ASCII.GetBytes("loopback");
        Assert.Equal(payload.Length, client.Send(payload));
        LoopUntil(netns, () => accepted.CanRead, 2_000);

        Span<byte> buffer = stackalloc byte[32];
        var read = accepted.Receive(buffer);
        Assert.Equal("loopback", Encoding.ASCII.GetString(buffer[..read]));
    }

    [Fact]
    public void LoopbackNamespace_UdpLoopback_SendReceive_Works()
    {
        using var netns = LoopbackNetNamespace.Create(0x0A590002u, 24);
        using var server = netns.CreateUdpSocket();
        using var client = netns.CreateUdpSocket();

        server.Bind(19210);
        client.Bind(19211);

        var payload = Encoding.ASCII.GetBytes("udp-loopback");
        Assert.Equal(payload.Length, client.SendTo(0x7F000001u, 19210, payload));

        LoopUntil(netns, () => server.CanRead, 2_000);

        Span<byte> buffer = stackalloc byte[32];
        var read = server.ReceiveFrom(buffer, out var remoteEndPoint);
        Assert.Equal("udp-loopback", Encoding.ASCII.GetString(buffer[..read]));
        Assert.Equal("127.0.0.1", remoteEndPoint.Address.ToString());
        Assert.Equal(19211, remoteEndPoint.Port);
    }

    [Fact]
    public void LoopbackNamespace_TcpCloseWrite_PropagatesRemoteEof()
    {
        using var netns = LoopbackNetNamespace.Create(0x0A590002u, 24);
        using var listener = netns.CreateTcpListener();
        using var client = netns.CreateTcpStream();

        listener.Listen(20300);
        client.Connect(0x7F000001u, 20300);

        LoopUntil(netns, () => listener.AcceptPending && client.CanWrite, 2_000);

        using var accepted = listener.Accept();
        LoopUntil(netns, () => client.State == 4 && accepted.State == 4, 2_000);

        var payload = Encoding.ASCII.GetBytes("bye");
        Assert.Equal(payload.Length, client.Send(payload));
        LoopUntil(netns, () => accepted.CanRead, 2_000);

        Span<byte> buffer = stackalloc byte[16];
        var read = accepted.Receive(buffer);
        Assert.Equal("bye", Encoding.ASCII.GetString(buffer[..read]));

        client.CloseWrite();
        LoopUntil(netns, () => !accepted.MayRead, 2_000);

        Assert.False(accepted.MayRead);
        Assert.Equal(0, accepted.Receive(buffer));
    }

    [Fact]
    public void LoopbackNamespace_TcpListenerListenZero_AllocatesEphemeralPortAndRejectsDuplicatePort()
    {
        using var netns = LoopbackNetNamespace.Create(0x0A590002u, 24);
        using var listener = netns.CreateTcpListener();
        using var duplicate = netns.CreateTcpListener();
        using var client = netns.CreateTcpStream();

        Assert.Equal(0, listener.TryListen(0));
        var port = listener.LocalPort;
        Assert.True(port >= 49152);
        Assert.NotEqual(0, duplicate.TryListen(port));

        Assert.Equal(0, client.TryConnect(0x7F000001u, port));
        LoopUntil(netns, () => listener.AcceptPending && client.CanWrite, 2_000);

        using var accepted = listener.Accept();
        LoopUntil(netns, () => client.State == 4 && accepted.State == 4, 2_000);

        var payload = Encoding.ASCII.GetBytes("ephemeral");
        Assert.Equal(payload.Length, client.Send(payload));
        LoopUntil(netns, () => accepted.CanRead, 2_000);

        Span<byte> buffer = stackalloc byte[32];
        var read = accepted.Receive(buffer);
        Assert.Equal("ephemeral", Encoding.ASCII.GetString(buffer[..read]));
    }

    [Fact]
    public void LoopbackNamespace_TcpCloseAfterBufferedSend_DrainsAllBytesAndFin()
    {
        using var netns = LoopbackNetNamespace.Create(0x0A590002u, 24);
        using var listener = netns.CreateTcpListener();
        using var client = netns.CreateTcpStream();

        listener.Listen(8081);
        client.Connect(netns.Ipv4AddressBe, 8081);
        var nowMs = 0L;
        for (; nowMs < 2_000; nowMs += 10)
        {
            if (listener.AcceptPending && client.CanWrite)
                break;
            netns.Poll(nowMs);
        }
        Assert.True(listener.AcceptPending);

        using var accepted = listener.Accept();
        for (; nowMs < 2_000; nowMs += 10)
        {
            if (client.State == 4 && accepted.State == 4)
                break;
            netns.Poll(nowMs);
        }
        Assert.Equal(4, client.State);
        Assert.Equal(4, accepted.State);

        var chunk1 = new byte[16 * 1024];
        var chunk2 = new byte[16 * 1024];
        for (var i = 0; i < chunk1.Length; i++)
        {
            chunk1[i] = (byte)(i & 0xFF);
            chunk2[i] = (byte)((i + 17) & 0xFF);
        }

        Assert.Equal(chunk1.Length, client.Send(chunk1));
        for (; nowMs < 2_000 && !client.CanWrite; nowMs += 10)
            netns.Poll(nowMs);
        Assert.Equal(chunk2.Length, client.Send(chunk2));
        netns.Poll(nowMs += 10);

        // Dispose/close the client immediately while chunk2 is still buffered in TX.
        client.Dispose();

        var received = new List<byte>(chunk1.Length + chunk2.Length);
        var scratch = new byte[4096];
        for (var endMs = nowMs + 2_000; nowMs < endMs; nowMs += 10)
        {
            netns.Poll(nowMs);
            while (accepted.CanRead)
            {
                var n = accepted.Receive(scratch);
                if (n <= 0)
                    break;
                received.AddRange(scratch.AsSpan(0, n));
            }

            if (!accepted.CanRead && !accepted.MayRead)
                break;
        }

        Assert.Equal(chunk1.Length + chunk2.Length, received.Count);
        Assert.Equal(chunk1, received.GetRange(0, chunk1.Length).ToArray());
        Assert.Equal(chunk2, received.GetRange(chunk1.Length, chunk2.Length).ToArray());
    }

    [Fact]
    public void LoopbackNamespace_ReservedTcpPort_AvoidsConnectEphemeralPortCollision()
    {
        using var netns = LoopbackNetNamespace.Create(0x0A590002u, 24);
        Assert.True(netns.TryReserveTcpPort(49152));

        using var listener = netns.CreateTcpListener();
        using var client = netns.CreateTcpStream();
        listener.Listen(8082);
        client.Connect(netns.Ipv4AddressBe, 8082);

        Assert.NotEqual(49152, client.LocalEndPoint.Port);
        netns.ReleaseTcpPort(49152);
        Assert.True(netns.TryReserveTcpPort(49152));
    }

    [Fact]
    public void LoopbackNamespace_TcpCloseWithUnreadPeer_ReclaimsSocketAndPortAfterNoProgressTimeout()
    {
        using var netns = LoopbackNetNamespace.Create(0x0A590002u, 24);
        using var listener = netns.CreateTcpListener();
        using var client = netns.CreateTcpStream();

        listener.Listen(8083);
        client.Connect(netns.Ipv4AddressBe, 8083);

        var nowMs = 0L;
        for (; nowMs < 2_000 && !(listener.AcceptPending && client.CanWrite); nowMs += 10)
            netns.Poll(nowMs);
        Assert.True(listener.AcceptPending);

        using var accepted = listener.Accept();
        for (; nowMs < 2_000 && !(client.State == 4 && accepted.State == 4); nowMs += 10)
            netns.Poll(nowMs);

        var clientPort = (ushort)client.LocalEndPoint.Port;
        var chunk = new byte[16 * 1024];
        Assert.Equal(chunk.Length, client.Send(chunk));
        for (; nowMs < 2_000 && !client.CanWrite; nowMs += 10)
            netns.Poll(nowMs);
        Assert.Equal(chunk.Length, client.Send(chunk));
        netns.Poll(nowMs += 10);

        // Close sender while peer never reads (second 16 KiB stays blocked in TX queue).
        client.Dispose();
        netns.Poll(nowMs += 10);

        // Immediately after close, the lingering stream still occupies clientPort for ephemeral allocation.
        Assert.True(netns.TryAllocateEphemeralPort(out var otherPort));
        Assert.NotEqual(clientPort, otherPort);
        netns.ReleaseTcpPort(otherPort);

        // Advance virtual time well past the bounded no-progress timeout (e.g. 1 hour).
        netns.Poll(nowMs + 3_600_000);

        // The stalled closing stream is aborted and its resources/port are reclaimed.
        Assert.True(netns.TryReserveTcpPort(clientPort));
        netns.ReleaseTcpPort(clientPort);
    }

    [Fact]
    public void LoopbackNamespace_PortAllocationWithManyUnboundUdpSockets_RemainsFastAndAccurate()
    {
        using var netns = LoopbackNetNamespace.Create(0x0A590002u, 24);
        var unboundUdp = new List<LoopbackNetNamespace.UdpDatagramSocket>(2048);
        try
        {
            for (var i = 0; i < 2048; i++)
                unboundUdp.Add(netns.CreateUdpSocket());

            using var boundUdp = netns.CreateUdpSocket();
            using var dupUdp = netns.CreateUdpSocket();
            boundUdp.Bind(50000);
            Assert.NotEqual(0, dupUdp.TryBind(50000));

            // Bound UDP port must not block TCP reservation on the same port, while ephemeral allocation skips it.
            Assert.True(netns.TryReserveTcpPort(50000));
            netns.ReleaseTcpPort(50000);

            for (var i = 0; i < 2000; i++)
            {
                Assert.True(netns.TryAllocateEphemeralPort(out var port));
                Assert.NotEqual((ushort)50000, port);
                netns.ReleaseTcpPort(port);
            }
        }
        finally
        {
            foreach (var sock in unboundUdp)
                sock.Dispose();
        }
    }

    private static void LoopUntil(LoopbackNetNamespace netns, Func<bool> condition, int timeoutMs)
    {
        for (var elapsed = 0; elapsed < timeoutMs; elapsed += 10)
        {
            if (condition())
                return;

            netns.Poll(elapsed);
        }

        throw new TimeoutException("Timed out waiting for netstack condition.");
    }
}