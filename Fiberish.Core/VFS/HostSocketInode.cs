using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Fiberish.Core;
using Fiberish.Core.Net;
using Fiberish.Diagnostics;
using Fiberish.Native;
using Fiberish.Syscalls;
using Microsoft.Extensions.Logging;

namespace Fiberish.VFS;

public sealed class HostSocketInode : Inode
{
    private static readonly ILogger Logger = Logging.CreateLogger<HostSocketInode>();
    [ThreadStatic] private static StringBuilder? CachedHexBuilder;
    private readonly HostSocketReadiness _readiness;
    private int _cachedSocketError;

    // AF_INET = 2, AF_INET6 = 10 (Linux)
    // SOCK_STREAM = 1, SOCK_DGRAM = 2
    public HostSocketInode(ulong ino, SuperBlock sb, AddressFamily af, SocketType type, ProtocolType proto,
        SocketType? linuxSocketType = null)
    {
        Ino = ino;
        SuperBlock = sb;
        NativeSocket = new Socket(af, type, proto);
        LinuxSocketType = linuxSocketType ?? type;
        NativeSocket.Blocking = false;
        Type = InodeType.Socket;
        Mode = 0x1ED; // 755
        _readiness = new HostSocketReadiness(this, NativeSocket, Logger, new SchedulerReadyDispatcher(KernelScheduler.Current));
    }

    // Wrap an accepted socket
    public HostSocketInode(ulong ino, SuperBlock sb, Socket connectedSocket)
    {
        Ino = ino;
        SuperBlock = sb;
        NativeSocket = connectedSocket;
        LinuxSocketType = connectedSocket.SocketType;
        NativeSocket.Blocking = false;
        Type = InodeType.Socket;
        Mode = 0x1ED; // 755
        _readiness = new HostSocketReadiness(this, NativeSocket, Logger, new SchedulerReadyDispatcher(KernelScheduler.Current));
    }

    public Socket NativeSocket { get; }
    public SocketType LinuxSocketType { get; }
    public AddressFamily HostAddressFamily => NativeSocket.AddressFamily;
    public ProtocolType HostProtocolType => NativeSocket.ProtocolType;
    public SocketType HostSocketType => NativeSocket.SocketType;

    public override short Poll(LinuxFile file, short events) => _readiness.Poll(file, events);

    public override bool RegisterWait(LinuxFile file, Action callback, short events) =>
        _readiness.RegisterWait(file, callback, events);

    public override IDisposable? RegisterWaitHandle(LinuxFile file, Action callback, short events) =>
        _readiness.RegisterWaitHandle(file, callback, events);

    protected override void Release()
    {
        _readiness.Dispose();
        base.Release();
    }

    private ValueTask<bool> WaitForSocketEventAsync(LinuxFile file, short events) =>
        _readiness.WaitForSocketEventAsync(file, events);

    private void ClearReadyBits(short bits) => _readiness.ClearReadyBits(bits);

    private bool TryDequeueAcceptedSocket(out Socket socket) => _readiness.TryDequeueAcceptedSocket(out socket);

