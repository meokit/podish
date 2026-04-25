using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Fiberish.Core.Net;
using Microsoft.Extensions.Logging;

namespace Podish.Core.Networking;

public sealed class PortForwardLoop : IDisposable
{
    private readonly
        Dictionary<string, (ContainerNetworkContext context, List<(PublishedPortSpec spec, Socket listener)> ports)>
        _activeContexts = [];

    private readonly Lock _contextsLock = new();
    private readonly ConcurrentQueue<LoopEvent> _eventQueue = new();
    private readonly ILogger _logger;
    private readonly Thread _loopThread;
    private readonly List<RelaySession> _sessions = [];
    private readonly AutoResetEvent _wakeSignal = new(false);

    private readonly Dictionary<string, nint> _wakeTokens = [];
    private volatile bool _disposed;
    private long _nextSessionId = 1;
    private volatile bool _running = true;
    private volatile bool _wakeSignalDisposed;

    public PortForwardLoop(ILogger logger)
    {
        _logger = logger;
        _loopThread = new Thread(Run) { IsBackground = true, Name = "PortForwardLoop" };
        _loopThread.Start();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _running = false;
        // Unblock wait-loop before joining.
        try
        {
            _wakeSignal.Set();
        }
        catch
        {
        }

        _loopThread.Join();
        _wakeSignalDisposed = true;
        _wakeSignal.Dispose();
        _disposed = true;
    }

    public void StartPublishedPorts(ContainerNetworkContext context, IReadOnlyList<PublishedPortSpec> ports)
    {
        foreach (var port in ports)
            _eventQueue.Enqueue(new LoopEvent
            {
                Type = LoopEventType.CommandStartPort,
                Context = context,
                PortSpec = port
            });

        WakeLoop();
    }

    public void StopPublishedPorts(ContainerNetworkContext context, TaskCompletionSource? completion = null)
    {
        _eventQueue.Enqueue(new LoopEvent
        {
            Type = LoopEventType.CommandStop,
            Context = context,
            Completion = completion
        });
        WakeLoop();
    }

    public IEnumerable<int> GetActivePorts(string containerId)
    {
        lock (_contextsLock)
        {
            if (_activeContexts.TryGetValue(containerId, out var tuple))
                return tuple.ports
                    .Where(p => p.listener.LocalEndPoint != null)
                    .Select(p => ((IPEndPoint)p.listener.LocalEndPoint!).Port)
                    .ToArray();
        }

        return Enumerable.Empty<int>();
    }

    private void Run()
    {
        _logger.LogInformation("PortForwardLoop started");
        var sw = Stopwatch.StartNew();

        while (_running)
        {
            // 1. Drain events
            while (_eventQueue.TryDequeue(out var ev)) ProcessEvent(ev);

            var now = sw.ElapsedMilliseconds;
            var nextGlobalDeadline = long.MaxValue;

            // 2. Poll Namespaces
            ContainerNetworkContext[] contextsToPoll;
            lock (_contextsLock)
            {
                contextsToPoll = _activeContexts.Values.Select(v => v.context).ToArray();
            }

            foreach (var ctx in contextsToPoll)
                try
                {
                    var nextDeadline = ctx.Namespace.Poll(now);
                    if (nextDeadline < nextGlobalDeadline)
                        nextGlobalDeadline = nextDeadline;
                    ctx.Namespace.ClearNotify();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to poll namespace for container {Id}", ctx.ContainerId);
                }

            // 3. Process Sessions (Relay logic)
            ProcessSessions();

            // 4. Wait
            var timeout = Timeout.Infinite;
            var currentNow = sw.ElapsedMilliseconds;
            if (nextGlobalDeadline != long.MaxValue) timeout = (int)Math.Max(0, nextGlobalDeadline - currentNow);
            // No need for arbitrary 100ms cap if we trust the signal
            _wakeSignal.WaitOne(timeout);
        }

        _logger.LogInformation("PortForwardLoop exiting");
        CleanupAll();
    }

