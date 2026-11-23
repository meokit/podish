using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;
using Podish.Cli.Display;
using Podish.Core.Graphics;
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
    private readonly bool _enableGles;
    private readonly List<ulong> _zOrder = [];
    private SdlDisplayBackend? _backend;
    private readonly Dictionary<(WaylandSceneHitKind Kind, WaylandColor Color, WaylandButtonBitmap? Bitmap), IDisplayTexture> _buttonIcons = [];
    private WaylandSceneHit _hoveredDecoration;
    private WaylandSceneHit _pressedDecoration;
    private SdlDisplayOutput? _output;
    private AngleDisplayRenderer? _renderer;
    private ulong _sceneFrameSequence;
    private ulong _scanoutFrameSequence;
    private SKTypeface? _titleTypeface;
    private ulong _textInputSurfaceId;
    private WaylandRect? _textInputRect;
    private DisplaySize _lastPreviewSizeRequest;

    public WaylandSdlDisplayHost(ILoggerFactory loggerFactory, WaylandDesktopOptions desktopOptions,
        bool enableGles)
    {
        _loggerFactory = loggerFactory;
        _desktopOptions = desktopOptions;
        _enableGles = enableGles;
        _theme = desktopOptions.Theme;
        _logger = loggerFactory.CreateLogger<WaylandSdlDisplayHost>();
    }

    public bool IsClosed => _output?.IsClosed ?? false;
    internal KmsEglOutput? GuestTextures => _renderer?.GuestTextures;
    internal bool HasPendingGpuUploads => _renderer?.HasPendingUploads == true;
    internal bool CanImportGpuBuffer(DrmScanoutBuffer buffer) => _renderer?.CanImport(buffer) == true;
    internal VirtualDrmScanout? Scanout { get; private set; }
    internal uint Scale120 { get; private set; } = 120;

    public void Dispose()
    {
        foreach (var surface in _surfaces.Values)
        {
            surface.Texture?.Dispose();
            surface.TitleTexture?.Dispose();
            surface.ApplicationIconTexture?.Dispose();
        }

        _surfaces.Clear();
        _zOrder.Clear();
        foreach (IDisplayTexture texture in _buttonIcons.Values)
            texture.Dispose();
        _buttonIcons.Clear();
        _titleTypeface?.Dispose();
        _renderer?.Dispose();
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
        _backend.ConfigureTextInput();
        _backend.ConfigureHighDpi();
        var output = _backend.CreateEglOutput(new DisplayOutputOptions(
            _desktopOptions.Width,
            _desktopOptions.Height,
            "Podish Wayland (Virtual KMS)",
            DisplayVSyncMode.Disabled,
            AllowHighDpi: true,
            Resizable: false,
            Hidden: true));
        _output = output;
        var pixels = output.GetPixelSize();
        double displayScale = Math.Min((double)pixels.Width / output.Width, (double)pixels.Height / output.Height);
        Scale120 = ResolveOutputScale(displayScale, _desktopOptions.Width, _desktopOptions.Height);
        Scanout = new VirtualDrmScanout(ToPixels(_desktopOptions.Width), ToPixels(_desktopOptions.Height));
        FitPreviewToScanout();
        output.ResizeNativeEglWindow();
        _renderer = AngleDisplayRenderer.Create(output.KmsWindow, Scanout,
            new DisplaySize(_desktopOptions.Width, _desktopOptions.Height), _enableGles, _logger);
        _output.StopTextInput();
        output.Show();
        _logger.LogInformation("Wayland desktop {Width}x{Height} logical, {PixelWidth}x{PixelHeight} scanout, scale={Scale}/120",
            _desktopOptions.Width, _desktopOptions.Height, Scanout.Width, Scanout.Height, Scale120);
    }

    internal static uint ResolveOutputScale(double displayScale, int width, int height, string? scaleOverride = null)
    {
        scaleOverride ??= Environment.GetEnvironmentVariable("PODISH_WAYLAND_SCALE");
        uint limit = Math.Min(480u, (uint)(120 * FramebufferDevice.DimensionLimit / Math.Max(width, height)));
        if (string.IsNullOrWhiteSpace(scaleOverride))
            return (uint)Math.Clamp(Math.Round(displayScale * 120), 120, limit);
        if (!double.TryParse(scaleOverride, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
            !double.IsFinite(value) || value is < 1 or > 4)
            throw new InvalidOperationException("PODISH_WAYLAND_SCALE must be a number between 1 and 4.");

        uint scale = (uint)Math.Round(value * 120, MidpointRounding.AwayFromZero);
        if (scale > limit)
            throw new InvalidOperationException("PODISH_WAYLAND_SCALE would exceed the virtual KMS mode size limit.");
        return scale;
    }

    private int ToPixels(int logical) => (int)((logical * Scale120 + 60) / 120);

    private void FitPreviewToScanout()
    {
        var output = _output!;
        var drawable = output.GetPixelSize();
        if (drawable.Width <= 0 || drawable.Height <= 0)
            return;

        var window = new DisplaySize(output.Width, output.Height);
        var scanout = new DisplaySize(Scanout!.Width, Scanout.Height);
        var requested = PixelMatchedWindowSize(window, drawable, scanout);
        if (requested == window || requested == _lastPreviewSizeRequest)
            return;

        _lastPreviewSizeRequest = requested;
        output.SetWindowSize(requested);
    }

    internal static DisplaySize PixelMatchedWindowSize(DisplaySize window, DisplaySize drawable, DisplaySize scanout)
    {
        static int Fit(int logical, int currentPixels, int targetPixels) =>
            Math.Abs(currentPixels - targetPixels) <= 1 ? logical :
                Math.Max(1, (int)Math.Round((double)logical * targetPixels / currentPixels, MidpointRounding.AwayFromZero));

        return new DisplaySize(Fit(window.Width, drawable.Width, scanout.Width),
            Fit(window.Height, drawable.Height, scanout.Height));
    }

    public bool DrainCommands(IEnumerable<WaylandDisplayCommand> commands, List<ulong> consumedLeases)
    {
        EnsureStarted();
        var dirty = false;

        foreach (var command in commands)
            switch (command.Kind)
            {
                case WaylandDisplayCommandKind.PresentSurface:
                    dirty = true;
                    UpsertSurface(command.SceneSurfaceId, command.Frame, command.Bounds);
                    consumedLeases.Add(command.Frame.LeaseToken);
                    break;
                case WaylandDisplayCommandKind.PresentGpuSurface:
                    if (UpsertGpuSurface(command.SceneSurfaceId, command.GpuFrame, command.Bounds))
                        dirty = true;
                    else
                        consumedLeases.Add(command.GpuFrame.LeaseToken);
                    break;
                case WaylandDisplayCommandKind.UpdateSurfaceBounds:
                    dirty = true;
                    UpdateSurfaceBounds(command.SceneSurfaceId, command.Bounds);
                    break;
                case WaylandDisplayCommandKind.UpdateSurfaceMapping:
                    dirty = true;
                    UpdateSurfaceBounds(command.SceneSurfaceId, command.Bounds);
                    _surfaces[command.SceneSurfaceId].Mapping = command.Mapping;
                    break;
                case WaylandDisplayCommandKind.UpdateDecoration:
                    dirty = true;
                    UpdateDecoration(command.SceneSurfaceId, command.Decoration);
                    break;
                case WaylandDisplayCommandKind.UpdateDecorationPointer:
                    dirty = true;
                    _hoveredDecoration = command.HoveredDecoration;
                    _pressedDecoration = command.PressedDecoration;
                    break;
                case WaylandDisplayCommandKind.SetSurfaceVisibility:
                    dirty = true;
                    SetSurfaceVisibility(command.SceneSurfaceId, command.Hidden);
                    break;
                case WaylandDisplayCommandKind.SetSurfaceLayer:
                    dirty = true;
                    SetSurfaceLayer(command.SceneSurfaceId, command.Layer);
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
                    UpdateTextInput(command.SceneSurfaceId, command.TextInputRect);
                    break;
                case WaylandDisplayCommandKind.ClearCursor:
                    ClearCursor();
                    break;
                case WaylandDisplayCommandKind.ScheduleFrame:
                    _sceneFrameSequence = command.FrameSequence;
                    dirty = true;
                    break;
            }

        if (dirty)
            RefreshTextInputRectangle();

        _renderer!.TakeCompletedUploads(consumedLeases);

        return dirty;
    }

    public IReadOnlyList<DisplayInputEvent> PumpInputEvents()
    {
        EnsureStarted();
        _output!.PumpEvents();
        var events = _output.DrainInputEvents();
        int width = _output.Width, height = _output.Height;
        if (events.Count == 0 || width <= 0 || height <= 0 || (width == _desktopOptions.Width && height == _desktopOptions.Height))
            return events;

        return events.Select(input => input.Kind is DisplayInputEventKind.PointerMotion or DisplayInputEventKind.PointerButton
            ? input with
            {
                X = ScaleCoordinate(input.X, _desktopOptions.Width, width),
                Y = ScaleCoordinate(input.Y, _desktopOptions.Height, height)
            }
            : input).ToArray();
    }

    public bool RefreshOutputSize()
    {
        EnsureStarted();
        if (!_output!.TryDequeueResize(out _))
            return false;

        while (_output.TryDequeueResize(out _)) { }
        FitPreviewToScanout();
        _output.ResizeNativeEglWindow();
        RefreshTextInputRectangle();
        return true;
    }

    public bool TryQueueScanoutFrame()
    {
        if (!Scanout!.TryQueueGpuFrame(CaptureScanoutFrame))
            return false;
        _scanoutFrameSequence = _sceneFrameSequence;
        return true;
    }

    private void CaptureScanoutFrame(DrmScanoutBuffer buffer)
    {
        _renderer!.BeginScene(buffer);
        DrawScene();
        _renderer.EndScene();
    }

    public bool TryPresentScanout(out uint timestamp, out ulong frameSequence)
    {
        frameSequence = _scanoutFrameSequence;
        if (!Scanout!.TryTakeGpuFrame(out var buffer, out timestamp))
            return false;

        bool presented = buffer!.Present();
        _logger.LogDebug("Presented virtual KMS GPU page flip at {Timestamp} ms", timestamp);
        return presented;
    }

    private void DrawScene()
    {
        int presented = 0;

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

            DrawDecoration(sceneSurfaceId, surface);
            _renderer!.Blit(surface.Texture, ToDisplayRect(surface.Bounds), surface.Mapping);
            presented++;
        }

        _logger.LogDebug("Wayland SDL host presented surfaces={Count}", presented);
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
        state.Mapping = frame.Mapping;
        PlacePresentedSurface(sceneSurfaceId, state, bounds);
    }

    private bool UpsertGpuSurface(ulong sceneSurfaceId, WaylandGpuFrame frame, WaylandSurfaceBounds bounds)
    {
        var format = frame.Buffer.PrimeFormat == 0x34325241 ? DisplayPixelFormat.Argb8888 : DisplayPixelFormat.Xrgb8888;
        int width = (int)frame.Buffer.Width, height = (int)frame.Buffer.Height;
        var state = EnsureSurfaceState(sceneSurfaceId);
        bool reuse = state.Texture != null && state.Width == width && state.Height == height && state.Format == format;
        var texture = reuse ? state.Texture! : _renderer!.CreateTexture(new DisplayTextureDescriptor(width, height, format));
        if (!_renderer!.UploadGpuBuffer(texture, frame.Buffer, frame.YInverted, frame.LeaseToken))
        {
            if (!reuse)
                texture.Dispose();
            _logger.LogWarning("Could not import the GPU buffer for Wayland surface {Surface}", sceneSurfaceId);
            return false;
        }
        if (!reuse)
        {
            state.Texture?.Dispose();
            state.Texture = texture;
            state.Width = width;
            state.Height = height;
            state.Format = format;
        }
        PlacePresentedSurface(sceneSurfaceId, state, bounds);
        state.Mapping = frame.Mapping;
        _logger.LogDebug("Composited a Wayland GPU buffer {Width}x{Height}", frame.Buffer.Width, frame.Buffer.Height);
        return true;
    }

    private void PlacePresentedSurface(ulong sceneSurfaceId, SurfaceTextureState state, WaylandSurfaceBounds bounds)
    {
        state.Bounds = bounds;
        if (!_zOrder.Contains(sceneSurfaceId))
            PlaceAtTopOfLayer(sceneSurfaceId, state.Layer);
    }

    private void UpdateSurfaceBounds(ulong sceneSurfaceId, WaylandSurfaceBounds bounds)
    {
        var state = EnsureSurfaceState(sceneSurfaceId);
        state.Bounds = bounds;
    }

    private void UpdateDecoration(ulong sceneSurfaceId, WaylandDecorationSceneState decoration)
    {
        var state = EnsureSurfaceState(sceneSurfaceId);
        if (!ReferenceEquals(state.Decoration.ApplicationIcon, decoration.ApplicationIcon))
        {
            state.ApplicationIconTexture?.Dispose();
            state.ApplicationIconTexture = CreateApplicationIconTexture(decoration.ApplicationIcon);
        }
        state.Decoration = decoration;
    }

    private IDisplayTexture? CreateApplicationIconTexture(WaylandApplicationIcon? icon)
    {
        if (icon == null)
            return null;

        int size = ToPixels(Math.Clamp(_theme.Spacing.TitlebarHeight - 8, 1, 18));
        byte[]? pixels;
        try
        {
            pixels = WaylandIconRasterizer.Rasterize(icon, size);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Could not decode the guest application icon");
            return null;
        }

        if (pixels == null)
            return null;
        var texture = _renderer!.CreateTexture(new DisplayTextureDescriptor(size, size, DisplayPixelFormat.Argb8888));
        texture.Update(pixels, size * 4);
        return texture;
    }

    private void SetSurfaceVisibility(ulong sceneSurfaceId, bool hidden)
    {
        EnsureSurfaceState(sceneSurfaceId).Hidden = hidden;
    }

    private void RaiseSurface(ulong sceneSurfaceId)
    {
        if (!_surfaces.TryGetValue(sceneSurfaceId, out SurfaceTextureState? state))
            return;

        PlaceAtTopOfLayer(sceneSurfaceId, state.Layer);
    }

    private void SetSurfaceLayer(ulong sceneSurfaceId, WaylandSceneLayer layer)
    {
        SurfaceTextureState state = EnsureSurfaceState(sceneSurfaceId);
        state.Layer = layer;
        if (_zOrder.Contains(sceneSurfaceId))
            PlaceAtTopOfLayer(sceneSurfaceId, layer);
    }

    private void PlaceAtTopOfLayer(ulong sceneSurfaceId, WaylandSceneLayer layer)
    {
        _zOrder.Remove(sceneSurfaceId);
        int index = _zOrder.FindIndex(id => _surfaces[id].Layer > layer);
        _zOrder.Insert(index < 0 ? _zOrder.Count : index, sceneSurfaceId);
    }

    private void RemoveSurface(ulong sceneSurfaceId)
    {
        if (!_surfaces.Remove(sceneSurfaceId, out var surface))
            return;

        _zOrder.Remove(sceneSurfaceId);
        surface.TitleTexture?.Dispose();
        surface.ApplicationIconTexture?.Dispose();
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

    private void DrawDecoration(ulong sceneSurfaceId, SurfaceTextureState surface)
    {
        if (!surface.Decoration.Visible || surface.Decoration.Minimized)
            return;

        var windowBounds = WaylandDecorationLayout.GetWindowBounds(surface.Bounds, surface.Decoration, _theme);
        var chrome = surface.Decoration.Active
            ? _theme.WindowDecoration.Active
            : _theme.WindowDecoration.Inactive;

        var metrics = WaylandDecorationMetrics.FromTheme(_theme);
        _renderer!.DrawChrome(ToDisplayRect(windowBounds),
            ToDisplayColor(surface.Decoration.Active ? chrome.BorderActive : chrome.Border),
            ToDisplayColor(chrome.Background), WaylandDecorationLayout.GetWindowCornerRadius(windowBounds, surface.Decoration, _theme),
            metrics.BorderThickness, metrics.TitlebarHeight);

        if (surface.ApplicationIconTexture is { } appIcon)
        {
            var iconBounds = WaylandDecorationLayout.GetApplicationIconBounds(windowBounds, _theme);
            if (iconBounds.Width > 0)
            {
                var pixels = _renderer.GetPixelBounds(ToDisplayRect(iconBounds));
                _renderer.BlitPixels(appIcon, new DisplayRect(pixels.X + (pixels.Width - appIcon.Width) / 2,
                    pixels.Y + (pixels.Height - appIcon.Height) / 2, appIcon.Width, appIcon.Height));
            }
        }
        DrawTitle(surface, windowBounds, chrome);
        DrawButton(sceneSurfaceId, surface.Decoration, WaylandSceneHitKind.MinimizeButton,
            WaylandDecorationLayout.GetMinimizeButtonBounds(windowBounds, _theme));
        DrawButton(sceneSurfaceId, surface.Decoration, WaylandSceneHitKind.MaximizeButton,
            WaylandDecorationLayout.GetMaximizeButtonBounds(windowBounds, _theme));
        DrawButton(sceneSurfaceId, surface.Decoration, WaylandSceneHitKind.CloseButton,
            WaylandDecorationLayout.GetCloseButtonBounds(windowBounds, _theme));
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

    private void UpdateTextInput(ulong sceneSurfaceId, WaylandRect? rect)
    {
        bool surfaceChanged = _textInputSurfaceId != sceneSurfaceId;
        if (surfaceChanged && _textInputSurfaceId != 0)
            _output!.StopTextInput();

        _textInputSurfaceId = sceneSurfaceId;
        _textInputRect = rect;
        if (sceneSurfaceId == 0)
            return;

        RefreshTextInputRectangle();
        if (surfaceChanged)
            _output!.StartTextInput();
    }

    private void RefreshTextInputRectangle()
    {
        if (_textInputSurfaceId == 0 || !_surfaces.TryGetValue(_textInputSurfaceId, out var surface) || surface.Hidden)
            return;

        int width = _desktopOptions.Width;
        int height = _desktopOptions.Height;
        WaylandRect rect = _textInputRect ?? new WaylandRect(0, 0, 1, 1);
        long x = (long)surface.Bounds.X + rect.X;
        long y = (long)surface.Bounds.Y + rect.Y;
        int left = (int)Math.Clamp(x, 0, width - 1);
        int top = (int)Math.Clamp(y, 0, height - 1);
        int right = (int)Math.Clamp(x + rect.Width, left + 1, width);
        int bottom = (int)Math.Clamp(y + rect.Height, top + 1, height);
        int windowWidth = _output!.Width, windowHeight = _output.Height;
        if (windowWidth <= 0 || windowHeight <= 0)
            return;
        int windowX = ScaleCoordinate(left, windowWidth, width);
        int windowY = ScaleCoordinate(top, windowHeight, height);
        int rectWidth = ScaleCoordinate(right, windowWidth, width) - windowX;
        int rectHeight = ScaleCoordinate(bottom, windowHeight, height) - windowY;
        _output.SetTextInputRect(new DisplayRect(windowX, windowY, Math.Max(1, rectWidth), Math.Max(1, rectHeight)));
    }

    private static int ScaleCoordinate(int coordinate, int targetSize, int sourceSize) =>
        (int)((long)coordinate * targetSize / sourceSize);

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

    private void DrawButton(ulong sceneSurfaceId, WaylandDecorationSceneState decoration,
        WaylandSceneHitKind kind, WaylandSurfaceBounds bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        bool hover = _hoveredDecoration.SceneSurfaceId == sceneSurfaceId && _hoveredDecoration.Kind == kind;
        bool pressed = hover && _pressedDecoration.SceneSurfaceId == sceneSurfaceId && _pressedDecoration.Kind == kind;
        bool toggled = kind == WaylandSceneHitKind.MaximizeButton && decoration.Maximized;
        WaylandButtonAppearance appearance = _theme.WindowDecoration.GetButton(kind)
            ?.GetAppearance(decoration.Active, toggled, hover, pressed) ?? GetDefaultButtonAppearance(kind, hover, pressed);
        if (appearance.Background.A != 0)
            _renderer!.FillRect(ToDisplayRect(bounds), ToDisplayColor(appearance.Background));

        IDisplayTexture texture = GetButtonTexture(kind, appearance);
        double scale = Math.Min(1.0, Math.Min((double)bounds.Width / texture.Width, (double)bounds.Height / texture.Height));
        int width = Math.Max(1, (int)(texture.Width * scale));
        int height = Math.Max(1, (int)(texture.Height * scale));
        _renderer!.Blit(texture, new DisplayRect(bounds.X + (bounds.Width - width) / 2,
            bounds.Y + (bounds.Height - height) / 2, width, height));
    }

    private WaylandButtonAppearance GetDefaultButtonAppearance(WaylandSceneHitKind kind, bool hover, bool pressed)
    {
        var buttons = _theme.WindowDecoration.Buttons;
        WaylandColor background = pressed ? buttons.BackgroundPressed : hover ? buttons.BackgroundHover : kind switch
        {
            WaylandSceneHitKind.CloseButton => buttons.BackgroundClose,
            WaylandSceneHitKind.MaximizeButton => buttons.BackgroundMaximize,
            _ => buttons.BackgroundMinimize
        };
        return new WaylandButtonAppearance(buttons.Foreground, background, null);
    }

    private void DrawTitle(SurfaceTextureState surface, WaylandSurfaceBounds windowBounds,
        WaylandSurfaceChromeTheme chrome)
    {
        if (string.IsNullOrWhiteSpace(surface.Decoration.Title))
            return;

        var titleBounds = WaylandDecorationLayout.GetTitleTextBounds(windowBounds, _theme,
            reserveIcon: surface.ApplicationIconTexture != null);
        if (titleBounds.Width <= 0 || titleBounds.Height <= 0)
            return;

        var pixels = _renderer!.GetPixelBounds(ToDisplayRect(titleBounds));
        int width = Math.Min(pixels.Width, Scanout!.Width);
        if (width <= 0 || pixels.Height <= 0)
            return;
        EnsureTitleTexture(surface, width, pixels.Height, chrome.Foreground);
        if (surface.TitleTexture != null)
            _renderer.BlitPixels(surface.TitleTexture, pixels with { Width = width });
    }

    private void EnsureTitleTexture(SurfaceTextureState surface, int width, int height, WaylandColor color)
    {
        var title = surface.Decoration.Title.Trim();
        if (surface.TitleTexture != null &&
            surface.CachedTitle == title &&
            surface.TitleTexture.Width == width && surface.TitleTexture.Height == height &&
            surface.CachedTitleColor == color)
            return;

        InvalidateTitleTexture(surface);
        surface.CachedTitle = title;
        surface.CachedTitleColor = color;

        if (string.IsNullOrEmpty(title))
            return;

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
            TextSize = _theme.Typography.TitleFontSize * Scale120 / 120f,
            Typeface = GetTitleTypeface(title)
        };

        var fittedTitle = EllipsizeTitle(title, paint, width);
        paint.GetFontMetrics(out var metrics);
        var baseline = (height - (metrics.Descent - metrics.Ascent)) * 0.5f - metrics.Ascent;
        float startX = _theme.Typography.Alignment switch
        {
            WaylandTitleAlignment.Center => (width - paint.MeasureText(fittedTitle)) / 2,
            WaylandTitleAlignment.Right => width - paint.MeasureText(fittedTitle),
            _ => 0
        };
        canvas.DrawText(fittedTitle, MathF.Round(Math.Max(0, startX)), MathF.Round(baseline), paint);
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

    private IDisplayTexture GetButtonTexture(WaylandSceneHitKind kind, WaylandButtonAppearance appearance)
    {
        var key = (kind, appearance.Foreground, appearance.Bitmap);
        if (_buttonIcons.TryGetValue(key, out IDisplayTexture? texture))
            return texture;

        int width = appearance.Bitmap?.Width ?? 16;
        int height = appearance.Bitmap?.Height ?? 16;
        int pitch = width * 4;
        var pixels = new byte[pitch * height];
        WaylandColor color = appearance.Foreground;
        if (appearance.Bitmap is WaylandButtonBitmap bitmap)
        {
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (bitmap.IsSet(x, y))
                    PutPixel(pixels, pitch, x, y, color.R, color.G, color.B, color.A);
        }
        else
        {
            int stroke = _theme.Spacing.IconStrokeWidth;
            switch (kind)
            {
                case WaylandSceneHitKind.CloseButton:
                    DrawCloseIcon(pixels, width, pitch, color, stroke);
                    break;
                case WaylandSceneHitKind.MaximizeButton:
                    DrawMaximizeIcon(pixels, width, pitch, color, stroke);
                    break;
                case WaylandSceneHitKind.MinimizeButton:
                    DrawMinimizeIcon(pixels, width, pitch, color, stroke);
                    break;
            }
        }

        texture = _renderer!.CreateTexture(new DisplayTextureDescriptor(width, height, DisplayPixelFormat.Argb8888), nearest: true);
        texture.Update(pixels, pitch);
        _buttonIcons.Add(key, texture);
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
        pixels[offset] = (byte)((b * a + 127) / 255);
        pixels[offset + 1] = (byte)((g * a + 127) / 255);
        pixels[offset + 2] = (byte)((r * a + 127) / 255);
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

        if (written != length)
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
            Math.Clamp(ScaleCoordinate(cursor.HotspotX, frame.Width, frame.LogicalWidth), 0, frame.Width - 1),
            Math.Clamp(ScaleCoordinate(cursor.HotspotY, frame.Height, frame.LogicalHeight), 0, frame.Height - 1),
            DisplayPixelFormat.Argb8888,
            pixels,
            frame.Stride);
    }

    private sealed class SurfaceTextureState(IDisplayTexture? texture, int width, int height, DisplayPixelFormat format)
    {
        public IDisplayTexture? Texture { get; set; } = texture;
        public IDisplayTexture? TitleTexture { get; set; }
        public IDisplayTexture? ApplicationIconTexture { get; set; }
        public WaylandSurfaceMapping? Mapping { get; set; }
        public int Width { get; set; } = width;
        public int Height { get; set; } = height;
        public DisplayPixelFormat Format { get; set; } = format;
        public WaylandSurfaceBounds Bounds { get; set; }
        public WaylandDecorationSceneState Decoration { get; set; }
        public bool Hidden { get; set; }
        public WaylandSceneLayer Layer { get; set; }
        public string CachedTitle { get; set; } = string.Empty;
        public WaylandColor CachedTitleColor { get; set; }
    }
}