    private bool HasBufferedAcceptedSocket() => _readiness.HasBufferedAcceptedSocket();

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
                return NetDeviceIoctlHelper.Handle(engine, request, arg);
        }
    }

    public async ValueTask<int> RecvAsync(LinuxFile file, byte[] buffer, int flags, int maxBytes = -1)
    {
        var recvLen = maxBytes > 0 ? Math.Min(maxBytes, buffer.Length) : buffer.Length;
        if (recvLen <= 0) return 0;
        Logger.LogTrace(
            "Host socket recv enter ino={Ino} len={Len} flags=0x{Flags:X} fileFlags=0x{FileFlags:X} connected={Connected}",
            Ino, recvLen, flags, (int)file.Flags, NativeSocket.Connected);

        while (true)
            try
            {
                var n = NativeSocket.Receive(buffer, 0, recvLen, (SocketFlags)flags);
                if (n > 0)
                    ClearReadyBits(PollEvents.POLLIN);
                Logger.LogTrace("Host socket recv done ino={Ino} bytes={Bytes}", Ino, n);
                return n;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock ||
                                             ex.SocketErrorCode == SocketError.IOPending)
            {
                ClearReadyBits(PollEvents.POLLIN);
                if ((file.Flags & FileFlags.O_NONBLOCK) != 0)
                {
                    Logger.LogDebug("Host socket recv would block (ino={Ino}, flags={Flags:X})", Ino, (int)file.Flags);
                    return -(int)Errno.EAGAIN;
                }

                var ready = await WaitForSocketEventAsync(file, PollEvents.POLLIN);
                if (!ready)
                    return -(int)Errno.ERESTARTSYS;
            }
            catch (SocketException ex)
            {
                return MapSocketError(ex.SocketErrorCode);
            }
            catch (ObjectDisposedException)
            {
                return -(int)Errno.ENOTCONN;
            }
    }

    public async ValueTask<(int Bytes, EndPoint? RemoteEp)> RecvFromAsync(LinuxFile file, byte[] buffer, int flags,
        EndPoint remoteEpTemplate)
    {
        while (true)
            try
            {
                var remoteEp = remoteEpTemplate;
                var n = NativeSocket.ReceiveFrom(buffer, 0, buffer.Length, (SocketFlags)flags, ref remoteEp);
                if (n > 0)
                    ClearReadyBits(PollEvents.POLLIN);
                return (n, remoteEp);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock ||
                                             ex.SocketErrorCode == SocketError.IOPending)
            {
                ClearReadyBits(PollEvents.POLLIN);
                if ((file.Flags & FileFlags.O_NONBLOCK) != 0)
                {
                    Logger.LogDebug("Host socket recvfrom would block (ino={Ino}, flags={Flags:X})", Ino,
                        (int)file.Flags);
                    return (-(int)Errno.EAGAIN, null);
                }

                var ready = await WaitForSocketEventAsync(file, PollEvents.POLLIN);
                if (!ready)
                    return (-(int)Errno.ERESTARTSYS, null);
            }
            catch (SocketException ex)
            {
                return (MapSocketError(ex.SocketErrorCode), null);
            }
            catch (ObjectDisposedException)
            {
                return (-(int)Errno.ENOTCONN, null);
            }
    }

    public async ValueTask<int> SendAsync(LinuxFile file, ReadOnlyMemory<byte> buffer, int flags)
    {
        byte[]? rented = null;
        ArraySegment<byte> segment;
        if (!MemoryMarshal.TryGetArray(buffer, out segment))
        {
            rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
            buffer.Span.CopyTo(rented);
            segment = new ArraySegment<byte>(rented, 0, buffer.Length);
        }
        Logger.LogTrace(
            "Host socket send enter ino={Ino} len={Len} flags=0x{Flags:X} fileFlags=0x{FileFlags:X} connected={Connected}",
            Ino, segment.Count, flags, (int)file.Flags, NativeSocket.Connected);

        try
        {
            while (true)
                try
                {
                    var n = NativeSocket.Send(segment.Array!, segment.Offset, segment.Count, (SocketFlags)flags);
                    if (n > 0)
                        ClearReadyBits(PollEvents.POLLOUT);
                    Logger.LogTrace("Host socket send done ino={Ino} bytes={Bytes}", Ino, n);
                    return n;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock ||
                                                 ex.SocketErrorCode == SocketError.IOPending)
                {
                    ClearReadyBits(PollEvents.POLLOUT);
                    if ((file.Flags & FileFlags.O_NONBLOCK) != 0)
                    {
                        Logger.LogDebug("Host socket send would block (ino={Ino}, flags={Flags:X})", Ino, (int)file.Flags);
                        return -(int)Errno.EAGAIN;
                    }

                    var ready = await WaitForSocketEventAsync(file, PollEvents.POLLOUT);
                    if (!ready)
                        return -(int)Errno.ERESTARTSYS;
                }
                catch (SocketException ex)
                {
                    return MapSocketError(ex.SocketErrorCode);
                }
                catch (ObjectDisposedException)
                {
                    return -(int)Errno.ENOTCONN;
                }
        }
        finally
        {
            if (rented != null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public async ValueTask<int> SendToAsync(LinuxFile file, ReadOnlyMemory<byte> buffer, int flags, EndPoint remoteEp)
    {
        byte[]? rented = null;
        ArraySegment<byte> segment;
        if (!MemoryMarshal.TryGetArray(buffer, out segment))
        {
            rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
            buffer.Span.CopyTo(rented);
            segment = new ArraySegment<byte>(rented, 0, buffer.Length);
        }

        try
        {
            while (true)
                try
                {
                    var n = NativeSocket.SendTo(segment.Array!, segment.Offset, segment.Count, (SocketFlags)flags, remoteEp);
                    if (n > 0)
                        ClearReadyBits(PollEvents.POLLOUT);
                    return n;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock ||
                                                 ex.SocketErrorCode == SocketError.IOPending)
                {
                    ClearReadyBits(PollEvents.POLLOUT);
                    if ((file.Flags & FileFlags.O_NONBLOCK) != 0)
                    {
                        Logger.LogDebug("Host socket sendto would block (ino={Ino}, flags={Flags:X})", Ino,
                            (int)file.Flags);
                        return -(int)Errno.EAGAIN;
                    }

                    var ready = await WaitForSocketEventAsync(file, PollEvents.POLLOUT);
                    if (!ready)
                        return -(int)Errno.ERESTARTSYS;
                }
                catch (SocketException ex)
                {
                    return MapSocketError(ex.SocketErrorCode);
                }
                catch (ObjectDisposedException)
                {
                    return -(int)Errno.ENOTCONN;
                }
        }
        finally
        {
            if (rented != null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public async ValueTask<int> ConnectAsync(LinuxFile file, EndPoint endpoint)
    {
        while (true)
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
                if ((file.Flags & FileFlags.O_NONBLOCK) != 0)
                {
                    Logger.LogDebug("Host socket connect in progress (ino={Ino}, flags={Flags:X}, endpoint={Endpoint})", Ino,
                        (int)file.Flags, endpoint);
                    return -(int)Errno.EINPROGRESS;
                }

                var ready = await WaitForSocketEventAsync(file, PollEvents.POLLOUT);
                if (!ready)
                    return -(int)Errno.ERESTARTSYS;

                var so = NativeSocket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error);
                if (so is not int soInt)
                    return -(int)Errno.EIO;
                var err = (SocketError)soInt;
                if (err == SocketError.Success || err == SocketError.IsConnected)
                    return 0;
                if (err is SocketError.WouldBlock or SocketError.IOPending or SocketError.InProgress or SocketError.AlreadyInProgress)
                    continue;
                return MapSocketError(err);
            }
            catch (SocketException ex)
            {
                return MapSocketError(ex.SocketErrorCode);
            }
            catch (ObjectDisposedException)
            {
                return -(int)Errno.ENOTCONN;
            }
    }

    public async ValueTask<Socket> AcceptAsync(LinuxFile file, int flags)
    {
        if (TryDequeueAcceptedSocket(out var queued))
        {
            if (!HasBufferedAcceptedSocket())
                ClearReadyBits(PollEvents.POLLIN);
            return queued;
        }

        while (true)
            try
            {
                var accepted = NativeSocket.Accept();
                if (!HasBufferedAcceptedSocket())
                    ClearReadyBits(PollEvents.POLLIN);
                return accepted;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock ||
                                             ex.SocketErrorCode == SocketError.IOPending)
            {
                if ((file.Flags & FileFlags.O_NONBLOCK) != 0)
                    throw new SocketException((int)SocketError.WouldBlock);

                var ready = await WaitForSocketEventAsync(file, PollEvents.POLLIN);
                if (!ready)
                    throw new SocketException((int)SocketError.Interrupted);
            }
            catch (ObjectDisposedException)
            {
                throw new SocketException((int)SocketError.NotConnected);
            }
    }

    public override int Read(LinuxFile file, Span<byte> buffer, long offset)
    {
        var rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            var bytes = NativeSocket.Receive(rented, 0, buffer.Length, SocketFlags.None);
            TraceIo("read", rented.AsSpan(0, bytes), bytes);
            rented.AsSpan(0, bytes).CopyTo(buffer);
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
        catch (ObjectDisposedException)
        {
            return -(int)Errno.ENOTCONN;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public override int Write(LinuxFile file, ReadOnlySpan<byte> buffer, long offset)
    {
        var rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            buffer.CopyTo(rented);
            var bytes = NativeSocket.Send(rented, 0, buffer.Length, SocketFlags.None);
            TraceIo("write", rented.AsSpan(0, Math.Min(bytes, buffer.Length)), bytes);
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
        catch (ObjectDisposedException)
        {
            return -(int)Errno.ENOTCONN;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
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

    internal void CachePendingSocketError(SocketError err)
    {
        if (err is SocketError.Success or SocketError.WouldBlock or SocketError.IOPending or SocketError.InProgress or SocketError.AlreadyInProgress)
            return;

        var mapped = MapSocketError(err);
        var linuxErr = mapped < 0 ? -mapped : (int)Errno.EIO;
        Interlocked.Exchange(ref _cachedSocketError, linuxErr);
    }

    internal int ConsumeCachedSocketError()
    {
        return Interlocked.Exchange(ref _cachedSocketError, 0);
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
        var sb = AcquireHexBuilder(data.Length * 3);
        try
        {
            for (var i = 0; i < data.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(data[i].ToString("X2"));
            }

            return sb.ToString();
        }
        finally
        {
            ReleaseHexBuilder(sb);
        }
    }

    private static StringBuilder AcquireHexBuilder(int capacity)
    {
        var cached = CachedHexBuilder;
        if (cached != null)
        {
            CachedHexBuilder = null;
            cached.Clear();
            if (cached.Capacity < capacity)
                cached.EnsureCapacity(capacity);
            return cached;
        }

        return new StringBuilder(capacity);
    }

    private static void ReleaseHexBuilder(StringBuilder sb)
    {
        const int maxCachedCapacity = 1024;
        if (sb.Capacity > maxCachedCapacity)
            return;
        sb.Clear();
        CachedHexBuilder = sb;
    }
}