    private void ProcessEvent(LoopEvent ev)
    {
        try
        {
            if (ev.Session?.IsDisposed == true && ev.Type is LoopEventType.HostReceive or LoopEventType.HostSend)
                return;

            switch (ev.Type)
            {
                case LoopEventType.CommandStartPort:
                    HandleStartPort(ev.Context!, ev.PortSpec!);
                    break;
                case LoopEventType.CommandStop:
                    HandleStop(ev.Context!, ev.Completion);
                    break;
                case LoopEventType.HostAccept:
                    HandleHostAccept(ev.Args!, ev.Context!, ev.PortSpec!);
                    break;
                case LoopEventType.HostReceive:
                    HandleHostReceive(ev.Session!);
                    break;
                case LoopEventType.HostSend:
                    HandleHostSend(ev.Session!);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing loop event {Type}", ev.Type);
        }
    }

    private void HandleStartPort(ContainerNetworkContext context, PublishedPortSpec spec)
    {
        try
        {
            var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Parse(spec.BindAddress), spec.HostPort));
            listener.Listen(128);

            lock (_contextsLock)
            {
                if (!_activeContexts.TryGetValue(context.ContainerId, out var tuple))
                {
                    tuple = (context, []);
                    _activeContexts[context.ContainerId] = tuple;

                    var token = NetstackWakeRegistry.Register(_wakeSignal);
                    context.Namespace.BindWakeCallback(token);
                    _wakeTokens[context.ContainerId] = token;
                }

                tuple.ports.Add((spec, listener));
            }

            _logger.LogInformation("Started port forward: host {HostPort} -> container {ContainerPort} (ID: {Id})",
                spec.HostPort, spec.ContainerPort, context.ContainerId);

            BeginAccept(listener, context, spec);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start published port {Port} for container {Id}", spec.HostPort,
                context.ContainerId);
        }
    }

    private void HandleStop(ContainerNetworkContext context, TaskCompletionSource? completion)
    {
        List<(PublishedPortSpec spec, Socket listener)>? portsToClose = null;
        lock (_contextsLock)
        {
            if (_activeContexts.Remove(context.ContainerId, out var tuple))
            {
                portsToClose = tuple.ports;
                if (_wakeTokens.Remove(context.ContainerId, out var token))
                {
                    context.Namespace.UnbindWakeCallback();
                    NetstackWakeRegistry.Unregister(token);
                }
            }
        }

        if (portsToClose != null)
            foreach (var p in portsToClose)
                try
                {
                    p.listener.Dispose();
                }
                catch
                {
                }

        // Close sessions related to this context
        for (var i = _sessions.Count - 1; i >= 0; i--)
        {
            var session = _sessions[i];
            if (session.Context == context)
            {
                _logger.LogDebug("Closing relay session {Id} due to port forward stop", session.Id);
                session.Dispose();
                _sessions.RemoveAt(i);
            }
        }

        completion?.TrySetResult();
    }

    private void BeginAccept(Socket listener, ContainerNetworkContext context, PublishedPortSpec spec)
    {
        var args = new SocketAsyncEventArgs { UserToken = listener };
        args.Completed += (s, e) =>
        {
            _eventQueue.Enqueue(new LoopEvent
            {
                Type = LoopEventType.HostAccept,
                Args = e,
                Context = context,
                PortSpec = spec
            });
            WakeLoop();
        };

        try
        {
            if (!listener.AcceptAsync(args)) HandleHostAccept(args, context, spec);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void HandleHostAccept(SocketAsyncEventArgs args, ContainerNetworkContext context, PublishedPortSpec spec)
    {
        var listener = (Socket)args.UserToken!;
        var accepted = args.AcceptSocket;

        if (args.SocketError == SocketError.Success && accepted != null)
        {
            _logger.LogDebug("Accepted host connection for port {Port}", spec.HostPort);

            try
            {
                // Resolve target
                var target = context.Switch.ResolvePublishedPortTarget(context, spec.ContainerPort, spec.Protocol);

                // Create guest stream
                var guestSocket = context.Namespace.CreateTcpStream();
                var guestStream = new PrivateNetstackStream(guestSocket);

                // Connect guest stream
                var ipBytes = target.Address.GetAddressBytes();
                if (ipBytes.Length != 4)
                    throw new NotSupportedException(
                        $"Only IPv4 addresses are currently supported. Address {target.Address} has {ipBytes.Length} bytes.");

                var ipBe = ((uint)ipBytes[0] << 24) | ((uint)ipBytes[1] << 16) | ((uint)ipBytes[2] << 8) | ipBytes[3];
                guestSocket.Connect(ipBe, (ushort)target.Port);

                var session = new RelaySession(_nextSessionId++, accepted, guestStream, context);
                _sessions.Add(session);

                // PortForwardLoop: We don't start Host I/O yet. 
                // ProcessSessions will check for GuestConnected.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to establish relay for port {Port}", spec.HostPort);
                try
                {
                    accepted.Dispose();
                }
                catch
                {
                }
            }
        }

        // Reset for next accept
        args.AcceptSocket = null;
        BeginAccept(listener, context, spec);
    }

    private void BeginHostReceive(RelaySession session)
    {
        if (session.HostReadClosed || session.HostReceivePending) return;

        if (!session.HostReceiveEventBound)
        {
            session.HostReceiveArgs.Completed += OnHostReceiveCompleted;
            session.HostReceiveEventBound = true;
        }

        session.HostReceiveArgs.UserToken = session;

        session.HostReceivePending = true;
        try
        {
            if (!session.HostSocket.ReceiveAsync(session.HostReceiveArgs)) HandleHostReceive(session);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Host receive failed: {Message}", ex.Message);
            session.HostReadClosed = true;
            session.HostReceivePending = false;
            try
            {
                session.GuestStream.CloseWrite();
            }
            catch
            {
            }

            WakeLoop();
        }
    }

    private void OnHostReceiveCompleted(object? sender, SocketAsyncEventArgs e)
    {
        var session = (RelaySession)e.UserToken!;
        _eventQueue.Enqueue(new LoopEvent { Type = LoopEventType.HostReceive, Session = session });
        WakeLoop();
    }

    private void HandleHostReceive(RelaySession session)
    {
        session.HostReceivePending = false;
        var args = session.HostReceiveArgs;

        if (args.SocketError != SocketError.Success || args.BytesTransferred == 0)
        {
            session.HostReadClosed = true;
            try
            {
                session.GuestStream.CloseWrite();
            }
            catch
            {
            }

            return;
        }

        // It's safe to reset offset to 0 and overwrite count because BeginHostReceive 
        // is ONLY ever called when HostToGuestCount is 0.
        if (session.HostToGuestCount > 0)
            _logger.LogWarning("Host receive completed but there was still unsent data in the buffer. Overwriting...");

        session.HostToGuestCount = args.BytesTransferred;
        session.HostToGuestOffset = 0;
        WakeLoop();
    }

    private void BeginHostSend(RelaySession session)
    {
        if (session.HostWriteClosed || session.HostSendPending || session.GuestToHostCount == 0) return;

        if (!session.HostSendEventBound)
        {
            session.HostSendArgs.Completed += OnHostSendCompleted;
            session.HostSendEventBound = true;
        }

        session.HostSendArgs.UserToken = session;
        session.HostSendArgs.SetBuffer(session.GuestToHostBuffer, session.GuestToHostOffset, session.GuestToHostCount);

        session.HostSendPending = true;
        try
        {
            if (!session.HostSocket.SendAsync(session.HostSendArgs)) HandleHostSend(session);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Host send failed: {Message}", ex.Message);
            session.HostWriteClosed = true;
            session.HostSendPending = false;
            WakeLoop();
        }
    }

    private void OnHostSendCompleted(object? sender, SocketAsyncEventArgs e)
    {
        var session = (RelaySession)e.UserToken!;
        _eventQueue.Enqueue(new LoopEvent { Type = LoopEventType.HostSend, Session = session });
        WakeLoop();
    }

    private void HandleHostSend(RelaySession session)
    {
        session.HostSendPending = false;
        var args = session.HostSendArgs;

        if (args.SocketError != SocketError.Success)
        {
            session.HostWriteClosed = true;
            return;
        }

        session.GuestToHostCount -= args.BytesTransferred;
        session.GuestToHostOffset += args.BytesTransferred;
        WakeLoop();
    }

    private void ProcessSessions()
    {
        for (var i = _sessions.Count - 1; i >= 0; i--)
        {
            var session = _sessions[i];
            try
            {
                var active = false;

                // 0. Guest Handshake Check
                if (!session.GuestConnected)
                {
                    if (session.GuestStream.MayRead || session.GuestStream.MayWrite)
                    {
                        _logger.LogDebug("Guest stream connected for session {Id}", session.Id);
                        session.GuestConnected = true;
                        BeginHostReceive(session);
                        active = true;
                    }
                    else if (session.GuestStream is PrivateNetstackStream ps &&
                             ps.IsClosed) // Assuming we add IsClosed or check MayRead/Write
                    {
                        _logger.LogWarning("Guest connection failed for session {Id}", session.Id);
                        session.Dispose();
                        _sessions.RemoveAt(i);
                        continue;
                    }
                }

                if (!session.GuestConnected) continue;

                // 1. Host -> Guest
                if (session.HostToGuestCount > 0)
                    if (session.GuestStream.CanWrite)
                    {
                        var n = session.GuestStream.Write(
                            session.HostToGuestBuffer.AsSpan(session.HostToGuestOffset, session.HostToGuestCount));
                        if (n > 0)
                        {
                            session.HostToGuestCount -= n;
                            session.HostToGuestOffset += n;
                            active = true;
                        }
                    }

                if (session.HostToGuestCount == 0 && !session.HostReadClosed && !session.HostReceivePending)
                {
                    BeginHostReceive(session);
                    active = true;
                }

                // 2. Guest -> Host
                if (session.GuestToHostCount == 0 && !session.GuestReadClosed)
                    if (session.GuestStream.CanRead)
                    {
                        var n = session.GuestStream.Read(session.GuestToHostBuffer);
                        if (n > 0)
                        {
                            session.GuestToHostCount = n;
                            session.GuestToHostOffset = 0;
                            active = true;
                        }
                        else if (!session.GuestStream.MayRead)
                        {
                            session.GuestReadClosed = true;
                            try
                            {
                                session.HostSocket.Shutdown(SocketShutdown.Send);
                            }
                            catch
                            {
                            }

                            session.HostWriteClosed = true; // shutdown send side, so we can't write anymore
                        }
                    }

                if (session.GuestToHostCount > 0 && !session.HostWriteClosed && !session.HostSendPending)
                {
                    BeginHostSend(session);
                    active = true;
                }

                // Check if session is finished
                if (session.HostReadClosed && session.GuestReadClosed && session.HostToGuestCount == 0 &&
                    session.GuestToHostCount == 0)
                {
                    _logger.LogDebug("Closing relay session {Id}", session.Id);
                    session.Dispose();
                    _sessions.RemoveAt(i);
                }
                else if (active)
                {
                    // If we did some work, maybe more can be done in this loop
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Exception in relay session {Id}. Closing session.", session.Id);
                try
                {
                    session.Dispose();
                }
                catch
                {
                }

                _sessions.RemoveAt(i);
            }
        }
    }

    private void CleanupAll()
    {
        foreach (var sess in _sessions) sess.Dispose();
        _sessions.Clear();
        lock (_contextsLock)
        {
            foreach (var ctx in _activeContexts.Values)
            {
                if (_wakeTokens.Remove(ctx.context.ContainerId, out var token))
                {
                    try
                    {
                        ctx.context.Namespace.UnbindWakeCallback();
                    }
                    catch
                    {
                    }

                    NetstackWakeRegistry.Unregister(token);
                }

                foreach (var p in ctx.ports)
                    try
                    {
                        p.listener.Dispose();
                    }
                    catch
                    {
                    }
            }

            _activeContexts.Clear();
            _wakeTokens.Clear();
        }
    }

    private void WakeLoop()
    {
        if (_wakeSignalDisposed)
            return;

        try
        {
            _wakeSignal.Set();
        }
        catch (ObjectDisposedException)
        {
            // Teardown can race with async socket completions; wake is best-effort.
        }
    }
}