namespace Podish.Wayland;

public readonly record struct WaylandShmFrame(
    ulong SceneSurfaceId,
    ulong LeaseToken,
    LinuxFile File,
    int Offset,
    int Width,
    int Height,
    int Stride,
    WlShmFormat Format);

public readonly record struct WaylandCursorFrame(
    ulong SceneSurfaceId,
    WaylandShmFrame Frame,
    int HotspotX,
    int HotspotY);

public readonly record struct WaylandSurfaceHit(ulong SceneSurfaceId, int SurfaceX, int SurfaceY);
public readonly record struct WaylandSurfaceBounds(int X, int Y, int Width, int Height);

public interface IWaylandFramePresenter
{
    ValueTask PresentSurfaceAsync(ulong sceneSurfaceId, WaylandShmFrame? frame,
        CancellationToken cancellationToken = default);
}

public interface IWaylandCursorPresenter
{
    ValueTask UpdateCursorAsync(ulong sceneSurfaceId, WaylandCursorFrame? cursor,
        CancellationToken cancellationToken = default);
}

public interface IWaylandSceneView
{
    bool TryGetSurfaceAt(int desktopX, int desktopY, out WaylandSurfaceHit hit);
    bool TryGetSurfaceBounds(ulong sceneSurfaceId, out WaylandSurfaceBounds bounds);
}

public interface IWaylandSceneDebugView
{
    IReadOnlyList<(ulong SceneSurfaceId, WaylandSurfaceBounds Bounds)> SnapshotSurfaceBounds();
}

public interface IWaylandDesktopSceneController
{
    void ResizeDesktop(int width, int height);
    void RaiseSurface(ulong sceneSurfaceId);
    void SetSurfaceBounds(ulong sceneSurfaceId, WaylandSurfaceBounds bounds);
}
