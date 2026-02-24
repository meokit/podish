using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.Syscalls;

namespace Fiberish.VFS;

public class UnixMessage
{
    public byte[] Data { get; init; } = [];
    public List<LinuxFile> Fds { get; init; } = new();
}

public class UnixSocketInode : Inode
{
    private readonly object _lock = new();
    private readonly AsyncWaitQueue _readWaitQueue = new();
    private readonly AsyncWaitQueue _writeWaitQueue = new();
    
    // For AF_UNIX
    private readonly Queue<UnixMessage> _receiveQueue = new();
    private UnixSocketInode? _peer;
    private bool _shutDownRead;
    private bool _shutDownWrite;
    private readonly SocketType _socketType;

    // Buffer for stream mode partial reads
    private byte[]? _partialBuffer;
    private int _partialOffset;

    // Backpressure: track queued bytes to limit memory usage
    private const int MaxSendBuffer = 262144; // 256KB
    private int _queuedBytes;

    public UnixSocketInode(ulong ino, SuperBlock sb, SocketType type)
    {
        Ino = ino;
        SuperBlock = sb;
        Type = InodeType.Socket;
        Mode = 0x1ED;
        _socketType = type;
    }

    public void ConnectPair(UnixSocketInode peer)
    {
        _peer = peer;
    }

    public override short Poll(LinuxFile file, short events)
    {
        short revents = 0;
        lock (_lock)
        {
            if ((events & PollEvents.POLLIN) != 0)
            {
                if (_receiveQueue.Count > 0 || _partialBuffer != null || _shutDownRead)
                    revents |= PollEvents.POLLIN;
            }

            if ((events & PollEvents.POLLOUT) != 0)
            {
                if (_shutDownWrite || _peer == null || _peer._shutDownRead)
                    revents |= PollEvents.POLLERR;
                else if (_peer._queuedBytes < MaxSendBuffer)
                    revents |= PollEvents.POLLOUT;
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

    public void EnqueueMessage(UnixMessage msg)
    {
        lock (_lock)
        {
            if (_shutDownRead) return; // Dropped
            _receiveQueue.Enqueue(msg);
            _queuedBytes += msg.Data.Length;
        }
        _readWaitQueue.Set(); // Signal that data is available (like PipeInode)
    }

    public void PeerDisconnected()
    {
        lock (_lock)
        {
            _shutDownRead = true;
            _shutDownWrite = true;
        }
        _readWaitQueue.Signal();
        _writeWaitQueue.Signal();
    }

    public async ValueTask<(int BytesRead, List<LinuxFile>? Fds)> RecvMessageAsync(LinuxFile file, byte[] buffer, int flags)
    {
        while (true)
        {
            lock (_lock)
            {
                if (_partialBuffer != null)
                {
                    int toCopy = Math.Min(buffer.Length, _partialBuffer.Length - _partialOffset);
                    Array.Copy(_partialBuffer, _partialOffset, buffer, 0, toCopy);
                    _partialOffset += toCopy;
                    if (_partialOffset >= _partialBuffer.Length)
                    {
                        _partialBuffer = null;
                        _partialOffset = 0;
                    }
                    _queuedBytes -= toCopy;
                    _writeWaitQueue.Set(); // signal backpressure relief 
                    return (toCopy, null); // FDs only on boundary
                }

                if (_receiveQueue.TryDequeue(out var msg))
                {
                    int toCopy = Math.Min(buffer.Length, msg.Data.Length);
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

                    return (toCopy, msg.Fds);
                }

                if (_shutDownRead || _peer == null)
                    return (0, null); // EOF
            }
            _writeWaitQueue.Set(); // backpressure relief

            if ((file.Flags & FileFlags.O_NONBLOCK) != 0) return (-(int)Errno.EAGAIN, null);

            var task = KernelScheduler.Current?.CurrentTask;
            if (task != null && task.HasUnblockedPendingSignal()) return (-(int)Errno.ERESTARTSYS, null);

            await _readWaitQueue.WaitAsync();

            if (task != null && task.HasUnblockedPendingSignal()) return (-(int)Errno.ERESTARTSYS, null);
        }
    }

    public async ValueTask<int> SendMessageAsync(LinuxFile file, byte[] data, List<LinuxFile>? fds, int flags)
    {
        UnixSocketInode? peer;
        lock (_lock)
        {
            if (_shutDownWrite || _peer == null) return -(int)Errno.EPIPE;
            peer = _peer;
        }

        // Backpressure: check peer's queued bytes
        while (true)
        {
            lock (peer._lock)
            {
                if (peer._queuedBytes < MaxSendBuffer)
                    break; // Space available
            }

            if ((file.Flags & FileFlags.O_NONBLOCK) != 0)
                return -(int)Errno.EAGAIN;

            await peer._writeWaitQueue.WaitAsync();

            // Re-check connection after wakeup
            lock (_lock)
            {
                if (_shutDownWrite || _peer == null) return -(int)Errno.EPIPE;
            }
        }

        var clonedData = new byte[data.Length];
        Array.Copy(data, clonedData, data.Length);

        var msg = new UnixMessage { Data = clonedData };
        if (fds != null)
        {
            // Bump refcounts because they are in the queue now
            foreach (var f in fds) f.Dentry.Inode!.Get();
            msg.Fds.AddRange(fds);
        }

        peer.EnqueueMessage(msg);
        return data.Length;
    }

    protected override void Release()
    {
        _peer?.PeerDisconnected();
        _peer = null;
        
        // Clean up unread FDs
        lock (_lock)
        {
            while (_receiveQueue.TryDequeue(out var msg))
            {
                if (msg.Fds != null)
                {
                    foreach (var f in msg.Fds) f.Dentry.Inode!.Put();
                }
            }
        }
        
        base.Release();
    }

    public override int Read(LinuxFile file, Span<byte> buffer, long offset)
    {
        lock (_lock)
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
                    foreach (var f in msg.Fds) f.Dentry.Inode?.Put();
                }

                // Reset read wait queue if no more data
                if (_receiveQueue.Count == 0 && _partialBuffer == null)
                    _readWaitQueue.Reset();

                _queuedBytes -= msg.Data.Length;
                if (_socketType == SocketType.Stream && toCopy < msg.Data.Length)
                    _queuedBytes += (msg.Data.Length - toCopy); // partial still queued
                _writeWaitQueue.Set(); // backpressure relief

                return toCopy;
            }

            if (_shutDownRead || _peer == null)
                return 0; // EOF
        }

        return -(int)Errno.EAGAIN;
    }

    public override int Write(LinuxFile file, ReadOnlySpan<byte> buffer, long offset)
    {
        UnixSocketInode? peer;
        lock (_lock)
        {
            if (_shutDownWrite || _peer == null) return -(int)Errno.EPIPE;
            peer = _peer;
        }

        // Backpressure check for synchronous write
        lock (peer._lock)
        {
            if (peer._queuedBytes >= MaxSendBuffer)
                return -(int)Errno.EAGAIN;
        }

        var clonedData = buffer.ToArray();
        var msg = new UnixMessage { Data = clonedData };
        peer.EnqueueMessage(msg);
        return buffer.Length;
    }
}
