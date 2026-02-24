using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Fiberish.Core;
using Fiberish.Diagnostics;
using Fiberish.Native;
using Fiberish.Syscalls;
using Microsoft.Extensions.Logging;

namespace Fiberish.VFS;

public sealed class HostSocketInode : Inode
{
    private static readonly ILogger Logger = Logging.CreateLogger<HostSocketInode>();
    private readonly SaeaAwaitable _readSaea;
    private readonly SaeaAwaitable _writeSaea;

    // AF_INET = 2, AF_INET6 = 10 (Linux)
    // SOCK_STREAM = 1, SOCK_DGRAM = 2
    public HostSocketInode(ulong ino, SuperBlock sb, AddressFamily af, SocketType type, ProtocolType proto)
    {
        Ino = ino;
        SuperBlock = sb;
        NativeSocket = new Socket(af, type, proto);
        NativeSocket.Blocking = false;
        Type = InodeType.Socket;
        Mode = 0x1ED; // 755

        _readSaea = SaeaPool.Rent();
        _writeSaea = SaeaPool.Rent();
    }

    // Wrap an accepted socket
    public HostSocketInode(ulong ino, SuperBlock sb, Socket connectedSocket)
    {
        Ino = ino;
        SuperBlock = sb;
        NativeSocket = connectedSocket;
        NativeSocket.Blocking = false;
        Type = InodeType.Socket;
        Mode = 0x1ED; // 755

        _readSaea = SaeaPool.Rent();
        _writeSaea = SaeaPool.Rent();
    }

    public Socket NativeSocket { get; }

