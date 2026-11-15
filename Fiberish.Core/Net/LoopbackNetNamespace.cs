using System.Net;
using System.Runtime.InteropServices;
using Fiberish.Core;
using Fiberish.Core.Native;
using Timer = Fiberish.Core.Timer;

namespace Fiberish.Core.Net;

public sealed class LoopbackNetNamespace : IDisposable
{
    private static readonly NetstackNative.NetnsNotifyCallback NotifyThunk = OnNetnsNotify;

    private static readonly nint NotifyThunkPtr =
        Marshal.GetFunctionPointerForDelegate(NotifyThunk);

    private readonly Lock _pumpGate = new();
    private readonly Dictionary<KernelScheduler, PollPump> _pumps = [];
    private int _pumpCount;

    private LoopbackNetNamespace(ulong handle, uint ipv4Be, byte prefixLen)
    {
        Handle = handle;
        Ipv4AddressBe = ipv4Be;
        PrefixLength = prefixLen;
    }

    public uint Ipv4AddressBe { get; }
    public byte PrefixLength { get; }
    public ulong Handle { get; private set; }

    public IPAddress PrivateIpv4Address => new([
        (byte)(Ipv4AddressBe >> 24), (byte)(Ipv4AddressBe >> 16), (byte)(Ipv4AddressBe >> 8), (byte)Ipv4AddressBe
    ]);

    public void Dispose()
    {
        if (Handle == 0)
            return;

        PollPump[]? pumps = null;
        lock (_pumpGate)
        {
            if (_pumps.Count > 0)
            {
                pumps = [.. _pumps.Values];
                _pumps.Clear();
                Volatile.Write(ref _pumpCount, 0);
            }
        }

        if (pumps != null)
        {
            foreach (var pump in pumps)
                pump.Dispose();
        }

        UnbindWakeCallback();
        var handle = Handle;
        Handle = 0;
        NetstackNative.Destroy(handle);
    }

    public static LoopbackNetNamespace Create(uint ipv4Be, byte prefixLen)
    {
        var handle = NetstackNative.CreateLoopback(ipv4Be, prefixLen);
        if (handle == 0)
            throw new InvalidOperationException("Failed to create loopback net namespace.");

        var rc = NetstackNative.GetIpv4(handle, out var actualIp, out var actualPrefix);
        if (rc != 0)
        {
            NetstackNative.Destroy(handle);
            throw new InvalidOperationException($"Failed to read net namespace address (rc={rc}).");
        }

        return new LoopbackNetNamespace(handle, actualIp, actualPrefix);
    }

    public long Poll(long nowMillis)
    {
        ThrowIfDisposed();
        var rc = NetstackNative.Poll(Handle, nowMillis, out var nextPollMillis);
        if (rc != 0)
            throw new InvalidOperationException($"Failed to poll net namespace (rc={rc}).");
        return nextPollMillis;
    }

    private static void OnNetnsNotify(nint token)
    {
        NetstackWakeRegistry.Signal(token);
    }

    public void BindWakeCallback(nint token)
    {
        ThrowIfDisposed();
        var rc = NetstackNative.SetNotifyCallback(Handle, NotifyThunkPtr, token);
        if (rc != 0) throw new InvalidOperationException($"Failed to bind wake callback (rc={rc})");
    }

    public void UnbindWakeCallback()
    {
        if (Handle != 0) NetstackNative.SetNotifyCallback(Handle, 0, 0);
    }

    public void ClearNotify()
    {
        ThrowIfDisposed();
        NetstackNative.ClearNotify(Handle);
    }

    public bool TryAllocateEphemeralPort(out ushort port)
    {
        ThrowIfDisposed();
        if (NetstackNative.AllocateEphemeralPort(Handle, out var candidate) == 0)
        {
            port = candidate;
            return true;
        }

        port = 0;
        return false;
    }

    public bool TryReserveTcpPort(ushort port)
    {
        if (port == 0)
            return false;

        ThrowIfDisposed();
        return NetstackNative.ReserveTcpPort(Handle, port) == 0;
    }

