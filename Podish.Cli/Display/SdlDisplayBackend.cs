using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Podish.Display;
using Silk.NET.Maths;
using Silk.NET.SDL;

namespace Podish.Cli.Display;

internal sealed unsafe class SdlDisplayBackend : IDisplayBackend
{
    private readonly ILogger _logger;
    private readonly Sdl _sdl;
    private bool _initialized;

    public SdlDisplayBackend(ILogger logger)
    {
        _logger = logger;
        _sdl = Sdl.GetApi();
    }

    public string Name => "sdl";

    public IDisplayOutput CreateOutput(DisplayOutputOptions options)
    {
        EnsureInitialized();
        return new SdlDisplayOutput(_sdl, _logger, options);
    }

    public void Dispose()
    {
        if (!_initialized)
            return;

        _sdl.QuitSubSystem(Sdl.InitVideo);
        _initialized = false;
    }

    private void EnsureInitialized()
    {
        if (_initialized)
            return;

        if (_sdl.InitSubSystem(Sdl.InitVideo) != 0)
            throw new InvalidOperationException($"SDL video init failed: {GetError()}");

        _initialized = true;
    }

    private string GetError()
    {
        return Marshal.PtrToStringUTF8((nint)_sdl.GetError()) ?? "unknown SDL error";
    }
}

internal sealed unsafe class SdlDisplayOutput : IDisplayOutput
{
    private readonly Sdl _sdl;
    private readonly ILogger _logger;
    private readonly Window* _window;
    private readonly SdlDisplayRenderer _renderer;
    private readonly List<DisplayInputEvent> _pendingInputEvents = [];
    private readonly Queue<DisplaySize> _pendingResizeEvents = [];
    private Cursor* _cursor;
    private bool _disposed;
    private bool _isClosed;

    public SdlDisplayOutput(Sdl sdl, ILogger logger, DisplayOutputOptions options)
    {
        _sdl = sdl;
        _logger = logger;
        VSyncMode = options.VSyncMode;
        Width = options.Width;
        Height = options.Height;

        WindowFlags flags = WindowFlags.Shown;
        if (options.Hidden)
            flags = WindowFlags.Hidden;
        if (options.AllowHighDpi)
            flags |= WindowFlags.AllowHighdpi;
        if (options.Resizable)
            flags |= WindowFlags.Resizable;

        _window = _sdl.CreateWindow(options.Title, 100, 100, options.Width, options.Height, (uint)flags);

        if (_window == null)
            throw new InvalidOperationException($"SDL create window failed: {GetError()}");

        _renderer = new SdlDisplayRenderer(_sdl, _logger, _window, options.Width, options.Height, options.VSyncMode);
        _logger.LogInformation("Opened SDL display output {Width}x{Height} vsync={VSync}", options.Width, options.Height,
            options.VSyncMode);
    }

    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool IsClosed => _isClosed;
    public DisplayVSyncMode VSyncMode { get; }
    public IDisplayRenderer Renderer => _renderer;

