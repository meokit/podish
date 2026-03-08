using System.Net.Sockets;
using Fiberish.Core;
using Fiberish.Syscalls;
using Microsoft.Extensions.Logging;

namespace Fiberish.VFS;

internal sealed class HostSocketReadiness : IDisposable
{
    private const long ReadyCacheTtlMs = 2;

    [ThreadStatic] private static Stack<SocketAsyncEventArgs>? ThreadProbeArgsPool;

    private readonly HostSocketInode _owner;
    private readonly Socket _socket;
    private readonly ILogger _logger;
    private readonly Queue<Socket> _acceptedProbeQueue = new();
    private short _readyCacheBits;
    private long _readyCacheExpireAtMs;

    public HostSocketReadiness(HostSocketInode owner, Socket socket, ILogger logger)
    {
        _owner = owner;
        _socket = socket;
        _logger = logger;
    }

    public short Poll(LinuxFile file, short events)
    {
        if (TryProbeReady(events, out var revents))
            return revents;
        return PollEvents.POLLERR;
    }

    public bool RegisterWait(LinuxFile file, Action callback, short events)
    {
        return RegisterWaitHandle(file, callback, events) != null;
    }

    public IDisposable? RegisterWaitHandle(LinuxFile file, Action callback, short events)
    {
        var scheduler = KernelScheduler.Current;
        if (scheduler == null) return null;
        _logger.LogTrace("Host socket RegisterWait ino={Ino} events=0x{Events:X}", _owner.Ino, events);

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
            var reg = IsNonBlockingConnectPending(file)
                ? ArmConnectProbe(callback, scheduler)
                : ArmWriteProbe(callback, scheduler);
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

    public async ValueTask<bool> WaitForSocketEventAsync(LinuxFile file, short events)
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

            // Keep POLLERR visibility independent of requested mask.
            var hasError = _socket.Poll(0, SelectMode.SelectError);

            // For non-blocking connect completion, SelectError alone is ambiguous on some hosts.
            // Linux poll semantics require SO_ERROR to disambiguate success (POLLOUT) vs failure (POLLERR).
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

    private IDisposable? ArmReadProbe(Action callback, KernelScheduler scheduler)
    {
        var saea = RentProbeArgs();
        saea.SetBuffer(null, 0, 0);
        saea.SocketFlags = SocketFlags.None;
        saea.AcceptSocket = null;
        var reg = new AsyncProbeRegistration(this, scheduler, callback, saea, isAcceptProbe: false);
        try
        {
            if (!_socket.ReceiveAsync(saea))
            {
                reg.HandleCompletedSync();
                scheduler.Schedule(callback);
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
            if (!_socket.AcceptAsync(saea))
            {
                reg.HandleCompletedSync();
                scheduler.Schedule(callback);
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
            if (!_socket.SendAsync(saea))
            {
                reg.HandleCompletedSync();
                scheduler.Schedule(callback);
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

    private IDisposable? ArmConnectProbe(Action callback, KernelScheduler scheduler)
    {
        var saea = RentProbeArgs();
        saea.SetBuffer(null, 0, 0);
        saea.SocketFlags = SocketFlags.None;
        saea.AcceptSocket = null;
        var reg = new AsyncProbeRegistration(this, scheduler, callback, saea, isAcceptProbe: false);
        try
        {
            if (!_socket.ConnectAsync(saea))
            {
                reg.HandleCompletedSync();
                scheduler.Schedule(callback);
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
        private readonly HostSocketReadiness _readiness;
        private readonly KernelScheduler _scheduler;
        private readonly Action _callback;
        private readonly SocketAsyncEventArgs _saea;
        private readonly bool _isAcceptProbe;
        private readonly Action _scheduledCompletion;
        private bool _disposed;
        private bool _completed;

        public AsyncProbeRegistration(HostSocketReadiness readiness, KernelScheduler scheduler, Action callback,
            SocketAsyncEventArgs saea, bool isAcceptProbe)
        {
            _readiness = readiness;
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
                _readiness.EnqueueAcceptedSocket(e.AcceptSocket);
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
                _readiness.PromoteReadyCache(readyHint);

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
}
