using Podish.Wayland;

namespace Podish.Cli.Wayland;

internal sealed class WaylandSdlFramePresenter : IWaylandFramePresenter, IWaylandCursorPresenter, IWaylandSceneView, IWaylandSceneDebugView, IWaylandDesktopSceneController, IDisposable
{
    private int _desktopWidth;
    private int _desktopHeight;
    private readonly Action<WaylandDisplayCommand> _enqueueCommand;
    private readonly Dictionary<ulong, SurfaceState> _surfaces = [];
    private readonly List<ulong> _zOrder = [];

    public WaylandSdlFramePresenter(WaylandDesktopOptions desktopOptions, Action<WaylandDisplayCommand> enqueueCommand)
    {
        _desktopWidth = desktopOptions.Width;
        _desktopHeight = desktopOptions.Height;
        _enqueueCommand = enqueueCommand;
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

        SurfaceState state;
        if (_surfaces.TryGetValue(sceneSurfaceId, out SurfaceState? existing))
        {
            state = existing;
            state.Width = frame.Value.Width;
            state.Height = frame.Value.Height;
            if (state.AutoCentered)
                state.Bounds = ComputeCenteredRect(frame.Value.Width, frame.Value.Height);
            else
                state.Bounds = new WaylandSurfaceBounds(state.Bounds.X, state.Bounds.Y, frame.Value.Width, frame.Value.Height);
        }
        else
        {
            state = new SurfaceState(frame.Value.Width, frame.Value.Height)
            {
                SceneSurfaceId = sceneSurfaceId,
                Bounds = ComputeCenteredRect(frame.Value.Width, frame.Value.Height),
                AutoCentered = true
            };
            _surfaces[sceneSurfaceId] = state;
        }

        _zOrder.Remove(sceneSurfaceId);
        _zOrder.Add(sceneSurfaceId);
        _enqueueCommand(new WaylandDisplayCommand(WaylandDisplayCommandKind.PresentSurface, sceneSurfaceId, frame.Value, state.Bounds));
        return ValueTask.CompletedTask;
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

    public bool TryGetSurfaceAt(int desktopX, int desktopY, out WaylandSurfaceHit hit)
    {
        for (int i = _zOrder.Count - 1; i >= 0; i--)
        {
            ulong sceneSurfaceId = _zOrder[i];
            if (!_surfaces.TryGetValue(sceneSurfaceId, out SurfaceState? surface))
                continue;

            WaylandSurfaceBounds bounds = surface.Bounds;
            if (desktopX < bounds.X || desktopY < bounds.Y || desktopX >= bounds.X + bounds.Width ||
                desktopY >= bounds.Y + bounds.Height)
                continue;

            hit = new WaylandSurfaceHit(sceneSurfaceId, desktopX - bounds.X, desktopY - bounds.Y);
            return true;
        }

        hit = default;
        return false;
    }

    public bool TryGetSurfaceBounds(ulong sceneSurfaceId, out WaylandSurfaceBounds bounds)
    {
        if (_surfaces.TryGetValue(sceneSurfaceId, out SurfaceState? surface))
        {
            bounds = new WaylandSurfaceBounds(
                surface.Bounds.X,
                surface.Bounds.Y,
                surface.Bounds.Width,
                surface.Bounds.Height);
            return true;
        }

        bounds = default;
        return false;
    }

    public IReadOnlyList<(ulong SceneSurfaceId, WaylandSurfaceBounds Bounds)> SnapshotSurfaceBounds()
    {
        return [.. _zOrder
            .Where(sceneSurfaceId => _surfaces.ContainsKey(sceneSurfaceId))
            .Select(sceneSurfaceId => (sceneSurfaceId, _surfaces[sceneSurfaceId].Bounds))];
    }

    public void ResizeDesktop(int width, int height)
    {
        _desktopWidth = width;
        _desktopHeight = height;

        foreach (SurfaceState state in _surfaces.Values)
        {
            if (!state.AutoCentered)
                continue;

            state.Bounds = ComputeCenteredRect(state.Width, state.Height);
            _enqueueCommand(new WaylandDisplayCommand(
                WaylandDisplayCommandKind.UpdateSurfaceBounds,
                state.SceneSurfaceId,
                Bounds: state.Bounds));
        }
    }

    public void RaiseSurface(ulong sceneSurfaceId)
    {
        if (!_surfaces.ContainsKey(sceneSurfaceId))
            return;

        _zOrder.Remove(sceneSurfaceId);
        _zOrder.Add(sceneSurfaceId);
        _enqueueCommand(new WaylandDisplayCommand(WaylandDisplayCommandKind.RaiseSurface, sceneSurfaceId));
    }

    public void SetSurfaceBounds(ulong sceneSurfaceId, WaylandSurfaceBounds bounds)
    {
        if (!_surfaces.TryGetValue(sceneSurfaceId, out SurfaceState? state))
            return;

        state.AutoCentered = false;
        state.Bounds = bounds;
        state.Width = bounds.Width;
        state.Height = bounds.Height;
        _enqueueCommand(new WaylandDisplayCommand(WaylandDisplayCommandKind.UpdateSurfaceBounds, sceneSurfaceId, Bounds: bounds));
    }

    public void Dispose()
    {
        _surfaces.Clear();
        _zOrder.Clear();
    }

    private WaylandSurfaceBounds ComputeCenteredRect(int width, int height)
    {
        return new WaylandSurfaceBounds(
            (_desktopWidth - width) / 2,
            (_desktopHeight - height) / 2,
            width,
            height);
    }

    private sealed class SurfaceState(int width, int height)
    {
        public ulong SceneSurfaceId { get; init; }
        public int Width { get; set; } = width;
        public int Height { get; set; } = height;
        public WaylandSurfaceBounds Bounds { get; set; }
        public bool AutoCentered { get; set; }
    }
}
