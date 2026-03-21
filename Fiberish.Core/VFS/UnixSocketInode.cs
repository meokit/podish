using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Fiberish.Auth.Permission;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.Syscalls;

namespace Fiberish.VFS;

public class UnixMessage
{
    public byte[] Data { get; init; } = [];
    public List<LinuxFile> Fds { get; init; } = new();
    public byte[]? SourceSunPathRaw { get; init; }
}

public class UnixSocketInode : Inode, ITaskWaitSource, IDispatcherWaitSource, ISocketEndpointOps, ISocketDataOps,
    ISocketOptionOps
{
    // Backpressure: track queued bytes to limit memory usage
    private const int MaxSendBuffer = 262144; // 256KB
    private readonly Queue<UnixSocketInode> _pendingConnections = new();
    private readonly AsyncWaitQueue _readWaitQueue;

    // For AF_UNIX
    private readonly Queue<UnixMessage> _receiveQueue = new();
    private readonly AsyncWaitQueue _writeWaitQueue;
    private readonly KernelScheduler _scheduler;
    private bool _lifecycleClosed;
    private int _listenBacklog = 1;
    private bool _listening;
    private byte[]? _localSunPathRaw;

    // Buffer for stream mode partial reads
    private byte[]? _partialBuffer;
    private int _partialOffset;
    private UnixSocketInode? _peer;
    private byte[]? _peerSunPathRaw;
    private bool _peerWriteClosed;
    private int _queuedBytes;
    private Action<UnixSocketInode>? _releaseUnbindCallback;
    private bool _shutDownRead;
    private bool _shutDownWrite;

    public UnixSocketInode(ulong ino, SuperBlock sb, SocketType type, KernelScheduler scheduler)
    {
        Ino = ino;
        SuperBlock = sb;
        Type = InodeType.Socket;
        Mode = 0x1ED;
        UnixSocketType = type;
        _scheduler = scheduler;
        _readWaitQueue = new AsyncWaitQueue(scheduler);
        _writeWaitQueue = new AsyncWaitQueue(scheduler);
        _writeWaitQueue.Set(); // Initially writable
    }

    public SocketType UnixSocketType { get; }

    public bool IsListening
    {
        get
        {
            using (EnterStateScope())
            {
                return _listening;
            }
        }
    }

    public bool IsConnected
    {
        get
        {
            using (EnterStateScope())
            {
                return _peer != null;
            }
        }
    }

    public bool IsBound
    {
        get
        {
            using (EnterStateScope())
            {
                return _localSunPathRaw != null;
            }
        }
    }

    bool IDispatcherWaitSource.RegisterWait(LinuxFile file, IReadyDispatcher dispatcher, Action callback,
        short events)
    {
        var scheduler = dispatcher.Scheduler
                        ?? throw new InvalidOperationException(
                            "Unix socket readiness wait requires an explicit scheduler.");
        var registered = false;
        if ((events & PollEvents.POLLIN) != 0)
        {
            _readWaitQueue.Register(callback, scheduler);
            registered = true;
        }

        if ((events & PollEvents.POLLOUT) != 0)
        {
            _writeWaitQueue.Register(callback, scheduler);
            registered = true;
        }

        return registered;
    }

    IDisposable? IDispatcherWaitSource.RegisterWaitHandle(LinuxFile file, IReadyDispatcher dispatcher,
        Action callback, short events)
    {
        var scheduler = dispatcher.Scheduler
                        ?? throw new InvalidOperationException(
                            "Unix socket readiness wait requires an explicit scheduler.");
        var registrations = new List<IDisposable>(2);
        if ((events & PollEvents.POLLIN) != 0)
        {
            var reg = _readWaitQueue.RegisterCancelable(callback, scheduler);
            if (reg != null) registrations.Add(reg);
        }

        if ((events & PollEvents.POLLOUT) != 0)
        {
            var reg = _writeWaitQueue.RegisterCancelable(callback, scheduler);
            if (reg != null) registrations.Add(reg);
        }

        return registrations.Count switch
        {
            0 => null,
            1 => registrations[0],
            _ => new CompositeDisposable(registrations)
        };
    }

    public async ValueTask<int> SendAsync(LinuxFile file, FiberTask task, ReadOnlyMemory<byte> buffer, int flags)
    {
        var rc = await SendMessageAsync(file, task, buffer.ToArray(), null, flags, null);
        if (rc == -(int)Errno.EPIPE) task.PostSignal((int)Signal.SIGPIPE);
        return rc;
    }

    public async ValueTask<int> SendToAsync(LinuxFile file, FiberTask task, ReadOnlyMemory<byte> buffer, int flags,
        object endpoint)
    {
        UnixSocketInode? explicitPeer = null;

        if (endpoint is UnixSockaddrInfo unixAddr)
        {
            if (UnixSocketType != SocketType.Dgram) return -(int)Errno.EISCONN;
            var sm = task.CPU.CurrentSyscallManager;
            if (sm == null) return -(int)Errno.ENOSYS;

            if (unixAddr.IsAbstract)
            {
                explicitPeer = sm.LookupUnixAbstractSocket(unixAddr.AbstractKey);
            }
            else
            {
                var loc = sm.PathWalk(unixAddr.Path);
                if (!loc.IsValid || loc.Dentry?.Inode == null) return -(int)Errno.ENOENT;
                if (loc.Dentry.Inode.Type != InodeType.Socket) return -(int)Errno.ECONNREFUSED;
                explicitPeer = sm.LookupUnixPathSocket(loc.Dentry.Inode);
            }

            if (explicitPeer == null) return -(int)Errno.ECONNREFUSED;
        }
        else if (endpoint != null)
        {
            return -(int)Errno.EAFNOSUPPORT;
        }

        var rc = await SendMessageAsync(file, task, buffer.ToArray(), null, flags, explicitPeer);
        if (rc == -(int)Errno.EPIPE) task.PostSignal((int)Signal.SIGPIPE);
        return rc;
    }

    public async ValueTask<int> RecvAsync(LinuxFile file, FiberTask task, byte[] buffer, int flags, int maxBytes = -1)
    {
        var res = await RecvMessageAsync(file, task, buffer, flags, maxBytes);
        var bytes = res.BytesRead;
        if (bytes > 0 && res.Fds != null)
            foreach (var f in res.Fds)
                f.Close();
        return bytes;
    }

    async ValueTask<RecvMessageResult> ISocketDataOps.RecvFromAsync(LinuxFile file, FiberTask task, byte[] buffer,
        int flags, int maxBytes)
    {
        var res = await RecvMessageAsync(file, task, buffer, flags, maxBytes);
        return new RecvMessageResult(res.BytesRead, res.Fds, null, res.SourceSunPathRaw);
    }

    public async ValueTask<int> SendMsgAsync(LinuxFile file, FiberTask task, byte[] buffer, List<LinuxFile>? fds,
        int flags, object? endpoint)
    {
        UnixSocketInode? explicitPeer = null;

        if (endpoint is UnixSockaddrInfo unixAddr)
        {
            if (UnixSocketType != SocketType.Dgram) return -(int)Errno.EISCONN;
            var sm = task.CPU.CurrentSyscallManager;
            if (sm == null) return -(int)Errno.ENOSYS;

            if (unixAddr.IsAbstract)
            {
                explicitPeer = sm.LookupUnixAbstractSocket(unixAddr.AbstractKey);
            }
            else
            {
                var loc = sm.PathWalk(unixAddr.Path);
                if (!loc.IsValid || loc.Dentry?.Inode == null) return -(int)Errno.ENOENT;
                if (loc.Dentry.Inode.Type != InodeType.Socket) return -(int)Errno.ECONNREFUSED;
                explicitPeer = sm.LookupUnixPathSocket(loc.Dentry.Inode);
            }

            if (explicitPeer == null) return -(int)Errno.ECONNREFUSED;
        }

        var rc = await SendMessageAsync(file, task, buffer, fds, flags, explicitPeer);
        if (rc == -(int)Errno.EPIPE) task.PostSignal((int)Signal.SIGPIPE);
        return rc;
    }

    async ValueTask<RecvMessageResult> ISocketDataOps.RecvMsgAsync(LinuxFile file, FiberTask task, byte[] buffer,
        int flags, int maxBytes)
    {
        var res = await RecvMessageAsync(file, task, buffer, flags, maxBytes);
        return new RecvMessageResult(res.BytesRead, res.Fds, null, res.SourceSunPathRaw);
    }


    // --- Capability Methods ---
    public AddressFamily SocketAddressFamily => AddressFamily.Unix;

    public int Bind(LinuxFile file, FiberTask task, object endpoint)
    {
        if (endpoint is not UnixSockaddrInfo unixAddr) return -(int)Errno.EAFNOSUPPORT;
        if (IsBound) return -(int)Errno.EINVAL;
        if (unixAddr.SunPathRaw.Length == 0) return -(int)Errno.EINVAL;

        var sm = task.CPU.CurrentSyscallManager;
        if (sm == null) return -(int)Errno.ENOSYS;

        if (unixAddr.IsAbstract)
        {
            if (!sm.TryBindUnixAbstractSocket(unixAddr.AbstractKey, this))
                return -(int)Errno.EADDRINUSE;
            SetLocalSunPathRaw(unixAddr.SunPathRaw);
            SetReleaseUnbindCallback(sm.UnbindUnixSocket);
            return 0;
        }

        var (parent, name, createErr) = sm.PathWalkForCreate(unixAddr.Path);
        if (createErr < 0) return createErr;
        if (!parent.IsValid || string.IsNullOrEmpty(name)) return -(int)Errno.EINVAL;
        if (parent.Mount != null && parent.Mount.IsReadOnly) return -(int)Errno.EROFS;

        var existing = sm.PathWalk(unixAddr.Path);
        if (existing.IsValid) return -(int)Errno.EADDRINUSE;

        var uid = task.Process.EUID;
        var gid = task.Process.EGID;
        var mode = DacPolicy.ApplyUmask(Mode & 0x0FFF, task.Process.Umask);
        var socketDentry = new Dentry(name, null, parent.Dentry, parent.Dentry!.SuperBlock);

        try
        {
            parent.Dentry.Inode!.Mknod(socketDentry, mode, uid, gid, InodeType.Socket, 0);
        }
        catch (Exception)
        {
            return -(int)Errno.EACCES;
        }

        if (socketDentry.Inode == null)
        {
            try
            {
                parent.Dentry.Inode!.Unlink(name);
                _ = parent.Dentry.TryUncacheChild(name, "SysBind.rollback.mknod-null-inode", out _);
            }
            catch
            {
            }

            return -(int)Errno.EIO;
        }

        if (!sm.TryBindUnixPathSocket(socketDentry.Inode, this))
        {
            try
            {
                parent.Dentry.Inode!.Unlink(name);
                _ = parent.Dentry.TryUncacheChild(name, "SysBind.rollback.bind-failed", out _);
            }
            catch
            {
            }

            return -(int)Errno.EADDRINUSE;
        }

        SetLocalSunPathRaw(unixAddr.SunPathRaw);
        SetReleaseUnbindCallback(sm.UnbindUnixSocket);
        return 0;
    }

    public async ValueTask<int> ConnectAsync(LinuxFile file, FiberTask task, object endpoint)
    {
        if (endpoint is not UnixSockaddrInfo unixAddr) return -(int)Errno.EAFNOSUPPORT;

        var sm = task.CPU.CurrentSyscallManager;
        if (sm == null) return -(int)Errno.ENOSYS;

        UnixSocketInode? target = null;
        if (unixAddr.IsAbstract)
        {
            target = sm.LookupUnixAbstractSocket(unixAddr.AbstractKey);
        }
        else
        {
            var loc = sm.PathWalk(unixAddr.Path);
            if (!loc.IsValid || loc.Dentry?.Inode == null) return -(int)Errno.ENOENT;
            if (loc.Dentry.Inode.Type != InodeType.Socket) return -(int)Errno.ECONNREFUSED;
            target = sm.LookupUnixPathSocket(loc.Dentry.Inode);
        }

        if (target == null) return -(int)Errno.ECONNREFUSED;
        if (UnixSocketType != target.UnixSocketType) return -(int)Errno.EPROTOTYPE;

        if (UnixSocketType == SocketType.Dgram)
        {
            ConnectPair(target);
            SetPeerSunPathRaw(target.GetLocalSunPathRaw());
            return 0;
        }

        if (IsConnected) return -(int)Errno.EISCONN;
        if (!target.IsListening) return -(int)Errno.ECONNREFUSED;

        var serverConn = new UnixSocketInode(0, SuperBlock, UnixSocketType, _scheduler);
        ConnectPair(serverConn);
        serverConn.ConnectPair(this);
        SetPeerSunPathRaw(target.GetLocalSunPathRaw());
        serverConn.SetLocalSunPathRaw(target.GetLocalSunPathRaw());
        serverConn.SetPeerSunPathRaw(GetLocalSunPathRaw());

        var enqueueRc = target.EnqueueConnection(serverConn);
        if (enqueueRc < 0)
        {
            DisconnectPeer();
            return enqueueRc;
        }

        return 0;
    }

    int ISocketEndpointOps.Listen(LinuxFile file, FiberTask task, int backlog)
    {
        return Listen(backlog);
    }

    async ValueTask<AcceptedSocketResult> ISocketEndpointOps.AcceptAsync(LinuxFile file, FiberTask task, int flags)
    {
        var (rc, inode) = await AcceptAsync(file, task, flags);
        if (rc != 0 || inode == null) return new AcceptedSocketResult(rc, null);
        return new AcceptedSocketResult(0, inode, null, inode.GetPeerSunPathRaw());
    }

    public SocketAddressResult GetSockName(LinuxFile file, FiberTask task)
    {
        return new SocketAddressResult(null, GetLocalSunPathRaw());
    }

    public SocketAddressResult GetPeerName(LinuxFile file, FiberTask task)
    {
        if (!IsConnected) return new SocketAddressResult(Rc: -(int)Errno.ENOTCONN);
        return new SocketAddressResult(null, GetPeerSunPathRaw());
    }

    int ISocketEndpointOps.Shutdown(LinuxFile file, FiberTask task, int how)
    {
        return Shutdown(how);
    }

    public int SetSocketOption(LinuxFile file, FiberTask task, int level, int optname, ReadOnlySpan<byte> optval)
    {
        return -(int)Errno.ENOPROTOOPT;
    }

    public int GetSocketOption(LinuxFile file, FiberTask task, int level, int optname, Span<byte> optval,
        out int written)
    {
        written = 0;
        return -(int)Errno.ENOPROTOOPT;
    }

    public bool RegisterWait(LinuxFile file, FiberTask task, Action callback, short events)
    {
        var registered = false;
        if ((events & PollEvents.POLLIN) != 0)
        {
            _readWaitQueue.Register(callback, task);
            registered = true;
        }

        if ((events & PollEvents.POLLOUT) != 0)
        {
            _writeWaitQueue.Register(callback, task);
            registered = true;
        }

        return registered;
    }

    public IDisposable? RegisterWaitHandle(LinuxFile file, FiberTask task, Action callback, short events)
    {
        var registrations = new List<IDisposable>(2);
        if ((events & PollEvents.POLLIN) != 0)
        {
            var reg = _readWaitQueue.RegisterCancelable(callback, task);
            if (reg != null) registrations.Add(reg);
        }

        if ((events & PollEvents.POLLOUT) != 0)
        {
            var reg = _writeWaitQueue.RegisterCancelable(callback, task);
            if (reg != null) registrations.Add(reg);
        }

        return registrations.Count switch
        {
            0 => null,
            1 => registrations[0],
            _ => new CompositeDisposable(registrations)
        };
    }

    private StateScope EnterStateScope([CallerMemberName] string? caller = null)
    {
        return default;
    }

    public void ConnectPair(UnixSocketInode peer)
    {
        using (EnterStateScope())
        {
            _peer = peer;
            _peerWriteClosed = false;
            _listening = false;
        }
    }

    public void DisconnectPeer()
    {
        using (EnterStateScope())
        {
            _peer = null;
            _peerWriteClosed = false;
            _peerSunPathRaw = null;
        }

        _writeWaitQueue.Set();
    }

    public int Listen(int backlog)
    {
        if (UnixSocketType == SocketType.Dgram) return -(int)Errno.EOPNOTSUPP;

        using (EnterStateScope())
        {
            if (_peer != null) return -(int)Errno.EINVAL;
            _listening = true;
            _listenBacklog = Math.Max(1, backlog);
            return 0;
        }
    }

    public int EnqueueConnection(UnixSocketInode accepted)
    {
        using (EnterStateScope())
        {
            if (!_listening) return -(int)Errno.ECONNREFUSED;
            if (_pendingConnections.Count >= _listenBacklog) return -(int)Errno.EAGAIN;
            _pendingConnections.Enqueue(accepted);
            _readWaitQueue.Set();
            return 0;
        }
    }

    public async ValueTask<(int Rc, UnixSocketInode? Inode)> AcceptAsync(LinuxFile file, FiberTask task, int flags)
    {
        using (EnterStateScope())
        {
            if (!_listening) return (-(int)Errno.EINVAL, null);
        }

        while (true)
        {
            using (EnterStateScope())
            {
                if (_pendingConnections.TryDequeue(out var accepted))
                {
                    if (_pendingConnections.Count == 0)
                        _readWaitQueue.Reset();
                    return (0, accepted);
                }
            }

            if ((file.Flags & FileFlags.O_NONBLOCK) != 0)
                return (-(int)Errno.EAGAIN, null);

            if (task.HasInterruptingPendingSignal())
                return (-(int)Errno.ERESTARTSYS, null);

            await _readWaitQueue.WaitInterruptiblyAsync(task);

            if (task.HasInterruptingPendingSignal())
                return (-(int)Errno.ERESTARTSYS, null);
        }
    }

    public int Shutdown(int how)
    {
        UnixSocketInode? peerToNotifyWriteClosed = null;
        switch (how)
        {
            case 0: // SHUT_RD
                using (EnterStateScope())
                {
                    _shutDownRead = true;
                    _peerWriteClosed = true;
                    _partialBuffer = null;
                    _partialOffset = 0;
                    while (_receiveQueue.TryDequeue(out var dropped))
                        ReleaseQueuedFds(dropped.Fds);
                    _queuedBytes = 0;
                    UpdateWriteWaitQueueState();
                }

                _readWaitQueue.Set();
                _writeWaitQueue.Set();
                return 0;

            case 1: // SHUT_WR
                using (EnterStateScope())
                {
                    if (_shutDownWrite) return 0;
                    _shutDownWrite = true;
                    peerToNotifyWriteClosed = _peer;
                }

                peerToNotifyWriteClosed?.NotifyPeerWriteClosed();
                return 0;

            case 2: // SHUT_RDWR
                using (EnterStateScope())
                {
                    _shutDownRead = true;
                    _shutDownWrite = true;
                    _peerWriteClosed = true;
                    _partialBuffer = null;
                    _partialOffset = 0;
                    while (_receiveQueue.TryDequeue(out var dropped))
                        ReleaseQueuedFds(dropped.Fds);
                    _queuedBytes = 0;
                    peerToNotifyWriteClosed = _peer;
                    UpdateWriteWaitQueueState();
                }

                peerToNotifyWriteClosed?.NotifyPeerWriteClosed();
                _readWaitQueue.Set();
                _writeWaitQueue.Set();
                return 0;

            default:
                return -(int)Errno.EINVAL;
        }
    }

    private void NotifyPeerWriteClosed()
    {
        using (EnterStateScope())
        {
            _peerWriteClosed = true;
        }

        _readWaitQueue.Set();
        _writeWaitQueue.Set();
    }

    public void SetLocalSunPathRaw(byte[]? raw)
    {
        using (EnterStateScope())
        {
            _localSunPathRaw = raw == null ? null : [.. raw];
        }
    }

    public byte[]? GetLocalSunPathRaw()
    {
        using (EnterStateScope())
        {
            return _localSunPathRaw == null ? null : [.. _localSunPathRaw];
        }
    }

    public void SetPeerSunPathRaw(byte[]? raw)
    {
        using (EnterStateScope())
        {
            _peerSunPathRaw = raw == null ? null : [.. raw];
        }
    }

    public byte[]? GetPeerSunPathRaw()
    {
        using (EnterStateScope())
        {
            return _peerSunPathRaw == null ? null : [.. _peerSunPathRaw];
        }
    }

    public void SetReleaseUnbindCallback(Action<UnixSocketInode>? callback)
    {
        using (EnterStateScope())
        {
            _releaseUnbindCallback = callback;
        }
    }

    public override short Poll(LinuxFile file, short events)
    {
        short revents = 0;
        using (EnterStateScope())
        {
            if ((events & PollEvents.POLLIN) != 0)
            {
                if (_listening)
                {
                    if (_pendingConnections.Count > 0)
                        revents |= PollEvents.POLLIN;
                }
                else if (_receiveQueue.Count > 0 || _partialBuffer != null || _shutDownRead || _peerWriteClosed)
                {
                    revents |= PollEvents.POLLIN;
                }
            }

            if ((events & PollEvents.POLLOUT) != 0)
            {
                if (_listening)
                {
                    // listening sockets are not writable
                }
                else if (_shutDownWrite || _peer == null || _peer._shutDownRead)
                {
                    revents |= PollEvents.POLLERR;
                }
                else if (_peer._queuedBytes < MaxSendBuffer)
                {
                    revents |= PollEvents.POLLOUT;
                }
            }

            if (_shutDownRead && _shutDownWrite) revents |= PollEvents.POLLHUP;
        }

        return revents;
    }

    public override bool RegisterWait(LinuxFile file, Action callback, short events)
    {
        return false;
    }

    public override IDisposable? RegisterWaitHandle(LinuxFile file, Action callback, short events)
    {
        return null;
    }

    public void EnqueueMessage(UnixMessage msg)
    {
        using (EnterStateScope())
        {
            if (_shutDownRead || _peerWriteClosed)
            {
                ReleaseQueuedFds(msg.Fds);
                return; // Dropped
            }

            _receiveQueue.Enqueue(msg);
            _queuedBytes += msg.Data.Length;
            UpdateWriteWaitQueueState();
        }

        _readWaitQueue.Set(); // Signal that data is available (like PipeInode)
    }

    public void PeerDisconnected()
    {
        using (EnterStateScope())
        {
            _shutDownRead = true;
            _shutDownWrite = true;
            _peerWriteClosed = true;
            _peer = null;
            UpdateWriteWaitQueueState();
        }

        _readWaitQueue.Signal();
        _writeWaitQueue.Signal();
    }

    public async ValueTask<(int BytesRead, List<LinuxFile>? Fds, byte[]? SourceSunPathRaw)> RecvMessageAsync(
        LinuxFile file, FiberTask task,
        byte[] buffer, int flags, int maxBytes = -1)
    {
        var recvLen = maxBytes > 0 ? Math.Min(maxBytes, buffer.Length) : buffer.Length;
        if (recvLen <= 0) return (0, null, null);
        var nonBlocking = (file.Flags & FileFlags.O_NONBLOCK) != 0 ||
                          (flags & LinuxConstants.MSG_DONTWAIT) != 0;
        while (true)
        {
            using (EnterStateScope())
            {
                if (_partialBuffer != null)
                {
                    var toCopy = Math.Min(recvLen, _partialBuffer.Length - _partialOffset);
                    Array.Copy(_partialBuffer, _partialOffset, buffer, 0, toCopy);
                    _partialOffset += toCopy;
                    if (_partialOffset >= _partialBuffer.Length)
                    {
                        _partialBuffer = null;
                        _partialOffset = 0;
                    }

                    if (_receiveQueue.Count == 0 && _partialBuffer == null)
                        _readWaitQueue.Reset();
                    _queuedBytes -= toCopy;
                    UpdateWriteWaitQueueState();
                    _writeWaitQueue.Set(); // signal backpressure relief
                    return (toCopy, null, null); // FDs only on boundary
                }

                if (_receiveQueue.TryDequeue(out var msg))
                {
                    var toCopy = Math.Min(recvLen, msg.Data.Length);
                    Array.Copy(msg.Data, 0, buffer, 0, toCopy);

                    if (UnixSocketType == SocketType.Stream && toCopy < msg.Data.Length)
                    {
                        _partialBuffer = msg.Data;
                        _partialOffset = toCopy;
                    }

                    // For Datagram, truncated bytes are discarded.
                    _queuedBytes -= toCopy;
                    if (UnixSocketType != SocketType.Stream || toCopy >= msg.Data.Length)
                        _queuedBytes -= msg.Data.Length - toCopy; // discard remainder for dgram
                    UpdateWriteWaitQueueState();
                    if (_receiveQueue.Count == 0 && _partialBuffer == null)
                        _readWaitQueue.Reset();

                    return (toCopy, msg.Fds, msg.SourceSunPathRaw);
                }

                if (_shutDownRead || _peer == null || _peerWriteClosed)
                    return (0, null, null); // EOF
            }

            _writeWaitQueue.Set(); // backpressure relief

            if (nonBlocking) return (-(int)Errno.EAGAIN, null, null);

            if (task.HasInterruptingPendingSignal()) return (-(int)Errno.ERESTARTSYS, null, null);

            await _readWaitQueue.WaitInterruptiblyAsync(task);

            if (task.HasInterruptingPendingSignal()) return (-(int)Errno.ERESTARTSYS, null, null);
        }
    }

    public async ValueTask<int> SendMessageAsync(LinuxFile file, FiberTask task, byte[] data, List<LinuxFile>? fds,
        int flags)
    {
        return await SendMessageAsync(file, task, data, fds, flags, null);
    }

    public async ValueTask<int> SendMessageAsync(LinuxFile file, FiberTask task, byte[] data, List<LinuxFile>? fds,
        int flags,
        UnixSocketInode? explicitPeer)
    {
        var nonBlocking = (file.Flags & FileFlags.O_NONBLOCK) != 0 ||
                          (flags & LinuxConstants.MSG_DONTWAIT) != 0;
        UnixSocketInode? peer;
        using (EnterStateScope())
        {
            if (_shutDownWrite) return -(int)Errno.EPIPE;
            if (_listening) return -(int)Errno.EINVAL;
            peer = explicitPeer ?? _peer;
        }

        if (peer == null)
            return UnixSocketType == SocketType.Dgram ? -(int)Errno.EDESTADDRREQ : -(int)Errno.ENOTCONN;

        // Backpressure: check peer's queued bytes
        while (true)
        {
            using (peer.EnterStateScope())
            {
                if (peer._shutDownRead) return -(int)Errno.EPIPE;
                if (peer._queuedBytes < MaxSendBuffer)
                    break; // Space available
            }

            if (nonBlocking)
                return -(int)Errno.EAGAIN;

            await peer._writeWaitQueue.WaitAsync(task);

            // Re-check connection after wakeup
            using (EnterStateScope())
            {
                if (_shutDownWrite) return -(int)Errno.EPIPE;
                var currentPeer = explicitPeer ?? _peer;
                if (currentPeer == null) return -(int)Errno.EPIPE;
                peer = currentPeer;
            }
        }

        var clonedData = new byte[data.Length];
        Array.Copy(data, clonedData, data.Length);

        var msg = new UnixMessage { Data = clonedData, SourceSunPathRaw = GetLocalSunPathRaw() };
        if (fds != null)
        {
            // Bump refcounts because they are in the queue now
            foreach (var f in fds) f.Get();
            msg.Fds.AddRange(fds);
        }

        peer.EnqueueMessage(msg);
        return data.Length;
    }

    private void CloseLifecycleOnce()
    {
        Action<UnixSocketInode>? callback;
        UnixSocketInode? peerToNotify;
        using (EnterStateScope())
        {
            if (_lifecycleClosed) return;
            _lifecycleClosed = true;
            callback = _releaseUnbindCallback;
            _releaseUnbindCallback = null;
            peerToNotify = _peer;
            _peer = null;
            _shutDownRead = true;
            _shutDownWrite = true;
            _peerWriteClosed = true;
            _partialBuffer = null;
            _partialOffset = 0;
            while (_receiveQueue.TryDequeue(out var msg)) ReleaseQueuedFds(msg.Fds);

            _queuedBytes = 0;
            UpdateWriteWaitQueueState();
        }

        peerToNotify?.PeerDisconnected();
        _readWaitQueue.Signal();
        _writeWaitQueue.Signal();
        callback?.Invoke(this);
    }

    public override void Release(LinuxFile linuxFile)
    {
        CloseLifecycleOnce();
    }

    protected override void OnEvictCache()
    {
        CloseLifecycleOnce();
        base.OnEvictCache();
    }

    public override int Read(LinuxFile file, Span<byte> buffer, long offset)
    {
        using (EnterStateScope())
        {
            if (_partialBuffer != null)
            {
                var toCopy = Math.Min(buffer.Length, _partialBuffer.Length - _partialOffset);
                new ReadOnlySpan<byte>(_partialBuffer, _partialOffset, toCopy).CopyTo(buffer.Slice(0, toCopy));
                _partialOffset += toCopy;
                if (_partialOffset >= _partialBuffer.Length)
                {
                    _partialBuffer = null;
                    _partialOffset = 0;
                }

                // Reset read wait queue if no more data
                if (_receiveQueue.Count == 0 && _partialBuffer == null)
                    _readWaitQueue.Reset();
                _queuedBytes -= toCopy;
                UpdateWriteWaitQueueState();
                _writeWaitQueue.Set(); // backpressure relief 
                return toCopy;
            }

            if (_receiveQueue.TryDequeue(out var msg))
            {
                var toCopy = Math.Min(buffer.Length, msg.Data.Length);
                new ReadOnlySpan<byte>(msg.Data, 0, toCopy).CopyTo(buffer.Slice(0, toCopy));

                if (UnixSocketType == SocketType.Stream && toCopy < msg.Data.Length)
                {
                    _partialBuffer = msg.Data;
                    _partialOffset = toCopy;
                }

                if (msg.Fds != null)
                    foreach (var f in msg.Fds)
                        f.Close();

                // Reset read wait queue if no more data
                if (_receiveQueue.Count == 0 && _partialBuffer == null)
                    _readWaitQueue.Reset();

                _queuedBytes -= msg.Data.Length;
                if (UnixSocketType == SocketType.Stream && toCopy < msg.Data.Length)
                    _queuedBytes += msg.Data.Length - toCopy; // partial still queued
                UpdateWriteWaitQueueState();
                _writeWaitQueue.Set(); // backpressure relief

                return toCopy;
            }

            if (_shutDownRead || _peer == null || _peerWriteClosed)
                return 0; // EOF
        }

        return -(int)Errno.EAGAIN;
    }

    public override int Write(LinuxFile file, ReadOnlySpan<byte> buffer, long offset)
    {
        UnixSocketInode? peer;
        using (EnterStateScope())
        {
            if (_shutDownWrite) return -(int)Errno.EPIPE;
            if (_listening) return -(int)Errno.EINVAL;
            if (_peer == null)
                return UnixSocketType == SocketType.Dgram ? -(int)Errno.EDESTADDRREQ : -(int)Errno.ENOTCONN;
            peer = _peer;
        }

        // Backpressure check for synchronous write
        using (peer.EnterStateScope())
        {
            if (peer._shutDownRead)
                return -(int)Errno.EPIPE;
            if (peer._queuedBytes >= MaxSendBuffer)
                return -(int)Errno.EAGAIN;
        }

        var clonedData = buffer.ToArray();
        var msg = new UnixMessage { Data = clonedData, SourceSunPathRaw = GetLocalSunPathRaw() };
        peer.EnqueueMessage(msg);
        return buffer.Length;
    }

    private static void ReleaseQueuedFds(List<LinuxFile>? fds)
    {
        if (fds == null || fds.Count == 0) return;
        foreach (var f in fds) f.Close();
    }

    private void UpdateWriteWaitQueueState()
    {
        if (_queuedBytes >= MaxSendBuffer || _shutDownRead)
            _writeWaitQueue.Reset();
        else
            _writeWaitQueue.Set();
    }

    // Single-thread scheduling model: keep a using-scope shape so lock semantics
    // can be introduced centrally later without changing call sites.
    private readonly struct StateScope : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class CompositeDisposable : IDisposable
    {
        private List<IDisposable>? _items;

        public CompositeDisposable(List<IDisposable> items)
        {
            _items = items;
        }

        public void Dispose()
        {
            var items = Interlocked.Exchange(ref _items, null);
            if (items == null) return;
            foreach (var item in items) item.Dispose();
        }
    }
}
