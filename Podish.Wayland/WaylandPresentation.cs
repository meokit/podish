namespace Podish.Wayland;

public readonly record struct WaylandShmFrame(
    ulong SceneSurfaceId,
    ulong LeaseToken,
    LinuxFile File,
    int Offset,
    int Width,
    int Height,
    int Stride,
    WlShmFormat Format)
{
    public WaylandSurfaceMapping? Mapping { get; init; }
    public int LogicalWidth => Mapping?.Width ?? Width;
    public int LogicalHeight => Mapping?.Height ?? Height;
}

public readonly record struct WaylandGpuFrame(ulong LeaseToken, DrmScanoutBuffer Buffer, bool YInverted)
{
    public WaylandSurfaceMapping? Mapping { get; init; }
    public int LogicalWidth => Mapping?.Width ?? (int)Buffer.Width;
    public int LogicalHeight => Mapping?.Height ?? (int)Buffer.Height;
}

public readonly record struct WaylandSurfaceMapping(int Width, int Height, float U, float V,
    float Ux, float Vx, float Uy, float Vy);

public interface IWaylandGpuFramePresenter
{
    bool CanImportGpuBuffer(DrmScanoutBuffer buffer);
    ValueTask PresentGpuSurfaceAsync(ulong sceneSurfaceId, WaylandGpuFrame frame);
    void UpdateGpuSurfaceMapping(ulong sceneSurfaceId, WaylandSurfaceMapping mapping) { }
}

public readonly record struct WaylandCursorFrame(
    ulong SceneSurfaceId,
    WaylandShmFrame Frame,
    int HotspotX,
    int HotspotY);

public enum WaylandSystemCursorShape
{
    Default,
    Text,
    Pointer,
    Crosshair,
    Move,
    EwResize,
    NsResize,
    NwseResize,
    NeswResize,
    NotAllowed,
    Wait,
    Progress,
    Grab,
    Grabbing,
    Help
}

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
    public int ButtonHeight { get; init; } = ButtonSize;

    public static WaylandDecorationMetrics FromTheme(WaylandUiTheme theme) =>
        new(
            theme.Spacing.WindowBorderThickness,
            theme.Spacing.TitlebarHeight,
            theme.Spacing.ButtonSize,
            theme.Spacing.ButtonGap,
            theme.Spacing.ButtonInset,
            theme.Spacing.ResizeGripThickness)
        {
            ButtonHeight = theme.Spacing.ButtonHeight ?? theme.Spacing.ButtonSize
        };
}

public readonly record struct WaylandDecorationSceneState(
    bool Visible,
    bool Active,
    bool Maximized,
    bool Minimized,
    string Title)
{
    public WaylandApplicationIcon? ApplicationIcon { get; init; }
}

