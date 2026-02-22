using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Fiberish.Core;
using Fiberish.Native;

using Fiberish.Syscalls;

namespace Fiberish.VFS;

public class HostSocketInode : Inode
{
    private readonly Socket _hostSocket;
    private readonly AsyncWaitQueue _readWaitQueue = new();
    private readonly AsyncWaitQueue _writeWaitQueue = new();
    private readonly object _lock = new();
    private bool _readWaiting;
    private bool _writeWaiting;

    // AF_INET = 2, AF_INET6 = 10 (Linux)
    // SOCK_STREAM = 1, SOCK_DGRAM = 2
    public HostSocketInode(ulong ino, SuperBlock sb, AddressFamily af, SocketType type, ProtocolType proto) 
    {
        Ino = ino;
        SuperBlock = sb;
        _hostSocket = new Socket(af, type, proto);
        _hostSocket.Blocking = false;
        Type = InodeType.Socket;
        Mode = 0x1ED; // 755
    }

    // Wrap an accepted socket
    public HostSocketInode(ulong ino, SuperBlock sb, Socket connectedSocket) 
    {
        Ino = ino;
        SuperBlock = sb;
        _hostSocket = connectedSocket;
        _hostSocket.Blocking = false;
        Type = InodeType.Socket;
        Mode = 0x1ED; // 755
    }

    public Socket NativeSocket => _hostSocket;

    public override short Poll(LinuxFile file, short events)
    {
        short revents = 0;
        
        try
        {
            if ((events & PollEvents.POLLIN) != 0 && _hostSocket.Poll(0, SelectMode.SelectRead))
                revents |= PollEvents.POLLIN;
            if ((events & PollEvents.POLLOUT) != 0 && _hostSocket.Poll(0, SelectMode.SelectWrite))
                revents |= PollEvents.POLLOUT;
            if ((events & PollEvents.POLLERR) != 0 && _hostSocket.Poll(0, SelectMode.SelectError))
                revents |= PollEvents.POLLERR;
        }
        catch (ObjectDisposedException) { revents |= PollEvents.POLLNVAL; }
        catch { revents |= PollEvents.POLLERR; }
        
        return revents;
    }

    protected override void Release()
    {
        _hostSocket.Dispose();
        _readWaitQueue.Signal();
        _writeWaitQueue.Signal();
        base.Release();
    }

    public override bool RegisterWait(LinuxFile file, Action callback, short events)
    {
        // For edge-triggered, Fire-and-forget 0-byte read/write async to bridge the Host thread pool to the Fiberish task queue
        bool registered = false;

        if ((events & PollEvents.POLLIN) != 0)
        {
            lock (_lock)
            {
                if (!_readWaiting)
                {
                    _readWaiting = true;
                    try
                    {
                        var tcs = new TaskCompletionSource();
                        var args = new SocketAsyncEventArgs();
                        args.SetBuffer(Array.Empty<byte>());
                        args.Completed += (s, e) =>
                        {
                            lock (_lock) { _readWaiting = false; }
                            _readWaitQueue.Signal();
                            callback();
                        };
                        
                        if (!_hostSocket.ReceiveAsync(args))
                        {
                            lock (_lock) { _readWaiting = false; }
                            _readWaitQueue.Signal();
                            KernelScheduler.Current?.Schedule(callback);
                        }
                    }
                    catch
                    {
                        lock (_lock) { _readWaiting = false; }
                        _readWaitQueue.Signal();
                        KernelScheduler.Current?.Schedule(callback);
                    }
                }
            }
            registered = true;
        }

        if ((events & PollEvents.POLLOUT) != 0)
        {
            lock (_lock)
            {
                if (!_writeWaiting)
                {
                    _writeWaiting = true;
                    try
                    {
                        var args = new SocketAsyncEventArgs();
                        args.SetBuffer(Array.Empty<byte>());
                        args.Completed += (s, e) =>
                        {
                            lock (_lock) { _writeWaiting = false; }
                            _writeWaitQueue.Signal();
                            callback();
                        };
                        
                        // SendAsync doesn't block if buffer is empty but checks writable state
                        if (!_hostSocket.SendAsync(args))
                        {
                            lock (_lock) { _writeWaiting = false; }
                            _writeWaitQueue.Signal();
                            KernelScheduler.Current?.Schedule(callback);
                        }
                    }
                    catch
                    {
                        lock (_lock) { _writeWaiting = false; }
                        _writeWaitQueue.Signal();
                        KernelScheduler.Current?.Schedule(callback);
                    }
                }
            }
            registered = true;
        }

        return registered;
    }

