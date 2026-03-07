using System.Net;
using System.Net.Sockets;
using System.Text;
using Fiberish.Core.Net;
using Moq;
using Podish.Core.Networking;
using Xunit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fiberish.Tests.Podish;

public class PortForwardingTests
{
    private class MockNetstackStream : INetstackStream
    {
        public bool CanRead => true;
        public bool CanWrite => true;
        public bool MayRead => true;
        public bool MayWrite => true;
        public bool IsClosed => false;
        public int Read(Span<byte> buffer) => 0;
        public int Write(ReadOnlySpan<byte> buffer) => buffer.Length;
        public void CloseWrite() { }
        public void Dispose() { }
    }

    [Fact]
    public void RelaySession_Buffering_Works()
    {
        // Arrange
        var mockStream = new MockNetstackStream();
        var hostSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var context = new ContainerNetworkContext("test", NetworkMode.Private, IPAddress.Any, null!, null!);
        using var session = new RelaySession(1, hostSocket, mockStream, context);

        var data = Encoding.ASCII.GetBytes("hello");
        data.CopyTo(session.HostToGuestBuffer, 0);
        session.HostToGuestCount = data.Length;

        // Act & Assert
        var written = session.GuestStream.Write(session.HostToGuestBuffer.AsSpan(0, session.HostToGuestCount));
        Assert.Equal(data.Length, written);
    }

    [Fact]
    public async Task PortForwardLoop_Integration_RelaysData()
    {
        // 1. Setup Guest Netstack
        using var netns = LoopbackNetNamespace.Create(0x0A580001u, 24);
        using var guestListener = netns.CreateTcpListener();
        guestListener.Listen(18080);

        var sw = new DummySwitch();
        var context = new ContainerNetworkContext("test-container", NetworkMode.Private, netns.PrivateIpv4Address, netns, sw);
        sw.Attach(context);

        // 2. Start PortForwardLoop
        var loop = new PortForwardLoop(NullLogger<PortForwardLoop>.Instance);

        try
        {
            // 3. Start Port Forwarding on Host
            var spec = new PublishedPortSpec
            {
                HostPort = 0, // Dynamic
                ContainerPort = 18080,
                Protocol = TransportProtocol.Tcp
            };
            
            loop.StartPublishedPorts(context, [spec]);

            // Wait for listener to be ready and get the port
            int hostPort = 0;
            for (int i = 0; i < 50; i++)
            {
                if (loop.GetActivePorts(context.ContainerId).Any())
                {
                    hostPort = loop.GetActivePorts(context.ContainerId).First();
                    break;
                }
                await Task.Delay(20);
            }
            Assert.NotEqual(0, hostPort);

            // 4. Connect from Host
            using var hostClient = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await hostClient.ConnectAsync("127.0.0.1", hostPort);

            // 5. Guest Accept
            LoopUntil(netns, () => guestListener.AcceptPending, 2000);
            using var guestAccepted = guestListener.Accept();
            
            // Wait for handshake
            LoopUntil(netns, () => guestAccepted.State == 4, 3000);

            // 6. Data Transfer: Host -> Guest
            var payload = Encoding.ASCII.GetBytes("hello-guest");
            await hostClient.SendAsync(payload, SocketFlags.None);

            var guestBuffer = new byte[32];
            LoopUntil(netns, () => guestAccepted.CanRead, 3000);
            var read = guestAccepted.Receive(guestBuffer);
            Assert.Equal("hello-guest", Encoding.ASCII.GetString(guestBuffer, 0, read));

            // 7. Data Transfer: Guest -> Host
            var response = Encoding.ASCII.GetBytes("hello-host");
            guestAccepted.Send(response);

            var hostBuffer = new byte[32];
            var hostRead = await hostClient.ReceiveAsync(hostBuffer, SocketFlags.None);
            Assert.Equal("hello-host", Encoding.ASCII.GetString(hostBuffer, 0, hostRead));

            // 8. Teardown
            var stopTcs = new TaskCompletionSource();
            loop.StopPublishedPorts(context, stopTcs);
            await stopTcs.Task;
        }
        finally
        {
            loop.Dispose();
        }
    }


    [Fact]
    public async Task PortForwardLoop_GuestWriteWithoutHostEvent_WakesRelay()
    {
        // 1. Setup Guest Netstack
        using var netns = LoopbackNetNamespace.Create(0x0A580001u, 24);
        using var guestListener = netns.CreateTcpListener();
        guestListener.Listen(18081);

        var sw = new DummySwitch();
        var context = new ContainerNetworkContext("test-container-wake", NetworkMode.Private, netns.PrivateIpv4Address, netns, sw);
        sw.Attach(context);

        // 2. Start PortForwardLoop
        var loop = new PortForwardLoop(NullLogger<PortForwardLoop>.Instance);

        try
        {
            // 3. Start Port Forwarding on Host
            var spec = new PublishedPortSpec
            {
                HostPort = 0, // Dynamic
                ContainerPort = 18081,
                Protocol = TransportProtocol.Tcp
            };
            
            loop.StartPublishedPorts(context, [spec]);

            int hostPort = 0;
            for (int i = 0; i < 50; i++)
            {
                if (loop.GetActivePorts(context.ContainerId).Any())
                {
                    hostPort = loop.GetActivePorts(context.ContainerId).First();
                    break;
                }
                await Task.Delay(20);
            }
            Assert.NotEqual(0, hostPort);

            // 4. Connect from Host
            using var hostClient = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await hostClient.ConnectAsync("127.0.0.1", hostPort);

            // 5. Guest Accept
            LoopUntil(netns, () => guestListener.AcceptPending, 2000);
            using var guestAccepted = guestListener.Accept();
            
            // Wait for handshake
            LoopUntil(netns, () => guestAccepted.State == 4, 3000);

            // Give the loop a moment to settle into a deep wait
            await Task.Delay(200); 

            // 6. Data Transfer: Guest -> Host (Crucial part: no host event!)
            var response = Encoding.ASCII.GetBytes("hello-waker");
            
            // Send data from guest. This should trigger `state.notify()` -> C# callback -> `_wakeSignal.Set()`
            guestAccepted.Send(response);

            // Host receives data without having sent anything first.
            // If the loop is sleeping forever, this will timeout and fail the test.
            var hostBuffer = new byte[32];
            
            var receiveTask = hostClient.ReceiveAsync(hostBuffer, SocketFlags.None);
            var timeoutTask = Task.Delay(2000);
            
            var completed = await Task.WhenAny(receiveTask, timeoutTask);
            Assert.Equal(receiveTask, completed); // Ensures it didn't timeout
            
            var hostRead = await receiveTask;
            Assert.Equal("hello-waker", Encoding.ASCII.GetString(hostBuffer, 0, hostRead));

            // 7. Teardown
            var stopTcs = new TaskCompletionSource();
            loop.StopPublishedPorts(context, stopTcs);
            await stopTcs.Task;
        }
        finally
        {
            loop.Dispose();
        }
    }

    private static void LoopUntil(LoopbackNetNamespace netns, Func<bool> condition, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return;
            netns.Poll(sw.ElapsedMilliseconds);
            Thread.Sleep(10);
        }
        throw new TimeoutException("Timed out waiting for netstack condition.");
    }
}