    public void ReleaseTcpPort(ushort port)
    {
        if (port == 0 || Handle == 0)
            return;

        NetstackNative.ReleaseTcpPort(Handle, port);
    }

    internal void RegisterWaitTarget(KernelScheduler scheduler, INetstackWaitTarget target)
    {
        if (Handle == 0)
            return;

        PollPump pump;
        lock (_pumpGate)
        {
            if (Handle == 0)
                return;
            if (!_pumps.TryGetValue(scheduler, out pump!))
            {
                pump = new PollPump(this, scheduler);
                _pumps[scheduler] = pump;
                Volatile.Write(ref _pumpCount, _pumps.Count);
            }
        }

        pump.Add(target);
    }

    internal void UnregisterWaitTarget(KernelScheduler scheduler, INetstackWaitTarget target)
    {
        PollPump? pump;
        lock (_pumpGate)
        {
            _pumps.TryGetValue(scheduler, out pump);
        }

        pump?.Remove(target);
    }

    internal void NudgeWaitPumps()
    {
        if (Volatile.Read(ref _pumpCount) == 0 || Handle == 0)
            return;

        lock (_pumpGate)
        {
            foreach (var pump in _pumps.Values)
                pump.Nudge();
        }
    }

    public TcpListenerSocket CreateTcpListener(int sendBytes = 0, int recvBytes = 0)
    {
        ThrowIfDisposed();
        var socket = sendBytes > 0 && recvBytes > 0
            ? NetstackNative.CreateTcpListenerSized(Handle, (nuint)sendBytes, (nuint)recvBytes)
            : NetstackNative.CreateTcpListener(Handle);
        if (socket == 0)
            throw new InvalidOperationException("Failed to create TCP listener socket.");
        return new TcpListenerSocket(this, socket);
    }

    public TcpStreamSocket CreateTcpStream(int sendBytes = 0, int recvBytes = 0)
    {
        ThrowIfDisposed();
        var socket = sendBytes > 0 && recvBytes > 0
            ? NetstackNative.CreateTcpStreamSized(Handle, (nuint)sendBytes, (nuint)recvBytes)
            : NetstackNative.CreateTcpStream(Handle);
        if (socket == 0)
            throw new InvalidOperationException("Failed to create TCP stream socket.");
        return new TcpStreamSocket(this, socket);
    }

    public UdpDatagramSocket CreateUdpSocket(int sendBytes = 0, int recvBytes = 0)
    {
        ThrowIfDisposed();
        var socket = sendBytes > 0 && recvBytes > 0
            ? NetstackNative.CreateUdpSocketSized(Handle, (nuint)sendBytes, (nuint)recvBytes)
            : NetstackNative.CreateUdpSocket(Handle);
        if (socket == 0)
            throw new InvalidOperationException("Failed to create UDP socket.");
        return new UdpDatagramSocket(this, socket);
    }

    /// <summary>
    ///     Joins an IPv4 multicast group on the namespace interface. Membership is reference
    ///     counted by the caller's per-socket registrations; the packet filter is shared.
    /// </summary>
    public int JoinMulticast(uint groupIpv4Be)
    {
        ThrowIfDisposed();
        return NetstackNative.JoinMulticast(Handle, groupIpv4Be);
    }

    /// <summary>Leaves a group; returns the last local error, 0 on success.</summary>
    public int LeaveMulticast(uint groupIpv4Be)
    {
        ThrowIfDisposed();
        return NetstackNative.LeaveMulticast(Handle, groupIpv4Be);
    }

    private void ThrowIfDisposed()
    {
        if (Handle == 0)
            throw new ObjectDisposedException(nameof(LoopbackNetNamespace));
    }

    public sealed class TcpListenerSocket : IDisposable
    {
        private readonly LoopbackNetNamespace _namespace;
        private ulong _handle;

        internal TcpListenerSocket(LoopbackNetNamespace @namespace, ulong handle)
        {
            _namespace = @namespace;
            _handle = handle;
        }

        public bool AcceptPending
        {
            get
            {
                ThrowIfDisposed();
                var rc = NetstackNative.TcpListenerAcceptPending(_namespace.Handle, _handle);
                if (rc < 0)
                    throw new InvalidOperationException($"Failed to query accept state (rc={rc}).");
                return rc != 0;
            }
        }

