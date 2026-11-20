using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Podish.Display;
using Podish.Core.Graphics;
using Podish.Wayland;
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

    internal SdlDisplayOutput CreateEglOutput(DisplayOutputOptions options)
    {
        EnsureInitialized();
        return new SdlDisplayOutput(_sdl, _logger, options, egl: true);
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
    private readonly SdlDisplayRenderer? _renderer;
    private void* _metalView;
    private nint _waylandLibrary;
    private nint _waylandWindow;
    private int _visible = 1;
    internal KmsEglWindow KmsWindow { get; }
    private readonly List<DisplayInputEvent> _pendingInputEvents = [];
    private readonly Queue<DisplaySize> _pendingResizeEvents = [];
    private Cursor* _cursor;
    private DisplaySystemCursor? _systemCursor;
    private bool _cursorIsSystem;
    private bool _disposed;
    private bool _isClosed;
    private readonly bool _hideSystemCursor;
    private readonly bool _restoreCursorVisibility;

    public SdlDisplayOutput(Sdl sdl, ILogger logger, DisplayOutputOptions options, bool egl = false)
    {
        _sdl = sdl;
        _logger = logger;
        VSyncMode = options.VSyncMode;
        Width = options.Width;
        Height = options.Height;

        WindowFlags flags = options.Hidden ? WindowFlags.Hidden : WindowFlags.Shown;
        _visible = options.Hidden ? 0 : 1;
        if (egl && OperatingSystem.IsMacOS()) flags |= WindowFlags.Metal;
        if (options.AllowHighDpi)
            flags |= WindowFlags.AllowHighdpi;
        if (options.Resizable)
            flags |= WindowFlags.Resizable;

        _window = _sdl.CreateWindow(options.Title, 100, 100, options.Width, options.Height, (uint)flags);

        if (_window == null)
            throw new InvalidOperationException($"SDL create window failed: {GetError()}");

        try
        {
            if (egl) KmsWindow = CreateNativeEglWindow();
            else _renderer = new SdlDisplayRenderer(_sdl, _logger, _window, options.Width, options.Height, options.VSyncMode);
        }
        catch
        {
            DestroyNativeEglWindow();
            _sdl.DestroyWindow(_window);
            throw;
        }
        _hideSystemCursor = options.HideSystemCursor;
        _restoreCursorVisibility = _hideSystemCursor && _sdl.ShowCursor(-1) == 1;
        UpdateCursorVisibility();
        _logger.LogInformation("Opened SDL display output {Width}x{Height} vsync={VSync}", options.Width, options.Height,
            options.VSyncMode);
    }

    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool IsClosed => _isClosed;
    public DisplayVSyncMode VSyncMode { get; }
    public IDisplayRenderer Renderer => _renderer ?? throw new InvalidOperationException("This window has an EGL renderer.");

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
                        ev.Motion.Timestamp)
                    {
                        DeltaX = ev.Motion.Xrel,
                        DeltaY = ev.Motion.Yrel
                    });
                    break;
                case EventType.Mousewheel:
                {
                    var direction = (uint)ev.Wheel.Direction == 1 ? -1 : 1; // SDL_MOUSEWHEEL_FLIPPED
                    _pendingInputEvents.Add(new DisplayInputEvent(DisplayInputEventKind.PointerWheel)
                    {
                        DeltaX = direction * ev.Wheel.X,
                        DeltaY = direction * ev.Wheel.Y
                    });
                    break;
                }
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
                case EventType.Textinput:
                {
                    byte* textPtr = ev.Text.Text;
                    string text = Marshal.PtrToStringUTF8((nint)textPtr) ?? string.Empty;
                    if (text.Length == 0)
                        break;

                    _pendingInputEvents.Add(new DisplayInputEvent(
                        DisplayInputEventKind.TextInput,
                        Timestamp: ev.Text.Timestamp,
                        Text: text));
                    break;
                }
                case EventType.Textediting:
                {
                    byte* textPtr = ev.Edit.Text;
                    string text = Marshal.PtrToStringUTF8((nint)textPtr) ?? string.Empty;
                    _pendingInputEvents.Add(new DisplayInputEvent(
                        DisplayInputEventKind.TextEditing,
                        Timestamp: ev.Edit.Timestamp,
                        Text: text,
                        CursorBegin: ev.Edit.Start,
                        CursorEnd: ev.Edit.Start + ev.Edit.Length));
                    break;
                }
                case EventType.Windowevent:
                    _logger.LogDebug("SDL window event event={Event} data1={Data1} data2={Data2} timestamp={Timestamp}",
                        (WindowEventID)ev.Window.Event, ev.Window.Data1, ev.Window.Data2, ev.Window.Timestamp);
                    switch ((WindowEventID)ev.Window.Event)
                    {
                        case WindowEventID.Hidden:
                        case WindowEventID.Minimized:
                            Volatile.Write(ref _visible, 0);
                            break;
                        case WindowEventID.Shown:
                        case WindowEventID.Restored:
                        case WindowEventID.Maximized:
                            Volatile.Write(ref _visible, 1);
                            break;
                        case WindowEventID.Close:
                            Volatile.Write(ref _visible, 0);
                            _isClosed = true;
                            break;
                        case WindowEventID.Enter when _hideSystemCursor && _sdl.GetMouseFocus() == _window:
                            int x, y;
                            _sdl.GetMouseState(&x, &y);
                            _pendingInputEvents.Add(new DisplayInputEvent(DisplayInputEventKind.PointerMotion,
                                x, y, ev.Window.Timestamp));
                            break;
                        case WindowEventID.Leave:
                            _pendingInputEvents.Add(new DisplayInputEvent(DisplayInputEventKind.PointerLeave, Timestamp: ev.Window.Timestamp));
                            break;
                        case WindowEventID.FocusLost:
                            _pendingInputEvents.Add(new DisplayInputEvent(DisplayInputEventKind.WindowFocusLost, Timestamp: ev.Window.Timestamp));
                            break;
                        case WindowEventID.FocusGained:
                            _pendingInputEvents.Add(new DisplayInputEvent(DisplayInputEventKind.WindowFocusGained, Timestamp: ev.Window.Timestamp));
                            break;
                        case WindowEventID.Resized:
                        case WindowEventID.SizeChanged:
                            Width = ev.Window.Data1;
                            Height = ev.Window.Data2;
                            _renderer?.SetLogicalSize(Width, Height);
                            _pendingResizeEvents.Enqueue(new DisplaySize(Width, Height));
                            break;
                    }
                    break;
            }
        }
        UpdateCursorVisibility();
    }

    private void UpdateCursorVisibility()
    {
        if (_restoreCursorVisibility)
            _sdl.ShowCursor(_sdl.GetMouseFocus() == _window ? 0 : 1);
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

    public void StartTextInput()
    {
        _sdl.StartTextInput();
    }

    public void StopTextInput()
    {
        _sdl.StopTextInput();
    }

    public void SetTextInputRect(DisplayRect rect)
    {
        var sdlRect = new Rectangle<int>(rect.X, rect.Y, rect.Width, rect.Height);
        _sdl.SetTextInputRect(in sdlRect);
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
        _cursorIsSystem = false;
        _systemCursor = null;
    }

    public void SetSystemCursor(DisplaySystemCursor cursor)
    {
        if (_cursor != null && _cursorIsSystem && _systemCursor == cursor)
        {
            _sdl.SetCursor(_cursor);
            _sdl.ShowCursor(1);
            return;
        }

        ClearCursor();

        _cursor = _sdl.CreateSystemCursor((SystemCursor)cursor);
        if (_cursor == null)
            throw new InvalidOperationException($"SDL create system cursor failed: {GetError()}");

        _sdl.SetCursor(_cursor);
        _sdl.ShowCursor(1);
        _cursorIsSystem = true;
        _systemCursor = cursor;
    }

    public void ClearCursor()
    {
        if (_cursor != null)
        {
            _sdl.SetCursor((Cursor*)0);
            _sdl.FreeCursor(_cursor);
            _cursor = null;
        }

        _cursorIsSystem = false;
        _systemCursor = null;
    }

    public void Present()
    {
        Renderer.Present();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        ClearCursor();
        if (_restoreCursorVisibility) _sdl.ShowCursor(1);
        _renderer?.Dispose();
        DestroyNativeEglWindow();
        if (_window != null)
            _sdl.DestroyWindow(_window);
        _disposed = true;
    }

    private KmsEglWindow CreateNativeEglWindow()
    {
        var canPresent = () => Volatile.Read(ref _visible) != 0;
        if (OperatingSystem.IsMacOS())
        {
            _metalView = _sdl.MetalCreateView(_window);
            var layer = _metalView == null ? 0 : (nint)_sdl.MetalGetLayer(_metalView);
            if (layer == 0) throw new InvalidOperationException($"SDL Metal view failed: {GetError()}");
            return new KmsEglWindow(layer, CanPresent: canPresent);
        }

        SysWMInfo wm = default;
        _sdl.GetVersion(&wm.Version);
        if (!_sdl.GetWindowWMInfo(_window, &wm))
            throw new InvalidOperationException($"SDL native window lookup failed: {GetError()}");
        if (wm.Subsystem == SysWMType.Windows)
            return new KmsEglWindow(wm.Info.Win.Hwnd, CanPresent: canPresent);
        if (wm.Subsystem == SysWMType.X11)
            return new KmsEglWindow((nint)wm.Info.X11.Window, (nint)wm.Info.X11.Display, 0x31D5, canPresent);
        if (wm.Subsystem == SysWMType.Wayland)
        {
            _waylandLibrary = NativeLibrary.Load("libwayland-egl.so.1");
            var create = (delegate* unmanaged[Cdecl]<void*, int, int, nint>)
                NativeLibrary.GetExport(_waylandLibrary, "wl_egl_window_create");
            _waylandWindow = create(wm.Info.Wayland.Surface, Width, Height);
            if (_waylandWindow == 0) throw new InvalidOperationException("Could not create the native Wayland EGL window.");
            return new KmsEglWindow(_waylandWindow, (nint)wm.Info.Wayland.Display, 0x31D8, canPresent);
        }
        throw new InvalidOperationException($"SDL window system {wm.Subsystem} has no ANGLE window adapter.");
    }

    private void DestroyNativeEglWindow()
    {
        if (_metalView != null) _sdl.MetalDestroyView(_metalView);
        if (_waylandLibrary == 0) return;
        if (_waylandWindow != 0)
        {
            var destroy = (delegate* unmanaged[Cdecl]<nint, void>)
                NativeLibrary.GetExport(_waylandLibrary, "wl_egl_window_destroy");
            destroy(_waylandWindow);
        }
        NativeLibrary.Free(_waylandLibrary);
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
        return WaylandKeyboardLayout.TryGetBySdlScancode(scancode, out WaylandKeyboardKeyDescriptor descriptor)
            ? descriptor.EvdevKey
            : null;
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
        {
            flags = RendererFlags.Software;
            _renderer = _sdl.CreateRenderer(window, -1, (uint)flags);
        }

        if (_renderer == null)
            throw new InvalidOperationException($"SDL create renderer failed: {GetError()}");

        SetLogicalSize(logicalWidth, logicalHeight);

        RendererInfo info = default;
        uint rendererFlags = _sdl.GetRendererInfo(_renderer, &info) == 0 ? info.Flags : 0u;
        Kind = (rendererFlags & (uint)RendererFlags.Accelerated) != 0
            ? DisplayRendererKind.Accelerated
            : ((flags & RendererFlags.Accelerated) != 0 ? DisplayRendererKind.Accelerated : DisplayRendererKind.Software);

        _logger.LogInformation("Opened SDL renderer kind={Kind} vsync={VSync}", Kind,
            (rendererFlags & (uint)RendererFlags.Presentvsync) != 0);
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

        try
        {
            bool useStraightAlpha = false;
            if (descriptor.Format == DisplayPixelFormat.Argb8888)
            {
                var premultipliedBlend = _sdl.ComposeCustomBlendMode(
                    BlendFactor.One, BlendFactor.OneMinusSrcAlpha, BlendOperation.Add,
                    BlendFactor.One, BlendFactor.OneMinusSrcAlpha, BlendOperation.Add);
                // SDL's software renderer only supports the standard blend modes.
                useStraightAlpha = _sdl.SetTextureBlendMode(texture, premultipliedBlend) != 0;
                if (useStraightAlpha)
                    ThrowIfFailed(_sdl.SetTextureBlendMode(texture, BlendMode.Blend), "set texture blend mode");
            }
            else
                ThrowIfFailed(_sdl.SetTextureBlendMode(texture, BlendMode.None), "set texture blend mode");
            return new SdlDisplayTexture(_sdl, texture, descriptor, useStraightAlpha);
        }
        catch
        {
            _sdl.DestroyTexture(texture);
            throw;
        }
    }

    public void Clear(DisplayColor color)
    {
        ThrowIfFailed(_sdl.SetRenderDrawColor(_renderer, color.R, color.G, color.B, color.A), "set render draw color");
        ThrowIfFailed(_sdl.RenderClear(_renderer), "render clear");
    }

    public void FillRect(DisplayRect rect, DisplayColor color)
    {
        ThrowIfFailed(_sdl.SetRenderDrawColor(_renderer, color.R, color.G, color.B, color.A), "set render draw color");
        Rectangle<int> sdlRect = new(rect.X, rect.Y, rect.Width, rect.Height);
        ThrowIfFailed(_sdl.RenderFillRect(_renderer, &sdlRect), "render fill rect");
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
    private readonly bool _useStraightAlpha;
    private bool _disposed;

    public SdlDisplayTexture(Sdl sdl, Texture* texture, DisplayTextureDescriptor descriptor, bool useStraightAlpha)
    {
        _sdl = sdl;
        _useStraightAlpha = useStraightAlpha;
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
        if (!_useStraightAlpha)
        {
            Upload(rect, pixels, pitch);
            return;
        }

        int rowBytes = checked(rect.Width * 4);
        int bytes = checked(rowBytes * rect.Height);
        byte[] rented = ArrayPool<byte>.Shared.Rent(bytes);
        try
        {
            for (int y = 0; y < rect.Height; y++)
            {
                var source = pixels.Slice(y * pitch, rowBytes);
                var target = rented.AsSpan(y * rowBytes, rowBytes);
                for (int x = 0; x < rowBytes; x += 4)
                {
                    // ARGB8888 is laid out B, G, R, A in these little-endian uploads.
                    byte alpha = target[x + 3] = source[x + 3];
                    for (int component = 0; component < 3; component++)
                        target[x + component] = alpha == 0 ? (byte)0 :
                            (byte)Math.Min(255, (source[x + component] * 255 + alpha / 2) / alpha);
                }
            }
            Upload(rect, rented.AsSpan(0, bytes), rowBytes);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private void Upload(DisplayRect rect, ReadOnlySpan<byte> pixels, int pitch)
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