    public void PumpEvents()
    {
        Event ev = default;
        while (_sdl.PollEvent(&ev) != 0)
        {
            _logger.LogDebug("SDL event type={Type}", (EventType)ev.Type);
            switch ((EventType)ev.Type)
            {
                case EventType.Quit:
                    _logger.LogDebug("SDL quit event");
                    _isClosed = true;
                    break;
                case EventType.Mousemotion:
                    _logger.LogDebug("SDL mouse motion x={X} y={Y} timestamp={Timestamp}", ev.Motion.X, ev.Motion.Y, ev.Motion.Timestamp);
                    _pendingInputEvents.Add(new DisplayInputEvent(
                        DisplayInputEventKind.PointerMotion,
                        ev.Motion.X,
                        ev.Motion.Y,
                        ev.Motion.Timestamp));
                    break;
                case EventType.Mousebuttondown:
                case EventType.Mousebuttonup:
                {
                    _logger.LogDebug(
                        "SDL mouse button type={Type} button={Button} x={X} y={Y} clicks={Clicks} timestamp={Timestamp}",
                        (EventType)ev.Type,
                        ev.Button.Button,
                        ev.Button.X,
                        ev.Button.Y,
                        ev.Button.Clicks,
                        ev.Button.Timestamp);
                    if (!TryMapPointerButton(ev.Button.Button, out DisplayPointerButton button))
                    {
                        _logger.LogDebug("SDL mouse button ignored unmapped button={Button}", ev.Button.Button);
                        break;
                    }
                    _pendingInputEvents.Add(new DisplayInputEvent(
                        DisplayInputEventKind.PointerButton,
                        ev.Button.X,
                        ev.Button.Y,
                        ev.Button.Timestamp,
                        button,
                        Pressed: (EventType)ev.Type == EventType.Mousebuttondown));
                    break;
                }
                case EventType.Keydown:
                case EventType.Keyup:
                {
                    uint? key = TryMapKeyboardScancodeToEvdev((int)ev.Key.Keysym.Scancode);
                    _logger.LogDebug(
                        "SDL keyboard type={Type} scancode={Scancode} sym={Sym} mod={Mod} repeat={Repeat} mappedKey={MappedKey}",
                        (EventType)ev.Type,
                        (int)ev.Key.Keysym.Scancode,
                        (int)ev.Key.Keysym.Sym,
                        (int)ev.Key.Keysym.Mod,
                        ev.Key.Repeat,
                        key);
                    if (key is not uint mappedKey)
                        break;

                    _pendingInputEvents.Add(new DisplayInputEvent(
                        DisplayInputEventKind.KeyboardKey,
                        Timestamp: ev.Key.Timestamp,
                        Pressed: (EventType)ev.Type == EventType.Keydown,
                        Key: mappedKey));
                    break;
                }
                case EventType.Windowevent:
                    _logger.LogDebug("SDL window event event={Event} data1={Data1} data2={Data2} timestamp={Timestamp}",
                        (WindowEventID)ev.Window.Event, ev.Window.Data1, ev.Window.Data2, ev.Window.Timestamp);
                    switch ((WindowEventID)ev.Window.Event)
                    {
                        case WindowEventID.Leave:
                            _pendingInputEvents.Add(new DisplayInputEvent(DisplayInputEventKind.PointerLeave, Timestamp: ev.Window.Timestamp));
                            break;
                        case WindowEventID.Resized:
                        case WindowEventID.SizeChanged:
                            Width = ev.Window.Data1;
                            Height = ev.Window.Data2;
                            _renderer.SetLogicalSize(Width, Height);
                            _pendingResizeEvents.Enqueue(new DisplaySize(Width, Height));
                            break;
                    }
                    break;
            }
        }
    }

    public IReadOnlyList<DisplayInputEvent> DrainInputEvents()
    {
        if (_pendingInputEvents.Count == 0)
            return Array.Empty<DisplayInputEvent>();

        var events = _pendingInputEvents.ToArray();
        _pendingInputEvents.Clear();
        return events;
    }

    public bool TryDequeueResize(out DisplaySize size)
    {
        if (_pendingResizeEvents.Count == 0)
        {
            size = default;
            return false;
        }

        size = _pendingResizeEvents.Dequeue();
        while (_pendingResizeEvents.Count > 0)
            size = _pendingResizeEvents.Dequeue();
        return true;
    }

    public void SetCursor(DisplayCursorDescriptor cursor)
    {
        ClearCursor();

        fixed (byte* pixelPtr = cursor.Pixels)
        {
            Surface* surface = _sdl.CreateRGBSurfaceWithFormatFrom(
                pixelPtr,
                cursor.Width,
                cursor.Height,
                32,
                cursor.Pitch,
                372645892u);
            if (surface == null)
                throw new InvalidOperationException($"SDL create cursor surface failed: {GetError()}");

            try
            {
                _cursor = _sdl.CreateColorCursor(surface, cursor.HotspotX, cursor.HotspotY);
                if (_cursor == null)
                    throw new InvalidOperationException($"SDL create color cursor failed: {GetError()}");
            }
            finally
            {
                _sdl.FreeSurface(surface);
            }
        }

        _sdl.SetCursor(_cursor);
        _sdl.ShowCursor(1);
    }

    public void ClearCursor()
    {
        if (_cursor != null)
        {
            _sdl.SetCursor((Cursor*)0);
            _sdl.FreeCursor(_cursor);
            _cursor = null;
        }
    }