public sealed record WaylandApplicationIcon(ReadOnlyMemory<byte> Data, bool IsSvg);

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

    public static int GetWindowCornerRadius(WaylandSurfaceBounds bounds, WaylandDecorationSceneState decoration,
        WaylandUiTheme theme) =>
        decoration.Visible && !decoration.Maximized
            ? Math.Clamp(theme.Corners.WindowCornerRadius, 0, Math.Min(bounds.Width / 2, theme.Spacing.TitlebarHeight))
            : 0;

    public static WaylandSurfaceBounds GetButtonRowBounds(WaylandSurfaceBounds windowBounds, WaylandUiTheme theme)
    {
        WaylandDecorationMetrics metrics = WaylandDecorationMetrics.FromTheme(theme);
        int width = metrics.ButtonSize * 3 + metrics.ButtonGap * 2;
        int left = windowBounds.X + metrics.BorderThickness;
        int right = Math.Max(left, windowBounds.X + windowBounds.Width - metrics.BorderThickness - metrics.ButtonInset);
        int x = Math.Max(left, right - width);
        int y = windowBounds.Y + (metrics.TitlebarHeight - metrics.ButtonHeight) / 2;
        return new WaylandSurfaceBounds(x, y, right - x, metrics.ButtonHeight);
    }

    public static WaylandSurfaceBounds GetMinimizeButtonBounds(WaylandSurfaceBounds windowBounds, WaylandUiTheme theme) =>
        GetButtonBounds(windowBounds, theme, 2);

    public static WaylandSurfaceBounds GetMaximizeButtonBounds(WaylandSurfaceBounds windowBounds, WaylandUiTheme theme) =>
        GetButtonBounds(windowBounds, theme, 1);

    public static WaylandSurfaceBounds GetCloseButtonBounds(WaylandSurfaceBounds windowBounds, WaylandUiTheme theme) =>
        GetButtonBounds(windowBounds, theme, 0);

    private static WaylandSurfaceBounds GetButtonBounds(WaylandSurfaceBounds windowBounds, WaylandUiTheme theme, int buttonsToRight)
    {
        WaylandDecorationMetrics metrics = WaylandDecorationMetrics.FromTheme(theme);
        WaylandSurfaceBounds row = GetButtonRowBounds(windowBounds, theme);
        int right = row.X + row.Width - (metrics.ButtonSize + metrics.ButtonGap) * buttonsToRight;
        int left = Math.Max(row.X, right - metrics.ButtonSize);
        return new WaylandSurfaceBounds(left, row.Y, Math.Max(0, right - left), row.Height);
    }

    public static WaylandSurfaceBounds GetApplicationIconBounds(WaylandSurfaceBounds windowBounds, WaylandUiTheme theme)
    {
        WaylandSurfaceBounds titlebar = GetTitlebarBounds(windowBounds, theme);
        WaylandSurfaceBounds buttons = GetButtonRowBounds(windowBounds, theme);
        int size = Math.Clamp(titlebar.Height - 8, 0, 18);
        int x = titlebar.X + theme.Spacing.TitlebarHorizontalPadding;
        if (x + size + theme.Spacing.TitlebarHorizontalPadding > buttons.X)
            size = 0;
        return new WaylandSurfaceBounds(x, titlebar.Y + (titlebar.Height - size) / 2, size, size);
    }

    public static WaylandSurfaceBounds GetTitleTextBounds(WaylandSurfaceBounds windowBounds, WaylandUiTheme theme,
        bool reserveIcon = false)
    {
        WaylandDecorationMetrics metrics = WaylandDecorationMetrics.FromTheme(theme);
        WaylandSurfaceBounds titlebar = GetTitlebarBounds(windowBounds, theme);
        WaylandSurfaceBounds buttonRow = GetButtonRowBounds(windowBounds, theme);
        int left = titlebar.X + theme.Spacing.TitlebarHorizontalPadding;
        var icon = reserveIcon ? GetApplicationIconBounds(windowBounds, theme) : default;
        if (icon.Width > 0)
            left = icon.X + icon.Width + theme.Spacing.TitlebarHorizontalPadding;
        int right = buttonRow.X - metrics.ButtonGap - theme.Spacing.TitlebarHorizontalPadding;
        if (theme.Typography.Alignment == WaylandTitleAlignment.Center)
        {
            int margin = Math.Max(left - windowBounds.X, windowBounds.X + windowBounds.Width - right);
            if (margin < windowBounds.Width / 2)
            {
                left = windowBounds.X + margin;
                right = windowBounds.X + windowBounds.Width - margin;
            }
        }
        return new WaylandSurfaceBounds(
            left,
            titlebar.Y,
            Math.Max(0, right - left),
            titlebar.Height);
    }
}

public interface IWaylandDecorationInputPresenter
{
    void UpdateDecorationPointer(WaylandSceneHit hovered, WaylandSceneHit pressed);
}

public interface IWaylandFramePresenter
{
    ValueTask PresentSurfaceAsync(ulong sceneSurfaceId, WaylandShmFrame? frame,
        CancellationToken cancellationToken = default);
}

public interface IWaylandFrameScheduler
{
    /// <summary>Schedules presentation and returns the sequence covering the current surface commits.</summary>
    ulong ScheduleFrame();
}

public interface IWaylandCursorPresenter
{
    ValueTask UpdateCursorAsync(ulong sceneSurfaceId, WaylandCursorFrame? cursor,
        CancellationToken cancellationToken = default);
    ValueTask UpdateSystemCursorAsync(WaylandSystemCursorShape? shape,
        CancellationToken cancellationToken = default);
}

public interface IWaylandTextInputPresenter
{
    ValueTask UpdateTextInputAsync(ulong sceneSurfaceId, WaylandRect? cursorRectangle,
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
    void SetSurfaceLayer(ulong sceneSurfaceId, WaylandSceneLayer layer) { }
}

public enum WaylandSceneLayer
{
    Background = -2,
    Bottom = -1,
    Window = 0,
    Top = 1,
    Fullscreen = 2,
    Overlay = 3
}
