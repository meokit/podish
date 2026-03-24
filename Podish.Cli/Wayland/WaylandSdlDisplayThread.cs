using System.Collections.Concurrent;
using System.Threading;
using Microsoft.Extensions.Logging;
using Podish.Display;
using Podish.Wayland;

namespace Podish.Cli.Wayland;

internal enum WaylandDisplayIngressKind
{
    Input,
    BufferConsumed,
    Resize,
    WindowClosed,
    VSyncTick
}

internal readonly record struct WaylandDisplayIngressEvent(
    WaylandDisplayIngressKind Kind,
    DisplayInputEvent InputEvent = default,
    ulong LeaseToken = 0,
    DisplaySize Size = default,
    uint Timestamp = 0);

internal enum WaylandDisplayCommandKind
{
    PresentSurface,
    UpdateSurfaceBounds,
    RaiseSurface,
    RemoveSurface,
    Shutdown
}

internal readonly record struct WaylandDisplayCommand(
    WaylandDisplayCommandKind Kind,
    ulong SceneSurfaceId = 0,
    WaylandShmFrame Frame = default,
    WaylandSurfaceBounds Bounds = default);

internal sealed class WaylandSdlDisplayThread : IDisposable
{
    private readonly ILogger _logger;
    private readonly Action<WaylandDisplayIngressEvent> _postIngress;
    private readonly ConcurrentQueue<WaylandDisplayCommand> _commands = [];
    private readonly AutoResetEvent _wakeEvent = new(false);
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new(false);
    private readonly WaylandSdlDisplayHost _host;
    private volatile bool _running = true;
    private Exception? _threadFailure;

    public WaylandSdlDisplayThread(ILoggerFactory loggerFactory, WaylandDesktopOptions desktopOptions,
        Action<WaylandDisplayIngressEvent> postIngress)
    {
        _logger = loggerFactory.CreateLogger<WaylandSdlDisplayThread>();
        _postIngress = postIngress;
        _host = new WaylandSdlDisplayHost(loggerFactory, desktopOptions);
        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "PodishWaylandSdl"
        };
    }

    public void Start()
    {
        _thread.Start();
        _started.Wait();
        if (_threadFailure != null)
            throw new InvalidOperationException("SDL display thread failed to start.", _threadFailure);
    }

    public void Enqueue(WaylandDisplayCommand command)
    {
        if (!_running)
            return;

        _commands.Enqueue(command);
        _wakeEvent.Set();
    }

    public void Dispose()
    {
        _running = false;
        _commands.Enqueue(new WaylandDisplayCommand(WaylandDisplayCommandKind.Shutdown));
        _wakeEvent.Set();
        if (_thread.IsAlive)
            _thread.Join();
        _wakeEvent.Dispose();
        _started.Dispose();
    }

    private void ThreadMain()
    {
        try
        {
            _host.Start();
            _started.Set();

            while (_running)
            {
                bool dirty = DrainCommands(out bool shutdownRequested);

                foreach (DisplayInputEvent inputEvent in _host.PumpInputEvents())
                    _postIngress(new WaylandDisplayIngressEvent(WaylandDisplayIngressKind.Input, inputEvent));

                if (_host.IsClosed)
                {
                    _postIngress(new WaylandDisplayIngressEvent(WaylandDisplayIngressKind.WindowClosed));
                    break;
                }

                if (dirty)
                {
                    _host.Render();
                    _postIngress(new WaylandDisplayIngressEvent(WaylandDisplayIngressKind.VSyncTick));
                }

                if (shutdownRequested)
                    break;

                _wakeEvent.WaitOne(dirty ? 0 : 8);
            }
        }
        catch (Exception ex)
        {
            _threadFailure = ex;
            _logger.LogError(ex, "SDL display thread failed");
            _started.Set();
            _postIngress(new WaylandDisplayIngressEvent(WaylandDisplayIngressKind.WindowClosed));
        }
        finally
        {
            _host.Dispose();
        }
    }

    private bool DrainCommands(out bool shutdownRequested)
    {
        var batch = new List<WaylandDisplayCommand>();
        while (_commands.TryDequeue(out WaylandDisplayCommand command))
            batch.Add(command);

        var consumed = new List<ulong>();
        bool dirty = _host.DrainCommands(batch, consumed, out shutdownRequested);
        if (shutdownRequested)
            _running = false;

        foreach (ulong leaseToken in consumed)
            _postIngress(new WaylandDisplayIngressEvent(WaylandDisplayIngressKind.BufferConsumed, LeaseToken: leaseToken));

        return dirty;
    }
}