    public void Present()
    {
        _renderer.Present();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        ClearCursor();
        _renderer.Dispose();
        if (_window != null)
            _sdl.DestroyWindow(_window);
        _disposed = true;
    }

    private string GetError()
    {
        return Marshal.PtrToStringUTF8((nint)_sdl.GetError()) ?? "unknown SDL error";
    }

    private static bool TryMapPointerButton(byte sdlButton, out DisplayPointerButton button)
    {
        switch (sdlButton)
        {
            case 1:
                button = DisplayPointerButton.Left;
                return true;
            case 2:
                button = DisplayPointerButton.Middle;
                return true;
            case 3:
                button = DisplayPointerButton.Right;
                return true;
            default:
                button = default;
                return false;
        }
    }

    private static uint? TryMapKeyboardScancodeToEvdev(int scancode)
    {
        return scancode switch
        {
            4 => 30, 5 => 48, 6 => 46, 7 => 32, 8 => 18, 9 => 33, 10 => 34, 11 => 35,
            12 => 23, 13 => 36, 14 => 37, 15 => 38, 16 => 50, 17 => 49, 18 => 24, 19 => 25,
            20 => 16, 21 => 19, 22 => 31, 23 => 20, 24 => 22, 25 => 47, 26 => 17, 27 => 45,
            28 => 21, 29 => 44,
            30 => 2, 31 => 3, 32 => 4, 33 => 5, 34 => 6, 35 => 7, 36 => 8, 37 => 9, 38 => 10, 39 => 11,
            40 => 28, 41 => 1, 42 => 14, 43 => 15, 44 => 57,
            45 => 12, 46 => 13, 47 => 26, 48 => 27, 49 => 43, 51 => 39, 52 => 40, 53 => 41,
            54 => 51, 55 => 52, 56 => 53,
            58 => 59, 59 => 60, 60 => 61, 61 => 62, 62 => 63, 63 => 64, 64 => 65, 65 => 66,
            66 => 67, 67 => 68, 68 => 87, 69 => 88,
            73 => 110, 74 => 102, 75 => 104, 76 => 111, 77 => 107, 78 => 109,
            79 => 106, 80 => 105, 81 => 108, 82 => 103,
            224 => 29, 225 => 42, 226 => 56, 227 => 125, 228 => 97, 229 => 54, 230 => 100, 231 => 126,
            _ => null
        };
    }
}

internal sealed unsafe class SdlDisplayRenderer : IDisplayRenderer
{
    private readonly Sdl _sdl;
    private readonly ILogger _logger;
    private readonly Renderer* _renderer;
    private bool _disposed;

    public SdlDisplayRenderer(Sdl sdl, ILogger logger, Window* window, int logicalWidth, int logicalHeight,
        DisplayVSyncMode vSyncMode)
    {
        _sdl = sdl;
        _logger = logger;

        RendererFlags flags = RendererFlags.Accelerated;
        if (vSyncMode != DisplayVSyncMode.Disabled)
            flags |= RendererFlags.Presentvsync;

        _renderer = _sdl.CreateRenderer(window, -1, (uint)flags);
        if (_renderer == null)
            throw new InvalidOperationException($"SDL create renderer failed: {GetError()}");

        SetLogicalSize(logicalWidth, logicalHeight);

        RendererInfo info = default;
        uint rendererFlags = _sdl.GetRendererInfo(_renderer, &info) == 0 ? info.Flags : 0u;
        Kind = (rendererFlags & (uint)RendererFlags.Accelerated) != 0
            ? DisplayRendererKind.Accelerated
            : ((flags & RendererFlags.Accelerated) != 0 ? DisplayRendererKind.Accelerated : DisplayRendererKind.Software);

        _logger.LogInformation("Opened SDL renderer kind={Kind} vsync={VSync}", Kind, vSyncMode);
    }

    public DisplayRendererKind Kind { get; }

    public void SetLogicalSize(int width, int height)
    {
        ThrowIfFailed(_sdl.RenderSetLogicalSize(_renderer, width, height), "set logical size");
        _logger.LogDebug("Configured SDL renderer logical size {Width}x{Height}", width, height);
    }

