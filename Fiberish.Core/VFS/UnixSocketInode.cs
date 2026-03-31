using System.Buffers;
using System.Buffers.Binary;
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
    public UnixCredentials? Credentials { get; init; }
}

internal sealed record UnixSocketDebugState(
    int QueuedBytes,
    int ReceiveQueueCount,
    int PartialBytesRemaining,
    bool PeerWriteClosed,
    bool ShutDownRead,
    bool ShutDownWrite,
    bool HasPeer,
    bool IsListening,
    bool ReadWaitSignaled,
    bool WriteWaitSignaled);

internal sealed record UnixSocketDebugDequeueResult(
    int BytesRead,
    byte[] Data,
    int FdCount,
    UnixSocketDebugState StateAfter);

public class UnixSocketInode : Inode, ITaskWaitSource, IDispatcherWaitSource, ISocketEndpointOps, ISocketDataOps,
    ISocketOptionOps
{
    // Backpressure: track queued bytes to limit memory usage
    private const int MaxSendBuffer = 262144; // 256KB
    private readonly Queue<UnixSocketInode> _pendingConnections = new();
    private readonly AsyncWaitQueue _readWaitQueue;

    // For AF_UNIX
    private readonly Queue<UnixMessage> _receiveQueue = new();
    private readonly KernelScheduler _scheduler;
    private readonly AsyncWaitQueue _writeWaitQueue;
    private bool _lifecycleClosed;
    private int _listenBacklog = 1;
    private bool _listening;
    private byte[]? _localSunPathRaw;

    // Buffer for stream mode partial reads
    private byte[]? _partialBuffer;
    private int _partialOffset;
    private bool _passCred;
    private UnixSocketInode? _peer;
    private UnixCredentials? _peerCredentials;
    private byte[]? _peerSunPathRaw;
    private bool _peerWriteClosed;
    private int _queuedBytes;
    private Action<UnixSocketInode>? _releaseUnbindCallback;
    private UnixCredentials? _sendCredentialsOverride;
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
        using (EnterStateScope())
        {
            var readWatch = new QueueReadinessWatch(PollEvents.POLLIN, IsReadReady, _readWaitQueue,
                _readWaitQueue.Reset);
            var writeWatch = BuildWriteWatch(events);
            return QueueReadinessRegistration.Register(callback, scheduler, events, readWatch, writeWatch);
        }
    }

    IDisposable? IDispatcherWaitSource.RegisterWaitHandle(LinuxFile file, IReadyDispatcher dispatcher,
        Action callback, short events)
    {
        var scheduler = dispatcher.Scheduler
                        ?? throw new InvalidOperationException(
                            "Unix socket readiness wait requires an explicit scheduler.");
        using (EnterStateScope())
        {
            var readWatch = new QueueReadinessWatch(PollEvents.POLLIN, IsReadReady, _readWaitQueue,
                _readWaitQueue.Reset);
            var writeWatch = BuildWriteWatch(events);
            return QueueReadinessRegistration.RegisterHandle(callback, scheduler, events, readWatch, writeWatch);
        }
    }

    public async ValueTask<int> SendAsync(LinuxFile file, FiberTask task, ReadOnlyMemory<byte> buffer, int flags)
    {
        var rc = await SendMessageAsync(file, task, buffer, null, flags, null);
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

        var rc = await SendMessageAsync(file, task, buffer, null, flags, explicitPeer);
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
        return new RecvMessageResult(res.BytesRead, res.Fds, null, res.SourceSunPathRaw, res.Credentials);
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
        return new RecvMessageResult(res.BytesRead, res.Fds, null, res.SourceSunPathRaw, res.Credentials);
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
        serverConn.SetPeerCredentials(new UnixCredentials(task.Process.TGID, task.Process.EUID, task.Process.EGID));

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
        if (level != LinuxConstants.SOL_SOCKET)
            return -(int)Errno.ENOPROTOOPT;

        switch (optname)
        {
            case LinuxConstants.SO_REUSEADDR:
            case LinuxConstants.SO_KEEPALIVE:
            case LinuxConstants.SO_SNDBUF:
            case LinuxConstants.SO_RCVBUF:
            case 12: // SO_PRIORITY
                return 0;
            case LinuxConstants.SO_PASSCRED:
                if (optval.Length < 4)
                    return -(int)Errno.EINVAL;
                _passCred = BinaryPrimitives.ReadInt32LittleEndian(optval) != 0;
                return 0;
            default:
                _ = file;
                _ = task;
                return -(int)Errno.ENOPROTOOPT;
        }
    }

    public int GetSocketOption(LinuxFile file, FiberTask task, int level, int optname, Span<byte> optval,
        out int written)
    {
        written = 4;
        if (level != LinuxConstants.SOL_SOCKET)
            return -(int)Errno.ENOPROTOOPT;

        switch (optname)
        {
            case LinuxConstants.SO_TYPE:
                BinaryPrimitives.WriteInt32LittleEndian(optval, UnixSocketType switch
                {
                    SocketType.Stream => LinuxConstants.SOCK_STREAM,
                    SocketType.Dgram => LinuxConstants.SOCK_DGRAM,
                    SocketType.Seqpacket => LinuxConstants.SOCK_SEQPACKET,
                    _ => 0
                });
                return 0;
            case LinuxConstants.SO_ERROR:
                BinaryPrimitives.WriteInt32LittleEndian(optval, 0);
                return 0;
            case LinuxConstants.SO_SNDBUF:
            case LinuxConstants.SO_RCVBUF:
                BinaryPrimitives.WriteInt32LittleEndian(optval, MaxSendBuffer);
                return 0;
            case LinuxConstants.SO_PASSCRED:
                BinaryPrimitives.WriteInt32LittleEndian(optval, _passCred ? 1 : 0);
                return 0;
            default:
                return -(int)Errno.ENOPROTOOPT;
        }
    }

    public bool RegisterWait(LinuxFile file, FiberTask task, Action callback, short events)
    {
        using (EnterStateScope())
        {
            var readWatch = new QueueReadinessWatch(PollEvents.POLLIN, IsReadReady, _readWaitQueue,
                _readWaitQueue.Reset);
            var writeWatch = BuildWriteWatch(events);
            return QueueReadinessRegistration.Register(callback, task, events, readWatch, writeWatch);
        }
    }

    public IDisposable? RegisterWaitHandle(LinuxFile file, FiberTask task, Action callback, short events)
    {
        using (EnterStateScope())
        {
            var readWatch = new QueueReadinessWatch(PollEvents.POLLIN, IsReadReady, _readWaitQueue,
                _readWaitQueue.Reset);
            var writeWatch = BuildWriteWatch(events);
            return QueueReadinessRegistration.RegisterHandle(callback, task, events, readWatch, writeWatch);
        }
    }

    internal UnixSocketDebugState GetDebugState()
    {
        using (EnterStateScope())
        {
            return new UnixSocketDebugState(
                _queuedBytes,
                _receiveQueue.Count,
                _partialBuffer == null ? 0 : _partialBuffer.Length - _partialOffset,
                _peerWriteClosed,
                _shutDownRead,
                _shutDownWrite,
                _peer != null,
                _listening,
                _readWaitQueue.IsSignaled,
                _writeWaitQueue.IsSignaled);
        }
    }

    internal UnixSocketDebugDequeueResult? DebugTryDequeue(int maxBytes)
    {
        using (EnterStateScope())
        {
            if (_partialBuffer != null)
            {
                var toCopy = Math.Min(maxBytes, _partialBuffer.Length - _partialOffset);
                var data = new byte[toCopy];
                Array.Copy(_partialBuffer, _partialOffset, data, 0, toCopy);
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
                _writeWaitQueue.Set();
                return new UnixSocketDebugDequeueResult(toCopy, data, 0, GetDebugState());
            }

            if (!_receiveQueue.TryDequeue(out var msg))
                return null;

            var bytes = Math.Min(maxBytes, msg.Data.Length);
            var copied = new byte[bytes];
            Array.Copy(msg.Data, 0, copied, 0, bytes);

            if (UnixSocketType == SocketType.Stream && bytes < msg.Data.Length)
            {
                _partialBuffer = msg.Data;
                _partialOffset = bytes;
            }

            _queuedBytes -= bytes;
            if (UnixSocketType != SocketType.Stream || bytes >= msg.Data.Length)
                _queuedBytes -= msg.Data.Length - bytes;
            UpdateWriteWaitQueueState();
            if (_receiveQueue.Count == 0 && _partialBuffer == null)
                _readWaitQueue.Reset();
            return new UnixSocketDebugDequeueResult(bytes, copied, msg.Fds.Count, GetDebugState());
        }
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

    public void SetPeerCredentials(UnixCredentials? credentials)
    {
        using (EnterStateScope())
        {
            _peerCredentials = credentials;
        }
    }

    public UnixCredentials? GetPeerCredentials()
    {
        using (EnterStateScope())
        {
            return _peerCredentials;
        }
    }

    public void SetSendCredentialsOverride(UnixCredentials? credentials)
    {
        using (EnterStateScope())
        {
            _sendCredentialsOverride = credentials;
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
            var readWatch = new QueueReadinessWatch(PollEvents.POLLIN, IsReadReady, _readWaitQueue,
                _readWaitQueue.Reset);
            var writeWatch = BuildWriteWatch(events);
            revents |= QueueReadinessRegistration.ComputeRevents(events, readWatch, writeWatch);

            if ((events & PollEvents.POLLOUT) != 0 && !_listening &&
                (_shutDownWrite || _peer == null || _peer._shutDownRead))
                revents |= PollEvents.POLLERR;

            if (_shutDownRead && _shutDownWrite) revents |= PollEvents.POLLHUP;
        }

        return revents;
    }

    private bool IsReadReady()
    {
        if (_listening)
            return _pendingConnections.Count > 0;

        return _receiveQueue.Count > 0 || _partialBuffer != null || _shutDownRead || _peerWriteClosed;
    }

    private QueueReadinessWatch BuildWriteWatch(short events)
    {
        if ((events & PollEvents.POLLOUT) == 0 || _listening || _shutDownWrite || _peer == null || _peer._shutDownRead)
            return default;

        return new QueueReadinessWatch(PollEvents.POLLOUT, () => _peer._queuedBytes < MaxSendBuffer, _writeWaitQueue,
            _writeWaitQueue.Reset);
    }

    public override bool RegisterWait(LinuxFile file, Action callback, short events)
    {
        return false;
    }

    public override IDisposable? RegisterWaitHandle(LinuxFile file, Action callback, short events)
    {
        return null;
    }

    public override async ValueTask WaitForRead(LinuxFile file, FiberTask task)
    {
        await _readWaitQueue.WaitInterruptiblyAsync(task);
    }

    public override async ValueTask WaitForWrite(LinuxFile file, FiberTask task)
    {
        await _writeWaitQueue.WaitInterruptiblyAsync(task);
    }

    public void EnqueueMessage(UnixMessage msg)
    {
        using (EnterStateScope())
        {
            if (_shutDownRead || _peerWriteClosed)
            {
                ReleaseQueuedFds(msg.Fds);
                return;
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

    public async
        ValueTask<(int BytesRead, List<LinuxFile>? Fds, byte[]? SourceSunPathRaw, UnixCredentials? Credentials)>
        RecvMessageAsync(
            LinuxFile file, FiberTask task,
            byte[] buffer, int flags, int maxBytes = -1)
    {
        var recvLen = maxBytes > 0 ? Math.Min(maxBytes, buffer.Length) : buffer.Length;
        if (recvLen <= 0) return (0, null, null, null);
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
                    _writeWaitQueue.Set();
                    return (toCopy, null, null, null);
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
                    return (toCopy, msg.Fds, msg.SourceSunPathRaw, msg.Credentials);
                }

                if (_shutDownRead || _peer == null || _peerWriteClosed)
                    return (0, null, null, null); // EOF
            }

            _writeWaitQueue.Set();

            if (nonBlocking)
                return (-(int)Errno.EAGAIN, null, null, null);

            if (task.HasInterruptingPendingSignal()) return (-(int)Errno.ERESTARTSYS, null, null, null);

            await _readWaitQueue.WaitInterruptiblyAsync(task);

            if (task.HasInterruptingPendingSignal()) return (-(int)Errno.ERESTARTSYS, null, null, null);
        }
    }

    public async ValueTask<int> SendMessageAsync(LinuxFile file, FiberTask task, byte[] data, List<LinuxFile>? fds,
        int flags)
    {
        return await SendMessageAsync(file, task, data, fds, flags, null);
    }

    public async ValueTask<int> SendMessageAsync(LinuxFile file, FiberTask task, ReadOnlyMemory<byte> data,
        List<LinuxFile>? fds, int flags, UnixSocketInode? explicitPeer)
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

        while (true)
        {
            using (peer.EnterStateScope())
            {
                if (peer._shutDownRead) return -(int)Errno.EPIPE;
                if (peer._queuedBytes < MaxSendBuffer)
                    break;
            }

            if (nonBlocking)
                return -(int)Errno.EAGAIN;

            await peer._writeWaitQueue.WaitAsync(task);

            using (EnterStateScope())
            {
                if (_shutDownWrite) return -(int)Errno.EPIPE;
                var currentPeer = explicitPeer ?? _peer;
                if (currentPeer == null) return -(int)Errno.EPIPE;
                peer = currentPeer;
            }
        }

        var clonedData = data.ToArray();
        UnixCredentials? credentials = null;
        if (peer._passCred)
            credentials = _sendCredentialsOverride ??
                          new UnixCredentials(task.Process.TGID, task.Process.EUID, task.Process.EGID);

        var msg = new UnixMessage
        {
            Data = clonedData,
            SourceSunPathRaw = GetLocalSunPathRaw(),
            Credentials = credentials
        };
        if (fds != null)
        {
            foreach (var f in fds) f.Get();
            msg.Fds.AddRange(fds);
        }

        peer.EnqueueMessage(msg);
        return clonedData.Length;
    }

    public async ValueTask<int> SendMessageAsync(LinuxFile file, FiberTask task, byte[] data, List<LinuxFile>? fds,
        int flags,
        UnixSocketInode? explicitPeer)
    {
        return await SendMessageAsync(file, task, data.AsMemory(), fds, flags, explicitPeer);
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

    protected internal override int ReadSpan(LinuxFile file, Span<byte> buffer, long offset)
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

    protected internal override int WriteSpan(LinuxFile file, ReadOnlySpan<byte> buffer, long offset)
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

    public override async ValueTask<int> ReadV(Engine engine, LinuxFile file, FiberTask? task,
        IReadOnlyList<Iovec> iovs, long offset, int flags)
    {
        if (task == null) return -(int)Errno.EPERM;

        var totalCap = 0L;
        foreach (var iov in iovs) totalCap += iov.Len;
        if (totalCap == 0) return 0;

        var recvFlags = 0;
        if ((file.Flags & FileFlags.O_NONBLOCK) != 0 || (flags & 0x00000008) != 0)
            recvFlags |= LinuxConstants.MSG_DONTWAIT;

        var chunkBuf = ArrayPool<byte>.Shared.Rent((int)Math.Min(totalCap, 65536));
        try
        {
            var totalRead = 0;
            var iovIndex = 0;
            var iovOffset = 0u;

            while (totalRead < totalCap)
            {
                var gatherLen = (int)Math.Min(totalCap - totalRead, 65536);

                // For stream sockets, after first successful read we don't want to block
                var currentFlags = recvFlags;
                if (totalRead > 0 && UnixSocketType == SocketType.Stream)
                    currentFlags |= LinuxConstants.MSG_DONTWAIT;

                var n = await RecvAsync(file, task, chunkBuf, currentFlags, gatherLen);

                if (n < 0)
                {
                    if (n == -(int)Errno.EAGAIN && totalRead > 0)
                        break;
                    return totalRead > 0 ? totalRead : n;
                }

                if (n == 0) break; // EOF

                var chunkProcessed = 0;
                while (chunkProcessed < n && iovIndex < iovs.Count)
                {
                    var iov = iovs[iovIndex];
                    var rem = iov.Len - iovOffset;
                    if (rem == 0)
                    {
                        iovIndex++;
                        iovOffset = 0;
                        continue;
                    }

                    var toCopy = (int)Math.Min(n - chunkProcessed, rem);
                    if (!engine.CopyToUser(iov.BaseAddr + iovOffset, chunkBuf.AsSpan(chunkProcessed, toCopy)))
                        return totalRead > 0 ? totalRead : -(int)Errno.EFAULT;

                    chunkProcessed += toCopy;
                    totalRead += toCopy;
                    iovOffset += (uint)toCopy;
                    if (iovOffset >= iov.Len)
                    {
                        iovIndex++;
                        iovOffset = 0;
                    }
                }

                if (UnixSocketType != SocketType.Stream)
                    break; // Datagrams only read one message!
            }

            return totalRead;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunkBuf);
        }
    }

    public override async ValueTask<int> WriteV(Engine engine, LinuxFile file, FiberTask? task,
        IReadOnlyList<Iovec> iovs, long offset, int flags)
    {
        if (task == null) return -(int)Errno.EPERM;

        var totalLen = 0L;
        foreach (var iov in iovs) totalLen += iov.Len;
        if (totalLen == 0) return 0;
        if (totalLen > 65536 && UnixSocketType == SocketType.Dgram)
            return -(int)Errno.EMSGSIZE;

        var toRent = UnixSocketType == SocketType.Dgram ? (int)totalLen : (int)Math.Min(totalLen, 65536);
        var chunkBuf = ArrayPool<byte>.Shared.Rent(toRent);
        try
        {
            var totalWritten = 0;
            var iovIndex = 0;
            var iovOffset = 0u;

            while (totalWritten < totalLen)
            {
                var gatherLen = 0;

                // Gather up to 64K
                while (iovIndex < iovs.Count && gatherLen < 65536)
                {
                    var iov = iovs[iovIndex];
                    var rem = iov.Len - iovOffset;
                    if (rem == 0)
                    {
                        iovIndex++;
                        iovOffset = 0;
                        continue;
                    }

                    var copyLen = (int)Math.Min(rem, 65536 - gatherLen);
                    if (!engine.CopyFromUser(iov.BaseAddr + iovOffset, chunkBuf.AsSpan(gatherLen, copyLen)))
                        return totalWritten > 0 ? totalWritten : -(int)Errno.EFAULT;

                    gatherLen += copyLen;
                    iovOffset += (uint)copyLen;
                    if (iovOffset >= iov.Len)
                    {
                        iovIndex++;
                        iovOffset = 0;
                    }
                }

                if (gatherLen == 0) break;

                var payload = chunkBuf.AsMemory(0, gatherLen);
                var sendFlags = 0;
                if ((flags & 0x00000008) != 0 /* RWF_NOWAIT */)
                    sendFlags |= LinuxConstants.MSG_DONTWAIT;

                var n = await SendAsync(file, task, payload, sendFlags);

                if (n == -(int)Errno.EPIPE)
                {
                    task.PostSignal((int)Signal.SIGPIPE);
                    return n;
                }

                if (n < 0)
                    return totalWritten > 0 ? totalWritten : n;

                totalWritten += n;
                if (n < gatherLen) break; // Partial write
            }

            return totalWritten;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunkBuf);
        }
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
}