        public ushort LocalPort
        {
            get
            {
                ThrowIfDisposed();
                var rc = NetstackNative.TcpListenerGetLocalPort(_namespace.Handle, _handle, out var port);
                return rc == 0 ? port : (ushort)0;
            }
        }

        public void Dispose()
        {
            if (_handle == 0)
                return;

            var handle = _handle;
            _handle = 0;
            NetstackNative.CloseSocket(_namespace.Handle, handle);
        }

        public int TryListen(ushort localPort, uint backlog = 16)
        {
            ThrowIfDisposed();
            return NetstackNative.TcpListenerListen(_namespace.Handle, _handle, localPort, backlog);
        }

        public void Listen(ushort localPort, uint backlog = 16)
        {
            var rc = TryListen(localPort, backlog);
            if (rc != 0)
                throw new InvalidOperationException($"Failed to listen on TCP socket (rc={rc}).");
        }

        public int TryAccept(out TcpStreamSocket? accepted)
        {
            ThrowIfDisposed();
            var rc = NetstackNative.TcpListenerAccept(_namespace.Handle, _handle, out var handle);
            if (rc != 0)
            {
                accepted = null;
                return rc;
            }

            accepted = new TcpStreamSocket(_namespace, handle);
            return 0;
        }

        public TcpStreamSocket Accept()
        {
            var rc = TryAccept(out var accepted);
            if (rc != 0 || accepted == null)
                throw new InvalidOperationException($"Failed to accept TCP socket (rc={rc}).");
            return accepted;
        }

        /// <summary>
        ///     Applies buffer capacities while the listener has no bound port and no pending
        ///     accepted connections. Returns the native error code.
        /// </summary>
        public int TrySetBuffers(int sendBytes, int recvBytes)
        {
            ThrowIfDisposed();
            return NetstackNative.SocketSetBuffers(_namespace.Handle, _handle, (nuint)sendBytes, (nuint)recvBytes);
        }

        public (int SendBytes, int RecvBytes) GetBufferSizes()
        {
            ThrowIfDisposed();
            var rc = NetstackNative.SocketGetBuffers(_namespace.Handle, _handle, out var send, out var recv);
            if (rc != 0)
                throw new InvalidOperationException($"Failed to query listener buffer sizes (rc={rc}).");
            return (checked((int)send), checked((int)recv));
        }

        private void ThrowIfDisposed()
        {
            if (_handle == 0)
                throw new ObjectDisposedException(nameof(TcpListenerSocket));
        }
    }

    public sealed class TcpStreamSocket : IDisposable
    {
        private readonly LoopbackNetNamespace _namespace;
        private ulong _handle;

        internal TcpStreamSocket(LoopbackNetNamespace @namespace, ulong handle)
        {
            _namespace = @namespace;
            _handle = handle;
        }

        public bool CanRead
        {
            get
            {
                ThrowIfDisposed();
                var rc = NetstackNative.TcpStreamCanRead(_namespace.Handle, _handle);
                if (rc < 0)
                    throw new InvalidOperationException($"Failed to query read readiness (rc={rc}).");
                return rc != 0;
            }
        }

        public bool CanWrite
        {
            get
            {
                ThrowIfDisposed();
                var rc = NetstackNative.TcpStreamCanWrite(_namespace.Handle, _handle);
                if (rc < 0)
                    throw new InvalidOperationException($"Failed to query write readiness (rc={rc}).");
                return rc != 0;
            }
        }

        public int AvailableSendBytes
        {
            get
            {
                ThrowIfDisposed();
                var rc = NetstackNative.TcpStreamSendAvailable(_namespace.Handle, _handle);
                if (rc < 0)
                    throw new InvalidOperationException($"Failed to query TCP send capacity (rc={rc}).");
                return rc;
            }
        }

        public bool MayRead
        {
            get
            {
                ThrowIfDisposed();
                var rc = NetstackNative.TcpStreamMayRead(_namespace.Handle, _handle);
                if (rc < 0)
                    throw new InvalidOperationException($"Failed to query TCP read viability (rc={rc}).");
                return rc != 0;
            }
        }

