using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Podish.Cli.Display;
using Podish.Display;
using Podish.Wayland;
using SkiaSharp;

namespace Podish.Cli.Wayland;

internal sealed class WaylandSdlDisplayHost : IDisposable
{
    private readonly WaylandDesktopOptions _desktopOptions;
    private readonly ILogger _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Dictionary<ulong, SurfaceTextureState> _surfaces = [];
    private readonly WaylandUiTheme _theme;
    private readonly List<ulong> _zOrder = [];
    private SdlDisplayBackend? _backend;
    private IDisplayTexture? _closeButtonIcon;
    private IDisplayTexture? _maximizeButtonIcon;
    private IDisplayTexture? _minimizeButtonIcon;
    private IDisplayOutput? _output;
    private IDisplayRenderer? _renderer;
    private SKTypeface? _titleTypeface;

    public WaylandSdlDisplayHost(ILoggerFactory loggerFactory, WaylandDesktopOptions desktopOptions)
    {
        _loggerFactory = loggerFactory;
        _desktopOptions = desktopOptions;
        _theme = desktopOptions.Theme;
        _logger = loggerFactory.CreateLogger<WaylandSdlDisplayHost>();
    }

    public bool IsClosed => _output?.IsClosed ?? false;

    public void Dispose()
    {
        foreach (var surface in _surfaces.Values)
        {
            surface.Texture?.Dispose();
            surface.TitleTexture?.Dispose();
        }

        _surfaces.Clear();
        _zOrder.Clear();
        _closeButtonIcon?.Dispose();
        _maximizeButtonIcon?.Dispose();
        _minimizeButtonIcon?.Dispose();
        _titleTypeface?.Dispose();
        _output?.ClearCursor();
        _output?.Dispose();
        _output = null;
        _renderer = null;
        _backend?.Dispose();
        _backend = null;
    }

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
            true));
        _renderer = _output.Renderer;
        _logger.LogInformation("Wayland SDL host started desktop={Width}x{Height}", _desktopOptions.Width,
            _desktopOptions.Height);
        EnsureButtonTextures();
    }

    public bool DrainCommands(IEnumerable<WaylandDisplayCommand> commands, List<ulong> consumedLeases,
        out bool shutdownRequested)
    {
        EnsureStarted();
        shutdownRequested = false;
        var dirty = false;

        foreach (var command in commands)
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
                case WaylandDisplayCommandKind.UpdateTextInput:
                    UpdateTextInput(!command.Hidden, command.TextInputRect);
                    break;
                case WaylandDisplayCommandKind.ClearCursor:
                    ClearCursor();
                    break;
                case WaylandDisplayCommandKind.Shutdown:
                    shutdownRequested = true;
                    break;
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
        _logger.LogDebug("Wayland SDL host render surfaces={Count} zOrder={ZOrderCount}", _surfaces.Count,
            _zOrder.Count);
        _renderer!.Clear(ToDisplayColor(_theme.Desktop.Background));

        foreach (var sceneSurfaceId in _zOrder)
        {
            if (!_surfaces.TryGetValue(sceneSurfaceId, out var surface))
                continue;
            if (surface.Hidden)
            {
                _logger.LogDebug("Wayland SDL host skip hidden sceneSurfaceId={SceneSurfaceId}", sceneSurfaceId);
                continue;
            }

            if (surface.Texture == null)
            {
                _logger.LogDebug("Wayland SDL host skip missing texture sceneSurfaceId={SceneSurfaceId}",
                    sceneSurfaceId);
                continue;
            }

            DrawDecoration(surface);
            _renderer.Blit(surface.Texture, destination: ToDisplayRect(surface.Bounds));
        }

        _output!.Present();
    }

    private void UpsertSurface(ulong sceneSurfaceId, WaylandShmFrame frame, WaylandSurfaceBounds bounds)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Stride <= 0)
            throw new InvalidOperationException(
                $"Invalid Wayland shm frame geometry: {frame.Width}x{frame.Height} stride={frame.Stride}.");

        var pixelFormat = frame.Format switch
        {
            WlShmFormat.Argb8888 => DisplayPixelFormat.Argb8888,
            WlShmFormat.Xrgb8888 => DisplayPixelFormat.Xrgb8888,
            _ => throw new NotSupportedException($"Unsupported wl_shm format {frame.Format}.")
        };

        var state = EnsureSurfaceTexture(sceneSurfaceId, frame.Width, frame.Height, pixelFormat);
        UpdateTexture(sceneSurfaceId, state.Texture!, frame);
        state.Width = frame.Width;
        state.Height = frame.Height;
        state.Format = pixelFormat;
        state.Bounds = bounds;
        _zOrder.Remove(sceneSurfaceId);
        _zOrder.Add(sceneSurfaceId);
    }

    private void UpdateSurfaceBounds(ulong sceneSurfaceId, WaylandSurfaceBounds bounds)
    {
        var state = EnsureSurfaceState(sceneSurfaceId);
        state.Bounds = bounds;
        InvalidateTitleTexture(state);
    }

    private void UpdateDecoration(ulong sceneSurfaceId, WaylandDecorationSceneState decoration)
    {
        var state = EnsureSurfaceState(sceneSurfaceId);
        state.Decoration = decoration;
        InvalidateTitleTexture(state);
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
        if (!_surfaces.Remove(sceneSurfaceId, out var surface))
            return;

        _zOrder.Remove(sceneSurfaceId);
        surface.TitleTexture?.Dispose();
        surface.Texture?.Dispose();
    }

    private SurfaceTextureState EnsureSurfaceTexture(ulong sceneSurfaceId, int width, int height,
        DisplayPixelFormat format)
    {
        EnsureStarted();
        if (_surfaces.TryGetValue(sceneSurfaceId, out var existing) &&
            existing.Texture != null &&
            existing.Width == width &&
            existing.Height == height &&
            existing.Format == format)
            return existing;

        var state = EnsureSurfaceState(sceneSurfaceId);
        state.Texture?.Dispose();
        state.Texture = _renderer!.CreateTexture(new DisplayTextureDescriptor(width, height, format));
        state.Width = width;
        state.Height = height;
        state.Format = format;
        return state;
    }

    private static DisplayRect ToDisplayRect(WaylandSurfaceBounds bounds)
    {
        return new DisplayRect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    private void DrawDecoration(SurfaceTextureState surface)
    {
        if (!surface.Decoration.Visible || surface.Decoration.Minimized)
            return;

        var windowBounds = WaylandDecorationLayout.GetWindowBounds(surface.Bounds, surface.Decoration, _theme);
        var chrome = surface.Decoration.Active
            ? _theme.WindowDecoration.Active
            : _theme.WindowDecoration.Inactive;

        _renderer!.FillRect(ToDisplayRect(windowBounds),
            ToDisplayColor(surface.Decoration.Active ? chrome.BorderActive : chrome.Border));
        _renderer.FillRect(ToDisplayRect(WaylandDecorationLayout.GetTitlebarBounds(windowBounds, _theme)),
            ToDisplayColor(chrome.Background));

        var minimizeRect = ToDisplayRect(WaylandDecorationLayout.GetMinimizeButtonBounds(windowBounds, _theme));
        var maximizeRect = ToDisplayRect(WaylandDecorationLayout.GetMaximizeButtonBounds(windowBounds, _theme));
        var closeRect = ToDisplayRect(WaylandDecorationLayout.GetCloseButtonBounds(windowBounds, _theme));
        _renderer.FillRect(minimizeRect, ToDisplayColor(_theme.WindowDecoration.Buttons.BackgroundMinimize));
        _renderer.FillRect(maximizeRect, ToDisplayColor(_theme.WindowDecoration.Buttons.BackgroundMaximize));
        _renderer.FillRect(closeRect, ToDisplayColor(_theme.WindowDecoration.Buttons.BackgroundClose));
        DrawTitle(surface, windowBounds, chrome);
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
            var descriptor = ReadCursor(cursor);
            _output!.SetCursor(descriptor);
        }
        catch (Exception ex)
        {
            _loggerFactory.CreateLogger<WaylandSdlDisplayHost>()
                .LogWarning(ex,
                    "Failed to bridge Wayland cursor sceneSurfaceId={SceneSurfaceId}; ignoring cursor update",
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

    private void UpdateTextInput(bool enabled, WaylandRect? rect)
    {
        EnsureStarted();
        if (!enabled)
        {
            _output!.StopTextInput();
            return;
        }

        if (rect is WaylandRect cursorRect)
            _output!.SetTextInputRect(new DisplayRect(cursorRect.X, cursorRect.Y, cursorRect.Width, cursorRect.Height));

        _output!.StartTextInput();
    }

    private void EnsureStarted()
    {
        if (_output == null || _renderer == null)
            throw new InvalidOperationException("SDL display host has not been started.");
    }

    private SurfaceTextureState EnsureSurfaceState(ulong sceneSurfaceId)
    {
        if (_surfaces.TryGetValue(sceneSurfaceId, out var existing))
            return existing;

        var state = new SurfaceTextureState(null, 1, 1, DisplayPixelFormat.Argb8888);
        _surfaces[sceneSurfaceId] = state;
        return state;
    }

    private void EnsureButtonTextures()
    {
        if (_renderer == null || _closeButtonIcon != null)
            return;

        var size = _theme.Spacing.ButtonSize;
        _closeButtonIcon = CreateButtonIconTexture(size, DrawCloseIcon);
        _maximizeButtonIcon = CreateButtonIconTexture(size, DrawMaximizeIcon);
        _minimizeButtonIcon = CreateButtonIconTexture(size, DrawMinimizeIcon);
    }

    private void DrawTitle(SurfaceTextureState surface, WaylandSurfaceBounds windowBounds,
        WaylandSurfaceChromeTheme chrome)
    {
        if (string.IsNullOrWhiteSpace(surface.Decoration.Title))
            return;

        var titleBounds = WaylandDecorationLayout.GetTitleTextBounds(windowBounds, _theme);
        if (titleBounds.Width <= 0 || titleBounds.Height <= 0)
            return;

        EnsureTitleTexture(surface, titleBounds, chrome.Foreground);
        if (surface.TitleTexture != null)
            _renderer!.Blit(surface.TitleTexture, destination: ToDisplayRect(titleBounds));
    }

    private void EnsureTitleTexture(SurfaceTextureState surface, WaylandSurfaceBounds titleBounds, WaylandColor color)
    {
        var title = surface.Decoration.Title.Trim();
        if (surface.TitleTexture != null &&
            surface.CachedTitle == title &&
            surface.CachedTitleBounds == titleBounds &&
            surface.CachedTitleColor == color)
            return;

        InvalidateTitleTexture(surface);
        surface.CachedTitle = title;
        surface.CachedTitleBounds = titleBounds;
        surface.CachedTitleColor = color;

        if (string.IsNullOrEmpty(title))
            return;

        var width = Math.Max(1, titleBounds.Width);
        var height = Math.Max(1, titleBounds.Height);
        var pixels = RasterizeTitle(title, width, height, color);
        var texture = _renderer!.CreateTexture(new DisplayTextureDescriptor(
            width,
            height,
            DisplayPixelFormat.Argb8888));
        texture.Update(pixels, width * 4);
        surface.TitleTexture = texture;
    }

    private void InvalidateTitleTexture(SurfaceTextureState surface)
    {
        surface.TitleTexture?.Dispose();
        surface.TitleTexture = null;
        surface.CachedTitle = string.Empty;
        surface.CachedTitleBounds = default;
        surface.CachedTitleColor = default;
    }

    private byte[] RasterizeTitle(string title, int width, int height, WaylandColor color)
    {
        var pixels = GC.AllocateUninitializedArray<byte>(width * height * 4);
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        using var paint = new SKPaint
        {
            IsAntialias = true,
            SubpixelText = true,
            Color = new SKColor(color.R, color.G, color.B, color.A),
            TextSize = _theme.Typography.TitleFontSize,
            Typeface = GetTitleTypeface(title)
        };

        var fittedTitle = EllipsizeTitle(title, paint, width);
        paint.GetFontMetrics(out var metrics);
        var baseline = (height - (metrics.Descent - metrics.Ascent)) * 0.5f - metrics.Ascent;
        canvas.DrawText(fittedTitle, 0, baseline, paint);
        canvas.Flush();
        Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
        return pixels;
    }

    private SKTypeface GetTitleTypeface(string title)
    {
        if (_titleTypeface != null)
            return _titleTypeface;

        var fontManager = SKFontManager.Default;
        var candidate = title.FirstOrDefault(c => !char.IsWhiteSpace(c) && c > 127);
        _titleTypeface = candidate != default
            ? fontManager.MatchCharacter(candidate)
            : fontManager.MatchCharacter('中');
        _titleTypeface ??= SKTypeface.Default;
        return _titleTypeface;
    }

    private static string EllipsizeTitle(string title, SKPaint paint, int maxWidth)
    {
        if (string.IsNullOrEmpty(title) || paint.MeasureText(title) <= maxWidth)
            return title;

        const string ellipsis = "…";
        if (paint.MeasureText(ellipsis) > maxWidth)
            return string.Empty;

        for (var length = title.Length - 1; length >= 0; length--)
        {
            var candidate = string.Concat(title.AsSpan(0, length), ellipsis);
            if (paint.MeasureText(candidate) <= maxWidth)
                return candidate;
        }

        return ellipsis;
    }

    private IDisplayTexture CreateButtonIconTexture(int size, Action<Span<byte>, int, int, WaylandColor, int> draw)
    {
        var texture = _renderer!.CreateTexture(new DisplayTextureDescriptor(size, size, DisplayPixelFormat.Argb8888));
        var pixels = new byte[size * size * 4];
        draw(pixels, size, size * 4, _theme.WindowDecoration.Buttons.Foreground, _theme.Spacing.IconStrokeWidth);
        texture.Update(pixels, size * 4);
        return texture;
    }

    private static void DrawCloseIcon(Span<byte> pixels, int size, int pitch, WaylandColor color, int stroke)
    {
        for (var i = 3; i < size - 3; i++)
        {
            DrawDot(pixels, pitch, i, i, stroke, color);
            DrawDot(pixels, pitch, size - 1 - i, i, stroke, color);
        }
    }

    private static void DrawMaximizeIcon(Span<byte> pixels, int size, int pitch, WaylandColor color, int stroke)
    {
        for (var x = 4; x < size - 4; x++)
        {
            DrawDot(pixels, pitch, x, 4, stroke, color);
            DrawDot(pixels, pitch, x, size - 5, stroke, color);
        }

        for (var y = 4; y < size - 4; y++)
        {
            DrawDot(pixels, pitch, 4, y, stroke, color);
            DrawDot(pixels, pitch, size - 5, y, stroke, color);
        }
    }

    private static void DrawMinimizeIcon(Span<byte> pixels, int size, int pitch, WaylandColor color, int stroke)
    {
        for (var x = 4; x < size - 4; x++)
            DrawDot(pixels, pitch, x, size - 6, stroke, color);
    }

    private static void PutPixel(Span<byte> pixels, int pitch, int x, int y, byte r, byte g, byte b, byte a)
    {
        var offset = y * pitch + x * 4;
        pixels[offset] = b;
        pixels[offset + 1] = g;
        pixels[offset + 2] = r;
        pixels[offset + 3] = a;
    }

    private static void DrawDot(Span<byte> pixels, int pitch, int centerX, int centerY, int stroke, WaylandColor color)
    {
        var radius = Math.Max(0, stroke - 1);
        for (var dy = -radius; dy <= radius; dy++)
        for (var dx = -radius; dx <= radius; dx++)
        {
            var x = centerX + dx;
            var y = centerY + dy;
            if (x < 0 || y < 0)
                continue;
            if (y * pitch + x * 4 + 3 >= pixels.Length)
                continue;
            PutPixel(pixels, pitch, x, y, color.R, color.G, color.B, color.A);
        }
    }

    private static DisplayColor ToDisplayColor(WaylandColor color)
    {
        return new DisplayColor(color.R, color.G, color.B, color.A);
    }

    private void UpdateTexture(ulong sceneSurfaceId, IDisplayTexture texture, WaylandShmFrame frame)
    {
        const int maxChunkRows = 64;
        var maxChunkBytes = frame.Stride * Math.Min(frame.Height, maxChunkRows);
        var scratch = ArrayPool<byte>.Shared.Rent(maxChunkBytes);
        try
        {
            for (var y = 0; y < frame.Height; y += maxChunkRows)
            {
                var rows = Math.Min(maxChunkRows, frame.Height - y);
                var chunkBytes = frame.Stride * rows;
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

        var offset = frame.Offset + (long)rowOffset * frame.Stride;
        var written = 0;
        var segments = frame.File.OpenedInode.GetReadableSegments(frame.File, offset, length);
        foreach (var chunk in segments)
        {
            chunk.CopyTo(destination.AsSpan(written, chunk.Length));
            written += chunk.Length;
        }

        if (!segments.Succeeded || written != length)
            throw new InvalidOperationException("Failed to read complete shm frame contents.");
    }

    private static DisplayCursorDescriptor ReadCursor(WaylandCursorFrame cursor)
    {
        var frame = cursor.Frame;
        var byteLength = checked(frame.Stride * frame.Height);
        var pixels = GC.AllocateUninitializedArray<byte>(byteLength);
        ReadExactly(frame, 0, pixels, byteLength);

        if (frame.Format == WlShmFormat.Xrgb8888)
            for (var i = 0; i < byteLength; i += 4)
                pixels[i + 3] = 0xFF;

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
        public IDisplayTexture? TitleTexture { get; set; }
        public int Width { get; set; } = width;
        public int Height { get; set; } = height;
        public DisplayPixelFormat Format { get; set; } = format;
        public WaylandSurfaceBounds Bounds { get; set; }
        public WaylandDecorationSceneState Decoration { get; set; }
        public bool Hidden { get; set; }
        public string CachedTitle { get; set; } = string.Empty;
        public WaylandSurfaceBounds CachedTitleBounds { get; set; }
        public WaylandColor CachedTitleColor { get; set; }
    }
}