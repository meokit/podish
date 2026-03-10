using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Threading;
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

public class UnixSocketInode : Inode
{
    private readonly AsyncWaitQueue _readWaitQueue = new();
    private readonly AsyncWaitQueue _writeWaitQueue = new();
    
    // For AF_UNIX
    private readonly Queue<UnixMessage> _receiveQueue = new();
    private readonly Queue<UnixSocketInode> _pendingConnections = new();
    private UnixSocketInode? _peer;
    private bool _shutDownRead;
    private bool _shutDownWrite;
    private bool _peerWriteClosed;
    private bool _listening;
    private int _listenBacklog = 1;
    private byte[]? _localSunPathRaw;
    private byte[]? _peerSunPathRaw;
    private Action<UnixSocketInode>? _releaseUnbindCallback;
    private bool _lifecycleClosed;
    private readonly SocketType _socketType;

    // Buffer for stream mode partial reads
    private byte[]? _partialBuffer;
    private int _partialOffset;

    // Backpressure: track queued bytes to limit memory usage
    private const int MaxSendBuffer = 262144; // 256KB
    private int _queuedBytes;

    // Single-thread scheduling model: keep a using-scope shape so lock semantics
    // can be introduced centrally later without changing call sites.
    private readonly struct StateScope : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private StateScope EnterStateScope([CallerMemberName] string? caller = null)
    {
        KernelScheduler.Current?.AssertSchedulerThread(caller);
        return default;
    }

    public UnixSocketInode(ulong ino, SuperBlock sb, SocketType type)
    {
        Ino = ino;
        SuperBlock = sb;
        Type = InodeType.Socket;
        Mode = 0x1ED;
        _socketType = type;
        _writeWaitQueue.Set(); // Initially writable
    }

    public SocketType UnixSocketType => _socketType;

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
        if (_socketType == SocketType.Dgram) return -(int)Errno.EOPNOTSUPP;

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

    public async ValueTask<(int Rc, UnixSocketInode? Inode)> AcceptAsync(LinuxFile file, int flags)
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

            var task = KernelScheduler.Current?.CurrentTask;
            if (task != null && task.HasUnblockedPendingSignal())
                return (-(int)Errno.ERESTARTSYS, null);

            await _readWaitQueue.WaitAsync();

            if (task != null && task.HasUnblockedPendingSignal())
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

            if (_shutDownRead && _shutDownWrite)
            {
                revents |= PollEvents.POLLHUP;
            }
        }
        return revents;
    }

    public override bool RegisterWait(LinuxFile file, Action callback, short events)
    {
        bool registered = false;
        if ((events & PollEvents.POLLIN) != 0)
        {
            _readWaitQueue.Register(callback);
            registered = true;
        }

        if ((events & PollEvents.POLLOUT) != 0)
        {
            _writeWaitQueue.Register(callback);
            registered = true;
        }

        return registered;
    }

    public override IDisposable? RegisterWaitHandle(LinuxFile file, Action callback, short events)
    {
        var registrations = new List<IDisposable>(2);
        if ((events & PollEvents.POLLIN) != 0)
        {
            var reg = _readWaitQueue.RegisterCancelable(callback);
            if (reg != null) registrations.Add(reg);
        }

        if ((events & PollEvents.POLLOUT) != 0)
        {
            var reg = _writeWaitQueue.RegisterCancelable(callback);
            if (reg != null) registrations.Add(reg);
        }

        return registrations.Count switch
        {
            0 => null,
            1 => registrations[0],
            _ => new CompositeDisposable(registrations)
        };
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

    public async ValueTask<(int BytesRead, List<LinuxFile>? Fds, byte[]? SourceSunPathRaw)> RecvMessageAsync(LinuxFile file,
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
                    int toCopy = Math.Min(recvLen, _partialBuffer.Length - _partialOffset);
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
                    int toCopy = Math.Min(recvLen, msg.Data.Length);
                    Array.Copy(msg.Data, 0, buffer, 0, toCopy);
                    
                    if (_socketType == SocketType.Stream && toCopy < msg.Data.Length)
                    {
                        _partialBuffer = msg.Data;
                        _partialOffset = toCopy;
                    }
                    // For Datagram, truncated bytes are discarded.
                    _queuedBytes -= toCopy;
                    if (_socketType != SocketType.Stream || toCopy >= msg.Data.Length)
                        _queuedBytes -= (msg.Data.Length - toCopy); // discard remainder for dgram
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

            var task = KernelScheduler.Current?.CurrentTask;
            if (task != null && task.HasUnblockedPendingSignal()) return (-(int)Errno.ERESTARTSYS, null, null);

            await _readWaitQueue.WaitAsync();

            if (task != null && task.HasUnblockedPendingSignal()) return (-(int)Errno.ERESTARTSYS, null, null);
        }
    }

    public async ValueTask<int> SendMessageAsync(LinuxFile file, byte[] data, List<LinuxFile>? fds, int flags)
    {
        return await SendMessageAsync(file, data, fds, flags, null);
    }

    public async ValueTask<int> SendMessageAsync(LinuxFile file, byte[] data, List<LinuxFile>? fds, int flags, UnixSocketInode? explicitPeer)
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
            return _socketType == SocketType.Dgram ? -(int)Errno.EDESTADDRREQ : -(int)Errno.ENOTCONN;

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

            await peer._writeWaitQueue.WaitAsync();

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
            while (_receiveQueue.TryDequeue(out var msg))
            {
                ReleaseQueuedFds(msg.Fds);
            }
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
                int toCopy = Math.Min(buffer.Length, _partialBuffer.Length - _partialOffset);
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
                int toCopy = Math.Min(buffer.Length, msg.Data.Length);
                new ReadOnlySpan<byte>(msg.Data, 0, toCopy).CopyTo(buffer.Slice(0, toCopy));
                
                if (_socketType == SocketType.Stream && toCopy < msg.Data.Length)
                {
                    _partialBuffer = msg.Data;
                    _partialOffset = toCopy;
                }

                if (msg.Fds != null)
                {
                    foreach (var f in msg.Fds) f.Close();
                }

                // Reset read wait queue if no more data
                if (_receiveQueue.Count == 0 && _partialBuffer == null)
                    _readWaitQueue.Reset();

                _queuedBytes -= msg.Data.Length;
                if (_socketType == SocketType.Stream && toCopy < msg.Data.Length)
                    _queuedBytes += (msg.Data.Length - toCopy); // partial still queued
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
                return _socketType == SocketType.Dgram ? -(int)Errno.EDESTADDRREQ : -(int)Errno.ENOTCONN;
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
}
