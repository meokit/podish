using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Fiberish.Core.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Podish.Core.Networking;
using Xunit;

namespace Fiberish.Tests.Podish;

public class PortForwardingTests
{
    [Fact]
    public void RelaySession_Buffering_Works()
    {
        // Arrange
        var mockStream = new MockNetstackStream();
        var hostSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var context = new ContainerNetworkContext("test", NetworkMode.Private, IPAddress.Any,
            (LoopbackNetNamespace)null!, null!);
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
        var context =
            new ContainerNetworkContext("test-container", NetworkMode.Private, netns.PrivateIpv4Address, netns, sw);
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
            var hostPort = 0;
            for (var i = 0; i < 50; i++)
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
        var context = new ContainerNetworkContext("test-container-wake", NetworkMode.Private, netns.PrivateIpv4Address,
            netns, sw);
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

            var hostPort = 0;
            for (var i = 0; i < 50; i++)
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
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return;
            netns.Poll(sw.ElapsedMilliseconds);
            Thread.Sleep(10);
        }

        throw new TimeoutException("Timed out waiting for netstack condition.");
    }

    // ==================================================================
    // Failure Path Tests
    // ==================================================================

    [Fact]
    public async Task PortForwardLoop_ConnectToClosedPort_HostGetsDisconnected()
    {
        // 1. Setup Guest Netstack WITHOUT a listener (port is closed)
        using var netns = LoopbackNetNamespace.Create(0x0A580003u, 24);
        var sw = new DummySwitch();
        var context = new ContainerNetworkContext("test-container-closed", NetworkMode.Private,
            netns.PrivateIpv4Address, netns, sw);
        sw.Attach(context);

        var loop = new PortForwardLoop(NullLogger<PortForwardLoop>.Instance);

        try
        {
            // 2. Start Port Forwarding on Host
            var spec = new PublishedPortSpec
            {
                HostPort = 0, // Dynamic
                ContainerPort = 18082, // Nothing listening here
                Protocol = TransportProtocol.Tcp
            };

            loop.StartPublishedPorts(context, [spec]);

            // Wait for listener to be ready
            var hostPort = 0;
            for (var i = 0; i < 50; i++)
            {
                if (loop.GetActivePorts(context.ContainerId).Any())
                {
                    hostPort = loop.GetActivePorts(context.ContainerId).First();
                    break;
                }

                await Task.Delay(20);
            }

            Assert.NotEqual(0, hostPort);

            // 3. Connect from Host - should accept but then disconnect when guest connection fails
            using var hostClient = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await hostClient.ConnectAsync("127.0.0.1", hostPort);

            // 4. Wait for the guest connection attempt to fail and host to be disconnected.
            // Avoid blocking Receive() with timeout here; use bounded polling to keep this
            // test deterministic across runtime/socket timing differences.
            var disconnected = false;
            for (var i = 0; i < 40; i++)
            {
                netns.Poll(i * 50);
                await Task.Delay(50);

                if (hostClient.Poll(0, SelectMode.SelectError))
                {
                    disconnected = true;
                    break;
                }

                if (hostClient.Poll(0, SelectMode.SelectRead))
                {
                    var buffer = new byte[32];
                    try
                    {
                        var read = hostClient.Receive(buffer);
                        if (read == 0)
                        {
                            disconnected = true; // EOF
                            break;
                        }
                    }
                    catch (SocketException)
                    {
                        disconnected = true; // reset/closed
                        break;
                    }
                }
            }

            Assert.True(disconnected, "Host connection should be closed when guest port is not listening");

            // 6. Teardown
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
    public async Task PortForwardLoop_TeardownWhileActiveConnection_CleanShutdown()
    {
        // 1. Setup Guest Netstack with listener
        using var netns = LoopbackNetNamespace.Create(0x0A580004u, 24);
        using var guestListener = netns.CreateTcpListener();
        guestListener.Listen(18083);

        var sw = new DummySwitch();
        var context = new ContainerNetworkContext("test-container-teardown", NetworkMode.Private,
            netns.PrivateIpv4Address, netns, sw);
        sw.Attach(context);

        var loop = new PortForwardLoop(NullLogger<PortForwardLoop>.Instance);

        try
        {
            // 2. Start Port Forwarding
            var spec = new PublishedPortSpec
            {
                HostPort = 0,
                ContainerPort = 18083,
                Protocol = TransportProtocol.Tcp
            };

            loop.StartPublishedPorts(context, [spec]);

            // Wait for listener
            var hostPort = 0;
            for (var i = 0; i < 50; i++)
            {
                if (loop.GetActivePorts(context.ContainerId).Any())
                {
                    hostPort = loop.GetActivePorts(context.ContainerId).First();
                    break;
                }

                await Task.Delay(20);
            }

            Assert.NotEqual(0, hostPort);

            // 3. Connect from Host
            using var hostClient = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await hostClient.ConnectAsync("127.0.0.1", hostPort);

            // 4. Accept on guest side
            LoopUntil(netns, () => guestListener.AcceptPending, 2000);
            using var guestAccepted = guestListener.Accept();
            LoopUntil(netns, () => guestAccepted.State == 4, 3000);

            // 5. Send some data to ensure connection is active
            var payload = Encoding.ASCII.GetBytes("active-data");
            await hostClient.SendAsync(payload, SocketFlags.None);

            LoopUntil(netns, () => guestAccepted.CanRead, 3000);
            var guestBuffer = new byte[32];
            var read = guestAccepted.Receive(guestBuffer);
            Assert.Equal("active-data", Encoding.ASCII.GetString(guestBuffer, 0, read));

            // 6. TEARDOWN while connection is active
            var stopTcs = new TaskCompletionSource();
            loop.StopPublishedPorts(context, stopTcs);

            // Should complete without hanging
            var completed = await Task.WhenAny(stopTcs.Task, Task.Delay(5000));
            Assert.Equal(stopTcs.Task, completed);
            await stopTcs.Task; // Will throw if faulted

            // 7. Host socket should be closed/disposed after teardown
            await Task.Delay(100);
            Assert.ThrowsAny<Exception>(() => hostClient.Send(Encoding.ASCII.GetBytes("should fail")));
        }
        finally
        {
            loop.Dispose();
        }
    }

    [Fact]
    public async Task PortForwardLoop_MultipleConnections_TeardownAllCleanly()
    {
        // 1. Setup Guest Netstack
        using var netns = LoopbackNetNamespace.Create(0x0A580005u, 24);
        using var guestListener = netns.CreateTcpListener();
        guestListener.Listen(18084);

        var sw = new DummySwitch();
        var context = new ContainerNetworkContext("test-container-multi", NetworkMode.Private, netns.PrivateIpv4Address,
            netns, sw);
        sw.Attach(context);

        var loop = new PortForwardLoop(NullLogger<PortForwardLoop>.Instance);

        try
        {
            // 2. Start Port Forwarding
            var spec = new PublishedPortSpec
            {
                HostPort = 0,
                ContainerPort = 18084,
                Protocol = TransportProtocol.Tcp
            };

            loop.StartPublishedPorts(context, [spec]);

            var hostPort = 0;
            for (var i = 0; i < 50; i++)
            {
                if (loop.GetActivePorts(context.ContainerId).Any())
                {
                    hostPort = loop.GetActivePorts(context.ContainerId).First();
                    break;
                }

                await Task.Delay(20);
            }

            Assert.NotEqual(0, hostPort);

            // 3. Establish multiple concurrent connections
            var hostClients = new List<Socket>();
            var guestSockets = new List<LoopbackNetNamespace.TcpStreamSocket>();

            for (var i = 0; i < 3; i++)
            {
                var hostClient = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await hostClient.ConnectAsync("127.0.0.1", hostPort);
                hostClients.Add(hostClient);

                LoopUntil(netns, () => guestListener.AcceptPending, 2000);
                var guestAccepted = guestListener.Accept();
                guestSockets.Add(guestAccepted);
                LoopUntil(netns, () => guestAccepted.State == 4, 3000);
            }

            Assert.Equal(3, hostClients.Count);

            // 4. Send data on all connections
            for (var i = 0; i < 3; i++)
            {
                var data = Encoding.ASCII.GetBytes($"msg-{i}");
                await hostClients[i].SendAsync(data, SocketFlags.None);
            }

            // 5. Verify all received
            for (var i = 0; i < 3; i++)
            {
                var buffer = new byte[32];
                LoopUntil(netns, () => guestSockets[i].CanRead, 3000);
                var read = guestSockets[i].Receive(buffer);
                Assert.Equal($"msg-{i}", Encoding.ASCII.GetString(buffer, 0, read));
            }

            // 6. TEARDOWN with multiple active connections
            var stopTcs = new TaskCompletionSource();
            loop.StopPublishedPorts(context, stopTcs);

            var completed = await Task.WhenAny(stopTcs.Task, Task.Delay(5000));
            Assert.Equal(stopTcs.Task, completed);
            await stopTcs.Task;

            // 7. All host sockets should be closed
            await Task.Delay(100);
            foreach (var client in hostClients)
            {
                Assert.ThrowsAny<Exception>(() => client.Send(Encoding.ASCII.GetBytes("fail")));
                client.Dispose();
            }

            foreach (var gs in guestSockets) gs.Dispose();
        }
        finally
        {
            loop.Dispose();
        }
    }

    // ==================================================================
    // Wake Callback Teardown Tests
    // ==================================================================

    [Fact]
    public async Task PortForwardLoop_Teardown_NoWakeCallbackCrashes()
    {
        // This test verifies that after StopPublishedPorts and Dispose,
        // no wake callbacks crash or cause issues
        using var netns = LoopbackNetNamespace.Create(0x0A580006u, 24);
        using var guestListener = netns.CreateTcpListener();
        guestListener.Listen(18085);

        var sw = new DummySwitch();
        var context = new ContainerNetworkContext("test-container-wake-teardown", NetworkMode.Private,
            netns.PrivateIpv4Address, netns, sw);
        sw.Attach(context);

        var loop = new PortForwardLoop(NullLogger<PortForwardLoop>.Instance);

        try
        {
            // Start and stop port forwarding to register/unregister wake callbacks
            var spec = new PublishedPortSpec
            {
                HostPort = 0,
                ContainerPort = 18085,
                Protocol = TransportProtocol.Tcp
            };

            loop.StartPublishedPorts(context, [spec]);

            // Wait for it to be active
            var hostPort = 0;
            for (var i = 0; i < 50; i++)
            {
                if (loop.GetActivePorts(context.ContainerId).Any())
                {
                    hostPort = loop.GetActivePorts(context.ContainerId).First();
                    break;
                }

                await Task.Delay(20);
            }

            Assert.NotEqual(0, hostPort);

            // Create some activity to trigger wake callbacks
            using var hostClient = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await hostClient.ConnectAsync("127.0.0.1", hostPort);

            LoopUntil(netns, () => guestListener.AcceptPending, 2000);
            using var guestAccepted = guestListener.Accept();
            LoopUntil(netns, () => guestAccepted.State == 4, 3000);

            // Trigger wake callback
            var payload = Encoding.ASCII.GetBytes("trigger-wake");
            guestAccepted.Send(payload);

            await Task.Delay(100);

            // Stop the port forwarding - this unregisters wake callbacks
            var stopTcs = new TaskCompletionSource();
            loop.StopPublishedPorts(context, stopTcs);
            await stopTcs.Task;

            // Now trigger more netstack activity after the wake callback should be unregistered
            // This should NOT crash or cause issues
            var morePayload = Encoding.ASCII.GetBytes("after-stop");
            guestAccepted.Send(morePayload);

            netns.Poll(100);

            // Dispose the loop
            loop.Dispose();

            // Trigger even more activity after dispose
            // The socket might be closed at this point, which is fine - we're just checking no crashes
            try
            {
                guestAccepted.Send(Encoding.ASCII.GetBytes("after-dispose"));
                netns.Poll(100);
            }
            catch
            {
                // Socket might be closed, that's acceptable
            }

            // If we get here without exceptions, the test passes
            Assert.True(true, "No crashes after wake callback teardown");
        }
        finally
        {
            // Ensure dispose is called even if test fails, but avoid double dispose
            try
            {
                loop.Dispose();
            }
            catch
            {
            }
        }
    }

    private class MockNetstackStream : INetstackStream
    {
        public bool IsClosed => false;
        public bool CanRead => true;
        public bool CanWrite => true;
        public bool MayRead => true;
        public bool MayWrite => true;

        public int Read(Span<byte> buffer)
        {
            return 0;
        }

        public int Write(ReadOnlySpan<byte> buffer)
        {
            return buffer.Length;
        }

        public void CloseWrite()
        {
        }

        public void Dispose()
        {
        }
    }
}