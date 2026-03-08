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
    private const long ReadyCacheTtlMs = 2;

    [ThreadStatic] private static Stack<SocketAsyncEventArgs>? ThreadProbeArgsPool;

    private readonly Queue<Socket> _acceptedProbeQueue = new();
    private short _readyCacheBits;
    private long _readyCacheExpireAtMs;

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
        if (TryProbeReady(events, out var revents))
            return revents;
        return PollEvents.POLLERR;
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

        lock (_acceptedProbeQueue)
        {
            while (_acceptedProbeQueue.Count > 0)
            {
                try
                {
                    _acceptedProbeQueue.Dequeue().Dispose();
                }
                catch
                {
                    // ignored
                }
            }
        }

        _readyCacheBits = 0;
        _readyCacheExpireAtMs = 0;

        base.Release();
    }

    public override bool RegisterWait(LinuxFile file, Action callback, short events)
    {
        return RegisterWaitHandle(file, callback, events) != null;
    }

    public override IDisposable? RegisterWaitHandle(LinuxFile file, Action callback, short events)
    {
        var scheduler = KernelScheduler.Current;
        if (scheduler == null) return null;
        Logger.LogTrace("Host socket RegisterWait ino={Ino} events=0x{Events:X}", Ino, events);

        List<IDisposable>? registrations = null;

        if ((events & PollEvents.POLLIN) != 0)
        {
            var reg = IsListeningSocket() ? ArmAcceptProbe(callback, scheduler) : ArmReadProbe(callback, scheduler);
            if (reg != null)
            {
                registrations ??= [];
                registrations.Add(reg);
            }
        }

        if ((events & PollEvents.POLLOUT) != 0)
        {
            var reg = ArmWriteProbe(callback, scheduler);
            if (reg != null)
            {
                registrations ??= [];
                registrations.Add(reg);
            }
        }

        if (registrations == null || registrations.Count == 0) return null;
        if (registrations.Count == 1) return registrations[0];
        return new CompositeRegistration(registrations);
    }

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

            if (registration == null)
            {
                if ((Poll(file, events) & events) != 0)
                    return true;
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

    private bool TryProbeReady(short events, out short revents)
    {
        revents = 0;
        try
        {
            const short terminal = PollEvents.POLLERR | PollEvents.POLLHUP | PollEvents.POLLNVAL;
            var now = Environment.TickCount64;
            var cached = GetCachedReadyBits(now);
            revents |= (short)(cached & (events | terminal));

            if ((events & PollEvents.POLLIN) != 0 && HasBufferedAcceptedSocket())
            {
                revents |= PollEvents.POLLIN;
                PromoteReadyCache(PollEvents.POLLIN);
            }

            if ((revents & (events | terminal)) != 0)
                return true;

            var needWrite = (events & PollEvents.POLLOUT) != 0;
            var canWrite = needWrite && NativeSocket.Poll(0, SelectMode.SelectWrite);

            var needRead = (events & PollEvents.POLLIN) != 0 || (NativeSocket.Connected && !canWrite);
            var canRead = needRead && NativeSocket.Poll(0, SelectMode.SelectRead);

            // Keep POLLERR visibility independent of requested mask.
            var hasError = NativeSocket.Poll(0, SelectMode.SelectError);

            if ((events & PollEvents.POLLIN) != 0 && canRead)
                revents |= PollEvents.POLLIN;
            if ((events & PollEvents.POLLOUT) != 0 && canWrite)
                revents |= PollEvents.POLLOUT;
            if (hasError)
                revents |= PollEvents.POLLERR;

            if (NativeSocket.Connected && canRead && !canWrite)
            {
                try
                {
                    if (NativeSocket.Available == 0)
                        revents |= PollEvents.POLLHUP;
                }
                catch (SocketException)
                {
                    revents |= PollEvents.POLLHUP;
                }
            }

            if (revents != 0)
                PromoteReadyCache(revents);
            else
                ClearExpiredReadyCache(now);

            return true;
        }
        catch (ObjectDisposedException)
        {
            revents |= PollEvents.POLLNVAL;
            PromoteReadyCache(PollEvents.POLLNVAL);
            return true;
        }
        catch
        {
            return false;
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
            var items = _items;
            _items = null;
            if (items == null) return;
            foreach (var item in items) item.Dispose();
        }
    }

    private sealed class AsyncProbeRegistration : IDisposable
    {
        private readonly HostSocketInode _inode;
        private readonly KernelScheduler _scheduler;
        private readonly Action _callback;
        private readonly SocketAsyncEventArgs _saea;
        private readonly bool _isAcceptProbe;
        private readonly Action _scheduledCompletion;
        private bool _disposed;
        private bool _completed;

        public AsyncProbeRegistration(HostSocketInode inode, KernelScheduler scheduler, Action callback,
            SocketAsyncEventArgs saea, bool isAcceptProbe)
        {
            _inode = inode;
            _scheduler = scheduler;
            _callback = callback;
            _saea = saea;
            _isAcceptProbe = isAcceptProbe;
            _scheduledCompletion = CompleteScheduled;
            _saea.UserToken = this;
            _saea.Completed += OnCompleted;
        }

        public void HandleCompletedSync()
        {
            CompleteOnSchedulerThread(_saea);
        }

        private void OnCompleted(object? sender, SocketAsyncEventArgs e)
        {
            if (e.UserToken is not AsyncProbeRegistration reg)
            {
                RecycleProbeArgs(e);
                return;
            }

            reg._scheduler.Schedule(reg._scheduledCompletion);
        }

        private void CompleteScheduled()
        {
            CompleteOnSchedulerThread(_saea);
        }

        private void CompleteOnSchedulerThread(SocketAsyncEventArgs e)
        {
            if (_completed) return;
            _completed = true;

            short readyHint = 0;
            if (_isAcceptProbe && e.AcceptSocket != null)
            {
                _inode.EnqueueAcceptedSocket(e.AcceptSocket);
                e.AcceptSocket = null;
                readyHint |= PollEvents.POLLIN;
            }
            else if (e.LastOperation == SocketAsyncOperation.Receive && e.SocketError == SocketError.Success)
            {
                readyHint |= PollEvents.POLLIN;
            }

            if (e.LastOperation == SocketAsyncOperation.Send && e.SocketError == SocketError.Success)
                readyHint |= PollEvents.POLLOUT;

            if (e.SocketError is not SocketError.Success and not SocketError.WouldBlock and not SocketError.IOPending and not SocketError.OperationAborted and not SocketError.Interrupted)
                readyHint |= PollEvents.POLLERR;

            if (readyHint != 0)
                _inode.PromoteReadyCache(readyHint);

            if (!_disposed && ShouldSignalRePoll(e))
                _callback();

            _saea.Completed -= OnCompleted;
            _saea.UserToken = null;
            if (_saea.AcceptSocket != null)
            {
                _saea.AcceptSocket.Dispose();
                _saea.AcceptSocket = null;
            }
            RecycleProbeArgs(_saea);
        }

        private static bool ShouldSignalRePoll(SocketAsyncEventArgs e)
        {
            return e.SocketError switch
            {
                SocketError.Success => true,
                SocketError.OperationAborted => false,
                SocketError.Interrupted => false,
                SocketError.WouldBlock => false,
                SocketError.IOPending => false,
                _ => true
            };
        }

        public void CancelAndRecycleImmediately()
        {
            if (_completed) return;
            _completed = true;
            _disposed = true;
            _saea.Completed -= OnCompleted;
            _saea.UserToken = null;
            if (_saea.AcceptSocket != null)
            {
                _saea.AcceptSocket.Dispose();
                _saea.AcceptSocket = null;
            }
            RecycleProbeArgs(_saea);
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }

    private bool IsListeningSocket()
    {
        return NativeSocket.SocketType == SocketType.Stream && NativeSocket.IsBound && !NativeSocket.Connected;
    }

    private IDisposable? ArmReadProbe(Action callback, KernelScheduler scheduler)
    {
        var saea = RentProbeArgs();
        saea.SetBuffer(null, 0, 0);
        saea.SocketFlags = SocketFlags.None;
        saea.AcceptSocket = null;
        var reg = new AsyncProbeRegistration(this, scheduler, callback, saea, isAcceptProbe: false);
        try
        {
            if (!NativeSocket.ReceiveAsync(saea))
            {
                reg.HandleCompletedSync();
                return null;
            }

            return reg;
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.WouldBlock or SocketError.IOPending)
        {
            reg.CancelAndRecycleImmediately();
            return null;
        }
        catch
        {
            reg.CancelAndRecycleImmediately();
            scheduler.Schedule(callback);
            return null;
        }
    }

    private IDisposable? ArmAcceptProbe(Action callback, KernelScheduler scheduler)
    {
        var saea = RentProbeArgs();
        saea.SetBuffer(null, 0, 0);
        saea.SocketFlags = SocketFlags.None;
        saea.AcceptSocket = null;
        var reg = new AsyncProbeRegistration(this, scheduler, callback, saea, isAcceptProbe: true);
        try
        {
            if (!NativeSocket.AcceptAsync(saea))
            {
                reg.HandleCompletedSync();
                return null;
            }

            return reg;
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.WouldBlock or SocketError.IOPending)
        {
            reg.CancelAndRecycleImmediately();
            return null;
        }
        catch
        {
            reg.CancelAndRecycleImmediately();
            scheduler.Schedule(callback);
            return null;
        }
    }

    private IDisposable? ArmWriteProbe(Action callback, KernelScheduler scheduler)
    {
        var saea = RentProbeArgs();
        saea.SetBuffer(null, 0, 0);
        saea.SocketFlags = SocketFlags.None;
        saea.AcceptSocket = null;
        var reg = new AsyncProbeRegistration(this, scheduler, callback, saea, isAcceptProbe: false);
        try
        {
            if (!NativeSocket.SendAsync(saea))
            {
                reg.HandleCompletedSync();
                return null;
            }

            return reg;
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.WouldBlock or SocketError.IOPending)
        {
            reg.CancelAndRecycleImmediately();
            return null;
        }
        catch
        {
            reg.CancelAndRecycleImmediately();
            scheduler.Schedule(callback);
            return null;
        }
    }

    private void EnqueueAcceptedSocket(Socket socket)
    {
        lock (_acceptedProbeQueue)
        {
            _acceptedProbeQueue.Enqueue(socket);
        }
    }

    private bool TryDequeueAcceptedSocket(out Socket socket)
    {
        lock (_acceptedProbeQueue)
        {
            if (_acceptedProbeQueue.Count > 0)
            {
                socket = _acceptedProbeQueue.Dequeue();
                return true;
            }
        }

        socket = null!;
        return false;
    }

    private bool HasBufferedAcceptedSocket()
    {
        lock (_acceptedProbeQueue)
            return _acceptedProbeQueue.Count > 0;
    }

    private short GetCachedReadyBits(long nowMs)
    {
        if (_readyCacheBits == 0) return 0;
        if (nowMs <= _readyCacheExpireAtMs) return _readyCacheBits;
        _readyCacheBits = 0;
        _readyCacheExpireAtMs = 0;
        return 0;
    }

    private void PromoteReadyCache(short bits)
    {
        _readyCacheBits |= bits;
        _readyCacheExpireAtMs = Environment.TickCount64 + ReadyCacheTtlMs;
    }

    private void ClearReadyBits(short bits)
    {
        _readyCacheBits = (short)(_readyCacheBits & ~bits);
        if (_readyCacheBits == 0)
            _readyCacheExpireAtMs = 0;
    }

    private void ClearExpiredReadyCache(long nowMs)
    {
        if (_readyCacheBits != 0 && nowMs > _readyCacheExpireAtMs)
        {
            _readyCacheBits = 0;
            _readyCacheExpireAtMs = 0;
        }
    }

    private static SocketAsyncEventArgs RentProbeArgs()
    {
        var pool = ThreadProbeArgsPool;
        if (pool != null && pool.Count > 0)
            return pool.Pop();
        return new SocketAsyncEventArgs();
    }

    private static void RecycleProbeArgs(SocketAsyncEventArgs saea)
    {
        saea.SetBuffer(null, 0, 0);
        saea.SocketFlags = SocketFlags.None;
        saea.AcceptSocket = null;
        var pool = ThreadProbeArgsPool ??= new Stack<SocketAsyncEventArgs>(16);
        if (pool.Count < 256)
            pool.Push(saea);
        else
            saea.Dispose();
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
    }

    public async ValueTask<int> SendAsync(LinuxFile file, ReadOnlyMemory<byte> buffer, int flags)
    {
        if (!MemoryMarshal.TryGetArray(buffer, out var segment)) segment = new ArraySegment<byte>(buffer.ToArray());
        Logger.LogTrace(
            "Host socket send enter ino={Ino} len={Len} flags=0x{Flags:X} fileFlags=0x{FileFlags:X} connected={Connected}",
            Ino, segment.Count, flags, (int)file.Flags, NativeSocket.Connected);

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
    }

    public async ValueTask<int> SendToAsync(LinuxFile file, ReadOnlyMemory<byte> buffer, int flags, EndPoint remoteEp)
    {
        if (!MemoryMarshal.TryGetArray(buffer, out var segment)) segment = new ArraySegment<byte>(buffer.ToArray());

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
                    Logger.LogDebug("Host socket connect in progress (ino={Ino}, flags={Flags:X})", Ino,
                        (int)file.Flags);
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