        public bool MayWrite
        {
            get
            {
                ThrowIfDisposed();
                var rc = NetstackNative.TcpStreamMayWrite(_namespace.Handle, _handle);
                if (rc < 0)
                    throw new InvalidOperationException($"Failed to query TCP write viability (rc={rc}).");
                return rc != 0;
            }
        }

        public int State
        {
            get
            {
                ThrowIfDisposed();
                var rc = NetstackNative.TcpStreamState(_namespace.Handle, _handle);
                if (rc < 0)
                    throw new InvalidOperationException($"Failed to query TCP state (rc={rc}).");
                return rc;
            }
        }

        public IPEndPoint LocalEndPoint
        {
            get
            {
                ThrowIfDisposed();
                var rc = NetstackNative.TcpStreamGetLocalEndpoint(_namespace.Handle, _handle, out var ipv4Be,
                    out var port);
                if (rc != 0)
                    throw new InvalidOperationException($"Failed to query local endpoint (rc={rc}).");
                return new IPEndPoint(
                    new IPAddress([(byte)(ipv4Be >> 24), (byte)(ipv4Be >> 16), (byte)(ipv4Be >> 8), (byte)ipv4Be]),
                    port);
            }
        }

        public IPEndPoint RemoteEndPoint
        {
            get
            {
                ThrowIfDisposed();
                var rc = NetstackNative.TcpStreamGetRemoteEndpoint(_namespace.Handle, _handle, out var ipv4Be,
                    out var port);
                if (rc != 0)
                    throw new InvalidOperationException($"Failed to query remote endpoint (rc={rc}).");
                return new IPEndPoint(
                    new IPAddress([(byte)(ipv4Be >> 24), (byte)(ipv4Be >> 16), (byte)(ipv4Be >> 8), (byte)ipv4Be]),
                    port);
            }
        }

        public void Dispose()
        {
            if (_handle == 0)
                return;

            var handle = _handle;
            _handle = 0;
            NetstackNative.CloseSocket(_namespace.Handle, handle);
        }

        public int TryConnect(uint remoteIpv4Be, ushort remotePort)
        {
            ThrowIfDisposed();
            return NetstackNative.TcpStreamConnect(_namespace.Handle, _handle, remoteIpv4Be, remotePort);
        }

        public void Connect(uint remoteIpv4Be, ushort remotePort)
        {
            var rc = TryConnect(remoteIpv4Be, remotePort);
            if (rc != 0)
                throw new InvalidOperationException($"Failed to connect TCP socket (rc={rc}).");
        }

        public int TrySend(ReadOnlySpan<byte> data, out int written)
        {
            ThrowIfDisposed();
            unsafe
            {
                fixed (byte* ptr = data)
                {
                    var rc = NetstackNative.TcpStreamSend(_namespace.Handle, _handle, ptr, (nuint)data.Length,
                        out var nativeWritten);
                    written = rc == 0 ? checked((int)nativeWritten) : 0;
                    return rc;
                }
            }
        }

        public int Send(ReadOnlySpan<byte> data)
        {
            var rc = TrySend(data, out var written);
            if (rc != 0)
                throw new InvalidOperationException($"Failed to send TCP data (rc={rc}).");
            return written;
        }

        public int TryReceive(Span<byte> buffer, out int read)
        {
            ThrowIfDisposed();
            unsafe
            {
                fixed (byte* ptr = buffer)
                {
                    var rc = NetstackNative.TcpStreamRecv(_namespace.Handle, _handle, ptr, (nuint)buffer.Length,
                        out var nativeRead);
                    read = rc == 0 ? checked((int)nativeRead) : 0;
                    return rc;
                }
            }
        }

        public int Receive(Span<byte> buffer)
        {
            var rc = TryReceive(buffer, out var read);
            if (rc != 0)
                throw new InvalidOperationException($"Failed to receive TCP data (rc={rc}).");
            return read;
        }