    public IDisplayTexture CreateTexture(DisplayTextureDescriptor descriptor)
    {
        uint format = descriptor.Format switch
        {
            DisplayPixelFormat.Argb8888 => 372645892u,
            DisplayPixelFormat.Xrgb8888 => 372645892u,
            _ => throw new NotSupportedException($"Unsupported display pixel format {descriptor.Format}.")
        };

        Silk.NET.SDL.TextureAccess access = descriptor.Access switch
        {
            DisplayTextureAccess.Static => Silk.NET.SDL.TextureAccess.Static,
            DisplayTextureAccess.Streaming => Silk.NET.SDL.TextureAccess.Streaming,
            DisplayTextureAccess.RenderTarget => Silk.NET.SDL.TextureAccess.Target,
            _ => throw new NotSupportedException($"Unsupported SDL texture access {descriptor.Access}.")
        };

        Texture* texture = _sdl.CreateTexture(_renderer, format, (int)access, descriptor.Width, descriptor.Height);
        if (texture == null)
            throw new InvalidOperationException($"SDL create texture failed: {GetError()}");

        return new SdlDisplayTexture(_sdl, texture, descriptor);
    }

    public void Clear(DisplayColor color)
    {
        ThrowIfFailed(_sdl.SetRenderDrawColor(_renderer, color.R, color.G, color.B, color.A), "set render draw color");
        ThrowIfFailed(_sdl.RenderClear(_renderer), "render clear");
    }

    public void Blit(IDisplayTexture texture, DisplayRect? source = null, DisplayRect? destination = null)
    {
        if (texture is not SdlDisplayTexture sdlTexture)
            throw new ArgumentException("Texture was not created by the SDL display backend.", nameof(texture));

        Rectangle<int> srcRect = default;
        Rectangle<int> dstRect = default;
        Rectangle<int>* srcPtr = null;
        Rectangle<int>* dstPtr = null;

        if (source is { } src)
        {
            srcRect = new Rectangle<int>(src.X, src.Y, src.Width, src.Height);
            srcPtr = &srcRect;
        }

        if (destination is { } dst)
        {
            dstRect = new Rectangle<int>(dst.X, dst.Y, dst.Width, dst.Height);
            dstPtr = &dstRect;
        }

        ThrowIfFailed(_sdl.RenderCopy(_renderer, sdlTexture.Texture, srcPtr, dstPtr), "render copy");
    }

    public void Present()
    {
        _sdl.RenderPresent(_renderer);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        if (_renderer != null)
            _sdl.DestroyRenderer(_renderer);
        _disposed = true;
    }

    private void ThrowIfFailed(int rc, string operation)
    {
        if (rc != 0)
            throw new InvalidOperationException($"SDL {operation} failed: {GetError()}");
    }

    private string GetError()
    {
        return Marshal.PtrToStringUTF8((nint)_sdl.GetError()) ?? "unknown SDL error";
    }
}

internal sealed unsafe class SdlDisplayTexture : IDisplayTexture
{
    private readonly Sdl _sdl;
    private bool _disposed;

    public SdlDisplayTexture(Sdl sdl, Texture* texture, DisplayTextureDescriptor descriptor)
    {
        _sdl = sdl;
        Texture = texture;
        Width = descriptor.Width;
        Height = descriptor.Height;
        Format = descriptor.Format;
    }

    public Texture* Texture { get; }
    public int Width { get; }
    public int Height { get; }
    public DisplayPixelFormat Format { get; }

    public void Update(ReadOnlySpan<byte> pixels, int pitch)
    {
        Update(new DisplayRect(0, 0, Width, Height), pixels, pitch);
    }

    public void Update(DisplayRect rect, ReadOnlySpan<byte> pixels, int pitch)
    {
        fixed (byte* pixelPtr = pixels)
        {
            Rectangle<int> sdlRect = new(rect.X, rect.Y, rect.Width, rect.Height);
            int rc = _sdl.UpdateTexture(Texture, &sdlRect, pixelPtr, pitch);
            if (rc != 0)
                throw new InvalidOperationException($"SDL update texture failed: {GetError()}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        if (Texture != null)
            _sdl.DestroyTexture(Texture);
        _disposed = true;
    }

    private string GetError()
    {
        return Marshal.PtrToStringUTF8((nint)_sdl.GetError()) ?? "unknown SDL error";
    }
}
