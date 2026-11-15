using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Fiberish.Core;
using Fiberish.Core.Net;
using Fiberish.Native;
using Fiberish.Syscalls;

namespace Fiberish.VFS;

public sealed class NetstackSocketInode : Inode, IDispatcherWaitSource, IDispatcherEdgeWaitSource, ITaskWaitSource,
    ISocketEndpointOps, ISocketDataOps, ISocketOptionOps, ISocketStreamInfo
{
    private const int NetErrInvalidArg = -1;
    private const int NetErrNotFound = -2;
    private const int NetErrInvalidState = -3;
    private const int NetErrWouldBlock = -4;
    private const int NetErrProtocol = -5;
    private const int NetErrAddrInUse = -6;
    private const int NetErrMsgSize = -7;

    // Linux reports sk_sndbuf/sk_rcvbuf, which are twice the requested value and have
    // protocol minima. The backend honors those byte capacities for real queueing.
    private const int DefaultBufferBytes = 16 * 1024;
    private const int SocketMinSendBuffer = 4608;
    private const int SocketMinReceiveBuffer = 2304;
    private const int AutomaticBufferCeiling = 2 * 212992;
    private const int MaximumForcedBufferBytes = 8 * 1024 * 1024;
    private const int DefaultKeepAliveIdleSeconds = 7200;

    private readonly LoopbackNetNamespace _namespace;
    private readonly SocketType _socketType;
    private readonly Lock _edgeWaitGate = new();
    private readonly List<PollingWaitRegistration> _edgeRegistrations = [];
    private readonly HashSet<uint> _multicastGroups = [];
    private int _backlog;
    private IPEndPoint? _boundEndPoint;
    private IPEndPoint? _connectedDatagramPeer;
    private IPEndPoint? _lastDatagramPeer;
    private LoopbackNetNamespace.TcpListenerSocket? _listener;
    private int? _receiveTimeoutMs;
    private ushort _reservedTcpPort;
    private bool _reuseAddress;
    private int? _sendTimeoutMs;
    private int _sendBufferBytes = DefaultBufferBytes;
    private int _recvBufferBytes = DefaultBufferBytes;
    private bool _noDelay;
    private bool _keepAlive;
    private int _keepAliveIdleSeconds = DefaultKeepAliveIdleSeconds;
    private bool _lingerEnabled;
    private int _lingerSeconds;
    private bool _shutdownRead;
    private bool _shutdownWrite;
    private int _socketError;
    private LoopbackNetNamespace.TcpStreamSocket? _stream;
    private LoopbackNetNamespace.UdpDatagramSocket? _udp;
    private byte[]? _pendingDatagram;
    private int _pendingDatagramLength;
    private IPEndPoint? _pendingDatagramPeer;

    public NetstackSocketInode(ulong ino, SuperBlock sb, LoopbackNetNamespace @namespace, SocketType socketType)
    {
        Ino = ino;
        SuperBlock = sb;
        _namespace = @namespace;
        _socketType = socketType;
        Type = InodeType.Socket;
        Mode = 0x1ED;
    }

    private NetstackSocketInode(ulong ino, SuperBlock sb, LoopbackNetNamespace @namespace,
        LoopbackNetNamespace.TcpStreamSocket stream)
        : this(ino, sb, @namespace, SocketType.Stream)
    {
        _stream = stream;
    }

    private LoopbackNetNamespace.TcpStreamSocket CreateStreamSocket()
    {
        var stream = _namespace.CreateTcpStream(_sendBufferBytes, _recvBufferBytes);
        ApplyTcpOptions(stream);
        return stream;
    }

    private LoopbackNetNamespace.TcpListenerSocket CreateListenerSocket()
    {
        var listener = _namespace.CreateTcpListener(_sendBufferBytes, _recvBufferBytes);
        if (_noDelay) listener.TrySetNoDelay(true);
        if (_keepAlive) listener.TrySetKeepAlive(checked(_keepAliveIdleSeconds * 1000));
        return listener;
    }

    private LoopbackNetNamespace.UdpDatagramSocket CreateUdpSocket() =>
        _namespace.CreateUdpSocket(_sendBufferBytes, _recvBufferBytes);

    private void ApplyTcpOptions(LoopbackNetNamespace.TcpStreamSocket stream)
    {
        if (_noDelay) stream.TrySetNoDelay(true);
        if (_keepAlive) stream.TrySetKeepAlive(checked(_keepAliveIdleSeconds * 1000));
    }

    private void CopyInheritedOptionsTo(NetstackSocketInode accepted)
    {
        // Read the established backend's snapshot, not the listener's current flags: options
        // changed after a connection entered the accept queue must not affect that connection.
        var stream = accepted._stream!;
        var sizes = stream.GetBufferSizes();
        accepted._sendBufferBytes = sizes.SendBytes;
        accepted._recvBufferBytes = sizes.RecvBytes;
        accepted._noDelay = stream.NoDelay;
        var keepAliveMillis = stream.KeepAliveIntervalMillis;
        accepted._keepAlive = keepAliveMillis != 0;
        accepted._keepAliveIdleSeconds = keepAliveMillis != 0 ? keepAliveMillis / 1000 : _keepAliveIdleSeconds;
    }

    public bool IsListener => _listener != null;
    public bool IsStream => _stream != null;
    bool ISocketStreamInfo.IsStreamSocket => _socketType == SocketType.Stream;

    public IPEndPoint? LocalEndPoint =>
        _stream?.LocalEndPoint ??
        _udp?.LocalEndPoint ??
        _boundEndPoint;

    public IPEndPoint? RemoteEndPoint => _stream?.RemoteEndPoint ?? _lastDatagramPeer;

    public async ValueTask<int> SendAsync(LinuxFile file, FiberTask task, ReadOnlyMemory<byte> buffer, int flags)
    {
        if (_socketType == SocketType.Dgram)
        {
            if (_connectedDatagramPeer == null)
                return -(int)Errno.EDESTADDRREQ;
            return await SendToAsyncImpl(file, task, buffer, _connectedDatagramPeer, flags);
        }

        if (_socketType != SocketType.Stream)
            return -(int)Errno.EDESTADDRREQ;
        if (_stream == null)
            return -(int)Errno.EPIPE;
        if (_shutdownWrite)
            return -(int)Errno.EPIPE;
        Drive();
        if (IsTerminalWriteClosed())
            return -(int)Errno.EPIPE;

        if (IsNonBlocking(file, flags) && !_stream.CanWrite)
        {
            ClearEdgeBits(PollEvents.POLLOUT);
            return -(int)Errno.EAGAIN;
        }

        if (!_stream.CanWrite)
        {
            ClearEdgeBits(PollEvents.POLLOUT);
            var ok = await WaitForAsync(task, PollEvents.POLLOUT, () => _stream.CanWrite || IsTerminalWriteClosed());
            if (!ok)
                return _sendTimeoutMs > 0 ? -(int)Errno.EINTR : -(int)Errno.ERESTARTSYS;
            if (_shutdownWrite || IsTerminalWriteClosed())
                return -(int)Errno.EPIPE;
        }

        var rc = _stream.TrySend(buffer.Span, out var written);
        if (rc == 0)
        {
            DriveAndNotify();
            if (!_stream.CanWrite && !IsTerminalWriteClosed())
                ClearEdgeBits(PollEvents.POLLOUT);
            return written;
        }

        if (rc == NetErrWouldBlock)
            ClearEdgeBits(PollEvents.POLLOUT);
        return MapNetstackIoError(rc, forSend: true);
    }

    public async ValueTask<int> RecvAsync(LinuxFile file, FiberTask task, byte[] buffer, int flags, int maxBytes = -1)
    {
        var recvLen = maxBytes > 0 ? Math.Min(maxBytes, buffer.Length) : buffer.Length;
        if (recvLen <= 0) return 0;
        if (_socketType == SocketType.Dgram)
        {
            if (_connectedDatagramPeer == null)
            {
                var result = await RecvFromAsyncImpl(file, task, buffer, flags, recvLen);
                return result.Bytes;
            }

            while (true)
            {
                var result = await RecvFromAsyncImpl(file, task, buffer, flags, recvLen);
                if (result.Bytes < 0)
                    return result.Bytes;
                if (result.RemoteEndPoint == null || result.RemoteEndPoint.Equals(_connectedDatagramPeer))
                    return result.Bytes;
            }
        }

        if (_socketType != SocketType.Stream)
            return -(int)Errno.ENOTCONN;
        if (_stream == null)
            return -(int)Errno.ENOTCONN;
        if (_shutdownRead)
            return 0;
        Drive();
        if (HasReadEof())
            return 0;

        if (IsNonBlocking(file, flags) && !_stream.CanRead)
        {
            if (HasReadEof())
                return 0;
            ClearEdgeBits(PollEvents.POLLIN);
            return -(int)Errno.EAGAIN;
        }

        if (!_stream.CanRead)
        {
            ClearEdgeBits(PollEvents.POLLIN);
            var ok = await WaitForAsync(task, PollEvents.POLLIN, () => _stream.CanRead || HasReadEof());
            if (!ok)
                return _receiveTimeoutMs > 0 ? -(int)Errno.EINTR : -(int)Errno.ERESTARTSYS;
            if (HasReadEof())
                return 0;
        }

        var rc = _stream.TryReceive(buffer.AsSpan(0, recvLen), out var read);
        if (rc == 0)
        {
            Drive();
            if (!_stream.CanRead && !HasReadEof())
                ClearEdgeBits(PollEvents.POLLIN);
            return read;
        }

        if (rc == NetErrWouldBlock)
            ClearEdgeBits(PollEvents.POLLIN);
        return MapNetstackIoError(rc, forSend: false);
    }

    public override async ValueTask<int> ReadV(Engine engine, LinuxFile file, FiberTask? task,
        ArraySegment<Iovec> iovs, long offset, int flags)
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
            var singleMessageOnly = _socketType != SocketType.Stream;

            while (totalRead < totalCapacity)
            {
                var chunkLen = (int)Math.Min(buffer.Length, totalCapacity - totalRead);
                var bytesRead = await RecvAsync(file, task, buffer, flags, chunkLen);
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

                if (singleMessageOnly || _stream == null || !_stream.CanRead)
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

        if (_socketType == SocketType.Stream)
            return await WriteVStreamAsync(engine, file, task, iovs, totalLength, flags);

        if (totalLength > 65536)
            return -(int)Errno.EMSGSIZE;
        if (_connectedDatagramPeer == null)
            return -(int)Errno.EDESTADDRREQ;

        var buffer = ArrayPool<byte>.Shared.Rent((int)totalLength);
        try
        {
            var gathered = 0;
            for (var i = 0; i < iovs.Count && gathered < totalLength; i++)
            {
                var iov = iovs[i];
                if (iov.Len == 0)
                    continue;
                var toCopy = (int)Math.Min(iov.Len, totalLength - gathered);
                if (!engine.CopyFromUser(iov.BaseAddr, buffer.AsSpan(gathered, toCopy)))
                    return -(int)Errno.EFAULT;
                gathered += toCopy;
            }

            if (gathered == 0)
                return 0;

            return await SendToAsyncImpl(file, task, buffer.AsMemory(0, gathered), _connectedDatagramPeer, flags);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async ValueTask<int> WriteVStreamAsync(Engine engine, LinuxFile file, FiberTask task,
        ArraySegment<Iovec> iovs, long totalLength, int flags)
    {
        if (_stream == null)
            return -(int)Errno.ENOTCONN;
        if (_shutdownWrite)
        {
            if ((flags & LinuxConstants.MSG_NOSIGNAL) == 0)
                task.PostSignal((int)Signal.SIGPIPE);
            return -(int)Errno.EPIPE;
        }

        Drive();
        if (IsTerminalWriteClosed())
        {
            if ((flags & LinuxConstants.MSG_NOSIGNAL) == 0)
                task.PostSignal((int)Signal.SIGPIPE);
            return -(int)Errno.EPIPE;
        }

        if (!_stream.CanWrite)
        {
            ClearEdgeBits(PollEvents.POLLOUT);
            if (IsNonBlocking(file, flags))
                return -(int)Errno.EAGAIN;

            var ok = await WaitForAsync(task, PollEvents.POLLOUT, () => _stream.CanWrite || IsTerminalWriteClosed());
            if (!ok)
                return _sendTimeoutMs > 0 ? -(int)Errno.EINTR : -(int)Errno.ERESTARTSYS;
            if (_shutdownWrite || IsTerminalWriteClosed())
            {
                if ((flags & LinuxConstants.MSG_NOSIGNAL) == 0)
                    task.PostSignal((int)Signal.SIGPIPE);
                return -(int)Errno.EPIPE;
            }
        }

        const int MaxTcpChunkBytes = 16 * 1024;
        var initialAvail = Math.Clamp(_stream.AvailableSendBytes, 1, MaxTcpChunkBytes);
        var rentLength = (int)Math.Min(totalLength, initialAvail);
        var buffer = ArrayPool<byte>.Shared.Rent(rentLength);
        try
        {
            var totalWritten = 0;
            var iovIndex = 0;
            var iovOffset = 0u;

            while (totalWritten < totalLength)
            {
                if (!_stream.CanWrite)
                    break;

                var avail = Math.Min(buffer.Length, _stream.AvailableSendBytes);
                if (avail <= 0)
                    break;

                var chunkTarget = (int)Math.Min(avail, totalLength - totalWritten);
                var gathered = 0;
                while (iovIndex < iovs.Count && gathered < chunkTarget)
                {
                    var iov = iovs[iovIndex];
                    var remaining = iov.Len - iovOffset;
                    if (remaining == 0)
                    {
                        iovIndex++;
                        iovOffset = 0;
                        continue;
                    }

                    var toCopy = (int)Math.Min(remaining, chunkTarget - gathered);
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

                var rc = _stream.TrySend(buffer.AsSpan(0, gathered), out var bytesWritten);
                if (rc != 0)
                {
                    if (rc == NetErrWouldBlock)
                        ClearEdgeBits(PollEvents.POLLOUT);
                    var err = MapNetstackIoError(rc, forSend: true);
                    if (err == -(int)Errno.EPIPE && (flags & LinuxConstants.MSG_NOSIGNAL) == 0)
                        task.PostSignal((int)Signal.SIGPIPE);
                    if (err == -(int)Errno.EAGAIN && totalWritten > 0)
                        break;
                    return totalWritten > 0 ? totalWritten : err;
                }

                DriveAndNotify();
                if (!_stream.CanWrite && !IsTerminalWriteClosed())
                    ClearEdgeBits(PollEvents.POLLOUT);

                totalWritten += bytesWritten;
                if (bytesWritten < gathered || !_stream.CanWrite)
                    break;
            }

            return totalWritten;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async ValueTask<int> SendToAsync(LinuxFile file, FiberTask task, ReadOnlyMemory<byte> buffer, int flags,
        object endpoint)
    {
        if (_socketType == SocketType.Stream)
            return await SendAsync(file, task, buffer, flags);
        if (endpoint is not IPEndPoint ipEp) return -(int)Errno.EAFNOSUPPORT;
        return await SendToAsyncImpl(file, task, buffer, ipEp, flags);
    }

    public async ValueTask<RecvMessageResult> RecvFromAsync(LinuxFile file, FiberTask task, byte[] buffer, int flags,
        int maxBytes = -1)
    {
        if (_socketType == SocketType.Stream)
            return await RecvStreamAsRecvResult(file, task, buffer, flags, maxBytes);

        var (bytes, ep) = await RecvFromAsyncImpl(file, task, buffer, flags, maxBytes);
        return new RecvMessageResult(bytes, null, ep);
    }

    public async ValueTask<int> SendMsgAsync(LinuxFile file, FiberTask task, byte[] buffer, List<LinuxFile>? fds,
        int flags, object? endpoint)
    {
        if (endpoint != null) return await SendToAsync(file, task, buffer, flags, endpoint);
        return await SendAsync(file, task, buffer, flags);
    }

    public ValueTask<RecvMessageResult> RecvMsgAsync(LinuxFile file, FiberTask task, byte[] buffer, int flags,
        int maxBytes = -1)
    {
        if (_socketType == SocketType.Stream) return RecvStreamAsRecvResult(file, task, buffer, flags, maxBytes);

        return RecvFromAsync(file, task, buffer, flags, maxBytes);
    }

    public AddressFamily SocketAddressFamily => AddressFamily.InterNetwork;

    public int Bind(LinuxFile file, FiberTask task, object endpoint)
    {
        if (endpoint is not IPEndPoint ipEp) return -(int)Errno.EAFNOSUPPORT;
        if (ipEp.Port > 0 && ipEp.Port < 1024 &&
            !task.Process.HasEffectiveCapabilityOrRoot(Fiberish.Core.Process.CapabilityNetBindService))
            return -(int)Errno.EACCES;
        return BindImpl(ipEp);
    }

    public int Listen(LinuxFile file, FiberTask task, int backlog)
    {
        if (_socketType != SocketType.Stream)
            return -(int)Errno.EOPNOTSUPP;
        if (_boundEndPoint == null)
        {
            if (!_namespace.TryAllocateEphemeralPort(out var autoPort))
                return -(int)Errno.EADDRINUSE;
            _reservedTcpPort = autoPort;
            _boundEndPoint = new IPEndPoint(IPAddress.Any, autoPort);
        }
        else if (_boundEndPoint.Port == 0)
        {
            if (!_namespace.TryAllocateEphemeralPort(out var autoPort))
                return -(int)Errno.EADDRINUSE;
            _reservedTcpPort = autoPort;
            _boundEndPoint = new IPEndPoint(_boundEndPoint.Address, autoPort);
        }

        _stream?.Dispose();
        _stream = null;

        _listener ??= CreateListenerSocket();
        var rc = _listener.TryListen((ushort)_boundEndPoint.Port, (uint)Math.Max(backlog, 1));
        if (rc != 0)
            return MapListenError(rc);

        var actualPort = _listener.LocalPort;
        if (actualPort != 0 && actualPort != _boundEndPoint.Port)
            _boundEndPoint = new IPEndPoint(_boundEndPoint.Address, actualPort);

        DriveAndNotify();
        _backlog = backlog;
        return 0;
    }

    public async ValueTask<int> ConnectAsync(LinuxFile file, FiberTask task, object endpoint)
    {
        if (endpoint is not IPEndPoint ipEp) return -(int)Errno.EAFNOSUPPORT;
        return await ConnectAsyncImpl(file, task, ipEp);
    }

    public async ValueTask<AcceptedSocketResult> AcceptAsync(LinuxFile file, FiberTask task, int flags)
    {
        if (_socketType != SocketType.Stream)
            return new AcceptedSocketResult(-(int)Errno.EOPNOTSUPP, null);
        if (_listener == null)
            return new AcceptedSocketResult(-(int)Errno.EINVAL, null);

        Drive();
        if ((file.Flags & FileFlags.O_NONBLOCK) != 0 && !_listener.AcceptPending)
        {
            ClearEdgeBits(PollEvents.POLLIN);
            return new AcceptedSocketResult(-(int)Errno.EAGAIN, null);
        }

        if (!_listener.AcceptPending)
        {
            ClearEdgeBits(PollEvents.POLLIN);
            var ok = await WaitForAsync(task, PollEvents.POLLIN, () => _listener.AcceptPending);
            if (!ok)
                return new AcceptedSocketResult(_receiveTimeoutMs > 0 ? -(int)Errno.EINTR : -(int)Errno.ERESTARTSYS,
                    null);
        }

        var rc = _listener.TryAccept(out var accepted);
        if (rc != 0 || accepted == null)
        {
            if (rc == NetErrWouldBlock)
                ClearEdgeBits(PollEvents.POLLIN);
            return new AcceptedSocketResult(rc == NetErrWouldBlock ? -(int)Errno.EAGAIN : -(int)Errno.EINVAL, null);
        }

        DriveAndNotify();
        if (!_listener.AcceptPending)
            ClearEdgeBits(PollEvents.POLLIN);
        var newIno = new NetstackSocketInode(0, SuperBlock, _namespace, accepted);
        CopyInheritedOptionsTo(newIno);
        return new AcceptedSocketResult(0, newIno, newIno.RemoteEndPoint);
    }


    public SocketAddressResult GetSockName(LinuxFile file, FiberTask task)
    {
        return new SocketAddressResult(LocalEndPoint);
    }

    public SocketAddressResult GetPeerName(LinuxFile file, FiberTask task)
    {
        var ep = RemoteEndPoint;
        if (ep == null) return new SocketAddressResult(Rc: -(int)Errno.ENOTCONN);
        return new SocketAddressResult(ep);
    }

    public int Shutdown(LinuxFile file, FiberTask task, int how)
    {
        if (how is < 0 or > 2)
            return -(int)Errno.EINVAL;
        if (_socketType != SocketType.Stream)
            return _connectedDatagramPeer != null ? 0 : -(int)Errno.ENOTCONN;
        if (_stream == null)
            return -(int)Errno.ENOTCONN;

        switch (how)
        {
            case 0:
                _shutdownRead = true;
                return 0;
            case 1:
                if (!_shutdownWrite)
                {
                    _stream.CloseWrite();
                    _shutdownWrite = true;
                }

                return 0;
            case 2:
                _shutdownRead = true;
                if (!_shutdownWrite)
                {
                    _stream.CloseWrite();
                    _shutdownWrite = true;
                }

                return 0;
            default:
                return -(int)Errno.EINVAL;
        }
    }

    public int SetSocketOption(LinuxFile file, FiberTask task, int level, int optname, ReadOnlySpan<byte> value)
    {
        if (level == LinuxConstants.SOL_SOCKET)
        {
            switch (optname)
            {
                case LinuxConstants.SO_REUSEADDR:
                    if (value.Length < 4) return -(int)Errno.EINVAL;
                    // The backend has no listener TIME_WAIT state, so this flag has no bind
                    // conflict to relax; Linux also accepts it unconditionally.
                    _reuseAddress = BitConverter.ToInt32(value[..4]) != 0;
                    return 0;
                case LinuxConstants.SO_REUSEPORT:
                    // Linux validates optlen before the option handler; keep EINVAL first.
                    if (value.Length < 4) return -(int)Errno.EINVAL;
                    // There is no per-port load distribution between sockets.
                    return -(int)Errno.ENOPROTOOPT;
                case LinuxConstants.SO_KEEPALIVE:
                {
                    if (value.Length < 4) return -(int)Errno.EINVAL;
                    _keepAlive = BitConverter.ToInt32(value[..4]) != 0;
                    return ApplyKeepAlive() ? 0 : -(int)Errno.EOPNOTSUPP;
                }
                case LinuxConstants.SO_OOBINLINE:
                    if (value.Length < 4) return -(int)Errno.EINVAL;
                    // smoltcp has no TCP urgent-data path, so accepting this would be a no-op.
                    return -(int)Errno.ENOPROTOOPT;
                case LinuxConstants.SO_SNDBUF:
                    if (value.Length < 4) return -(int)Errno.EINVAL;
                    return SetBufferSize(send: true, BitConverter.ToInt32(value[..4]), force: false);
                case LinuxConstants.SO_RCVBUF:
                    if (value.Length < 4) return -(int)Errno.EINVAL;
                    return SetBufferSize(send: false, BitConverter.ToInt32(value[..4]), force: false);
                case LinuxConstants.SO_SNDBUFFORCE:
                    if (!task.Process.HasEffectiveCapabilityOrRoot(Fiberish.Core.Process.CapabilityNetAdmin))
                        return -(int)Errno.EPERM;
                    if (value.Length < 4) return -(int)Errno.EINVAL;
                    return SetBufferSize(send: true, BitConverter.ToInt32(value[..4]), force: true);
                case LinuxConstants.SO_RCVBUFFORCE:
                    if (!task.Process.HasEffectiveCapabilityOrRoot(Fiberish.Core.Process.CapabilityNetAdmin))
                        return -(int)Errno.EPERM;
                    if (value.Length < 4) return -(int)Errno.EINVAL;
                    return SetBufferSize(send: false, BitConverter.ToInt32(value[..4]), force: true);
                case LinuxConstants.SO_LINGER:
                {
                    if (value.Length < 8) return -(int)Errno.EINVAL;
                    var seconds = BitConverter.ToInt32(value.Slice(4, 4));
                    if (seconds < 0) return -(int)Errno.EINVAL;
                    _lingerEnabled = BitConverter.ToInt32(value[..4]) != 0;
                    _lingerSeconds = seconds;
                    return 0;
                }
                case LinuxConstants.SO_RCVTIMEO:
                    if (value.Length < 8) return -(int)Errno.EINVAL;
                    _receiveTimeoutMs = ParseTimevalMs(value);
                    return 0;
                case LinuxConstants.SO_SNDTIMEO:
                    if (value.Length < 8) return -(int)Errno.EINVAL;
                    _sendTimeoutMs = ParseTimevalMs(value);
                    return 0;
                default:
                    return -(int)Errno.ENOPROTOOPT;
            }
        }

        if (level == LinuxConstants.IPPROTO_TCP && _socketType == SocketType.Stream)
        {
            switch (optname)
            {
                case LinuxConstants.TCP_NODELAY:
                {
                    if (value.Length < 4) return -(int)Errno.EINVAL;
                    _noDelay = BitConverter.ToInt32(value[..4]) != 0;
                    var rc = _stream?.TrySetNoDelay(_noDelay) ?? _listener?.TrySetNoDelay(_noDelay) ?? 0;
                    return rc == 0 ? 0 : -(int)Errno.EOPNOTSUPP;
                }
                case LinuxConstants.TCP_KEEPIDLE:
                {
                    if (value.Length < 4) return -(int)Errno.EINVAL;
                    var seconds = BitConverter.ToInt32(value[..4]);
                    if (seconds is < 1 or > 32767) return -(int)Errno.EINVAL;
                    _keepAliveIdleSeconds = seconds;
                    return ApplyKeepAlive() ? 0 : -(int)Errno.EOPNOTSUPP;
                }
                case LinuxConstants.TCP_KEEPINTVL:
                case LinuxConstants.TCP_KEEPCNT:
                    // smoltcp has one keepalive interval and does not expose probe count or
                    // a separate post-probe interval; do not pretend these were applied.
                    return -(int)Errno.ENOPROTOOPT;
                default:
                    return -(int)Errno.ENOPROTOOPT;
            }
        }

        if (level == LinuxConstants.IPPROTO_IP)
        {
            switch (optname)
            {
                case LinuxConstants.MCAST_JOIN_GROUP:
                {
                    var parse = ParseMulticastGroup(value, out var group);
                    if (parse != 0) return parse;
                    if (!_multicastGroups.Add(group)) return 0; // Repeated joins are idempotent.
                    if (_namespace.Handle == 0 || _namespace.JoinMulticast(group) == 0) return 0;
                    _multicastGroups.Remove(group);
                    return -(int)Errno.EINVAL;
                }
                case LinuxConstants.MCAST_LEAVE_GROUP:
                {
                    var parse = ParseMulticastGroup(value, out var group);
                    if (parse != 0) return parse;
                    if (!_multicastGroups.Remove(group)) return -(int)Errno.EADDRNOTAVAIL;
                    if (_namespace.Handle != 0) _namespace.LeaveMulticast(group);
                    return 0;
                }
                default:
                    return -(int)Errno.ENOPROTOOPT;
            }
        }

        return -(int)Errno.ENOPROTOOPT;
    }

    public int GetSocketOption(LinuxFile file, FiberTask task, int level, int optname, Span<byte> destination,
        out int written)
    {
        written = 4;
        if (level == LinuxConstants.SOL_SOCKET)
        {
            switch (optname)
            {
                case LinuxConstants.SO_TYPE:
                    if (destination.Length < 4) return -(int)Errno.EINVAL;
                    BitConverter.TryWriteBytes(destination, _socketType switch
                    {
                        SocketType.Stream => LinuxConstants.SOCK_STREAM,
                        SocketType.Dgram => LinuxConstants.SOCK_DGRAM,
                        _ => 0
                    });
                    return 0;
                case LinuxConstants.SO_ERROR:
                    if (destination.Length < 4) return -(int)Errno.EINVAL;
                    BitConverter.TryWriteBytes(destination, _socketError);
                    _socketError = 0;
                    return 0;
                case LinuxConstants.SO_REUSEADDR:
                    if (destination.Length < 4) return -(int)Errno.EINVAL;
                    BitConverter.TryWriteBytes(destination, _reuseAddress ? 1 : 0);
                    return 0;
                case LinuxConstants.SO_SNDBUF:
                case LinuxConstants.SO_RCVBUF:
                {
                    if (destination.Length < 4) return -(int)Errno.EINVAL;
                    var sizes = CurrentBufferSizes();
                    BitConverter.TryWriteBytes(destination,
                        optname == LinuxConstants.SO_SNDBUF ? sizes.Send : sizes.Recv);
                    return 0;
                }
                case LinuxConstants.SO_KEEPALIVE:
                    if (destination.Length < 4) return -(int)Errno.EINVAL;
                    BitConverter.TryWriteBytes(destination, _keepAlive ? 1 : 0);
                    return 0;
                case LinuxConstants.SO_OOBINLINE:
                case LinuxConstants.SO_REUSEPORT:
                    return -(int)Errno.ENOPROTOOPT;
                case LinuxConstants.SO_LINGER:
                {
                    if (destination.Length < 8) return -(int)Errno.EINVAL;
                    BitConverter.TryWriteBytes(destination, _lingerEnabled ? 1 : 0);
                    BitConverter.TryWriteBytes(destination.Slice(4, 4), _lingerSeconds);
                    written = 8;
                    return 0;
                }
                case LinuxConstants.SO_RCVTIMEO:
                    if (destination.Length < 8) return -(int)Errno.EINVAL;
                    WriteTimevalMs(destination, _receiveTimeoutMs);
                    written = 8;
                    return 0;
                case LinuxConstants.SO_SNDTIMEO:
                    if (destination.Length < 8) return -(int)Errno.EINVAL;
                    WriteTimevalMs(destination, _sendTimeoutMs);
                    written = 8;
                    return 0;
                default:
                    return -(int)Errno.ENOPROTOOPT;
            }
        }

        if (level == LinuxConstants.IPPROTO_TCP && _socketType == SocketType.Stream)
        {
            switch (optname)
            {
                case LinuxConstants.TCP_NODELAY:
                    if (destination.Length < 4) return -(int)Errno.EINVAL;
                    BitConverter.TryWriteBytes(destination, ReadNoDelay() ? 1 : 0);
                    return 0;
                case LinuxConstants.TCP_KEEPIDLE:
                    if (destination.Length < 4) return -(int)Errno.EINVAL;
                    BitConverter.TryWriteBytes(destination, _keepAliveIdleSeconds);
                    return 0;
                case LinuxConstants.TCP_KEEPINTVL:
                case LinuxConstants.TCP_KEEPCNT:
                    return -(int)Errno.ENOPROTOOPT;
                default:
                    return -(int)Errno.ENOPROTOOPT;
            }
        }

        if (level == LinuxConstants.IPPROTO_IP)
            return -(int)Errno.ENOPROTOOPT;

        return -(int)Errno.EOPNOTSUPP;
    }

    private int SetBufferSize(bool send, int requested, bool force)
    {
        var minimum = send ? SocketMinSendBuffer : SocketMinReceiveBuffer;
        var doubled = requested > int.MaxValue / 2 ? int.MaxValue : requested * 2;
        var size = (long)Math.Max(doubled, minimum);
        if (!force) size = Math.Min(size, AutomaticBufferCeiling);
        if (size > MaximumForcedBufferBytes) return -(int)Errno.ENOMEM;

        var bytes = (int)size;
        var nextSend = send ? bytes : _sendBufferBytes;
        var nextRecv = send ? _recvBufferBytes : bytes;
        var rc = ApplyBufferSizes(nextSend, nextRecv);
        if (rc != 0) return rc;
        _sendBufferBytes = nextSend;
        _recvBufferBytes = nextRecv;
        return 0;
    }

    private int ApplyBufferSizes(int sendBytes, int recvBytes)
    {
        int rc;
        if (_stream != null) rc = _stream.TrySetBuffers(sendBytes, recvBytes);
        else if (_listener != null) rc = _listener.TrySetBuffers(sendBytes, recvBytes);
        else if (_udp != null) rc = _udp.TrySetBuffers(sendBytes, recvBytes);
        else return 0; // Stored and applied when the backend object is created.

        if (rc == 0) return 0;
        return rc switch
        {
            NetErrInvalidState => -(int)Errno.EOPNOTSUPP,
            NetErrNotFound => -(int)Errno.EBADF,
            _ => -(int)Errno.EINVAL
        };
    }

    private (int Send, int Recv) CurrentBufferSizes()
    {
        if (_stream != null) return _stream.GetBufferSizes();
        if (_listener != null) return _listener.GetBufferSizes();
        if (_udp != null) return _udp.GetBufferSizes();
        return (_sendBufferBytes, _recvBufferBytes);
    }

    private bool ReadNoDelay()
    {
        if (_stream != null) return _stream.NoDelay;
        return _noDelay;
    }

    private bool ApplyKeepAlive()
    {
        var millis = _keepAlive ? checked(_keepAliveIdleSeconds * 1000) : 0;
        return (_stream?.TrySetKeepAlive(millis) ?? _listener?.TrySetKeepAlive(millis) ?? 0) == 0;
    }

    private static int ParseMulticastGroup(ReadOnlySpan<byte> value, out uint group)
    {
        group = 0;
        // struct group_req: __u32 interface index, then sockaddr_storage.
        if (value.Length < 20) return -(int)Errno.EINVAL;
        var interfaceIndex = BinaryPrimitives.ReadInt32LittleEndian(value[..4]);
        var family = BinaryPrimitives.ReadUInt16LittleEndian(value.Slice(4, 2));
        if (family != LinuxConstants.AF_INET) return -(int)Errno.EAFNOSUPPORT;
        // The empty network namespace has only lo (index 1); 0 selects the default interface.
        if (interfaceIndex is not (0 or 1)) return -(int)Errno.ENODEV;
        group = BinaryPrimitives.ReadUInt32BigEndian(value.Slice(8, 4));
        var firstOctet = (byte)(group >> 24);
        if (firstOctet is < 224 or > 239) return -(int)Errno.EINVAL;
        return 0;
    }

    private static void WriteTimevalMs(Span<byte> destination, int? millis)
    {
        var value = millis ?? 0;
        BinaryPrimitives.WriteInt32LittleEndian(destination, value / 1000);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(4, 4), value % 1000 * 1000);
    }

    private async ValueTask<RecvMessageResult> RecvStreamAsRecvResult(LinuxFile file, FiberTask task, byte[] buffer,
        int flags, int maxBytes)
    {
        var n = await RecvAsync(file, task, buffer, flags, maxBytes);
        return new RecvMessageResult(n);
    }

    private int BindImpl(IPEndPoint endpoint)
    {
        if (!IsValidBindAddress(endpoint.Address))
            return -(int)Errno.EADDRNOTAVAIL;

        if (_socketType == SocketType.Dgram)
        {
            _udp ??= CreateUdpSocket();
            var rc = _udp.TryBind((ushort)endpoint.Port);
            if (rc != 0)
                return MapBindError(rc);
            var actualPort = _udp.LocalEndPoint.Port;
            _boundEndPoint = new IPEndPoint(endpoint.Address, actualPort != 0 ? actualPort : endpoint.Port);
            return 0;
        }

        if (_boundEndPoint != null)
            return -(int)Errno.EINVAL;

        ushort port;
        if (endpoint.Port == 0)
        {
            if (!_namespace.TryAllocateEphemeralPort(out port))
                return -(int)Errno.EADDRINUSE;
        }
        else
        {
            port = checked((ushort)endpoint.Port);
            if (!_namespace.TryReserveTcpPort(port))
                return -(int)Errno.EADDRINUSE;
        }

        _reservedTcpPort = port;
        _boundEndPoint = new IPEndPoint(endpoint.Address, port);
        return 0;
    }

    private async ValueTask<int> ConnectAsyncImpl(LinuxFile file, FiberTask task, IPEndPoint endpoint)
    {
        if (_socketType == SocketType.Dgram)
        {
            if (!IsValidConnectAddress(endpoint.Address))
                return -(int)Errno.ENETUNREACH;
            if (_boundEndPoint == null)
            {
                _udp ??= CreateUdpSocket();
                var udpBindRc = _udp.TryBind(0);
                if (udpBindRc != 0)
                    return MapBindError(udpBindRc);
                _boundEndPoint = _udp.LocalEndPoint;
            }

            _connectedDatagramPeer = endpoint;
            _lastDatagramPeer = endpoint;
            return 0;
        }

        if (_socketType != SocketType.Stream)
            return -(int)Errno.EOPNOTSUPP;
        if (!IsValidConnectAddress(endpoint.Address))
            return -(int)Errno.ENETUNREACH;
        if (_stream != null)
        {
            var state = _stream.State;
            // Established and closing states all mean the socket still has a connection;
            // a peer that already sent FIN leaves us in CLOSE_WAIT and connect() reports
            // EISCONN rather than starting a new handshake.
            if (state is >= 4 and <= 10)
                return -(int)Errno.EISCONN;
            if (state is 2 or 3)
                return -(int)Errno.EALREADY;
        }

        _listener?.Dispose();
        _listener = null;

        var targetIp = NormalizeConnectAddress(endpoint.Address);
        _stream ??= CreateStreamSocket();
        var rc = _stream.TryConnect(ToIpv4Be(targetIp), (ushort)endpoint.Port);
        if (rc != 0)
            return MapConnectError(rc);

        DriveAndNotify();
        if ((file.Flags & FileFlags.O_NONBLOCK) != 0)
            return _stream.State == 4 ? 0 : -(int)Errno.EINPROGRESS;

        var sawEstablished = _stream.State >= 4;
        var ok = await WaitForAsync(task, PollEvents.POLLOUT, () =>
        {
            var st = _stream.State;
            // Established and every later state (FIN_WAIT..TIME_WAIT) prove the handshake
            // completed; a peer that closes immediately leaves us in CLOSE_WAIT.
            if (st >= 4)
                sawEstablished = true;
            return sawEstablished || st == 0;
        });
        if (!ok)
            return _sendTimeoutMs > 0 ? -(int)Errno.EINTR : -(int)Errno.ERESTARTSYS;
        if (!sawEstablished)
        {
            _stream.Dispose();
            _stream = null;
            return -(int)Errno.ECONNREFUSED;
        }

        return 0;
    }

    private async ValueTask<int> SendToAsyncImpl(LinuxFile file, FiberTask task, ReadOnlyMemory<byte> buffer,
        IPEndPoint endpoint, int flags)
    {
        if (_socketType != SocketType.Dgram)
            return -(int)Errno.EISCONN;
        // A cork owns its route, including when the first MSG_MORE send has zero bytes.
        // Later sendto destinations must not redirect data already acknowledged to the caller.
        endpoint = _pendingDatagramPeer ?? endpoint;
        if (!IsValidConnectAddress(endpoint.Address))
            return -(int)Errno.ENETUNREACH;

        const int maxDatagramBytes = 65507;
        if (buffer.Length > maxDatagramBytes - _pendingDatagramLength)
        {
            // Linux aborts the cork on overflow. Never acknowledge bytes that were truncated,
            // including when the overflowing send also carries MSG_MORE.
            _pendingDatagramLength = 0;
            _pendingDatagramPeer = null;
            return -(int)Errno.EMSGSIZE;
        }

        _udp ??= CreateUdpSocket();
        if (_boundEndPoint == null)
        {
            var bindRc = _udp.TryBind(0);
            if (bindRc != 0)
                return MapBindError(bindRc);
            _boundEndPoint = _udp.LocalEndPoint;
        }

        // Linux corks MSG_MORE datagrams: the payload is appended to a pending datagram and
        // actually sent by the next send()/sendto()/sendmsg() without MSG_MORE.
        if ((flags & LinuxConstants.MSG_MORE) != 0)
        {
            _pendingDatagramPeer ??= new IPEndPoint(endpoint.Address, endpoint.Port);
            AppendPendingDatagram(buffer.Span);
            return buffer.Length;
        }

        byte[]? combined = null;
        ReadOnlyMemory<byte> payloadMemory = buffer;
        var pendingLength = _pendingDatagramLength;
        if (pendingLength > 0)
        {
            var merged = new byte[pendingLength + buffer.Length];
            _pendingDatagram.AsSpan(0, pendingLength).CopyTo(merged);
            buffer.Span.CopyTo(merged.AsSpan(pendingLength));
            combined = merged;
            payloadMemory = merged;
            _pendingDatagramLength = 0;
        }
        _pendingDatagramPeer = null;

        Drive();
        if (IsNonBlocking(file, flags) && !_udp.CanWrite)
        {
            ClearEdgeBits(PollEvents.POLLOUT);
            return -(int)Errno.EAGAIN;
        }

        if (!_udp.CanWrite)
        {
            ClearEdgeBits(PollEvents.POLLOUT);
            var ok = await WaitForAsync(task, PollEvents.POLLOUT, () => _udp.CanWrite);
            if (!ok)
                return _sendTimeoutMs > 0 ? -(int)Errno.EINTR : -(int)Errno.ERESTARTSYS;
        }

        _lastDatagramPeer = endpoint;
        var rc = _udp.TrySendTo(ToIpv4Be(endpoint.Address), (ushort)endpoint.Port, payloadMemory.Span,
            out var written);
        if (rc == 0)
        {
            DriveAndNotify();
            if (!_udp.CanWrite)
                ClearEdgeBits(PollEvents.POLLOUT);
            // When a corked datagram was flushed, send() reports only the caller's bytes.
            return combined != null ? buffer.Length : written;
        }

        if (rc == NetErrMsgSize)
            return -(int)Errno.EMSGSIZE;
        if (rc == NetErrWouldBlock)
        {
            ClearEdgeBits(PollEvents.POLLOUT);
            return -(int)Errno.EAGAIN;
        }
        return -(int)Errno.EINVAL;
    }

    private void AppendPendingDatagram(ReadOnlySpan<byte> data)
    {
        // Keep the corked payload bounded like a single datagram would be.
        const int MaxPendingDatagramBytes = 65507;
        if (data.IsEmpty)
            return;

        if (_pendingDatagram == null || _pendingDatagram.Length < _pendingDatagramLength + data.Length)
        {
            var next = new byte[Math.Min(MaxPendingDatagramBytes,
                Math.Max(1024, (_pendingDatagramLength + data.Length) * 2))];
            if (_pendingDatagramLength > 0)
                _pendingDatagram.AsSpan(0, _pendingDatagramLength).CopyTo(next);
            _pendingDatagram = next;
        }

        data.CopyTo(_pendingDatagram.AsSpan(_pendingDatagramLength));
        _pendingDatagramLength += data.Length;
    }

    private async ValueTask<(int Bytes, IPEndPoint? RemoteEndPoint)> RecvFromAsyncImpl(LinuxFile file, FiberTask task,
        byte[] buffer, int flags, int maxBytes = -1)
    {
        var recvLen = maxBytes > 0 ? Math.Min(maxBytes, buffer.Length) : buffer.Length;
        if (recvLen <= 0) return (0, null);
        if (_socketType != SocketType.Dgram)
            return (-(int)Errno.ENOTCONN, null);
        if (_udp == null)
            return (-(int)Errno.EINVAL, null);

        Drive();
        if (IsNonBlocking(file, flags) && !_udp.CanRead)
        {
            ClearEdgeBits(PollEvents.POLLIN);
            return (-(int)Errno.EAGAIN, null);
        }

        if (!_udp.CanRead)
        {
            ClearEdgeBits(PollEvents.POLLIN);
            var ok = await WaitForAsync(task, PollEvents.POLLIN, () => _udp.CanRead);
            if (!ok)
                return (_receiveTimeoutMs > 0 ? -(int)Errno.EINTR : -(int)Errno.ERESTARTSYS, null);
        }

        var rc = _udp.TryReceiveFrom(buffer.AsSpan(0, recvLen), out var bytes, out var remoteEndPoint);
        if (rc == 0)
        {
            _lastDatagramPeer = remoteEndPoint;
            if (!_udp.CanRead)
                ClearEdgeBits(PollEvents.POLLIN);
            return (bytes, remoteEndPoint);
        }

        if (rc == NetErrWouldBlock)
        {
            ClearEdgeBits(PollEvents.POLLIN);
            return (-(int)Errno.EAGAIN, null);
        }
        return (-(int)Errno.EINVAL, null);
    }

    public override short Poll(LinuxFile file, short events)
    {
        Drive();
        return PollEventsWithoutDrive(events);
    }

    private short PollEventsWithoutDrive(short events)
    {
        short revents = 0;
        if (_udp != null)
        {
            if ((events & PollEvents.POLLIN) != 0 && _udp.CanRead)
                revents |= PollEvents.POLLIN;
            if ((events & PollEvents.POLLOUT) != 0 && _udp.CanWrite)
                revents |= PollEvents.POLLOUT;
            return revents;
        }

        if (_listener != null)
        {
            if ((events & PollEvents.POLLIN) != 0 && _listener.AcceptPending)
                revents |= PollEvents.POLLIN;
            return revents;
        }

        if (_stream != null)
        {
            if ((events & PollEvents.POLLIN) != 0 && (_stream.CanRead || HasReadEof()))
                revents |= PollEvents.POLLIN;
            if ((events & PollEvents.POLLOUT) != 0 && !_shutdownWrite && _stream.CanWrite)
                revents |= PollEvents.POLLOUT;
            if ((events & PollEvents.POLLRDHUP) != 0 && (_shutdownRead || HasReadEof()))
                revents |= PollEvents.POLLRDHUP;
            if (HasReadEof())
                revents |= PollEvents.POLLHUP;
            if ((_shutdownWrite && !_stream.CanWrite) || IsTerminalWriteClosed())
                revents |= PollEvents.POLLERR;
        }

        return revents;
    }

    public override int Ioctl(LinuxFile linuxFile, FiberTask task, uint request, uint arg)
    {
        var sm = task.CPU.CurrentSyscallManager;
        if (sm == null)
            return -(int)Errno.EPERM;
        return NetDeviceIoctlHelper.Handle(sm, task.CPU, request, arg);
    }

    bool IDispatcherWaitSource.RegisterWait(LinuxFile linuxFile, IReadyDispatcher dispatcher, Action callback,
        short events)
    {
        return ((IDispatcherWaitSource)this).RegisterWaitHandle(linuxFile, dispatcher, callback, events) != null;
    }

    IDisposable? IDispatcherWaitSource.RegisterWaitHandle(LinuxFile linuxFile, IReadyDispatcher dispatcher,
        Action callback, short events)
    {
        var scheduler = dispatcher.Scheduler;
        if (scheduler == null)
            return null;

        var registration = new PollingWaitRegistration(this, linuxFile, scheduler, callback, events, edgeTriggered: false);
        registration.Arm();
        return registration;
    }

    IDisposable? IDispatcherEdgeWaitSource.RegisterEdgeTriggeredWaitHandle(LinuxFile linuxFile,
        IReadyDispatcher dispatcher, Action callback, short events)
    {
        var scheduler = dispatcher.Scheduler;
        if (scheduler == null)
            return null;

        var registration = new PollingWaitRegistration(this, linuxFile, scheduler, callback, events, edgeTriggered: true);
        registration.Arm();
        return registration;
    }

    bool ITaskWaitSource.RegisterWait(LinuxFile linuxFile, FiberTask task, Action callback, short events)
    {
        return ((ITaskWaitSource)this).RegisterWaitHandle(linuxFile, task, callback, events) != null;
    }

    IDisposable? ITaskWaitSource.RegisterWaitHandle(LinuxFile linuxFile, FiberTask task, Action callback, short events)
    {
        var registration = new PollingWaitRegistration(this, linuxFile, task.CommonKernel, callback, events,
            edgeTriggered: false);
        registration.Arm();
        return registration;
    }

    public override IDisposable? RegisterWaitHandle(LinuxFile linuxFile, Action callback, short events)
    {
        return null;
    }

    public override void Release(LinuxFile linuxFile)
    {
        CloseSocketHandles();
    }

    protected override void OnEvictCache()
    {
        CloseSocketHandles();
        base.OnEvictCache();
    }

    private void CloseSocketHandles()
    {
        if (_stream != null && _lingerEnabled && _lingerSeconds == 0)
        {
            // SO_LINGER {on,0}: abort with RST instead of a graceful FIN handshake.
            // A positive timeout still performs the backend's bounded graceful drain;
            // close(2) does not block the scheduler waiting for it.
            try { _stream.Abort(); }
            catch (ObjectDisposedException) { }
        }

        _listener?.Dispose();
        _stream?.Dispose();
        _udp?.Dispose();
        _listener = null;
        _stream = null;
        _udp = null;
        _pendingDatagram = null;
        _pendingDatagramLength = 0;
        _pendingDatagramPeer = null;
        if (_namespace.Handle != 0)
        {
            foreach (var group in _multicastGroups)
                _namespace.LeaveMulticast(group);
        }
        _multicastGroups.Clear();
        if (_reservedTcpPort != 0)
        {
            _namespace.ReleaseTcpPort(_reservedTcpPort);
            _reservedTcpPort = 0;
        }
        try
        {
            if (_namespace.Handle != 0)
                DriveAndNotify();
        }
        catch
        {
        }
    }

    private static int MapBindError(int rc)
    {
        return rc switch
        {
            NetErrAddrInUse => -(int)Errno.EADDRINUSE,
            NetErrProtocol => -(int)Errno.EADDRINUSE,
            _ => -(int)Errno.EINVAL
        };
    }

    private static int MapListenError(int rc)
    {
        return rc switch
        {
            NetErrAddrInUse => -(int)Errno.EADDRINUSE,
            NetErrInvalidArg => -(int)Errno.EINVAL,
            _ => -(int)Errno.EINVAL
        };
    }

    private static int MapConnectError(int rc)
    {
        return rc switch
        {
            NetErrAddrInUse => -(int)Errno.EADDRNOTAVAIL,
            NetErrProtocol => -(int)Errno.ECONNREFUSED,
            NetErrInvalidState => -(int)Errno.EISCONN,
            _ => -(int)Errno.EINVAL
        };
    }

    private static int MapNetstackIoError(int rc, bool forSend)
    {
        return rc switch
        {
            NetErrWouldBlock => -(int)Errno.EAGAIN,
            NetErrMsgSize => -(int)Errno.EMSGSIZE,
            NetErrInvalidState => forSend ? -(int)Errno.EPIPE : -(int)Errno.ENOTCONN,
            _ => -(int)Errno.EIO
        };
    }

    private void Drive()
    {
        // One iface.poll() advances a single step of the loopback handshake/data path, so a
        // connect needs several of them. Keep polling while a deadline is already due.
        var nowMs = Environment.TickCount64;
        for (var i = 0; i < 8; i++)
        {
            if (_namespace.Poll(nowMs) > 0)
                break;
        }
    }

    private void DriveAndNotify()
    {
        Drive();
        _namespace.NudgeWaitPumps();
    }

    private async ValueTask<bool> WaitForAsync(FiberTask currentTask, short events, Func<bool> ready)
    {
        while (true)
        {
            Drive();
            if (ready())
                return true;

            if (currentTask.HasInterruptingPendingSignal())
                return false;

            // The netstack reports its next internal deadline, which can be hundreds of
            // milliseconds away (TCP RTO). A readiness poll must not sleep that long, or a
            // loopback handshake takes as long as a retransmission timeout.
            var delay = Math.Clamp(_namespace.Poll(Environment.TickCount64), 1, 2);
            var result = await new SleepAwaitable(delay, currentTask);
            if (result == AwaitResult.Interrupted)
                return false;
        }
    }

    private bool WaitSynchronously(Func<bool> ready)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 2_000)
        {
            Drive();
            if (ready())
                return true;

            var delay = (int)Math.Clamp(_namespace.Poll(Environment.TickCount64), 1, 10);
            Thread.Sleep(delay);
        }

        return false;
    }

    private int? ParseTimevalMs(ReadOnlySpan<byte> value)
    {
        if (value.Length < 8)
            return 0;
        var sec = BitConverter.ToInt32(value[..4]);
        var usec = BitConverter.ToInt32(value.Slice(4, 4));
        return sec * 1000 + usec / 1000;
    }

    private uint ToIpv4Be(IPAddress address)
    {
        var normalized = NormalizeConnectAddress(address);
        var bytes = normalized.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private IPAddress NormalizeConnectAddress(IPAddress address)
    {
        return address.Equals(IPAddress.Any) ? IPAddress.Loopback : address;
    }

    private bool IsValidBindAddress(IPAddress address)
    {
        return address.Equals(IPAddress.Any) || IPAddress.IsLoopback(address) ||
               address.Equals(_namespace.PrivateIpv4Address);
    }

    private bool IsValidConnectAddress(IPAddress address)
    {
        return address.Equals(IPAddress.Any) || IPAddress.IsLoopback(address) ||
               address.Equals(_namespace.PrivateIpv4Address);
    }

    private bool HasReadEof()
    {
        if (_shutdownRead)
            return true;
        if (_stream == null || _stream.CanRead)
            return false;
        if (_stream.MayRead)
            return false;

        return _stream.State is not 2 and not 3;
    }

    private bool IsTerminalWriteClosed()
    {
        if (_shutdownWrite || _stream == null)
            return true;

        return !_stream.MayWrite && _stream.State is 0 or 9 or 10 or 11;
    }

    private static bool IsNonBlocking(LinuxFile file, int flags)
    {
        return (file.Flags & FileFlags.O_NONBLOCK) != 0 || (flags & LinuxConstants.MSG_DONTWAIT) != 0;
    }

    private void ClearEdgeBits(short bits)
    {
        lock (_edgeWaitGate)
        {
            var clearMask = (short)~bits;
            foreach (var reg in _edgeRegistrations)
                reg.ClearObservedBits(clearMask);
        }
    }

    private sealed class PollingWaitRegistration : IDisposable, LoopbackNetNamespace.INetstackWaitTarget
    {
        private readonly Action _callback;
        private readonly bool _edgeTriggered;
        private readonly short _events;
        private readonly NetstackSocketInode _inode;
        private readonly short _mask;
        private readonly KernelScheduler _scheduler;
        private bool _disposed;
        private short _lastObservedEvents;

        public PollingWaitRegistration(NetstackSocketInode inode, LinuxFile file, KernelScheduler scheduler,
            Action callback, short events, bool edgeTriggered)
        {
            _ = file;
            _inode = inode;
            _scheduler = scheduler;
            _callback = callback;
            _events = events;
            _edgeTriggered = edgeTriggered;
            _mask = (short)(events | PollEvents.POLLERR | PollEvents.POLLHUP);
            if (edgeTriggered)
            {
                var initial = (short)(_inode.PollEventsWithoutDrive(_events) & _mask);
                lock (_inode._edgeWaitGate)
                {
                    _lastObservedEvents = initial;
                    _inode._edgeRegistrations.Add(this);
                }
            }
        }

        public int PumpIndex { get; set; } = -1;

        public void ClearObservedBits(short clearMask)
        {
            _lastObservedEvents = (short)(_lastObservedEvents & clearMask);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _inode._namespace.UnregisterWaitTarget(_scheduler, this);
            if (_edgeTriggered)
            {
                lock (_inode._edgeWaitGate)
                {
                    _inode._edgeRegistrations.Remove(this);
                }
            }
        }

        public void Arm()
        {
            if (_disposed)
                return;

            _inode._namespace.RegisterWaitTarget(_scheduler, this);
        }

        public bool EvaluateReady()
        {
            if (_disposed)
                return false;

            var current = (short)(_inode.PollEventsWithoutDrive(_events) & _mask);
            if (_edgeTriggered)
            {
                lock (_inode._edgeWaitGate)
                {
                    var rising = (short)(current & ~_lastObservedEvents);
                    _lastObservedEvents = current;
                    return rising != 0;
                }
            }

            return current != 0;
        }

        public void InvokeReady()
        {
            if (_disposed)
                return;

            _callback();
        }
    }
}
