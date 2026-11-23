using System.Collections.Concurrent;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;
using Podish.Core.Graphics;
using Podish.Display;
using Podish.Wayland;

namespace Podish.Cli.Wayland;

internal readonly record struct WaylandDesktopOptions(int Width, int Height, WaylandUiTheme Theme)
{
    public WaylandDesktopOptions(int Width, int Height) : this(Width, Height, WaylandUiTheme.CatppuccinLatte)
    {
    }

    public static WaylandDesktopOptions Default => new(1024, 768);
}

internal sealed class WaylandDisplayServerState : IDisposable
{
    private readonly WaylandSdlDisplayHost _host;
    private readonly AutoResetEvent _wake = new(false);
    private readonly ConcurrentQueue<WaylandDisplayCommand> _commands = [];
    private readonly object _commandLock = new();
    private bool _disposed;

    public WaylandDisplayServerState(ILoggerFactory loggerFactory, WaylandDesktopOptions desktopOptions,
        bool enableGles)
    {
        LoggerFactory = loggerFactory;
        Logger = loggerFactory.CreateLogger<WaylandDisplayServerState>();
        DesktopOptions = desktopOptions;
        _host = new WaylandSdlDisplayHost(loggerFactory, desktopOptions, enableGles);
        Presenter = new WaylandSdlFramePresenter(
            desktopOptions,
            EnqueueCommand,
            loggerFactory.CreateLogger<WaylandSdlFramePresenter>(), _host.CanImportGpuBuffer);
    }

    public ILoggerFactory LoggerFactory { get; }
    public ILogger Logger { get; }
    public WaylandDesktopOptions DesktopOptions { get; }
    public IWaylandFramePresenter Presenter { get; }
    internal KmsEglOutput? GuestTextures => _host.GuestTextures;
    internal VirtualDrmScanout? Scanout => _host.Scanout;
    internal uint Scale120 => _host.Scale120;

    public void Start()
    {
        _host.Start();
        Scanout!.FrameReady += Wake;
    }

    public void RunMainLoop(Task runtimeTask, WaylandDisplayIngressBridge ingressBridge)
    {
        bool scanoutDirty = true;
        while (!runtimeTask.IsCompleted)
        {
            bool dirty = DrainCommands(out List<ulong> consumed);

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

            bool outputResized = _host.RefreshOutputSize();

            if (_host.IsClosed)
            {
                ingressBridge.Post(new WaylandDisplayIngressEvent(WaylandDisplayIngressKind.WindowClosed));
                break;
            }

            if (_host.TryPresentScanout(out uint timestamp, out ulong frameSequence))
            {
                ingressBridge.Post(new WaylandDisplayIngressEvent(WaylandDisplayIngressKind.VSyncTick,
                    Timestamp: timestamp, FrameSequence: frameSequence));
            }

            scanoutDirty |= dirty || outputResized;
            if (scanoutDirty && _host.TryQueueScanoutFrame())
                scanoutDirty = false;

            // SDL events still need a poll, while scene and vblank notifications wake immediately.
            _wake.WaitOne(_host.HasPendingGpuUploads ? 1 : 8);
        }
    }

    public void Dispose()
    {
        lock (_commandLock)
        {
            _disposed = true;
            if (Scanout is { } scanout)
                scanout.FrameReady -= Wake;
        }

        while (_commands.TryDequeue(out WaylandDisplayCommand command))
            command.ReleaseBuffer();

        if (Presenter is IDisposable disposablePresenter)
            disposablePresenter.Dispose();
        _host.Dispose();
        _wake.Dispose();
    }

    private void EnqueueCommand(WaylandDisplayCommand command)
    {
        lock (_commandLock)
        {
            if (_disposed)
                return;

            // A client can destroy its surface before the display thread finishes uploading it.
            command.RetainBuffer();
            _commands.Enqueue(command);
            _wake.Set();
        }
    }

    private void Wake()
    {
        lock (_commandLock)
            if (!_disposed)
                _wake.Set();
    }

    private bool DrainCommands(out List<ulong> consumed)
    {
        consumed = [];

        var batch = new List<WaylandDisplayCommand>();
        while (_commands.TryDequeue(out WaylandDisplayCommand command))
            batch.Add(command);

        try
        {
            return _host.DrainCommands(batch, consumed);
        }
        finally
        {
            foreach (WaylandDisplayCommand command in batch)
                command.ReleaseBuffer();
        }
    }

}
