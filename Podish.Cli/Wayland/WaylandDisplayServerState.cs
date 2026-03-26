using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Podish.Display;
using Podish.Wayland;

namespace Podish.Cli.Wayland;

internal readonly record struct WaylandDesktopOptions(int Width, int Height, WaylandUiTheme Theme)
{
    public WaylandDesktopOptions(int Width, int Height) : this(Width, Height, WaylandUiTheme.BreezeLight)
    {
    }

    public static WaylandDesktopOptions Default => new(1024, 768, WaylandUiTheme.BreezeLight);
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

            foreach (DisplayInputEvent inputEvent in OrderInputEventsForIme(inputEvents))
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

    private static IEnumerable<DisplayInputEvent> OrderInputEventsForIme(IReadOnlyList<DisplayInputEvent> inputEvents)
    {
        bool containsImeTextEvent = inputEvents.Any(static ev =>
            ev.Kind is DisplayInputEventKind.TextEditing or DisplayInputEventKind.TextInput);

        static int Priority(DisplayInputEventKind kind) => kind switch
        {
            DisplayInputEventKind.TextEditing => 0,
            DisplayInputEventKind.TextInput => 1,
            DisplayInputEventKind.KeyboardKey => 2,
            _ => 3
        };

        return inputEvents
            .Where(ev => !containsImeTextEvent || !ShouldSuppressKeyboardEventForIme(ev))
            .Select(static (ev, index) => (ev, index))
            .OrderBy(static item => Priority(item.ev.Kind))
            .ThenBy(static item => item.index)
            .Select(static item => item.ev);
    }

    private static bool ShouldSuppressKeyboardEventForIme(DisplayInputEvent inputEvent)
    {
        if (inputEvent.Kind != DisplayInputEventKind.KeyboardKey)
            return false;

        if (!WaylandKeyboardLayout.TryGetByEvdevKey(inputEvent.Key, out WaylandKeyboardKeyDescriptor descriptor))
            return false;

        if (descriptor.ModifierRole != WaylandKeyboardModifierRole.None)
            return false;

        return descriptor.EvdevKey is
            >= 2 and <= 13 or
            >= 16 and <= 27 or
            >= 30 and <= 53 or
            14 or
            15 or
            28 or
            57;
    }
}