        public int TryCloseWrite()
        {
            ThrowIfDisposed();
            return NetstackNative.TcpStreamClose(_namespace.Handle, _handle);
        }

        public void CloseWrite()
        {
            var rc = TryCloseWrite();
            if (rc != 0)
                throw new InvalidOperationException($"Failed to close TCP send side (rc={rc}).");
        }

        public int TrySetBuffers(int sendBytes, int recvBytes)
        {
            ThrowIfDisposed();
            return NetstackNative.SocketSetBuffers(_namespace.Handle, _handle, (nuint)sendBytes, (nuint)recvBytes);
        }

        public (int SendBytes, int RecvBytes) GetBufferSizes()
        {
            ThrowIfDisposed();
            var rc = NetstackNative.SocketGetBuffers(_namespace.Handle, _handle, out var send, out var recv);
            if (rc != 0)
                throw new InvalidOperationException($"Failed to query TCP buffer sizes (rc={rc}).");
            return (checked((int)send), checked((int)recv));
        }

        public int TrySetNoDelay(bool enabled)
        {
            ThrowIfDisposed();
            return NetstackNative.TcpStreamSetNoDelay(_namespace.Handle, _handle, enabled ? 1 : 0);
        }

        public bool NoDelay
        {
            get
            {
                ThrowIfDisposed();
                var rc = NetstackNative.TcpStreamGetNoDelay(_namespace.Handle, _handle);
                if (rc < 0)
                    throw new InvalidOperationException($"Failed to query TCP_NODELAY (rc={rc}).");
                return rc != 0;
            }
        }

        public int TrySetKeepAlive(int intervalMillis)
        {
            ThrowIfDisposed();
            return NetstackNative.TcpStreamSetKeepAlive(_namespace.Handle, _handle, intervalMillis);
        }

        public int KeepAliveIntervalMillis
        {
            get
            {
                ThrowIfDisposed();
                var rc = NetstackNative.TcpStreamGetKeepAlive(_namespace.Handle, _handle, out var millis);
                if (rc != 0)
                    throw new InvalidOperationException($"Failed to query keepalive interval (rc={rc}).");
                return checked((int)millis);
            }
        }

        public void Abort()
        {
            ThrowIfDisposed();
            var rc = NetstackNative.TcpStreamAbort(_namespace.Handle, _handle);
            if (rc != 0)
                throw new InvalidOperationException($"Failed to abort TCP stream (rc={rc}).");
        }

        private void ThrowIfDisposed()
        {
            if (_handle == 0)
                throw new ObjectDisposedException(nameof(TcpStreamSocket));
        }
    }

    public sealed class UdpDatagramSocket : IDisposable
    {
        private readonly LoopbackNetNamespace _namespace;
        private ulong _handle;

        internal UdpDatagramSocket(LoopbackNetNamespace @namespace, ulong handle)
        {
            _namespace = @namespace;
            _handle = handle;
        }

        public bool CanRead
        {
            get
            {
                ThrowIfDisposed();
                var rc = NetstackNative.UdpSocketCanRead(_namespace.Handle, _handle);
                if (rc < 0)
                    throw new InvalidOperationException($"Failed to query UDP read readiness (rc={rc}).");
                return rc != 0;
            }
        }

        public bool CanWrite
        {
            get
            {
                ThrowIfDisposed();
                var rc = NetstackNative.UdpSocketCanWrite(_namespace.Handle, _handle);
                if (rc < 0)
                    throw new InvalidOperationException($"Failed to query UDP write readiness (rc={rc}).");
                return rc != 0;
            }
        }

        public IPEndPoint LocalEndPoint
        {
            get
            {
                ThrowIfDisposed();
                var rc = NetstackNative.UdpSocketGetLocalEndpoint(_namespace.Handle, _handle, out var ipv4Be,
                    out var port);
                if (rc != 0)
                    throw new InvalidOperationException($"Failed to query UDP local endpoint (rc={rc}).");
                return new IPEndPoint(
                    new IPAddress([(byte)(ipv4Be >> 24), (byte)(ipv4Be >> 16), (byte)(ipv4Be >> 8), (byte)ipv4Be]),
                    port);
            }
        }

