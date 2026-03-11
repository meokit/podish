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