using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Sockets;
using Fiberish.Syscalls;
using Microsoft.Extensions.Logging;

namespace Fiberish.VFS;

internal sealed class HostSocketProbeEngine : IDisposable
{
    private const long ReadyCacheTtlMs = 2;
    private static readonly ConcurrentBag<SocketAsyncEventArgs> ReadProbeArgsPool = new();
    private static readonly ConcurrentBag<SocketAsyncEventArgs> WriteProbeArgsPool = new();
    private static readonly ConcurrentBag<SocketAsyncEventArgs> AcceptProbeArgsPool = new();
    private static readonly EventHandler<SocketAsyncEventArgs> StaticProbeCompleted = OnProbeCompletedStatic;
    private readonly Queue<Socket> _acceptedProbeQueue = new();
    private readonly IReadyDispatcher _dispatcher;
    private readonly ILogger _logger;

    private readonly HostSocketInode _owner;
    private readonly List<WaiterRegistration> _readWaiters = [];
    private readonly Socket _socket;

    private readonly object _waitersLock = new();
    private readonly List<WaiterRegistration> _writeWaiters = [];
    private SocketAsyncEventArgs? _acceptProbeArgs;
    private int _acceptProbeInFlight;
    private int _disposed;
    private int _notifyDispatchScheduled;
    private int _readNotifyPending;

    private SocketAsyncEventArgs? _readProbeArgs;

    private int _readProbeInFlight;

    private short _readyCacheBits;
    private long _readyCacheExpireAtMs;
    private int _writeNotifyPending;
    private SocketAsyncEventArgs? _writeProbeArgs;
    private int _writeProbeInFlight;

    public HostSocketProbeEngine(HostSocketInode owner, Socket socket, ILogger logger, IReadyDispatcher dispatcher)
    {
        _owner = owner;
        _socket = socket;
        _logger = logger;
        _dispatcher = dispatcher;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        ReturnOrDisposeProbeArgs(ref _readProbeArgs, ReadProbeArgsPool, Volatile.Read(ref _readProbeInFlight) != 0);
        ReturnOrDisposeProbeArgs(ref _writeProbeArgs, WriteProbeArgsPool, Volatile.Read(ref _writeProbeInFlight) != 0);
        ReturnOrDisposeProbeArgs(ref _acceptProbeArgs, AcceptProbeArgsPool,
            Volatile.Read(ref _acceptProbeInFlight) != 0);

        try
        {
            _socket.Dispose();
        }
        catch
        {
            // ignored
        }

        lock (_acceptedProbeQueue)
        {
            while (_acceptedProbeQueue.Count > 0)
                try
                {
                    _acceptedProbeQueue.Dequeue().Dispose();
                }
                catch
                {
                    // ignored
                }
        }

        lock (_waitersLock)
        {
            _readWaiters.Clear();
            _writeWaiters.Clear();
        }

        _readyCacheBits = 0;
        _readyCacheExpireAtMs = 0;
    }

    public short Poll(short events)
    {
        if (TryProbeReady(events, out var revents))
            return revents;
        return PollEvents.POLLERR;
    }

    public IDisposable? RegisterWaitHandle(LinuxFile file, Action callback, short events)
    {
        if (!_dispatcher.CanDispatch) return null;
        _logger.LogTrace("Host socket RegisterWait ino={Ino} events=0x{Events:X}", _owner.Ino, events);

        IDisposable? regIn = null;
        IDisposable? regOut = null;

        if ((events & PollEvents.POLLIN) != 0)
        {
            regIn = RegisterWaiter(callback, PollEvents.POLLIN);
            if (IsListeningSocket())
                ArmAcceptProbe();
            else
                ArmReadProbe();
        }

        if ((events & PollEvents.POLLOUT) != 0)
        {
            regOut = RegisterWaiter(callback, PollEvents.POLLOUT);
            if (IsNonBlockingConnectPending(file))
                ArmConnectProbe();
            else
                ArmWriteProbe();
        }

        if (regIn == null) return regOut;
        if (regOut == null) return regIn;
        return new DualRegistration(regIn, regOut);
    }