        public void Dispose()
        {
            if (_handle == 0)
                return;

            var handle = _handle;
            _handle = 0;
            NetstackNative.CloseSocket(_namespace.Handle, handle);
        }

        public int TrySetBuffers(int sendBytes, int recvBytes)
        {
            ThrowIfDisposed();
            return NetstackNative.SocketSetBuffers(_namespace.Handle, _handle, (nuint)sendBytes, (nuint)recvBytes);
        }

        public (int SendBytes, int RecvBytes) GetBufferSizes()
        {
            ThrowIfDisposed();
            var rc = NetstackNative.SocketGetBuffers(_namespace.Handle, _handle, out var send, out var recv);
            if (rc != 0)
                throw new InvalidOperationException($"Failed to query UDP buffer sizes (rc={rc}).");
            return (checked((int)send), checked((int)recv));
        }

        public int TryBind(ushort localPort)
        {
            ThrowIfDisposed();
            return NetstackNative.UdpSocketBind(_namespace.Handle, _handle, localPort);
        }

        public void Bind(ushort localPort)
        {
            var rc = TryBind(localPort);
            if (rc != 0)
                throw new InvalidOperationException($"Failed to bind UDP socket (rc={rc}).");
        }

        public int TrySendTo(uint remoteIpv4Be, ushort remotePort, ReadOnlySpan<byte> data, out int written)
        {
            ThrowIfDisposed();
            unsafe
            {
                fixed (byte* ptr = data)
                {
                    var rc = NetstackNative.UdpSocketSendTo(_namespace.Handle, _handle, remoteIpv4Be, remotePort, ptr,
                        (nuint)data.Length,
                        out var nativeWritten);
                    written = rc == 0 ? checked((int)nativeWritten) : 0;
                    return rc;
                }
            }
        }

        public int SendTo(uint remoteIpv4Be, ushort remotePort, ReadOnlySpan<byte> data)
        {
            var rc = TrySendTo(remoteIpv4Be, remotePort, data, out var written);
            if (rc != 0)
                throw new InvalidOperationException($"Failed to send UDP data (rc={rc}).");
            return written;
        }

        public int TryReceiveFrom(Span<byte> buffer, out int read, out IPEndPoint remoteEndPoint)
        {
            ThrowIfDisposed();
            unsafe
            {
                fixed (byte* ptr = buffer)
                {
                    var rc = NetstackNative.UdpSocketRecvFrom(_namespace.Handle, _handle, ptr, (nuint)buffer.Length,
                        out var nativeRead,
                        out var ipv4Be, out var port);
                    if (rc != 0)
                    {
                        read = 0;
                        remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);
                        return rc;
                    }

                    remoteEndPoint =
                        new IPEndPoint(
                            new IPAddress([
                                (byte)(ipv4Be >> 24), (byte)(ipv4Be >> 16), (byte)(ipv4Be >> 8), (byte)ipv4Be
                            ]), port);
                    read = checked((int)nativeRead);
                    return 0;
                }
            }
        }

        public int ReceiveFrom(Span<byte> buffer, out IPEndPoint remoteEndPoint)
        {
            var rc = TryReceiveFrom(buffer, out var read, out remoteEndPoint);
            if (rc != 0)
                throw new InvalidOperationException($"Failed to receive UDP data (rc={rc}).");
            return read;
        }

