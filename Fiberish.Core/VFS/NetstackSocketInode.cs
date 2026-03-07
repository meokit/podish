using System.Net;
using System.Diagnostics;
using System.Net.Sockets;
using Fiberish.Core;
using Fiberish.Core.Net;
using Fiberish.Native;
using Fiberish.Syscalls;

namespace Fiberish.VFS;

public sealed class NetstackSocketInode : Inode
{
    private readonly LoopbackNetNamespace _namespace;
    private readonly SocketType _socketType;
    private LoopbackNetNamespace.TcpListenerSocket? _listener;
    private LoopbackNetNamespace.TcpStreamSocket? _stream;
    private LoopbackNetNamespace.UdpDatagramSocket? _udp;
    private IPEndPoint? _boundEndPoint;
    private IPEndPoint? _lastDatagramPeer;
    private IPEndPoint? _connectedDatagramPeer;
    private int _socketError;
    private bool _reuseAddress;
    private int? _receiveTimeoutMs;
    private int? _sendTimeoutMs;
    private int _backlog;
    private bool _shutdownRead;
    private bool _shutdownWrite;

    public NetstackSocketInode(ulong ino, SuperBlock sb, LoopbackNetNamespace @namespace, SocketType socketType)
    {
        Ino = ino;
        SuperBlock = sb;
        _namespace = @namespace;
        _socketType = socketType;
        Type = InodeType.Socket;
        Mode = 0x1ED;
    }

    private NetstackSocketInode(ulong ino, SuperBlock sb, LoopbackNetNamespace @namespace, LoopbackNetNamespace.TcpStreamSocket stream)
        : this(ino, sb, @namespace, SocketType.Stream)
    {
        _stream = stream;
    }

    public bool IsListener => _listener != null;
    public bool IsStream => _stream != null;

    public IPEndPoint? LocalEndPoint =>
        _stream?.LocalEndPoint ??
        _udp?.LocalEndPoint ??
        _boundEndPoint;

    public IPEndPoint? RemoteEndPoint => _stream?.RemoteEndPoint ?? _lastDatagramPeer;

    public int Bind(IPEndPoint endpoint)
    {
        if (!IsValidBindAddress(endpoint.Address))
            return -(int)Errno.EADDRNOTAVAIL;
        _boundEndPoint = endpoint;
        if (_socketType == SocketType.Dgram)
        {
            _udp ??= _namespace.CreateUdpSocket();
            _udp.Bind((ushort)endpoint.Port);
        }
        return 0;
    }

    public int SetSocketOption(int level, int optname, ReadOnlySpan<byte> value)
    {
        if (level != LinuxConstants.SOL_SOCKET)
            return -(int)Errno.ENOPROTOOPT;

        switch (optname)
        {
            case LinuxConstants.SO_REUSEADDR:
                _reuseAddress = value.Length >= 4 && BitConverter.ToInt32(value[..4]) != 0;
                return 0;
            case LinuxConstants.SO_RCVTIMEO:
                _receiveTimeoutMs = ParseTimevalMs(value);
                return 0;
            case LinuxConstants.SO_SNDTIMEO:
                _sendTimeoutMs = ParseTimevalMs(value);
                return 0;
            default:
                return -(int)Errno.ENOPROTOOPT;
        }
    }

    public int GetSocketOption(int level, int optname, Span<byte> destination, out int written)
    {
        written = 4;
        if (level != LinuxConstants.SOL_SOCKET)
            return -(int)Errno.ENOPROTOOPT;

        switch (optname)
        {
            case LinuxConstants.SO_TYPE:
                BitConverter.TryWriteBytes(destination, _socketType switch
                {
                    SocketType.Stream => LinuxConstants.SOCK_STREAM,
                    SocketType.Dgram => LinuxConstants.SOCK_DGRAM,
                    _ => 0
                });
                return 0;
            case LinuxConstants.SO_ERROR:
                BitConverter.TryWriteBytes(destination, _socketError);
                _socketError = 0;
                return 0;
            case LinuxConstants.SO_REUSEADDR:
                BitConverter.TryWriteBytes(destination, _reuseAddress ? 1 : 0);
                return 0;
            default:
                return -(int)Errno.ENOPROTOOPT;
        }
    }

    public int Listen(int backlog)
    {
        if (_socketType != SocketType.Stream)
            return -(int)Errno.EOPNOTSUPP;
        if (_boundEndPoint == null)
            return -(int)Errno.EINVAL;

        _stream?.Dispose();
        _stream = null;

        _listener ??= _namespace.CreateTcpListener();
        _listener.Listen((ushort)_boundEndPoint.Port, (uint)Math.Max(backlog, 1));
        _backlog = backlog;
        return 0;
    }

