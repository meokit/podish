using System.Reflection;
using System.Text;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Core;

public sealed class VirtualDaemonTests
{
    [Fact]
    public async Task VirtualDaemon_EchoesOverUnixSocket()
    {
        using var env = new TestEnv();
        var payload = Encoding.UTF8.GetBytes("ping-from-guest");
        var step = "before invoke";

        var result = await env.InvokeOnSchedulerAsync(async () =>
        {
            step = "spawn daemon";
            var runtime = env.Registry.Spawn(new EchoVirtualDaemon("/virt-echo.sock"));

            Assert.Equal(ProcessKind.VirtualDaemon, runtime.Process.Kind);
            Assert.Equal(TaskExecutionMode.HostService, runtime.Task.ExecutionMode);
            Assert.Equal("/virt-echo.sock", runtime.Daemon.UnixPath);

            step = "create client";
            var client = env.CreateClientTask("virt-client");
            var clientSocket = new UnixSocketInode(
                0,
                client.Syscalls.MemfdSuperBlock,
                System.Net.Sockets.SocketType.Stream,
                client.Task.CommonKernel);
            var clientFile = new LinuxFile(
                new Dentry($"socket:[{clientSocket.Ino}]", clientSocket, null, client.Syscalls.MemfdSuperBlock),
                FileFlags.O_RDWR,
                client.Syscalls.AnonMount);

            var endpoint = new UnixSockaddrInfo
            {
                IsAbstract = false,
                Path = "/virt-echo.sock",
                SunPathRaw = Encoding.UTF8.GetBytes("/virt-echo.sock\0")
            };

            step = "connect";
            var connectRc = await clientSocket.ConnectAsync(clientFile, client.Task, endpoint);
            Assert.Equal(0, connectRc);

            step = "send";
            var sent = await clientSocket.SendAsync(clientFile, client.Task, payload, 0);
            Assert.Equal(payload.Length, sent);

            step = "recv";
            var recvBuffer = new byte[128];
            var received = await clientSocket.RecvAsync(clientFile, client.Task, recvBuffer, 0, recvBuffer.Length);
            Assert.Equal(payload.Length, received);

            step = "close";
            clientFile.Close();
            return Encoding.UTF8.GetString(recvBuffer, 0, received);
        }, () => step);

        Assert.Equal("ping-from-guest", result);
    }

