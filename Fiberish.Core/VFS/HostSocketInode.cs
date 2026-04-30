using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Fiberish.Core;
using Fiberish.Core.Net;
using Fiberish.Diagnostics;
using Fiberish.Native;
using Fiberish.Syscalls;
using Microsoft.Extensions.Logging;

namespace Fiberish.VFS;

public sealed class HostSocketInode : Inode, IDispatcherWaitSource, IDispatcherEdgeWaitSource, ISocketEndpointOps, ISocketDataOps,
    ISocketUserBufferOps, ISocketOptionOps
{
    private static readonly ILogger Logger = Logging.CreateLogger<HostSocketInode>();
    [ThreadStatic] private static StringBuilder? CachedHexBuilder;
    private readonly HostSocketReadiness _readiness;
    private int _connectInFlight;
    private int _cachedSocketError;
    private int _receiveTimeoutMs;
    private int _sendTimeoutMs;

    // AF_INET = 2, AF_INET6 = 10 (Linux)
    // SOCK_STREAM = 1, SOCK_DGRAM = 2
    public HostSocketInode(ulong ino, SuperBlock sb, AddressFamily af, SocketType type, ProtocolType proto,
        SocketType? linuxSocketType = null)
    {
        Ino = ino;
        SuperBlock = sb;
        NativeSocket = new Socket(af, type, proto);
        LinuxSocketType = linuxSocketType ?? type;
        NativeSocket.Blocking = false;
        Type = InodeType.Socket;
        Mode = 0x1ED; // 755
        _readiness = new HostSocketReadiness(this, NativeSocket, Logger);
    }

    // Wrap an accepted socket
    public HostSocketInode(ulong ino, SuperBlock sb, Socket connectedSocket)
    {
        Ino = ino;
        SuperBlock = sb;
        NativeSocket = connectedSocket;
        LinuxSocketType = connectedSocket.SocketType;
        NativeSocket.Blocking = false;
        Type = InodeType.Socket;
        Mode = 0x1ED; // 755
        _readiness = new HostSocketReadiness(this, NativeSocket, Logger);
    }

    public Socket NativeSocket { get; }
    public SocketType LinuxSocketType { get; }
    public AddressFamily HostAddressFamily => NativeSocket.AddressFamily;
    public ProtocolType HostProtocolType => NativeSocket.ProtocolType;
    public SocketType HostSocketType => NativeSocket.SocketType;
    public bool HasReceiveTimeout => _receiveTimeoutMs > 0;
    public bool HasSendTimeout => _sendTimeoutMs > 0;

    bool IDispatcherWaitSource.RegisterWait(LinuxFile linuxFile, IReadyDispatcher dispatcher, Action callback,
        short events)
    {
        return RegisterWait(linuxFile, dispatcher, callback, events);
    }

    IDisposable? IDispatcherWaitSource.RegisterWaitHandle(LinuxFile linuxFile, IReadyDispatcher dispatcher,
        Action callback, short events)
    {
        return RegisterWaitHandle(linuxFile, dispatcher, callback, events);
    }

    IDisposable? IDispatcherEdgeWaitSource.RegisterEdgeTriggeredWaitHandle(LinuxFile linuxFile,
        IReadyDispatcher dispatcher, Action callback, short events)
    {
        // Host socket probes already wait for the next readiness notification instead of
        // synchronously replaying the current Poll() snapshot, so the normal dispatcher path
        // matches epoll ET rearm semantics.
        return RegisterWaitHandle(linuxFile, dispatcher, callback, events);
    }

    public async ValueTask<int> RecvAsync(LinuxFile file, FiberTask task, byte[] buffer, int flags, int maxBytes = -1)
    {
        var recvLen = maxBytes > 0 ? Math.Min(maxBytes, buffer.Length) : buffer.Length;
        if (recvLen <= 0) return 0;
        var hostFlags = TranslateRecvFlags(flags);
        Logger.LogTrace(
            "Host socket recv enter ino={Ino} len={Len} flags=0x{Flags:X} fileFlags=0x{FileFlags:X} connected={Connected}",
            Ino, recvLen, flags, (int)file.Flags, NativeSocket.Connected);

        while (true)
        {
            var n = NativeSocket.Receive(buffer, 0, recvLen, hostFlags, out var error);
            if (error == SocketError.Success)
            {
                if (n > 0)
                    ClearReadyBits(PollEvents.POLLIN);
                Logger.LogTrace("Host socket recv done ino={Ino} bytes={Bytes}", Ino, n);
                return n;
            }

            if (error is SocketError.WouldBlock or SocketError.IOPending)
            {
                ClearReadyBits(PollEvents.POLLIN);
                if (IsNonBlocking(file, flags))
                {
                    Logger.LogDebug("Host socket recv would block (ino={Ino}, flags={Flags:X})", Ino, (int)file.Flags);
                    return -(int)Errno.EAGAIN;
                }

                var ready = await WaitForSocketEventAsync(file, task, PollEvents.POLLIN);
                if (!ready)
                    return _receiveTimeoutMs > 0 ? -(int)Errno.EINTR : -(int)Errno.ERESTARTSYS;
                continue;
            }

            return MapSocketError(error);
        }
    }

    public async ValueTask<RecvMessageResult> RecvFromAsync(LinuxFile file, FiberTask task, byte[] buffer, int flags,
        int maxBytes = -1)
    {
        var recvLen = maxBytes > 0 ? Math.Min(maxBytes, buffer.Length) : buffer.Length;
        if (recvLen <= 0) return new RecvMessageResult(0);
        var hostFlags = TranslateRecvFlags(flags);
        var remoteEpTemplate = HostAddressFamily == AddressFamily.InterNetworkV6
            ? (EndPoint)new IPEndPoint(IPAddress.IPv6Any, 0)
            : new IPEndPoint(IPAddress.Any, 0);
        while (true)
            try
            {
                var remoteEp = remoteEpTemplate;
                var n = NativeSocket.ReceiveFrom(buffer, 0, recvLen, hostFlags, ref remoteEp);
                if (n > 0)
                    ClearReadyBits(PollEvents.POLLIN);
                return new RecvMessageResult(n, null, remoteEp);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock ||
                                             ex.SocketErrorCode == SocketError.IOPending)
            {
                ClearReadyBits(PollEvents.POLLIN);
                if (IsNonBlocking(file, flags))
                {
                    Logger.LogDebug("Host socket recvfrom would block (ino={Ino}, flags={Flags:X})", Ino,
                        (int)file.Flags);
                    return new RecvMessageResult(-(int)Errno.EAGAIN);
                }

                var ready = await WaitForSocketEventAsync(file, task, PollEvents.POLLIN);
                if (!ready)
                    return new RecvMessageResult(_receiveTimeoutMs > 0 ? -(int)Errno.EINTR : -(int)Errno.ERESTARTSYS);
            }
            catch (SocketException ex)
            {
                return new RecvMessageResult(MapSocketError(ex.SocketErrorCode));
            }
            catch (ObjectDisposedException)
            {
                return new RecvMessageResult(-(int)Errno.ENOTCONN);
            }
            catch (InvalidOperationException)
            {
                return new RecvMessageResult(-(int)Errno.EINVAL);
            }
    }

    public ValueTask<RecvMessageResult> RecvFromUserAsync(
        LinuxFile file,
        FiberTask task,
        Engine engine,
        uint userBufferPtr,
        int flags,
        int maxBytes = -1)
    {
        var recvLen = maxBytes > 0 ? maxBytes : 0;
        if (recvLen <= 0) return ValueTask.FromResult(new RecvMessageResult(0));

        var hostFlags = TranslateRecvFlags(flags);
        var remoteEpTemplate = HostAddressFamily == AddressFamily.InterNetworkV6
            ? (EndPoint)new IPEndPoint(IPAddress.IPv6Any, 0)
            : new IPEndPoint(IPAddress.Any, 0);

        while (true)
        {
            if (!engine.TryGetWritableUserBuffer(userBufferPtr, recvLen, out var userBuffer))
                return ValueTask.FromResult(new RecvMessageResult(-(int)Errno.EFAULT));

            var canReceiveDirectly = userBuffer.Length >= recvLen || LinuxSocketType == SocketType.Stream;
            if (!canReceiveDirectly)
                return RecvFromUserSlowAsync(file, task, engine, userBufferPtr, flags, recvLen, remoteEpTemplate);

            try
            {
                var remoteEp = remoteEpTemplate;
                var n = NativeSocket.ReceiveFrom(userBuffer, hostFlags, ref remoteEp);
                if (n > 0)
                    ClearReadyBits(PollEvents.POLLIN);
                return ValueTask.FromResult(new RecvMessageResult(n, null, remoteEp));
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock ||
                                             ex.SocketErrorCode == SocketError.IOPending)
            {
                ClearReadyBits(PollEvents.POLLIN);
                if (IsNonBlocking(file, flags))
                {
                    Logger.LogDebug("Host socket recvfrom would block (ino={Ino}, flags={Flags:X})", Ino,
                        (int)file.Flags);
                    return ValueTask.FromResult(new RecvMessageResult(-(int)Errno.EAGAIN));
                }

                return WaitAndRetryAsync();
            }
            catch (SocketException ex)
            {
                return ValueTask.FromResult(new RecvMessageResult(MapSocketError(ex.SocketErrorCode)));
            }
            catch (ObjectDisposedException)
            {
                return ValueTask.FromResult(new RecvMessageResult(-(int)Errno.ENOTCONN));
            }
            catch (InvalidOperationException)
            {
                return ValueTask.FromResult(new RecvMessageResult(-(int)Errno.EINVAL));
            }
        }

        async ValueTask<RecvMessageResult> WaitAndRetryAsync()
        {
            var ready = await WaitForSocketEventAsync(file, task, PollEvents.POLLIN);
            if (!ready)
                return new RecvMessageResult(_receiveTimeoutMs > 0 ? -(int)Errno.EINTR : -(int)Errno.ERESTARTSYS);

            return await RecvFromUserAsync(file, task, engine, userBufferPtr, flags, recvLen);
        }
    }

    private async ValueTask<RecvMessageResult> RecvFromUserSlowAsync(
        LinuxFile file,
        FiberTask task,
        Engine engine,
        uint userBufferPtr,
        int flags,
        int recvLen,
        EndPoint remoteEpTemplate)
    {
        var rented = ArrayPool<byte>.Shared.Rent(recvLen);
        try
        {
            while (true)
            {
                try
                {
                    var remoteEp = remoteEpTemplate;
                    var n = NativeSocket.ReceiveFrom(rented, 0, recvLen, TranslateRecvFlags(flags), ref remoteEp);
                    if (n > 0)
                        ClearReadyBits(PollEvents.POLLIN);
                    if (n > 0 && !engine.CopyToUser(userBufferPtr, rented.AsSpan(0, n)))
                        return new RecvMessageResult(-(int)Errno.EFAULT);
                    return new RecvMessageResult(n, null, remoteEp);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock ||
                                                 ex.SocketErrorCode == SocketError.IOPending)
                {
                    ClearReadyBits(PollEvents.POLLIN);
                    if (IsNonBlocking(file, flags))
                    {
                        Logger.LogDebug("Host socket recvfrom would block (ino={Ino}, flags={Flags:X})", Ino,
                            (int)file.Flags);
                        return new RecvMessageResult(-(int)Errno.EAGAIN);
                    }

                    var ready = await WaitForSocketEventAsync(file, task, PollEvents.POLLIN);
                    if (!ready)
                        return new RecvMessageResult(_receiveTimeoutMs > 0 ? -(int)Errno.EINTR : -(int)Errno.ERESTARTSYS);
                }
                catch (SocketException ex)
                {
                    return new RecvMessageResult(MapSocketError(ex.SocketErrorCode));
                }
                catch (ObjectDisposedException)
                {
                    return new RecvMessageResult(-(int)Errno.ENOTCONN);
                }
                catch (InvalidOperationException)
                {
                    return new RecvMessageResult(-(int)Errno.EINVAL);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public async ValueTask<int> SendAsync(LinuxFile file, FiberTask task, ReadOnlyMemory<byte> buffer, int flags)
    {
        var hostFlags = TranslateSendFlags(flags);
        Logger.LogTrace(
            "Host socket send enter ino={Ino} len={Len} flags=0x{Flags:X} fileFlags=0x{FileFlags:X} connected={Connected}",
            Ino, buffer.Length, flags, (int)file.Flags, NativeSocket.Connected);

        while (true)
        {
            var n = NativeSocket.Send(buffer.Span, hostFlags, out var error);
            if (error == SocketError.Success)
            {
                if (n > 0)
                    ClearReadyBits(PollEvents.POLLOUT);
                Logger.LogTrace("Host socket send done ino={Ino} bytes={Bytes}", Ino, n);
                return n;
            }

            if (error is SocketError.WouldBlock or SocketError.IOPending)
            {
                ClearReadyBits(PollEvents.POLLOUT);
                if (IsNonBlocking(file, flags))
                {
                    Logger.LogDebug("Host socket send would block (ino={Ino}, flags={Flags:X})", Ino,
                        (int)file.Flags);
                    return -(int)Errno.EAGAIN;
                }

                var ready = await WaitForSocketEventAsync(file, task, PollEvents.POLLOUT);
                if (!ready)
                    return _sendTimeoutMs > 0 ? -(int)Errno.EINTR : -(int)Errno.ERESTARTSYS;
                continue;
            }

            return MapSocketError(error);
        }
    }

    public async ValueTask<int> SendToAsync(LinuxFile file, FiberTask task, ReadOnlyMemory<byte> buffer, int flags,
        object remoteEpObj)
    {
        if (remoteEpObj is not EndPoint remoteEp) return -(int)Errno.EAFNOSUPPORT;
        var hostFlags = TranslateSendFlags(flags);
        byte[]? rented = null;
        ArraySegment<byte> segment;
        if (!MemoryMarshal.TryGetArray(buffer, out segment))
        {
            rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
            buffer.Span.CopyTo(rented);
            segment = new ArraySegment<byte>(rented, 0, buffer.Length);
        }

        try
        {
            while (true)
                try
                {
                    var n = NativeSocket.SendTo(segment.Array!, segment.Offset, segment.Count, hostFlags,
                        remoteEp);
                    if (n > 0)
                        ClearReadyBits(PollEvents.POLLOUT);
                    return n;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock ||
                                                 ex.SocketErrorCode == SocketError.IOPending)
                {
                    ClearReadyBits(PollEvents.POLLOUT);
                    if (IsNonBlocking(file, flags))
                    {
                        Logger.LogDebug("Host socket sendto would block (ino={Ino}, flags={Flags:X})", Ino,
                            (int)file.Flags);
                        return -(int)Errno.EAGAIN;
                    }

                    var ready = await WaitForSocketEventAsync(file, task, PollEvents.POLLOUT);
                    if (!ready)
                        return _sendTimeoutMs > 0 ? -(int)Errno.EINTR : -(int)Errno.ERESTARTSYS;
                }
                catch (SocketException ex)
                {
                    return MapSocketError(ex.SocketErrorCode);
                }
                catch (ObjectDisposedException)
                {
                    return -(int)Errno.ENOTCONN;
                }
        }
        finally
        {
            if (rented != null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public async ValueTask<int> SendMsgAsync(LinuxFile file, FiberTask task, byte[] buffer, List<LinuxFile>? fds,
        int flags, object? endpoint)
    {
        if (endpoint != null)
            return await SendToAsync(file, task, buffer, flags, endpoint);
        return await SendAsync(file, task, buffer, flags);
    }

    public ValueTask<RecvMessageResult> RecvMsgAsync(LinuxFile file, FiberTask task, byte[] buffer, int flags,
        int maxBytes = -1)
    {
        return RecvFromAsync(file, task, buffer, flags, maxBytes);
    }

    public AddressFamily SocketAddressFamily => HostAddressFamily;

    public async ValueTask<int> ConnectAsync(LinuxFile file, FiberTask task, object endpointObj)
    {
        if (endpointObj is not EndPoint endpoint) return -(int)Errno.EAFNOSUPPORT;
        while (true)
        {
            var connectStart = StartAsyncConnect(endpoint);
            if (!connectStart.Pending)
            {
                if (connectStart.Error is SocketError.Success or SocketError.IsConnected)
                    return 0;
                return MapSocketError(connectStart.Error);
            }

            if ((file.Flags & FileFlags.O_NONBLOCK) != 0)
            {
                Logger.LogDebug(
                    "Host socket connect in progress (ino={Ino}, flags={Flags:X}, endpoint={Endpoint})",
                    Ino,
                    (int)file.Flags, endpoint);
                return -(int)Errno.EINPROGRESS;
            }

            var ready = await WaitForSocketEventAsync(file, task, PollEvents.POLLOUT);
            if (!ready)
                return _sendTimeoutMs > 0 ? -(int)Errno.EINTR : -(int)Errno.ERESTARTSYS;

            var cachedErrno = ConsumeCachedSocketError();
            if (cachedErrno != 0)
                return -cachedErrno;

            if (NativeSocket.Connected)
                return 0;

            var err = GetPendingSocketError();
            if (err == SocketError.SocketError)
                return -(int)Errno.EIO;
            if (NativeSocket.Connected && (err == SocketError.Success || err == SocketError.IsConnected))
                return 0;
            if (err is not SocketError.Success and not SocketError.WouldBlock and not SocketError.IOPending
                and not SocketError.InProgress and not SocketError.AlreadyInProgress)
                return MapSocketError(err);

            if (!HasManagedConnectPending)
                return -(int)Errno.ECONNREFUSED;
        }
    }

    private ConnectStartResult StartAsyncConnect(EndPoint endpoint)
    {
        if (Interlocked.CompareExchange(ref _connectInFlight, 1, 0) != 0)
            return new ConnectStartResult(true, SocketError.AlreadyInProgress);

        SocketAsyncEventArgs? args = null;
        try
        {
            args = new SocketAsyncEventArgs
            {
                RemoteEndPoint = endpoint
            };
            args.Completed += OnConnectCompleted;

            if (!NativeSocket.ConnectAsync(args))
            {
                var error = args.SocketError;
                FinishAsyncConnect(args);
                return new ConnectStartResult(false, error);
            }

            return new ConnectStartResult(true, SocketError.IOPending);
        }
        catch (SocketException ex)
        {
            if (args != null)
            {
                args.Completed -= OnConnectCompleted;
                args.Dispose();
            }

            Interlocked.Exchange(ref _connectInFlight, 0);
            return new ConnectStartResult(false, ex.SocketErrorCode);
        }
        catch (ObjectDisposedException)
        {
            if (args != null)
            {
                args.Completed -= OnConnectCompleted;
                args.Dispose();
            }

            Interlocked.Exchange(ref _connectInFlight, 0);
            return new ConnectStartResult(false, SocketError.NotConnected);
        }
    }

    private void OnConnectCompleted(object? sender, SocketAsyncEventArgs e)
    {
        FinishAsyncConnect(e);
    }

    private void FinishAsyncConnect(SocketAsyncEventArgs args)
    {
        try
        {
            _readiness.NotifyManagedConnectCompleted(args.SocketError);
        }
        finally
        {
            args.Completed -= OnConnectCompleted;
            args.Dispose();
            Interlocked.Exchange(ref _connectInFlight, 0);
        }
    }

    private SocketError GetPendingSocketError()
    {
        try
        {
            var so = NativeSocket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error);
            return so switch
            {
                int soInt => (SocketError)soInt,
                SocketError err => err,
                _ => SocketError.SocketError
            };
        }
        catch (SocketException ex)
        {
            return ex.SocketErrorCode;
        }
        catch (ObjectDisposedException)
        {
            return SocketError.NotConnected;
        }
    }

    public async ValueTask<AcceptedSocketResult> AcceptAsync(LinuxFile file, FiberTask task, int flags)
    {
        if (TryDequeueAcceptedSocket(out var queued))
        {
            if (!HasBufferedAcceptedSocket())
                ClearReadyBits(PollEvents.POLLIN);
            var newIno = new HostSocketInode(0, SuperBlock, queued);
            return new AcceptedSocketResult(0, newIno, queued.RemoteEndPoint);
        }

        while (true)
            try
            {
                var accepted = NativeSocket.Accept();
                if (!HasBufferedAcceptedSocket())
                    ClearReadyBits(PollEvents.POLLIN);
                var newIno = new HostSocketInode(0, SuperBlock, accepted);
                return new AcceptedSocketResult(0, newIno, accepted.RemoteEndPoint);
            }
            catch (SocketException ex)
            {
                var error = ex.SocketErrorCode;
                if (error is SocketError.WouldBlock or SocketError.IOPending)
                {
                    if ((file.Flags & FileFlags.O_NONBLOCK) != 0)
                        return new AcceptedSocketResult(-(int)Errno.EAGAIN, null);

                    var ready = await WaitForSocketEventAsync(file, task, PollEvents.POLLIN);
                    if (!ready)
                        return new AcceptedSocketResult(
                            _receiveTimeoutMs > 0 ? -(int)Errno.EINTR : -(int)Errno.ERESTARTSYS,
                            null);
                    continue;
                }

                return new AcceptedSocketResult(MapSocketError(error), null);
            }
            catch (ObjectDisposedException)
            {
                return new AcceptedSocketResult(-(int)Errno.ENOTCONN, null);
            }
    }


    // --- Capability Methods ---
    public int Bind(LinuxFile file, FiberTask task, object endpoint)
    {
        if (endpoint is not EndPoint ep) return -(int)Errno.EAFNOSUPPORT;
        try
        {
            NativeSocket!.Bind(ep);
            return 0;
        }
        catch (SocketException ex)
        {
            return MapSocketError(ex.SocketErrorCode);
        }
    }

    public int Listen(LinuxFile file, FiberTask task, int backlog)
    {
        try
        {
            NativeSocket!.Listen(backlog);
            return 0;
        }
        catch (SocketException ex)
        {
            return MapSocketError(ex.SocketErrorCode);
        }
    }

    public SocketAddressResult GetSockName(LinuxFile file, FiberTask task)
    {
        EndPoint? ep;
        try
        {
            ep = NativeSocket!.LocalEndPoint;
        }
        catch
        {
            ep = null;
        }

        if (ep == null)
            ep = HostAddressFamily == AddressFamily.InterNetworkV6
                ? new IPEndPoint(IPAddress.IPv6Any, 0)
                : new IPEndPoint(IPAddress.Any, 0);
        return new SocketAddressResult(ep);
    }

    public SocketAddressResult GetPeerName(LinuxFile file, FiberTask task)
    {
        try
        {
            var ep = NativeSocket!.RemoteEndPoint;
            return new SocketAddressResult(ep);
        }
        catch (SocketException ex)
        {
            return new SocketAddressResult(Rc: MapSocketError(ex.SocketErrorCode));
        }
        catch (ObjectDisposedException)
        {
            return new SocketAddressResult(Rc: -(int)Errno.ENOTCONN);
        }
        catch
        {
            return new SocketAddressResult(Rc: -(int)Errno.EINVAL);
        }
    }

    public int Shutdown(LinuxFile file, FiberTask task, int how)
    {
        try
        {
            var mode = how switch
            {
                0 => SocketShutdown.Receive,
                1 => SocketShutdown.Send,
                2 => SocketShutdown.Both,
                _ => throw new ArgumentOutOfRangeException(nameof(how))
            };
            NativeSocket!.Shutdown(mode);
            return 0;
        }
        catch (ArgumentOutOfRangeException)
        {
            return -(int)Errno.EINVAL;
        }
        catch (SocketException ex)
        {
            return MapSocketError(ex.SocketErrorCode);
        }
        catch (ObjectDisposedException)
        {
            return -(int)Errno.ENOTCONN;
        }
    }

    public int SetSocketOption(LinuxFile file, FiberTask task, int level, int optname, ReadOnlySpan<byte> optval)
    {
        try
        {
            if (level == LinuxConstants.SOL_SOCKET)
                switch (optname)
                {
                    case LinuxConstants.SO_REUSEADDR:
                        NativeSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress,
                            BinaryPrimitives.ReadInt32LittleEndian(optval) != 0);
                        return 0;
                    case LinuxConstants.SO_KEEPALIVE:
                        NativeSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive,
                            BinaryPrimitives.ReadInt32LittleEndian(optval) != 0);
                        return 0;
                    case LinuxConstants.SO_OOBINLINE:
                        NativeSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.OutOfBandInline,
                            BinaryPrimitives.ReadInt32LittleEndian(optval) != 0);
                        return 0;
                    case LinuxConstants.SO_SNDBUF:
                        NativeSocket.SendBufferSize = BinaryPrimitives.ReadInt32LittleEndian(optval);
                        return 0;
                    case LinuxConstants.SO_RCVBUF:
                        NativeSocket.ReceiveBufferSize = BinaryPrimitives.ReadInt32LittleEndian(optval);
                        return 0;
                    case LinuxConstants.SO_LINGER:
                        if (optval.Length < 8) return -(int)Errno.EINVAL;
                        var lingerOn = BinaryPrimitives.ReadInt32LittleEndian(optval.Slice(0, 4)) != 0;
                        var lingerSec = BinaryPrimitives.ReadInt32LittleEndian(optval.Slice(4, 4));
                        if (lingerSec < 0) lingerSec = 0;
                        NativeSocket.LingerState = new LingerOption(lingerOn, lingerSec);
                        return 0;
                    case LinuxConstants.SO_REUSEPORT:
                        return 0;
                    case LinuxConstants.SO_RCVTIMEO:
                        if (optval.Length >= 8)
                        {
                            long sec = BinaryPrimitives.ReadInt32LittleEndian(optval.Slice(0, 4));
                            long usec = BinaryPrimitives.ReadInt32LittleEndian(optval.Slice(4, 4));
                            _receiveTimeoutMs = (int)(sec * 1000 + usec / 1000);
                        }

                        return 0;
                    case LinuxConstants.SO_SNDTIMEO:
                        if (optval.Length >= 8)
                        {
                            long sec = BinaryPrimitives.ReadInt32LittleEndian(optval.Slice(0, 4));
                            long usec = BinaryPrimitives.ReadInt32LittleEndian(optval.Slice(4, 4));
                            _sendTimeoutMs = (int)(sec * 1000 + usec / 1000);
                        }

                        return 0;
                    default:
                        return -(int)Errno.ENOPROTOOPT;
                }

            if (level == LinuxConstants.IPPROTO_TCP)
                switch (optname)
                {
                    case LinuxConstants.TCP_NODELAY:
                        NativeSocket.NoDelay = BinaryPrimitives.ReadInt32LittleEndian(optval) != 0;
                        return 0;
                    case LinuxConstants.TCP_KEEPIDLE:
                    case LinuxConstants.TCP_KEEPINTVL:
                    case LinuxConstants.TCP_KEEPCNT:
                        return 0;
                    default:
                        return -(int)Errno.ENOPROTOOPT;
                }

            if (level == LinuxConstants.IPPROTO_IPV6)
            {
                if (optname == LinuxConstants.IPV6_V6ONLY)
                {
                    NativeSocket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only,
                        BinaryPrimitives.ReadInt32LittleEndian(optval) != 0);
                    return 0;
                }

                return -(int)Errno.ENOPROTOOPT;
            }

            if (level == LinuxConstants.IPPROTO_ICMPV6 && optname == LinuxConstants.ICMPV6_FILTER)
                return 0;

            return -(int)Errno.ENOPROTOOPT;
        }
        catch (SocketException ex)
        {
            return MapSocketError(ex.SocketErrorCode);
        }
    }

    public int GetSocketOption(LinuxFile file, FiberTask task, int level, int optname, Span<byte> optval,
        out int written)
    {
        written = 4;
        try
        {
            if (level == LinuxConstants.SOL_SOCKET)
                switch (optname)
                {
                    case LinuxConstants.SO_REUSEADDR:
                        BinaryPrimitives.WriteInt32LittleEndian(optval,
                            NativeSocket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress) is int
                                v1
                                ? v1
                                : 0);
                        return 0;
                    case LinuxConstants.SO_KEEPALIVE:
                        BinaryPrimitives.WriteInt32LittleEndian(optval,
                            NativeSocket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive) is int v2
                                ? v2
                                : 0);
                        return 0;
                    case LinuxConstants.SO_ERROR:
                        var cachedSoError = ConsumeCachedSocketError();
                        if (cachedSoError != 0)
                        {
                            BinaryPrimitives.WriteInt32LittleEndian(optval, cachedSoError);
                            return 0;
                        }

                        var soErrorObj = NativeSocket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error);
                        var soError = soErrorObj switch
                        {
                            int i => (SocketError)i, SocketError se => se, _ => SocketError.Success
                        };
                        var linuxErr = soError == SocketError.Success ? 0 : -MapSocketError(soError);
                        BinaryPrimitives.WriteInt32LittleEndian(optval, linuxErr);
                        return 0;
                    case LinuxConstants.SO_SNDBUF:
                        BinaryPrimitives.WriteInt32LittleEndian(optval, NativeSocket.SendBufferSize);
                        return 0;
                    case LinuxConstants.SO_RCVBUF:
                        BinaryPrimitives.WriteInt32LittleEndian(optval, NativeSocket.ReceiveBufferSize);
                        return 0;
                    case LinuxConstants.SO_LINGER:
                        var linger = NativeSocket.LingerState ?? new LingerOption(false, 0);
                        BinaryPrimitives.WriteInt32LittleEndian(optval.Slice(0, 4), linger.Enabled ? 1 : 0);
                        BinaryPrimitives.WriteInt32LittleEndian(optval.Slice(4, 4), linger.LingerTime);
                        written = 8;
                        return 0;
                    case LinuxConstants.SO_TYPE:
                        BinaryPrimitives.WriteInt32LittleEndian(optval, LinuxSocketType switch
                        {
                            SocketType.Stream => LinuxConstants.SOCK_STREAM,
                            SocketType.Dgram => LinuxConstants.SOCK_DGRAM,
                            SocketType.Raw => LinuxConstants.SOCK_RAW,
                            SocketType.Seqpacket => LinuxConstants.SOCK_SEQPACKET,
                            _ => 0
                        });
                        return 0;
                    default:
                        return -(int)Errno.ENOPROTOOPT;
                }

            if (level == LinuxConstants.IPPROTO_TCP)
                switch (optname)
                {
                    case LinuxConstants.TCP_NODELAY:
                        BinaryPrimitives.WriteInt32LittleEndian(optval, NativeSocket.NoDelay ? 1 : 0);
                        return 0;
                    default:
                        return -(int)Errno.ENOPROTOOPT;
                }

            return -(int)Errno.ENOPROTOOPT;
        }
        catch (SocketException ex)
        {
            return MapSocketError(ex.SocketErrorCode);
        }
    }

    public override short Poll(LinuxFile file, short events)
    {
        return _readiness.Poll(file, events);
    }

    public override bool RegisterWait(LinuxFile file, Action callback, short events)
    {
        return false;
    }

    public override IDisposable? RegisterWaitHandle(LinuxFile file, Action callback, short events)
    {
        return null;
    }

    internal bool RegisterWait(LinuxFile file, IReadyDispatcher dispatcher, Action callback, short events)
    {
        return _readiness.RegisterWait(file, dispatcher, callback, events);
    }

    internal IDisposable? RegisterWaitHandle(LinuxFile file, IReadyDispatcher dispatcher, Action callback, short events)
    {
        return _readiness.RegisterWaitHandle(file, dispatcher, callback, events);
    }

    protected override void OnEvictCache()
    {
        _readiness.Dispose();
        base.OnEvictCache();
    }

    public override async ValueTask<AwaitResult> WaitForRead(LinuxFile file, FiberTask task)
    {
        return await WaitForSocketEventAsync(file, task, PollEvents.POLLIN)
            ? AwaitResult.Completed
            : AwaitResult.Interrupted;
    }

    public override async ValueTask<AwaitResult> WaitForWrite(LinuxFile file, FiberTask task,
        int minWritableBytes = 1)
    {
        _ = minWritableBytes;
        return await WaitForSocketEventAsync(file, task, PollEvents.POLLOUT)
            ? AwaitResult.Completed
            : AwaitResult.Interrupted;
    }

    private ValueTask<bool> WaitForSocketEventAsync(LinuxFile file, FiberTask task, short events)
    {
        return _readiness.WaitForSocketEventAsync(file, task, events);
    }

    private void ClearReadyBits(short bits)
    {
        _readiness.ClearReadyBits(bits);
    }

    private bool TryDequeueAcceptedSocket(out Socket socket)
    {
        return _readiness.TryDequeueAcceptedSocket(out socket);
    }

    private bool HasBufferedAcceptedSocket()
    {
        return _readiness.HasBufferedAcceptedSocket();
    }

    public override int Ioctl(LinuxFile linuxFile, FiberTask task, uint request, uint arg)
    {
        var engine = task.CPU;
        switch (request)
        {
            case LinuxConstants.FIONBIO:
            {
                Span<byte> valBuf = stackalloc byte[4];
                if (!engine.CopyFromUser(arg, valBuf)) return -(int)Errno.EFAULT;
                var val = MemoryMarshal.Read<int>(valBuf);
                if (val != 0) linuxFile.Flags |= FileFlags.O_NONBLOCK;
                else linuxFile.Flags &= ~FileFlags.O_NONBLOCK;
                return 0;
            }
            case LinuxConstants.FIONREAD:
            {
                var available = NativeSocket.Available;
                Span<byte> valBuf = stackalloc byte[4];
                MemoryMarshal.Write(valBuf, in available);
                if (!engine.CopyToUser(arg, valBuf)) return -(int)Errno.EFAULT;
                return 0;
            }
            default:
                var sm = engine.CurrentSyscallManager;
                if (sm == null)
                    return -(int)Errno.EPERM;
                return NetDeviceIoctlHelper.Handle(sm, engine, request, arg);
        }
    }

    public override int ReadToHost(FiberTask? task, LinuxFile file, Span<byte> buffer, long offset)
    {
        _ = task;
        var bytes = NativeSocket.Receive(buffer, SocketFlags.None, out var error);
        if (error == SocketError.Success)
        {
            TraceIo("read", buffer[..bytes], bytes);
            return bytes;
        }

        if (error is SocketError.WouldBlock or SocketError.IOPending)
            return -(int)Errno.EAGAIN;

        return MapSocketError(error);
    }

    public override int WriteFromHost(FiberTask? task, LinuxFile file, ReadOnlySpan<byte> buffer,
        long offset)
    {
        _ = task;
        var bytes = NativeSocket.Send(buffer, SocketFlags.None, out var error);
        if (error == SocketError.Success)
        {
            TraceIo("write", buffer[..Math.Min(bytes, buffer.Length)], bytes);
            return bytes;
        }

        if (error is SocketError.WouldBlock or SocketError.IOPending)
            return -(int)Errno.EAGAIN;

        return MapSocketError(error);
    }

    public override async ValueTask<int> ReadV(Engine engine, LinuxFile file, FiberTask? task,
        ArraySegment<Iovec> iovs,
        long offset, int flags)
    {
        if (offset != -1)
            return -(int)Errno.ESPIPE;
        if (task == null)
            return -(int)Errno.EPERM;

        var totalCapacity = 0L;
        foreach (var iov in iovs)
            totalCapacity += iov.Len;
        if (totalCapacity == 0)
            return 0;

        var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(totalCapacity, 64 * 1024));
        try
        {
            var totalRead = 0;
            var iovIndex = 0;
            var iovOffset = 0u;
            var singleMessageOnly = LinuxSocketType != SocketType.Stream;

            while (totalRead < totalCapacity)
            {
                var chunkLen = (int)Math.Min(buffer.Length, totalCapacity - totalRead);
                // Linux recv(2): stream sockets may return any available prefix, but datagram/
                // seqpacket receives return at most one message per call.
                var recvFlags = totalRead > 0 && !singleMessageOnly ? flags | LinuxConstants.MSG_DONTWAIT : flags;
                var bytesRead = await RecvAsync(file, task, buffer, recvFlags, chunkLen);
                if (bytesRead < 0)
                {
                    if (bytesRead == -(int)Errno.EAGAIN && totalRead > 0)
                        break;
                    return totalRead > 0 ? totalRead : bytesRead;
                }

                if (bytesRead == 0)
                    break;

                var chunkOffset = 0;
                while (chunkOffset < bytesRead && iovIndex < iovs.Count)
                {
                    var iov = iovs[iovIndex];
                    var remaining = iov.Len - iovOffset;
                    if (remaining == 0)
                    {
                        iovIndex++;
                        iovOffset = 0;
                        continue;
                    }

                    var toCopy = (int)Math.Min(remaining, bytesRead - chunkOffset);
                    if (!engine.CopyToUser(iov.BaseAddr + iovOffset, buffer.AsSpan(chunkOffset, toCopy)))
                        return totalRead > 0 ? totalRead : -(int)Errno.EFAULT;

                    chunkOffset += toCopy;
                    totalRead += toCopy;
                    iovOffset += (uint)toCopy;
                    if (iovOffset >= iov.Len)
                    {
                        iovIndex++;
                        iovOffset = 0;
                    }
                }

                if (singleMessageOnly)
                    break;
            }

            return totalRead;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public override async ValueTask<int> WriteV(Engine engine, LinuxFile file, FiberTask? task,
        ArraySegment<Iovec> iovs, long offset, int flags)
    {
        if (offset != -1)
            return -(int)Errno.ESPIPE;
        if (task == null)
            return -(int)Errno.EPERM;

        var totalLength = 0L;
        foreach (var iov in iovs)
            totalLength += iov.Len;
        if (totalLength == 0)
            return 0;
        if (LinuxSocketType != SocketType.Stream && totalLength > 65536)
            return -(int)Errno.EMSGSIZE;

        var rentLength = LinuxSocketType == SocketType.Stream
            ? (int)Math.Min(totalLength, 64 * 1024)
            : (int)totalLength;
        var buffer = ArrayPool<byte>.Shared.Rent(rentLength);
        try
        {
            var totalWritten = 0;
            var iovIndex = 0;
            var iovOffset = 0u;
            var singleMessageOnly = LinuxSocketType != SocketType.Stream;

            while (totalWritten < totalLength)
            {
                var gathered = 0;
                while (iovIndex < iovs.Count && gathered < buffer.Length)
                {
                    var iov = iovs[iovIndex];
                    var remaining = iov.Len - iovOffset;
                    if (remaining == 0)
                    {
                        iovIndex++;
                        iovOffset = 0;
                        continue;
                    }

                    var toCopy = (int)Math.Min(remaining, buffer.Length - gathered);
                    if (!engine.CopyFromUser(iov.BaseAddr + iovOffset, buffer.AsSpan(gathered, toCopy)))
                        return totalWritten > 0 ? totalWritten : -(int)Errno.EFAULT;

                    gathered += toCopy;
                    iovOffset += (uint)toCopy;
                    if (iovOffset >= iov.Len)
                    {
                        iovIndex++;
                        iovOffset = 0;
                    }
                }

                if (gathered == 0)
                    break;

                // Linux send(2): datagram-style sockets send one atomic message per call and
                // oversize messages fail with EMSGSIZE rather than being split across sends.
                var sendFlags = totalWritten > 0 && !singleMessageOnly ? flags | LinuxConstants.MSG_DONTWAIT : flags;
                var bytesWritten = await SendAsync(file, task, buffer.AsMemory(0, gathered), sendFlags);
                if (bytesWritten == -(int)Errno.EPIPE)
                {
                    task.PostSignal((int)Signal.SIGPIPE);
                    return totalWritten > 0 ? totalWritten : bytesWritten;
                }

                if (bytesWritten < 0)
                {
                    if (bytesWritten == -(int)Errno.EAGAIN && totalWritten > 0)
                        break;
                    return totalWritten > 0 ? totalWritten : bytesWritten;
                }

                totalWritten += bytesWritten;
                if (singleMessageOnly || bytesWritten < gathered)
                    break;
            }

            return totalWritten;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public int MapSocketError(SocketError err)
    {
        return err switch
        {
            SocketError.Success => 0,
            SocketError.AccessDenied => -(int)Errno.EACCES,
            SocketError.AddressFamilyNotSupported => -(int)Errno.EAFNOSUPPORT,
            SocketError.AddressAlreadyInUse => -(int)Errno.EADDRINUSE,
            SocketError.AddressNotAvailable => -(int)Errno.EADDRNOTAVAIL,
            SocketError.NetworkDown => -(int)Errno.ENETDOWN,
            SocketError.NetworkUnreachable => -(int)Errno.ENETUNREACH,
            SocketError.NetworkReset => -(int)Errno.ENETRESET,
            SocketError.ConnectionAborted => -(int)Errno.ECONNABORTED,
            SocketError.ConnectionReset => -(int)Errno.ECONNRESET,
            SocketError.NoBufferSpaceAvailable => -(int)Errno.ENOBUFS,
            SocketError.IsConnected => -(int)Errno.EISCONN,
            SocketError.NotConnected => -(int)Errno.ENOTCONN,
            SocketError.TimedOut => -(int)Errno.ETIMEDOUT,
            SocketError.ConnectionRefused => -(int)Errno.ECONNREFUSED,
            SocketError.HostUnreachable => -(int)Errno.EHOSTUNREACH,
            SocketError.WouldBlock => -(int)Errno.EAGAIN,
            SocketError.IOPending => -(int)Errno.EAGAIN,
            SocketError.InProgress => -(int)Errno.EINPROGRESS,
            SocketError.AlreadyInProgress => -(int)Errno.EALREADY,
            SocketError.Interrupted => -(int)Errno.EINTR,
            SocketError.Shutdown => -(int)Errno.EPIPE,
            SocketError.InvalidArgument => -(int)Errno.EINVAL,
            SocketError.MessageSize => -(int)Errno.EMSGSIZE,
            SocketError.ProtocolNotSupported => -(int)Errno.EPROTONOSUPPORT,
            SocketError.SocketNotSupported => -(int)Errno.ESOCKTNOSUPPORT,
            _ => -(int)Errno.EIO
        };
    }

    internal void CachePendingSocketError(SocketError err)
    {
        if (err is SocketError.Success or SocketError.WouldBlock or SocketError.IOPending or SocketError.InProgress
            or SocketError.AlreadyInProgress)
            return;

        var mapped = MapSocketError(err);
        var linuxErr = mapped < 0 ? -mapped : (int)Errno.EIO;
        Interlocked.Exchange(ref _cachedSocketError, linuxErr);
    }

    internal int ConsumeCachedSocketError()
    {
        return Interlocked.Exchange(ref _cachedSocketError, 0);
    }

    internal int PeekCachedSocketError()
    {
        return Volatile.Read(ref _cachedSocketError);
    }

    internal bool HasManagedConnectPending => Volatile.Read(ref _connectInFlight) != 0;

    private static bool IsNonBlocking(LinuxFile file, int flags)
    {
        return (file.Flags & FileFlags.O_NONBLOCK) != 0 || (flags & LinuxConstants.MSG_DONTWAIT) != 0;
    }

    private readonly record struct ConnectStartResult(bool Pending, SocketError Error);

    private static SocketFlags TranslateSendFlags(int linuxFlags)
    {
        var hostFlags = SocketFlags.None;
        if ((linuxFlags & LinuxConstants.MSG_OOB) != 0) hostFlags |= SocketFlags.OutOfBand;
        if ((linuxFlags & LinuxConstants.MSG_DONTROUTE) != 0) hostFlags |= SocketFlags.DontRoute;
        return hostFlags;
    }

    private static SocketFlags TranslateRecvFlags(int linuxFlags)
    {
        var hostFlags = TranslateSendFlags(linuxFlags);
        if ((linuxFlags & LinuxConstants.MSG_PEEK) != 0) hostFlags |= SocketFlags.Peek;
        return hostFlags;
    }

    private void TraceIo(string op, ReadOnlySpan<byte> data, int bytes)
    {
        if (bytes <= 0 || !Logger.IsEnabled(LogLevel.Trace)) return;
        var previewLen = Math.Min(bytes, 64);
        Logger.LogTrace("Host socket {Op} ino={Ino} bytes={Bytes} preview={Preview}",
            op, Ino, bytes, HexPreview(data[..previewLen]));
    }

    private static string HexPreview(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return "";
        var sb = AcquireHexBuilder(data.Length * 3);
        try
        {
            for (var i = 0; i < data.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(data[i].ToString("X2"));
            }

            return sb.ToString();
        }
        finally
        {
            ReleaseHexBuilder(sb);
        }
    }

    private static StringBuilder AcquireHexBuilder(int capacity)
    {
        var cached = CachedHexBuilder;
        if (cached != null)
        {
            CachedHexBuilder = null;
            cached.Clear();
            if (cached.Capacity < capacity)
                cached.EnsureCapacity(capacity);
            return cached;
        }

        return new StringBuilder(capacity);
    }

    private static void ReleaseHexBuilder(StringBuilder sb)
    {
        const int maxCachedCapacity = 1024;
        if (sb.Capacity > maxCachedCapacity)
            return;
        sb.Clear();
        CachedHexBuilder = sb;
    }
}