    public async ValueTask<int> ConnectAsync(LinuxFile file, IPEndPoint endpoint)
    {
        if (_socketType == SocketType.Dgram)
        {
            if (!IsValidConnectAddress(endpoint.Address))
                return -(int)Errno.ENETUNREACH;
            if (_boundEndPoint == null)
            {
                _udp ??= _namespace.CreateUdpSocket();
                _udp.Bind(0);
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
        _listener?.Dispose();
        _listener = null;

        _stream ??= _namespace.CreateTcpStream();
        _stream.Connect(ToIpv4Be(endpoint.Address), (ushort)endpoint.Port);

        if ((file.Flags & FileFlags.O_NONBLOCK) != 0)
            return _stream.State == 4 ? 0 : -(int)Errno.EINPROGRESS;

        return await WaitForAsync(file, PollEvents.POLLOUT, () => _stream.State == 4) ? 0 : -(int)Errno.ERESTARTSYS;
    }

    public async ValueTask<(int Rc, NetstackSocketInode? Inode)> AcceptAsync(LinuxFile file, int flags)
    {
        if (_socketType != SocketType.Stream)
            return (-(int)Errno.EOPNOTSUPP, null);
        if (_listener == null)
            return (-(int)Errno.EINVAL, null);

        if ((file.Flags & FileFlags.O_NONBLOCK) != 0 && !_listener.AcceptPending)
            return (-(int)Errno.EAGAIN, null);

        if (!_listener.AcceptPending)
        {
            var ok = await WaitForAsync(file, PollEvents.POLLIN, () => _listener.AcceptPending);
            if (!ok)
                return (-(int)Errno.ERESTARTSYS, null);
        }

        var accepted = _listener.Accept();
        return (0, new NetstackSocketInode(0, SuperBlock, _namespace, accepted));
    }

    public async ValueTask<int> SendAsync(LinuxFile file, ReadOnlyMemory<byte> buffer, int flags)
    {
        if (_socketType == SocketType.Dgram)
        {
            if (_connectedDatagramPeer == null)
                return -(int)Errno.EDESTADDRREQ;
            return await SendToAsync(file, buffer, _connectedDatagramPeer, flags);
        }
        if (_socketType != SocketType.Stream)
            return -(int)Errno.EDESTADDRREQ;
        if (_stream == null)
            return -(int)Errno.ENOTCONN;
        if (_shutdownWrite)
            return -(int)Errno.EPIPE;
        if (IsTerminalWriteClosed())
            return -(int)Errno.EPIPE;

        if ((file.Flags & FileFlags.O_NONBLOCK) != 0 && !_stream.CanWrite)
            return -(int)Errno.EAGAIN;

        if (!_stream.CanWrite)
        {
            var ok = await WaitForAsync(file, PollEvents.POLLOUT, () => _stream.CanWrite || IsTerminalWriteClosed());
            if (!ok)
                return -(int)Errno.ERESTARTSYS;
            if (_shutdownWrite || IsTerminalWriteClosed())
                return -(int)Errno.EPIPE;
        }

        return _stream.Send(buffer.Span);
    }

    public async ValueTask<int> RecvAsync(LinuxFile file, byte[] buffer, int flags)
    {
        if (_socketType == SocketType.Dgram)
        {
            if (_connectedDatagramPeer == null)
                return -(int)Errno.ENOTCONN;

            while (true)
            {
                var result = await RecvFromAsync(file, buffer, flags);
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
        if (HasReadEof())
            return 0;

        if ((file.Flags & FileFlags.O_NONBLOCK) != 0 && !_stream.CanRead)
            return HasReadEof() ? 0 : -(int)Errno.EAGAIN;

        if (!_stream.CanRead)
        {
            var ok = await WaitForAsync(file, PollEvents.POLLIN, () => _stream.CanRead || HasReadEof());
            if (!ok)
                return -(int)Errno.ERESTARTSYS;
            if (HasReadEof())
                return 0;
        }

        return _stream.Receive(buffer);
    }

    public async ValueTask<int> SendToAsync(LinuxFile file, ReadOnlyMemory<byte> buffer, IPEndPoint endpoint, int flags)
    {
        if (_socketType != SocketType.Dgram)
            return -(int)Errno.EISCONN;
        if (!IsValidConnectAddress(endpoint.Address))
            return -(int)Errno.ENETUNREACH;
        _udp ??= _namespace.CreateUdpSocket();
        if (_boundEndPoint == null)
        {
            _udp.Bind(0);
            _boundEndPoint = _udp.LocalEndPoint;
        }

        if ((file.Flags & FileFlags.O_NONBLOCK) != 0 && !_udp.CanWrite)
            return -(int)Errno.EAGAIN;

        if (!_udp.CanWrite)
        {
            var ok = await WaitForAsync(file, PollEvents.POLLOUT, () => _udp.CanWrite);
            if (!ok)
                return -(int)Errno.ERESTARTSYS;
        }

        _lastDatagramPeer = endpoint;
        return _udp.SendTo(ToIpv4Be(endpoint.Address), (ushort)endpoint.Port, buffer.Span);
    }

    public async ValueTask<(int Bytes, IPEndPoint? RemoteEndPoint)> RecvFromAsync(LinuxFile file, byte[] buffer, int flags)
    {
        if (_socketType != SocketType.Dgram)
            return (-(int)Errno.ENOTCONN, null);
        if (_udp == null)
            return (-(int)Errno.EINVAL, null);

        if ((file.Flags & FileFlags.O_NONBLOCK) != 0 && !_udp.CanRead)
            return (-(int)Errno.EAGAIN, null);

        if (!_udp.CanRead)
        {
            var ok = await WaitForAsync(file, PollEvents.POLLIN, () => _udp.CanRead);
            if (!ok)
                return (-(int)Errno.ERESTARTSYS, null);
        }

        var bytes = _udp.ReceiveFrom(buffer, out var remoteEndPoint);
        _lastDatagramPeer = remoteEndPoint;
        return (bytes, remoteEndPoint);
    }

    public override short Poll(LinuxFile file, short events)
    {
        Drive();

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
            if ((events & PollEvents.POLLOUT) != 0 && !_shutdownWrite && (_stream.CanWrite || _stream.State == 4))
                revents |= PollEvents.POLLOUT;
            if (HasReadEof())
                revents |= PollEvents.POLLHUP;
            if (_shutdownWrite && !_stream.CanWrite)
                revents |= PollEvents.POLLERR;
        }

        return revents;
    }

    public override int Ioctl(LinuxFile linuxFile, uint request, uint arg, Engine engine)
    {
        return NetDeviceIoctlHelper.Handle(engine, request, arg);
    }

    public int Shutdown(int how)
    {
        if (_socketType != SocketType.Stream)
            return 0;
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

    public override IDisposable? RegisterWaitHandle(LinuxFile linuxFile, Action callback, short events)
    {
        var scheduler = KernelScheduler.Current;
        if (scheduler == null)
            return null;

        var registration = new PollingWaitRegistration(this, linuxFile, scheduler, callback, events);
        registration.Arm();
        return registration;
    }

    protected override void Release()
    {
        _listener?.Dispose();
        _stream?.Dispose();
        _udp?.Dispose();
        _listener = null;
        _stream = null;
        _udp = null;
        base.Release();
    }

    private void Drive()
    {
        _namespace.Poll(Environment.TickCount64);
    }

    private async ValueTask<bool> WaitForAsync(LinuxFile file, short events, Func<bool> ready)
    {
        var currentScheduler = KernelScheduler.Current;
        var currentTask = currentScheduler?.CurrentTask;
        if (currentTask == null)
            return WaitSynchronously(ready);

        while (true)
        {
            Drive();
            if (ready())
                return true;

            if (currentTask != null && currentTask.HasUnblockedPendingSignal())
                return false;

            var delay = Math.Max(1, _namespace.Poll(Environment.TickCount64));
            var result = await new SleepAwaitable(delay);
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
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private bool IsValidBindAddress(IPAddress address)
    {
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.Loopback) || address.Equals(_namespace.PrivateIpv4Address))
            return true;
        return false;
    }

    private bool IsValidConnectAddress(IPAddress address)
    {
        if (address.Equals(IPAddress.Loopback) || address.Equals(_namespace.PrivateIpv4Address))
            return true;
        return false;
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

    private sealed class PollingWaitRegistration : IDisposable
    {
        private readonly NetstackSocketInode _inode;
        private readonly LinuxFile _file;
        private readonly KernelScheduler _scheduler;
        private readonly Action _callback;
        private readonly short _events;
        private Fiberish.Core.Timer? _timer;
        private bool _disposed;

        public PollingWaitRegistration(NetstackSocketInode inode, LinuxFile file, KernelScheduler scheduler, Action callback, short events)
        {
            _inode = inode;
            _file = file;
            _scheduler = scheduler;
            _callback = callback;
            _events = events;
        }

        public void Arm()
        {
            if (_disposed)
                return;

            var delay = Math.Max(1, _inode._namespace.Poll(Environment.TickCount64));
            _timer = _scheduler.ScheduleTimer(delay, Tick);
        }

        public void Dispose()
        {
            _disposed = true;
            _timer?.Cancel();
            _timer = null;
        }

        private void Tick()
        {
            if (_disposed)
                return;

            if (_inode.Poll(_file, _events) != 0)
            {
                _scheduler.Schedule(_callback);
                return;
            }

            Arm();
        }
    }
}
