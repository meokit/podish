using System.Net;
using System.Diagnostics;
using Fiberish.Core;
using Fiberish.Core.Net;
using Fiberish.Native;
using Fiberish.Syscalls;

namespace Fiberish.VFS;

public sealed class NetstackSocketInode : Inode
{
    private readonly LoopbackNetNamespace _namespace;
    private LoopbackNetNamespace.TcpListenerSocket? _listener;
    private LoopbackNetNamespace.TcpStreamSocket? _stream;
    private IPEndPoint? _boundEndPoint;
    private int _backlog;

    public NetstackSocketInode(ulong ino, SuperBlock sb, LoopbackNetNamespace @namespace)
    {
        Ino = ino;
        SuperBlock = sb;
        _namespace = @namespace;
        Type = InodeType.Socket;
        Mode = 0x1ED;
    }

    private NetstackSocketInode(ulong ino, SuperBlock sb, LoopbackNetNamespace @namespace, LoopbackNetNamespace.TcpStreamSocket stream)
        : this(ino, sb, @namespace)
    {
        _stream = stream;
    }

    public bool IsListener => _listener != null;
    public bool IsStream => _stream != null;

    public IPEndPoint? LocalEndPoint =>
        _stream?.LocalEndPoint ??
        _boundEndPoint;

    public IPEndPoint? RemoteEndPoint => _stream?.RemoteEndPoint;

    public int Bind(IPEndPoint endpoint)
    {
        if (!IsValidBindAddress(endpoint.Address))
            return -(int)Errno.EADDRNOTAVAIL;
        _boundEndPoint = endpoint;
        return 0;
    }

    public int Listen(int backlog)
    {
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
        if (_stream == null)
            return -(int)Errno.ENOTCONN;

        if ((file.Flags & FileFlags.O_NONBLOCK) != 0 && !_stream.CanWrite)
            return -(int)Errno.EAGAIN;

        if (!_stream.CanWrite)
        {
            var ok = await WaitForAsync(file, PollEvents.POLLOUT, () => _stream.CanWrite);
            if (!ok)
                return -(int)Errno.ERESTARTSYS;
        }

        return _stream.Send(buffer.Span);
    }

    public async ValueTask<int> RecvAsync(LinuxFile file, byte[] buffer, int flags)
    {
        if (_stream == null)
            return -(int)Errno.ENOTCONN;

        if ((file.Flags & FileFlags.O_NONBLOCK) != 0 && !_stream.CanRead)
            return -(int)Errno.EAGAIN;

        if (!_stream.CanRead)
        {
            var ok = await WaitForAsync(file, PollEvents.POLLIN, () => _stream.CanRead);
            if (!ok)
                return -(int)Errno.ERESTARTSYS;
        }

        return _stream.Receive(buffer);
    }

    public override short Poll(LinuxFile file, short events)
    {
        Drive();

        short revents = 0;
        if (_listener != null)
        {
            if ((events & PollEvents.POLLIN) != 0 && _listener.AcceptPending)
                revents |= PollEvents.POLLIN;
            return revents;
        }

        if (_stream != null)
        {
            if ((events & PollEvents.POLLIN) != 0 && _stream.CanRead)
                revents |= PollEvents.POLLIN;
            if ((events & PollEvents.POLLOUT) != 0 && (_stream.CanWrite || _stream.State == 4))
                revents |= PollEvents.POLLOUT;
        }

        return revents;
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
        _listener = null;
        _stream = null;
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
