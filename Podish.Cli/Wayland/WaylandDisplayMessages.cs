using Fiberish.VFS;
using Podish.Display;
using Podish.Wayland;

namespace Podish.Cli.Wayland;

internal enum WaylandDisplayIngressKind
{
    Input,
    BufferConsumed,
    WindowClosed,
    VSyncTick
}

internal readonly record struct WaylandDisplayIngressEvent(
    WaylandDisplayIngressKind Kind,
    DisplayInputEvent InputEvent = default,
    ulong LeaseToken = 0,
    uint Timestamp = 0,
    ulong FrameSequence = 0);

internal enum WaylandDisplayCommandKind
{
    PresentSurface,
    PresentGpuSurface,
    UpdateSurfaceBounds,
    UpdateSurfaceMapping,
    UpdateDecoration,
    SetSurfaceVisibility,
    SetSurfaceLayer,
    RaiseSurface,
    RemoveSurface,
    SetCursor,
    SetSystemCursor,
    UpdateTextInput,
    ClearCursor,
    UpdateDecorationPointer,
    ScheduleFrame
}

internal readonly record struct WaylandDisplayCommand(
    WaylandDisplayCommandKind Kind,
    ulong SceneSurfaceId = 0,
    WaylandShmFrame Frame = default,
    WaylandSurfaceBounds Bounds = default,
    WaylandCursorFrame Cursor = default,
    WaylandSystemCursorShape? SystemCursor = null,
    WaylandRect? TextInputRect = null,
    WaylandDecorationSceneState Decoration = default,
    bool Hidden = false,
    WaylandSceneHit HoveredDecoration = default,
    WaylandSceneHit PressedDecoration = default,
    WaylandSceneLayer Layer = WaylandSceneLayer.Window,
    ulong FrameSequence = 0,
    WaylandGpuFrame GpuFrame = default,
    WaylandSurfaceMapping? Mapping = null)
{
    public LinuxFile? SharedMemoryFile => Kind switch
    {
        WaylandDisplayCommandKind.PresentSurface => Frame.File,
        WaylandDisplayCommandKind.SetCursor => Cursor.Frame.File,
        _ => null
    };

    public void RetainBuffer()
    {
        SharedMemoryFile?.Get();
        if (Kind == WaylandDisplayCommandKind.PresentGpuSurface)
            GpuFrame.Buffer.Retain();
    }

    public void ReleaseBuffer()
    {
        SharedMemoryFile?.Close();
        if (Kind == WaylandDisplayCommandKind.PresentGpuSurface)
            GpuFrame.Buffer.Release();
    }
}
