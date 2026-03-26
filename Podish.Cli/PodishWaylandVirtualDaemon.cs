using System.Collections.Concurrent;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;
using Podish.Cli.Wayland;
using Podish.Display;
using Podish.Wayland;

namespace Podish.Cli;

internal sealed class PodishWaylandVirtualDaemon : IVirtualDaemon
{
    private readonly ILogger _logger;
    private readonly int _ownerPid;
    private readonly ConcurrentQueue<WaylandDisplayIngressEvent> _displayIngressQueue = [];
    private readonly bool _enableHostDisplay;
    private readonly WaylandDisplayIngressBridge? _displayIngressBridge;
    private readonly WaylandServer _server;
    private int _displayIngressScheduled;

    public PodishWaylandVirtualDaemon(string unixPath, int ownerPid, ILoggerFactory loggerFactory,
        IWaylandFramePresenter? framePresenter = null,
        WaylandDisplayIngressBridge? displayIngressBridge = null,
        bool enableHostDisplay = true,
        WaylandDesktopOptions? desktopOptions = null)
    {
        UnixPath = unixPath;
        _ownerPid = ownerPid;
        _logger = loggerFactory.CreateLogger<PodishWaylandVirtualDaemon>();
        _enableHostDisplay = enableHostDisplay;
        _displayIngressBridge = displayIngressBridge;
        WaylandDesktopOptions desktop = desktopOptions ?? WaylandDesktopOptions.Default;
        _server = new WaylandServer(framePresenter, new WaylandServer.OutputInfo(
            desktop.Width,
            desktop.Height,
            1,
            "WL-1",
            "Podish Virtual Output",
            "Podish",
            "SDL Desktop"));
    }

    public string Name => "podish-wayland";
    public string UnixPath { get; }

    public void OnStart(VirtualDaemonContext context)
    {
        if (_enableHostDisplay)
            _displayIngressBridge?.Bind(ingress => PostDisplayIngress(context, ingress));

        ScheduleAcceptLoop(context);
    }

    public void OnSignal(VirtualDaemonContext context, int signo)
    {
        _logger.LogDebug("Wayland daemon received signal {Signal}", signo);
        context.Exit(128 + signo);
    }

    public void OnStop(VirtualDaemonContext context)
    {
        _logger.LogDebug("Wayland daemon stopping path={Path}", UnixPath);
        _server.ShutdownAsync().AsTask().GetAwaiter().GetResult();
    }

    private void ScheduleAcceptLoop(VirtualDaemonContext context)
    {
        context.Schedule(async ctx =>
        {
            ctx.EnsureUnixListener().Flags |= FileFlags.O_NONBLOCK;

            while (!ctx.Task.Exited)
            {
                if (OwnerExited(ctx))
                {
                    ctx.Exit(0);
                    return;
                }

                var (rc, connection) = await ctx.AcceptAsync();
                if (ctx.Task.Exited)
                    return;
                if (rc == -(int)Errno.EAGAIN)
                {
                    await new SleepAwaitable(25, ctx.Task, ctx.Scheduler);
                    continue;
                }

                if (rc != 0 || connection == null)
                {
                    _logger.LogDebug("Wayland accept returned rc={Rc}", rc);
                    return;
                }

                _logger.LogInformation("Wayland accepted client connection path={Path}", UnixPath);

                if (connection.File.OpenedInode is UnixSocketInode acceptedSocket)
                {
                    UnixCredentials? peerCredentials = acceptedSocket.GetPeerCredentials();
                    if (peerCredentials != null)
                        acceptedSocket.SetSendCredentialsOverride(peerCredentials);
                }

                ctx.ScheduleChild(childCtx =>
                {
                    connection.BindTask(childCtx.Task);
                    _logger.LogDebug("Wayland scheduling child task tid={Tid}", childCtx.Task.TID);
                    return new ValueTask(HandleClientAsync(connection));
                });
            }
        });
    }

    private void PostDisplayIngress(VirtualDaemonContext context, WaylandDisplayIngressEvent ingress)
    {
        _displayIngressQueue.Enqueue(ingress);
        context.Scheduler.ScheduleFromAnyThread(() => RequestDisplayIngressPump(context));
    }

    private void RequestDisplayIngressPump(VirtualDaemonContext context)
    {
        if (Interlocked.Exchange(ref _displayIngressScheduled, 1) != 0)
            return;

        context.ScheduleChild(_ => new ValueTask(RunDisplayIngressPumpAsync(context)));
    }

    private async Task RunDisplayIngressPumpAsync(VirtualDaemonContext context)
    {
        try
        {
            while (_displayIngressQueue.TryDequeue(out WaylandDisplayIngressEvent ingress))
                if (!await HandleDisplayIngressAsync(ingress, context))
                    return;
        }
        finally
        {
            Interlocked.Exchange(ref _displayIngressScheduled, 0);
            if (!_displayIngressQueue.IsEmpty)
                RequestDisplayIngressPump(context);
        }
    }

    private bool OwnerExited(VirtualDaemonContext context)
    {
        if (_ownerPid <= 0)
            return false;

        var owner = context.Scheduler.GetProcess(_ownerPid);
        return owner == null || owner.State is ProcessState.Zombie or ProcessState.Dead;
    }