    public void ClearReadyBits(short bits)
    {
        _readyCacheBits = (short)(_readyCacheBits & ~bits);
        if (_readyCacheBits == 0)
            _readyCacheExpireAtMs = 0;
    }

    public bool TryDequeueAcceptedSocket(out Socket socket)
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

    public bool HasBufferedAcceptedSocket()
    {
        lock (_acceptedProbeQueue)
        {
            return _acceptedProbeQueue.Count > 0;
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

            // Prefer runtime async readiness (epoll/kqueue under the hood) over synchronous probing.
            MaybeArmProbesForPoll(events);

            var needWrite = (events & PollEvents.POLLOUT) != 0;
            var canWrite = false;
            if (needWrite)
                try
                {
                    canWrite = _socket.Poll(0, SelectMode.SelectWrite);
                }
                catch (SocketException)
                {
                    // keep false; error path below handles fault state.
                }

            var canRead = false;
            if ((events & PollEvents.POLLIN) != 0)
                if (!IsListeningSocket())
                    try
                    {
                        canRead = _socket.Available > 0;
                    }
                    catch (SocketException)
                    {
                        // keep false
                    }

            var hasError = false;
            try
            {
                hasError = _socket.Poll(0, SelectMode.SelectError);
            }
            catch (SocketException)
            {
                hasError = true;
            }

            if ((events & PollEvents.POLLOUT) != 0 &&
                _socket.SocketType == SocketType.Stream &&
                !_socket.Connected &&
                (canWrite || hasError))
                try
                {
                    var soObj = _socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error);
                    var soError = soObj switch
                    {
                        int i => (SocketError)i,
                        SocketError se => se,
                        _ => SocketError.SocketError
                    };
                    if (soError == SocketError.Success)
                    {
                        canWrite = true;
                        hasError = false;
                    }
                    else if (soError != SocketError.WouldBlock && soError != SocketError.InProgress &&
                             soError != SocketError.IOPending)
                    {
                        hasError = true;
                        canWrite = false;
                        _owner.CachePendingSocketError(soError);
                    }
                    else
                    {
                        hasError = false;
                    }
                }
                catch
                {
                    // fall back to poll bits
                }

            if ((events & PollEvents.POLLIN) != 0 && canRead)
                revents |= PollEvents.POLLIN;
            if ((events & PollEvents.POLLOUT) != 0 && canWrite)
                revents |= PollEvents.POLLOUT;
            if (hasError)
                revents |= PollEvents.POLLERR;

            if (_socket.Connected && !canRead && !canWrite)
                try
                {
                    if (_socket.Poll(0, SelectMode.SelectRead) && _socket.Available == 0)
                        revents |= PollEvents.POLLHUP;
                }
                catch (SocketException)
                {
                    revents |= PollEvents.POLLHUP;
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

    private void MaybeArmProbesForPoll(short events)
    {
        if (!_dispatcher.CanDispatch || Volatile.Read(ref _disposed) != 0)
            return;

        if ((events & PollEvents.POLLIN) != 0)
        {
            if (IsListeningSocket())
                ArmAcceptProbe();
            else
                ArmReadProbe();
        }

        if ((events & PollEvents.POLLOUT) != 0)
        {
            if (_socket.SocketType == SocketType.Stream && !_socket.Connected)
                ArmConnectProbe();
            else
                ArmWriteProbe();
        }
    }

    private bool IsListeningSocket()
    {
        return _socket.SocketType == SocketType.Stream && _socket.IsBound && !_socket.Connected;
    }

    private bool IsNonBlockingConnectPending(LinuxFile file)
    {
        return _socket.SocketType == SocketType.Stream &&
               !_socket.Connected &&
               (file.Flags & FileFlags.O_NONBLOCK) != 0;
    }

    private SocketAsyncEventArgs EnsureProbeArgs(ProbeKind kind)
    {
        return kind switch
        {
            ProbeKind.Read => _readProbeArgs ??= RentProbeArgs(ReadProbeArgsPool, kind),
            ProbeKind.Write => _writeProbeArgs ??= RentProbeArgs(WriteProbeArgsPool, kind),
            _ => _acceptProbeArgs ??= RentProbeArgs(AcceptProbeArgsPool, kind)
        };
    }

    private void ArmReadProbe()
    {
        if (Interlocked.Exchange(ref _readProbeInFlight, 1) != 0)
            return;

        var args = EnsureProbeArgs(ProbeKind.Read);
        if (args.UserToken is ProbeTag readTag) readTag.Owner = this;
        try
        {
            if (!_socket.ReceiveAsync(args))
                HandleProbeCompleted(args, ProbeKind.Read);
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.WouldBlock or SocketError.IOPending)
        {
            Interlocked.Exchange(ref _readProbeInFlight, 0);
        }
        catch
        {
            Interlocked.Exchange(ref _readProbeInFlight, 0);
            NotifyWaiters(PollEvents.POLLERR, true, false);
        }
    }

    private void ArmWriteProbe()
    {
        if (Interlocked.Exchange(ref _writeProbeInFlight, 1) != 0)
            return;

        var args = EnsureProbeArgs(ProbeKind.Write);
        if (args.UserToken is ProbeTag writeTag) writeTag.Owner = this;
        try
        {
            if (!_socket.SendAsync(args))
                HandleProbeCompleted(args, ProbeKind.Write);
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.WouldBlock or SocketError.IOPending)
        {
            Interlocked.Exchange(ref _writeProbeInFlight, 0);
        }
        catch
        {
            Interlocked.Exchange(ref _writeProbeInFlight, 0);
            NotifyWaiters(PollEvents.POLLERR, false, true);
        }
    }

    private void ArmConnectProbe()
    {
        if (Interlocked.Exchange(ref _writeProbeInFlight, 1) != 0)
            return;

        var args = EnsureProbeArgs(ProbeKind.Write);
        if (args.UserToken is ProbeTag connectTag) connectTag.Owner = this;
        try
        {
            if (!_socket.ConnectAsync(args))
                HandleProbeCompleted(args, ProbeKind.Write);
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.WouldBlock or SocketError.IOPending
                                             or SocketError.InProgress or SocketError.AlreadyInProgress)
        {
            Interlocked.Exchange(ref _writeProbeInFlight, 0);
        }
        catch
        {
            Interlocked.Exchange(ref _writeProbeInFlight, 0);
            NotifyWaiters(PollEvents.POLLERR, false, true);
        }
    }

    private void ArmAcceptProbe()
    {
        if (Interlocked.Exchange(ref _acceptProbeInFlight, 1) != 0)
            return;

        var args = EnsureProbeArgs(ProbeKind.Accept);
        if (args.UserToken is ProbeTag acceptTag) acceptTag.Owner = this;
        try
        {
            if (!_socket.AcceptAsync(args))
                HandleProbeCompleted(args, ProbeKind.Accept);
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.WouldBlock or SocketError.IOPending)
        {
            Interlocked.Exchange(ref _acceptProbeInFlight, 0);
        }
        catch
        {
            Interlocked.Exchange(ref _acceptProbeInFlight, 0);
            NotifyWaiters(PollEvents.POLLERR, true, false);
        }
    }

    private static void OnProbeCompletedStatic(object? sender, SocketAsyncEventArgs e)
    {
        if (e.UserToken is not ProbeTag tag || tag.Owner == null)
            return;
        var owner = tag.Owner;
        owner._dispatcher.Post(() => owner.HandleProbeCompleted(e, tag.Kind));
    }

    private void HandleProbeCompleted(SocketAsyncEventArgs e, ProbeKind kind)
    {
        switch (kind)
        {
            case ProbeKind.Read:
                Interlocked.Exchange(ref _readProbeInFlight, 0);
                break;
            case ProbeKind.Write:
                Interlocked.Exchange(ref _writeProbeInFlight, 0);
                break;
            case ProbeKind.Accept:
                Interlocked.Exchange(ref _acceptProbeInFlight, 0);
                break;
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            if (kind == ProbeKind.Accept && e.AcceptSocket != null)
            {
                try
                {
                    e.AcceptSocket.Dispose();
                }
                catch
                {
                    // ignored
                }

                e.AcceptSocket = null;
            }

            return;
        }

        short readyHint = 0;
        if (kind == ProbeKind.Accept && e.AcceptSocket != null)
        {
            lock (_acceptedProbeQueue)
            {
                _acceptedProbeQueue.Enqueue(e.AcceptSocket);
            }

            e.AcceptSocket = null;
            readyHint |= PollEvents.POLLIN;
        }
        else if (kind == ProbeKind.Read && e.SocketError == SocketError.Success)
        {
            readyHint |= PollEvents.POLLIN;
        }

        if (kind == ProbeKind.Write)
        {
            if (e.LastOperation == SocketAsyncOperation.Send && e.SocketError == SocketError.Success)
                readyHint |= PollEvents.POLLOUT;
            if (e.LastOperation == SocketAsyncOperation.Connect)
                readyHint |= PollEvents.POLLOUT | PollEvents.POLLERR;
        }

        if (e.SocketError is not SocketError.Success and not SocketError.WouldBlock and not SocketError.IOPending
            and not SocketError.OperationAborted and not SocketError.Interrupted)
            readyHint |= PollEvents.POLLERR;

        if (readyHint != 0)
            PromoteReadyCache(readyHint);

        if (!ShouldSignalRePoll(e))
            return;

        NotifyWaiters(readyHint,
            kind is ProbeKind.Read or ProbeKind.Accept,
            kind == ProbeKind.Write);
    }

    private void NotifyWaiters(short readyHint, bool notifyRead, bool notifyWrite)
    {
        if (readyHint == 0)
            return;

        if (notifyRead)
            Volatile.Write(ref _readNotifyPending, 1);
        if (notifyWrite)
            Volatile.Write(ref _writeNotifyPending, 1);

        if (Interlocked.Exchange(ref _notifyDispatchScheduled, 1) == 0)
            _dispatcher.Post(FlushPendingWaiterNotifications);
    }

    private void FlushPendingWaiterNotifications()
    {
        try
        {
            while (true)
            {
                var doRead = Interlocked.Exchange(ref _readNotifyPending, 0) != 0;
                var doWrite = Interlocked.Exchange(ref _writeNotifyPending, 0) != 0;
                if (!doRead && !doWrite)
                    break;

                WaiterRegistration[]? readArr = null;
                WaiterRegistration[]? writeArr = null;
                var readCount = 0;
                var writeCount = 0;

                try
                {
                    lock (_waitersLock)
                    {
                        if (doRead && _readWaiters.Count > 0)
                        {
                            readCount = _readWaiters.Count;
                            readArr = ArrayPool<WaiterRegistration>.Shared.Rent(readCount);
                            _readWaiters.CopyTo(readArr, 0);
                        }

                        if (doWrite && _writeWaiters.Count > 0)
                        {
                            writeCount = _writeWaiters.Count;
                            writeArr = ArrayPool<WaiterRegistration>.Shared.Rent(writeCount);
                            _writeWaiters.CopyTo(writeArr, 0);
                        }
                    }

                    for (var i = 0; i < readCount; i++)
                        readArr![i].TryInvoke();
                    for (var i = 0; i < writeCount; i++)
                        writeArr![i].TryInvoke();
                }
                finally
                {
                    if (readArr != null)
                    {
                        Array.Clear(readArr, 0, readCount);
                        ArrayPool<WaiterRegistration>.Shared.Return(readArr);
                    }

                    if (writeArr != null)
                    {
                        Array.Clear(writeArr, 0, writeCount);
                        ArrayPool<WaiterRegistration>.Shared.Return(writeArr);
                    }
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _notifyDispatchScheduled, 0);
            if (Volatile.Read(ref _readNotifyPending) != 0 || Volatile.Read(ref _writeNotifyPending) != 0)
                if (Interlocked.Exchange(ref _notifyDispatchScheduled, 1) == 0)
                    _dispatcher.Post(FlushPendingWaiterNotifications);
        }
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

    private IDisposable RegisterWaiter(Action callback, short eventMask)
    {
        var reg = new WaiterRegistration(this, callback, eventMask);
        lock (_waitersLock)
        {
            if ((eventMask & PollEvents.POLLIN) != 0)
                _readWaiters.Add(reg);
            if ((eventMask & PollEvents.POLLOUT) != 0)
                _writeWaiters.Add(reg);
        }

        return reg;
    }

    private void UnregisterWaiter(WaiterRegistration reg)
    {
        lock (_waitersLock)
        {
            if ((reg.EventMask & PollEvents.POLLIN) != 0)
                _readWaiters.Remove(reg);
            if ((reg.EventMask & PollEvents.POLLOUT) != 0)
                _writeWaiters.Remove(reg);
        }
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

    private void ClearExpiredReadyCache(long nowMs)
    {
        if (_readyCacheBits != 0 && nowMs > _readyCacheExpireAtMs)
        {
            _readyCacheBits = 0;
            _readyCacheExpireAtMs = 0;
        }
    }

    private static SocketAsyncEventArgs RentProbeArgs(ConcurrentBag<SocketAsyncEventArgs> pool, ProbeKind kind)
    {
        SocketAsyncEventArgs saea;
        if (!pool.TryTake(out saea!))
        {
            saea = new SocketAsyncEventArgs();
            saea.Completed += StaticProbeCompleted;
        }

        saea.SetBuffer(null, 0, 0);
        saea.SocketFlags = SocketFlags.None;
        if (saea.AcceptSocket != null)
        {
            saea.AcceptSocket.Dispose();
            saea.AcceptSocket = null;
        }

        var tag = saea.UserToken as ProbeTag ?? new ProbeTag();
        tag.Kind = kind;
        tag.Owner = null;
        saea.UserToken = tag;
        return saea;
    }

    private static void ReturnOrDisposeProbeArgs(ref SocketAsyncEventArgs? args,
        ConcurrentBag<SocketAsyncEventArgs> pool,
        bool mayStillBeInFlight)
    {
        var saea = args;
        args = null;
        if (saea == null)
            return;

        if (saea.UserToken is ProbeTag tag)
            tag.Owner = null;

        if (mayStillBeInFlight)
            // Do not dispose while I/O may still be in flight.
            // The owning socket teardown will abort operation and runtime will release references on completion.
            return;

        saea.SetBuffer(null, 0, 0);
        saea.SocketFlags = SocketFlags.None;
        if (saea.AcceptSocket != null)
        {
            saea.AcceptSocket.Dispose();
            saea.AcceptSocket = null;
        }

        pool.Add(saea);
    }

    private sealed class WaiterRegistration : IDisposable
    {
        private readonly Action _callback;
        private int _disposed;
        private HostSocketProbeEngine? _owner;

        public WaiterRegistration(HostSocketProbeEngine owner, Action callback, short eventMask)
        {
            _owner = owner;
            _callback = callback;
            EventMask = eventMask;
        }

        public short EventMask { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.UnregisterWaiter(this);
        }

        public void TryInvoke()
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            _callback();
        }
    }

    private sealed class DualRegistration : IDisposable
    {
        private IDisposable? _first;
        private IDisposable? _second;

        public DualRegistration(IDisposable first, IDisposable second)
        {
            _first = first;
            _second = second;
        }

        public void Dispose()
        {
            var first = _first;
            var second = _second;
            _first = null;
            _second = null;
            first?.Dispose();
            second?.Dispose();
        }
    }

    private sealed class ProbeTag
    {
        public HostSocketProbeEngine? Owner { get; set; }
        public ProbeKind Kind { get; set; }
    }

    private enum ProbeKind
    {
        Read,
        Write,
        Accept
    }
}