using Fiberish.VFS;
using Microsoft.Extensions.Logging;
using Podish.Display;
using Podish.Wayland;

namespace Podish.Cli.Wayland;

internal sealed class WaylandSdlFramePresenter : IWaylandFramePresenter, IWaylandGpuFramePresenter, IWaylandFrameScheduler, IWaylandCursorPresenter, IWaylandTextInputPresenter, IWaylandSceneView, IWaylandSceneDebugView, IWaylandDesktopSceneController, IWaylandDecorationInputPresenter, IDisposable
{
    private readonly Microsoft.Extensions.Logging.ILogger _logger;
    private int _desktopWidth;
    private int _desktopHeight;
    private readonly WaylandUiTheme _theme;
    private readonly Action<WaylandDisplayCommand> _enqueueCommand;
    private readonly Func<DrmScanoutBuffer, bool>? _canImportGpuBuffer;
    private readonly Dictionary<ulong, SurfaceState> _surfaces = [];
    private readonly List<ulong> _zOrder = [];
    private ulong _frameSequence;

    public WaylandSdlFramePresenter(WaylandDesktopOptions desktopOptions, Action<WaylandDisplayCommand> enqueueCommand)
    {
        _desktopWidth = desktopOptions.Width;
        _desktopHeight = desktopOptions.Height;
        _theme = desktopOptions.Theme;
        _enqueueCommand = enqueueCommand;
        _logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    public WaylandSdlFramePresenter(
        WaylandDesktopOptions desktopOptions,
        Action<WaylandDisplayCommand> enqueueCommand,
        Microsoft.Extensions.Logging.ILogger logger,
        Func<DrmScanoutBuffer, bool>? canImportGpuBuffer = null)
    {
        _desktopWidth = desktopOptions.Width;
        _desktopHeight = desktopOptions.Height;
        _theme = desktopOptions.Theme;
        _enqueueCommand = enqueueCommand;
        _logger = logger;
        _canImportGpuBuffer = canImportGpuBuffer;
    }

    public ulong ScheduleFrame()
    {
        ulong sequence = ++_frameSequence;
        _enqueueCommand(new WaylandDisplayCommand(WaylandDisplayCommandKind.ScheduleFrame, FrameSequence: sequence));
        return sequence;
    }

    public ValueTask PresentSurfaceAsync(ulong sceneSurfaceId, WaylandShmFrame? frame,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (frame == null)
        {
            _surfaces.Remove(sceneSurfaceId);
            _zOrder.Remove(sceneSurfaceId);
            _enqueueCommand(new WaylandDisplayCommand(WaylandDisplayCommandKind.RemoveSurface, sceneSurfaceId));
            return ValueTask.CompletedTask;
        }

        var bounds = SetSurfaceFrameSize(sceneSurfaceId, frame.Value.LogicalWidth, frame.Value.LogicalHeight);
        _enqueueCommand(new WaylandDisplayCommand(WaylandDisplayCommandKind.PresentSurface, sceneSurfaceId, frame.Value, bounds));
        return ValueTask.CompletedTask;
    }

    public bool CanImportGpuBuffer(DrmScanoutBuffer buffer) => _canImportGpuBuffer?.Invoke(buffer) == true;

    public ValueTask PresentGpuSurfaceAsync(ulong sceneSurfaceId, WaylandGpuFrame frame)
    {
        var bounds = SetSurfaceFrameSize(sceneSurfaceId, frame.LogicalWidth, frame.LogicalHeight);
        _enqueueCommand(new WaylandDisplayCommand(WaylandDisplayCommandKind.PresentGpuSurface, sceneSurfaceId,
            Bounds: bounds, GpuFrame: frame));
        return ValueTask.CompletedTask;
    }

    public void UpdateGpuSurfaceMapping(ulong sceneSurfaceId, WaylandSurfaceMapping mapping)
    {
        if (_surfaces.ContainsKey(sceneSurfaceId))
        {
            var bounds = SetSurfaceFrameSize(sceneSurfaceId, mapping.Width, mapping.Height);
            _enqueueCommand(new WaylandDisplayCommand(WaylandDisplayCommandKind.UpdateSurfaceMapping, sceneSurfaceId,
                Bounds: bounds, Mapping: mapping));
        }
    }

    private WaylandSurfaceBounds SetSurfaceFrameSize(ulong sceneSurfaceId, int width, int height)
    {
        SurfaceState state;
        if (_surfaces.TryGetValue(sceneSurfaceId, out SurfaceState? existing))
        {
            state = existing;
            state.Width = width;
            state.Height = height;
            if (state.AutoCentered)
            {
                WaylandSurfaceBounds centered = ComputeCenteredContentRect(width, height, state.Decoration);
                state.ContentBounds = centered;
            }
            else
            {
                state.ContentBounds = new WaylandSurfaceBounds(
                    state.ContentBounds.X,
                    state.ContentBounds.Y,
                    width,
                    height);
            }
        }
        else
        {
            state = new SurfaceState(width, height)
            {
                SceneSurfaceId = sceneSurfaceId,
                ContentBounds = ComputeCenteredContentRect(width, height, default),
                AutoCentered = true
            };
            _surfaces[sceneSurfaceId] = state;
        }

        if (!_zOrder.Contains(sceneSurfaceId))
            PlaceAtTopOfLayer(sceneSurfaceId, state.Layer);
        return state.ContentBounds;
    }

    public ValueTask UpdateCursorAsync(ulong sceneSurfaceId, WaylandCursorFrame? cursor, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _surfaces.Remove(sceneSurfaceId);
        _zOrder.Remove(sceneSurfaceId);
        _enqueueCommand(new WaylandDisplayCommand(WaylandDisplayCommandKind.RemoveSurface, sceneSurfaceId));

        if (cursor == null)
        {
            _enqueueCommand(new WaylandDisplayCommand(WaylandDisplayCommandKind.ClearCursor, sceneSurfaceId));
            return ValueTask.CompletedTask;
        }

        _enqueueCommand(new WaylandDisplayCommand(
            WaylandDisplayCommandKind.SetCursor,
            sceneSurfaceId,
            Cursor: cursor.Value));
        return ValueTask.CompletedTask;
    }

    public ValueTask UpdateSystemCursorAsync(WaylandSystemCursorShape? shape, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _enqueueCommand(new WaylandDisplayCommand(
            shape.HasValue ? WaylandDisplayCommandKind.SetSystemCursor : WaylandDisplayCommandKind.ClearCursor,
            SystemCursor: shape));
        return ValueTask.CompletedTask;
    }

    public ValueTask UpdateTextInputAsync(ulong sceneSurfaceId, WaylandRect? cursorRectangle, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _enqueueCommand(new WaylandDisplayCommand(
            WaylandDisplayCommandKind.UpdateTextInput,
            sceneSurfaceId,
            TextInputRect: cursorRectangle));
        return ValueTask.CompletedTask;
    }

    public void UpdateDecorationPointer(WaylandSceneHit hovered, WaylandSceneHit pressed)
    {
        _enqueueCommand(new WaylandDisplayCommand(WaylandDisplayCommandKind.UpdateDecorationPointer,
            HoveredDecoration: hovered, PressedDecoration: pressed));
    }

    public bool TryGetSurfaceAt(int desktopX, int desktopY, out WaylandSurfaceHit hit)
    {
        if (TryGetSceneHitAt(desktopX, desktopY, out WaylandSceneHit sceneHit))
        {
            hit = new WaylandSurfaceHit(sceneHit.SceneSurfaceId, sceneHit.SurfaceX, sceneHit.SurfaceY);
            return true;
        }

        hit = default;
        return false;
    }

    public bool TryGetSceneHitAt(int desktopX, int desktopY, out WaylandSceneHit hit)
    {
        for (int i = _zOrder.Count - 1; i >= 0; i--)
        {
            ulong sceneSurfaceId = _zOrder[i];
            if (!_surfaces.TryGetValue(sceneSurfaceId, out SurfaceState? surface) || surface.Hidden)
                continue;

            if (TryHitSurface(surface, desktopX, desktopY, out hit))
                return true;
        }

        hit = default;
        return false;
    }

    public bool TryGetSurfaceBounds(ulong sceneSurfaceId, out WaylandSurfaceBounds bounds)
    {
        if (_surfaces.TryGetValue(sceneSurfaceId, out SurfaceState? surface))
        {
            bounds = surface.ContentBounds;
            return true;
        }

        bounds = default;
        return false;
    }

    public bool TryGetWindowBounds(ulong sceneSurfaceId, out WaylandSurfaceBounds bounds)
    {
        if (_surfaces.TryGetValue(sceneSurfaceId, out SurfaceState? surface))
        {
            bounds = GetWindowBounds(surface);
            return true;
        }

        bounds = default;
        return false;
    }

    public IReadOnlyList<(ulong SceneSurfaceId, WaylandSurfaceBounds Bounds)> SnapshotSurfaceBounds()
    {
        return [.. _zOrder
            .Where(sceneSurfaceId => _surfaces.ContainsKey(sceneSurfaceId))
            .Select(sceneSurfaceId => (sceneSurfaceId, _surfaces[sceneSurfaceId].ContentBounds))];
    }

    public void ResizeDesktop(int width, int height)
    {
        _desktopWidth = width;
        _desktopHeight = height;

        foreach (SurfaceState state in _surfaces.Values)
        {
            if (!state.AutoCentered)
                continue;

            state.ContentBounds = ComputeCenteredContentRect(state.Width, state.Height, state.Decoration);
            _enqueueCommand(new WaylandDisplayCommand(
                WaylandDisplayCommandKind.UpdateSurfaceBounds,
                state.SceneSurfaceId,
                Bounds: state.ContentBounds));
        }
    }

    public void RaiseSurface(ulong sceneSurfaceId)
    {
        if (!_surfaces.TryGetValue(sceneSurfaceId, out SurfaceState? state))
            return;

        PlaceAtTopOfLayer(sceneSurfaceId, state.Layer);
        _enqueueCommand(new WaylandDisplayCommand(WaylandDisplayCommandKind.RaiseSurface, sceneSurfaceId));
    }

    public void SetSurfaceLayer(ulong sceneSurfaceId, WaylandSceneLayer layer)
    {
        SurfaceState state = GetOrCreateState(sceneSurfaceId);
        if (state.Layer == layer)
            return;

        state.Layer = layer;
        if (_zOrder.Contains(sceneSurfaceId))
            PlaceAtTopOfLayer(sceneSurfaceId, layer);
        _enqueueCommand(new WaylandDisplayCommand(WaylandDisplayCommandKind.SetSurfaceLayer, sceneSurfaceId, Layer: layer));
    }

    private void PlaceAtTopOfLayer(ulong sceneSurfaceId, WaylandSceneLayer layer)
    {
        _zOrder.Remove(sceneSurfaceId);
        int index = _zOrder.FindIndex(id => _surfaces[id].Layer > layer);
        _zOrder.Insert(index < 0 ? _zOrder.Count : index, sceneSurfaceId);
    }

    public void SetSurfaceBounds(ulong sceneSurfaceId, WaylandSurfaceBounds bounds)
    {
        SurfaceState state = GetOrCreateState(sceneSurfaceId);

        state.AutoCentered = false;
        state.ContentBounds = bounds;
        state.Width = bounds.Width;
        state.Height = bounds.Height;
        _enqueueCommand(new WaylandDisplayCommand(WaylandDisplayCommandKind.UpdateSurfaceBounds, sceneSurfaceId, Bounds: bounds));
    }

    public void SetWindowBounds(ulong sceneSurfaceId, WaylandSurfaceBounds bounds)
    {
        SurfaceState state = GetOrCreateState(sceneSurfaceId);

        SetSurfaceBounds(sceneSurfaceId, WaylandDecorationLayout.GetContentBoundsFromWindowBounds(bounds, state.Decoration, _theme));
    }

    public void SetSurfaceDecoration(ulong sceneSurfaceId, WaylandDecorationSceneState decoration)
    {
        SurfaceState state = GetOrCreateState(sceneSurfaceId);

        state.Decoration = decoration;
        if (state.AutoCentered)
            state.ContentBounds = ComputeCenteredContentRect(state.Width, state.Height, decoration);

        _enqueueCommand(new WaylandDisplayCommand(
            WaylandDisplayCommandKind.UpdateDecoration,
            sceneSurfaceId,
            Decoration: decoration));
        _enqueueCommand(new WaylandDisplayCommand(
            WaylandDisplayCommandKind.UpdateSurfaceBounds,
            sceneSurfaceId,
            Bounds: state.ContentBounds));
    }

    public void SetSurfaceHidden(ulong sceneSurfaceId, bool hidden)
    {
        SurfaceState state = GetOrCreateState(sceneSurfaceId);

        state.Hidden = hidden;
        _enqueueCommand(new WaylandDisplayCommand(
            WaylandDisplayCommandKind.SetSurfaceVisibility,
            sceneSurfaceId,
            Hidden: hidden));
    }

    public void Dispose()
    {
        _surfaces.Clear();
        _zOrder.Clear();
    }

    private WaylandSurfaceBounds ComputeCenteredContentRect(int width, int height, WaylandDecorationSceneState decoration)
    {
        var contentBounds = new WaylandSurfaceBounds(0, 0, width, height);
        WaylandSurfaceBounds windowBounds = WaylandDecorationLayout.GetWindowBounds(contentBounds, decoration, _theme);
        int leftInset = -windowBounds.X;
        int topInset = -windowBounds.Y;
        int outerWidth = windowBounds.Width;
        int outerHeight = windowBounds.Height;

        return new WaylandSurfaceBounds(
            (_desktopWidth - outerWidth) / 2 + leftInset,
            (_desktopHeight - outerHeight) / 2 + topInset,
            width,
            height);
    }

    private WaylandSurfaceBounds GetWindowBounds(SurfaceState surface)
    {
        return WaylandDecorationLayout.GetWindowBounds(surface.ContentBounds, surface.Decoration, _theme);
    }

    private SurfaceState GetOrCreateState(ulong sceneSurfaceId)
    {
        if (_surfaces.TryGetValue(sceneSurfaceId, out SurfaceState? existing))
            return existing;

        var state = new SurfaceState(1, 1)
        {
            SceneSurfaceId = sceneSurfaceId,
            ContentBounds = ComputeCenteredContentRect(1, 1, default),
            AutoCentered = true
        };
        _surfaces[sceneSurfaceId] = state;
        return state;
    }

    private bool TryHitSurface(SurfaceState surface, int desktopX, int desktopY, out WaylandSceneHit hit)
    {
        WaylandSurfaceBounds contentBounds = surface.ContentBounds;
        WaylandSurfaceBounds windowBounds = GetWindowBounds(surface);
        if (desktopX < windowBounds.X || desktopY < windowBounds.Y ||
            desktopX >= windowBounds.X + windowBounds.Width || desktopY >= windowBounds.Y + windowBounds.Height)
        {
            hit = default;
            return false;
        }

        int radius = WaylandDecorationLayout.GetWindowCornerRadius(windowBounds, surface.Decoration, _theme);
        float localX = desktopX - windowBounds.X + 0.5f;
        float localY = desktopY - windowBounds.Y + 0.5f;
        float dx = Math.Max(0, radius - Math.Min(localX, windowBounds.Width - localX));
        float dy = Math.Max(0, radius - localY);
        if (dx * dx + dy * dy > radius * radius)
        {
            hit = default;
            return false;
        }

        int surfaceX = desktopX - contentBounds.X;
        int surfaceY = desktopY - contentBounds.Y;
        if (desktopX >= contentBounds.X && desktopY >= contentBounds.Y &&
            desktopX < contentBounds.X + contentBounds.Width && desktopY < contentBounds.Y + contentBounds.Height)
        {
            hit = new WaylandSceneHit(surface.SceneSurfaceId, WaylandSceneHitKind.Surface, surfaceX, surfaceY);
            return true;
        }

        if (surface.Decoration.Visible && !surface.Decoration.Minimized)
        {
            if (TryHitDecoration(surface, windowBounds, desktopX, desktopY, surfaceX, surfaceY, _theme, out hit))
                return true;
        }

        hit = default;
        return false;
    }

    private static bool TryHitDecoration(SurfaceState surface, WaylandSurfaceBounds windowBounds, int desktopX, int desktopY,
        int surfaceX, int surfaceY, WaylandUiTheme theme, out WaylandSceneHit hit)
    {
        WaylandDecorationMetrics metrics = WaylandDecorationMetrics.FromTheme(theme);
        DisplayRect close = ToDisplayRect(WaylandDecorationLayout.GetCloseButtonBounds(windowBounds, theme));
        DisplayRect maximize = ToDisplayRect(WaylandDecorationLayout.GetMaximizeButtonBounds(windowBounds, theme));
        DisplayRect minimize = ToDisplayRect(WaylandDecorationLayout.GetMinimizeButtonBounds(windowBounds, theme));

        if (Contains(close, desktopX, desktopY))
        {
            hit = new WaylandSceneHit(surface.SceneSurfaceId, WaylandSceneHitKind.CloseButton, surfaceX, surfaceY);
            return true;
        }

        if (Contains(maximize, desktopX, desktopY))
        {
            hit = new WaylandSceneHit(surface.SceneSurfaceId, WaylandSceneHitKind.MaximizeButton, surfaceX, surfaceY);
            return true;
        }

        if (Contains(minimize, desktopX, desktopY))
        {
            hit = new WaylandSceneHit(surface.SceneSurfaceId, WaylandSceneHitKind.MinimizeButton, surfaceX, surfaceY);
            return true;
        }

        XdgToplevelResizeEdge resizeEdges = XdgToplevelResizeEdge.None;
        int border = metrics.ResizeGripThickness;
        if (desktopX < surface.ContentBounds.X)
            resizeEdges |= XdgToplevelResizeEdge.Left;
        else if (desktopX >= surface.ContentBounds.X + surface.ContentBounds.Width)
            resizeEdges |= XdgToplevelResizeEdge.Right;
        if (desktopY < surface.ContentBounds.Y)
            resizeEdges |= XdgToplevelResizeEdge.Top;
        else if (desktopY >= surface.ContentBounds.Y + surface.ContentBounds.Height)
            resizeEdges |= XdgToplevelResizeEdge.Bottom;

        bool insideResizeZone =
            desktopX < windowBounds.X + border ||
            desktopX >= windowBounds.X + windowBounds.Width - border ||
            desktopY < windowBounds.Y + border ||
            desktopY >= windowBounds.Y + windowBounds.Height - border;
        if (resizeEdges != XdgToplevelResizeEdge.None && insideResizeZone)
        {
            hit = new WaylandSceneHit(surface.SceneSurfaceId, WaylandSceneHitKind.ResizeBorder, surfaceX, surfaceY, resizeEdges);
            return true;
        }

        hit = new WaylandSceneHit(surface.SceneSurfaceId, WaylandSceneHitKind.Titlebar, surfaceX, surfaceY);
        return true;
    }

    private static bool Contains(DisplayRect rect, int x, int y)
    {
        return x >= rect.X && y >= rect.Y && x < rect.X + rect.Width && y < rect.Y + rect.Height;
    }

    private static DisplayRect ToDisplayRect(WaylandSurfaceBounds bounds) => new(bounds.X, bounds.Y, bounds.Width, bounds.Height);

    private sealed class SurfaceState(int width, int height)
    {
        public ulong SceneSurfaceId { get; init; }
        public int Width { get; set; } = width;
        public int Height { get; set; } = height;
        public WaylandSurfaceBounds ContentBounds { get; set; }
        public bool AutoCentered { get; set; }
        public WaylandDecorationSceneState Decoration { get; set; }
        public bool Hidden { get; set; }
        public WaylandSceneLayer Layer { get; set; }
    }
}