    private async Task HandleClientAsync(VirtualDaemonConnection connection)
    {
        WaylandServerSession? session = null;
        try
        {
            using (connection)
            {
                _logger.LogInformation("Wayland client handler starting");
                session = new WaylandServerSession(connection, _server, _logger);
                await session.RunAsync();
                _logger.LogInformation("Wayland client handler completed");
            }
        }
        catch (IOException ex)
        {
            _logger.LogInformation(ex, "Wayland client disconnected while sending or receiving");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Wayland client handler failed");
            throw;
        }
        finally
        {
            if (session != null)
                await _server.DisconnectClientAsync(session.Client);
        }
    }

    private async ValueTask HandleInputEventAsync(DisplayInputEvent inputEvent)
    {
        if (_server.FramePresenter is IWaylandSceneView sceneView &&
            inputEvent.Kind is DisplayInputEventKind.PointerMotion or DisplayInputEventKind.PointerButton)
        {
            if (sceneView.TryGetSurfaceAt(inputEvent.X, inputEvent.Y, out WaylandSurfaceHit hit))
                _logger.LogDebug("Wayland input kind={Kind} x={X} y={Y} hit sceneSurfaceId={SceneSurfaceId} sx={SurfaceX} sy={SurfaceY} button={Button} pressed={Pressed}",
                    inputEvent.Kind, inputEvent.X, inputEvent.Y, hit.SceneSurfaceId, hit.SurfaceX, hit.SurfaceY, inputEvent.Button, inputEvent.Pressed);
            else
            {
                if (_server.FramePresenter is IWaylandSceneDebugView debugView &&
                    inputEvent.Kind == DisplayInputEventKind.PointerButton)
                {
                    string surfaces = string.Join(", ", debugView.SnapshotSurfaceBounds()
                        .Select(entry => $"#{entry.SceneSurfaceId}@({entry.Bounds.X},{entry.Bounds.Y},{entry.Bounds.Width}x{entry.Bounds.Height})"));
                    _logger.LogDebug("Wayland input kind={Kind} x={X} y={Y} hit none button={Button} pressed={Pressed} scene=[{Scene}]",
                        inputEvent.Kind, inputEvent.X, inputEvent.Y, inputEvent.Button, inputEvent.Pressed, surfaces);
                }
                else
                {
                    _logger.LogDebug("Wayland input kind={Kind} x={X} y={Y} hit none button={Button} pressed={Pressed}",
                        inputEvent.Kind, inputEvent.X, inputEvent.Y, inputEvent.Button, inputEvent.Pressed);
                }
            }
        }
        else
        {
            _logger.LogDebug("Wayland input kind={Kind} x={X} y={Y} button={Button} key={Key} pressed={Pressed}",
                inputEvent.Kind, inputEvent.X, inputEvent.Y, inputEvent.Button, inputEvent.Key, inputEvent.Pressed);
        }

        switch (inputEvent.Kind)
        {
            case DisplayInputEventKind.PointerMotion:
                await _server.HandlePointerMotionAsync(inputEvent.X, inputEvent.Y, inputEvent.Timestamp);
                break;
            case DisplayInputEventKind.PointerButton:
                await _server.HandlePointerMotionAsync(inputEvent.X, inputEvent.Y, inputEvent.Timestamp);
                await _server.HandlePointerButtonAsync((uint)inputEvent.Button, inputEvent.Pressed, inputEvent.Timestamp);
                break;
            case DisplayInputEventKind.PointerLeave:
                await _server.ClearPointerFocusAsync();
                break;
            case DisplayInputEventKind.KeyboardKey:
                await _server.HandleKeyboardKeyAsync(inputEvent.Key, inputEvent.Pressed, inputEvent.Timestamp);
                break;
            case DisplayInputEventKind.TextInput:
                await _server.HandleTextInputCommitAsync(inputEvent.Text ?? string.Empty);
                break;
            case DisplayInputEventKind.TextEditing:
                await _server.HandleTextInputPreeditAsync(inputEvent.Text ?? string.Empty, inputEvent.CursorBegin, inputEvent.CursorEnd);
                break;
            case DisplayInputEventKind.WindowFocusLost:
                await _server.HandleHostTextInputFocusLostAsync();
                break;
            case DisplayInputEventKind.WindowFocusGained:
                await _server.HandleHostTextInputFocusGainedAsync();
                break;
        }
    }

    private async ValueTask<bool> HandleDisplayIngressAsync(WaylandDisplayIngressEvent ingress, VirtualDaemonContext? context)
    {
        switch (ingress.Kind)
        {
            case WaylandDisplayIngressKind.Input:
                await HandleInputEventAsync(ingress.InputEvent);
                return true;
            case WaylandDisplayIngressKind.BufferConsumed:
                await _server.HandleBufferConsumedAsync(ingress.LeaseToken);
                return true;
            case WaylandDisplayIngressKind.Resize:
                if (_server.FramePresenter is IWaylandDesktopSceneController desktopSceneController)
                    desktopSceneController.ResizeDesktop(ingress.Size.Width, ingress.Size.Height);
                return true;
            case WaylandDisplayIngressKind.WindowClosed:
                _logger.LogInformation("Wayland display output closed, exiting wayland compositor");
                context?.Exit(0);
                return false;
            case WaylandDisplayIngressKind.VSyncTick:
                await _server.HandlePresentationTickAsync(ingress.Timestamp != 0
                    ? ingress.Timestamp
                    : unchecked((uint)Environment.TickCount64));
                return true;
            default:
                return true;
        }
    }
}
