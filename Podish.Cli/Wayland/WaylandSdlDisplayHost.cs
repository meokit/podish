using System.Buffers;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;
using Podish.Cli.Display;
using Podish.Display;
using Podish.Wayland;

namespace Podish.Cli.Wayland;

internal sealed class WaylandSdlDisplayHost : IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly WaylandDesktopOptions _desktopOptions;
    private readonly WaylandUiTheme _theme;
    private SdlDisplayBackend? _backend;
    private IDisplayOutput? _output;
    private IDisplayRenderer? _renderer;
    private IDisplayTexture? _closeButtonIcon;
    private IDisplayTexture? _maximizeButtonIcon;
    private IDisplayTexture? _minimizeButtonIcon;
    private readonly Dictionary<ulong, SurfaceTextureState> _surfaces = [];
    private readonly List<ulong> _zOrder = [];

    public WaylandSdlDisplayHost(ILoggerFactory loggerFactory, WaylandDesktopOptions desktopOptions)
    {
        _loggerFactory = loggerFactory;
        _desktopOptions = desktopOptions;
        _theme = desktopOptions.Theme;
    }

    public bool IsClosed => _output?.IsClosed ?? false;

    public void Start()
    {
        if (_output != null)
            return;

        _backend = new SdlDisplayBackend(_loggerFactory.CreateLogger<SdlDisplayBackend>());
        _output = _backend.CreateOutput(new DisplayOutputOptions(
            _desktopOptions.Width,
            _desktopOptions.Height,
            "Podish Wayland",
            DisplayVSyncMode.Enabled,
            AllowHighDpi: true,
            Resizable: true));
        _renderer = _output.Renderer;
        EnsureButtonTextures();
    }

    public bool DrainCommands(IEnumerable<WaylandDisplayCommand> commands, List<ulong> consumedLeases, out bool shutdownRequested)
    {
        EnsureStarted();
        shutdownRequested = false;
        bool dirty = false;

        foreach (WaylandDisplayCommand command in commands)
        {
            switch (command.Kind)
            {
                case WaylandDisplayCommandKind.PresentSurface:
                    dirty = true;
                    UpsertSurface(command.SceneSurfaceId, command.Frame, command.Bounds);
                    consumedLeases.Add(command.Frame.LeaseToken);
                    break;
                case WaylandDisplayCommandKind.UpdateSurfaceBounds:
                    dirty = true;
                    UpdateSurfaceBounds(command.SceneSurfaceId, command.Bounds);
                    break;
                case WaylandDisplayCommandKind.UpdateDecoration:
                    dirty = true;
                    UpdateDecoration(command.SceneSurfaceId, command.Decoration);
                    break;
                case WaylandDisplayCommandKind.SetSurfaceVisibility:
                    dirty = true;
                    SetSurfaceVisibility(command.SceneSurfaceId, command.Hidden);
                    break;
                case WaylandDisplayCommandKind.RaiseSurface:
                    dirty = true;
                    RaiseSurface(command.SceneSurfaceId);
                    break;
                case WaylandDisplayCommandKind.RemoveSurface:
                    dirty = true;
                    RemoveSurface(command.SceneSurfaceId);
                    break;
                case WaylandDisplayCommandKind.SetCursor:
                    SetCursor(command.Cursor);
                    break;
                case WaylandDisplayCommandKind.SetSystemCursor:
                    if (command.SystemCursor is WaylandSystemCursorShape shape)
                        SetSystemCursor(shape);
                    else
                        ClearCursor();
                    break;
                case WaylandDisplayCommandKind.ClearCursor:
                    ClearCursor();
                    break;
                case WaylandDisplayCommandKind.Shutdown:
                    shutdownRequested = true;
                    break;
            }
        }

        return dirty;
    }

    public IReadOnlyList<DisplayInputEvent> PumpInputEvents()
    {
        EnsureStarted();
        _output!.PumpEvents();
        return _output.DrainInputEvents();
    }

    public bool TryDequeueResize(out DisplaySize size)
    {
        EnsureStarted();
        return _output!.TryDequeueResize(out size);
    }

    public void Render()
    {
        EnsureStarted();
        _renderer!.Clear(ToDisplayColor(_theme.Desktop.Background));

        foreach (ulong sceneSurfaceId in _zOrder)
        {
            if (!_surfaces.TryGetValue(sceneSurfaceId, out SurfaceTextureState? surface))
                continue;
            if (surface.Hidden)
                continue;
            if (surface.Texture == null)
                continue;

            DrawDecoration(surface);
            _renderer.Blit(surface.Texture, destination: ToDisplayRect(surface.Bounds));
        }

        _output!.Present();
    }

    public void Dispose()
    {
        foreach (SurfaceTextureState surface in _surfaces.Values)
            surface.Texture?.Dispose();
        _surfaces.Clear();
        _zOrder.Clear();
        _closeButtonIcon?.Dispose();
        _maximizeButtonIcon?.Dispose();
        _minimizeButtonIcon?.Dispose();
        _output?.ClearCursor();
        _output?.Dispose();
        _output = null;
        _renderer = null;
        _backend?.Dispose();
        _backend = null;
    }

    private void UpsertSurface(ulong sceneSurfaceId, WaylandShmFrame frame, WaylandSurfaceBounds bounds)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Stride <= 0)
            throw new InvalidOperationException(
                $"Invalid Wayland shm frame geometry: {frame.Width}x{frame.Height} stride={frame.Stride}.");

        DisplayPixelFormat pixelFormat = frame.Format switch
        {
            WlShmFormat.Argb8888 => DisplayPixelFormat.Argb8888,
            WlShmFormat.Xrgb8888 => DisplayPixelFormat.Xrgb8888,
            _ => throw new NotSupportedException($"Unsupported wl_shm format {frame.Format}.")
        };

        SurfaceTextureState state = EnsureSurfaceTexture(sceneSurfaceId, frame.Width, frame.Height, pixelFormat);
        UpdateTexture(state.Texture!, frame);
        state.Width = frame.Width;
        state.Height = frame.Height;
        state.Format = pixelFormat;
        state.Bounds = bounds;

        _zOrder.Remove(sceneSurfaceId);
        _zOrder.Add(sceneSurfaceId);
    }

    private void UpdateSurfaceBounds(ulong sceneSurfaceId, WaylandSurfaceBounds bounds)
    {
        EnsureSurfaceState(sceneSurfaceId).Bounds = bounds;
    }

    private void UpdateDecoration(ulong sceneSurfaceId, WaylandDecorationSceneState decoration)
    {
        EnsureSurfaceState(sceneSurfaceId).Decoration = decoration;
    }

    private void SetSurfaceVisibility(ulong sceneSurfaceId, bool hidden)
    {
        EnsureSurfaceState(sceneSurfaceId).Hidden = hidden;
    }

    private void RaiseSurface(ulong sceneSurfaceId)
    {
        if (!_surfaces.ContainsKey(sceneSurfaceId))
            return;

        _zOrder.Remove(sceneSurfaceId);
        _zOrder.Add(sceneSurfaceId);
    }

    private void RemoveSurface(ulong sceneSurfaceId)
    {
        if (!_surfaces.Remove(sceneSurfaceId, out SurfaceTextureState? surface))
            return;

        _zOrder.Remove(sceneSurfaceId);
        surface.Texture?.Dispose();
    }

    private SurfaceTextureState EnsureSurfaceTexture(ulong sceneSurfaceId, int width, int height,
        DisplayPixelFormat format)
    {
        EnsureStarted();
        if (_surfaces.TryGetValue(sceneSurfaceId, out SurfaceTextureState? existing) &&
            existing.Texture != null &&
            existing.Width == width &&
            existing.Height == height &&
            existing.Format == format)
            return existing;

        SurfaceTextureState state = EnsureSurfaceState(sceneSurfaceId);
        state.Texture?.Dispose();
        state.Texture = _renderer!.CreateTexture(new DisplayTextureDescriptor(width, height, format, DisplayTextureAccess.Streaming));
        state.Width = width;
        state.Height = height;
        state.Format = format;
        return state;
    }

    private static DisplayRect ToDisplayRect(WaylandSurfaceBounds bounds) =>
        new(bounds.X, bounds.Y, bounds.Width, bounds.Height);

    private void DrawDecoration(SurfaceTextureState surface)
    {
        if (!surface.Decoration.Visible || surface.Decoration.Minimized)
            return;

        WaylandSurfaceBounds windowBounds = WaylandDecorationLayout.GetWindowBounds(surface.Bounds, surface.Decoration, _theme);
        WaylandSurfaceChromeTheme chrome = surface.Decoration.Active
            ? _theme.WindowDecoration.Active
            : _theme.WindowDecoration.Inactive;

        _renderer!.FillRect(ToDisplayRect(windowBounds), ToDisplayColor(surface.Decoration.Active ? chrome.BorderActive : chrome.Border));
        _renderer.FillRect(ToDisplayRect(WaylandDecorationLayout.GetTitlebarBounds(windowBounds, _theme)), ToDisplayColor(chrome.Background));

        DisplayRect minimizeRect = ToDisplayRect(WaylandDecorationLayout.GetMinimizeButtonBounds(windowBounds, _theme));
        DisplayRect maximizeRect = ToDisplayRect(WaylandDecorationLayout.GetMaximizeButtonBounds(windowBounds, _theme));
        DisplayRect closeRect = ToDisplayRect(WaylandDecorationLayout.GetCloseButtonBounds(windowBounds, _theme));
        _renderer.FillRect(minimizeRect, ToDisplayColor(_theme.WindowDecoration.Buttons.BackgroundMinimize));
        _renderer.FillRect(maximizeRect, ToDisplayColor(_theme.WindowDecoration.Buttons.BackgroundMaximize));
        _renderer.FillRect(closeRect, ToDisplayColor(_theme.WindowDecoration.Buttons.BackgroundClose));
        if (_minimizeButtonIcon != null)
            _renderer.Blit(_minimizeButtonIcon, destination: minimizeRect);
        if (_maximizeButtonIcon != null)
            _renderer.Blit(_maximizeButtonIcon, destination: maximizeRect);
        if (_closeButtonIcon != null)
            _renderer.Blit(_closeButtonIcon, destination: closeRect);
    }

    private void SetCursor(WaylandCursorFrame cursor)
    {
        EnsureStarted();
        try
        {
            DisplayCursorDescriptor descriptor = ReadCursor(cursor);
            _output!.SetCursor(descriptor);
        }
        catch (Exception ex)
        {
            _loggerFactory.CreateLogger<WaylandSdlDisplayHost>()
                .LogWarning(ex, "Failed to bridge Wayland cursor sceneSurfaceId={SceneSurfaceId}; ignoring cursor update",
                    cursor.SceneSurfaceId);
        }
    }

    private void ClearCursor()
    {
        EnsureStarted();
        _output!.ClearCursor();
    }

    private void SetSystemCursor(WaylandSystemCursorShape shape)
    {
        EnsureStarted();
        _output!.SetSystemCursor(shape switch
        {
            WaylandSystemCursorShape.Default => DisplaySystemCursor.Arrow,
            WaylandSystemCursorShape.Text => DisplaySystemCursor.IBeam,
            WaylandSystemCursorShape.Pointer => DisplaySystemCursor.Hand,
            WaylandSystemCursorShape.Crosshair => DisplaySystemCursor.Crosshair,
            WaylandSystemCursorShape.Move => DisplaySystemCursor.SizeAll,
            WaylandSystemCursorShape.EwResize => DisplaySystemCursor.SizeWe,
            WaylandSystemCursorShape.NsResize => DisplaySystemCursor.SizeNs,
            WaylandSystemCursorShape.NwseResize => DisplaySystemCursor.SizeNwse,
            WaylandSystemCursorShape.NeswResize => DisplaySystemCursor.SizeNesw,
            WaylandSystemCursorShape.NotAllowed => DisplaySystemCursor.No,
            WaylandSystemCursorShape.Wait => DisplaySystemCursor.Wait,
            WaylandSystemCursorShape.Progress => DisplaySystemCursor.WaitArrow,
            WaylandSystemCursorShape.Grab => DisplaySystemCursor.Hand,
            WaylandSystemCursorShape.Grabbing => DisplaySystemCursor.SizeAll,
            WaylandSystemCursorShape.Help => DisplaySystemCursor.Arrow,
            _ => DisplaySystemCursor.Arrow
        });
    }

    private void EnsureStarted()
    {
        if (_output == null || _renderer == null)
            throw new InvalidOperationException("SDL display host has not been started.");
    }

    private SurfaceTextureState EnsureSurfaceState(ulong sceneSurfaceId)
    {
        if (_surfaces.TryGetValue(sceneSurfaceId, out SurfaceTextureState? existing))
            return existing;

        var state = new SurfaceTextureState(null, 1, 1, DisplayPixelFormat.Argb8888);
        _surfaces[sceneSurfaceId] = state;
        return state;
    }

    private void EnsureButtonTextures()
    {
        if (_renderer == null || _closeButtonIcon != null)
            return;

        int size = _theme.Spacing.ButtonSize;
        _closeButtonIcon = CreateButtonIconTexture(size, DrawCloseIcon);
        _maximizeButtonIcon = CreateButtonIconTexture(size, DrawMaximizeIcon);
        _minimizeButtonIcon = CreateButtonIconTexture(size, DrawMinimizeIcon);
    }

    private IDisplayTexture CreateButtonIconTexture(int size, Action<Span<byte>, int, int, WaylandColor, int> draw)
    {
        IDisplayTexture texture = _renderer!.CreateTexture(new DisplayTextureDescriptor(size, size, DisplayPixelFormat.Argb8888, DisplayTextureAccess.Streaming));
        byte[] pixels = new byte[size * size * 4];
        draw(pixels, size, size * 4, _theme.WindowDecoration.Buttons.Foreground, _theme.Spacing.IconStrokeWidth);
        texture.Update(pixels, size * 4);
        return texture;
    }

    private static void DrawCloseIcon(Span<byte> pixels, int size, int pitch, WaylandColor color, int stroke)
    {
        for (int i = 3; i < size - 3; i++)
        {
            DrawDot(pixels, pitch, i, i, stroke, color);
            DrawDot(pixels, pitch, size - 1 - i, i, stroke, color);
        }
    }

    private static void DrawMaximizeIcon(Span<byte> pixels, int size, int pitch, WaylandColor color, int stroke)
    {
        for (int x = 4; x < size - 4; x++)
        {
            DrawDot(pixels, pitch, x, 4, stroke, color);
            DrawDot(pixels, pitch, x, size - 5, stroke, color);
        }

        for (int y = 4; y < size - 4; y++)
        {
            DrawDot(pixels, pitch, 4, y, stroke, color);
            DrawDot(pixels, pitch, size - 5, y, stroke, color);
        }
    }

    private static void DrawMinimizeIcon(Span<byte> pixels, int size, int pitch, WaylandColor color, int stroke)
    {
        for (int x = 4; x < size - 4; x++)
            DrawDot(pixels, pitch, x, size - 6, stroke, color);
    }

    private static void PutPixel(Span<byte> pixels, int pitch, int x, int y, byte r, byte g, byte b, byte a)
    {
        int offset = y * pitch + x * 4;
        pixels[offset] = b;
        pixels[offset + 1] = g;
        pixels[offset + 2] = r;
        pixels[offset + 3] = a;
    }

    private static void DrawDot(Span<byte> pixels, int pitch, int centerX, int centerY, int stroke, WaylandColor color)
    {
        int radius = Math.Max(0, stroke - 1);
        for (int dy = -radius; dy <= radius; dy++)
        for (int dx = -radius; dx <= radius; dx++)
        {
            int x = centerX + dx;
            int y = centerY + dy;
            if (x < 0 || y < 0)
                continue;
            if (y * pitch + x * 4 + 3 >= pixels.Length)
                continue;
            PutPixel(pixels, pitch, x, y, color.R, color.G, color.B, color.A);
        }
    }

    private static DisplayColor ToDisplayColor(WaylandColor color) => new(color.R, color.G, color.B, color.A);

    private static void UpdateTexture(IDisplayTexture texture, WaylandShmFrame frame)
    {
        const int maxChunkRows = 64;
        int maxChunkBytes = frame.Stride * Math.Min(frame.Height, maxChunkRows);
        byte[] scratch = ArrayPool<byte>.Shared.Rent(maxChunkBytes);
        try
        {
            for (int y = 0; y < frame.Height; y += maxChunkRows)
            {
                int rows = Math.Min(maxChunkRows, frame.Height - y);
                int chunkBytes = frame.Stride * rows;
                ReadExactly(frame, y, scratch, chunkBytes);
                texture.Update(new DisplayRect(0, y, frame.Width, rows), scratch.AsSpan(0, chunkBytes), frame.Stride);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    private static void ReadExactly(WaylandShmFrame frame, int rowOffset, byte[] destination, int length)
    {
        if (frame.File.OpenedInode == null)
            throw new InvalidOperationException("Wayland shm file has no inode.");

        long offset = frame.Offset + (long)rowOffset * frame.Stride;
        if (frame.File.OpenedInode is IndexedMemoryInode indexedInode &&
            TryReadIndexedSegments(indexedInode, offset, destination, length))
            return;

        ReadExactly(frame.File, offset, destination.AsSpan(0, length));
    }

    private static bool TryReadIndexedSegments(IndexedMemoryInode indexedInode, long offset, byte[] destination,
        int length)
    {
        int written = 0;
        bool ok = indexedInode.VisitReadSegments(offset, length, (ptr, chunkLength) =>
        {
            unsafe
            {
                new ReadOnlySpan<byte>((void*)ptr, chunkLength).CopyTo(destination.AsSpan(written, chunkLength));
            }

            written += chunkLength;
            return true;
        });

        return ok && written == length;
    }

    private static void ReadExactly(LinuxFile file, long offset, Span<byte> destination)
    {
        if (file.OpenedInode == null)
            throw new InvalidOperationException("Wayland shm file has no inode.");

        int totalRead = 0;
        while (totalRead < destination.Length)
        {
            int read = file.OpenedInode.Read(file, destination[totalRead..], offset + totalRead);
            if (read <= 0)
                throw new InvalidOperationException("Failed to read complete shm frame contents.");
            totalRead += read;
        }
    }

    private static DisplayCursorDescriptor ReadCursor(WaylandCursorFrame cursor)
    {
        WaylandShmFrame frame = cursor.Frame;
        int byteLength = checked(frame.Stride * frame.Height);
        byte[] pixels = GC.AllocateUninitializedArray<byte>(byteLength);
        ReadExactly(frame, 0, pixels, byteLength);

        if (frame.Format == WlShmFormat.Xrgb8888)
        {
            for (int i = 0; i < byteLength; i += 4)
                pixels[i + 3] = 0xFF;
        }

        return new DisplayCursorDescriptor(
            frame.Width,
            frame.Height,
            cursor.HotspotX,
            cursor.HotspotY,
            DisplayPixelFormat.Argb8888,
            pixels,
            frame.Stride);
    }

    private sealed class SurfaceTextureState(IDisplayTexture? texture, int width, int height, DisplayPixelFormat format)
    {
        public IDisplayTexture? Texture { get; set; } = texture;
        public int Width { get; set; } = width;
        public int Height { get; set; } = height;
        public DisplayPixelFormat Format { get; set; } = format;
        public WaylandSurfaceBounds Bounds { get; set; }
        public WaylandDecorationSceneState Decoration { get; set; }
        public bool Hidden { get; set; }
    }
}
