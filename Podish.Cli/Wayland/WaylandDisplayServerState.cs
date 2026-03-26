using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Podish.Display;
using Podish.Wayland;

namespace Podish.Cli.Wayland;

internal readonly record struct WaylandDesktopOptions(int Width, int Height)
{
    public static WaylandDesktopOptions Default => new(1024, 768);
}

internal sealed class WaylandDisplayServerState : IDisposable
{
    private readonly WaylandSdlDisplayHost _host;
    private readonly ConcurrentQueue<WaylandDisplayCommand> _commands = [];

    public WaylandDisplayServerState(ILoggerFactory loggerFactory, WaylandDesktopOptions desktopOptions)
    {
        LoggerFactory = loggerFactory;
        Logger = loggerFactory.CreateLogger<WaylandDisplayServerState>();
        DesktopOptions = desktopOptions;
        _host = new WaylandSdlDisplayHost(loggerFactory, desktopOptions);
        Presenter = new WaylandSdlFramePresenter(desktopOptions, command => _commands.Enqueue(command));
    }

    public ILoggerFactory LoggerFactory { get; }
    public ILogger Logger { get; }
    public WaylandDesktopOptions DesktopOptions { get; }
    public IWaylandFramePresenter Presenter { get; }

    public void RunMainLoop(Task runtimeTask, WaylandDisplayIngressBridge ingressBridge)
    {
        _host.Start();

        while (!runtimeTask.IsCompleted)
        {
            bool dirty = DrainCommands(out List<ulong> consumed, out bool shutdownRequested);

            foreach (ulong leaseToken in consumed)
                ingressBridge.Post(new WaylandDisplayIngressEvent(WaylandDisplayIngressKind.BufferConsumed, LeaseToken: leaseToken));

            IReadOnlyList<DisplayInputEvent> inputEvents = _host.PumpInputEvents();
            if (inputEvents.Count > 0)
                Logger.LogDebug("Wayland host drained input events count={Count}", inputEvents.Count);

            foreach (DisplayInputEvent inputEvent in inputEvents)
            {
                Logger.LogDebug("Wayland host input kind={Kind} x={X} y={Y} button={Button} key={Key} pressed={Pressed} ts={Timestamp}",
                    inputEvent.Kind, inputEvent.X, inputEvent.Y, inputEvent.Button, inputEvent.Key, inputEvent.Pressed, inputEvent.Timestamp);
                ingressBridge.Post(new WaylandDisplayIngressEvent(WaylandDisplayIngressKind.Input, inputEvent));
            }

            if (_host.TryDequeueResize(out DisplaySize size))
            {
                dirty = true;
                ingressBridge.Post(new WaylandDisplayIngressEvent(WaylandDisplayIngressKind.Resize, Size: size));
            }

            if (_host.IsClosed)
            {
                ingressBridge.Post(new WaylandDisplayIngressEvent(WaylandDisplayIngressKind.WindowClosed));
                break;
            }

            if (dirty)
            {
                _host.Render();
                ingressBridge.Post(new WaylandDisplayIngressEvent(
                    WaylandDisplayIngressKind.VSyncTick,
                    Timestamp: unchecked((uint)Environment.TickCount64)));
            }

            if (shutdownRequested)
                break;

            Thread.Sleep(dirty ? 0 : 8);
        }
    }

    public void Dispose()
    {
        if (Presenter is IDisposable disposablePresenter)
            disposablePresenter.Dispose();
        _host.Dispose();
    }

    private bool DrainCommands(out List<ulong> consumed, out bool shutdownRequested)
    {
        consumed = [];
        shutdownRequested = false;

        var batch = new List<WaylandDisplayCommand>();
        while (_commands.TryDequeue(out WaylandDisplayCommand command))
            batch.Add(command);

        return _host.DrainCommands(batch, consumed, out shutdownRequested);
    }
}