    public override int Ioctl(LinuxFile linuxFile, uint request, uint arg, Engine engine)
    {
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
                return -(int)Errno.ENOTTY;
        }
    }

    public override short Poll(LinuxFile file, short events)
    {
        short revents = 0;
        try
        {
            var canRead = NativeSocket.Poll(0, SelectMode.SelectRead);
            var canWrite = NativeSocket.Poll(0, SelectMode.SelectWrite);
            var hasError = NativeSocket.Poll(0, SelectMode.SelectError);

            if ((events & PollEvents.POLLIN) != 0 && canRead)
                revents |= PollEvents.POLLIN;
            if ((events & PollEvents.POLLOUT) != 0 && canWrite)
                revents |= PollEvents.POLLOUT;

            // Linux poll semantics: POLLERR/POLLHUP are reported regardless of requested events.
            if (hasError)
                revents |= PollEvents.POLLERR;

            if (NativeSocket.Connected && canRead && !canWrite)
                try
                {
                    if (NativeSocket.Available == 0) revents |= PollEvents.POLLHUP;
                }
                catch (SocketException)
                {
                    revents |= PollEvents.POLLHUP;
                }
        }
        catch (ObjectDisposedException)
        {
            revents |= PollEvents.POLLNVAL;
        }
        catch
        {
            revents |= PollEvents.POLLERR;
        }

        return revents;
    }

    protected override void Release()
    {
        try
        {
            NativeSocket.Dispose();
        }
        catch
        {
            // ignored
        }

        SaeaPool.Return(_readSaea);
        SaeaPool.Return(_writeSaea);
        base.Release();
    }

    public override bool RegisterWait(LinuxFile file, Action callback, short events)
    {
        var registered = false;
        var scheduler = KernelScheduler.Current;
        if (scheduler == null) return false;
        Logger.LogTrace("Host socket RegisterWait ino={Ino} events=0x{Events:X}", Ino, events);

        if ((events & PollEvents.POLLIN) != 0)
        {
            var saea = new SocketAsyncEventArgs();
            // Use 1-byte + PEEK instead of 0-byte receive; 0-byte completion is unreliable on some hosts.
            var probe = new byte[1];
            saea.SetBuffer(probe, 0, 1);
            saea.SocketFlags = SocketFlags.Peek;
            saea.UserToken = new RegisterWaitToken(callback, scheduler, probe);
            saea.Completed += OnRegisterWaitCompleted;

            try
            {
                if (!NativeSocket.ReceiveAsync(saea))
                {
                    Logger.LogTrace("Host socket RegisterWait POLLIN completed synchronously ino={Ino}", Ino);
                    scheduler.Schedule(callback);
                    saea.Dispose();
                }
                else
                {
                    Logger.LogTrace("Host socket RegisterWait POLLIN armed async ino={Ino}", Ino);
                    registered = true;
                }
            }
            catch (Exception ex)
            {
                Logger.LogTrace(ex, "Host socket RegisterWait POLLIN failed ino={Ino}, scheduling callback", Ino);
                scheduler.Schedule(callback);
                saea.Dispose();
            }
        }

        if ((events & PollEvents.POLLOUT) != 0)
        {
            var saea = new SocketAsyncEventArgs();
            saea.SetBuffer(Array.Empty<byte>());
            saea.UserToken = new RegisterWaitToken(callback, scheduler, Array.Empty<byte>());
            saea.Completed += OnRegisterWaitCompleted;

            try
            {
                if (!NativeSocket.SendAsync(saea))
                {
                    Logger.LogTrace("Host socket RegisterWait POLLOUT completed synchronously ino={Ino}", Ino);
                    scheduler.Schedule(callback);
                    saea.Dispose();
                }
                else
                {
                    Logger.LogTrace("Host socket RegisterWait POLLOUT armed async ino={Ino}", Ino);
                    registered = true;
                }
            }
            catch (Exception ex)
            {
                Logger.LogTrace(ex, "Host socket RegisterWait POLLOUT failed ino={Ino}, scheduling callback", Ino);
                scheduler.Schedule(callback);
                saea.Dispose();
            }
        }

        return registered;
    }

    private void OnRegisterWaitCompleted(object? sender, SocketAsyncEventArgs e)
    {
        Logger.LogTrace(
            "Host socket RegisterWait completed ino={Ino} bytes={Bytes} error={Error} flags=0x{Flags:X}",
            Ino, e.BytesTransferred, e.SocketError, (int)e.SocketFlags);
        if (e.UserToken is RegisterWaitToken token) token.Scheduler.Schedule(token.Callback);
        TokenProbeClear(e.UserToken);
        e.Dispose();
    }

    private static void TokenProbeClear(object? tokenObj)
    {
        if (tokenObj is RegisterWaitToken token) token.ProbeBuffer[0] = 0;
    }

    // --- Async Operations using SAEA ---

    public async ValueTask<int> RecvAsync(LinuxFile file, byte[] buffer, int flags)
    {
        Logger.LogTrace(
            "Host socket recv enter ino={Ino} len={Len} flags=0x{Flags:X} fileFlags=0x{FileFlags:X} connected={Connected}",
            Ino, buffer.Length, flags, (int)file.Flags, NativeSocket.Connected);
        if ((file.Flags & FileFlags.O_NONBLOCK) != 0)
            try
            {
                var n = NativeSocket.Receive(buffer, 0, buffer.Length, (SocketFlags)flags);
                Logger.LogTrace("Host socket recv nonblock done ino={Ino} bytes={Bytes}", Ino, n);
                return n;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock ||
                                             ex.SocketErrorCode == SocketError.IOPending)
            {
                Logger.LogDebug("Host socket recv would block (ino={Ino}, flags={Flags:X})", Ino, (int)file.Flags);
                return -(int)Errno.EAGAIN;
            }
            catch (SocketException ex)
            {
                return MapSocketError(ex.SocketErrorCode);
            }

        _readSaea.ResetState();
        _readSaea.SetBuffer(buffer, 0, buffer.Length);
        _readSaea.SocketFlags = (SocketFlags)flags;

        Logger.LogTrace("Host socket recv async start ino={Ino} len={Len}", Ino, buffer.Length);
        if (!NativeSocket.ReceiveAsync(_readSaea))
        {
            if (_readSaea.SocketError != SocketError.Success)
                return MapSocketError(_readSaea.SocketError);
            Logger.LogTrace("Host socket recv completed sync-from-async ino={Ino} bytes={Bytes}", Ino,
                _readSaea.BytesTransferred);
            return _readSaea.BytesTransferred;
        }

        await _readSaea;

        var task = KernelScheduler.Current?.CurrentTask;
        if (task != null && task.HasUnblockedPendingSignal())
            return -(int)Errno.ERESTARTSYS;

        if (_readSaea.SocketError != SocketError.Success)
            return MapSocketError(_readSaea.SocketError);

        Logger.LogTrace("Host socket recv async done ino={Ino} bytes={Bytes}", Ino, _readSaea.BytesTransferred);
        return _readSaea.BytesTransferred;
    }

    public async ValueTask<(int Bytes, EndPoint? RemoteEp)> RecvFromAsync(LinuxFile file, byte[] buffer, int flags,
        EndPoint remoteEpTemplate)
    {
        if ((file.Flags & FileFlags.O_NONBLOCK) != 0)
            try
            {
                var remoteEp = remoteEpTemplate;
                var n = NativeSocket.ReceiveFrom(buffer, 0, buffer.Length, (SocketFlags)flags, ref remoteEp);
                return (n, remoteEp);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock ||
                                             ex.SocketErrorCode == SocketError.IOPending)
            {
                Logger.LogDebug("Host socket recvfrom would block (ino={Ino}, flags={Flags:X})", Ino, (int)file.Flags);
                return (-(int)Errno.EAGAIN, null);
            }
            catch (SocketException ex)
            {
                return (MapSocketError(ex.SocketErrorCode), null);
            }

        _readSaea.ResetState();
        _readSaea.SetBuffer(buffer, 0, buffer.Length);
        _readSaea.SocketFlags = (SocketFlags)flags;
        _readSaea.RemoteEndPoint = remoteEpTemplate;

        if (!NativeSocket.ReceiveFromAsync(_readSaea))
        {
            if (_readSaea.SocketError != SocketError.Success)
                return (MapSocketError(_readSaea.SocketError), null);
            return (_readSaea.BytesTransferred, _readSaea.RemoteEndPoint);
        }

        await _readSaea;

        var task = KernelScheduler.Current?.CurrentTask;
        if (task != null && task.HasUnblockedPendingSignal())
            return (-(int)Errno.ERESTARTSYS, null);

        if (_readSaea.SocketError != SocketError.Success)
            return (MapSocketError(_readSaea.SocketError), null);

        return (_readSaea.BytesTransferred, _readSaea.RemoteEndPoint);
    }

    public async ValueTask<int> SendAsync(LinuxFile file, ReadOnlyMemory<byte> buffer, int flags)
    {
        if (!MemoryMarshal.TryGetArray(buffer, out var segment)) segment = new ArraySegment<byte>(buffer.ToArray());
        Logger.LogTrace(
            "Host socket send enter ino={Ino} len={Len} flags=0x{Flags:X} fileFlags=0x{FileFlags:X} connected={Connected}",
            Ino, segment.Count, flags, (int)file.Flags, NativeSocket.Connected);

        if ((file.Flags & FileFlags.O_NONBLOCK) != 0)
            try
            {
                var n = NativeSocket.Send(segment.Array!, segment.Offset, segment.Count, (SocketFlags)flags);
                Logger.LogTrace("Host socket send nonblock done ino={Ino} bytes={Bytes}", Ino, n);
                return n;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock ||
                                             ex.SocketErrorCode == SocketError.IOPending)
            {
                Logger.LogDebug("Host socket send would block (ino={Ino}, flags={Flags:X})", Ino, (int)file.Flags);
                return -(int)Errno.EAGAIN;
            }
            catch (SocketException ex)
            {
                return MapSocketError(ex.SocketErrorCode);
            }

        _writeSaea.ResetState();
        _writeSaea.SetBuffer(segment.Array, segment.Offset, segment.Count);
        _writeSaea.SocketFlags = (SocketFlags)flags;

        Logger.LogTrace("Host socket send async start ino={Ino} len={Len}", Ino, segment.Count);
        if (!NativeSocket.SendAsync(_writeSaea))
        {
            if (_writeSaea.SocketError != SocketError.Success)
                return MapSocketError(_writeSaea.SocketError);
            Logger.LogTrace("Host socket send completed sync-from-async ino={Ino} bytes={Bytes}", Ino,
                _writeSaea.BytesTransferred);
            return _writeSaea.BytesTransferred;
        }

        await _writeSaea;

        var task = KernelScheduler.Current?.CurrentTask;
        if (task != null && task.HasUnblockedPendingSignal())
            return -(int)Errno.ERESTARTSYS;

        if (_writeSaea.SocketError != SocketError.Success)
            return MapSocketError(_writeSaea.SocketError);

        Logger.LogTrace("Host socket send async done ino={Ino} bytes={Bytes}", Ino, _writeSaea.BytesTransferred);
        return _writeSaea.BytesTransferred;
    }

    public async ValueTask<int> SendToAsync(LinuxFile file, ReadOnlyMemory<byte> buffer, int flags, EndPoint remoteEp)
    {
        if (!MemoryMarshal.TryGetArray(buffer, out var segment)) segment = new ArraySegment<byte>(buffer.ToArray());

        if ((file.Flags & FileFlags.O_NONBLOCK) != 0)
            try
            {
                return NativeSocket.SendTo(segment.Array!, segment.Offset, segment.Count, (SocketFlags)flags, remoteEp);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock ||
                                             ex.SocketErrorCode == SocketError.IOPending)
            {
                Logger.LogDebug("Host socket sendto would block (ino={Ino}, flags={Flags:X})", Ino, (int)file.Flags);
                return -(int)Errno.EAGAIN;
            }
            catch (SocketException ex)
            {
                return MapSocketError(ex.SocketErrorCode);
            }

        _writeSaea.ResetState();
        _writeSaea.SetBuffer(segment.Array, segment.Offset, segment.Count);
        _writeSaea.SocketFlags = (SocketFlags)flags;
        _writeSaea.RemoteEndPoint = remoteEp;

        if (!NativeSocket.SendToAsync(_writeSaea))
        {
            if (_writeSaea.SocketError != SocketError.Success)
                return MapSocketError(_writeSaea.SocketError);
            return _writeSaea.BytesTransferred;
        }

        await _writeSaea;

        var task = KernelScheduler.Current?.CurrentTask;
        if (task != null && task.HasUnblockedPendingSignal())
            return -(int)Errno.ERESTARTSYS;

        if (_writeSaea.SocketError != SocketError.Success)
            return MapSocketError(_writeSaea.SocketError);

        return _writeSaea.BytesTransferred;
    }

    public async ValueTask<int> ConnectAsync(LinuxFile file, EndPoint endpoint)
    {
        if ((file.Flags & FileFlags.O_NONBLOCK) != 0)
            try
            {
                NativeSocket.Connect(endpoint);
                return 0;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock ||
                                             ex.SocketErrorCode == SocketError.IOPending ||
                                             ex.SocketErrorCode == SocketError.InProgress ||
                                             ex.SocketErrorCode == SocketError.AlreadyInProgress)
            {
                Logger.LogDebug("Host socket connect in progress (ino={Ino}, flags={Flags:X})", Ino, (int)file.Flags);
                return -(int)Errno.EINPROGRESS;
            }
            catch (SocketException ex)
            {
                return MapSocketError(ex.SocketErrorCode);
            }

        _writeSaea.ResetState();
        _writeSaea.RemoteEndPoint = endpoint;

        if (!NativeSocket.ConnectAsync(_writeSaea))
        {
            if (_writeSaea.SocketError != SocketError.Success)
                return MapSocketError(_writeSaea.SocketError);
            return 0;
        }

        await _writeSaea;

        var task = KernelScheduler.Current?.CurrentTask;
        if (task != null && task.HasUnblockedPendingSignal())
            return -(int)Errno.EINTR;

        if (_writeSaea.SocketError != SocketError.Success)
            return MapSocketError(_writeSaea.SocketError);

        return 0;
    }

    public async ValueTask<Socket> AcceptAsync(LinuxFile file, int flags)
    {
        if ((file.Flags & FileFlags.O_NONBLOCK) != 0)
            try
            {
                return NativeSocket.Accept();
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock ||
                                             ex.SocketErrorCode == SocketError.IOPending)
            {
                throw new SocketException((int)SocketError.WouldBlock);
            }

        _readSaea.ResetState();
        _readSaea.AcceptSocket = null;

        if (!NativeSocket.AcceptAsync(_readSaea))
        {
            if (_readSaea.SocketError != SocketError.Success)
                throw new SocketException((int)_readSaea.SocketError);
            return _readSaea.AcceptSocket!;
        }

        await _readSaea;

        var task = KernelScheduler.Current?.CurrentTask;
        if (task != null && task.HasUnblockedPendingSignal())
            throw new SocketException((int)SocketError.Interrupted);

        if (_readSaea.SocketError != SocketError.Success)
            throw new SocketException((int)_readSaea.SocketError);

        return _readSaea.AcceptSocket!;
    }

    public override int Read(LinuxFile file, Span<byte> buffer, long offset)
    {
        try
        {
            var arr = buffer.ToArray();
            var bytes = NativeSocket.Receive(arr);
            TraceIo("read", arr.AsSpan(0, bytes), bytes);
            arr.AsSpan(0, bytes).CopyTo(buffer);
            return bytes;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock ||
                                         ex.SocketErrorCode == SocketError.IOPending)
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
            var data = buffer.ToArray();
            var bytes = NativeSocket.Send(data);
            TraceIo("write", data.AsSpan(0, Math.Min(bytes, data.Length)), bytes);
            return bytes;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock ||
                                         ex.SocketErrorCode == SocketError.IOPending)
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
            SocketError.Interrupted => -(int)Errno.EINTR,
            SocketError.InvalidArgument => -(int)Errno.EINVAL,
            SocketError.MessageSize => -(int)Errno.EMSGSIZE,
            SocketError.ProtocolNotSupported => -(int)Errno.EPROTONOSUPPORT,
            SocketError.SocketNotSupported => -(int)Errno.ESOCKTNOSUPPORT,
            _ => -(int)Errno.EIO
        };
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
        var sb = new StringBuilder(data.Length * 3);
        for (var i = 0; i < data.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(data[i].ToString("X2"));
        }

        return sb.ToString();
    }

    private record RegisterWaitToken(Action Callback, KernelScheduler Scheduler, byte[] ProbeBuffer);
}