        private void ThrowIfDisposed()
        {
            if (_handle == 0)
                throw new ObjectDisposedException(nameof(UdpDatagramSocket));
        }
    }

    internal interface INetstackWaitTarget
    {
        int PumpIndex { get; set; }
        bool EvaluateReady();
        void InvokeReady();
    }

    private sealed class PollPump : IDisposable
    {
        private readonly long _baseWallMs;
        private readonly Lock _gate = new();
        private readonly LoopbackNetNamespace _namespace;
        private readonly List<INetstackWaitTarget> _readyScratch = [];
        private readonly KernelScheduler _scheduler;
        private readonly List<INetstackWaitTarget> _targets = [];
        private readonly Action _tickAction;
        private bool _disposed;
        private long _lastNextPollMs = 1;
        private int _nudgePending;
        private Timer? _timer;
        private bool _timerScheduled;

        public PollPump(LoopbackNetNamespace @namespace, KernelScheduler scheduler)
        {
            _namespace = @namespace;
            _scheduler = scheduler;
            _baseWallMs = Environment.TickCount64 - scheduler.CurrentTick;
            _tickAction = OnTick;
        }

        public void Add(INetstackWaitTarget target)
        {
            lock (_gate)
            {
                if (_disposed || target.PumpIndex >= 0)
                    return;

                target.PumpIndex = _targets.Count;
                _targets.Add(target);
                if (!_timerScheduled)
                {
                    var delay = Math.Clamp(_lastNextPollMs, 1, 10);
                    _timerScheduled = true;
                    _timer = _scheduler.ScheduleTimer(delay, _tickAction);
                }
            }
        }

        public void Remove(INetstackWaitTarget target)
        {
            lock (_gate)
            {
                var idx = target.PumpIndex;
                if (idx < 0 || idx >= _targets.Count || !ReferenceEquals(_targets[idx], target))
                    return;

                SwapRemoveAt(idx);
                if (_targets.Count == 0 && _timerScheduled)
                {
                    _timer?.Cancel();
                    _timer = null;
                    _timerScheduled = false;
                }
            }
        }

        public void Nudge()
        {
            if (_disposed)
                return;

            lock (_gate)
            {
                if (_disposed || _targets.Count == 0)
                    return;
            }

            if (Interlocked.Exchange(ref _nudgePending, 1) == 0)
                _scheduler.Schedule(_tickAction);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _timer?.Cancel();
                _timer = null;
                _timerScheduled = false;
                for (var i = 0; i < _targets.Count; i++)
                    _targets[i].PumpIndex = -1;
                _targets.Clear();
                _readyScratch.Clear();
            }
        }

        private void SwapRemoveAt(int idx)
        {
            var lastIdx = _targets.Count - 1;
            var removed = _targets[idx];
            if (idx != lastIdx)
            {
                var moved = _targets[lastIdx];
                _targets[idx] = moved;
                moved.PumpIndex = idx;
            }

            _targets.RemoveAt(lastIdx);
            removed.PumpIndex = -1;
        }

        private void OnTick()
        {
            bool shouldPoll;
            lock (_gate)
            {
                _timer?.Cancel();
                _timer = null;
                _timerScheduled = false;
                Volatile.Write(ref _nudgePending, 0);
                shouldPoll = !_disposed && _targets.Count > 0;
            }

            if (!shouldPoll)
                return;

            long nextPoll;
            try
            {
                var nowMs = Math.Max(Environment.TickCount64, _baseWallMs + _scheduler.CurrentTick);
                nextPoll = _namespace.Poll(nowMs);
            }
            catch (ObjectDisposedException)
            {
                Dispose();
                return;
            }

            lock (_gate)
            {
                if (_disposed || _targets.Count == 0)
                    return;

                _lastNextPollMs = nextPoll;
                _readyScratch.Clear();
                for (var i = 0; i < _targets.Count; i++)
                {
                    var target = _targets[i];
                    if (target.EvaluateReady())
                        _readyScratch.Add(target);
                }

                for (var i = 0; i < _readyScratch.Count; i++)
                {
                    var readyTarget = _readyScratch[i];
                    var idx = readyTarget.PumpIndex;
                    if (idx >= 0 && idx < _targets.Count && ReferenceEquals(_targets[idx], readyTarget))
                        SwapRemoveAt(idx);
                }
            }

            try
            {
                for (var i = 0; i < _readyScratch.Count; i++)
                    _readyScratch[i].InvokeReady();
            }
            finally
            {
                _readyScratch.Clear();
            }

            lock (_gate)
            {
                if (!_disposed && _targets.Count > 0 && !_timerScheduled)
                {
                    var delay = Math.Clamp(_lastNextPollMs, 1, 10);
                    _timerScheduled = true;
                    _timer = _scheduler.ScheduleTimer(delay, _tickAction);
                }
            }
        }
    }
}