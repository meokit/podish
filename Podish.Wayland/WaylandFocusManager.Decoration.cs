namespace Podish.Wayland;

internal sealed partial class WaylandFocusManager
{
    private WaylandSceneHit _hoveredDecoration;
    private WaylandSceneHit _pressedDecoration;

    private void UpdateDecorationPointer(WaylandSceneHit hit)
    {
        WaylandSceneHit hovered = hit.Kind is WaylandSceneHitKind.CloseButton or
            WaylandSceneHitKind.MaximizeButton or WaylandSceneHitKind.MinimizeButton
            ? new WaylandSceneHit(hit.SceneSurfaceId, hit.Kind, 0, 0)
            : default;
        if (_hoveredDecoration == hovered)
            return;
        _hoveredDecoration = hovered;
        SendDecorationPointer();
    }

    private void UpdateDecorationPress(bool pressed)
    {
        WaylandSceneHit hit = pressed ? _hoveredDecoration : default;
        if (_pressedDecoration == hit)
            return;
        _pressedDecoration = hit;
        SendDecorationPointer();
    }

    private void SendDecorationPointer()
    {
        if (_server.FramePresenter is IWaylandDecorationInputPresenter presenter)
            presenter.UpdateDecorationPointer(_hoveredDecoration, _pressedDecoration);
    }
}
