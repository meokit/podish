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

public enum WaylandSceneHitKind
{
    Surface,
    Titlebar,
    ResizeBorder,
    CloseButton,
    MaximizeButton,
    MinimizeButton
}

public readonly record struct WaylandDecorationMetrics(int BorderThickness, int TitlebarHeight, int ButtonSize, int ButtonGap, int ButtonInset, int ResizeGripThickness)
{
    public static WaylandDecorationMetrics FromTheme(WaylandUiTheme theme) =>
        new(
            theme.Spacing.WindowBorderThickness,
            theme.Spacing.TitlebarHeight,
            theme.Spacing.ButtonSize,
            theme.Spacing.ButtonGap,
            theme.Spacing.ButtonInset,
            theme.Spacing.ResizeGripThickness);
}

public readonly record struct WaylandDecorationSceneState(
    bool Visible,
    bool Active,
    bool Maximized,
    bool Minimized,
    string Title);

public readonly record struct WaylandSceneHit(
    ulong SceneSurfaceId,
    WaylandSceneHitKind Kind,
    int SurfaceX,
    int SurfaceY,
    XdgToplevelResizeEdge ResizeEdges = XdgToplevelResizeEdge.None);

public readonly record struct WaylandSurfaceHit(ulong SceneSurfaceId, int SurfaceX, int SurfaceY);
public readonly record struct WaylandSurfaceBounds(int X, int Y, int Width, int Height);

public static class WaylandDecorationLayout
{
    public static WaylandSurfaceBounds GetWindowBounds(WaylandSurfaceBounds contentBounds, WaylandDecorationSceneState decoration, WaylandUiTheme theme)
    {
        if (!decoration.Visible || decoration.Minimized)
            return contentBounds;

        WaylandDecorationMetrics metrics = WaylandDecorationMetrics.FromTheme(theme);
        return new WaylandSurfaceBounds(
            contentBounds.X - metrics.BorderThickness,
            contentBounds.Y - metrics.TitlebarHeight,
            contentBounds.Width + metrics.BorderThickness * 2,
            contentBounds.Height + metrics.TitlebarHeight + metrics.BorderThickness);
    }

    public static WaylandSurfaceBounds GetContentBoundsFromWindowBounds(WaylandSurfaceBounds windowBounds, WaylandDecorationSceneState decoration, WaylandUiTheme theme)
    {
        if (!decoration.Visible || decoration.Minimized)
            return windowBounds;

        WaylandDecorationMetrics metrics = WaylandDecorationMetrics.FromTheme(theme);
        return new WaylandSurfaceBounds(
            windowBounds.X + metrics.BorderThickness,
            windowBounds.Y + metrics.TitlebarHeight,
            Math.Max(1, windowBounds.Width - metrics.BorderThickness * 2),
            Math.Max(1, windowBounds.Height - metrics.TitlebarHeight - metrics.BorderThickness));
    }

    public static WaylandSurfaceBounds GetTitlebarBounds(WaylandSurfaceBounds windowBounds, WaylandUiTheme theme)
    {
        WaylandDecorationMetrics metrics = WaylandDecorationMetrics.FromTheme(theme);
        return new WaylandSurfaceBounds(
            windowBounds.X + metrics.BorderThickness,
            windowBounds.Y,
            Math.Max(1, windowBounds.Width - metrics.BorderThickness * 2),
            metrics.TitlebarHeight);
    }

    public static WaylandSurfaceBounds GetButtonRowBounds(WaylandSurfaceBounds windowBounds, WaylandUiTheme theme)
    {
        WaylandDecorationMetrics metrics = WaylandDecorationMetrics.FromTheme(theme);
        int width = metrics.ButtonSize * 3 + metrics.ButtonGap * 2;
        int x = windowBounds.X + windowBounds.Width - metrics.BorderThickness - metrics.ButtonInset - width;
        int y = windowBounds.Y + (metrics.TitlebarHeight - metrics.ButtonSize) / 2;
        return new WaylandSurfaceBounds(x, y, width, metrics.ButtonSize);
    }

    public static WaylandSurfaceBounds GetMinimizeButtonBounds(WaylandSurfaceBounds windowBounds, WaylandUiTheme theme)
    {
        WaylandDecorationMetrics metrics = WaylandDecorationMetrics.FromTheme(theme);
        WaylandSurfaceBounds row = GetButtonRowBounds(windowBounds, theme);
        return new WaylandSurfaceBounds(row.X, row.Y, metrics.ButtonSize, metrics.ButtonSize);
    }

    public static WaylandSurfaceBounds GetMaximizeButtonBounds(WaylandSurfaceBounds windowBounds, WaylandUiTheme theme)
    {
        WaylandDecorationMetrics metrics = WaylandDecorationMetrics.FromTheme(theme);
        WaylandSurfaceBounds row = GetButtonRowBounds(windowBounds, theme);
        return new WaylandSurfaceBounds(row.X + metrics.ButtonSize + metrics.ButtonGap, row.Y, metrics.ButtonSize, metrics.ButtonSize);
    }

    public static WaylandSurfaceBounds GetCloseButtonBounds(WaylandSurfaceBounds windowBounds, WaylandUiTheme theme)
    {
        WaylandDecorationMetrics metrics = WaylandDecorationMetrics.FromTheme(theme);
        WaylandSurfaceBounds row = GetButtonRowBounds(windowBounds, theme);
        return new WaylandSurfaceBounds(row.X + (metrics.ButtonSize + metrics.ButtonGap) * 2, row.Y, metrics.ButtonSize, metrics.ButtonSize);
    }
}

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
    bool TryGetSceneHitAt(int desktopX, int desktopY, out WaylandSceneHit hit);
    bool TryGetSurfaceBounds(ulong sceneSurfaceId, out WaylandSurfaceBounds bounds);
    bool TryGetWindowBounds(ulong sceneSurfaceId, out WaylandSurfaceBounds bounds);
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
    void SetWindowBounds(ulong sceneSurfaceId, WaylandSurfaceBounds bounds);
    void SetSurfaceDecoration(ulong sceneSurfaceId, WaylandDecorationSceneState decoration);
    void SetSurfaceHidden(ulong sceneSurfaceId, bool hidden);
}
