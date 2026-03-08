using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
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

    }

    public Socket NativeSocket { get; }
    public SocketType LinuxSocketType { get; }
    public AddressFamily HostAddressFamily => NativeSocket.AddressFamily;
    public ProtocolType HostProtocolType => NativeSocket.ProtocolType;
    public SocketType HostSocketType => NativeSocket.SocketType;

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

        base.Release();
    }

    public override bool RegisterWait(LinuxFile file, Action callback, short events)
    {
        return RegisterWaitHandle(file, callback, events) != null;
    }

    public override IDisposable? RegisterWaitHandle(LinuxFile file, Action callback, short events)
    {
        var registered = false;
        var scheduler = KernelScheduler.Current;
        if (scheduler == null) return null;
        Logger.LogTrace("Host socket RegisterWait ino={Ino} events=0x{Events:X}", Ino, events);
        List<IDisposable>? registrations = null;

        if ((events & PollEvents.POLLIN) != 0)
        {
            var reg = ArmReadWait(callback, scheduler);
            if (reg != null)
            {
                registrations ??= [];
                registrations.Add(reg);
                registered = true;
            }
        }

        if ((events & PollEvents.POLLOUT) != 0)
        {
            var reg = ArmWriteWait(callback, scheduler);
            if (reg != null)
            {
                registrations ??= [];
                registrations.Add(reg);
                registered = true;
            }
        }

        if (!registered) return null;
        if (registrations == null || registrations.Count == 0) return null;
        if (registrations.Count == 1) return registrations[0];
        return new CompositeRegistration(registrations);
    }

    private void OnRegisterWaitCompleted(object? sender, SocketAsyncEventArgs e)
    {
        Logger.LogTrace(
            "Host socket RegisterWait completed ino={Ino} bytes={Bytes} error={Error} flags=0x{Flags:X}",
            Ino, e.BytesTransferred, e.SocketError, (int)e.SocketFlags);
        if (e.UserToken is RegisterWaitToken token && token.TryComplete())
            token.Scheduler.Schedule(token.Callback);
        if (e.UserToken is RegisterWaitToken token2) token2.DetachSaea();
        e.UserToken = null;
        e.Completed -= OnRegisterWaitCompleted;
        e.Dispose();
    }

    private IDisposable? ArmReadWait(Action callback, KernelScheduler scheduler)
    {
        var saea = new SocketAsyncEventArgs();
        saea.SetBuffer(Array.Empty<byte>());
        saea.SocketFlags = SocketFlags.None;
        var token = new RegisterWaitToken(callback, scheduler, saea);
        saea.UserToken = token;
        saea.Completed += OnRegisterWaitCompleted;

        try
        {
            if (!NativeSocket.ReceiveAsync(saea))
            {
                Logger.LogTrace("Host socket RegisterWait POLLIN completed synchronously ino={Ino}", Ino);
                token.TryComplete();
                scheduler.Schedule(callback);
                saea.Completed -= OnRegisterWaitCompleted;
                saea.Dispose();
                return null;
            }

            Logger.LogTrace("Host socket RegisterWait POLLIN armed async ino={Ino}", Ino);
            return token;
        }
        catch (Exception ex)
        {
            Logger.LogTrace(ex, "Host socket RegisterWait POLLIN failed ino={Ino}, scheduling callback", Ino);
            token.TryComplete();
            scheduler.Schedule(callback);
            saea.Completed -= OnRegisterWaitCompleted;
            saea.Dispose();
            return null;
        }
    }

    private IDisposable? ArmWriteWait(Action callback, KernelScheduler scheduler)
    {
        var saea = new SocketAsyncEventArgs();
        saea.SetBuffer(Array.Empty<byte>());
        var token = new RegisterWaitToken(callback, scheduler, saea);
        saea.UserToken = token;
        saea.Completed += OnRegisterWaitCompleted;

        try
        {
            if (!NativeSocket.SendAsync(saea))
            {
                Logger.LogTrace("Host socket RegisterWait POLLOUT completed synchronously ino={Ino}", Ino);
                token.TryComplete();
                scheduler.Schedule(callback);
                saea.Completed -= OnRegisterWaitCompleted;
                saea.Dispose();
                return null;
            }

            Logger.LogTrace("Host socket RegisterWait POLLOUT armed async ino={Ino}", Ino);
            return token;
        }
        catch (Exception ex)
        {
            Logger.LogTrace(ex, "Host socket RegisterWait POLLOUT failed ino={Ino}, scheduling callback", Ino);
            token.TryComplete();
            scheduler.Schedule(callback);
            saea.Completed -= OnRegisterWaitCompleted;
            saea.Dispose();
            return null;
        }
    }

    private sealed class CompositeRegistration : IDisposable
    {
        private List<IDisposable>? _items;

        public CompositeRegistration(List<IDisposable> items)
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

    private sealed class RegisterWaitToken : IDisposable
    {
        private int _state; // 0=pending, 1=completed, 2=canceled
        private SocketAsyncEventArgs? _saea;

        public RegisterWaitToken(Action callback, KernelScheduler scheduler, SocketAsyncEventArgs saea)
        {
            Callback = callback;
            Scheduler = scheduler;
            _saea = saea;
        }

        public Action Callback { get; }
        public KernelScheduler Scheduler { get; }

        public bool TryComplete()
        {
            return Interlocked.CompareExchange(ref _state, 1, 0) == 0;
        }

        public void DetachSaea()
        {
            Interlocked.Exchange(ref _saea, null);
        }

        public void Dispose()
        {
            // Cancellation is logical: suppress callback delivery.
            // We let in-flight SAEA finish and be disposed by completion path.
            if (Interlocked.CompareExchange(ref _state, 2, 0) != 0) return;
            var saea = Interlocked.Exchange(ref _saea, null);
            if (saea != null) saea.UserToken = null;
        }
    }

    // --- Async Operations ---

    private async ValueTask<bool> WaitForSocketEventAsync(LinuxFile file, short events)
    {
        while (true)
        {
            if ((Poll(file, events) & events) != 0)
                return true;

            var task = KernelScheduler.Current?.CurrentTask;
            if (task != null && task.HasUnblockedPendingSignal())
                return false;

            var waitQueue = new AsyncWaitQueue();
            using var registration = RegisterWaitHandle(file, waitQueue.Signal, events);

            if ((Poll(file, events) & events) != 0)
                return true;

            if (registration == null)
            {
                var spin = await new SleepAwaitable(1);
                if (spin == AwaitResult.Interrupted)
                    return false;
                continue;
            }

            var result = await waitQueue.WaitAsync();
            if (result == AwaitResult.Interrupted)
                return false;
        }
    }

    public async ValueTask<int> RecvAsync(LinuxFile file, byte[] buffer, int flags, int maxBytes = -1)
    {
        var recvLen = maxBytes > 0 ? Math.Min(maxBytes, buffer.Length) : buffer.Length;
        if (recvLen <= 0) return 0;
        Logger.LogTrace(
            "Host socket recv enter ino={Ino} len={Len} flags=0x{Flags:X} fileFlags=0x{FileFlags:X} connected={Connected}",
            Ino, recvLen, flags, (int)file.Flags, NativeSocket!.Connected);

        while (true)
            try
            {
                var n = NativeSocket.Receive(buffer, 0, recvLen, (SocketFlags)flags);
                Logger.LogTrace("Host socket recv done ino={Ino} bytes={Bytes}", Ino, n);
                return n;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock ||
                                             ex.SocketErrorCode == SocketError.IOPending)
            {
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
    }

    public async ValueTask<(int Bytes, EndPoint? RemoteEp)> RecvFromAsync(LinuxFile file, byte[] buffer, int flags,
        EndPoint remoteEpTemplate)
    {
        while (true)
            try
            {
                var remoteEp = remoteEpTemplate;
                var n = NativeSocket.ReceiveFrom(buffer, 0, buffer.Length, (SocketFlags)flags, ref remoteEp);
                return (n, remoteEp);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock ||
                                             ex.SocketErrorCode == SocketError.IOPending)
            {
                if ((file.Flags & FileFlags.O_NONBLOCK) != 0)
                {
                    Logger.LogDebug("Host socket recvfrom would block (ino={Ino}, flags={Flags:X})", Ino, (int)file.Flags);
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
    }

    public async ValueTask<int> SendAsync(LinuxFile file, ReadOnlyMemory<byte> buffer, int flags)
    {
        if (!MemoryMarshal.TryGetArray(buffer, out var segment)) segment = new ArraySegment<byte>(buffer.ToArray());
        Logger.LogTrace(
            "Host socket send enter ino={Ino} len={Len} flags=0x{Flags:X} fileFlags=0x{FileFlags:X} connected={Connected}",
            Ino, segment.Count, flags, (int)file.Flags, NativeSocket!.Connected);

        while (true)
            try
            {
                var n = NativeSocket.Send(segment.Array!, segment.Offset, segment.Count, (SocketFlags)flags);
                Logger.LogTrace("Host socket send done ino={Ino} bytes={Bytes}", Ino, n);
                return n;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock ||
                                             ex.SocketErrorCode == SocketError.IOPending)
            {
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
    }

    public async ValueTask<int> SendToAsync(LinuxFile file, ReadOnlyMemory<byte> buffer, int flags, EndPoint remoteEp)
    {
        if (!MemoryMarshal.TryGetArray(buffer, out var segment)) segment = new ArraySegment<byte>(buffer.ToArray());

        while (true)
            try
            {
                return NativeSocket.SendTo(segment.Array!, segment.Offset, segment.Count, (SocketFlags)flags, remoteEp);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock ||
                                             ex.SocketErrorCode == SocketError.IOPending)
            {
                if ((file.Flags & FileFlags.O_NONBLOCK) != 0)
                {
                    Logger.LogDebug("Host socket sendto would block (ino={Ino}, flags={Flags:X})", Ino, (int)file.Flags);
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
                    Logger.LogDebug("Host socket connect in progress (ino={Ino}, flags={Flags:X})", Ino, (int)file.Flags);
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
    }

    public async ValueTask<Socket> AcceptAsync(LinuxFile file, int flags)
    {
        while (true)
            try
            {
                return NativeSocket.Accept();
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

}
