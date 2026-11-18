using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Fiberish.Core;
using Fiberish.Core.Net;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public class SocketSyscallTests
{
    [Fact]
    public async Task Socket_HostNetwork_SoErrorReturnsPositiveErrno()
    {
        using var env = new TestEnv();
        // Select an unused local port. Windows can silently drop SYNs to a bound-but-not-
        // listening port, so release the reservation before requesting an active refusal.
        using var reserved = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        reserved.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var fd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_STREAM, 0);
        var file = env.SyscallManager.GetFD(fd)!;
        var inode = Assert.IsType<HostSocketInode>(file.OpenedInode);
        // Bypass both managed connect caches: Socket.Connect itself can consume SO_ERROR
        // on an immediately refused connection. Issue the nonblocking native syscall instead.
        var address = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(address, (ushort)AddressFamily.InterNetwork);
        if (OperatingSystem.IsMacOS()) { address[0] = 16; address[1] = 2; } // Darwin sin_len/sin_family
        BinaryPrimitives.WriteUInt16BigEndian(address.AsSpan(2), (ushort)((IPEndPoint)reserved.LocalEndPoint!).Port);
        IPAddress.Loopback.TryWriteBytes(address.AsSpan(4), out _);
        reserved.Dispose();
        var handle = inode.NativeSocket.SafeHandle.DangerousGetHandle();
        var connectRc = OperatingSystem.IsWindows()
            ? WindowsConnect(handle, address, address.Length)
            : UnixConnect(handle, address, address.Length);
        var nativeError = Marshal.GetLastPInvokeError();
        Assert.Equal(-1, connectRc);
        if (OperatingSystem.IsWindows())
            Assert.True(nativeError is 10035 or 10061, $"Native connect failed with unexpected WSA error {nativeError}");
        inode.NativeSocket.Poll(3_000_000,
            OperatingSystem.IsWindows() ? SelectMode.SelectError : SelectMode.SelectWrite);
        var value = new byte[4];
        Assert.Equal(0, inode.GetSocketOption(file, env.Task, LinuxConstants.SOL_SOCKET,
            LinuxConstants.SO_ERROR, value, out _));
        Assert.Equal((int)Errno.ECONNREFUSED, BinaryPrimitives.ReadInt32LittleEndian(value));
    }

    [DllImport("ws2_32.dll", EntryPoint = "connect", SetLastError = true)]
    private static extern int WindowsConnect(nint socket, byte[] address, int length);

    [DllImport("libc", EntryPoint = "connect", SetLastError = true)]
    private static extern int UnixConnect(nint socket, byte[] address, int length);

    [Theory]
    [InlineData(LinuxConstants.SO_SNDBUF)]
    [InlineData(LinuxConstants.SO_RCVBUF)]
    public async Task Socket_PrivateNetwork_UdpBufferOptionSurvivesBind(int option)
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;
        var fd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0);
        var file = env.SyscallManager.GetFD(fd)!;
        var inode = Assert.IsType<NetstackSocketInode>(file.OpenedInode);
        var value = BitConverter.GetBytes(65536);
        Assert.Equal(0, inode.SetSocketOption(file, env.Task, LinuxConstants.SOL_SOCKET, option, value));
        Assert.Equal(0, inode.Bind(file, env.Task, new IPEndPoint(IPAddress.Loopback, 0)));
        Assert.Equal(0, inode.GetSocketOption(file, env.Task, LinuxConstants.SOL_SOCKET, option, value, out _));
        Assert.Equal(131072, BinaryPrimitives.ReadInt32LittleEndian(value));
        var udp = (LoopbackNetNamespace.UdpDatagramSocket)typeof(NetstackSocketInode)
            .GetField("_udp", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(inode)!;
        var sizes = udp.GetBufferSizes();
        Assert.Equal(131072, option == LinuxConstants.SO_SNDBUF ? sizes.SendBytes : sizes.RecvBytes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Socket_PrivateNetwork_ListenerTcpOptionsApplyToEveryAcceptedBackend(
        bool setAfterListen, bool disableBeforeAccept)
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;
        var serverFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_STREAM, 0);
        var serverFile = env.SyscallManager.GetFD(serverFd)!;
        var server = Assert.IsType<NetstackSocketInode>(serverFile.OpenedInode);
        Assert.Equal(0, server.Bind(serverFile, env.Task, new IPEndPoint(IPAddress.Loopback, 0)));
        if (setAfterListen) Assert.Equal(0, server.Listen(serverFile, env.Task, 4));
        Assert.Equal(0, server.SetSocketOption(serverFile, env.Task, LinuxConstants.IPPROTO_TCP,
            LinuxConstants.TCP_NODELAY, BitConverter.GetBytes(1)));
        Assert.Equal(0, server.SetSocketOption(serverFile, env.Task, LinuxConstants.IPPROTO_TCP,
            LinuxConstants.TCP_KEEPIDLE, BitConverter.GetBytes(100)));
        Assert.Equal(0, server.SetSocketOption(serverFile, env.Task, LinuxConstants.SOL_SOCKET,
            LinuxConstants.SO_KEEPALIVE, BitConverter.GetBytes(1)));
        if (!setAfterListen) Assert.Equal(0, server.Listen(serverFile, env.Task, 4));

        // The second accept also checks the replacement listener created by smoltcp promotion.
        for (var i = 0; i < 2; i++)
        {
            var clientFd = await CallSysSocket(env, LinuxConstants.AF_INET,
                LinuxConstants.SOCK_STREAM | LinuxConstants.SOCK_NONBLOCK, 0);
            var clientFile = env.SyscallManager.GetFD(clientFd)!;
            var client = Assert.IsType<NetstackSocketInode>(clientFile.OpenedInode);
            var connect = await client.ConnectAsync(clientFile, env.Task, server.LocalEndPoint!);
            Assert.True(connect == 0 || connect == -(int)Errno.EINPROGRESS);
            for (var poll = 0; poll < 100 && (server.Poll(serverFile, PollEvents.POLLIN) & PollEvents.POLLIN) == 0; poll++) { }
            Assert.NotEqual(0, server.Poll(serverFile, PollEvents.POLLIN) & PollEvents.POLLIN);
            if (disableBeforeAccept)
            {
                Assert.Equal(0, server.SetSocketOption(serverFile, env.Task, LinuxConstants.IPPROTO_TCP,
                    LinuxConstants.TCP_NODELAY, BitConverter.GetBytes(0)));
                Assert.Equal(0, server.SetSocketOption(serverFile, env.Task, LinuxConstants.SOL_SOCKET,
                    LinuxConstants.SO_KEEPALIVE, BitConverter.GetBytes(0)));
            }
            var result = await server.AcceptAsync(serverFile, env.Task, 0);
            Assert.Equal(0, result.Rc);
            var accepted = Assert.IsType<NetstackSocketInode>(result.Inode);
            using var acceptedFile = new LinuxFile(new Dentry("accepted", accepted, null, accepted.SuperBlock),
                FileFlags.O_RDWR, null);
            var native = (LoopbackNetNamespace.TcpStreamSocket)typeof(NetstackSocketInode)
                .GetField("_stream", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(accepted)!;
            Assert.True(native.NoDelay);
            Assert.Equal(100_000, native.KeepAliveIntervalMillis);
            var value = new byte[4];
            Assert.Equal(0, accepted.GetSocketOption(acceptedFile, env.Task, LinuxConstants.IPPROTO_TCP,
                LinuxConstants.TCP_NODELAY, value, out _));
            Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(value));
            Assert.Equal(0, accepted.GetSocketOption(acceptedFile, env.Task, LinuxConstants.SOL_SOCKET,
                LinuxConstants.SO_KEEPALIVE, value, out _));
            Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(value));
            if (disableBeforeAccept)
            {
                Assert.Equal(0, server.SetSocketOption(serverFile, env.Task, LinuxConstants.IPPROTO_TCP,
                    LinuxConstants.TCP_NODELAY, BitConverter.GetBytes(1)));
                Assert.Equal(0, server.SetSocketOption(serverFile, env.Task, LinuxConstants.SOL_SOCKET,
                    LinuxConstants.SO_KEEPALIVE, BitConverter.GetBytes(1)));
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Socket_PrivateNetwork_UdpCorkKeepsFirstDestinationEvenForEmptyFirstSend(bool emptyFirst)
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;
        var aFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0);
        var bFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0);
        var senderFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0);
        var aFile = env.SyscallManager.GetFD(aFd)!;
        var bFile = env.SyscallManager.GetFD(bFd)!;
        var senderFile = env.SyscallManager.GetFD(senderFd)!;
        var a = Assert.IsType<NetstackSocketInode>(aFile.OpenedInode);
        var b = Assert.IsType<NetstackSocketInode>(bFile.OpenedInode);
        var sender = Assert.IsType<NetstackSocketInode>(senderFile.OpenedInode);
        Assert.Equal(0, a.Bind(aFile, env.Task, new IPEndPoint(IPAddress.Loopback, 0)));
        Assert.Equal(0, b.Bind(bFile, env.Task, new IPEndPoint(IPAddress.Loopback, 0)));
        var first = emptyFirst ? Array.Empty<byte>() : new byte[] { 0x41 };
        Assert.Equal(first.Length, await sender.SendToAsync(senderFile, env.Task, first,
            LinuxConstants.MSG_MORE, a.LocalEndPoint!));
        Assert.Equal(1, await sender.SendToAsync(senderFile, env.Task, new byte[] { 0x42 }, 0, b.LocalEndPoint!));
        var data = new byte[8];
        Assert.Equal(first.Length + 1,
            (await a.RecvFromAsync(aFile, env.Task, data, LinuxConstants.MSG_DONTWAIT)).BytesRead);
        Assert.Equal(first.Concat(new byte[] { 0x42 }), data.Take(first.Length + 1));
        Assert.Equal(-(int)Errno.EAGAIN,
            (await b.RecvFromAsync(bFile, env.Task, data, LinuxConstants.MSG_DONTWAIT)).BytesRead);
        // A new datagram must not retain the previous cork's destination.
        Assert.Equal(1, await sender.SendToAsync(senderFile, env.Task, new byte[] { 0x43 }, 0, b.LocalEndPoint!));
        Assert.Equal(1, (await b.RecvFromAsync(bFile, env.Task, data, LinuxConstants.MSG_DONTWAIT)).BytesRead);
        Assert.Equal(0x43, data[0]);
    }

    [Fact]
    public async Task RecvMsg_LargeAddressCapacityAndInputMsgFlags_DoNotRejectOrPeekTheDatagram()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;
        env.MapUserPage(0x30000);
        env.MapUserPage(0x31000);
        var receiverFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0);
        var senderFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0);
        var receiverFile = env.SyscallManager.GetFD(receiverFd)!;
        var senderFile = env.SyscallManager.GetFD(senderFd)!;
        var receiver = Assert.IsType<NetstackSocketInode>(receiverFile.OpenedInode);
        var sender = Assert.IsType<NetstackSocketInode>(senderFile.OpenedInode);
        Assert.Equal(0, receiver.Bind(receiverFile, env.Task, new IPEndPoint(IPAddress.Loopback, 0)));
        var destination = Assert.IsType<IPEndPoint>(receiver.GetSockName(receiverFile, env.Task).EndPoint);
        Assert.Equal(0, await sender.ConnectAsync(senderFile, env.Task, destination));
        Assert.Equal(1, await sender.SendAsync(senderFile, env.Task, new byte[] { 0x7a }, 0));
        WriteIovec(env, 0x30100, 0x30200, 8);
        WriteMsgHdr(env, 0x30000, 0x30100, 1, 0, 0, 0x31000, 4096);
        env.WriteInt32(0x30018, LinuxConstants.MSG_PEEK | LinuxConstants.MSG_OOB);
        Assert.Equal(1, await CallSysRecvMsg(env, (uint)receiverFd, 0x30000, LinuxConstants.MSG_DONTWAIT));
        Assert.Equal(16, env.ReadInt32(0x30004));
        Assert.Equal(0x7a, env.ReadBytes(0x30200, 1)[0]);
        Assert.Equal(-(int)Errno.EAGAIN,
            (await receiver.RecvFromAsync(receiverFile, env.Task, new byte[8], LinuxConstants.MSG_DONTWAIT)).BytesRead);
    }

    [Fact]
    public async Task Socket_PrivateNetwork_BufferOptionsUseRealBackendCapacityAndRejectLiveResize()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;
        var fd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0);
        var file = env.SyscallManager.GetFD(fd)!;
        var inode = Assert.IsType<NetstackSocketInode>(file.OpenedInode);
        var requested = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(requested, 65536);
        Assert.Equal(0, inode.SetSocketOption(file, env.Task, LinuxConstants.SOL_SOCKET,
            LinuxConstants.SO_SNDBUF, requested));
        var result = new byte[4];
        Assert.Equal(0, inode.GetSocketOption(file, env.Task, LinuxConstants.SOL_SOCKET,
            LinuxConstants.SO_SNDBUF, result, out var written));
        Assert.Equal(4, written);
        Assert.Equal(131072, BinaryPrimitives.ReadInt32LittleEndian(result));

        // Linux clamps small values up to the protocol minimum (doubled in getsockopt).
        BinaryPrimitives.WriteInt32LittleEndian(requested, 1024);
        Assert.Equal(0, inode.SetSocketOption(file, env.Task, LinuxConstants.SOL_SOCKET,
            LinuxConstants.SO_RCVBUF, requested));
        Assert.Equal(0, inode.GetSocketOption(file, env.Task, LinuxConstants.SOL_SOCKET,
            LinuxConstants.SO_RCVBUF, result, out _));
        Assert.Equal(2304, BinaryPrimitives.ReadInt32LittleEndian(result));

        Assert.Equal(0, inode.Bind(file, env.Task, new IPEndPoint(IPAddress.Any, 0)));
        // The smoltcp socket owns fixed-size buffers once open. Refuse rather than silently
        // keep accepting a size that has no effect.
        Assert.Equal(-(int)Errno.EOPNOTSUPP, inode.SetSocketOption(file, env.Task,
            LinuxConstants.SOL_SOCKET, LinuxConstants.SO_SNDBUF, requested));
    }

    [Fact]
    public async Task Socket_PrivateNetwork_TcpOptionsApplyToTheBackendAndUnsupportedKnobsAreRejected()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;
        env.MapUserPage(0x30000);
        env.MapUserPage(0x31000);
        WriteSockaddrIn(env, 0x30000, 0x7F000001u, 19260);
        WriteSockaddrIn(env, 0x31000, 0x7F000001u, 19260);
        var serverFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_STREAM, 0);
        var clientFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_STREAM, 0);
        Assert.Equal(0, await CallSysBind(env, (uint)serverFd, 0x30000, 16));
        Assert.Equal(0, await CallSysListen(env, (uint)serverFd, 1));
        Assert.Equal(0, await CallSysConnect(env, (uint)clientFd, 0x31000, 16));
        Assert.True(await CallSysAccept(env, (uint)serverFd, 0, 0) >= 0);

        var file = env.SyscallManager.GetFD(clientFd)!;
        var inode = Assert.IsType<NetstackSocketInode>(file.OpenedInode);
        var value = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(value, 1);
        Assert.Equal(0, inode.SetSocketOption(file, env.Task, LinuxConstants.IPPROTO_TCP,
            LinuxConstants.TCP_NODELAY, value));
        Assert.Equal(0, inode.GetSocketOption(file, env.Task, LinuxConstants.IPPROTO_TCP,
            LinuxConstants.TCP_NODELAY, value, out _));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(value));

        Assert.Equal(0, inode.SetSocketOption(file, env.Task, LinuxConstants.SOL_SOCKET,
            LinuxConstants.SO_KEEPALIVE, value));
        BinaryPrimitives.WriteInt32LittleEndian(value, 100);
        Assert.Equal(0, inode.SetSocketOption(file, env.Task, LinuxConstants.IPPROTO_TCP,
            LinuxConstants.TCP_KEEPIDLE, value));
        Assert.Equal(0, inode.GetSocketOption(file, env.Task, LinuxConstants.IPPROTO_TCP,
            LinuxConstants.TCP_KEEPIDLE, value, out _));
        Assert.Equal(100, BinaryPrimitives.ReadInt32LittleEndian(value));

        // No backend equivalent: reject instead of accepting a no-op. Linux checks optlen
        // before the option handler, so a short buffer must still produce EINVAL.
        Assert.Equal(-(int)Errno.ENOPROTOOPT, inode.SetSocketOption(file, env.Task,
            LinuxConstants.IPPROTO_TCP, LinuxConstants.TCP_KEEPINTVL, value));
        Assert.Equal(-(int)Errno.ENOPROTOOPT, inode.SetSocketOption(file, env.Task,
            LinuxConstants.SOL_SOCKET, LinuxConstants.SO_OOBINLINE, value));
        Assert.Equal(-(int)Errno.ENOPROTOOPT, inode.SetSocketOption(file, env.Task,
            LinuxConstants.SOL_SOCKET, LinuxConstants.SO_REUSEPORT, value));
        var shortValue = new byte[1];
        Assert.Equal(-(int)Errno.EINVAL, inode.SetSocketOption(file, env.Task,
            LinuxConstants.SOL_SOCKET, LinuxConstants.SO_OOBINLINE, shortValue));
        Assert.Equal(-(int)Errno.EINVAL, inode.SetSocketOption(file, env.Task,
            LinuxConstants.SOL_SOCKET, LinuxConstants.SO_REUSEPORT, shortValue));
    }

    [Fact]
    public async Task Socket_PrivateNetwork_MulticastMembershipIsPerSocketAndNotInherited()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;
        env.MapUserPage(0x30000);
        env.MapUserPage(0x31000);
        env.MapUserPage(0x32000);
        WriteSockaddrIn(env, 0x30000, 0x7F000001u, 19261);
        WriteSockaddrIn(env, 0x31000, 0x7F000001u, 19261);
        var listenerFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_STREAM, 0);
        var clientFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_STREAM, 0);
        Assert.Equal(0, await CallSysBind(env, (uint)listenerFd, 0x30000, 16));
        Assert.Equal(0, await CallSysListen(env, (uint)listenerFd, 1));
        Assert.Equal(0, await CallSysConnect(env, (uint)clientFd, 0x31000, 16));
        var acceptedFd = await CallSysAccept(env, (uint)listenerFd, 0, 0);
        Assert.True(acceptedFd >= 0);

        // struct group_req: ifindex 0, AF_INET, port 0, 239.255.0.1.
        var groupReq = new byte[20];
        BinaryPrimitives.WriteUInt16LittleEndian(groupReq.AsSpan(4, 2), LinuxConstants.AF_INET);
        groupReq[8] = 239;
        groupReq[9] = 255;
        var listener = env.SyscallManager.GetFD(listenerFd)!;
        var accepted = env.SyscallManager.GetFD(acceptedFd)!;
        var listenerInode = Assert.IsType<NetstackSocketInode>(listener.OpenedInode);
        var acceptedInode = Assert.IsType<NetstackSocketInode>(accepted.OpenedInode);
        Assert.Equal(0, listenerInode.SetSocketOption(listener, env.Task, LinuxConstants.IPPROTO_IP,
            LinuxConstants.MCAST_JOIN_GROUP, groupReq));
        Assert.Equal(0, listenerInode.SetSocketOption(listener, env.Task, LinuxConstants.IPPROTO_IP,
            LinuxConstants.MCAST_JOIN_GROUP, groupReq)); // Idempotent duplicate join.
        Assert.Equal(-(int)Errno.EADDRNOTAVAIL, acceptedInode.SetSocketOption(accepted, env.Task,
            LinuxConstants.IPPROTO_IP, LinuxConstants.MCAST_LEAVE_GROUP, groupReq));
        Assert.Equal(0, listenerInode.SetSocketOption(listener, env.Task, LinuxConstants.IPPROTO_IP,
            LinuxConstants.MCAST_LEAVE_GROUP, groupReq));
        Assert.Equal(-(int)Errno.EADDRNOTAVAIL, listenerInode.SetSocketOption(listener, env.Task,
            LinuxConstants.IPPROTO_IP, LinuxConstants.MCAST_LEAVE_GROUP, groupReq));

        groupReq[8] = 127; // Not a multicast address.
        Assert.Equal(-(int)Errno.EINVAL, listenerInode.SetSocketOption(listener, env.Task,
            LinuxConstants.IPPROTO_IP, LinuxConstants.MCAST_JOIN_GROUP, groupReq));
    }

    [Theory]
    [InlineData(0ul)]
    [InlineData(1ul)]
    public async Task SocketPair_DescriptorLimit_RollsBackWithoutClosingAnOwnedReferenceTwice(ulong limit)
    {
        using var env = new TestEnv();
        env.MapUserPage(0x30000);
        var before = env.SyscallManager.FDs.Keys.Order().ToArray();
        Assert.True(env.Process.TrySetResourceLimit(LinuxConstants.RLIMIT_NOFILE, new ResourceLimit(limit, limit)));
        Assert.Equal(-(int)Errno.EMFILE,
            await CallSysSocketPair(env, LinuxConstants.AF_UNIX, LinuxConstants.SOCK_STREAM, 0, 0x30000));
        Assert.Equal(before, env.SyscallManager.FDs.Keys.Order().ToArray());
    }

    [Fact]
    public async Task RecvMsg_CmsgDescriptorLimit_TruncatesWithoutReturningNegativeDescriptorsOrLeakingRefs()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x30000);
        var fd = await CallSysSocket(env, LinuxConstants.AF_UNIX, LinuxConstants.SOCK_DGRAM, 0);
        Assert.True(fd >= 0);
        var file = env.SyscallManager.GetFD(fd)!;
        var beforeRefs = file.OpenedInode!.RefCount;
        file.Get(); // A received SCM_RIGHTS reference, separate from the existing descriptor.
        Assert.True(env.Process.TrySetResourceLimit(LinuxConstants.RLIMIT_NOFILE, new ResourceLimit(0, 0)));
        var method = typeof(SyscallManager).GetMethod("WriteCmsg", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object?[] args = { env.Engine, 0x30000u, 16, new List<LinuxFile> { file }, null, 0, false, true };
        Assert.Equal(0, method.Invoke(env.SyscallManager, args));
        Assert.Equal(0, args[5]);
        Assert.Equal(true, args[6]);
        Assert.Same(file, env.SyscallManager.GetFD(fd));
        Assert.Equal(beforeRefs, file.OpenedInode.RefCount);
        Assert.Equal(1, typeof(LinuxFile).GetField("_refCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(file));
    }

    [Fact]
    public async Task Socket_PrivateNetwork_UdpCorkOverflow_ReturnsEmsgsizeAndDiscardsPendingDatagram()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;
        var receiverFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0);
        var senderFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0);
        var receiverFile = env.SyscallManager.GetFD(receiverFd)!;
        var senderFile = env.SyscallManager.GetFD(senderFd)!;
        var receiver = Assert.IsType<NetstackSocketInode>(receiverFile.OpenedInode);
        var sender = Assert.IsType<NetstackSocketInode>(senderFile.OpenedInode);
        Assert.Equal(0, receiver.Bind(receiverFile, env.Task, new IPEndPoint(IPAddress.Loopback, 0)));
        var destination = Assert.IsType<IPEndPoint>(receiver.GetSockName(receiverFile, env.Task).EndPoint);
        Assert.Equal(0, await sender.ConnectAsync(senderFile, env.Task, destination));
        Assert.Equal(65000, await sender.SendAsync(senderFile, env.Task, new byte[65000], LinuxConstants.MSG_MORE));
        Assert.Equal(-(int)Errno.EMSGSIZE,
            await sender.SendAsync(senderFile, env.Task, new byte[1000], LinuxConstants.MSG_MORE));
        Assert.Equal(1, await sender.SendAsync(senderFile, env.Task, new byte[] { 0x7a }, 0));
        var data = new byte[8];
        var received = await receiver.RecvFromAsync(receiverFile, env.Task, data, LinuxConstants.MSG_DONTWAIT);
        Assert.Equal(1, received.BytesRead);
        Assert.Equal(0x7a, data[0]);
    }

    private static ValueTask<int> CallSysSocket(TestEnv env, uint domain, uint type, uint protocol)
    {
        return env.Invoke("SysSocket", domain, type, protocol, 0u, 0u, 0u);
    }

    private static ValueTask<int> CallSysSetSockOpt(TestEnv env, uint fd, uint level, uint optname, uint optval,
        uint optlen)
    {
        return env.Invoke("SysSetSockOpt", fd, level, optname, optval, optlen, 0u);
    }

    private static ValueTask<int> CallSysGetSockOpt(TestEnv env, uint fd, uint level, uint optname, uint optval,
        uint optlenPtr)
    {
        return env.Invoke("SysGetSockOpt", fd, level, optname, optval, optlenPtr, 0u);
    }

    private static ValueTask<int> CallSysBind(TestEnv env, uint fd, uint addrPtr, uint addrLen)
    {
        return env.Invoke("SysBind", fd, addrPtr, addrLen, 0u, 0u, 0u);
    }

    private static ValueTask<int> CallSysListen(TestEnv env, uint fd, uint backlog)
    {
        return env.Invoke("SysListen", fd, backlog, 0u, 0u, 0u, 0u);
    }

    private static ValueTask<int> CallSysConnect(TestEnv env, uint fd, uint addrPtr, uint addrLen)
    {
        return env.Invoke("SysConnect", fd, addrPtr, addrLen, 0u, 0u, 0u);
    }

    private static ValueTask<int> CallSysGetSockName(TestEnv env, uint fd, uint addrPtr, uint addrLenPtr)
    {
        return env.Invoke("SysGetSockName", fd, addrPtr, addrLenPtr, 0u, 0u, 0u);
    }

    private static ValueTask<int> CallSysGetPeerName(TestEnv env, uint fd, uint addrPtr, uint addrLenPtr)
    {
        return env.Invoke("SysGetPeerName", fd, addrPtr, addrLenPtr, 0u, 0u, 0u);
    }

    private static ValueTask<int> CallSysAccept(TestEnv env, uint fd, uint addrPtr, uint addrLenPtr)
    {
        return env.Invoke("SysAccept", fd, addrPtr, addrLenPtr, 0u, 0u, 0u);
    }

    private static ValueTask<int> CallSysSocketPair(TestEnv env, uint domain, uint type, uint protocol, uint svPtr)
    {
        return env.Invoke("SysSocketPair", domain, type, protocol, svPtr, 0u, 0u);
    }

    private static ValueTask<int> CallSysSendMsg(TestEnv env, uint fd, uint msgPtr, uint flags)
    {
        return env.Invoke("SysSendMsg", fd, msgPtr, flags, 0u, 0u, 0u);
    }

    private static ValueTask<int> CallSysRecvMsg(TestEnv env, uint fd, uint msgPtr, uint flags)
    {
        return env.Invoke("SysRecvMsg", fd, msgPtr, flags, 0u, 0u, 0u);
    }

    private static ValueTask<int> CallSysShutdown(TestEnv env, uint fd, uint how)
    {
        return env.Invoke("SysShutdown", fd, how, 0u, 0u, 0u, 0u);
    }

    private static ValueTask<int> CallSysSend(TestEnv env, uint fd, uint bufPtr, uint len, uint flags = 0)
    {
        return env.Invoke("SysSend", fd, bufPtr, len, flags, 0u, 0u);
    }

    private static ValueTask<int> CallSysSendTo(TestEnv env, uint fd, uint bufPtr, uint len, uint flags,
        uint addrPtr = 0, uint addrLen = 0)
    {
        return env.Invoke("SysSendTo", fd, bufPtr, len, flags, addrPtr, addrLen);
    }

    private static ValueTask<int> CallSysRecvFrom(TestEnv env, uint fd, uint bufPtr, uint len, uint flags,
        uint addrPtr = 0, uint addrLen = 0)
    {
        return env.Invoke("SysRecvFrom", fd, bufPtr, len, flags, addrPtr, addrLen);
    }

    private static ValueTask<int> CallSysIoctl(TestEnv env, uint fd, uint request, uint arg)
    {
        return env.Invoke("SysIoctl", fd, request, arg, 0u, 0u, 0u);
    }

    private static ValueTask<int> CallSysRead(TestEnv env, uint fd, uint bufPtr, uint len)
    {
        return env.Invoke("SysRead", fd, bufPtr, len, 0u, 0u, 0u);
    }

    private static ValueTask<int> CallSysWrite(TestEnv env, uint fd, uint bufPtr, uint len)
    {
        return env.Invoke("SysWrite", fd, bufPtr, len, 0u, 0u, 0u);
    }

    private static ValueTask<int> CallSysReadV(TestEnv env, uint fd, uint iovPtr, uint iovCount)
    {
        return env.Invoke("SysReadV", fd, iovPtr, iovCount, 0u, 0u, 0u);
    }

    private static ValueTask<int> CallSysWriteV(TestEnv env, uint fd, uint iovPtr, uint iovCount)
    {
        return env.Invoke("SysWriteV", fd, iovPtr, iovCount, 0u, 0u, 0u);
    }

    private static ValueTask<int> CallSysUnlink(TestEnv env, uint pathPtr)
    {
        return env.Invoke("SysUnlink", pathPtr, 0u, 0u, 0u, 0u, 0u);
    }

    private static ValueTask<int> CallSysLink(TestEnv env, uint oldPathPtr, uint newPathPtr)
    {
        return env.Invoke("SysLink", oldPathPtr, newPathPtr, 0u, 0u, 0u, 0u);
    }

    private static ValueTask<int> CallSysRename(TestEnv env, uint oldPathPtr, uint newPathPtr)
    {
        return env.Invoke("SysRename", oldPathPtr, newPathPtr, 0u, 0u, 0u, 0u);
    }

    private static ValueTask<int> CallSysClose(TestEnv env, uint fd)
    {
        return env.Invoke("SysClose", fd, 0u, 0u, 0u, 0u, 0u);
    }

    [Fact]
    public async Task Socket_RawWithProtocolZero_ReturnsEprotonosupport()
    {
        using var env = new TestEnv();

        var rc = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_RAW, 0);

        if (OperatingSystem.IsMacOS())
        {
            if (rc < 0)
                Assert.True(rc is -(int)Errno.EACCES or -(int)Errno.EPERM, $"unexpected errno {rc}");
            else
                Assert.IsType<HostSocketInode>(Assert.IsType<LinuxFile>(env.SyscallManager.GetFD(rc)).Dentry.Inode);
            return;
        }

        Assert.Equal(-(int)Errno.EPROTONOSUPPORT, rc);
    }

    [Fact]
    public async Task Socket_RawWithUnsupportedProtocol_ReturnsEprotonosupport()
    {
        using var env = new TestEnv();

        var rc = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_RAW, LinuxConstants.IPPROTO_TCP);

        if (OperatingSystem.IsMacOS())
        {
            if (rc < 0)
                Assert.True(rc is -(int)Errno.EACCES or -(int)Errno.EPERM, $"unexpected errno {rc}");
            else
                Assert.IsType<HostSocketInode>(Assert.IsType<LinuxFile>(env.SyscallManager.GetFD(rc)).Dentry.Inode);
            return;
        }

        Assert.Equal(-(int)Errno.EPROTONOSUPPORT, rc);
    }

    [Fact]
    public async Task Socket_RawIcmp_CreatesRawSocketOrReturnsPermissionError()
    {
        using var env = new TestEnv();

        var rc = await CallSysSocket(env, LinuxConstants.AF_INET,
            LinuxConstants.SOCK_RAW | LinuxConstants.SOCK_NONBLOCK | LinuxConstants.SOCK_CLOEXEC,
            LinuxConstants.IPPROTO_ICMP);

        if (rc < 0)
        {
            Assert.True(rc is -(int)Errno.EACCES or -(int)Errno.EPERM,
                $"unexpected errno {rc}");
            return;
        }

        var file = Assert.IsType<LinuxFile>(env.SyscallManager.GetFD(rc));
        var inode = Assert.IsType<HostSocketInode>(file.Dentry.Inode);
        Assert.Equal(FileFlags.O_RDWR | FileFlags.O_NONBLOCK | FileFlags.O_CLOEXEC, file.Flags);
        Assert.Equal(SocketType.Raw, inode.LinuxSocketType);
        Assert.True(inode.HostSocketType is SocketType.Dgram or SocketType.Raw);
        Assert.Equal(ProtocolType.Icmp, inode.HostProtocolType);
    }

    [Fact]
    public async Task Socket_RawIcmpV6_CreatesRawSocketOrReturnsPermissionError()
    {
        using var env = new TestEnv();

        var rc = await CallSysSocket(env, LinuxConstants.AF_INET6, LinuxConstants.SOCK_RAW,
            LinuxConstants.IPPROTO_ICMPV6);

        if (rc < 0)
        {
            Assert.True(rc is -(int)Errno.EACCES or -(int)Errno.EPERM,
                $"unexpected errno {rc}");
            return;
        }

        var file = Assert.IsType<LinuxFile>(env.SyscallManager.GetFD(rc));
        var inode = Assert.IsType<HostSocketInode>(file.Dentry.Inode);
        Assert.Equal(SocketType.Raw, inode.LinuxSocketType);
        Assert.True(inode.HostSocketType is SocketType.Dgram or SocketType.Raw);
        Assert.Equal(ProtocolType.IcmpV6, inode.HostProtocolType);
    }

    [Fact]
    public async Task Socket_RawIcmpV6_OnIpv4_ReturnsEprotonosupport()
    {
        using var env = new TestEnv();

        var rc = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_RAW,
            LinuxConstants.IPPROTO_ICMPV6);

        if (OperatingSystem.IsMacOS())
        {
            if (rc < 0)
            {
                Assert.True(rc is -(int)Errno.EACCES or -(int)Errno.EPERM, $"unexpected errno {rc}");
            }
            else
            {
                var file = Assert.IsType<LinuxFile>(env.SyscallManager.GetFD(rc));
                var inode = Assert.IsType<HostSocketInode>(file.Dentry.Inode);
                Assert.Equal(SocketType.Raw, inode.LinuxSocketType);
                Assert.Equal(AddressFamily.InterNetwork, inode.HostAddressFamily);
                Assert.Equal(ProtocolType.Icmp, inode.HostProtocolType);
                Assert.Equal(SocketType.Dgram, inode.HostSocketType);
            }

            return;
        }

        Assert.Equal(-(int)Errno.EPROTONOSUPPORT, rc);
    }

    [Fact]
    public async Task Socket_DgramIcmp_CreatesPingSocketOrReturnsPermissionError()
    {
        using var env = new TestEnv();

        var rc = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM,
            LinuxConstants.IPPROTO_ICMP);

        if (rc < 0)
        {
            Assert.True(rc is -(int)Errno.EACCES or -(int)Errno.EPERM,
                $"unexpected errno {rc}");
            return;
        }

        var file = Assert.IsType<LinuxFile>(env.SyscallManager.GetFD(rc));
        var inode = Assert.IsType<HostSocketInode>(file.Dentry.Inode);
        Assert.Equal(SocketType.Dgram, inode.LinuxSocketType);
        Assert.Equal(ProtocolType.Icmp, inode.HostProtocolType);
        Assert.True(inode.HostSocketType is SocketType.Dgram or SocketType.Raw);
    }

    [Fact]
    public async Task Socket_DgramIcmpV6_CreatesPingSocketOrReturnsPermissionError()
    {
        using var env = new TestEnv();

        var rc = await CallSysSocket(env, LinuxConstants.AF_INET6, LinuxConstants.SOCK_DGRAM,
            LinuxConstants.IPPROTO_ICMPV6);

        if (rc < 0)
        {
            Assert.True(rc is -(int)Errno.EACCES or -(int)Errno.EPERM,
                $"unexpected errno {rc}");
            return;
        }

        var file = Assert.IsType<LinuxFile>(env.SyscallManager.GetFD(rc));
        var inode = Assert.IsType<HostSocketInode>(file.Dentry.Inode);
        Assert.Equal(SocketType.Dgram, inode.LinuxSocketType);
        Assert.Equal(ProtocolType.IcmpV6, inode.HostProtocolType);
        Assert.True(inode.HostSocketType is SocketType.Dgram or SocketType.Raw);
    }

    [Fact]
    public async Task Socket_DgramIcmpV6_OnIpv4_ReturnsEprotonosupport()
    {
        using var env = new TestEnv();

        var rc = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM,
            LinuxConstants.IPPROTO_ICMPV6);

        Assert.Equal(-(int)Errno.EPROTONOSUPPORT, rc);
    }

    [Fact]
    public async Task SetSockOpt_UnsupportedSocketOption_ReturnsEnoprotoopt()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x10000);
        env.WriteInt32(0x10000, 1);

        var fd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0);
        Assert.True(fd >= 0);

        var rc = await CallSysSetSockOpt(env, (uint)fd, LinuxConstants.SOL_SOCKET, 0x7fff, 0x10000, 4);
        Assert.Equal(-(int)Errno.ENOPROTOOPT, rc);
    }

    [Fact]
    public async Task GetSockOpt_UnsupportedLevel_ReturnsEnoprotoopt()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x11000);
        env.MapUserPage(0x12000);
        env.WriteInt32(0x12000, 4);

        var fd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0);
        Assert.True(fd >= 0);

        var rc = await CallSysGetSockOpt(env, (uint)fd, 0x7fff, 1, 0x11000, 0x12000);
        Assert.Equal(-(int)Errno.ENOPROTOOPT, rc);
    }

    [Fact]
    public async Task GetSockOpt_SoType_OnRawSocket_ReturnsSockRaw()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x13000);
        env.MapUserPage(0x14000);
        env.WriteInt32(0x14000, 4);

        var fd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_RAW, LinuxConstants.IPPROTO_ICMP);
        if (fd < 0)
        {
            Assert.True(fd is -(int)Errno.EACCES or -(int)Errno.EPERM,
                $"unexpected errno {fd}");
            return;
        }

        var rc = await CallSysGetSockOpt(env, (uint)fd, LinuxConstants.SOL_SOCKET, LinuxConstants.SO_TYPE, 0x13000,
            0x14000);
        Assert.Equal(0, rc);
        Assert.Equal(LinuxConstants.SOCK_RAW, env.ReadInt32(0x13000));
        Assert.Equal(4, env.ReadInt32(0x14000));
    }

    [Fact]
    public async Task GetSockOpt_SoType_OnPingSocket_ReturnsSockDgram()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x15000);
        env.MapUserPage(0x16000);
        env.WriteInt32(0x16000, 4);

        var fd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM,
            LinuxConstants.IPPROTO_ICMP);
        if (fd < 0)
        {
            Assert.True(fd is -(int)Errno.EACCES or -(int)Errno.EPERM,
                $"unexpected errno {fd}");
            return;
        }

        var rc = await CallSysGetSockOpt(env, (uint)fd, LinuxConstants.SOL_SOCKET, LinuxConstants.SO_TYPE, 0x15000,
            0x16000);
        Assert.Equal(0, rc);
        Assert.Equal(LinuxConstants.SOCK_DGRAM, env.ReadInt32(0x15000));
        Assert.Equal(4, env.ReadInt32(0x16000));
    }

    [Fact]
    public async Task SetGetSockOpt_SoLinger_OnHostStreamSocket_RoundTrips()
    {
        using var env = new TestEnv();

        var fd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_STREAM, 0);
        Assert.True(fd >= 0);

        env.MapUserPage(0x17000);
        env.MapUserPage(0x18000);
        env.MapUserPage(0x19000);

        // struct linger { int l_onoff; int l_linger; }
        env.WriteInt32(0x17000, 1);
        env.WriteInt32(0x17004, 0);
        Assert.Equal(0,
            await CallSysSetSockOpt(env, (uint)fd, LinuxConstants.SOL_SOCKET, LinuxConstants.SO_LINGER, 0x17000, 8));

        env.WriteInt32(0x19000, 8);
        Assert.Equal(0,
            await CallSysGetSockOpt(env, (uint)fd, LinuxConstants.SOL_SOCKET, LinuxConstants.SO_LINGER, 0x18000,
                0x19000));

        Assert.Equal(8, env.ReadInt32(0x19000));
        Assert.Equal(1, env.ReadInt32(0x18000));
        Assert.Equal(0, env.ReadInt32(0x18004));

        var file = Assert.IsType<LinuxFile>(env.SyscallManager.GetFD(fd));
        var inode = Assert.IsType<HostSocketInode>(file.Dentry.Inode);
        var linger = inode.NativeSocket.LingerState;
        Assert.NotNull(linger);
        Assert.True(linger.Enabled);
        Assert.Equal(0, linger.LingerTime);
    }

    [Fact]
    public async Task SetSockOpt_TcpKeepAliveOptions_OnHostStreamSocket_AreAccepted()
    {
        using var env = new TestEnv();

        var fd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_STREAM, 0);
        Assert.True(fd >= 0);

        env.MapUserPage(0x1C000);
        env.WriteInt32(0x1C000, 30);

        Assert.Equal(0,
            await CallSysSetSockOpt(env, (uint)fd, LinuxConstants.IPPROTO_TCP, LinuxConstants.TCP_KEEPIDLE, 0x1C000,
                4));
        Assert.Equal(0,
            await CallSysSetSockOpt(env, (uint)fd, LinuxConstants.IPPROTO_TCP, LinuxConstants.TCP_KEEPINTVL, 0x1C000,
                4));
        Assert.Equal(0,
            await CallSysSetSockOpt(env, (uint)fd, LinuxConstants.IPPROTO_TCP, LinuxConstants.TCP_KEEPCNT, 0x1C000,
                4));
    }

    [Fact]
    public async Task Shutdown_UnconnectedSocket_WithSoLingerZero_ReturnsEnotconnAndKeepsFdAlive()
    {
        using var env = new TestEnv();

        var fd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_STREAM, 0);
        Assert.True(fd >= 0);

        env.MapUserPage(0x1A000);
        env.MapUserPage(0x1B000);

        // SO_LINGER { on=1, linger=0 }
        env.WriteInt32(0x1A000, 1);
        env.WriteInt32(0x1A004, 0);
        Assert.Equal(0,
            await CallSysSetSockOpt(env, (uint)fd, LinuxConstants.SOL_SOCKET, LinuxConstants.SO_LINGER, 0x1A000, 8));

        Assert.Equal(-(int)Errno.ENOTCONN, await CallSysShutdown(env, (uint)fd, 2));

        var file = Assert.IsType<LinuxFile>(env.SyscallManager.GetFD(fd));
        var inode = Assert.IsType<HostSocketInode>(file.Dentry.Inode);
        _ = inode.NativeSocket.Available;

        env.WriteBytes(0x1B000, [1]);
        var sendRc = await CallSysSend(env, (uint)fd, 0x1B000, 1);
        Assert.True(sendRc is -(int)Errno.ENOTCONN or -(int)Errno.EPIPE or -(int)Errno.ECONNREFUSED
            or -(int)Errno.ECONNABORTED);
    }

    [Fact]
    public async Task Socket_PrivateNetwork_Stream_UsesNetstackSocketInode()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;

        var fd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_STREAM, 0);

        Assert.True(fd >= 0);
        var file = Assert.IsType<LinuxFile>(env.SyscallManager.GetFD(fd));
        Assert.IsType<NetstackSocketInode>(file.Dentry.Inode);
    }

    [Fact]
    public async Task Socket_PrivateNetwork_Dgram_ReturnsEsocktnosupport()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;

        var rc = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0);

        Assert.True(rc >= 0);
        var file = Assert.IsType<LinuxFile>(env.SyscallManager.GetFD(rc));
        Assert.IsType<NetstackSocketInode>(file.Dentry.Inode);
    }

    [Fact]
    public async Task Socket_PrivateNetwork_Raw_ReturnsEsocktnosupport()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;

        var rc = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_RAW, 0);

        Assert.Equal(-(int)Errno.ESOCKTNOSUPPORT, rc);
    }

    [Fact]
    public async Task Socket_PrivateNetwork_Dgram_GetSockOpt_TypeAndError_Work()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;
        env.MapUserPage(0x26000);
        env.MapUserPage(0x27000);
        env.WriteInt32(0x27000, 4);

        var fd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0);
        Assert.True(fd >= 0);

        Assert.Equal(0,
            await CallSysGetSockOpt(env, (uint)fd, LinuxConstants.SOL_SOCKET, LinuxConstants.SO_TYPE, 0x26000,
                0x27000));
        Assert.Equal(LinuxConstants.SOCK_DGRAM, env.ReadInt32(0x26000));

        env.WriteInt32(0x27000, 4);
        Assert.Equal(0,
            await CallSysGetSockOpt(env, (uint)fd, LinuxConstants.SOL_SOCKET, LinuxConstants.SO_ERROR, 0x26000,
                0x27000));
        Assert.Equal(0, env.ReadInt32(0x26000));
    }

    [Fact]
    public async Task Socket_HostNetwork_ClosedPortConnect_DoesNotReportFalseSuccessOrEio()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x28000);
        env.MapUserPage(0x29000);
        env.MapUserPage(0x2A000);

        int closedPort;
        using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            closedPort = ((IPEndPoint)probe.LocalEndPoint!).Port;
        }

        WriteSockaddrIn(env, 0x28000, 0x7F000001u, (ushort)closedPort);

        var fd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_STREAM, 0);
        Assert.True(fd >= 0);

        var connectRc = await CallSysConnect(env, (uint)fd, 0x28000, 16);
        Assert.NotEqual(0, connectRc);
        Assert.Equal(-(int)Errno.ECONNREFUSED, connectRc);

        env.WriteBytes(0x2A000, [0x42]);
        var sendRc = await CallSysSend(env, (uint)fd, 0x2A000, 1);
        Assert.True(sendRc is -(int)Errno.EPIPE or -(int)Errno.ENOTCONN or -(int)Errno.ECONNREFUSED);
    }

    [Fact]
    public async Task Socket_NetlinkRoute_CreatesNetlinkRouteInode()
    {
        using var env = new TestEnv();

        var fd = await CallSysSocket(env, LinuxConstants.AF_NETLINK, LinuxConstants.SOCK_DGRAM,
            LinuxConstants.NETLINK_ROUTE);
        Assert.True(fd >= 0);

        var file = Assert.IsType<LinuxFile>(env.SyscallManager.GetFD(fd));
        Assert.IsType<NetlinkSocketInode>(file.Dentry.Inode);
    }

    [Fact]
    public async Task Socket_NetlinkRoute_GetLinkAndAddrDump_ReturnsMultipartMessages()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;
        _ = env.SyscallManager.GetOrCreatePrivateNetNamespace();
        env.MapUserPage(0x33000);
        env.MapUserPage(0x34000);

        var fd = await CallSysSocket(env, LinuxConstants.AF_NETLINK,
            LinuxConstants.SOCK_DGRAM | LinuxConstants.SOCK_NONBLOCK,
            LinuxConstants.NETLINK_ROUTE);
        Assert.True(fd >= 0);

        WriteNlMsg(env, 0x33000, LinuxConstants.RTM_GETLINK, LinuxConstants.NLM_F_REQUEST | LinuxConstants.NLM_F_DUMP,
            100);
        Assert.Equal(16, await CallSysSendTo(env, (uint)fd, 0x33000, 16, 0));

        var rc1 = await CallSysRecvFrom(env, (uint)fd, 0x34000, 512, 0);
        var type1 = BinaryPrimitives.ReadUInt16LittleEndian(env.ReadBytes(0x34000 + 4, 2));
        Assert.True(rc1 > 0);
        Assert.Equal(LinuxConstants.RTM_NEWLINK, type1);

        var rc2 = await CallSysRecvFrom(env, (uint)fd, 0x34000, 512, 0);
        var type2 = BinaryPrimitives.ReadUInt16LittleEndian(env.ReadBytes(0x34000 + 4, 2));
        Assert.True(rc2 > 0);
        Assert.Equal(LinuxConstants.RTM_NEWLINK, type2);

        var rc3 = await CallSysRecvFrom(env, (uint)fd, 0x34000, 512, 0);
        var doneType = BinaryPrimitives.ReadUInt16LittleEndian(env.ReadBytes(0x34000 + 4, 2));
        Assert.True(rc3 > 0);
        Assert.Equal(LinuxConstants.NLMSG_DONE, doneType);

        WriteNlMsg(env, 0x33000, LinuxConstants.RTM_GETADDR, LinuxConstants.NLM_F_REQUEST | LinuxConstants.NLM_F_DUMP,
            101);
        Assert.Equal(16, await CallSysSendTo(env, (uint)fd, 0x33000, 16, 0));

        var a1 = await CallSysRecvFrom(env, (uint)fd, 0x34000, 512, 0);
        var t1 = BinaryPrimitives.ReadUInt16LittleEndian(env.ReadBytes(0x34000 + 4, 2));
        Assert.True(a1 > 0);
        Assert.Equal(LinuxConstants.RTM_NEWADDR, t1);

        var a2 = await CallSysRecvFrom(env, (uint)fd, 0x34000, 512, 0);
        var t2 = BinaryPrimitives.ReadUInt16LittleEndian(env.ReadBytes(0x34000 + 4, 2));
        Assert.True(a2 > 0);
        Assert.Equal(LinuxConstants.RTM_NEWADDR, t2);

        var a3 = await CallSysRecvFrom(env, (uint)fd, 0x34000, 512, 0);
        var t3 = BinaryPrimitives.ReadUInt16LittleEndian(env.ReadBytes(0x34000 + 4, 2));
        Assert.True(a3 > 0);
        Assert.Equal(LinuxConstants.NLMSG_DONE, t3);

        var empty = await CallSysRecvFrom(env, (uint)fd, 0x34000, 512, 0);
        Assert.Equal(-(int)Errno.EAGAIN, empty);
    }

    [Fact]
    public async Task Socket_NetlinkRoute_WritePath_ProducesDumpAndGetSockNameWorks()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;
        _ = env.SyscallManager.GetOrCreatePrivateNetNamespace();
        env.MapUserPage(0x38000);
        env.MapUserPage(0x39000);
        env.MapUserPage(0x3A000);
        env.WriteInt32(0x3A000, 12);

        var fd = await CallSysSocket(env, LinuxConstants.AF_NETLINK,
            LinuxConstants.SOCK_DGRAM | LinuxConstants.SOCK_NONBLOCK,
            LinuxConstants.NETLINK_ROUTE);
        Assert.True(fd >= 0);

        Assert.Equal(0, await CallSysGetSockName(env, (uint)fd, 0x39000, 0x3A000));
        Assert.Equal(12, env.ReadInt32(0x3A000));
        var family = BinaryPrimitives.ReadUInt16LittleEndian(env.ReadBytes(0x39000, 2));
        Assert.Equal((ushort)LinuxConstants.AF_NETLINK, family);

        var sockaddr = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(sockaddr, LinuxConstants.AF_NETLINK);
        BinaryPrimitives.WriteUInt32LittleEndian(sockaddr.AsSpan(4), 0x4242);
        BinaryPrimitives.WriteUInt32LittleEndian(sockaddr.AsSpan(8), 1);
        env.WriteBytes(0x38000, sockaddr);
        Assert.Equal(0, await CallSysBind(env, (uint)fd, 0x38000, (uint)sockaddr.Length));
        Assert.Equal(0, await CallSysGetSockName(env, (uint)fd, 0x39000, 0x3A000));
        Assert.Equal(sockaddr, env.ReadBytes(0x39000, sockaddr.Length));

        WriteNlMsg(env, 0x38000, LinuxConstants.RTM_GETLINK, LinuxConstants.NLM_F_REQUEST | LinuxConstants.NLM_F_DUMP,
            200);
        Assert.Equal(16, await CallSysWrite(env, (uint)fd, 0x38000, 16));

        var first = await CallSysRecvFrom(env, (uint)fd, 0x39000, 512, 0);
        Assert.True(first > 0);
        var msgType = BinaryPrimitives.ReadUInt16LittleEndian(env.ReadBytes(0x39000 + 4, 2));
        Assert.Equal(LinuxConstants.RTM_NEWLINK, msgType);
    }

    [Fact]
    public async Task Ioctl_Siocgifconf_InPrivateMode_ReturnsLoAndEth0()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;
        _ = env.SyscallManager.GetOrCreatePrivateNetNamespace();
        env.MapUserPage(0x35000);
        env.MapUserPage(0x36000);

        var fd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0);
        Assert.True(fd >= 0);

        env.WriteInt32(0x35000, 64);
        env.WriteUInt32(0x35004, 0x36000);
        Assert.Equal(0, await CallSysIoctl(env, (uint)fd, LinuxConstants.SIOCGIFCONF, 0x35000));
        Assert.Equal(64, env.ReadInt32(0x35000));

        var name0 = ReadIfreqName(env, 0x36000);
        var name1 = ReadIfreqName(env, 0x36020);
        Assert.Equal("lo", name0);
        Assert.Equal("eth0", name1);
    }

    [Fact]
    public async Task Ioctl_Siocgifflags_OnHostSocket_ReturnsLoopbackFlagForLo()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x37000);

        var fd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0);
        Assert.True(fd >= 0);

        WriteIfreqName(env, 0x37000, "lo");
        Assert.Equal(0, await CallSysIoctl(env, (uint)fd, LinuxConstants.SIOCGIFFLAGS, 0x37000));

        var flags = BinaryPrimitives.ReadUInt16LittleEndian(env.ReadBytes(0x37010, 2));
        Assert.NotEqual(0u, flags & LinuxConstants.IFF_LOOPBACK);
    }

    [Fact]
    public async Task Ioctl_Siocgiftxqlen_ReturnsQueueLength()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;
        _ = env.SyscallManager.GetOrCreatePrivateNetNamespace();
        env.MapUserPage(0x3B000);

        var fd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0);
        Assert.True(fd >= 0);

        WriteIfreqName(env, 0x3B000, "eth0");
        Assert.Equal(0, await CallSysIoctl(env, (uint)fd, LinuxConstants.SIOCGIFTXQLEN, 0x3B000));
        var qlen = BinaryPrimitives.ReadInt32LittleEndian(env.ReadBytes(0x3B010, 4));
        Assert.Equal(1000, qlen);
    }

    [Fact]
    public async Task Socket_HostUdp_SendTo_WithMsgNosignal_SendsPacket()
    {
        using var server = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        server.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        server.ReceiveTimeout = 2000;
        var port = ((IPEndPoint)server.LocalEndPoint!).Port;

        using var env = new TestEnv();
        env.MapUserPage(0x3C000);
        env.MapUserPage(0x3D000);
        env.WriteBytes(0x3C000, Encoding.ASCII.GetBytes("dns-probe"));
        WriteSockaddrIn(env, 0x3D000, 0x7F000001u, (ushort)port);

        var fd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0);
        Assert.True(fd >= 0);

        var rc = await CallSysSendTo(env, (uint)fd, 0x3C000, 9, LinuxConstants.MSG_NOSIGNAL, 0x3D000, 16);
        Assert.Equal(9, rc);

        var receiveBuffer = new byte[64];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        var received = server.ReceiveFrom(receiveBuffer, 0, receiveBuffer.Length, SocketFlags.None, ref remote);
        Assert.Equal("dns-probe", Encoding.ASCII.GetString(receiveBuffer, 0, received));
    }

    [Fact]
    public async Task Socket_HostUdp_ReadV_ConsumesSingleDatagramPerCall()
    {
        using var server = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        server.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var serverPort = ((IPEndPoint)server.LocalEndPoint!).Port;

        using var env = new TestEnv();
        env.MapUserPage(0x3E000);
        env.MapUserPage(0x3F000);
        env.MapUserPage(0x40000);
        env.MapUserPage(0x41000);
        env.MapUserPage(0x42000);
        env.MapUserPage(0x43000);
        env.MapUserPage(0x44000);
        env.MapUserPage(0x45000);
        env.WriteInt32(0x40000, 16);

        WriteSockaddrIn(env, 0x3E000, 0x7F000001u, (ushort)serverPort);
        var fd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0);
        Assert.True(fd >= 0);
        Assert.Equal(0, await CallSysConnect(env, (uint)fd, 0x3E000, 16));
        Assert.Equal(0, await CallSysGetSockName(env, (uint)fd, 0x3F000, 0x40000));

        var (guestIp, guestPort) = ReadSockaddrIn(env, 0x3F000);
        var guestEndpoint = new IPEndPoint(new IPAddress(BinaryPrimitives.ReverseEndianness(guestIp)), guestPort);
        _ = server.SendTo(Encoding.ASCII.GetBytes("one"), guestEndpoint);
        _ = server.SendTo(Encoding.ASCII.GetBytes("two"), guestEndpoint);

        env.WriteBytes(0x42000, [0x58, 0x58]);
        env.WriteBytes(0x43000, [0x59, 0x59]);
        env.WriteBytes(0x45000, [0x5A, 0x5A]);
        env.WriteBytes(0x45010, [0x57, 0x57]);
        WriteIovec(env, 0x41000, 0x42000, 2);
        WriteIovec(env, 0x41008, 0x43000, 2);
        WriteIovec(env, 0x44000, 0x45000, 2);
        WriteIovec(env, 0x44008, 0x45010, 2);

        var first = await CallSysReadV(env, (uint)fd, 0x41000, 2);
        Assert.Equal(3, first);
        Assert.Equal([0x6F, 0x6E], env.ReadBytes(0x42000, 2));
        Assert.Equal([0x65, 0x59], env.ReadBytes(0x43000, 2));

        var second = await CallSysReadV(env, (uint)fd, 0x44000, 2);
        Assert.Equal(3, second);
        Assert.Equal([0x74, 0x77], env.ReadBytes(0x45000, 2));
        Assert.Equal([0x6F, 0x57], env.ReadBytes(0x45010, 2));
    }

    [Fact]
    public async Task Socket_HostUdp_WriteV_SendsSingleDatagram()
    {
        using var server = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        server.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        server.ReceiveTimeout = 2000;
        var serverPort = ((IPEndPoint)server.LocalEndPoint!).Port;

        using var env = new TestEnv();
        env.MapUserPage(0x46000);
        env.MapUserPage(0x47000);
        env.MapUserPage(0x48000);
        env.MapUserPage(0x49000);
        env.WriteBytes(0x47000, Encoding.ASCII.GetBytes("alpha"));
        env.WriteBytes(0x48000, Encoding.ASCII.GetBytes("beta"));

        WriteSockaddrIn(env, 0x46000, 0x7F000001u, (ushort)serverPort);
        WriteIovec(env, 0x49000, 0x47000, 5);
        WriteIovec(env, 0x49008, 0x48000, 4);

        var fd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0);
        Assert.True(fd >= 0);
        Assert.Equal(0, await CallSysConnect(env, (uint)fd, 0x46000, 16));

        var written = await CallSysWriteV(env, (uint)fd, 0x49000, 2);
        Assert.Equal(9, written);

        var receiveBuffer = new byte[64];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        var received = server.ReceiveFrom(receiveBuffer, 0, receiveBuffer.Length, SocketFlags.None, ref remote);
        Assert.Equal(9, received);
        Assert.Equal("alphabeta", Encoding.ASCII.GetString(receiveBuffer, 0, received));
    }

    [Fact]
    public async Task Socket_PrivateNetwork_Dgram_Connect_ReportsPeerAndEphemeralLocalPort()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;
        env.MapUserPage(0x28000);
        env.MapUserPage(0x29000);
        env.MapUserPage(0x2A000);
        env.MapUserPage(0x2B000);
        env.WriteInt32(0x29000, 16);
        env.WriteInt32(0x2B000, 16);

        WriteSockaddrIn(env, 0x28000, 0x7F000001u, 19220);
        var fd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0);
        Assert.True(fd >= 0);

        Assert.Equal(0, await CallSysConnect(env, (uint)fd, 0x28000, 16));

        Assert.Equal(0, await CallSysGetPeerName(env, (uint)fd, 0x2A000, 0x2B000));
        var (peerIp, peerPort) = ReadSockaddrIn(env, 0x2A000);
        Assert.Equal(0x7F000001u, peerIp);
        Assert.Equal(19220, peerPort);

        env.WriteInt32(0x29000, 16);
        Assert.Equal(0, await CallSysGetSockName(env, (uint)fd, 0x28000, 0x29000));
        var (localIp, localPort) = ReadSockaddrIn(env, 0x28000);
        Assert.Equal(0x00000000u, localIp);
        Assert.True(localPort >= 49152);
    }

    [Fact]
    public async Task Socket_PrivateNetwork_GetSockNameAndPeerName_ReportLoopbackEndpoints()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;
        env.MapUserPage(0x17000);
        env.MapUserPage(0x18000);
        env.MapUserPage(0x19000);
        env.MapUserPage(0x1A000);
        env.MapUserPage(0x1B000);
        env.MapUserPage(0x1C000);

        WriteSockaddrIn(env, 0x17000, 0x7F000001u, 19090);
        WriteSockaddrIn(env, 0x18000, 0x7F000001u, 19090);
        env.WriteInt32(0x1A000, 16);
        env.WriteInt32(0x1C000, 16);

        var serverFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_STREAM, 0);
        var clientFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_STREAM, 0);
        Assert.True(serverFd >= 0 && clientFd >= 0);

        Assert.Equal(0, await CallSysBind(env, (uint)serverFd, 0x17000, 16));
        Assert.Equal(0, await CallSysListen(env, (uint)serverFd, 1));
        Assert.Equal(0, await CallSysConnect(env, (uint)clientFd, 0x18000, 16));

        var clientNameRc = await CallSysGetSockName(env, (uint)clientFd, 0x19000, 0x1A000);
        Assert.Equal(0, clientNameRc);
        Assert.Equal(16, env.ReadInt32(0x1A000));
        var (clientIp, clientPort) = ReadSockaddrIn(env, 0x19000);
        Assert.Equal(0x7F000001u, clientIp);
        Assert.True(clientPort >= 49152);

        var peerRc = await CallSysGetPeerName(env, (uint)clientFd, 0x1B000, 0x1C000);
        Assert.Equal(0, peerRc);
        Assert.Equal(16, env.ReadInt32(0x1C000));
        var (peerIp, peerPort) = ReadSockaddrIn(env, 0x1B000);
        Assert.Equal(0x7F000001u, peerIp);
        Assert.Equal(19090, peerPort);
    }

    [Fact]
    public async Task Socket_PrivateNetwork_Accept_HalfNullPeerAddress_ReturnsEfault()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;
        env.MapUserPage(0x41000);
        env.MapUserPage(0x42000);
        env.MapUserPage(0x43000);

        WriteSockaddrIn(env, 0x41000, 0x7F000001u, 19110);
        WriteSockaddrIn(env, 0x42000, 0x7F000001u, 19110);
        env.WriteInt32(0x43000, 16);

        var serverFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_STREAM, 0);
        var clientFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_STREAM, 0);
        Assert.True(serverFd >= 0 && clientFd >= 0);

        Assert.Equal(0, await CallSysBind(env, (uint)serverFd, 0x41000, 16));
        Assert.Equal(0, await CallSysListen(env, (uint)serverFd, 1));
        Assert.Equal(0, await CallSysConnect(env, (uint)clientFd, 0x42000, 16));

        var rc = await CallSysAccept(env, (uint)serverFd, 0x41000, 0);

        Assert.Equal(-(int)Errno.EFAULT, rc);
    }

    [Fact]
    public async Task Socket_PrivateNetwork_RecvFrom_HalfNullPeerAddress_ReturnsEfault()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;
        env.MapUserPage(0x44000);
        env.MapUserPage(0x45000);
        env.MapUserPage(0x46000);
        env.MapUserPage(0x47000);
        env.MapUserPage(0x48000);

        WriteSockaddrIn(env, 0x44000, 0x7F000001u, 19120);
        WriteSockaddrIn(env, 0x45000, 0x7F000001u, 19120);
        env.WriteInt32(0x48000, 16);
        env.WriteBytes(0x47000, Encoding.ASCII.GetBytes("ping"));

        var receiverFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0);
        var senderFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0);
        Assert.True(receiverFd >= 0 && senderFd >= 0);

        Assert.Equal(0, await CallSysBind(env, (uint)receiverFd, 0x44000, 16));
        Assert.Equal(4, await CallSysSendTo(env, (uint)senderFd, 0x47000, 4, 0, 0x45000, 16));

        var rc = await CallSysRecvFrom(env, (uint)receiverFd, 0x46000, 16, 0, 0x44000, 0);

        Assert.Equal(-(int)Errno.EFAULT, rc);
    }

    [Fact]
    public async Task Socket_PrivateNetwork_RecvFrom_NullPeerAddressPair_IsAllowed()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;
        env.MapUserPage(0x49000);
        env.MapUserPage(0x4A000);
        env.MapUserPage(0x4B000);
        env.MapUserPage(0x4C000);

        WriteSockaddrIn(env, 0x49000, 0x7F000001u, 19130);
        WriteSockaddrIn(env, 0x4A000, 0x7F000001u, 19130);
        env.WriteBytes(0x4C000, Encoding.ASCII.GetBytes("pong"));

        var receiverFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0);
        var senderFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0);
        Assert.True(receiverFd >= 0 && senderFd >= 0);

        Assert.Equal(0, await CallSysBind(env, (uint)receiverFd, 0x49000, 16));
        Assert.Equal(4, await CallSysSendTo(env, (uint)senderFd, 0x4C000, 4, 0, 0x4A000, 16));

        var rc = await CallSysRecvFrom(env, (uint)receiverFd, 0x4B000, 16, 0, 0, 0);

        Assert.Equal(4, rc);
        Assert.Equal("pong", Encoding.ASCII.GetString(env.ReadBytes(0x4B000, 4)));
    }

    [Fact]
    public async Task Socket_PrivateNetwork_SendMsgRecvMsg_StreamPayload_Works()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;
        env.MapUserPage(0x1D000);
        env.MapUserPage(0x1E000);
        env.MapUserPage(0x1F000);
        env.MapUserPage(0x20000);
        env.MapUserPage(0x21000);
        env.MapUserPage(0x22000);
        env.MapUserPage(0x23000);
        env.MapUserPage(0x24000);
        env.MapUserPage(0x25000);

        WriteSockaddrIn(env, 0x1D000, 0x7F000001u, 19100);
        WriteSockaddrIn(env, 0x1E000, 0x7F000001u, 19100);

        var serverFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_STREAM, 0);
        var clientFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_STREAM, 0);
        Assert.True(serverFd >= 0 && clientFd >= 0);

        Assert.Equal(0, await CallSysBind(env, (uint)serverFd, 0x1D000, 16));
        Assert.Equal(0, await CallSysListen(env, (uint)serverFd, 1));
        Assert.Equal(0, await CallSysConnect(env, (uint)clientFd, 0x1E000, 16));

        var acceptedFd = await CallSysAccept(env, (uint)serverFd, 0, 0);
        Assert.True(acceptedFd >= 0);

        var payload = "netmsg";
        env.WriteBytes(0x22000, Encoding.ASCII.GetBytes(payload));
        WriteIovec(env, 0x20000, 0x22000, payload.Length);
        WriteMsgHdr(env, 0x1F000, 0x20000, 1, 0, 0, 0, 0);

        Assert.Equal(payload.Length, await CallSysSendMsg(env, (uint)clientFd, 0x1F000, 0));

        WriteIovec(env, 0x24000, 0x25000, 16);
        WriteMsgHdr(env, 0x23000, 0x24000, 1, 0, 0, 0, 0);
        var recv = await CallSysRecvMsg(env, (uint)acceptedFd, 0x23000, 0);
        Assert.Equal(payload.Length, recv);
        Assert.Equal(payload, Encoding.ASCII.GetString(env.ReadBytes(0x25000, recv)));
    }

    [Fact]
    public async Task Socket_PrivateNetwork_ShutdownWrite_MakesFurtherSendFailWithEpipe()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;
        env.MapUserPage(0x30000);
        env.MapUserPage(0x31000);
        env.MapUserPage(0x32000);

        WriteSockaddrIn(env, 0x30000, 0x7F000001u, 19140);
        WriteSockaddrIn(env, 0x31000, 0x7F000001u, 19140);
        env.WriteBytes(0x32000, [1, 2, 3, 4]);

        var serverFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_STREAM, 0);
        var clientFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_STREAM, 0);
        Assert.True(serverFd >= 0 && clientFd >= 0);

        Assert.Equal(0, await CallSysBind(env, (uint)serverFd, 0x30000, 16));
        Assert.Equal(0, await CallSysListen(env, (uint)serverFd, 1));
        Assert.Equal(0, await CallSysConnect(env, (uint)clientFd, 0x31000, 16));

        var acceptedFd = await CallSysAccept(env, (uint)serverFd, 0, 0);
        Assert.True(acceptedFd >= 0);

        Assert.Equal(0, await CallSysShutdown(env, (uint)clientFd, 1));
        Assert.Equal(-(int)Errno.EPIPE, await CallSysSend(env, (uint)clientFd, 0x32000u, 4u));
    }

    [Fact]
    public async Task Socket_PrivateNetwork_BindPortZero_Listen_WriteRead_AndCloseReleasePort_Works()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;
        env.MapUserPage(0x30000);
        env.MapUserPage(0x31000);
        env.MapUserPage(0x32000);

        WriteSockaddrIn(env, 0x30000, 0x7F000001u, 0);
        env.WriteInt32(0x31000, 16);

        var serverFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_STREAM, 0);
        Assert.True(serverFd >= 0);

        Assert.Equal(0, await CallSysBind(env, (uint)serverFd, 0x30000, 16));
        Assert.Equal(0, await CallSysGetSockName(env, (uint)serverFd, 0x30000, 0x31000));
        var (boundIp, boundPort) = ReadSockaddrIn(env, 0x30000);
        Assert.Equal(0x7F000001u, boundIp);
        Assert.True(boundPort >= 49152);

        Assert.Equal(0, await CallSysListen(env, (uint)serverFd, 16));

        // Duplicate bind on the same port returns EADDRINUSE (never EFAULT).
        var dupFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_STREAM, 0);
        Assert.True(dupFd >= 0);
        WriteSockaddrIn(env, 0x32000, 0x7F000001u, boundPort);
        Assert.Equal(-(int)Errno.EADDRINUSE, await CallSysBind(env, (uint)dupFd, 0x32000, 16));

        var clientFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_STREAM, 0);
        Assert.True(clientFd >= 0);
        Assert.Equal(0, await CallSysConnect(env, (uint)clientFd, 0x32000, 16));

        var acceptedFd = await CallSysAccept(env, (uint)serverFd, 0, 0);
        Assert.True(acceptedFd >= 0);

        // Stream write() / read() must go through NetstackSocketInode ReadV/WriteV instead of returning 0.
        var req = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n\r\n");
        env.WriteBytes(0x32000, req);
        Assert.Equal(req.Length, await CallSysWrite(env, (uint)clientFd, 0x32000, (uint)req.Length));

        var read = await CallSysRead(env, (uint)acceptedFd, 0x31000, 64);
        Assert.Equal(req.Length, read);
        Assert.Equal("GET / HTTP/1.1\r\n\r\n", Encoding.ASCII.GetString(env.ReadBytes(0x31000, read)));

        // Closing the server FD releases the reserved port so dupFd can bind it.
        Assert.Equal(0, await CallSysClose(env, (uint)serverFd));
        WriteSockaddrIn(env, 0x32000, 0x7F000001u, boundPort);
        Assert.Equal(0, await CallSysBind(env, (uint)dupFd, 0x32000, 16));
        Assert.Equal(0, await CallSysListen(env, (uint)dupFd, 4));
    }

    [Fact]
    public async Task Socket_PrivateNetwork_UnboundListen_AutoAssignsEphemeralPort()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;
        env.MapUserPage(0x30000);
        env.MapUserPage(0x31000);
        env.WriteInt32(0x31000, 16);

        var serverFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_STREAM, 0);
        Assert.True(serverFd >= 0);

        Assert.Equal(0, await CallSysListen(env, (uint)serverFd, 8));
        Assert.Equal(0, await CallSysGetSockName(env, (uint)serverFd, 0x30000, 0x31000));
        var (boundIp, boundPort) = ReadSockaddrIn(env, 0x30000);
        Assert.Equal(0x00000000u, boundIp);
        Assert.True(boundPort >= 49152);
    }

    [Fact]
    public async Task Socket_PrivateNetwork_CloseAfter32KibWrite_DrainsAllBytesToPeer()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;
        env.MapUserPage(0x30000);
        env.MapUserPage(0x31000);
        for (uint addr = 0x32000; addr < 0x36000; addr += LinuxConstants.PageSize)
            env.MapUserPage(addr);
        for (uint addr = 0x36000; addr < 0x3A000; addr += LinuxConstants.PageSize)
            env.MapUserPage(addr);

        WriteSockaddrIn(env, 0x30000, 0x7F000001u, 19400);
        WriteSockaddrIn(env, 0x31000, 0x7F000001u, 19400);

        var serverFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_STREAM, 0);
        var clientFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_STREAM, 0);
        Assert.True(serverFd >= 0 && clientFd >= 0);

        Assert.Equal(0, await CallSysBind(env, (uint)serverFd, 0x30000, 16));
        Assert.Equal(0, await CallSysListen(env, (uint)serverFd, 4));
        Assert.Equal(0, await CallSysConnect(env, (uint)clientFd, 0x31000, 16));

        var acceptedFd = await CallSysAccept(env, (uint)serverFd, 0, 0);
        Assert.True(acceptedFd >= 0);

        var chunk1 = new byte[16 * 1024];
        var chunk2 = new byte[16 * 1024];
        for (var i = 0; i < chunk1.Length; i++)
        {
            chunk1[i] = (byte)(i & 0xFF);
            chunk2[i] = (byte)((i * 3 + 5) & 0xFF);
        }

        env.WriteBytes(0x32000, chunk1);
        Assert.Equal(chunk1.Length, await CallSysWrite(env, (uint)clientFd, 0x32000, (uint)chunk1.Length));
        env.WriteBytes(0x32000, chunk2);
        Assert.Equal(chunk2.Length, await CallSysWrite(env, (uint)clientFd, 0x32000, (uint)chunk2.Length));

        // Close sender immediately after the two buffered writes return success.
        Assert.Equal(0, await CallSysClose(env, (uint)clientFd));

        var received = new List<byte>(chunk1.Length + chunk2.Length);
        while (true)
        {
            var n = await CallSysRead(env, (uint)acceptedFd, 0x36000, 16 * 1024);
            Assert.True(n >= 0);
            if (n == 0)
                break;
            received.AddRange(env.ReadBytes(0x36000, n));
        }

        Assert.Equal(chunk1.Length + chunk2.Length, received.Count);
        Assert.Equal(chunk1, received.GetRange(0, chunk1.Length).ToArray());
        Assert.Equal(chunk2, received.GetRange(chunk1.Length, chunk2.Length).ToArray());
    }

    [Fact]
    public async Task Socket_PrivateNetwork_EpollEdgeTriggered_DoesNotRepeatWithoutIoAndRearmsAfterDrain()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;
        env.MapUserPage(0x30000);
        env.MapUserPage(0x31000);
        env.MapUserPage(0x32000);
        env.MapUserPage(0x33000);
        env.MapUserPage(0x34000);

        WriteSockaddrIn(env, 0x30000, 0x7F000001u, 19410);
        WriteSockaddrIn(env, 0x31000, 0x7F000001u, 19410);

        var serverFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_STREAM, 0);
        var clientFd = await CallSysSocket(env, LinuxConstants.AF_INET,
            LinuxConstants.SOCK_STREAM | LinuxConstants.SOCK_NONBLOCK, 0);
        Assert.True(serverFd >= 0 && clientFd >= 0);

        Assert.Equal(0, await CallSysBind(env, (uint)serverFd, 0x30000, 16));
        Assert.Equal(0, await CallSysListen(env, (uint)serverFd, 4));
        var connectRc = await CallSysConnect(env, (uint)clientFd, 0x31000, 16);
        Assert.True(connectRc == 0 || connectRc == -(int)Errno.EINPROGRESS);

        var acceptedFd = await CallSysAccept(env, (uint)serverFd, 0, 0);
        Assert.True(acceptedFd >= 0);

        var epfd = await env.Invoke("SysEpollCreate1", 0, 0, 0, 0, 0, 0);
        Assert.True(epfd >= 0);

        var ev = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(ev.AsSpan(0, 4),
            LinuxConstants.EPOLLOUT | LinuxConstants.EPOLLET);
        BinaryPrimitives.WriteUInt64LittleEndian(ev.AsSpan(4, 8), 0xCAFEBABEUL);
        env.WriteBytes(0x32000, ev);
        Assert.Equal(0,
            await env.Invoke("SysEpollCtl", (uint)epfd, LinuxConstants.EPOLL_CTL_ADD, (uint)clientFd, 0x32000, 0, 0));

        // First harvest observes EPOLLOUT.
        Assert.Equal(1, await env.Invoke("SysEpollWait", (uint)epfd, 0x33000, 1, 100, 0, 0));
        Assert.Equal(LinuxConstants.EPOLLOUT,
            BinaryPrimitives.ReadUInt32LittleEndian(env.ReadBytes(0x33000, 12).AsSpan(0, 4)));

        // Without performing any I/O, waiting past polling timer intervals must NOT fire again.
        Assert.Equal(0, await env.Invoke("SysEpollWait", (uint)epfd, 0x33000, 1, 40, 0, 0));

        // Switch watch to EPOLLIN | EPOLLET and test partial read vs full drain.
        BinaryPrimitives.WriteUInt32LittleEndian(ev.AsSpan(0, 4),
            LinuxConstants.EPOLLIN | LinuxConstants.EPOLLET);
        env.WriteBytes(0x32000, ev);
        Assert.Equal(0,
            await env.Invoke("SysEpollCtl", (uint)epfd, LinuxConstants.EPOLL_CTL_MOD, (uint)clientFd, 0x32000, 0, 0));

        env.WriteBytes(0x34000, Encoding.ASCII.GetBytes("hello!"));
        Assert.Equal(6, await CallSysWrite(env, (uint)acceptedFd, 0x34000, 6));

        Assert.Equal(1, await env.Invoke("SysEpollWait", (uint)epfd, 0x33000, 1, 100, 0, 0));

        // Partial read leaves bytes buffered -> no new edge.
        Assert.Equal(2, await CallSysRead(env, (uint)clientFd, 0x34000, 2));
        Assert.Equal(0, await env.Invoke("SysEpollWait", (uint)epfd, 0x33000, 1, 35, 0, 0));

        // Drain the rest (4 bytes), then peer sends new data -> new edge fires.
        Assert.Equal(4, await CallSysRead(env, (uint)clientFd, 0x34000, 8));
        Assert.Equal(-(int)Errno.EAGAIN, await CallSysRead(env, (uint)clientFd, 0x34000, 8));

        env.WriteBytes(0x34000, Encoding.ASCII.GetBytes("ok"));
        Assert.Equal(2, await CallSysWrite(env, (uint)acceptedFd, 0x34000, 2));
        Assert.Equal(1, await env.Invoke("SysEpollWait", (uint)epfd, 0x33000, 1, 100, 0, 0));
    }

    [Fact]
    public async Task Socket_PrivateNetwork_UnconnectedUdpReadAndReadV_Succeed()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;
        env.MapUserPage(0x49000);
        env.MapUserPage(0x4A000);
        env.MapUserPage(0x4B000);
        env.MapUserPage(0x4C000);
        env.MapUserPage(0x4D000);

        WriteSockaddrIn(env, 0x49000, 0x7F000001u, 19420);
        WriteSockaddrIn(env, 0x4A000, 0x7F000001u, 19420);

        var receiverFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0);
        var senderFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0);
        Assert.True(receiverFd >= 0 && senderFd >= 0);

        Assert.Equal(0, await CallSysBind(env, (uint)receiverFd, 0x49000, 16));

        env.WriteBytes(0x4C000, Encoding.ASCII.GetBytes("abc"));
        Assert.Equal(3, await CallSysSendTo(env, (uint)senderFd, 0x4C000, 3, 0, 0x4A000, 16));

        var readRc = await CallSysRead(env, (uint)receiverFd, 0x4B000, 16);
        Assert.Equal(3, readRc);
        Assert.Equal("abc", Encoding.ASCII.GetString(env.ReadBytes(0x4B000, 3)));

        env.WriteBytes(0x4C000, Encoding.ASCII.GetBytes("xyz"));
        Assert.Equal(3, await CallSysSendTo(env, (uint)senderFd, 0x4C000, 3, 0, 0x4A000, 16));

        WriteIovec(env, 0x4D000, 0x4B000, 1);
        WriteIovec(env, 0x4D008, 0x4B010, 2);
        var readvRc = await CallSysReadV(env, (uint)receiverFd, 0x4D000, 2);
        Assert.Equal(3, readvRc);
        Assert.Equal("x", Encoding.ASCII.GetString(env.ReadBytes(0x4B000, 1)));
        Assert.Equal("yz", Encoding.ASCII.GetString(env.ReadBytes(0x4B010, 2)));
    }

    [Fact]
    public async Task Socket_PrivateNetwork_BoundWithoutListen_PreventsEphemeralConnectCollision()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;
        env.MapUserPage(0x30000);
        env.MapUserPage(0x31000);
        env.MapUserPage(0x32000);
        env.MapUserPage(0x33000);
        env.WriteInt32(0x33000, 16);

        // Bind socketA to 49152 without calling listen().
        WriteSockaddrIn(env, 0x30000, 0x7F000001u, 49152);
        var boundOnlyFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_STREAM, 0);
        Assert.True(boundOnlyFd >= 0);
        Assert.Equal(0, await CallSysBind(env, (uint)boundOnlyFd, 0x30000, 16));

        // Listen on another port and connect a client; native ephemeral allocation must skip 49152.
        WriteSockaddrIn(env, 0x31000, 0x7F000001u, 19430);
        var serverFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_STREAM, 0);
        var clientFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_STREAM, 0);
        Assert.True(serverFd >= 0 && clientFd >= 0);

        Assert.Equal(0, await CallSysBind(env, (uint)serverFd, 0x31000, 16));
        Assert.Equal(0, await CallSysListen(env, (uint)serverFd, 4));
        Assert.Equal(0, await CallSysConnect(env, (uint)clientFd, 0x31000, 16));

        Assert.Equal(0, await CallSysGetSockName(env, (uint)clientFd, 0x32000, 0x33000));
        var (_, clientPort) = ReadSockaddrIn(env, 0x32000);
        Assert.NotEqual(49152, clientPort);
    }

    [Fact]
    public async Task Socket_PrivateNetwork_ManyIdleWatches_ShareOneNamespacePollPumpWithLowAllocation()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;
        var dispatcher = new SchedulerReadyDispatcher(env.Scheduler);

        var registrations = new List<IDisposable>(256);
        var files = new List<LinuxFile>(256);
        var fireCounts = new int[256];

        try
        {
            for (var i = 0; i < 256; i++)
            {
                var fd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0);
                Assert.True(fd >= 0);
                var file = Assert.IsType<LinuxFile>(env.SyscallManager.GetFD(fd));
                var inode = Assert.IsType<NetstackSocketInode>(file.OpenedInode);
                if (i == 0)
                {
                    env.MapUserPage(0x30000);
                    WriteSockaddrIn(env, 0x30000, 0x7F000001u, 19440);
                    Assert.Equal(0, await CallSysBind(env, (uint)fd, 0x30000, 16));
                }

                var idx = i;
                var reg = ((IDispatcherWaitSource)inode).RegisterWaitHandle(file, dispatcher,
                    () => Interlocked.Increment(ref fireCounts[idx]), PollEvents.POLLIN);
                Assert.NotNull(reg);
                registrations.Add(reg!);
                files.Add(file);
            }

            // Warm up one tick, then measure 1000ms of virtual-time timer advances.
            var startTick = env.Scheduler.CurrentTick;
            env.Scheduler.TimerSystem.Advance(startTick + 10);

            var allocBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var ms = 20; ms <= 1010; ms += 10)
                env.Scheduler.TimerSystem.Advance(startTick + ms);
            var allocAfter = GC.GetAllocatedBytesForCurrentThread();

            Assert.True(allocAfter - allocBefore < 64 * 1024,
                $"Expected < 64 KiB managed allocation for 1s of shared pump polling, got {allocAfter - allocBefore} bytes");
            Assert.All(fireCounts, count => Assert.Equal(0, count));

            // Sending a UDP packet to socket 0 should wake its watch on the next pump tick.
            var senderFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_DGRAM, 0);
            Assert.True(senderFd >= 0);
            env.MapUserPage(0x31000);
            env.WriteBytes(0x31000, Encoding.ASCII.GetBytes("wake"));
            Assert.Equal(4, await CallSysSendTo(env, (uint)senderFd, 0x31000, 4, 0, 0x30000, 16));

            env.Scheduler.TimerSystem.Advance(env.Scheduler.CurrentTick + 25);
            Assert.Equal(1, fireCounts[0]);
        }
        finally
        {
            foreach (var reg in registrations)
                reg.Dispose();
        }

        env.Scheduler.TimerSystem.Advance(env.Scheduler.CurrentTick + 20);
        Assert.False(env.Scheduler.TimerSystem.HasTimers);
        Assert.Equal(1, fireCounts[0]);
    }

    [Fact]
    public async Task Socket_PrivateNetwork_WriteV_64Kib_CapsToTxCapacityAndTransfersAllData()
    {
        using var env = new TestEnv();
        env.SyscallManager.NetworkMode = NetworkMode.Private;
        env.MapUserPage(0x30000);
        env.MapUserPage(0x31000);
        env.MapUserPage(0x32000);
        for (uint addr = 0x40000; addr < 0x50000; addr += LinuxConstants.PageSize)
            env.MapUserPage(addr);
        for (uint addr = 0x50000; addr < 0x60000; addr += LinuxConstants.PageSize)
            env.MapUserPage(addr);

        WriteSockaddrIn(env, 0x30000, 0x7F000001u, 19450);
        WriteSockaddrIn(env, 0x31000, 0x7F000001u, 19450);

        var serverFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_STREAM, 0);
        var clientFd = await CallSysSocket(env, LinuxConstants.AF_INET, LinuxConstants.SOCK_STREAM, 0);
        Assert.True(serverFd >= 0 && clientFd >= 0);

        Assert.Equal(0, await CallSysBind(env, (uint)serverFd, 0x30000, 16));
        Assert.Equal(0, await CallSysListen(env, (uint)serverFd, 4));
        Assert.Equal(0, await CallSysConnect(env, (uint)clientFd, 0x31000, 16));
        var acceptedFd = await CallSysAccept(env, (uint)serverFd, 0, 0);
        Assert.True(acceptedFd >= 0);

        var payload = new byte[64 * 1024];
        for (var i = 0; i < payload.Length; i++)
            payload[i] = (byte)((i * 7 + 13) & 0xFF);
        env.WriteBytes(0x40000, payload);

        // 4 x 16 KiB iovecs -> WriteV writes up to native available capacity (16 KiB)
        // without copying or discarding the remaining 48 KiB.
        for (var i = 0; i < 4; i++)
            WriteIovec(env, 0x32000 + (uint)(i * 8), 0x40000 + (uint)(i * 16 * 1024), 16 * 1024);

        var written1 = await CallSysWriteV(env, (uint)clientFd, 0x32000, 4);
        Assert.Equal(16 * 1024, written1);

        // Non-blocking WriteV when TX buffer is full returns EAGAIN cleanly.
        var clientFile = Assert.IsType<LinuxFile>(env.SyscallManager.GetFD(clientFd));
        clientFile.Flags |= FileFlags.O_NONBLOCK;
        Assert.Equal(-(int)Errno.EAGAIN, await CallSysWriteV(env, (uint)clientFd, 0x32008, 3));
        clientFile.Flags &= ~FileFlags.O_NONBLOCK;

        var received = new List<byte>(payload.Length);
        var offset = written1;
        while (received.Count < payload.Length)
        {
            while (received.Count < offset)
            {
                var n = await CallSysRead(env, (uint)acceptedFd, 0x50000, 32 * 1024);
                Assert.True(n > 0);
                received.AddRange(env.ReadBytes(0x50000, n));
            }

            if (offset < payload.Length)
            {
                WriteIovec(env, 0x32020, 0x40000 + (uint)offset, payload.Length - offset);
                var nextWritten = await CallSysWriteV(env, (uint)clientFd, 0x32020, 1);
                Assert.True(nextWritten > 0);
                offset += nextWritten;
            }
        }

        Assert.Equal(payload, received.ToArray());
    }

    [Fact]
    public async Task Socket_UnixPath_BindListenConnectAccept_SendRecv_Works()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x33000);
        env.MapUserPage(0x34000);
        env.MapUserPage(0x35000);
        env.MapUserPage(0x36000);

        var path = $"/sock_unix_syscall_test_{Guid.NewGuid():N}";
        var sockaddrLen = WriteSockaddrUn(env, 0x33000, path);
        WriteSockaddrUn(env, 0x34000, path);

        var serverFd = await CallSysSocket(env, LinuxConstants.AF_UNIX, LinuxConstants.SOCK_STREAM, 0);
        var clientFd = await CallSysSocket(env, LinuxConstants.AF_UNIX, LinuxConstants.SOCK_STREAM, 0);
        Assert.True(serverFd >= 0 && clientFd >= 0);

        Assert.Equal(0, await CallSysBind(env, (uint)serverFd, 0x33000, (uint)sockaddrLen));
        Assert.Equal(0, await CallSysListen(env, (uint)serverFd, 1));
        Assert.Equal(0, await CallSysConnect(env, (uint)clientFd, 0x34000, (uint)sockaddrLen));

        var acceptedFd = await CallSysAccept(env, (uint)serverFd, 0, 0);
        Assert.True(acceptedFd >= 0);

        var payload = Encoding.ASCII.GetBytes("unix");
        env.WriteBytes(0x35000, payload);
        Assert.Equal(payload.Length, await CallSysSend(env, (uint)clientFd, 0x35000, (uint)payload.Length));

        var recv = await CallSysRecvFrom(env, (uint)acceptedFd, 0x36000, 16, 0);
        Assert.Equal(payload.Length, recv);
        Assert.Equal("unix", Encoding.ASCII.GetString(env.ReadBytes(0x36000, recv)));
    }

    [Fact]
    public async Task Socket_UnixSocketPair_CopyToUserFault_RollsBackDescriptors()
    {
        using var env = new TestEnv();

        var rc = await CallSysSocketPair(env, LinuxConstants.AF_UNIX, LinuxConstants.SOCK_STREAM, 0, 0xDEAD0000);
        Assert.Equal(-(int)Errno.EFAULT, rc);
        Assert.Empty(env.SyscallManager.FDs);
    }

    [Fact]
    public async Task Socket_UnixPath_UnlinkThenRebind_WhileOriginalOpen_Works()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x47000);
        env.MapUserPage(0x48000);

        var path = $"/sock_unix_rebind_test_{Guid.NewGuid():N}";
        var sockaddrLen = WriteSockaddrUn(env, 0x47000, path);
        env.WriteBytes(0x48000, Encoding.UTF8.GetBytes(path + '\0'));

        var firstFd = await CallSysSocket(env, LinuxConstants.AF_UNIX, LinuxConstants.SOCK_STREAM, 0);
        Assert.True(firstFd >= 0);
        Assert.Equal(0, await CallSysBind(env, (uint)firstFd, 0x47000, (uint)sockaddrLen));
        Assert.True(env.SyscallManager.PathWalk(path).IsValid);

        Assert.Equal(0, await CallSysUnlink(env, 0x48000));
        Assert.False(env.SyscallManager.PathWalk(path).IsValid);

        var secondFd = await CallSysSocket(env, LinuxConstants.AF_UNIX, LinuxConstants.SOCK_STREAM, 0);
        Assert.True(secondFd >= 0);
        Assert.Equal(0, await CallSysBind(env, (uint)secondFd, 0x47000, (uint)sockaddrLen));
        Assert.True(env.SyscallManager.PathWalk(path).IsValid);
    }

    [Fact]
    public async Task Socket_UnixPath_Rename_KeepsEndpointReachableViaNewPath()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x4D000);
        env.MapUserPage(0x4E000);
        env.MapUserPage(0x4F000);
        env.MapUserPage(0x50000);

        var oldPath = $"/sock_unix_rename_old_{Guid.NewGuid():N}";
        var newPath = $"/sock_unix_rename_new_{Guid.NewGuid():N}";
        var oldLen = WriteSockaddrUn(env, 0x4D000, oldPath);
        var newLen = WriteSockaddrUn(env, 0x4E000, newPath);
        WriteCString(env, 0x4F000, oldPath);
        WriteCString(env, 0x50000, newPath);

        var serverFd = await CallSysSocket(env, LinuxConstants.AF_UNIX, LinuxConstants.SOCK_STREAM, 0);
        var clientFd = await CallSysSocket(env, LinuxConstants.AF_UNIX, LinuxConstants.SOCK_STREAM, 0);
        Assert.True(serverFd >= 0 && clientFd >= 0);

        Assert.Equal(0, await CallSysBind(env, (uint)serverFd, 0x4D000, (uint)oldLen));
        Assert.Equal(0, await CallSysListen(env, (uint)serverFd, 1));
        Assert.Equal(0, await CallSysRename(env, 0x4F000, 0x50000));
        Assert.False(env.SyscallManager.PathWalk(oldPath).IsValid);
        Assert.True(env.SyscallManager.PathWalk(newPath).IsValid);

        WriteSockaddrUn(env, 0x4E000, newPath);
        var raw = env.ReadBytes(0x4E000, newLen);
        var nul = Array.IndexOf(raw, (byte)0, 2);
        Assert.True(nul > 2);
        Assert.Equal(newPath, Encoding.UTF8.GetString(raw, 2, nul - 2));
        Assert.Equal(0, await CallSysConnect(env, (uint)clientFd, 0x4E000, (uint)newLen));
    }

    [Fact]
    public async Task Socket_UnixPath_LinkAlias_IsReachableViaAliasPath()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x51000);
        env.MapUserPage(0x52000);
        env.MapUserPage(0x53000);
        env.MapUserPage(0x54000);

        var basePath = $"/sock_unix_link_base_{Guid.NewGuid():N}";
        var aliasPath = $"/sock_unix_link_alias_{Guid.NewGuid():N}";
        var baseLen = WriteSockaddrUn(env, 0x51000, basePath);
        var aliasLen = WriteSockaddrUn(env, 0x52000, aliasPath);
        WriteCString(env, 0x53000, basePath);
        WriteCString(env, 0x54000, aliasPath);

        var serverFd = await CallSysSocket(env, LinuxConstants.AF_UNIX, LinuxConstants.SOCK_STREAM, 0);
        var clientFd = await CallSysSocket(env, LinuxConstants.AF_UNIX, LinuxConstants.SOCK_STREAM, 0);
        Assert.True(serverFd >= 0 && clientFd >= 0);

        Assert.Equal(0, await CallSysBind(env, (uint)serverFd, 0x51000, (uint)baseLen));
        Assert.Equal(0, await CallSysListen(env, (uint)serverFd, 1));
        Assert.Equal(0, await CallSysLink(env, 0x53000, 0x54000));
        Assert.True(env.SyscallManager.PathWalk(aliasPath).IsValid);

        Assert.Equal(0, await CallSysConnect(env, (uint)clientFd, 0x52000, (uint)aliasLen));
    }

    [Fact]
    public async Task Socket_UnixPath_CloseBoundSocket_RemovesActiveEndpointButKeepsNode()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x55000);
        env.MapUserPage(0x56000);
        env.MapUserPage(0x57000);

        var path = $"/sock_unix_close_bound_{Guid.NewGuid():N}";
        var sockaddrLen = WriteSockaddrUn(env, 0x55000, path);
        WriteCString(env, 0x56000, path);

        var serverFd = await CallSysSocket(env, LinuxConstants.AF_UNIX, LinuxConstants.SOCK_STREAM, 0);
        var clientFd = await CallSysSocket(env, LinuxConstants.AF_UNIX, LinuxConstants.SOCK_STREAM, 0);
        Assert.True(serverFd >= 0 && clientFd >= 0);

        Assert.Equal(0, await CallSysBind(env, (uint)serverFd, 0x55000, (uint)sockaddrLen));
        Assert.Equal(0, await CallSysListen(env, (uint)serverFd, 1));
        Assert.Equal(0, await CallSysClose(env, (uint)serverFd));
        Assert.True(env.SyscallManager.PathWalk(path).IsValid);

        var rc = await CallSysConnect(env, (uint)clientFd, 0x55000, (uint)sockaddrLen);
        Assert.Equal(-(int)Errno.ECONNREFUSED, rc);
    }

    [Fact]
    public async Task Socket_UnixRecvMsg_ScmRights_MsgCmsgCloexec_DeliversCloexecFd()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x37000);
        env.MapUserPage(0x38000);
        env.MapUserPage(0x39000);
        env.MapUserPage(0x3A000);
        env.MapUserPage(0x3B000);
        env.MapUserPage(0x3C000);
        env.MapUserPage(0x3D000);
        env.MapUserPage(0x3E000);

        var spRc = await CallSysSocketPair(env, LinuxConstants.AF_UNIX, LinuxConstants.SOCK_DGRAM, 0, 0x37000);
        Assert.Equal(0, spRc);
        var sockA = env.ReadInt32(0x37000);
        var sockB = env.ReadInt32(0x37004);
        Assert.True(sockA >= 0 && sockB >= 0);

        var passFd = await CallSysSocket(env, LinuxConstants.AF_UNIX, LinuxConstants.SOCK_DGRAM, 0);
        Assert.True(passFd >= 0);

        env.WriteBytes(0x39000, [0x2A]);
        WriteIovec(env, 0x38000, 0x39000, 1);
        var sendControlLen = WriteScmRightsCmsg(env, 0x3A000, [passFd]);
        WriteMsgHdr(env, 0x3B000, 0x38000, 1, 0x3A000, sendControlLen, 0, 0);
        Assert.Equal(1, await CallSysSendMsg(env, (uint)sockA, 0x3B000, 0));

        WriteIovec(env, 0x3C000, 0x3D000, 8);
        WriteMsgHdr(env, 0x3E000, 0x3C000, 1, 0x3A000, 64, 0, 0);
        var recv = await CallSysRecvMsg(env, (uint)sockB, 0x3E000, LinuxConstants.MSG_CMSG_CLOEXEC);
        Assert.Equal(1, recv);
        Assert.Equal(16, env.ReadInt32(0x3E000 + 20));
        Assert.Equal(0, env.ReadInt32(0x3E000 + 24));

        var receivedFd = env.ReadInt32(0x3A000 + 12);
        Assert.True(receivedFd >= 0);
        Assert.True(env.SyscallManager.IsFdCloseOnExec(receivedFd));
    }

    [Fact]
    public async Task Socket_UnixRecvMsg_ScmRights_ControlTooSmall_SetsCtrunc()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x3F000);
        env.MapUserPage(0x40000);
        env.MapUserPage(0x41000);
        env.MapUserPage(0x42000);
        env.MapUserPage(0x43000);
        env.MapUserPage(0x44000);
        env.MapUserPage(0x45000);
        env.MapUserPage(0x46000);

        var spRc = await CallSysSocketPair(env, LinuxConstants.AF_UNIX, LinuxConstants.SOCK_DGRAM, 0, 0x3F000);
        Assert.Equal(0, spRc);
        var sockA = env.ReadInt32(0x3F000);
        var sockB = env.ReadInt32(0x3F004);
        Assert.True(sockA >= 0 && sockB >= 0);

        var passFd1 = await CallSysSocket(env, LinuxConstants.AF_UNIX, LinuxConstants.SOCK_DGRAM, 0);
        var passFd2 = await CallSysSocket(env, LinuxConstants.AF_UNIX, LinuxConstants.SOCK_DGRAM, 0);
        Assert.True(passFd1 >= 0 && passFd2 >= 0);

        env.WriteBytes(0x41000, [0x55]);
        WriteIovec(env, 0x40000, 0x41000, 1);
        var sendControlLen = WriteScmRightsCmsg(env, 0x42000, [passFd1, passFd2]);
        WriteMsgHdr(env, 0x43000, 0x40000, 1, 0x42000, sendControlLen, 0, 0);
        Assert.Equal(1, await CallSysSendMsg(env, (uint)sockA, 0x43000, 0));

        var fdCountBeforeRecv = env.SyscallManager.FDs.Count;
        WriteIovec(env, 0x44000, 0x45000, 8);
        WriteMsgHdr(env, 0x46000, 0x44000, 1, 0x42000, 16, 0, 0); // only room for 1 fd
        var recv = await CallSysRecvMsg(env, (uint)sockB, 0x46000, 0);
        Assert.Equal(1, recv);
        Assert.Equal(16, env.ReadInt32(0x46000 + 20));
        Assert.Equal(LinuxConstants.MSG_CTRUNC, env.ReadInt32(0x46000 + 24) & LinuxConstants.MSG_CTRUNC);
        Assert.Equal(fdCountBeforeRecv + 1, env.SyscallManager.FDs.Count);
        Assert.True(env.ReadInt32(0x42000 + 12) >= 0);
    }

    [Fact]
    public async Task Socket_UnixRecvFrom_MsgDontwait_ReturnsEagainWhenNoData()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x49000);
        env.MapUserPage(0x4A000);

        var spRc = await CallSysSocketPair(env, LinuxConstants.AF_UNIX, LinuxConstants.SOCK_DGRAM, 0, 0x49000);
        Assert.Equal(0, spRc);
        var receiverFd = env.ReadInt32(0x49004);
        Assert.True(receiverFd >= 0);

        var rc = await CallSysRecvFrom(env, (uint)receiverFd, 0x4A000, 8, LinuxConstants.MSG_DONTWAIT);
        Assert.Equal(-(int)Errno.EAGAIN, rc);
    }

    [Fact]
    public async Task Socket_UnixSend_MsgDontwait_ReturnsEagainWhenPeerBufferIsFull()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x4B000);
        env.MapUserPage(0x4C000);
        env.WriteBytes(0x4C000, new byte[4096]);

        var spRc = await CallSysSocketPair(env, LinuxConstants.AF_UNIX, LinuxConstants.SOCK_DGRAM, 0, 0x4B000);
        Assert.Equal(0, spRc);
        var senderFd = env.ReadInt32(0x4B000);
        Assert.True(senderFd >= 0);

        var sawEagain = false;
        for (var i = 0; i < 256; i++)
        {
            var rc = await CallSysSend(env, (uint)senderFd, 0x4C000, 4096, LinuxConstants.MSG_DONTWAIT);
            if (rc == -(int)Errno.EAGAIN)
            {
                sawEagain = true;
                break;
            }

            Assert.Equal(4096, rc);
        }

        Assert.True(sawEagain);
    }

    [Fact]
    public async Task SendMsg_UnixDatagram_MsgOob_ReturnsEopnotsupp()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x4D000);
        env.MapUserPage(0x4E000);
        env.MapUserPage(0x4F000);
        env.WriteBytes(0x4F000, [0x41]);

        Assert.Equal(0, await CallSysSocketPair(env, LinuxConstants.AF_UNIX,
            LinuxConstants.SOCK_DGRAM, 0, 0x4D000));
        var senderFd = env.ReadInt32(0x4D000);

        WriteIovec(env, 0x4E000, 0x4F000, 1);
        WriteMsgHdr(env, 0x4D800, 0x4E000, 1, 0, 0, 0, 0);
        Assert.Equal(-(int)Errno.EOPNOTSUPP,
            await CallSysSendMsg(env, (uint)senderFd, 0x4D800, LinuxConstants.MSG_OOB));
    }

    [Fact]
    public async Task SendMsg_InvalidCmsgPointer_ReturnsEfault()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x50000);
        env.MapUserPage(0x51000);
        env.MapUserPage(0x52000);
        env.WriteBytes(0x52000, [0x41]);

        Assert.Equal(0, await CallSysSocketPair(env, LinuxConstants.AF_UNIX,
            LinuxConstants.SOCK_DGRAM, 0, 0x50000));
        var senderFd = env.ReadInt32(0x50000);

        WriteIovec(env, 0x51000, 0x52000, 1);
        WriteMsgHdr(env, 0x50000 + 0x800, 0x51000, 1, 0xDEAD0000, 16, 0, 0);
        Assert.Equal(-(int)Errno.EFAULT,
            await CallSysSendMsg(env, (uint)senderFd, 0x50800, 0));
    }

    [Fact]
    public async Task SendMsg_InvalidMsgPointerWithMsgOob_ReturnsEfault()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x54000);
        Assert.Equal(0, await CallSysSocketPair(env, LinuxConstants.AF_UNIX,
            LinuxConstants.SOCK_DGRAM, 0, 0x54000));
        var senderFd = env.ReadInt32(0x54000);

        Assert.Equal(-(int)Errno.EFAULT,
            await CallSysSendMsg(env, (uint)senderFd, 0xDEAD0000, LinuxConstants.MSG_OOB));
    }

    [Fact]
    public async Task SendMsg_NullControlWithLength_ReturnsEinval()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x55000);
        env.MapUserPage(0x56000);
        env.MapUserPage(0x57000);
        env.WriteBytes(0x57000, [0x41]);

        Assert.Equal(0, await CallSysSocketPair(env, LinuxConstants.AF_UNIX,
            LinuxConstants.SOCK_DGRAM, 0, 0x55000));
        var senderFd = env.ReadInt32(0x55000);

        WriteIovec(env, 0x56000, 0x57000, 1);
        WriteMsgHdr(env, 0x55800, 0x56000, 1, 0, 16, 0, 0);
        Assert.Equal(-(int)Errno.EINVAL,
            await CallSysSendMsg(env, (uint)senderFd, 0x55800, 0));
    }

    [Fact]
    public async Task SendMsg_InvalidCmsgPointerWithMsgOob_ReturnsEfault()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x58000);
        env.MapUserPage(0x59000);
        env.MapUserPage(0x5A000);
        env.WriteBytes(0x5A000, [0x41]);

        Assert.Equal(0, await CallSysSocketPair(env, LinuxConstants.AF_UNIX,
            LinuxConstants.SOCK_DGRAM, 0, 0x58000));
        var senderFd = env.ReadInt32(0x58000);

        WriteIovec(env, 0x59000, 0x5A000, 1);
        WriteMsgHdr(env, 0x58800, 0x59000, 1, 0xDEAD0000, 16, 0, 0);
        Assert.Equal(-(int)Errno.EFAULT,
            await CallSysSendMsg(env, (uint)senderFd, 0x58800, LinuxConstants.MSG_OOB));
    }

    [Fact]
    public async Task SendMsg_InvalidScmRightFd_ReturnsEbadf()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x5B000);
        env.MapUserPage(0x5C000);
        env.MapUserPage(0x5D000);
        env.MapUserPage(0x5E000);
        env.WriteBytes(0x5D000, [0x41]);

        Assert.Equal(0, await CallSysSocketPair(env, LinuxConstants.AF_UNIX,
            LinuxConstants.SOCK_DGRAM, 0, 0x5B000));
        var senderFd = env.ReadInt32(0x5B000);

        Span<byte> cmsg = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(cmsg[..4], 16);
        BinaryPrimitives.WriteInt32LittleEndian(cmsg[4..8], LinuxConstants.SOL_SOCKET);
        BinaryPrimitives.WriteInt32LittleEndian(cmsg[8..12], LinuxConstants.SCM_RIGHTS);
        BinaryPrimitives.WriteInt32LittleEndian(cmsg[12..16], 9999);
        env.WriteBytes(0x5E000, cmsg.ToArray());

        WriteIovec(env, 0x5C000, 0x5D000, 1);
        WriteMsgHdr(env, 0x5B800, 0x5C000, 1, 0x5E000, 16, 0, 0);
        Assert.Equal(-(int)Errno.EBADF,
            await CallSysSendMsg(env, (uint)senderFd, 0x5B800, 0));
    }

    [Fact]
    public async Task SendMsg_ShortControlLength_ReturnsEinval()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x5E000);
        env.MapUserPage(0x5F000);
        env.MapUserPage(0x60000);
        env.MapUserPage(0x61000);
        env.WriteBytes(0x61000, [0x41]);

        Assert.Equal(0, await CallSysSocketPair(env, LinuxConstants.AF_UNIX,
            LinuxConstants.SOCK_DGRAM, 0, 0x5F000));
        var senderFd = env.ReadInt32(0x5F000);

        WriteIovec(env, 0x60000, 0x61000, 1);
        WriteMsgHdr(env, 0x5F800, 0x60000, 1, 0x5E000, 8, 0, 0);
        Assert.Equal(-(int)Errno.EINVAL,
            await CallSysSendMsg(env, (uint)senderFd, 0x5F800, 0));
    }

    private static void WriteSockaddrIn(TestEnv env, uint addr, uint ipv4Be, ushort port)
    {
        Span<byte> buf = stackalloc byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(buf[..2], LinuxConstants.AF_INET);
        BinaryPrimitives.WriteUInt16BigEndian(buf[2..4], port);
        BinaryPrimitives.WriteUInt32BigEndian(buf[4..8], ipv4Be);
        Assert.True(env.Engine.CopyToUser(addr, buf));
    }

    private static int WriteSockaddrUn(TestEnv env, uint addr, string path)
    {
        var pathBytes = Encoding.UTF8.GetBytes(path);
        var len = 2 + pathBytes.Length + 1;
        var buf = new byte[len];
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0, 2), LinuxConstants.AF_UNIX);
        pathBytes.CopyTo(buf.AsSpan(2, pathBytes.Length));
        buf[^1] = 0;
        Assert.True(env.Engine.CopyToUser(addr, buf));
        return len;
    }

    private static void WriteCString(TestEnv env, uint addr, string value)
    {
        env.WriteBytes(addr, Encoding.UTF8.GetBytes(value + '\0'));
    }

    private static void WriteIovec(TestEnv env, uint addr, uint baseAddr, int len)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(buf[..4], baseAddr);
        BinaryPrimitives.WriteInt32LittleEndian(buf[4..8], len);
        Assert.True(env.Engine.CopyToUser(addr, buf));
    }

    private static int WriteScmRightsCmsg(TestEnv env, uint controlPtr, IReadOnlyList<int> fds)
    {
        var cmsgLen = 12 + fds.Count * 4;
        var buf = new byte[cmsgLen];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), cmsgLen);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), 1); // SOL_SOCKET
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8, 4), 1); // SCM_RIGHTS
        for (var i = 0; i < fds.Count; i++)
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(12 + i * 4, 4), fds[i]);
        env.WriteBytes(controlPtr, buf);
        return cmsgLen;
    }

    private static void WriteMsgHdr(TestEnv env, uint addr, uint iovPtr, int iovLen, uint controlPtr, int controlLen,
        uint namePtr, int nameLen)
    {
        Span<byte> buf = stackalloc byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(buf[..4], namePtr);
        BinaryPrimitives.WriteInt32LittleEndian(buf[4..8], nameLen);
        BinaryPrimitives.WriteUInt32LittleEndian(buf[8..12], iovPtr);
        BinaryPrimitives.WriteInt32LittleEndian(buf[12..16], iovLen);
        BinaryPrimitives.WriteUInt32LittleEndian(buf[16..20], controlPtr);
        BinaryPrimitives.WriteInt32LittleEndian(buf[20..24], controlLen);
        BinaryPrimitives.WriteInt32LittleEndian(buf[24..28], 0);
        Assert.True(env.Engine.CopyToUser(addr, buf));
    }

    private static void WriteNlMsg(TestEnv env, uint ptr, ushort type, ushort flags, uint seq)
    {
        Span<byte> buf = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(buf[..4], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(buf[4..6], type);
        BinaryPrimitives.WriteUInt16LittleEndian(buf[6..8], flags);
        BinaryPrimitives.WriteUInt32LittleEndian(buf[8..12], seq);
        BinaryPrimitives.WriteUInt32LittleEndian(buf[12..16], 0);
        Assert.True(env.Engine.CopyToUser(ptr, buf));
    }

    private static void WriteIfreqName(TestEnv env, uint ptr, string name)
    {
        var buf = new byte[LinuxConstants.IFNAMSIZ];
        var bytes = Encoding.ASCII.GetBytes(name);
        Array.Copy(bytes, 0, buf, 0, Math.Min(bytes.Length, LinuxConstants.IFNAMSIZ - 1));
        env.WriteBytes(ptr, buf);
    }

    private static string ReadIfreqName(TestEnv env, uint ptr)
    {
        var raw = env.ReadBytes(ptr, LinuxConstants.IFNAMSIZ);
        var nul = Array.IndexOf(raw, (byte)0);
        if (nul < 0) nul = raw.Length;
        return Encoding.ASCII.GetString(raw, 0, nul);
    }

    private static (uint IpBe, ushort Port) ReadSockaddrIn(TestEnv env, uint addr)
    {
        Span<byte> buf = stackalloc byte[16];
        Assert.True(env.Engine.CopyFromUser(addr, buf));
        var family = BinaryPrimitives.ReadUInt16LittleEndian(buf[..2]);
        Assert.Equal((ushort)LinuxConstants.AF_INET, family);
        var port = BinaryPrimitives.ReadUInt16BigEndian(buf[2..4]);
        var ip = BinaryPrimitives.ReadUInt32BigEndian(buf[4..8]);
        return (ip, port);
    }

    private sealed class TestEnv : IDisposable
    {
        private readonly TestRuntimeFactory _runtime = new();

        public TestEnv()
        {
            Engine = _runtime.CreateEngine();
            Vma = _runtime.CreateAddressSpace();
            Process = new Process(100, Vma, null!);
            Scheduler = new KernelScheduler();
            Task = new FiberTask(100, Process, Engine, Scheduler);
            Engine.Owner = Task;
            Task.Status = FiberTaskStatus.Waiting;

            SyscallManager = new SyscallManager(Engine, Vma, 0);
            SyscallManager.MountRootHostfs(".");
        }

        public Engine Engine { get; }
        public VMAManager Vma { get; }
        public Process Process { get; }
        public FiberTask Task { get; }
        public KernelScheduler Scheduler { get; }
        public SyscallManager SyscallManager { get; }

        public void Dispose()
        {
            SyscallManager.Close();
            GC.KeepAlive(Task);
        }

        public ValueTask<int> Invoke(string methodName, uint a1, uint a2, uint a3, uint a4, uint a5, uint a6)
        {
            var method = typeof(SyscallManager).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);

            var tcs = new TaskCompletionSource<int>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            async void Entry()
            {
                var previous = Engine.CurrentSyscallManager;
                Engine.CurrentSyscallManager = SyscallManager;
                try
                {
                    var pending = (ValueTask<int>)method!.Invoke(SyscallManager, [Engine, a1, a2, a3, a4, a5, a6])!;
                    var rc = await pending;
                    tcs.TrySetResult(rc);
                }
                catch (TargetInvocationException ex) when (ex.InnerException != null)
                {
                    tcs.TrySetException(ex.InnerException);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
                finally
                {
                    Engine.CurrentSyscallManager = previous;
                    Scheduler.Running = false;
                    Scheduler.WakeUp();
                }
            }

            Task.Continuation = Entry;
            Scheduler.Running = true;
            Scheduler.Schedule(Task);
            Scheduler.Run();
            return new ValueTask<int>(tcs.Task);
        }

        public void MapUserPage(uint addr)
        {
            Vma.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[test]",
                Engine);
            Assert.True(Vma.HandleFault(addr, true, Engine));
        }

        public void WriteInt32(uint addr, int value)
        {
            Span<byte> buf = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(buf, value);
            Assert.True(Engine.CopyToUser(addr, buf));
        }

        public void WriteUInt32(uint addr, uint value)
        {
            Span<byte> buf = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(buf, value);
            Assert.True(Engine.CopyToUser(addr, buf));
        }

        public int ReadInt32(uint addr)
        {
            Span<byte> buf = stackalloc byte[4];
            Assert.True(Engine.CopyFromUser(addr, buf));
            return BinaryPrimitives.ReadInt32LittleEndian(buf);
        }

        public void WriteBytes(uint addr, byte[] data)
        {
            Assert.True(Engine.CopyToUser(addr, data));
        }

        public byte[] ReadBytes(uint addr, int len)
        {
            var data = new byte[len];
            Assert.True(Engine.CopyFromUser(addr, data));
            return data;
        }
    }
}
