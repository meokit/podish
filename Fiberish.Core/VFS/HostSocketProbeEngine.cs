using System.Net.Sockets;
using Fiberish.Syscalls;
using Microsoft.Extensions.Logging;

namespace Fiberish.VFS;

internal sealed class HostSocketProbeEngine : IDisposable
{
    private const long ReadyCacheTtlMs = 2;

    [ThreadStatic] private static Stack<SocketAsyncEventArgs>? ThreadProbeArgsPool;
    [ThreadStatic] private static Stack<AsyncProbeRegistration>? ThreadProbeRegistrationPool;

    private readonly HostSocketInode _owner;
    private readonly Socket _socket;
    private readonly ILogger _logger;
    private readonly IReadyDispatcher _dispatcher;
    private readonly Queue<Socket> _acceptedProbeQueue = new();
    private short _readyCacheBits;
    private long _readyCacheExpireAtMs;

    public HostSocketProbeEngine(HostSocketInode owner, Socket socket, ILogger logger, IReadyDispatcher dispatcher)
    {
        _owner = owner;
        _socket = socket;
        _logger = logger;
        _dispatcher = dispatcher;
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
            regIn = IsListeningSocket() ? ArmAcceptProbe(callback) : ArmReadProbe(callback);
        }

        if ((events & PollEvents.POLLOUT) != 0)
        {
            regOut = IsNonBlockingConnectPending(file)
                ? ArmConnectProbe(callback)
                : ArmWriteProbe(callback);
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
            return _acceptedProbeQueue.Count > 0;
    }

