using System.Buffers;
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
    private bool _shutdownRead;
    private bool _shutdownWrite;
    private int _socketError;
    private LoopbackNetNamespace.TcpStreamSocket? _stream;
    private LoopbackNetNamespace.UdpDatagramSocket? _udp;

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

        _listener ??= _namespace.CreateTcpListener();
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
                    _reuseAddress = BitConverter.ToInt32(value[..4]) != 0;
                    return 0;
                case LinuxConstants.SO_REUSEPORT:
                case LinuxConstants.SO_KEEPALIVE:
                case LinuxConstants.SO_OOBINLINE:
                case LinuxConstants.SO_SNDBUF:
                case LinuxConstants.SO_RCVBUF:
                    if (value.Length < 4) return -(int)Errno.EINVAL;
                    return 0;
                case LinuxConstants.SO_SNDBUFFORCE:
                case LinuxConstants.SO_RCVBUFFORCE:
                    if (!task.Process.HasEffectiveCapabilityOrRoot(Fiberish.Core.Process.CapabilityNetAdmin))
                        return -(int)Errno.EPERM;
                    if (value.Length < 4) return -(int)Errno.EINVAL;
                    return 0;
                case LinuxConstants.SO_LINGER:
                    if (value.Length < 8) return -(int)Errno.EINVAL;
                    return 0;
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
                case LinuxConstants.TCP_KEEPIDLE:
                case LinuxConstants.TCP_KEEPINTVL:
                case LinuxConstants.TCP_KEEPCNT:
                    if (value.Length < 4) return -(int)Errno.EINVAL;
                    return 0;
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
                    if (value.Length < 20) return -(int)Errno.EINVAL;
                    _multicastGroups.Add(BitConverter.ToUInt32(value.Slice(4, 4)));
                    return 0;
                }
                case LinuxConstants.MCAST_LEAVE_GROUP:
                {
                    if (value.Length < 20) return -(int)Errno.EINVAL;
                    return _multicastGroups.Remove(BitConverter.ToUInt32(value.Slice(4, 4)))
                        ? 0
                        : -(int)Errno.EADDRNOTAVAIL;
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
                    if (destination.Length < 4) return -(int)Errno.EINVAL;
                    BitConverter.TryWriteBytes(destination, 16 * 1024);
                    return 0;
                case LinuxConstants.SO_KEEPALIVE:
                case LinuxConstants.SO_OOBINLINE:
                case LinuxConstants.SO_REUSEPORT:
                    if (destination.Length < 4) return -(int)Errno.EINVAL;
                    BitConverter.TryWriteBytes(destination, 0);
                    return 0;
                default:
                    return -(int)Errno.ENOPROTOOPT;
            }
        }

        if (level == LinuxConstants.IPPROTO_IP || (level == LinuxConstants.IPPROTO_TCP && _socketType == SocketType.Stream))
            return -(int)Errno.ENOPROTOOPT;

        return -(int)Errno.EOPNOTSUPP;
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
            _udp ??= _namespace.CreateUdpSocket();
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
                _udp ??= _namespace.CreateUdpSocket();
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
            if (_stream.State == 4)
                return -(int)Errno.EISCONN;
            if (_stream.State is 2 or 3)
                return -(int)Errno.EALREADY;
        }

        _listener?.Dispose();
        _listener = null;

        var targetIp = NormalizeConnectAddress(endpoint.Address);
        _stream ??= _namespace.CreateTcpStream();
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
            if (st == 4)
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
        if (!IsValidConnectAddress(endpoint.Address))
            return -(int)Errno.ENETUNREACH;
        _udp ??= _namespace.CreateUdpSocket();
        if (_boundEndPoint == null)
        {
            var bindRc = _udp.TryBind(0);
            if (bindRc != 0)
                return MapBindError(bindRc);
            _boundEndPoint = _udp.LocalEndPoint;
        }

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
        var rc = _udp.TrySendTo(ToIpv4Be(endpoint.Address), (ushort)endpoint.Port, buffer.Span, out var written);
        if (rc == 0)
        {
            DriveAndNotify();
            if (!_udp.CanWrite)
                ClearEdgeBits(PollEvents.POLLOUT);
            return written;
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
        _listener?.Dispose();
        _stream?.Dispose();
        _udp?.Dispose();
        _listener = null;
        _stream = null;
        _udp = null;
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
        var nowMs = Environment.TickCount64;
        for (var i = 0; i < 4; i++)
        {
            if (_namespace.Poll(nowMs) != 0)
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

            var delay = Math.Max(1, _namespace.Poll(Environment.TickCount64));
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