    public WaitQueueAwaiter WaitReadAsync()
    {
        return _readWaitQueue.WaitAsync();
    }

    public async ValueTask<int> RecvAsync(LinuxFile file, byte[] buffer, int flags)
    {
        while (true)
        {
            try
            {
                int bytes = _hostSocket.Receive(buffer, (SocketFlags)flags);
                return bytes;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock || ex.SocketErrorCode == SocketError.IOPending)
            {
                if ((file.Flags & FileFlags.O_NONBLOCK) != 0) return -(int)Errno.EAGAIN;

                // Ensure background polling is active
                RegisterWait(file, () => { }, PollEvents.POLLIN);

                var task = KernelScheduler.Current?.CurrentTask;
                if (task != null && task.HasUnblockedPendingSignal()) return -(int)Errno.ERESTARTSYS;

                await _readWaitQueue.WaitAsync();

                if (task != null && task.HasUnblockedPendingSignal()) return -(int)Errno.ERESTARTSYS;
            }
            catch (SocketException ex)
            {
                return MapSocketError(ex.SocketErrorCode);
            }
        }
    }

    public async ValueTask<int> SendAsync(LinuxFile file, ReadOnlyMemory<byte> buffer, int flags)
    {
        while (true)
        {
            try
            {
                // To avoid pinning and unsafe MemoryMarshal tricks if we don't have to,
                // we can just use ArraySegment if Memory happens to be array.
                // In our Syscall Handler, we typically allocate an array anyway.
                if (!System.Runtime.InteropServices.MemoryMarshal.TryGetArray(buffer, out var segment))
                {
                    segment = new ArraySegment<byte>(buffer.ToArray());
                }

                int bytes = _hostSocket.Send(segment.Array!, segment.Offset, segment.Count, (SocketFlags)flags);
                return bytes;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock || ex.SocketErrorCode == SocketError.IOPending)
            {
                if ((file.Flags & FileFlags.O_NONBLOCK) != 0) return -(int)Errno.EAGAIN;

                RegisterWait(file, () => { }, PollEvents.POLLOUT);

                var task = KernelScheduler.Current?.CurrentTask;
                if (task != null && task.HasUnblockedPendingSignal()) return -(int)Errno.ERESTARTSYS;

                await _writeWaitQueue.WaitAsync();

                if (task != null && task.HasUnblockedPendingSignal()) return -(int)Errno.ERESTARTSYS;
            }
            catch (SocketException ex)
            {
                return MapSocketError(ex.SocketErrorCode);
            }
        }
    }

    public override int Read(LinuxFile file, Span<byte> buffer, long offset)
    {
        try
        {
            var arr = buffer.ToArray();
            var bytes = _hostSocket.Receive(arr);
            arr.AsSpan(0, bytes).CopyTo(buffer);
            return bytes;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock || ex.SocketErrorCode == SocketError.IOPending)
        {
            return -(int)Errno.EAGAIN;
        }
        catch (SocketException ex)
        {
            return MapSocketError(ex.SocketErrorCode);
        }
    }

    public override int Write(LinuxFile file, ReadOnlySpan<byte> buffer, long offset)
    {
        try
        {
            return _hostSocket.Send(buffer.ToArray());
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock || ex.SocketErrorCode == SocketError.IOPending)
        {
            return -(int)Errno.EAGAIN;
        }
        catch (SocketException ex)
        {
            return MapSocketError(ex.SocketErrorCode);
        }
    }

    public int MapSocketError(SocketError err)
    {
        return err switch
        {
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
            _ => -(int)Errno.EIO
        };
    }
}