    public void Dispose()
    {
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
            var canWrite = needWrite && _socket.Poll(0, SelectMode.SelectWrite);

            var canRead = false;
            if ((events & PollEvents.POLLIN) != 0)
            {
                if (!IsListeningSocket())
                {
                    try
                    {
                        canRead = _socket.Available > 0;
                    }
                    catch (SocketException)
                    {
                        // leave canRead false; error/hup path below will report state.
                    }
                }
            }

            var hasError = _socket.Poll(0, SelectMode.SelectError);
            if ((events & PollEvents.POLLOUT) != 0 &&
                _socket.SocketType == SocketType.Stream &&
                !_socket.Connected &&
                (canWrite || hasError))
            {
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
                    }
                    else
                    {
                        hasError = false;
                    }
                }
                catch
                {
                    // Fall back to poll bits if SO_ERROR isn't available.
                }
            }

            if ((events & PollEvents.POLLIN) != 0 && canRead)
                revents |= PollEvents.POLLIN;
            if ((events & PollEvents.POLLOUT) != 0 && canWrite)
                revents |= PollEvents.POLLOUT;
            if (hasError)
                revents |= PollEvents.POLLERR;

            if (_socket.Connected && !canRead && !canWrite)
            {
                try
                {
                    if (_socket.Poll(0, SelectMode.SelectRead) && _socket.Available == 0)
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

    private IDisposable? ArmReadProbe(Action callback)
    {
        var saea = RentProbeArgs();
        saea.SetBuffer(null, 0, 0);
        saea.SocketFlags = SocketFlags.None;
        saea.AcceptSocket = null;
        var reg = RentProbeRegistration(this, _dispatcher, callback, saea, isAcceptProbe: false);
        try
        {
            if (!_socket.ReceiveAsync(saea))
            {
                reg.HandleCompletedSync();
                _dispatcher.Post(callback);
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
            _dispatcher.Post(callback);
            return null;
        }
    }

    private IDisposable? ArmAcceptProbe(Action callback)
    {
        var saea = RentProbeArgs();
        saea.SetBuffer(null, 0, 0);
        saea.SocketFlags = SocketFlags.None;
        saea.AcceptSocket = null;
        var reg = RentProbeRegistration(this, _dispatcher, callback, saea, isAcceptProbe: true);
        try
        {
            if (!_socket.AcceptAsync(saea))
            {
                reg.HandleCompletedSync();
                _dispatcher.Post(callback);
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
            _dispatcher.Post(callback);
            return null;
        }
    }

    private IDisposable? ArmWriteProbe(Action callback)
    {
        var saea = RentProbeArgs();
        saea.SetBuffer(null, 0, 0);
        saea.SocketFlags = SocketFlags.None;
        saea.AcceptSocket = null;
        var reg = RentProbeRegistration(this, _dispatcher, callback, saea, isAcceptProbe: false);
        try
        {
            if (!_socket.SendAsync(saea))
            {
                reg.HandleCompletedSync();
                _dispatcher.Post(callback);
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
            _dispatcher.Post(callback);
            return null;
        }
    }

    private IDisposable? ArmConnectProbe(Action callback)
    {
        var saea = RentProbeArgs();
        saea.SetBuffer(null, 0, 0);
        saea.SocketFlags = SocketFlags.None;
        saea.AcceptSocket = null;
        var reg = RentProbeRegistration(this, _dispatcher, callback, saea, isAcceptProbe: false);
        try
        {
            if (!_socket.ConnectAsync(saea))
            {
                reg.HandleCompletedSync();
                _dispatcher.Post(callback);
                return null;
            }

            return reg;
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.WouldBlock or SocketError.IOPending or SocketError.InProgress or SocketError.AlreadyInProgress)
        {
            reg.CancelAndRecycleImmediately();
            return null;
        }
        catch
        {
            reg.CancelAndRecycleImmediately();
            _dispatcher.Post(callback);
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

    private static AsyncProbeRegistration RentProbeRegistration(HostSocketProbeEngine probe, IReadyDispatcher dispatcher,
        Action callback, SocketAsyncEventArgs saea, bool isAcceptProbe)
    {
        var pool = ThreadProbeRegistrationPool;
        AsyncProbeRegistration reg;
        if (pool != null && pool.Count > 0)
            reg = pool.Pop();
        else
            reg = new AsyncProbeRegistration();

        reg.Initialize(probe, dispatcher, callback, saea, isAcceptProbe);
        return reg;
    }

    private static void RecycleProbeRegistration(AsyncProbeRegistration reg)
    {
        reg.ResetForPool();
        var pool = ThreadProbeRegistrationPool ??= new Stack<AsyncProbeRegistration>(64);
        if (pool.Count < 1024)
            pool.Push(reg);
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

    private sealed class AsyncProbeRegistration : IDisposable
    {
        private HostSocketProbeEngine _probe = null!;
        private IReadyDispatcher _dispatcher = null!;
        private Action _callback = null!;
        private SocketAsyncEventArgs _saea = null!;
        private bool _isAcceptProbe;
        private readonly Action _scheduledCompletion;
        private bool _disposed;
        private bool _completed;

        public AsyncProbeRegistration()
        {
            _scheduledCompletion = CompleteScheduled;
        }

        public void Initialize(HostSocketProbeEngine probe, IReadyDispatcher dispatcher, Action callback,
            SocketAsyncEventArgs saea, bool isAcceptProbe)
        {
            _probe = probe;
            _dispatcher = dispatcher;
            _callback = callback;
            _saea = saea;
            _isAcceptProbe = isAcceptProbe;
            _disposed = false;
            _completed = false;
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

            reg._dispatcher.Post(reg._scheduledCompletion);
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
                _probe.EnqueueAcceptedSocket(e.AcceptSocket);
                e.AcceptSocket = null;
                readyHint |= PollEvents.POLLIN;
            }
            else if (e.LastOperation == SocketAsyncOperation.Receive && e.SocketError == SocketError.Success)
            {
                readyHint |= PollEvents.POLLIN;
            }

            if (e.LastOperation == SocketAsyncOperation.Send && e.SocketError == SocketError.Success)
                readyHint |= PollEvents.POLLOUT;
            if (e.LastOperation == SocketAsyncOperation.Connect)
                readyHint |= PollEvents.POLLOUT | PollEvents.POLLERR;

            if (e.SocketError is not SocketError.Success and not SocketError.WouldBlock and not SocketError.IOPending and not SocketError.OperationAborted and not SocketError.Interrupted)
                readyHint |= PollEvents.POLLERR;

            if (readyHint != 0)
                _probe.PromoteReadyCache(readyHint);

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
            RecycleProbeRegistration(this);
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
            RecycleProbeRegistration(this);
        }

        public void Dispose()
        {
            _disposed = true;
        }

        public void ResetForPool()
        {
            _probe = null!;
            _dispatcher = null!;
            _callback = null!;
            _saea = null!;
            _isAcceptProbe = false;
            _disposed = false;
            _completed = false;
        }
    }
}