    [Fact]
    public async Task VirtualDaemon_ReceivesMemfdAndReadsItsData()
    {
        using var env = new TestEnv();
        const string expected = "hello-from-memfd";
        var step = "before invoke";

        var result = await env.InvokeOnSchedulerAsync(async () =>
        {
            step = "spawn daemon";
            env.Registry.Spawn(new MemfdReaderVirtualDaemon("/virt-memfd.sock"));

            step = "create client";
            var client = env.CreateClientTask("virt-client-memfd");
            var clientSocket = new UnixSocketInode(
                0,
                client.Syscalls.MemfdSuperBlock,
                System.Net.Sockets.SocketType.Stream,
                client.Task.CommonKernel);
            var clientFile = new LinuxFile(
                new Dentry($"socket:[{clientSocket.Ino}]", clientSocket, null, client.Syscalls.MemfdSuperBlock),
                FileFlags.O_RDWR,
                client.Syscalls.AnonMount);

            var endpoint = new UnixSockaddrInfo
            {
                IsAbstract = false,
                Path = "/virt-memfd.sock",
                SunPathRaw = Encoding.UTF8.GetBytes("/virt-memfd.sock\0")
            };

            step = "connect";
            var connectRc = await clientSocket.ConnectAsync(clientFile, client.Task, endpoint);
            Assert.Equal(0, connectRc);

            step = "create memfd";
            var memfd = env.CreateMemfdLikeFile(client.Syscalls, "payload", expected);
            step = "sendmsg";
            var sent = await clientSocket.SendMsgAsync(clientFile, client.Task, [], [memfd], 0, null);
            Assert.Equal(0, sent);

            step = "recv ack";
            var ackBuffer = new byte[64];
            var ackBytes = await clientSocket.RecvAsync(clientFile, client.Task, ackBuffer, 0, ackBuffer.Length);
            Assert.True(ackBytes > 0);

            step = "close";
            memfd.Close();
            clientFile.Close();
            return Encoding.UTF8.GetString(ackBuffer, 0, ackBytes);
        }, () => step);

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task VirtualDaemon_SendMsgWithoutAncillaryData_WorksOverStreamSocket()
    {
        using var env = new TestEnv();
        var payload = Encoding.UTF8.GetBytes("ping-sendmsg-no-fds");
        var step = "before invoke";
        var daemonStep = "daemon init";

        var result = await env.InvokeOnSchedulerAsync(async () =>
        {
            step = "spawn daemon";
            env.Registry.Spawn(new SendMsgEchoVirtualDaemon("/virt-sendmsg.sock", s => daemonStep = s));

            step = "create client";
            var client = env.CreateClientTask("virt-client-sendmsg");
            var clientSocket = new UnixSocketInode(
                0,
                client.Syscalls.MemfdSuperBlock,
                System.Net.Sockets.SocketType.Stream,
                client.Task.CommonKernel);
            var clientFile = new LinuxFile(
                new Dentry($"socket:[{clientSocket.Ino}]", clientSocket, null, client.Syscalls.MemfdSuperBlock),
                FileFlags.O_RDWR,
                client.Syscalls.AnonMount);

            var endpoint = new UnixSockaddrInfo
            {
                IsAbstract = false,
                Path = "/virt-sendmsg.sock",
                SunPathRaw = Encoding.UTF8.GetBytes("/virt-sendmsg.sock\0")
            };

            step = "connect";
            var connectRc = await clientSocket.ConnectAsync(clientFile, client.Task, endpoint);
            Assert.Equal(0, connectRc);

            step = "sendmsg";
            var sent = await clientSocket.SendMsgAsync(clientFile, client.Task, payload, null, 0, null);
            Assert.Equal(payload.Length, sent);

            step = "recv";
            var recvBuffer = new byte[128];
            var recv = await ((ISocketDataOps)clientSocket).RecvMsgAsync(clientFile, client.Task, recvBuffer, 0, 128);
            Assert.Equal(payload.Length, recv.BytesRead);
            Assert.True(recv.Fds == null || recv.Fds.Count == 0);

            clientFile.Close();
            return Encoding.UTF8.GetString(recvBuffer, 0, recv.BytesRead);
        }, () => $"{step} / {daemonStep}");

        Assert.Equal("ping-sendmsg-no-fds", result);
    }

    [Fact]
    public async Task VirtualDaemon_ClientSendMsgWithoutAncillary_DaemonCanReceiveAndReplyWithSend()
    {
        using var env = new TestEnv();
        var payload = Encoding.UTF8.GetBytes("client-sendmsg");
        var step = "before invoke";
        var daemonStep = "daemon init";

        var result = await env.InvokeOnSchedulerAsync(async () =>
        {
            step = "spawn daemon";
            env.Registry.Spawn(new ReceiveMsgReplySendVirtualDaemon("/virt-sendmsg-in.sock", s => daemonStep = s));

            step = "create client";
            var client = env.CreateClientTask("virt-client-sendmsg-in");
            var clientSocket = new UnixSocketInode(
                0,
                client.Syscalls.MemfdSuperBlock,
                System.Net.Sockets.SocketType.Stream,
                client.Task.CommonKernel);
            var clientFile = new LinuxFile(
                new Dentry($"socket:[{clientSocket.Ino}]", clientSocket, null, client.Syscalls.MemfdSuperBlock),
                FileFlags.O_RDWR,
                client.Syscalls.AnonMount);

            var endpoint = new UnixSockaddrInfo
            {
                IsAbstract = false,
                Path = "/virt-sendmsg-in.sock",
                SunPathRaw = Encoding.UTF8.GetBytes("/virt-sendmsg-in.sock\0")
            };

            step = "connect";
            Assert.Equal(0, await clientSocket.ConnectAsync(clientFile, client.Task, endpoint));

            step = "sendmsg";
            Assert.Equal(payload.Length, await clientSocket.SendMsgAsync(clientFile, client.Task, payload, null, 0, null));

            step = "recv";
            var recvBuffer = new byte[128];
            var recv = await clientSocket.RecvAsync(clientFile, client.Task, recvBuffer, 0, 128);
            Assert.True(recv > 0);
            clientFile.Close();
            return Encoding.UTF8.GetString(recvBuffer, 0, recv);
        }, () => $"{step} / {daemonStep}");

        Assert.Equal("client-sendmsg", result);
    }

    [Fact]
    public async Task VirtualDaemon_DaemonSendMsgWithoutAncillary_ClientCanReceive()
    {
        using var env = new TestEnv();
        var payload = Encoding.UTF8.GetBytes("daemon-sendmsg");
        var step = "before invoke";
        var daemonStep = "daemon init";
        VirtualDaemonRuntime? runtime = null;

        var result = await env.InvokeOnSchedulerAsync(async () =>
        {
            step = "spawn daemon";
            runtime = env.Registry.Spawn(new ReceiveReplySendMsgVirtualDaemon("/virt-sendmsg-out.sock", s => daemonStep = s));

            step = "create client";
            var client = env.CreateClientTask("virt-client-sendmsg-out");
            var clientSocket = new UnixSocketInode(
                0,
                client.Syscalls.MemfdSuperBlock,
                System.Net.Sockets.SocketType.Stream,
                client.Task.CommonKernel);
            var clientFile = new LinuxFile(
                new Dentry($"socket:[{clientSocket.Ino}]", clientSocket, null, client.Syscalls.MemfdSuperBlock),
                FileFlags.O_RDWR,
                client.Syscalls.AnonMount);

            var endpoint = new UnixSockaddrInfo
            {
                IsAbstract = false,
                Path = "/virt-sendmsg-out.sock",
                SunPathRaw = Encoding.UTF8.GetBytes("/virt-sendmsg-out.sock\0")
            };

            step = "connect";
            Assert.Equal(0, await clientSocket.ConnectAsync(clientFile, client.Task, endpoint));

            step = "send";
            Assert.Equal(payload.Length, await clientSocket.SendAsync(clientFile, client.Task, payload, 0));

            step = "recv";
            if (runtime?.LastScheduledFailure != null)
                throw new InvalidOperationException("Virtual daemon child task failed.", runtime.LastScheduledFailure);
            var recvBuffer = new byte[128];
            var recv = await clientSocket.RecvAsync(clientFile, client.Task, recvBuffer, 0, 128);
            Assert.True(recv > 0);
            clientFile.Close();
            return Encoding.UTF8.GetString(recvBuffer, 0, recv);
        }, () => $"{step} / {daemonStep} / failure={(runtime?.LastScheduledFailure?.GetType().Name ?? "none")}");

        Assert.Equal("daemon-sendmsg", result);
    }

    [Fact]
    public async Task VirtualDaemon_NonBlockingReadExact_MustReceiveSecondFrameAfterSendingControlPacket()
    {
        using var env = new TestEnv();
        var step = "before invoke";

        var result = await env.InvokeOnSchedulerAsync(async () =>
        {
            step = "spawn daemon";
            env.Registry.Spawn(new InterleavedFrameVirtualDaemon("/virt-frames.sock"));

            step = "create client";
            var client = env.CreateClientTask("virt-client-frames");
            var clientSocket = new UnixSocketInode(
                0,
                client.Syscalls.MemfdSuperBlock,
                System.Net.Sockets.SocketType.Stream,
                client.Task.CommonKernel);
            var clientFile = new LinuxFile(
                new Dentry($"socket:[{clientSocket.Ino}]", clientSocket, null, client.Syscalls.MemfdSuperBlock),
                FileFlags.O_RDWR,
                client.Syscalls.AnonMount);

            var endpoint = new UnixSockaddrInfo
            {
                IsAbstract = false,
                Path = "/virt-frames.sock",
                SunPathRaw = Encoding.UTF8.GetBytes("/virt-frames.sock\0")
            };

            step = "connect";
            var connectRc = await clientSocket.ConnectAsync(clientFile, client.Task, endpoint);
            Assert.Equal(0, connectRc);

            step = "send frame 1";
            Assert.Equal(20, await clientSocket.SendAsync(clientFile, client.Task, new byte[20], 0));
            Assert.Equal(16384, await clientSocket.SendAsync(clientFile, client.Task, new byte[16384], 0));

            step = "recv control";
            var control = new byte[20];
            var controlRead = await clientSocket.RecvAsync(clientFile, client.Task, control, 0, control.Length);
            Assert.Equal(control.Length, controlRead);

            step = "send frame 2";
            Assert.Equal(20, await clientSocket.SendAsync(clientFile, client.Task, new byte[20], 0));
            Assert.Equal(16384, await clientSocket.SendAsync(clientFile, client.Task, new byte[16384], 0));

            step = "recv ack";
            var ack = new byte[1];
            var ackRead = await clientSocket.RecvAsync(clientFile, client.Task, ack, 0, ack.Length);
            Assert.Equal(1, ackRead);

            step = "close";
            clientFile.Close();
            return ack[0];
        }, () => step);

        Assert.Equal((byte)1, result);
    }

    [Fact]
    public async Task VirtualDaemon_ScheduleChild_MustIsolateConnectionContinuationFromAcceptLoop()
    {
        using var env = new TestEnv();
        var step = "before invoke";

        var result = await env.InvokeOnSchedulerAsync(async () =>
        {
            step = "spawn daemon";
            env.Registry.Spawn(new ChildTaskFrameVirtualDaemon("/virt-child-frames.sock"));

            step = "create client";
            var client = env.CreateClientTask("virt-client-child-frames");
            var clientSocket = new UnixSocketInode(
                0,
                client.Syscalls.MemfdSuperBlock,
                System.Net.Sockets.SocketType.Stream,
                client.Task.CommonKernel);
            var clientFile = new LinuxFile(
                new Dentry($"socket:[{clientSocket.Ino}]", clientSocket, null, client.Syscalls.MemfdSuperBlock),
                FileFlags.O_RDWR,
                client.Syscalls.AnonMount);

            var endpoint = new UnixSockaddrInfo
            {
                IsAbstract = false,
                Path = "/virt-child-frames.sock",
                SunPathRaw = Encoding.UTF8.GetBytes("/virt-child-frames.sock\0")
            };

            step = "connect";
            var connectRc = await clientSocket.ConnectAsync(clientFile, client.Task, endpoint);
            Assert.Equal(0, connectRc);

            step = "send frame 1";
            Assert.Equal(20, await clientSocket.SendAsync(clientFile, client.Task, new byte[20], 0));
            Assert.Equal(16384, await clientSocket.SendAsync(clientFile, client.Task, new byte[16384], 0));

            step = "recv control";
            var control = new byte[20];
            var controlRead = await clientSocket.RecvAsync(clientFile, client.Task, control, 0, control.Length);
            Assert.Equal(control.Length, controlRead);

            step = "send frame 2";
            Assert.Equal(20, await clientSocket.SendAsync(clientFile, client.Task, new byte[20], 0));
            Assert.Equal(16384, await clientSocket.SendAsync(clientFile, client.Task, new byte[16384], 0));

            step = "recv ack";
            var ack = new byte[1];
            var ackRead = await clientSocket.RecvAsync(clientFile, client.Task, ack, 0, ack.Length);
            Assert.Equal(1, ackRead);

            step = "close";
            clientFile.Close();
            return ack[0];
        }, () => step);

        Assert.Equal((byte)1, result);
    }

    private sealed class EchoVirtualDaemon : IVirtualDaemon
    {
        public EchoVirtualDaemon(string unixPath)
        {
            UnixPath = unixPath;
            Name = "virt-echo";
        }

        public string Name { get; }
        public string UnixPath { get; }

        public void OnStart(VirtualDaemonContext context)
        {
            context.Schedule(async ctx =>
            {
                var (rc, connection) = await ctx.AcceptAsync();
                Assert.Equal(0, rc);
                Assert.NotNull(connection);

                using (connection!)
                {
                    var buffer = new byte[256];
                    var bytes = await connection.RecvAsync(buffer, 0, buffer.Length);
                    if (bytes > 0)
                        await connection.SendAsync(buffer.AsMemory(0, bytes), 0);
                }

                ctx.Exit(0);
            });
        }

        public void OnSignal(VirtualDaemonContext context, int signo)
        {
            context.Exit(128 + signo);
        }

        public void OnStop(VirtualDaemonContext context)
        {
        }
    }

    private sealed class MemfdReaderVirtualDaemon : IVirtualDaemon
    {
        public MemfdReaderVirtualDaemon(string unixPath)
        {
            UnixPath = unixPath;
            Name = "virt-memfd-reader";
        }

        public string Name { get; }
        public string UnixPath { get; }

        public void OnStart(VirtualDaemonContext context)
        {
            context.Schedule(async ctx =>
            {
                var (rc, connection) = await ctx.AcceptAsync();
                Assert.Equal(0, rc);
                Assert.NotNull(connection);

                using (connection!)
                {
                    var recv = await connection.RecvMsgAsync(new byte[1], 0, 1);
                    Assert.Equal(0, recv.BytesRead);
                    Assert.NotNull(recv.Fds);
                    var file = Assert.Single(recv.Fds!);

                    try
                    {
                        var readBuffer = new byte[256];
                        var inode = Assert.IsAssignableFrom<Inode>(file.OpenedInode);
                        var bytes = inode.Read(file, readBuffer, 0);
                        Assert.True(bytes > 0);
                        await connection.SendAsync(readBuffer.AsMemory(0, bytes), 0);
                    }
                    finally
                    {
                        file.Close();
                    }
                }

                ctx.Exit(0);
            });
        }

        public void OnSignal(VirtualDaemonContext context, int signo)
        {
            context.Exit(128 + signo);
        }

        public void OnStop(VirtualDaemonContext context)
        {
        }
    }

    private sealed class SendMsgEchoVirtualDaemon : IVirtualDaemon
    {
        private readonly Action<string> _setStep;

        public SendMsgEchoVirtualDaemon(string unixPath, Action<string> setStep)
        {
            UnixPath = unixPath;
            Name = "virt-sendmsg-echo";
            _setStep = setStep;
        }

        public string Name { get; }
        public string UnixPath { get; }

        public void OnStart(VirtualDaemonContext context)
        {
            context.Schedule(async ctx =>
            {
                _setStep("daemon accept");
                var (rc, connection) = await ctx.AcceptAsync();
                Assert.Equal(0, rc);
                Assert.NotNull(connection);

                using (connection!)
                {
                    _setStep("daemon recvmsg");
                    var recvBuffer = new byte[128];
                    var recv = await connection.RecvMsgAsync(recvBuffer, 0, 128);
                    Assert.True(recv.BytesRead > 0);
                    Assert.True(recv.Fds == null || recv.Fds.Count == 0);

                    _setStep("daemon sendmsg");
                    var echo = recvBuffer[..recv.BytesRead];
                    var sent = await connection.SendMsgAsync(echo, null, 0, null);
                    Assert.Equal(echo.Length, sent);
                    _setStep("daemon done");
                }
            });
        }

        public void OnSignal(VirtualDaemonContext context, int signo)
        {
            context.Exit(128 + signo);
        }

        public void OnStop(VirtualDaemonContext context)
        {
        }
    }

    private sealed class ReceiveMsgReplySendVirtualDaemon : IVirtualDaemon
    {
        private readonly Action<string> _setStep;

        public ReceiveMsgReplySendVirtualDaemon(string unixPath, Action<string> setStep)
        {
            UnixPath = unixPath;
            Name = "virt-sendmsg-in";
            _setStep = setStep;
        }

        public string Name { get; }
        public string UnixPath { get; }

        public void OnStart(VirtualDaemonContext context)
        {
            context.Schedule(async ctx =>
            {
                _setStep("daemon accept");
                var (rc, connection) = await ctx.AcceptAsync();
                Assert.Equal(0, rc);
                Assert.NotNull(connection);

                using (connection!)
                {
                    _setStep("daemon recvmsg");
                    var recvBuffer = new byte[128];
                    var recv = await connection.RecvMsgAsync(recvBuffer, 0, 128);
                    Assert.True(recv.BytesRead > 0);
                    Assert.True(recv.Fds == null || recv.Fds.Count == 0);
                    _setStep("daemon send");
                    var sent = await connection.SendAsync(recvBuffer.AsMemory(0, recv.BytesRead), 0);
                    Assert.Equal(recv.BytesRead, sent);
                    _setStep("daemon done");
                }
            });
        }

        public void OnSignal(VirtualDaemonContext context, int signo) => context.Exit(128 + signo);
        public void OnStop(VirtualDaemonContext context) { }
    }

    private sealed class ReceiveReplySendMsgVirtualDaemon : IVirtualDaemon
    {
        private readonly Action<string> _setStep;

        public ReceiveReplySendMsgVirtualDaemon(string unixPath, Action<string> setStep)
        {
            UnixPath = unixPath;
            Name = "virt-sendmsg-out";
            _setStep = setStep;
        }

        public string Name { get; }
        public string UnixPath { get; }

        public void OnStart(VirtualDaemonContext context)
        {
            context.Schedule(async ctx =>
            {
                _setStep("daemon accept");
                var (rc, connection) = await ctx.AcceptAsync();
                Assert.Equal(0, rc);
                Assert.NotNull(connection);

                using (connection!)
                {
                    _setStep("daemon recv");
                    var recvBuffer = new byte[128];
                    var recv = await connection.RecvAsync(recvBuffer, 0, 128);
                    Assert.True(recv > 0);
                    var acceptedSocket = Assert.IsType<UnixSocketInode>(connection.File.OpenedInode);
                    var acceptedState = acceptedSocket.GetDebugState();
                    var clientPeer = (UnixSocketInode?)typeof(UnixSocketInode)
                        .GetField("_peer", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .GetValue(acceptedSocket);
                    var peerState = clientPeer?.GetDebugState();
                    _setStep($"daemon sendmsg pre accepted={acceptedState} peer={peerState}");
                    var sent = await connection.SendMsgAsync(recvBuffer[..recv], null, 0, null);
                    Assert.Equal(recv, sent);
                    _setStep($"daemon done accepted={acceptedSocket.GetDebugState()} peer={clientPeer?.GetDebugState()}");
                }
            });
        }

        public void OnSignal(VirtualDaemonContext context, int signo) => context.Exit(128 + signo);
        public void OnStop(VirtualDaemonContext context) { }
    }

    private sealed class InterleavedFrameVirtualDaemon : IVirtualDaemon
    {
        public InterleavedFrameVirtualDaemon(string unixPath)
        {
            UnixPath = unixPath;
            Name = "virt-frames";
        }

        public string Name { get; }
        public string UnixPath { get; }

        public void OnStart(VirtualDaemonContext context)
        {
            context.Schedule(async ctx =>
            {
                var (rc, connection) = await ctx.AcceptAsync();
                Assert.Equal(0, rc);
                Assert.NotNull(connection);

                using (connection!)
                {
                    connection.File.Flags |= FileFlags.O_NONBLOCK;

                    await ReadExactAsync(connection, 20);
                    await ReadExactAsync(connection, 16384);

                    Assert.Equal(20, await connection.SendAsync(new byte[20], 0));

                    await ReadExactAsync(connection, 20);
                    await ReadExactAsync(connection, 16384);

                    Assert.Equal(1, await connection.SendAsync(new byte[] { 1 }, 0));
                }

                ctx.Exit(0);
            });
        }

        public void OnSignal(VirtualDaemonContext context, int signo)
        {
            context.Exit(128 + signo);
        }

        public void OnStop(VirtualDaemonContext context)
        {
        }

        private static async Task ReadExactAsync(VirtualDaemonConnection connection, int bytesNeeded)
        {
            var buffer = new byte[bytesNeeded];
            var total = 0;
            while (total < bytesNeeded)
            {
                var scratch = new byte[bytesNeeded - total];
                var read = await connection.RecvAsync(scratch, 0, scratch.Length);
                if (read == -(int)Native.Errno.EAGAIN || read == -(int)Native.Errno.EINTR)
                {
                    await new SleepAwaitable(1, connection.Task, connection.Runtime.Scheduler);
                    continue;
                }

                Assert.True(read > 0, $"Unexpected read result {read} while waiting for {bytesNeeded} bytes.");
                Buffer.BlockCopy(scratch, 0, buffer, total, read);
                total += read;
            }
        }
    }

    private sealed class ChildTaskFrameVirtualDaemon : IVirtualDaemon
    {
        public ChildTaskFrameVirtualDaemon(string unixPath)
        {
            UnixPath = unixPath;
            Name = "virt-child-frames";
        }

        public string Name { get; }
        public string UnixPath { get; }

        public void OnStart(VirtualDaemonContext context)
        {
            context.Schedule(async ctx =>
            {
                var (rc, connection) = await ctx.AcceptAsync();
                Assert.Equal(0, rc);
                Assert.NotNull(connection);

                ctx.ScheduleChild(async childCtx =>
                {
                    connection!.BindTask(childCtx.Task);
                    using (connection)
                    {
                        connection.File.Flags |= FileFlags.O_NONBLOCK;

                        await ReadExactAsync(connection, 20);
                        await ReadExactAsync(connection, 16384);

                        Assert.Equal(20, await connection.SendAsync(new byte[20], 0));

                        await ReadExactAsync(connection, 20);
                        await ReadExactAsync(connection, 16384);

                        Assert.Equal(1, await connection.SendAsync(new byte[] { 1 }, 0));
                    }

                    ctx.Exit(0);
                });

                while (!ctx.Task.Exited)
                    await new SleepAwaitable(25, ctx.Task, ctx.Scheduler);
            });
        }

        public void OnSignal(VirtualDaemonContext context, int signo)
        {
            context.Exit(128 + signo);
        }

        public void OnStop(VirtualDaemonContext context)
        {
        }

        private static async Task ReadExactAsync(VirtualDaemonConnection connection, int bytesNeeded)
        {
            var buffer = new byte[bytesNeeded];
            var total = 0;
            while (total < bytesNeeded)
            {
                var scratch = new byte[bytesNeeded - total];
                var read = await connection.RecvAsync(scratch, 0, scratch.Length);
                if (read == -(int)Native.Errno.EAGAIN || read == -(int)Native.Errno.EINTR)
                {
                    await new SleepAwaitable(1, connection.Task, connection.Runtime.Scheduler);
                    continue;
                }

                Assert.True(read > 0, $"Unexpected read result {read} while waiting for {bytesNeeded} bytes.");
                Buffer.BlockCopy(scratch, 0, buffer, total, read);
                total += read;
            }
        }
    }

    private sealed class TestEnv : IDisposable
    {
        private static readonly FieldInfo OwnerThreadIdField =
            typeof(KernelScheduler).GetField("_ownerThreadId", BindingFlags.Instance | BindingFlags.NonPublic)!;

        private readonly ClientTaskHandle _anchor;
        private readonly List<ClientTaskHandle> _clients = [];
        private Exception? _schedulerFailure;
        private readonly Thread _schedulerThread;

        public TestEnv()
        {
            Runtime = KernelRuntime.BootstrapBare(false);
            Scheduler = new KernelScheduler();

            var rootFs = new Tmpfs();
            var rootSb = rootFs.ReadSuper(new FileSystemType { Name = "tmpfs" }, 0, "", null);
            Runtime.Syscalls.MountRoot(rootSb, new SyscallManager.RootMountOptions
            {
                Source = "tmpfs",
                FsType = "tmpfs",
                Options = "rw"
            });

            Registry = new VirtualDaemonRegistry(Runtime.Syscalls, Scheduler);
            _anchor = CreateClientTaskCore("scheduler-anchor", track: false);
            _anchor.Task.Status = FiberTaskStatus.Waiting;
            _schedulerThread = new Thread(() =>
            {
                try
                {
                    ResetSchedulerThreadBinding();
                    Scheduler.Running = true;
                    Scheduler.Run();
                }
                catch (Exception ex)
                {
                    _schedulerFailure = ex;
                }
                finally
                {
                    ResetSchedulerThreadBinding();
                }
            })
            {
                IsBackground = true,
                Name = "VirtualDaemonTests.KernelScheduler"
            };
            _schedulerThread.Start();
            Assert.True(WaitForSchedulerReady(TimeSpan.FromSeconds(5)));
        }

        public KernelRuntime Runtime { get; }
        public KernelScheduler Scheduler { get; }
        public VirtualDaemonRegistry Registry { get; }

        public void Dispose()
        {
            Scheduler.Running = false;
            Scheduler.WakeUp();
            Assert.True(_schedulerThread.Join(TimeSpan.FromSeconds(5)));

            foreach (var client in _clients)
            {
                client.Syscalls.Close();
                client.Engine.Dispose();
            }

            _anchor.Syscalls.Close();
            _anchor.Engine.Dispose();

            Runtime.Syscalls.Close();
            Runtime.Engine.Dispose();
        }

        public async Task<T> InvokeOnSchedulerAsync<T>(Func<ValueTask<T>> action, Func<string>? stepProvider = null)
        {
            if (_schedulerFailure != null)
                throw new InvalidOperationException("Virtual daemon scheduler thread failed.", _schedulerFailure);

            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            Scheduler.ScheduleFromAnyThread(() => StartScheduledAction(action, tcs));

            try
            {
                return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException) when (_schedulerFailure != null)
            {
                throw new InvalidOperationException("Virtual daemon scheduler thread failed.", _schedulerFailure);
            }
            catch (TimeoutException ex)
            {
                var step = stepProvider?.Invoke();
                throw new TimeoutException(
                    step == null ? ex.Message : $"The operation timed out at step '{step}'.", ex);
            }
        }

        public ClientTaskHandle CreateClientTask(string name)
        {
            Scheduler.AssertSchedulerThread();
            return CreateClientTaskCore(name, track: true);
        }

        private ClientTaskHandle CreateClientTaskCore(string name, bool track)
        {
            var pid = Scheduler.AllocateTaskId();
            var mem = new VMAManager(Runtime.Syscalls.Mem.Backings);
            var engine = new Engine();
            var syscalls = Runtime.Syscalls.Clone(mem, false, true);
            syscalls.CurrentSyscallEngine = engine;
            syscalls.RegisterEngine(engine);
            engine.CurrentSyscallManager = syscalls;

            var process = new Process(pid, mem, syscalls)
            {
                PGID = pid,
                SID = pid,
                Name = name
            };

            Scheduler.RegisterProcess(process);
            var task = new FiberTask(pid, process, engine, Scheduler)
            {
                Status = FiberTaskStatus.Waiting
            };
            engine.Owner = task;

            var handle = new ClientTaskHandle(process, task, engine, syscalls);
            if (track)
                _clients.Add(handle);
            return handle;
        }

        public LinuxFile CreateMemfdLikeFile(SyscallManager syscalls, string name, string content)
        {
            Scheduler.AssertSchedulerThread();

            var inode = syscalls.MemfdSuperBlock.AllocInode();
            inode.Type = InodeType.File;
            inode.Mode = 0x180;
            var dentry = new Dentry($"memfd:{name}", inode, syscalls.MemfdSuperBlock.Root, syscalls.MemfdSuperBlock);
            var file = new LinuxFile(dentry, FileFlags.O_RDWR, syscalls.AnonMount);

            var payload = Encoding.UTF8.GetBytes(content);
            var rc = inode.Write(file, payload, 0);
            Assert.Equal(payload.Length, rc);
            return file;
        }

        private void ResetSchedulerThreadBinding()
        {
            OwnerThreadIdField.SetValue(Scheduler, 0);
        }

        private bool WaitForSchedulerReady(TimeSpan timeout)
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Scheduler.ScheduleFromAnyThread(() => ready.TrySetResult());
            return ready.Task.Wait(timeout);
        }

        private static void StartScheduledAction<T>(Func<ValueTask<T>> action, TaskCompletionSource<T> tcs)
        {
            ValueTask<T> pending;
            try
            {
                pending = action();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
                return;
            }

            if (pending.IsCompletedSuccessfully)
            {
                tcs.TrySetResult(pending.Result);
                return;
            }

            _ = CompleteScheduledActionAsync(pending, tcs);
        }

        private static async Task CompleteScheduledActionAsync<T>(ValueTask<T> pending, TaskCompletionSource<T> tcs)
        {
            try
            {
                tcs.TrySetResult(await pending);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }
    }

    private sealed record ClientTaskHandle(Process Process, FiberTask Task, Engine Engine, SyscallManager Syscalls);
}
