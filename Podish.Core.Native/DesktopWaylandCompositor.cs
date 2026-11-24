using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;
using Podish.Core.Graphics;
using Podish.Display;
using Podish.Wayland;

namespace Podish.Core.Native;

internal enum DesktopIngressKind
{
    Input,
    BufferConsumed,
    WindowClosed,
    VSyncTick,
    ResizeOutput,
    FocusLauncher,
    ShowHome,
    ActivateToplevel,
    CloseToplevel,
    BackAction,
    ReloadConfig,
    SetGlobalDpi
}

internal readonly record struct DesktopIngressEvent(
    DesktopIngressKind Kind,
    DisplayInputEvent InputEvent = default,
    ulong LeaseToken = 0,
    uint Timestamp = 0,
    ulong FrameSequence = 0,
    int Width = 0,
    int Height = 0,
    ulong SurfaceId = 0,
    uint Scale120 = 0,
    int ClientDpi = 0,
    int ReservedBottomInset = -1);

internal enum DesktopDisplayCommandKind
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
    SetSystemCursor,
    UpdateTextInput,
    ClearCursor,
    UpdateDecorationPointer,
    ScheduleFrame,
    ResizeRenderer
}

internal readonly record struct DesktopDisplayCommand(
    DesktopDisplayCommandKind Kind,
    ulong SceneSurfaceId = 0,
    WaylandShmFrame Frame = default,
    WaylandSurfaceBounds Bounds = default,
    WaylandSystemCursorShape? SystemCursor = null,
    WaylandRect? TextInputRect = null,
    WaylandDecorationSceneState Decoration = default,
    bool Hidden = false,
    WaylandSceneHit HoveredDecoration = default,
    WaylandSceneHit PressedDecoration = default,
    WaylandSceneLayer Layer = WaylandSceneLayer.Window,
    ulong FrameSequence = 0,
    WaylandGpuFrame GpuFrame = default,
    WaylandSurfaceMapping? Mapping = null,
    int LogicalWidth = 0,
    int LogicalHeight = 0,
    int PixelWidth = 0,
    int PixelHeight = 0,
    uint Scale120 = 0,
    int ClientDpi = 0)
{
    public LinuxFile? SharedMemoryFile => Kind == DesktopDisplayCommandKind.PresentSurface ? Frame.File : null;

    public void RetainBuffer()
    {
        SharedMemoryFile?.Get();
        if (Kind == DesktopDisplayCommandKind.PresentGpuSurface)
            GpuFrame.Buffer.Retain();
    }

    public void ReleaseBuffer()
    {
        SharedMemoryFile?.Close();
        if (Kind == DesktopDisplayCommandKind.PresentGpuSurface)
            GpuFrame.Buffer.Release();
    }
}

internal sealed class DesktopIngressBridge
{
    private readonly ConcurrentQueue<DesktopIngressEvent> _pending = [];
    private Action<DesktopIngressEvent>? _sink;

    public void Bind(Action<DesktopIngressEvent> sink)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        while (_pending.TryDequeue(out var ev))
            _sink(ev);
    }

    public void Post(DesktopIngressEvent ev)
    {
        var sink = _sink;
        if (sink != null)
        {
            sink(ev);
            return;
        }
        _pending.Enqueue(ev);
        sink = _sink;
        if (sink == null) return;
        while (_pending.TryDequeue(out ev))
            sink(ev);
    }
}

internal sealed unsafe class DesktopHostCallbacks
{
    public delegate* unmanaged[Cdecl]<nint, int, int, int, int, int, void> OnTextInputChanged;
    public delegate* unmanaged[Cdecl]<nint, int, void> OnCursorChanged;
    public delegate* unmanaged[Cdecl]<nint, int, byte*, void> OnStatusChanged;
    public delegate* unmanaged[Cdecl]<nint, byte*, int, int, float, uint, int, byte*, int, int> RasterizeText;
    public delegate* unmanaged[Cdecl]<nint, byte*, int, int, int, byte*, int, int> RasterizeIcon;
    public delegate* unmanaged[Cdecl]<nint, int, int, void> OnVmSettingsChanged;
    public nint UserData;

    public void NotifyStatus(int state, string message)
    {
        if (OnStatusChanged == null) return;
        var bytes = Encoding.UTF8.GetBytes(message + "\0");
        fixed (byte* ptr = bytes)
            OnStatusChanged(UserData, state, ptr);
    }

    public void NotifyTextInput(bool active, int x, int y, int w, int h)
    {
        if (OnTextInputChanged == null) return;
        OnTextInputChanged(UserData, active ? 1 : 0, x, y, w, h);
    }

    public void NotifyCursor(WaylandSystemCursorShape shape)
    {
        if (OnCursorChanged == null) return;
        OnCursorChanged(UserData, (int)shape);
    }

    public void NotifyVmSettingsChanged(int memoryLimitMb, int blockCacheMb)
    {
        if (OnVmSettingsChanged == null) return;
        OnVmSettingsChanged(UserData, memoryLimitMb, blockCacheMb);
    }

    public byte[]? TryRasterizeText(string title, int width, int height, float fontSizePx, WaylandColor color,
        WaylandTitleAlignment alignment)
    {
        if (RasterizeText == null || width <= 0 || height <= 0) return null;
        var outBytes = new byte[width * height * 4];
        var utf8 = Encoding.UTF8.GetBytes(title + "\0");
        uint rgba = ((uint)color.R << 24) | ((uint)color.G << 16) | ((uint)color.B << 8) | color.A;
        fixed (byte* textPtr = utf8)
        fixed (byte* outPtr = outBytes)
        {
            var ok = RasterizeText(UserData, textPtr, width, height, fontSizePx, rgba, (int)alignment, outPtr, outBytes.Length);
            return ok != 0 ? outBytes : null;
        }
    }

    public byte[]? TryRasterizeIcon(WaylandApplicationIcon icon, int size)
    {
        if (RasterizeIcon == null || size <= 0 || icon.Data.IsEmpty) return null;
        var outBytes = new byte[size * size * 4];
        var data = icon.Data.ToArray();
        fixed (byte* inPtr = data)
        fixed (byte* outPtr = outBytes)
        {
            var ok = RasterizeIcon(UserData, inPtr, data.Length, icon.IsSvg ? 1 : 0, size, outPtr, outBytes.Length);
            return ok != 0 ? outBytes : null;
        }
    }
}

internal sealed class DesktopScenePresenter :
    IWaylandFramePresenter,
    IWaylandGpuFramePresenter,
    IWaylandFrameScheduler,
    IWaylandCursorPresenter,
    IWaylandTextInputPresenter,
    IWaylandSceneView,
    IWaylandSceneDebugView,
    IWaylandDesktopSceneController,
    IWaylandDecorationInputPresenter,
    IDisposable
{
    private readonly WaylandUiTheme _theme;
    private readonly Action<DesktopDisplayCommand> _enqueue;
    private readonly Func<DrmScanoutBuffer, bool>? _canImportGpu;
    private readonly Dictionary<ulong, SurfaceState> _surfaces = [];
    private readonly List<ulong> _zOrder = [];
    private int _desktopWidth;
    private int _desktopHeight;
    private int _reservedBottomInset;
    private ulong _frameSequence;

    public DesktopScenePresenter(int width, int height, WaylandUiTheme theme,
        Action<DesktopDisplayCommand> enqueue, Func<DrmScanoutBuffer, bool>? canImportGpu,
        int reservedBottomInset = 0)
    {
        _desktopWidth = width;
        _desktopHeight = height;
        _reservedBottomInset = Math.Max(0, reservedBottomInset);
        _theme = theme;
        _enqueue = enqueue;
        _canImportGpu = canImportGpu;
    }

    public void SetReservedBottomInset(int inset)
    {
        _reservedBottomInset = Math.Max(0, inset);
    }

    public ulong ScheduleFrame()
    {
        ulong seq = ++_frameSequence;
        _enqueue(new DesktopDisplayCommand(DesktopDisplayCommandKind.ScheduleFrame, FrameSequence: seq));
        return seq;
    }

    public ValueTask PresentSurfaceAsync(ulong sceneSurfaceId, WaylandShmFrame? frame, CancellationToken ct = default)
    {
        if (frame == null)
        {
            _surfaces.Remove(sceneSurfaceId);
            _zOrder.Remove(sceneSurfaceId);
            _enqueue(new DesktopDisplayCommand(DesktopDisplayCommandKind.RemoveSurface, sceneSurfaceId));
            return ValueTask.CompletedTask;
        }

        var bounds = SetSurfaceFrameSize(sceneSurfaceId, frame.Value.LogicalWidth, frame.Value.LogicalHeight);
        _enqueue(new DesktopDisplayCommand(DesktopDisplayCommandKind.PresentSurface, sceneSurfaceId, frame.Value, bounds));
        return ValueTask.CompletedTask;
    }

    public bool CanImportGpuBuffer(DrmScanoutBuffer buffer) => _canImportGpu?.Invoke(buffer) == true;

    public ValueTask PresentGpuSurfaceAsync(ulong sceneSurfaceId, WaylandGpuFrame frame)
    {
        var bounds = SetSurfaceFrameSize(sceneSurfaceId, frame.LogicalWidth, frame.LogicalHeight);
        _enqueue(new DesktopDisplayCommand(DesktopDisplayCommandKind.PresentGpuSurface, sceneSurfaceId, Bounds: bounds, GpuFrame: frame));
        return ValueTask.CompletedTask;
    }

    public void UpdateGpuSurfaceMapping(ulong sceneSurfaceId, WaylandSurfaceMapping mapping)
    {
        if (_surfaces.ContainsKey(sceneSurfaceId))
        {
            var bounds = SetSurfaceFrameSize(sceneSurfaceId, mapping.Width, mapping.Height);
            _enqueue(new DesktopDisplayCommand(DesktopDisplayCommandKind.UpdateSurfaceMapping, sceneSurfaceId,
                Bounds: bounds, Mapping: mapping));
        }
    }

    public ValueTask UpdateCursorAsync(ulong sceneSurfaceId, WaylandCursorFrame? cursor, CancellationToken ct = default)
    {
        _surfaces.Remove(sceneSurfaceId);
        _zOrder.Remove(sceneSurfaceId);
        _enqueue(new DesktopDisplayCommand(DesktopDisplayCommandKind.RemoveSurface, sceneSurfaceId));
        return ValueTask.CompletedTask;
    }

    public ValueTask UpdateSystemCursorAsync(WaylandSystemCursorShape? shape, CancellationToken ct = default)
    {
        _enqueue(new DesktopDisplayCommand(
            shape.HasValue ? DesktopDisplayCommandKind.SetSystemCursor : DesktopDisplayCommandKind.ClearCursor,
            SystemCursor: shape));
        return ValueTask.CompletedTask;
    }

    public ValueTask UpdateTextInputAsync(ulong sceneSurfaceId, WaylandRect? cursorRectangle, CancellationToken ct = default)
    {
        _enqueue(new DesktopDisplayCommand(DesktopDisplayCommandKind.UpdateTextInput, sceneSurfaceId,
            TextInputRect: cursorRectangle));
        return ValueTask.CompletedTask;
    }

    public void UpdateDecorationPointer(WaylandSceneHit hovered, WaylandSceneHit pressed)
    {
        _enqueue(new DesktopDisplayCommand(DesktopDisplayCommandKind.UpdateDecorationPointer,
            HoveredDecoration: hovered, PressedDecoration: pressed));
    }

    public bool TryGetSurfaceAt(int desktopX, int desktopY, out WaylandSurfaceHit hit)
    {
        if (TryGetSceneHitAt(desktopX, desktopY, out var sceneHit))
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
            ulong id = _zOrder[i];
            if (!_surfaces.TryGetValue(id, out var surface) || surface.Hidden)
                continue;
            if (TryHitSurface(surface, desktopX, desktopY, out hit))
                return true;
        }
        hit = default;
        return false;
    }

    public bool TryGetSurfaceBounds(ulong sceneSurfaceId, out WaylandSurfaceBounds bounds)
    {
        if (_surfaces.TryGetValue(sceneSurfaceId, out var surface))
        {
            bounds = surface.ContentBounds;
            return true;
        }
        bounds = default;
        return false;
    }

    public bool TryGetWindowBounds(ulong sceneSurfaceId, out WaylandSurfaceBounds bounds)
    {
        if (_surfaces.TryGetValue(sceneSurfaceId, out var surface))
        {
            bounds = WaylandDecorationLayout.GetWindowBounds(surface.ContentBounds, surface.Decoration, _theme);
            return true;
        }
        bounds = default;
        return false;
    }

    public IReadOnlyList<(ulong SceneSurfaceId, WaylandSurfaceBounds Bounds)> SnapshotSurfaceBounds() =>
        [.. _zOrder.Where(_surfaces.ContainsKey).Select(id => (id, _surfaces[id].ContentBounds))];

    public void ResizeDesktop(int width, int height)
    {
        _desktopWidth = width;
        _desktopHeight = height;
        foreach (var state in _surfaces.Values)
        {
            if (!state.AutoCentered) continue;
            state.ContentBounds = ComputeCenteredContentRect(state.Width, state.Height, state.Decoration);
            _enqueue(new DesktopDisplayCommand(DesktopDisplayCommandKind.UpdateSurfaceBounds, state.SceneSurfaceId,
                Bounds: state.ContentBounds));
        }
    }

    public void RaiseSurface(ulong sceneSurfaceId)
    {
        if (!_surfaces.TryGetValue(sceneSurfaceId, out var state)) return;
        PlaceAtTopOfLayer(sceneSurfaceId, state.Layer);
        _enqueue(new DesktopDisplayCommand(DesktopDisplayCommandKind.RaiseSurface, sceneSurfaceId));
    }

    public void SetSurfaceLayer(ulong sceneSurfaceId, WaylandSceneLayer layer)
    {
        var state = GetOrCreateState(sceneSurfaceId);
        if (state.Layer == layer) return;
        state.Layer = layer;
        if (_zOrder.Contains(sceneSurfaceId))
            PlaceAtTopOfLayer(sceneSurfaceId, layer);
        _enqueue(new DesktopDisplayCommand(DesktopDisplayCommandKind.SetSurfaceLayer, sceneSurfaceId, Layer: layer));
    }

    public void SetSurfaceBounds(ulong sceneSurfaceId, WaylandSurfaceBounds bounds)
    {
        var state = GetOrCreateState(sceneSurfaceId);
        state.AutoCentered = false;
        state.ContentBounds = bounds;
        state.Width = bounds.Width;
        state.Height = bounds.Height;
        _enqueue(new DesktopDisplayCommand(DesktopDisplayCommandKind.UpdateSurfaceBounds, sceneSurfaceId, Bounds: bounds));
    }

    public void SetWindowBounds(ulong sceneSurfaceId, WaylandSurfaceBounds bounds)
    {
        var state = GetOrCreateState(sceneSurfaceId);
        SetSurfaceBounds(sceneSurfaceId, WaylandDecorationLayout.GetContentBoundsFromWindowBounds(bounds, state.Decoration, _theme));
    }

    public void SetSurfaceDecoration(ulong sceneSurfaceId, WaylandDecorationSceneState decoration)
    {
        var state = GetOrCreateState(sceneSurfaceId);
        state.Decoration = decoration;
        if (state.AutoCentered)
            state.ContentBounds = ComputeCenteredContentRect(state.Width, state.Height, decoration);
        _enqueue(new DesktopDisplayCommand(DesktopDisplayCommandKind.UpdateDecoration, sceneSurfaceId, Decoration: decoration));
        _enqueue(new DesktopDisplayCommand(DesktopDisplayCommandKind.UpdateSurfaceBounds, sceneSurfaceId, Bounds: state.ContentBounds));
    }

    public void SetSurfaceHidden(ulong sceneSurfaceId, bool hidden)
    {
        var state = GetOrCreateState(sceneSurfaceId);
        state.Hidden = hidden;
        _enqueue(new DesktopDisplayCommand(DesktopDisplayCommandKind.SetSurfaceVisibility, sceneSurfaceId, Hidden: hidden));
    }

    public void Dispose()
    {
        _surfaces.Clear();
        _zOrder.Clear();
    }

    private WaylandSurfaceBounds SetSurfaceFrameSize(ulong id, int width, int height)
    {
        if (!_surfaces.TryGetValue(id, out var state))
        {
            state = new SurfaceState(width, height)
            {
                SceneSurfaceId = id,
                ContentBounds = ComputeCenteredContentRect(width, height, default),
                AutoCentered = true
            };
            _surfaces[id] = state;
        }
        else
        {
            state.Width = width;
            state.Height = height;
            state.ContentBounds = state.AutoCentered
                ? ComputeCenteredContentRect(width, height, state.Decoration)
                : new WaylandSurfaceBounds(state.ContentBounds.X, state.ContentBounds.Y, width, height);
        }
        if (!_zOrder.Contains(id))
            PlaceAtTopOfLayer(id, state.Layer);
        return state.ContentBounds;
    }

    private void PlaceAtTopOfLayer(ulong id, WaylandSceneLayer layer)
    {
        _zOrder.Remove(id);
        int idx = _zOrder.FindIndex(other => _surfaces[other].Layer > layer);
        _zOrder.Insert(idx < 0 ? _zOrder.Count : idx, id);
    }

    private WaylandSurfaceBounds ComputeCenteredContentRect(int width, int height, WaylandDecorationSceneState decoration)
    {
        var wb = WaylandDecorationLayout.GetWindowBounds(new WaylandSurfaceBounds(0, 0, width, height), decoration, _theme);
        int usableHeight = Math.Max(1, _desktopHeight - _reservedBottomInset);
        int x = Math.Max(-wb.X, (_desktopWidth - wb.Width) / 2 - wb.X);
        int y = Math.Max(-wb.Y, (usableHeight - wb.Height) / 2 - wb.Y);
        return new WaylandSurfaceBounds(x, y, width, height);
    }

    private SurfaceState GetOrCreateState(ulong id)
    {
        if (_surfaces.TryGetValue(id, out var existing)) return existing;
        var state = new SurfaceState(1, 1)
        {
            SceneSurfaceId = id,
            ContentBounds = ComputeCenteredContentRect(1, 1, default),
            AutoCentered = true
        };
        _surfaces[id] = state;
        return state;
    }

    private bool TryHitSurface(SurfaceState surface, int desktopX, int desktopY, out WaylandSceneHit hit)
    {
        var cb = surface.ContentBounds;
        var wb = WaylandDecorationLayout.GetWindowBounds(cb, surface.Decoration, _theme);
        if (desktopX < wb.X || desktopY < wb.Y || desktopX >= wb.X + wb.Width || desktopY >= wb.Y + wb.Height)
        {
            hit = default;
            return false;
        }
        int sx = desktopX - cb.X, sy = desktopY - cb.Y;
        if (desktopX >= cb.X && desktopY >= cb.Y && desktopX < cb.X + cb.Width && desktopY < cb.Y + cb.Height)
        {
            hit = new WaylandSceneHit(surface.SceneSurfaceId, WaylandSceneHitKind.Surface, sx, sy);
            return true;
        }
        if (surface.Decoration.Visible && !surface.Decoration.Minimized)
        {
            if (TryHitDecoration(surface, wb, desktopX, desktopY, sx, sy, out hit))
                return true;
        }
        hit = default;
        return false;
    }

    private bool TryHitDecoration(SurfaceState surface, WaylandSurfaceBounds wb, int dx, int dy, int sx, int sy, out WaylandSceneHit hit)
    {
        static bool Contains(WaylandSurfaceBounds r, int x, int y) =>
            x >= r.X && y >= r.Y && x < r.X + r.Width && y < r.Y + r.Height;

        if (Contains(WaylandDecorationLayout.GetCloseButtonBounds(wb, _theme), dx, dy))
        {
            hit = new WaylandSceneHit(surface.SceneSurfaceId, WaylandSceneHitKind.CloseButton, sx, sy);
            return true;
        }
        if (Contains(WaylandDecorationLayout.GetMaximizeButtonBounds(wb, _theme), dx, dy))
        {
            hit = new WaylandSceneHit(surface.SceneSurfaceId, WaylandSceneHitKind.MaximizeButton, sx, sy);
            return true;
        }
        if (Contains(WaylandDecorationLayout.GetMinimizeButtonBounds(wb, _theme), dx, dy))
        {
            hit = new WaylandSceneHit(surface.SceneSurfaceId, WaylandSceneHitKind.MinimizeButton, sx, sy);
            return true;
        }

        var metrics = WaylandDecorationMetrics.FromTheme(_theme);
        XdgToplevelResizeEdge edges = XdgToplevelResizeEdge.None;
        int border = metrics.ResizeGripThickness;
        if (dx < surface.ContentBounds.X) edges |= XdgToplevelResizeEdge.Left;
        else if (dx >= surface.ContentBounds.X + surface.ContentBounds.Width) edges |= XdgToplevelResizeEdge.Right;
        if (dy < surface.ContentBounds.Y) edges |= XdgToplevelResizeEdge.Top;
        else if (dy >= surface.ContentBounds.Y + surface.ContentBounds.Height) edges |= XdgToplevelResizeEdge.Bottom;

        bool inResize = dx < wb.X + border || dx >= wb.X + wb.Width - border ||
                        dy < wb.Y + border || dy >= wb.Y + wb.Height - border;
        if (edges != XdgToplevelResizeEdge.None && inResize)
        {
            hit = new WaylandSceneHit(surface.SceneSurfaceId, WaylandSceneHitKind.ResizeBorder, sx, sy, edges);
            return true;
        }

        hit = new WaylandSceneHit(surface.SceneSurfaceId, WaylandSceneHitKind.Titlebar, sx, sy);
        return true;
    }

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

internal sealed class DesktopApplicationIcons(SyscallManager syscalls, ILogger logger, int targetPixels)
{
    private static readonly string[] DataDirs = ["/usr/local/share", "/usr/share"];
    private static readonly int[] Sizes = [16, 22, 24, 32, 48, 64, 128, 256];
    private readonly int[] _orderedSizes = Sizes.OrderBy(s => s < targetPixels).ThenBy(s => Math.Abs(s - targetPixels)).ToArray();

    public WaylandApplicationIcon? Resolve(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId) || appId.Length > 256) return null;
        try
        {
            string name = appId.EndsWith(".desktop", StringComparison.Ordinal) ? appId[..^8] : appId;
            string shortName = name[(name.LastIndexOf('.') + 1)..];
            string? icon = ReadDesktopIcon(name) ?? (shortName != name ? ReadDesktopIcon(shortName) : null);
            return (icon != null ? FindIcon(icon) : null) ?? FindIcon(name) ?? (shortName != name ? FindIcon(shortName) : null);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not resolve icon for {AppId}", appId);
            return null;
        }
    }

    private string? ReadDesktopIcon(string name)
    {
        foreach (var dir in DataDirs)
        {
            var bytes = ReadFile($"{dir}/applications/{name}.desktop", 64 * 1024);
            if (bytes == null) continue;
            bool inEntry = false;
            using var reader = new StringReader(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
            while (reader.ReadLine() is { } line)
            {
                var span = line.AsSpan().Trim();
                if (span.StartsWith("["))
                {
                    if (inEntry) break;
                    inEntry = span.SequenceEqual("[Desktop Entry]");
                }
                if (inEntry && span.StartsWith("Icon=", StringComparison.Ordinal))
                    return span[5..].Trim().ToString();
            }
        }
        return null;
    }

    private WaylandApplicationIcon? FindIcon(string icon)
    {
        if (icon.StartsWith('/')) return ReadIcon(icon);
        bool hasExt = icon.EndsWith(".png", StringComparison.Ordinal) || icon.EndsWith(".svg", StringComparison.Ordinal);
        foreach (var data in DataDirs)
        foreach (var dir in IconDirs(data))
        {
            var res = hasExt ? ReadIcon($"{dir}/{icon}") : ReadIcon($"{dir}/{icon}.png") ?? ReadIcon($"{dir}/{icon}.svg");
            if (res != null) return res;
        }
        return null;
    }

    private IEnumerable<string> IconDirs(string data)
    {
        foreach (var s in _orderedSizes) yield return $"{data}/icons/hicolor/{s}x{s}/apps";
        yield return $"{data}/icons/hicolor/scalable/apps";
        foreach (var s in _orderedSizes) yield return $"{data}/icons/breeze/apps/{s}";
        yield return $"{data}/pixmaps";
        yield return $"{data}/icons";
    }

    private WaylandApplicationIcon? ReadIcon(string path)
    {
        bool svg = path.EndsWith(".svg", StringComparison.Ordinal);
        if (!svg && !path.EndsWith(".png", StringComparison.Ordinal)) return null;
        var bytes = ReadFile(path, 512 * 1024);
        return bytes == null ? null : new WaylandApplicationIcon(bytes, svg);
    }

    private byte[]? ReadFile(string path, int limit)
    {
        var loc = syscalls.PathWalk(path);
        var inode = loc.Dentry?.Inode;
        if (inode == null || inode.Type != InodeType.File || inode.Size == 0 || inode.Size > (ulong)limit)
            return null;
        using var file = new LinuxFile(loc, FileFlags.O_RDONLY);
        var bytes = new byte[(int)inode.Size];
        var segs = inode.GetReadableSegments(file, 0, bytes.Length);
        int offset = 0;
        foreach (var seg in segs)
        {
            seg.CopyTo(bytes.AsSpan(offset));
            offset += seg.Length;
        }
        return segs.Succeeded && offset == bytes.Length ? bytes : null;
    }
}

internal sealed class DesktopVmSettingsState
{
    private readonly Lock _gate = new();
    private readonly DesktopHostCallbacks _callbacks;

    public DesktopVmSettingsState(int initialMemoryLimitMb, int initialBlockCacheMb, DesktopHostCallbacks callbacks)
    {
        ActiveMemoryLimitMb = Math.Max(0, initialMemoryLimitMb);
        ActiveBlockCacheMb = Math.Max(0, initialBlockCacheMb);
        SavedMemoryLimitMb = ActiveMemoryLimitMb;
        SavedBlockCacheMb = ActiveBlockCacheMb;
        _callbacks = callbacks;
    }

    public int ActiveMemoryLimitMb { get; }
    public int ActiveBlockCacheMb { get; }
    public int SavedMemoryLimitMb { get; private set; }
    public int SavedBlockCacheMb { get; private set; }

    public (int MemoryLimitMb, int BlockCacheMb, int ActiveMemoryLimitMb, int ActiveBlockCacheMb) Snapshot()
    {
        lock (_gate)
            return (SavedMemoryLimitMb, SavedBlockCacheMb, ActiveMemoryLimitMb, ActiveBlockCacheMb);
    }

    public bool UpdateSaved(int memoryLimitMb, int blockCacheMb)
    {
        memoryLimitMb = Math.Max(0, memoryLimitMb);
        blockCacheMb = Math.Max(0, blockCacheMb);
        bool requiresRestart;
        lock (_gate)
        {
            SavedMemoryLimitMb = memoryLimitMb;
            SavedBlockCacheMb = blockCacheMb;
            requiresRestart = memoryLimitMb != ActiveMemoryLimitMb || blockCacheMb != ActiveBlockCacheMb;
        }
        _callbacks.NotifyVmSettingsChanged(memoryLimitMb, blockCacheMb);
        return requiresRestart;
    }
}

internal sealed class DesktopDisplayPolicy
{
    public const string ConfigPath = "/home/user/.config/podish/compositor.json";

    private readonly Lock _gate = new();
    private readonly bool _mobileMode;
    private readonly ILogger _logger;
    private int _globalDpi;
    private Dictionary<string, int> _appDpis = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _desktopAliasMap = new(StringComparer.OrdinalIgnoreCase);

    public DesktopDisplayPolicy(bool mobileMode, int initialGlobalDpi, ILogger logger)
    {
        _mobileMode = mobileMode;
        _globalDpi = Math.Max(0, initialGlobalDpi);
        _logger = logger;
    }

    public void SetGlobalDpiOverride(int globalDpi)
    {
        lock (_gate)
            _globalDpi = Math.Max(0, globalDpi);
    }

    public void ReloadFromVfs(SyscallManager syscalls)
    {
        var desktopAliases = ScanDesktopAliases(syscalls);
        int nextGlobal = -1;
        Dictionary<string, int>? nextApps = null;

        var bytes = ReadVfsFile(syscalls, ConfigPath, 256 * 1024);
        if (bytes != null && bytes.Length > 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(bytes);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("globalDpi", out var gProp) && gProp.TryGetInt32(out int gVal))
                        nextGlobal = gVal <= 0 ? 0 : Math.Clamp(gVal, 72, 384);

                    if (root.TryGetProperty("appDpis", out var aProp) && aProp.ValueKind == JsonValueKind.Object)
                    {
                        nextApps = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        foreach (var prop in aProp.EnumerateObject())
                        {
                            if (!string.IsNullOrWhiteSpace(prop.Name) && prop.Value.TryGetInt32(out int dpi) && dpi > 0)
                                nextApps[NormalizeId(prop.Name)] = Math.Clamp(dpi, 72, 384);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse compositor config at {Path}", ConfigPath);
            }
        }

        lock (_gate)
        {
            _desktopAliasMap = desktopAliases;
            if (nextGlobal >= 0) _globalDpi = nextGlobal;
            if (nextApps != null) _appDpis = nextApps;
        }
    }

    public WaylandServer.OutputInfo ResolveClientOutput(WaylandClient client, WaylandServer server)
    {
        var canvas = server.Output;
        // Whitelist Launcher & Launcher Settings so they always render at host native DPI/scale.
        if (client.IsWhitelistedLauncher)
            return canvas;

        int targetDpi;
        lock (_gate)
        {
            targetDpi = ResolveAppDpiLocked(client.AppIdHint)
                        ?? ResolveAppDpiLocked(client.PeerExecutableName)
                        ?? ResolveEffectiveGlobalDpiLocked(canvas);
        }

        if (targetDpi <= 0)
            return canvas;

        uint scale120 = (uint)Math.Clamp((int)Math.Round(targetDpi * 120.0 / 96.0), 60, 480);
        int width = Math.Clamp((int)Math.Round(canvas.PixelWidth * 120.0 / scale120), 320, FramebufferDevice.DimensionLimit);
        int height = Math.Clamp((int)Math.Round(canvas.PixelHeight * 120.0 / scale120), 320, FramebufferDevice.DimensionLimit);
        int intScale = Math.Max(1, (int)((scale120 + 119) / 120));

        return new WaylandServer.OutputInfo(width, height, intScale, canvas.Name, canvas.Description, canvas.Make, canvas.Model)
        {
            Scale120 = scale120,
            ClientDpi = targetDpi
        };
    }

    private int ResolveEffectiveGlobalDpiLocked(WaylandServer.OutputInfo canvas)
    {
        if (_globalDpi > 0) return _globalDpi;
        if (_mobileMode && Math.Min(canvas.Width, canvas.Height) < 540)
            return 168; // 1.75x balanced mobile default for compact phone screens
        return 0;       // follow host native output
    }

    private int? ResolveAppDpiLocked(string? rawId)
    {
        if (string.IsNullOrWhiteSpace(rawId)) return null;
        string normalized = NormalizeId(rawId);
        if (_appDpis.TryGetValue(normalized, out int direct) && direct > 0)
            return direct;
        if (_desktopAliasMap.TryGetValue(normalized, out string? mapped) &&
            _appDpis.TryGetValue(mapped, out int aliased) && aliased > 0)
            return aliased;

        int dot = normalized.LastIndexOf('.');
        if (dot >= 0 && dot + 1 < normalized.Length)
        {
            string shortName = normalized[(dot + 1)..];
            if (_appDpis.TryGetValue(shortName, out int shortDpi) && shortDpi > 0)
                return shortDpi;
            if (_desktopAliasMap.TryGetValue(shortName, out string? mappedShort) &&
                _appDpis.TryGetValue(mappedShort, out int aliasedShort) && aliasedShort > 0)
                return aliasedShort;
        }
        return null;
    }

    private static string NormalizeId(string raw)
    {
        string trimmed = raw.Trim();
        if (trimmed.EndsWith(".desktop", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^8];
        return trimmed.ToLowerInvariant();
    }

    private static Dictionary<string, string> ScanDesktopAliases(SyscallManager syscalls)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string[] dirs = ["/usr/local/share/applications", "/usr/share/applications", "/home/user/.local/share/applications"];
        foreach (string dir in dirs)
        {
            var dirLoc = syscalls.PathWalk(dir);
            if (dirLoc.Dentry?.Inode is not { Type: InodeType.Directory } dirInode)
                continue;
            foreach (var entry in dirInode.GetEntries())
            {
                string entryName = entry.Name.ToString();
                if (!entryName.EndsWith(".desktop", StringComparison.OrdinalIgnoreCase))
                    continue;
                string desktopId = NormalizeId(entryName);
                map[desktopId] = desktopId;

                var bytes = ReadVfsFile(syscalls, $"{dir}/{entryName}", 64 * 1024);
                if (bytes == null) continue;
                bool inEntry = false;
                using var reader = new StringReader(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
                while (reader.ReadLine() is { } line)
                {
                    var span = line.AsSpan().Trim();
                    if (span.StartsWith("["))
                    {
                        if (inEntry) break;
                        inEntry = span.SequenceEqual("[Desktop Entry]");
                        continue;
                    }
                    if (!inEntry) continue;
                    if (span.StartsWith("Exec=", StringComparison.Ordinal))
                    {
                        var cmd = span[5..].Trim();
                        if (cmd.Length > 0)
                        {
                            int sp = cmd.IndexOf(' ');
                            string token = (sp > 0 ? cmd[..sp] : cmd).Trim('"').ToString();
                            string exe = Path.GetFileName(token);
                            if (!string.IsNullOrWhiteSpace(exe))
                                map.TryAdd(exe.ToLowerInvariant(), desktopId);
                        }
                    }
                    else if (span.StartsWith("StartupWMClass=", StringComparison.Ordinal))
                    {
                        string wm = span[15..].Trim().ToString();
                        if (!string.IsNullOrWhiteSpace(wm))
                            map.TryAdd(wm.ToLowerInvariant(), desktopId);
                    }
                }
            }
        }
        return map;
    }

    private static byte[]? ReadVfsFile(SyscallManager syscalls, string path, int limit)
    {
        try
        {
            var loc = syscalls.PathWalk(path);
            var inode = loc.Dentry?.Inode;
            if (inode == null || inode.Type != InodeType.File || inode.Size == 0 || inode.Size > (ulong)limit)
                return null;
            using var file = new LinuxFile(loc, FileFlags.O_RDONLY);
            var bytes = new byte[(int)inode.Size];
            var segs = inode.GetReadableSegments(file, 0, bytes.Length);
            int offset = 0;
            foreach (var seg in segs)
            {
                seg.CopyTo(bytes.AsSpan(offset));
                offset += seg.Length;
            }
            return segs.Succeeded && offset == bytes.Length ? bytes : null;
        }
        catch
        {
            return null;
        }
    }
}

internal sealed class DesktopControlDaemon : IVirtualDaemon
{
    private readonly ILogger _logger;
    private readonly int _ownerPid;
    private readonly DesktopIngressBridge _bridge;
    private readonly DesktopVmSettingsState _vmSettings;
    private readonly bool _mobileMode;

    public DesktopControlDaemon(string unixPath, int ownerPid, ILoggerFactory loggerFactory,
        DesktopIngressBridge bridge, DesktopVmSettingsState vmSettings, bool mobileMode)
    {
        UnixPath = unixPath;
        _ownerPid = ownerPid;
        _logger = loggerFactory.CreateLogger<DesktopControlDaemon>();
        _bridge = bridge;
        _vmSettings = vmSettings;
        _mobileMode = mobileMode;
    }

    public string Name => "podish-control";
    public string UnixPath { get; }

    public void OnStart(VirtualDaemonContext context)
    {
        context.Schedule(async ctx =>
        {
            ctx.EnsureUnixListener().Flags |= FileFlags.O_NONBLOCK;
            while (!ctx.Task.Exited)
            {
                if (_ownerPid > 0)
                {
                    var owner = ctx.Scheduler.GetProcess(_ownerPid);
                    if (owner == null || owner.State is ProcessState.Zombie or ProcessState.Dead)
                    {
                        ctx.Exit(0);
                        return;
                    }
                }

                var (rc, conn) = await ctx.AcceptAsync();
                if (ctx.Task.Exited) return;
                if (rc == -(int)Errno.EAGAIN)
                {
                    await new SleepAwaitable(20, ctx.Task, ctx.Scheduler);
                    continue;
                }
                if (rc != 0 || conn == null) return;

                ctx.ScheduleChild(childCtx =>
                {
                    conn.BindTask(childCtx.Task);
                    return new ValueTask(HandleControlClientAsync(conn));
                });
            }
        });
    }

    public void OnSignal(VirtualDaemonContext context, int signo) => context.Exit(128 + signo);
    public void OnStop(VirtualDaemonContext context) { }

    private async Task HandleControlClientAsync(VirtualDaemonConnection connection)
    {
        try
        {
            using (connection)
            {
                connection.File.Flags &= ~FileFlags.O_NONBLOCK;
                var buf = new byte[4096];
                var sb = new StringBuilder();
                while (true)
                {
                    int n = await connection.RecvAsync(buf);
                    if (n <= 0) break;
                    sb.Append(Encoding.UTF8.GetString(buf, 0, n));
                    while (true)
                    {
                        string current = sb.ToString();
                        int nl = current.IndexOf('\n');
                        if (nl < 0) break;
                        string line = current[..nl].Trim();
                        sb.Clear();
                        sb.Append(current[(nl + 1)..]);
                        if (line.Length == 0) continue;
                        string reply = HandleCommand(line);
                        await connection.SendAsync(Encoding.UTF8.GetBytes(reply + "\n"));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Compositor control client closed");
        }
    }

    private string HandleCommand(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            string cmd = root.TryGetProperty("cmd", out var cmdProp) ? (cmdProp.GetString() ?? "") : "";
            switch (cmd)
            {
                case "reload_config":
                    _bridge.Post(new DesktopIngressEvent(DesktopIngressKind.ReloadConfig));
                    return """{"ok":true}""";

                case "get_vm_settings":
                {
                    var s = _vmSettings.Snapshot();
                    return string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"{{\"ok\":true,\"memoryLimitMb\":{s.MemoryLimitMb},\"blockCacheMb\":{s.BlockCacheMb},\"activeMemoryLimitMb\":{s.ActiveMemoryLimitMb},\"activeBlockCacheMb\":{s.ActiveBlockCacheMb},\"mobileMode\":{(_mobileMode ? "true" : "false")}}}");
                }

                case "set_vm_settings":
                {
                    int mem = root.TryGetProperty("memoryLimitMb", out var mp) && mp.TryGetInt32(out int mv) ? mv : _vmSettings.SavedMemoryLimitMb;
                    int blk = root.TryGetProperty("blockCacheMb", out var bp) && bp.TryGetInt32(out int bv) ? bv : _vmSettings.SavedBlockCacheMb;
                    bool restart = _vmSettings.UpdateSaved(mem, blk);
                    return string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"{{\"ok\":true,\"memoryLimitMb\":{Math.Max(0, mem)},\"blockCacheMb\":{Math.Max(0, blk)},\"requiresRestart\":{(restart ? "true" : "false")}}}");
                }

                default:
                    return """{"ok":false,"error":"unknown command"}""";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Invalid control command: {Cmd}", line);
            return """{"ok":false,"error":"invalid json"}""";
        }
    }
}

internal sealed class DesktopWaylandDaemon : IVirtualDaemon
{
    private readonly ILogger _logger;
    private readonly int _ownerPid;
    private readonly ConcurrentQueue<DesktopIngressEvent> _ingressQueue = [];
    private readonly DesktopIngressBridge _bridge;
    private readonly WaylandServer _server;
    private readonly DesktopDisplayPolicy _displayPolicy;
    private DesktopApplicationIcons? _icons;
    private int _scheduled;

    public DesktopWaylandDaemon(string unixPath, int ownerPid, ILoggerFactory loggerFactory,
        IWaylandFramePresenter presenter, DesktopIngressBridge bridge,
        int width, int height, bool enableGpuBuffers, uint scale120,
        bool mobileMode = false, int reservedBottomInset = 0, int initialGlobalDpi = 0,
        Action<WaylandServer.ToplevelSnapshot[]>? onToplevelsChanged = null)
    {
        UnixPath = unixPath;
        _ownerPid = ownerPid;
        _logger = loggerFactory.CreateLogger<DesktopWaylandDaemon>();
        _bridge = bridge;
        _displayPolicy = new DesktopDisplayPolicy(mobileMode, initialGlobalDpi, _logger);
        _server = new WaylandServer(presenter, new WaylandServer.OutputInfo(
            width, height, Math.Max(1, (int)((scale120 + 119) / 120)),
            "WL-1", "Podish Apple Display", "Podish", "Metal ANGLE")
        {
            Scale120 = scale120,
            ClientDpi = (int)Math.Round(scale120 * 96.0 / 120.0)
        },
            _logger, appId => _icons?.Resolve(appId), enableGpuBuffers)
        {
            MobileMode = mobileMode,
            ReservedBottomInset = Math.Max(0, reservedBottomInset),
            OnToplevelsChanged = onToplevelsChanged
        };
        _server.ClientOutputResolver = client => _displayPolicy.ResolveClientOutput(client, _server);
    }

    public string Name => "podish-wayland";
    public string UnixPath { get; }

    public void OnStart(VirtualDaemonContext context)
    {
        int iconPixels = (int)((18 * _server.Output.Scale120 + 60) / 120);
        _icons = new DesktopApplicationIcons(context.Syscalls, _logger, iconPixels);
        _displayPolicy.ReloadFromVfs(context.Syscalls);
        _bridge.Bind(ev => PostIngress(context, ev));
        ScheduleAcceptLoop(context);
    }

    public void OnSignal(VirtualDaemonContext context, int signo) => context.Exit(128 + signo);

    public void OnStop(VirtualDaemonContext context)
    {
        _server.ShutdownAsync().AsTask().GetAwaiter().GetResult();
    }

    private void ScheduleAcceptLoop(VirtualDaemonContext context)
    {
        context.Schedule(async ctx =>
        {
            ctx.EnsureUnixListener().Flags |= FileFlags.O_NONBLOCK;
            while (!ctx.Task.Exited)
            {
                if (_ownerPid > 0)
                {
                    var owner = ctx.Scheduler.GetProcess(_ownerPid);
                    if (owner == null || owner.State is ProcessState.Zombie or ProcessState.Dead)
                    {
                        ctx.Exit(0);
                        return;
                    }
                }

                var (rc, conn) = await ctx.AcceptAsync();
                if (ctx.Task.Exited) return;
                if (rc == -(int)Errno.EAGAIN)
                {
                    await new SleepAwaitable(20, ctx.Task, ctx.Scheduler);
                    continue;
                }
                if (rc != 0 || conn == null) return;

                string? peerExeName = null;
                if (conn.File.OpenedInode is UnixSocketInode sock)
                {
                    var creds = sock.GetPeerCredentials();
                    if (creds != null)
                    {
                        sock.SetSendCredentialsOverride(creds);
                        var proc = ctx.Scheduler.GetProcess(creds.Pid);
                        if (proc != null)
                        {
                            peerExeName = !string.IsNullOrEmpty(proc.ExecutablePath)
                                ? Path.GetFileName(proc.ExecutablePath)
                                : proc.Comm;
                        }
                    }
                }

                ctx.ScheduleChild(childCtx =>
                {
                    conn.BindTask(childCtx.Task);
                    return new ValueTask(HandleClientAsync(conn, peerExeName));
                });
            }
        });
    }

    private void PostIngress(VirtualDaemonContext context, DesktopIngressEvent ev)
    {
        _ingressQueue.Enqueue(ev);
        context.Scheduler.ScheduleFromAnyThread(() => RequestPump(context));
    }

    private void RequestPump(VirtualDaemonContext context)
    {
        if (Interlocked.Exchange(ref _scheduled, 1) != 0) return;
        context.ScheduleChild(_ => new ValueTask(RunPumpAsync(context)));
    }

    private async Task RunPumpAsync(VirtualDaemonContext context)
    {
        try
        {
            while (_ingressQueue.TryDequeue(out var ev))
                if (!await HandleIngressAsync(ev, context))
                    return;
        }
        finally
        {
            Interlocked.Exchange(ref _scheduled, 0);
            if (!_ingressQueue.IsEmpty)
                RequestPump(context);
        }
    }

    private async Task HandleClientAsync(VirtualDaemonConnection connection, string? peerExeName)
    {
        WaylandClient? client = null;
        try
        {
            using (connection)
            {
                var wlConn = new WaylandConnection(connection);
                client = _server.CreateClient(msg => wlConn.SendAsync(msg));
                client.PeerExecutableName = peerExeName;
                client.OnClosed += () =>
                {
                    try { wlConn.RawConnection.Dispose(); } catch { }
                };

                wlConn.RawConnection.File.Flags &= ~FileFlags.O_NONBLOCK;
                while (true)
                {
                    var message = await wlConn.ReceiveAsync();
                    if (message == null) return;
                    int fds = client.GetExpectedFdCount(message.Header);
                    if (fds > 0)
                        message = message with { Fds = wlConn.TakePendingFds(fds) };
                    try
                    {
                        await client.ProcessMessageAsync(message);
                    }
                    catch (WaylandProtocolException ex)
                    {
                        _logger.LogWarning(ex, "Wayland protocol error obj={Obj} code={Code}", ex.ObjectId, ex.ErrorCode);
                        await client.SendProtocolErrorAsync(ex.ObjectId, ex.ErrorCode, ex.Message);
                        return;
                    }
                }
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Wayland client disconnected");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Wayland client session failed");
        }
        finally
        {
            if (client != null)
                await _server.DisconnectClientAsync(client);
        }
    }

    private async ValueTask<bool> HandleIngressAsync(DesktopIngressEvent ev, VirtualDaemonContext context)
    {
        switch (ev.Kind)
        {
            case DesktopIngressKind.Input:
                await HandleInputAsync(ev.InputEvent);
                return true;
            case DesktopIngressKind.BufferConsumed:
                await _server.HandleBufferConsumedAsync(ev.LeaseToken);
                return true;
            case DesktopIngressKind.VSyncTick:
                await _server.HandlePresentationTickAsync(
                    ev.Timestamp != 0 ? ev.Timestamp : unchecked((uint)Environment.TickCount64),
                    ev.FrameSequence);
                return true;
            case DesktopIngressKind.ResizeOutput:
                if (ev.ReservedBottomInset >= 0 && _server.MobileMode)
                {
                    _server.ReservedBottomInset = ev.ReservedBottomInset;
                    if (_server.FramePresenter is DesktopScenePresenter dsp)
                        dsp.SetReservedBottomInset(ev.ReservedBottomInset);
                }
                await _server.ReconfigureOutputAsync(
                    ev.Width,
                    ev.Height,
                    ev.Scale120 > 0 ? ev.Scale120 : _server.Output.Scale120,
                    ev.ClientDpi > 0 ? ev.ClientDpi : _server.Output.ClientDpi);
                return true;
            case DesktopIngressKind.FocusLauncher:
                await _server.FocusToplevelByAppIdAsync("podish-launcher");
                return true;
            case DesktopIngressKind.ShowHome:
                await _server.ShowHomeLauncherAsync("podish-launcher");
                return true;
            case DesktopIngressKind.ActivateToplevel:
                await _server.ActivateToplevelAsync(ev.SurfaceId);
                return true;
            case DesktopIngressKind.CloseToplevel:
                await _server.CloseToplevelAsync(ev.SurfaceId);
                return true;
            case DesktopIngressKind.BackAction:
                await _server.SendBackKeyAsync(ev.Timestamp != 0 ? ev.Timestamp : unchecked((uint)Environment.TickCount64));
                return true;
            case DesktopIngressKind.ReloadConfig:
                _displayPolicy.ReloadFromVfs(context.Syscalls);
                await _server.RefreshAllClientOutputsAsync();
                return true;
            case DesktopIngressKind.SetGlobalDpi:
                _displayPolicy.SetGlobalDpiOverride(ev.ClientDpi);
                await _server.RefreshAllClientOutputsAsync();
                return true;
            case DesktopIngressKind.WindowClosed:
                context.Exit(0);
                return false;
            default:
                return true;
        }
    }

    private async ValueTask HandleInputAsync(DisplayInputEvent input)
    {
        switch (input.Kind)
        {
            case DisplayInputEventKind.PointerMotion:
                await _server.HandlePointerMotionAsync(input.X, input.Y, input.Timestamp);
                break;
            case DisplayInputEventKind.PointerButton:
                await _server.HandlePointerMotionAsync(input.X, input.Y, input.Timestamp);
                await _server.HandlePointerButtonAsync((uint)input.Button, input.Pressed, input.Timestamp);
                break;
            case DisplayInputEventKind.PointerWheel:
                await _server.HandlePointerAxisAsync(input.DeltaX, input.DeltaY, input.Timestamp);
                break;
            case DisplayInputEventKind.PointerLeave:
                await _server.ClearPointerFocusAsync();
                break;
            case DisplayInputEventKind.KeyboardKey:
                await _server.HandleKeyboardKeyAsync(input.Key, input.Pressed, input.Timestamp, input.HandledByIme);
                break;
            case DisplayInputEventKind.TextInput:
                await _server.HandleTextInputCommitAsync(input.Text ?? string.Empty);
                break;
            case DisplayInputEventKind.TextEditing:
                await _server.HandleTextInputPreeditAsync(input.Text ?? string.Empty, input.CursorBegin, input.CursorEnd);
                break;
            case DisplayInputEventKind.WindowFocusLost:
                await _server.HandleHostTextInputFocusLostAsync();
                break;
            case DisplayInputEventKind.WindowFocusGained:
                await _server.HandleHostTextInputFocusGainedAsync();
                break;
        }
    }
}

internal sealed class DesktopCompositorHost : IDisposable
{
    private const double RecentsAnimDurationSec = 0.24;
    private const double CardDismissAnimDurationSec = 0.20;

    private readonly ILogger _logger;
    private readonly string _angleDir;
    private readonly nint _metalLayer;
    private readonly bool _enableGles;
    private readonly bool _mobileMode;
    private readonly DesktopHostCallbacks _callbacks;
    private readonly WaylandUiTheme _theme;
    private readonly ConcurrentQueue<DesktopDisplayCommand> _commands = [];
    private readonly AutoResetEvent _wake = new(false);
    private readonly object _commandLock = new();
    private readonly object _uiLock = new();
    private readonly Dictionary<ulong, SurfaceRenderState> _surfaces = [];
    private readonly List<ulong> _zOrder = [];
    private readonly Dictionary<(WaylandSceneHitKind, WaylandColor, WaylandButtonBitmap?), IDisplayTexture> _buttonIcons = [];
    private readonly Dictionary<(string Text, int Width, int Height, int Align), IDisplayTexture> _uiTextCache = [];

    private DesktopAngleRenderer? _renderer;
    private int _logicalWidth;
    private int _logicalHeight;
    private int _pixelWidth;
    private int _pixelHeight;
    private uint _scale120;
    private int _clientDpi;
    private int _visible = 1;
    private ulong _sceneFrameSequence;
    private ulong _scanoutFrameSequence;
    private WaylandSceneHit _hoveredDecoration;
    private WaylandSceneHit _pressedDecoration;
    private ulong _textInputSurfaceId;
    private WaylandRect? _textInputRect;
    private bool _lastNotifiedTextInputActive;
    private int _lastNotifiedTextInputX = int.MinValue;
    private int _lastNotifiedTextInputY = int.MinValue;
    private int _lastNotifiedTextInputW = int.MinValue;
    private int _lastNotifiedTextInputH = int.MinValue;
    private bool _disposed;
    private volatile bool _stopRequested;
    private Thread? _renderThread;
    private Exception? _initException;

    // Mobile shell, Android 3-button navbar, Android Recents overview & Trackpad state (guarded by _uiLock)
    private bool _trackpadMode;
    private int _cursorX;
    private int _cursorY;
    private int _navPressedButton = -1; // -1 none, 0 Back, 1 Home, 2 Recents
    private bool _uiDirty;

    private readonly List<RecentsCardItem> _recentsCards = [];
    private readonly List<DismissingCardAnim> _dismissingCards = [];
    private bool _recentsTargetOpen;
    private float _recentsProgress;
    private float _recentsAnimStartProgress;
    private double _recentsAnimStartTime;
    private ulong _recentsSourceSurfaceId;
    private float _recentsScrollX;
    private float _recentsTargetScrollX;
    private bool _recentsSnapping;
    private double _lastStepTime;

    // Recents touch/pointer gesture state (guarded by _uiLock)
    private bool _recentsPointerDown;
    private int _recentsDownX;
    private int _recentsDownY;
    private int _recentsLastX;
    private int _recentsLastY;
    private float _recentsDownScrollX;
    private int _recentsDragAxis; // 0 undecided, 1 horizontal carousel, 2 vertical swipe-dismiss
    private int _recentsDragCardIndex = -1;
    private float _recentsDragCardOffsetY;
    private bool _recentsDownOnCloseBtn;
    private ulong _recentsDownCloseSurfaceId;

    public DesktopCompositorHost(
        ILoggerFactory loggerFactory,
        string angleDir,
        nint metalLayer,
        int logicalWidth,
        int logicalHeight,
        int pixelWidth,
        int pixelHeight,
        uint scale120,
        bool enableGles,
        bool touchMode,
        DesktopHostCallbacks callbacks)
    {
        _logger = loggerFactory.CreateLogger<DesktopCompositorHost>();
        _angleDir = angleDir;
        _metalLayer = metalLayer;
        _logicalWidth = Math.Clamp(logicalWidth, 1, FramebufferDevice.DimensionLimit);
        _logicalHeight = Math.Clamp(logicalHeight, 1, FramebufferDevice.DimensionLimit);
        _pixelWidth = Math.Clamp(pixelWidth, 1, FramebufferDevice.DimensionLimit);
        _pixelHeight = Math.Clamp(pixelHeight, 1, FramebufferDevice.DimensionLimit);
        _scale120 = Math.Clamp(scale120 == 0 ? 120u : scale120, 60u, 480u);
        _clientDpi = Math.Clamp((int)Math.Round(_scale120 * 96.0 / 120.0), 72, 480);
        _enableGles = enableGles;
        _mobileMode = touchMode;
        _callbacks = callbacks;
        _theme = CreateTheme(touchMode);
        _cursorX = _logicalWidth / 2;
        _cursorY = Math.Max(1, (_logicalHeight - ComputeNavBarHeight(_logicalWidth, _logicalHeight, _mobileMode)) / 2);

        Scanout = new VirtualDrmScanout(_pixelWidth, _pixelHeight);
        IngressBridge = new DesktopIngressBridge();
        Presenter = new DesktopScenePresenter(
            _logicalWidth,
            _logicalHeight,
            _theme,
            EnqueueCommand,
            CanImportGpu,
            reservedBottomInset: ComputeNavBarHeight(_logicalWidth, _logicalHeight, _mobileMode));
    }

    public VirtualDrmScanout Scanout { get; }
    public DesktopIngressBridge IngressBridge { get; }
    public DesktopScenePresenter Presenter { get; }
    public KmsEglOutput? GuestTextures => _renderer?.GuestTextures;
    public int LogicalWidth => Volatile.Read(ref _logicalWidth);
    public int LogicalHeight => Volatile.Read(ref _logicalHeight);
    public uint Scale120 => Volatile.Read(ref _scale120);
    public int ClientDpi => Volatile.Read(ref _clientDpi);
    public int NavBarHeight => ComputeNavBarHeight(LogicalWidth, LogicalHeight, _mobileMode);

    public static int ComputeNavBarHeight(int logicalWidth, int logicalHeight, bool mobileMode)
    {
        if (!mobileMode) return 0;
        return Math.Clamp((int)Math.Round(logicalHeight * 0.064), 48, 120);
    }

    private static WaylandUiTheme CreateTheme(bool touchMode)
    {
        var baseTheme = WaylandUiTheme.CatppuccinLatte;
        if (!touchMode)
            return baseTheme;

        // Touch-friendly taller titlebar and larger close/max/min hit targets on mobile.
        return baseTheme with
        {
            Typography = baseTheme.Typography with { TitleFontSize = 14 },
            Spacing = baseTheme.Spacing with
            {
                TitlebarHeight = 36,
                ButtonSize = 24,
                ButtonHeight = 24,
                ButtonGap = 10,
                ButtonInset = 10,
                ResizeGripThickness = 12
            }
        };
    }

    public void StartRenderThread()
    {
        using var ready = new ManualResetEventSlim(false);
        _renderThread = new Thread(() => RenderLoop(ready))
        {
            Name = "PodishAngleCompositor",
            IsBackground = true
        };
        _renderThread.Start();
        ready.Wait();
        if (_initException != null)
            throw new InvalidOperationException(_initException.Message, _initException);
    }

    public void SetVisible(bool visible)
    {
        Volatile.Write(ref _visible, visible ? 1 : 0);
        if (visible) Wake();
    }

    public void SetTrackpadMode(bool enabled)
    {
        lock (_uiLock)
        {
            if (_trackpadMode == enabled) return;
            _trackpadMode = enabled;
            if (enabled)
            {
                _cursorX = Math.Clamp(_cursorX, 0, Math.Max(0, LogicalWidth - 1));
                _cursorY = Math.Clamp(_cursorY, 0, Math.Max(0, LogicalHeight - NavBarHeight - 1));
            }
            _uiDirty = true;
        }
        Wake();
    }

    public void UpdateToplevelSnapshots(WaylandServer.ToplevelSnapshot[] snapshots)
    {
        lock (_uiLock)
        {
            // Keep existing MRU ordering for already-known cards, prepend newly opened apps,
            // and move the currently active app to index 0 when Recents is closed.
            var incomingById = new Dictionary<ulong, WaylandServer.ToplevelSnapshot>(snapshots.Length);
            foreach (var s in snapshots)
                incomingById[s.SceneSurfaceId] = s;

            var existingIds = new HashSet<ulong>();
            for (int i = _recentsCards.Count - 1; i >= 0; i--)
            {
                var card = _recentsCards[i];
                if (incomingById.TryGetValue(card.SceneSurfaceId, out var updated))
                {
                    card.AppId = updated.AppId;
                    card.Title = string.IsNullOrWhiteSpace(updated.Title)
                        ? (string.IsNullOrWhiteSpace(updated.AppId) ? "Application" : updated.AppId)
                        : updated.Title;
                    card.IsActive = updated.Active;
                    card.IsMinimized = updated.Minimized;
                    existingIds.Add(card.SceneSurfaceId);
                }
                else
                {
                    _recentsCards.RemoveAt(i);
                }
            }

            foreach (var s in snapshots)
            {
                if (!existingIds.Contains(s.SceneSurfaceId))
                {
                    _recentsCards.Insert(0, new RecentsCardItem
                    {
                        SceneSurfaceId = s.SceneSurfaceId,
                        AppId = s.AppId,
                        Title = string.IsNullOrWhiteSpace(s.Title)
                            ? (string.IsNullOrWhiteSpace(s.AppId) ? "Application" : s.AppId)
                            : s.Title,
                        IsActive = s.Active,
                        IsMinimized = s.Minimized
                    });
                }
            }

            if (!_recentsTargetOpen && _recentsProgress < 0.05f)
            {
                int activeIdx = _recentsCards.FindIndex(c => c.IsActive);
                if (activeIdx > 0)
                {
                    var active = _recentsCards[activeIdx];
                    _recentsCards.RemoveAt(activeIdx);
                    _recentsCards.Insert(0, active);
                }
            }

            ClampRecentsScrollLocked();
            _uiDirty = true;
        }
        Wake();
    }

    /// <summary>
    /// Action: 0 = Back, 1 = Home, 2 = Recents
    /// </summary>
    public void TriggerNavAction(int action)
    {
        switch (action)
        {
            case 0: // Back
                bool closedRecents = false;
                lock (_uiLock)
                {
                    if (_recentsTargetOpen || _recentsProgress > 0.05f)
                    {
                        SetRecentsOpenLocked(false);
                        closedRecents = true;
                    }
                }
                if (closedRecents)
                {
                    Wake();
                    return;
                }
                IngressBridge.Post(new DesktopIngressEvent(
                    DesktopIngressKind.BackAction,
                    Timestamp: unchecked((uint)Environment.TickCount64)));
                break;

            case 1: // Home
                lock (_uiLock)
                {
                    if (_recentsTargetOpen || _recentsProgress > 0.01f)
                    {
                        _recentsSourceSurfaceId = 0;
                        SetRecentsOpenLocked(false);
                    }
                }
                Wake();
                IngressBridge.Post(new DesktopIngressEvent(DesktopIngressKind.ShowHome));
                break;

            case 2: // Recents toggle
                lock (_uiLock)
                {
                    bool opening = !_recentsTargetOpen;
                    if (opening)
                    {
                        // Remember which visible app card (if any) we are animating out from.
                        var activeCard = _recentsCards.FirstOrDefault(c => c.IsActive && !c.IsMinimized);
                        _recentsSourceSurfaceId = activeCard?.SceneSurfaceId ?? 0;
                        int focusIdx = activeCard != null ? _recentsCards.IndexOf(activeCard) : 0;
                        float stride = ComputeCardSlotStride(LogicalWidth);
                        _recentsScrollX = Math.Max(0, focusIdx) * stride;
                        _recentsTargetScrollX = _recentsScrollX;
                        _recentsSnapping = false;
                    }
                    SetRecentsOpenLocked(opening);
                }
                Wake();
                break;
        }
    }

    public bool TryInterceptInput(DisplayInputEvent ev)
    {
        if (ev.Kind is DisplayInputEventKind.PointerMotion or DisplayInputEventKind.PointerButton)
        {
            bool wakeCursor = false;
            lock (_uiLock)
            {
                _cursorX = Math.Clamp(ev.X, 0, Math.Max(0, LogicalWidth - 1));
                _cursorY = Math.Clamp(ev.Y, 0, Math.Max(0, LogicalHeight - 1));
                if (_trackpadMode)
                {
                    _uiDirty = true;
                    wakeCursor = true;
                }
            }
            if (wakeCursor) Wake();
        }

        // Handle hardware/keyboard Escape as Back when Recents is open.
        if (ev.Kind == DisplayInputEventKind.KeyboardKey && ev.Key == 1 && ev.Pressed)
        {
            bool handled = false;
            lock (_uiLock)
            {
                if (_recentsTargetOpen || _recentsProgress > 0.05f)
                {
                    SetRecentsOpenLocked(false);
                    handled = true;
                }
            }
            if (handled)
            {
                Wake();
                return true;
            }
        }

        int w = LogicalWidth;
        int h = LogicalHeight;
        int navH = NavBarHeight;

        // 1. Mobile Android 3-button bottom navigation bar interception
        if (_mobileMode && navH > 0 && ev.Kind is DisplayInputEventKind.PointerButton or DisplayInputEventKind.PointerMotion)
        {
            int navTop = h - navH;
            bool inNavBar = ev.Y >= navTop;
            int hitBtn = inNavBar ? Math.Clamp(ev.X * 3 / Math.Max(1, w), 0, 2) : -1;

            if (ev.Kind == DisplayInputEventKind.PointerButton && ev.Button == DisplayPointerButton.Left)
            {
                if (ev.Pressed && inNavBar)
                {
                    lock (_uiLock)
                    {
                        _navPressedButton = hitBtn;
                        _uiDirty = true;
                    }
                    Wake();
                    return true;
                }

                if (!ev.Pressed)
                {
                    int triggered = -1;
                    lock (_uiLock)
                    {
                        if (_navPressedButton >= 0)
                        {
                            if (inNavBar && hitBtn == _navPressedButton)
                                triggered = _navPressedButton;
                            _navPressedButton = -1;
                            _uiDirty = true;
                        }
                    }
                    if (triggered >= 0)
                    {
                        Wake();
                        TriggerNavAction(triggered);
                        return true;
                    }
                    if (inNavBar)
                    {
                        Wake();
                        return true;
                    }
                }
            }
            else if (ev.Kind == DisplayInputEventKind.PointerMotion)
            {
                bool capturedNav = false;
                lock (_uiLock)
                {
                    if (_navPressedButton >= 0)
                    {
                        capturedNav = true;
                        if (!inNavBar)
                        {
                            _navPressedButton = -1;
                            _uiDirty = true;
                        }
                    }
                }
                if (capturedNav)
                {
                    Wake();
                    return true;
                }
            }
        }

        // 2. Android Recents / Multitasking gesture & click interception
        bool recentsActive;
        lock (_uiLock)
            recentsActive = _recentsTargetOpen || _recentsProgress > 0.05f;

        if (!recentsActive)
            return false;

        if (ev.Kind == DisplayInputEventKind.PointerWheel)
        {
            lock (_uiLock)
            {
                if (_recentsCards.Count > 1)
                {
                    float delta = Math.Abs(ev.DeltaX) >= Math.Abs(ev.DeltaY) ? ev.DeltaX : ev.DeltaY;
                    float stride = ComputeCardSlotStride(w);
                    float maxScroll = (_recentsCards.Count - 1) * stride;
                    _recentsScrollX = Math.Clamp(_recentsScrollX + delta * 3.0f, 0, maxScroll);
                    SnapToNearestCardLocked(w);
                    _uiDirty = true;
                }
            }
            Wake();
            return true;
        }

        if (ev.Kind == DisplayInputEventKind.PointerButton && ev.Button == DisplayPointerButton.Left)
        {
            if (ev.Pressed)
            {
                HandleRecentsPointerDown(ev.X, ev.Y, w, h, navH);
            }
            else
            {
                HandleRecentsPointerUp(ev.X, ev.Y, w, h, navH);
            }
            return true;
        }

        if (ev.Kind == DisplayInputEventKind.PointerMotion)
        {
            HandleRecentsPointerMove(ev.X, ev.Y, w, h, navH);
            return true;
        }

        // Block stray input from leaking to background apps while Recents overview is visible.
        return ev.Kind is DisplayInputEventKind.PointerButton or DisplayInputEventKind.PointerLeave;
    }

    private void HandleRecentsPointerDown(int x, int y, int w, int h, int navH)
    {
        lock (_uiLock)
        {
            _recentsPointerDown = true;
            _recentsDownX = x;
            _recentsDownY = y;
            _recentsLastX = x;
            _recentsLastY = y;
            _recentsDownScrollX = _recentsScrollX;
            _recentsDragAxis = 0;
            _recentsDragCardIndex = -1;
            _recentsDragCardOffsetY = 0;
            _recentsDownOnCloseBtn = false;
            _recentsDownCloseSurfaceId = 0;
            _recentsSnapping = false;

            float stride = ComputeCardSlotStride(w);
            for (int i = 0; i < _recentsCards.Count; i++)
            {
                var card = _recentsCards[i];
                var layout = ComputeCardLayout(card, i, w, h, navH, stride, _recentsScrollX, 1f, 0f);
                // Expand close button hit area slightly for comfortable touch taps
                var closeHit = new DisplayRect(layout.CloseBtnRect.X - 10, layout.CloseBtnRect.Y - 10,
                    layout.CloseBtnRect.Width + 20, layout.CloseBtnRect.Height + 20);
                if (ContainsRect(closeHit, x, y))
                {
                    _recentsDownOnCloseBtn = true;
                    _recentsDownCloseSurfaceId = card.SceneSurfaceId;
                    _recentsDragCardIndex = i;
                    break;
                }
                if (ContainsRect(layout.PreviewRect, x, y) || ContainsRect(layout.HeaderRect, x, y))
                {
                    _recentsDragCardIndex = i;
                    break;
                }
            }
            _uiDirty = true;
        }
        Wake();
    }

    private void HandleRecentsPointerMove(int x, int y, int w, int h, int navH)
    {
        bool changed = false;
        lock (_uiLock)
        {
            if (!_recentsPointerDown) return;
            int totalDx = x - _recentsDownX;
            int totalDy = y - _recentsDownY;
            _recentsLastX = x;
            _recentsLastY = y;

            if (_recentsDragAxis == 0 && (Math.Abs(totalDx) > 14 || Math.Abs(totalDy) > 14))
            {
                if (_recentsDownOnCloseBtn)
                {
                    // Cancel close-button tap if user started dragging
                    _recentsDownOnCloseBtn = false;
                }
                if (Math.Abs(totalDx) >= Math.Abs(totalDy))
                    _recentsDragAxis = 1; // Horizontal carousel swipe
                else if (_recentsDragCardIndex >= 0)
                    _recentsDragAxis = 2; // Vertical swipe-to-dismiss card
            }

            if (_recentsDragAxis == 1 && _recentsCards.Count > 0)
            {
                float stride = ComputeCardSlotStride(w);
                float maxScroll = Math.Max(0, (_recentsCards.Count - 1) * stride);
                float raw = _recentsDownScrollX - totalDx;
                // Gentle rubber-band overscroll at ends
                if (raw < 0) raw *= 0.3f;
                else if (raw > maxScroll) raw = maxScroll + (raw - maxScroll) * 0.3f;
                _recentsScrollX = raw;
                changed = true;
            }
            else if (_recentsDragAxis == 2 && _recentsDragCardIndex >= 0)
            {
                // Allow upward swipe freely, gently dampen downward pull
                _recentsDragCardOffsetY = totalDy < 0 ? totalDy : totalDy * 0.22f;
                changed = true;
            }

            if (changed) _uiDirty = true;
        }
        if (changed) Wake();
    }

    private void HandleRecentsPointerUp(int x, int y, int w, int h, int navH)
    {
        ulong activateSurfaceId = 0;
        ulong closeSurfaceId = 0;

        lock (_uiLock)
        {
            if (!_recentsPointerDown) return;
            _recentsPointerDown = false;

            int totalDx = x - _recentsDownX;
            int totalDy = y - _recentsDownY;
            int usableH = Math.Max(1, h - navH);

            if (_recentsDragAxis == 0)
            {
                // Tap gesture!
                if (_recentsDownOnCloseBtn && _recentsDownCloseSurfaceId != 0)
                {
                    closeSurfaceId = StartDismissCardLocked(_recentsDownCloseSurfaceId, 0f);
                }
                else if (_recentsDragCardIndex >= 0 && _recentsDragCardIndex < _recentsCards.Count)
                {
                    var chosen = _recentsCards[_recentsDragCardIndex];
                    activateSurfaceId = chosen.SceneSurfaceId;
                    _recentsSourceSurfaceId = chosen.SceneSurfaceId;
                    // Move chosen card to front of MRU order
                    _recentsCards.RemoveAt(_recentsDragCardIndex);
                    _recentsCards.Insert(0, chosen);
                    _recentsScrollX = 0;
                    _recentsTargetScrollX = 0;
                    SetRecentsOpenLocked(false);
                }
                else
                {
                    // Tapped outside any card (or empty state): smoothly close Recents
                    SetRecentsOpenLocked(false);
                }
            }
            else if (_recentsDragAxis == 1)
            {
                // Finish horizontal swipe with velocity/threshold snap
                float stride = ComputeCardSlotStride(w);
                if (_recentsCards.Count > 1)
                {
                    int currentIdx = (int)Math.Round(_recentsScrollX / Math.Max(1f, stride));
                    if (totalDx < -w * 0.12f)
                        currentIdx = Math.Min(_recentsCards.Count - 1, (int)Math.Round(_recentsDownScrollX / Math.Max(1f, stride)) + 1);
                    else if (totalDx > w * 0.12f)
                        currentIdx = Math.Max(0, (int)Math.Round(_recentsDownScrollX / Math.Max(1f, stride)) - 1);
                    currentIdx = Math.Clamp(currentIdx, 0, _recentsCards.Count - 1);
                    _recentsTargetScrollX = currentIdx * stride;
                    _recentsSnapping = true;
                }
                else
                {
                    _recentsTargetScrollX = 0;
                    _recentsSnapping = true;
                }
            }
            else if (_recentsDragAxis == 2 && _recentsDragCardIndex >= 0 && _recentsDragCardIndex < _recentsCards.Count)
            {
                if (_recentsDragCardOffsetY < -usableH * 0.16f || totalDy < -usableH * 0.16f)
                {
                    var card = _recentsCards[_recentsDragCardIndex];
                    closeSurfaceId = StartDismissCardLocked(card.SceneSurfaceId, _recentsDragCardOffsetY);
                }
            }

            _recentsDragAxis = 0;
            _recentsDragCardIndex = -1;
            _recentsDragCardOffsetY = 0;
            _recentsDownOnCloseBtn = false;
            _recentsDownCloseSurfaceId = 0;
            _uiDirty = true;
        }

        Wake();

        if (activateSurfaceId != 0)
            IngressBridge.Post(new DesktopIngressEvent(DesktopIngressKind.ActivateToplevel, SurfaceId: activateSurfaceId));
        if (closeSurfaceId != 0)
            IngressBridge.Post(new DesktopIngressEvent(DesktopIngressKind.CloseToplevel, SurfaceId: closeSurfaceId));
    }

    private ulong StartDismissCardLocked(ulong surfaceId, float initialOffsetY)
    {
        int idx = _recentsCards.FindIndex(c => c.SceneSurfaceId == surfaceId);
        if (idx < 0) return 0;

        var card = _recentsCards[idx];
        _recentsCards.RemoveAt(idx);
        _dismissingCards.Add(new DismissingCardAnim
        {
            Card = card,
            CardSlotIndex = idx,
            InitialOffsetY = initialOffsetY,
            StartTime = NowSeconds()
        });
        ClampRecentsScrollLocked();
        SnapToNearestCardLocked(LogicalWidth);
        return surfaceId;
    }

    private void SetRecentsOpenLocked(bool open)
    {
        if (_recentsTargetOpen == open && Math.Abs(_recentsProgress - (open ? 1f : 0f)) < 0.001f)
            return;
        _recentsTargetOpen = open;
        _recentsAnimStartProgress = _recentsProgress;
        _recentsAnimStartTime = NowSeconds();
        _uiDirty = true;
    }

    private void ClampRecentsScrollLocked()
    {
        float stride = ComputeCardSlotStride(LogicalWidth);
        float maxScroll = Math.Max(0, (_recentsCards.Count - 1) * stride);
        _recentsScrollX = Math.Clamp(_recentsScrollX, 0, maxScroll);
        _recentsTargetScrollX = Math.Clamp(_recentsTargetScrollX, 0, maxScroll);
    }

    private void SnapToNearestCardLocked(int w)
    {
        if (_recentsCards.Count <= 1)
        {
            _recentsTargetScrollX = 0;
            _recentsSnapping = Math.Abs(_recentsScrollX) > 0.5f;
            return;
        }
        float stride = ComputeCardSlotStride(w);
        int idx = Math.Clamp((int)Math.Round(_recentsScrollX / Math.Max(1f, stride)), 0, _recentsCards.Count - 1);
        _recentsTargetScrollX = idx * stride;
        _recentsSnapping = Math.Abs(_recentsScrollX - _recentsTargetScrollX) > 0.5f;
    }

    public void RequestResize(int logicalWidth, int logicalHeight, int pixelWidth, int pixelHeight, uint scale120, int clientDpi = 0)
    {
        logicalWidth = Math.Clamp(logicalWidth, 1, FramebufferDevice.DimensionLimit);
        logicalHeight = Math.Clamp(logicalHeight, 1, FramebufferDevice.DimensionLimit);
        pixelWidth = Math.Clamp(pixelWidth, 1, FramebufferDevice.DimensionLimit);
        pixelHeight = Math.Clamp(pixelHeight, 1, FramebufferDevice.DimensionLimit);
        scale120 = Math.Clamp(scale120 == 0 ? 120u : scale120, 60u, 480u);
        int resolvedDpi = clientDpi > 0
            ? Math.Clamp(clientDpi, 72, 480)
            : Math.Clamp((int)Math.Round(scale120 * 96.0 / 120.0), 72, 480);
        int navH = ComputeNavBarHeight(logicalWidth, logicalHeight, _mobileMode);

        EnqueueCommand(new DesktopDisplayCommand(
            DesktopDisplayCommandKind.ResizeRenderer,
            LogicalWidth: logicalWidth,
            LogicalHeight: logicalHeight,
            PixelWidth: pixelWidth,
            PixelHeight: pixelHeight,
            Scale120: scale120,
            ClientDpi: resolvedDpi));
        IngressBridge.Post(new DesktopIngressEvent(
            DesktopIngressKind.ResizeOutput,
            Width: logicalWidth,
            Height: logicalHeight,
            Scale120: scale120,
            ClientDpi: resolvedDpi,
            ReservedBottomInset: navH));
    }

    public void Stop()
    {
        _stopRequested = true;
        Wake();
        _renderThread?.Join(TimeSpan.FromSeconds(5));
        _renderThread = null;
    }

    public void Dispose()
    {
        lock (_commandLock)
        {
            if (_disposed) return;
            _disposed = true;
            _stopRequested = true;
        }
        Scanout.FrameReady -= Wake;
        _wake.Set();
        _renderThread?.Join(TimeSpan.FromSeconds(5));
        _renderThread = null;

        while (_commands.TryDequeue(out var cmd))
            cmd.ReleaseBuffer();

        Presenter.Dispose();
        _wake.Dispose();
    }

    private bool CanImportGpu(DrmScanoutBuffer buffer) => _renderer?.CanImport(buffer) == true;

    private void EnqueueCommand(DesktopDisplayCommand command)
    {
        lock (_commandLock)
        {
            if (_disposed) return;
            command.RetainBuffer();
            _commands.Enqueue(command);
            _wake.Set();
        }
    }

    private void Wake()
    {
        lock (_commandLock)
        {
            if (!_disposed)
                _wake.Set();
        }
    }

    private static double NowSeconds() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private bool StepAnimations(out bool animating)
    {
        double now = NowSeconds();
        double dt = _lastStepTime <= 0 ? 0.016 : Math.Clamp(now - _lastStepTime, 0.001, 0.05);
        _lastStepTime = now;

        bool dirty = false;
        animating = false;

        lock (_uiLock)
        {
            if (_uiDirty)
            {
                dirty = true;
                _uiDirty = false;
            }

            float target = _recentsTargetOpen ? 1f : 0f;
            if (Math.Abs(_recentsProgress - target) > 0.0005f)
            {
                double elapsed = Math.Max(0, now - _recentsAnimStartTime);
                float t = (float)Math.Clamp(elapsed / RecentsAnimDurationSec, 0.0, 1.0);
                // Smooth cubic ease-out
                float eased = 1f - MathF.Pow(1f - t, 3f);
                _recentsProgress = _recentsAnimStartProgress + (target - _recentsAnimStartProgress) * eased;
                if (t >= 1f)
                    _recentsProgress = target;
                else
                    animating = true;
                dirty = true;
            }

            if (_recentsSnapping && !_recentsPointerDown)
            {
                float diff = _recentsTargetScrollX - _recentsScrollX;
                if (Math.Abs(diff) < 0.75f)
                {
                    _recentsScrollX = _recentsTargetScrollX;
                    _recentsSnapping = false;
                }
                else
                {
                    float factor = 1f - MathF.Exp((float)(-14.0 * dt));
                    _recentsScrollX += diff * factor;
                    animating = true;
                }
                dirty = true;
            }

            for (int i = _dismissingCards.Count - 1; i >= 0; i--)
            {
                var d = _dismissingCards[i];
                float t = (float)Math.Clamp((now - d.StartTime) / CardDismissAnimDurationSec, 0.0, 1.0);
                d.Progress = t;
                dirty = true;
                if (t >= 1f)
                    _dismissingCards.RemoveAt(i);
                else
                    animating = true;
            }
        }

        return dirty;
    }

    private void RenderLoop(ManualResetEventSlim ready)
    {
        try
        {
            var kmsWindow = new KmsEglWindow(_metalLayer, CanPresent: () => Volatile.Read(ref _visible) != 0);
            _renderer = DesktopAngleRenderer.Create(_angleDir, kmsWindow, Scanout,
                new DisplaySize(_logicalWidth, _logicalHeight), _enableGles, _logger);
            Scanout.FrameReady += Wake;
            ready.Set();
        }
        catch (Exception ex)
        {
            _initException = ex;
            ready.Set();
            return;
        }

        try
        {
            bool scanoutDirty = true;
            while (!_stopRequested)
            {
                bool dirty;
                List<ulong> consumed;
                using (_renderer.EnterDisplayLock())
                {
                    dirty = DrainCommands(out consumed);
                    if (dirty)
                        _renderer.Flush();
                }
                foreach (var lease in consumed)
                    IngressBridge.Post(new DesktopIngressEvent(DesktopIngressKind.BufferConsumed, LeaseToken: lease));

                bool animDirty = StepAnimations(out bool animating);

                if (Scanout.TryTakeGpuFrame(out var buffer, out uint timestamp))
                {
                    bool presented = false;
                    if (buffer != null)
                    {
                        using (_renderer.EnterDisplayLock())
                            presented = buffer.Present();
                    }
                    if (presented)
                    {
                        IngressBridge.Post(new DesktopIngressEvent(DesktopIngressKind.VSyncTick,
                            Timestamp: timestamp, FrameSequence: _scanoutFrameSequence));
                    }
                }

                scanoutDirty |= dirty || animDirty;
                if (scanoutDirty)
                {
                    bool queued;
                    using (_renderer.EnterDisplayLock())
                        queued = Scanout.TryQueueGpuFrame(CaptureScanoutFrame);
                    if (queued)
                    {
                        _scanoutFrameSequence = _sceneFrameSequence;
                        scanoutDirty = false;
                    }
                }

                int waitMs = _renderer.HasPendingUploads ? 1 : (animating ? 8 : 12);
                _wake.WaitOne(waitMs);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Apple ANGLE compositor loop failed");
            _callbacks.NotifyStatus(-1, ex.Message);
        }
        finally
        {
            Scanout.FrameReady -= Wake;
            CleanupRendererResources();
        }
    }

    private void CleanupRendererResources()
    {
        using (_renderer?.EnterDisplayLock() ?? default)
        {
            foreach (var s in _surfaces.Values)
            {
                s.Texture?.Dispose();
                s.TitleTexture?.Dispose();
                s.AppIconTexture?.Dispose();
            }
            _surfaces.Clear();
            _zOrder.Clear();
            foreach (var tex in _buttonIcons.Values)
                tex.Dispose();
            _buttonIcons.Clear();
            foreach (var tex in _uiTextCache.Values)
                tex.Dispose();
            _uiTextCache.Clear();
            _renderer?.Dispose();
            _renderer = null;
        }
    }

    private bool DrainCommands(out List<ulong> consumed)
    {
        consumed = [];
        var batch = new List<DesktopDisplayCommand>();
        while (_commands.TryDequeue(out var cmd))
            batch.Add(cmd);

        bool dirty = false;
        bool textInputChanged = false;
        try
        {
            foreach (var cmd in batch)
            {
                switch (cmd.Kind)
                {
                    case DesktopDisplayCommandKind.PresentSurface:
                        dirty = true;
                        UpsertShmSurface(cmd.SceneSurfaceId, cmd.Frame, cmd.Bounds);
                        consumed.Add(cmd.Frame.LeaseToken);
                        break;
                    case DesktopDisplayCommandKind.PresentGpuSurface:
                        if (UpsertGpuSurface(cmd.SceneSurfaceId, cmd.GpuFrame, cmd.Bounds))
                            dirty = true;
                        else
                            consumed.Add(cmd.GpuFrame.LeaseToken);
                        break;
                    case DesktopDisplayCommandKind.UpdateSurfaceBounds:
                        dirty = true;
                        EnsureSurfaceState(cmd.SceneSurfaceId).Bounds = cmd.Bounds;
                        break;
                    case DesktopDisplayCommandKind.UpdateSurfaceMapping:
                        dirty = true;
                        var ms = EnsureSurfaceState(cmd.SceneSurfaceId);
                        ms.Bounds = cmd.Bounds;
                        ms.Mapping = cmd.Mapping;
                        break;
                    case DesktopDisplayCommandKind.UpdateDecoration:
                        dirty = true;
                        UpdateDecoration(cmd.SceneSurfaceId, cmd.Decoration);
                        break;
                    case DesktopDisplayCommandKind.UpdateDecorationPointer:
                        dirty = true;
                        _hoveredDecoration = cmd.HoveredDecoration;
                        _pressedDecoration = cmd.PressedDecoration;
                        break;
                    case DesktopDisplayCommandKind.SetSurfaceVisibility:
                        dirty = true;
                        EnsureSurfaceState(cmd.SceneSurfaceId).Hidden = cmd.Hidden;
                        break;
                    case DesktopDisplayCommandKind.SetSurfaceLayer:
                        dirty = true;
                        SetSurfaceLayer(cmd.SceneSurfaceId, cmd.Layer);
                        break;
                    case DesktopDisplayCommandKind.RaiseSurface:
                        dirty = true;
                        RaiseSurface(cmd.SceneSurfaceId);
                        break;
                    case DesktopDisplayCommandKind.RemoveSurface:
                        dirty = true;
                        if (cmd.SceneSurfaceId == _textInputSurfaceId)
                        {
                            _textInputSurfaceId = 0;
                            _textInputRect = null;
                            textInputChanged = true;
                        }
                        RemoveSurface(cmd.SceneSurfaceId);
                        break;
                    case DesktopDisplayCommandKind.SetSystemCursor:
                        _callbacks.NotifyCursor(cmd.SystemCursor ?? WaylandSystemCursorShape.Default);
                        break;
                    case DesktopDisplayCommandKind.ClearCursor:
                        _callbacks.NotifyCursor(WaylandSystemCursorShape.Default);
                        break;
                    case DesktopDisplayCommandKind.UpdateTextInput:
                        _textInputSurfaceId = cmd.SceneSurfaceId;
                        _textInputRect = cmd.TextInputRect;
                        textInputChanged = true;
                        break;
                    case DesktopDisplayCommandKind.ScheduleFrame:
                        _sceneFrameSequence = cmd.FrameSequence;
                        dirty = true;
                        break;
                    case DesktopDisplayCommandKind.ResizeRenderer:
                        dirty = true;
                        Volatile.Write(ref _logicalWidth, cmd.LogicalWidth);
                        Volatile.Write(ref _logicalHeight, cmd.LogicalHeight);
                        Volatile.Write(ref _pixelWidth, cmd.PixelWidth);
                        Volatile.Write(ref _pixelHeight, cmd.PixelHeight);
                        Volatile.Write(ref _scale120, cmd.Scale120);
                        if (cmd.ClientDpi > 0)
                            Volatile.Write(ref _clientDpi, cmd.ClientDpi);
                        _renderer!.Resize(new DisplaySize(cmd.LogicalWidth, cmd.LogicalHeight),
                            new DisplaySize(cmd.PixelWidth, cmd.PixelHeight));
                        lock (_uiLock)
                        {
                            _cursorX = Math.Clamp(_cursorX, 0, Math.Max(0, cmd.LogicalWidth - 1));
                            _cursorY = Math.Clamp(_cursorY, 0, Math.Max(0, cmd.LogicalHeight - 1));
                            ClampRecentsScrollLocked();
                        }
                        break;
                }
            }

            if (textInputChanged || (dirty && _textInputSurfaceId != 0))
                NotifyTextInputRect();

            _renderer!.TakeCompletedUploads(consumed);
            return dirty;
        }
        finally
        {
            foreach (var cmd in batch)
                cmd.ReleaseBuffer();
        }
    }

    private void CaptureScanoutFrame(DrmScanoutBuffer buffer)
    {
        float recentsProgress;
        float recentsScrollX;
        ulong recentsSourceId;
        int dragCardIdx;
        float dragOffsetY;
        int navPressed;
        bool trackpadMode;
        int cursorX;
        int cursorY;
        List<RecentsCardItem> cardsSnapshot;
        List<DismissingCardAnim> dismissingSnapshot;

        lock (_uiLock)
        {
            recentsProgress = _recentsProgress;
            recentsScrollX = _recentsScrollX;
            recentsSourceId = _recentsSourceSurfaceId;
            dragCardIdx = _recentsDragCardIndex;
            dragOffsetY = _recentsDragCardOffsetY;
            navPressed = _navPressedButton;
            trackpadMode = _trackpadMode;
            cursorX = _cursorX;
            cursorY = _cursorY;
            cardsSnapshot = [.. _recentsCards];
            dismissingSnapshot = [.. _dismissingCards];
        }

        _renderer!.BeginScene(buffer);

        // 1. Normal desktop surfaces (skip the source app surface while Recents is animating it directly as a card)
        bool recentsVisible = recentsProgress > 0.001f;
        bool hideAppsForRecents = recentsVisible && (cardsSnapshot.Count > 0 || dismissingSnapshot.Count > 0);

        foreach (var id in _zOrder)
        {
            if (!_surfaces.TryGetValue(id, out var surface) || surface.Hidden || surface.Texture == null)
                continue;
            if (hideAppsForRecents)
            {
                bool isRecentsCard = cardsSnapshot.Any(c => c.SceneSurfaceId == id) ||
                                     dismissingSnapshot.Any(d => d.Card.SceneSurfaceId == id);
                if (isRecentsCard)
                    continue;
            }
            DrawDecoration(id, surface);
            _renderer.Blit(surface.Texture, new DisplayRect(surface.Bounds.X, surface.Bounds.Y,
                surface.Bounds.Width, surface.Bounds.Height), surface.Mapping);
        }

        // 2. Android-style Recents / Multitasking overview
        if (recentsVisible)
        {
            RenderRecentsOverlay(recentsProgress, recentsScrollX, recentsSourceId,
                dragCardIdx, dragOffsetY, cardsSnapshot, dismissingSnapshot);
        }

        // 3. Mobile bottom Android 3-button navigation bar (Compositor-owned)
        if (_mobileMode)
        {
            RenderAndroidNavBar(navPressed);
        }

        // 4. Trackpad simulation mode arrow cursor
        if (trackpadMode)
        {
            int curSize = Math.Clamp((int)Math.Round(Math.Min(LogicalWidth, LogicalHeight) * 0.042), 20, 32);
            _renderer.DrawVectorIcon(
                new DisplayRect(cursorX - 2, cursorY - 2, curSize, curSize),
                iconKind: 4,
                new DisplayColor(255, 255, 255, 255));
        }

        _renderer.EndScene();
    }

    private void RenderAndroidNavBar(int pressedButton)
    {
        int w = LogicalWidth;
        int h = LogicalHeight;
        int navH = NavBarHeight;
        if (navH <= 0) return;

        int top = h - navH;
        // Translucent dark glass backdrop + crisp 1px top separator
        _renderer!.FillRect(new DisplayRect(0, top, w, navH), new DisplayColor(8, 12, 24, 214));
        _renderer.FillRect(new DisplayRect(0, top, w, 1), new DisplayColor(255, 255, 255, 36));

        // Position icons slightly above center so they sit comfortably above iPhone's home indicator.
        int iconBox = Math.Clamp((int)Math.Round(navH * 0.42), 20, 34);
        int centerY = top + (int)Math.Round(navH * 0.44);
        int pillW = Math.Max(iconBox * 2 + 12, w / 5);
        int pillH = Math.Min(navH - 8, iconBox + 14);

        for (int i = 0; i < 3; i++)
        {
            int centerX = (int)Math.Round(w * (i + 0.5) / 3.0);
            bool pressed = pressedButton == i;
            if (pressed)
            {
                _renderer.DrawRoundedRect(
                    new DisplayRect(centerX - pillW / 2, centerY - pillH / 2, pillW, pillH),
                    new DisplayColor(255, 255, 255, 48),
                    pillH * 0.5f);
            }

            var iconColor = pressed
                ? new DisplayColor(97, 206, 255, 255)
                : new DisplayColor(238, 244, 255, 232);
            _renderer.DrawVectorIcon(
                new DisplayRect(centerX - iconBox / 2, centerY - iconBox / 2, iconBox, iconBox),
                iconKind: i,
                iconColor);
        }
    }

    private void RenderRecentsOverlay(
        float progress,
        float scrollX,
        ulong sourceSurfaceId,
        int dragCardIdx,
        float dragOffsetY,
        List<RecentsCardItem> cards,
        List<DismissingCardAnim> dismissing)
    {
        int w = LogicalWidth;
        int h = LogicalHeight;
        int navH = NavBarHeight;
        int usableH = Math.Max(1, h - navH);

        // Smooth dimmed backdrop scrim over the Plasma Mobile wallpaper
        byte scrimAlpha = (byte)Math.Clamp((int)Math.Round(152 * progress), 0, 255);
        _renderer!.FillRect(new DisplayRect(0, 0, w, usableH), new DisplayColor(6, 10, 22, scrimAlpha));

        if (cards.Count == 0 && dismissing.Count == 0)
        {
            int pillW = Math.Clamp((int)Math.Round(w * 0.56), 190, 340);
            int pillH = Math.Clamp((int)Math.Round(usableH * 0.06), 44, 62);
            int px = (w - pillW) / 2;
            int py = (usableH - pillH) / 2;
            byte bgA = (byte)Math.Clamp((int)Math.Round(190 * progress), 0, 255);
            byte brA = (byte)Math.Clamp((int)Math.Round(55 * progress), 0, 255);
            _renderer.DrawRoundedRect(new DisplayRect(px - 1, py - 1, pillW + 2, pillH + 2),
                new DisplayColor(255, 255, 255, brA), pillH * 0.5f);
            _renderer.DrawRoundedRect(new DisplayRect(px, py, pillW, pillH),
                new DisplayColor(16, 24, 44, bgA), pillH * 0.5f);

            var labelTex = GetOrCreateUiTextTexture("No recent apps", pillW - 24, pillH - 10, WaylandTitleAlignment.Center);
            if (labelTex != null)
            {
                _renderer.BlitRounded(labelTex, new DisplayRect(px + 12, py + 5, pillW - 24, pillH - 10), 0f, progress);
            }
            return;
        }

        float stride = ComputeCardSlotStride(w);

        // Draw cards (draw non-focused cards first, focused/source card last so it stays crisp on top)
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < cards.Count; i++)
            {
                var card = cards[i];
                bool isPriority = card.SceneSurfaceId == sourceSurfaceId || (sourceSurfaceId == 0 && i == 0);
                if ((pass == 0 && isPriority) || (pass == 1 && !isPriority))
                    continue;

                float offsetY = (i == dragCardIdx) ? dragOffsetY : 0f;
                float cardAlpha = progress;
                if (i == dragCardIdx && offsetY < 0)
                    cardAlpha *= Math.Clamp(1f + offsetY / (usableH * 0.55f), 0.25f, 1f);

                DrawRecentsCard(card, i, w, h, navH, stride, scrollX, progress, offsetY, cardAlpha,
                    animateFromWindowBounds: card.SceneSurfaceId == sourceSurfaceId);
            }
        }

        // Draw any currently flying-away dismissed cards
        foreach (var d in dismissing)
        {
            float ease = 1f - MathF.Pow(1f - d.Progress, 2f);
            float offsetY = d.InitialOffsetY - ease * usableH * 0.65f;
            float alpha = progress * (1f - d.Progress);
            DrawRecentsCard(d.Card, d.CardSlotIndex, w, h, navH, stride, scrollX, 1f, offsetY, alpha,
                animateFromWindowBounds: false);
        }
    }

    private void DrawRecentsCard(
        RecentsCardItem card,
        int index,
        int w,
        int h,
        int navH,
        float stride,
        float scrollX,
        float progress,
        float offsetY,
        float alpha,
        bool animateFromWindowBounds)
    {
        if (alpha <= 0.01f) return;

        var layout = ComputeCardLayout(card, index, w, h, navH, stride, scrollX, progress, offsetY);
        if (layout.PreviewRect.X + layout.PreviewRect.Width < -24 || layout.PreviewRect.X > w + 24)
            return;

        DisplayRect previewRect = layout.PreviewRect;
        float cornerRadius = 18f * progress;
        float headerAlpha = Math.Clamp((progress - 0.25f) / 0.75f, 0f, 1f) * alpha;

        if (animateFromWindowBounds && progress < 0.999f)
        {
            DisplayRect startRect = new(0, 0, w, Math.Max(1, h - navH));
            if (_surfaces.TryGetValue(card.SceneSurfaceId, out var src) && src.Bounds.Width > 0 && src.Bounds.Height > 0)
                startRect = new DisplayRect(src.Bounds.X, src.Bounds.Y, src.Bounds.Width, src.Bounds.Height);
            previewRect = LerpRect(startRect, layout.PreviewRect, progress);
            alpha = Math.Max(alpha, 1f - progress * 0.0f);
        }

        // Subtle translucent border & shadow frame around the preview card
        byte borderAlpha = (byte)Math.Clamp((int)Math.Round(68 * progress * alpha), 0, 255);
        byte shadowAlpha = (byte)Math.Clamp((int)Math.Round(96 * progress * alpha), 0, 255);
        _renderer!.DrawRoundedRect(
            new DisplayRect(previewRect.X - 3, previewRect.Y + 4, previewRect.Width + 6, previewRect.Height + 6),
            new DisplayColor(0, 0, 0, shadowAlpha),
            cornerRadius + 3f);
        _renderer.DrawRoundedRect(
            new DisplayRect(previewRect.X - 2, previewRect.Y - 2, previewRect.Width + 4, previewRect.Height + 4),
            new DisplayColor(255, 255, 255, borderAlpha),
            cornerRadius + 2f);

        // Window live texture preview (or dark fallback card if texture hasn't committed yet)
        if (_surfaces.TryGetValue(card.SceneSurfaceId, out var surface) && surface.Texture != null)
        {
            _renderer.BlitRounded(surface.Texture, previewRect, cornerRadius, alpha, surface.Mapping);
        }
        else
        {
            byte cardBgA = (byte)Math.Clamp((int)Math.Round(220 * alpha), 0, 255);
            _renderer.DrawRoundedRect(previewRect, new DisplayColor(22, 30, 50, cardBgA), cornerRadius);
        }

        // Card header: App Icon + Title + Close (×) button
        if (headerAlpha > 0.02f)
        {
            var hdr = layout.HeaderRect;
            int iconSize = Math.Clamp(hdr.Height - 10, 18, 30);
            int textStartX = hdr.X + 4;

            if (_surfaces.TryGetValue(card.SceneSurfaceId, out var surf) && surf.AppIconTexture is { } iconTex)
            {
                var iconRect = new DisplayRect(hdr.X + 2, hdr.Y + (hdr.Height - iconSize) / 2, iconSize, iconSize);
                _renderer.BlitRounded(iconTex, iconRect, 6f, headerAlpha);
                textStartX = iconRect.X + iconRect.Width + 10;
            }

            int titleW = Math.Max(40, layout.CloseBtnRect.X - textStartX - 10);
            var titleTex = GetOrCreateUiTextTexture(card.Title, titleW, hdr.Height, WaylandTitleAlignment.Left);
            if (titleTex != null)
            {
                _renderer.BlitRounded(titleTex, new DisplayRect(textStartX, hdr.Y, titleW, hdr.Height), 0f, headerAlpha);
            }

            // Close (×) button pill
            var closeRect = layout.CloseBtnRect;
            byte closeBgA = (byte)Math.Clamp((int)Math.Round(155 * headerAlpha), 0, 255);
            byte closeFgA = (byte)Math.Clamp((int)Math.Round(245 * headerAlpha), 0, 255);
            _renderer.DrawRoundedRect(closeRect, new DisplayColor(24, 34, 58, closeBgA), closeRect.Height * 0.5f);
            int crossPad = Math.Max(5, closeRect.Width / 4);
            _renderer.DrawVectorIcon(
                new DisplayRect(closeRect.X + crossPad, closeRect.Y + crossPad,
                    closeRect.Width - crossPad * 2, closeRect.Height - crossPad * 2),
                iconKind: 3,
                new DisplayColor(255, 255, 255, closeFgA));
        }
    }

    /// <summary>
    /// Computes Android Recents card layout following the exact scaling specification:
    /// 1) Scale window width to align to the card column width (screen width minus base side padding).
    /// 2) If the aligned width causes height to exceed vertical bounds, scale down further so height
    ///    fits without clipping top/bottom and center horizontally with transparent padding on both sides.
    /// </summary>
    private CardLayout ComputeCardLayout(
        RecentsCardItem card,
        int index,
        int w,
        int h,
        int navH,
        float stride,
        float scrollX,
        float progress,
        float offsetY)
    {
        int usableH = Math.Max(1, h - navH);
        int topSafe = Math.Max(40, (int)Math.Round(h * 0.055));
        int bottomGap = Math.Max(18, (int)Math.Round(h * 0.025));
        int baseSidePadding = Math.Max(22, (int)Math.Round(w * 0.085));
        int headerH = Math.Clamp((int)Math.Round(h * 0.036), 34, 48);
        int headerGap = Math.Max(8, (int)Math.Round(h * 0.008));

        int alignedWidth = Math.Max(120, w - baseSidePadding * 2);
        int maxPreviewHeight = Math.Max(140, usableH - topSafe - bottomGap - headerH - headerGap - 12);

        int srcW = w;
        int srcH = usableH;
        if (_surfaces.TryGetValue(card.SceneSurfaceId, out var surf))
        {
            if (surf.Bounds.Width > 16 && surf.Bounds.Height > 16)
            {
                srcW = surf.Bounds.Width;
                srcH = surf.Bounds.Height;
            }
            else if (surf.Width > 16 && surf.Height > 16)
            {
                srcW = surf.Width;
                srcH = surf.Height;
            }
        }

        // Step 1: Align width to alignedWidth
        float drawW = alignedWidth;
        float drawH = srcH * (drawW / Math.Max(1f, srcW));

        // Step 2: If height overflows vertical screen bounds, scale down to fit height
        // and leave symmetrical transparent padding on both left and right sides.
        if (drawH > maxPreviewHeight)
        {
            float scale = maxPreviewHeight / Math.Max(1f, srcH);
            drawH = maxPreviewHeight;
            drawW = srcW * scale;
        }

        // Subtle carousel depth scale for side-peeking neighbor cards
        float deltaX = index * stride - scrollX;
        float normDist = Math.Clamp(Math.Abs(deltaX) / Math.Max(1f, stride), 0f, 1f);
        float focusScale = 1f - 0.06f * normDist;
        drawW *= focusScale;
        drawH *= focusScale;

        int pw = Math.Max(60, (int)Math.Round(drawW));
        int ph = Math.Max(60, (int)Math.Round(drawH));

        int totalBlockH = headerH + headerGap + ph;
        int blockTop = topSafe + Math.Max(0, (usableH - topSafe - bottomGap - totalBlockH) / 2) + (int)Math.Round(offsetY);
        int centerX = (int)Math.Round(w * 0.5f + deltaX * (0.35f + 0.65f * progress));

        int px = centerX - pw / 2;
        int py = blockTop + headerH + headerGap;
        var previewRect = new DisplayRect(px, py, pw, ph);

        int hdrW = Math.Min(alignedWidth, Math.Max(pw, (int)Math.Round(w * 0.62)));
        int hdrX = centerX - hdrW / 2;
        var headerRect = new DisplayRect(hdrX, blockTop, hdrW, headerH);

        int closeSize = Math.Clamp(headerH - 4, 28, 38);
        var closeBtnRect = new DisplayRect(
            headerRect.X + headerRect.Width - closeSize,
            headerRect.Y + (headerH - closeSize) / 2,
            closeSize,
            closeSize);

        return new CardLayout(previewRect, headerRect, closeBtnRect);
    }

    private static float ComputeCardSlotStride(int logicalWidth) => Math.Max(180f, logicalWidth * 0.88f);

    private static DisplayRect LerpRect(DisplayRect a, DisplayRect b, float t) => new(
        (int)Math.Round(a.X + (b.X - a.X) * t),
        (int)Math.Round(a.Y + (b.Y - a.Y) * t),
        Math.Max(1, (int)Math.Round(a.Width + (b.Width - a.Width) * t)),
        Math.Max(1, (int)Math.Round(a.Height + (b.Height - a.Height) * t)));

    private static bool ContainsRect(DisplayRect r, int x, int y) =>
        x >= r.X && y >= r.Y && x < r.X + r.Width && y < r.Y + r.Height;

    private IDisplayTexture? GetOrCreateUiTextTexture(string text, int logicalW, int logicalH, WaylandTitleAlignment align)
    {
        if (string.IsNullOrWhiteSpace(text) || logicalW <= 4 || logicalH <= 4) return null;
        var pxBounds = _renderer!.GetPixelBounds(new DisplayRect(0, 0, logicalW, logicalH));
        int pw = Math.Clamp(pxBounds.Width, 8, Math.Max(8, _pixelWidth));
        int ph = Math.Clamp(pxBounds.Height, 8, Math.Max(8, _pixelHeight));
        var key = (text, pw, ph, (int)align);
        if (_uiTextCache.TryGetValue(key, out var cached))
            return cached;

        if (_uiTextCache.Count > 32)
        {
            foreach (var tex in _uiTextCache.Values)
                tex.Dispose();
            _uiTextCache.Clear();
        }

        float fontPx = Math.Clamp(ph * 0.48f, 14f, 44f);
        var bytes = _callbacks.TryRasterizeText(text, pw, ph, fontPx, new WaylandColor(246, 249, 255, 255), align);
        if (bytes == null) return null;
        var created = _renderer.CreateTexture(new DisplayTextureDescriptor(pw, ph, DisplayPixelFormat.Argb8888));
        created.Update(bytes, pw * 4);
        _uiTextCache[key] = created;
        return created;
    }

    private void UpsertShmSurface(ulong id, WaylandShmFrame frame, WaylandSurfaceBounds bounds)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Stride <= 0) return;
        var format = frame.Format == WlShmFormat.Argb8888 ? DisplayPixelFormat.Argb8888 : DisplayPixelFormat.Xrgb8888;
        bool hadExisting = _surfaces.TryGetValue(id, out var prev) && prev.Texture != null &&
                           prev.Width == frame.Width && prev.Height == frame.Height && prev.Format == format;
        var state = EnsureSurfaceTexture(id, frame.Width, frame.Height, format);
        UploadShmFrame(state.Texture!, frame, allowPartialDamage: hadExisting);
        state.Mapping = frame.Mapping;
        state.Bounds = bounds;
        if (!_zOrder.Contains(id))
            PlaceAtTopOfLayer(id, state.Layer);
    }

    private bool UpsertGpuSurface(ulong id, WaylandGpuFrame frame, WaylandSurfaceBounds bounds)
    {
        var format = frame.Buffer.PrimeFormat == 0x34325241 ? DisplayPixelFormat.Argb8888 : DisplayPixelFormat.Xrgb8888;
        int w = (int)frame.Buffer.Width, h = (int)frame.Buffer.Height;
        var state = EnsureSurfaceState(id);
        bool reuse = state.Texture != null && state.Width == w && state.Height == h && state.Format == format;
        var tex = reuse ? state.Texture! : _renderer!.CreateTexture(new DisplayTextureDescriptor(w, h, format));
        if (!_renderer!.UploadGpuBuffer(tex, frame.Buffer, frame.YInverted, frame.LeaseToken))
        {
            _logger.LogWarning("UpsertGpuSurface rejected buffer id={Id} {W}x{H} bounds={Bounds}", id, w, h, bounds);
            if (!reuse) tex.Dispose();
            return false;
        }
        if (!reuse)
        {
            state.Texture?.Dispose();
            state.Texture = tex;
            state.Width = w;
            state.Height = h;
            state.Format = format;
        }
        state.Bounds = bounds;
        state.Mapping = frame.Mapping;
        if (!_zOrder.Contains(id))
            PlaceAtTopOfLayer(id, state.Layer);
        return true;
    }

    private static void UploadShmFrame(IDisplayTexture texture, WaylandShmFrame frame, bool allowPartialDamage)
    {
        var inode = frame.File.OpenedInode;
        if (inode == null) return;

        int uploadY = 0;
        int uploadH = frame.Height;
        int uploadX = 0;
        int uploadW = frame.Width;
        if (allowPartialDamage && frame.DamageBounds is { Width: > 0, Height: > 0 } dmg)
        {
            int x0 = Math.Clamp(dmg.X, 0, frame.Width - 1);
            int y0 = Math.Clamp(dmg.Y, 0, frame.Height - 1);
            int x1 = Math.Clamp(dmg.X + dmg.Width, x0 + 1, frame.Width);
            int y1 = Math.Clamp(dmg.Y + dmg.Height, y0 + 1, frame.Height);
            if ((long)(x1 - x0) * (y1 - y0) < (long)frame.Width * frame.Height * 3 / 4)
            {
                uploadX = x0;
                uploadY = y0;
                uploadW = x1 - x0;
                uploadH = y1 - y0;
            }
        }

        long readOffset = (long)frame.Offset + (long)uploadY * frame.Stride + (long)uploadX * 4;
        int byteCount = checked((uploadH - 1) * frame.Stride + uploadW * 4);
        var buffer = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            var span = buffer.AsSpan(0, byteCount);
            var segs = inode.GetReadableSegments(frame.File, readOffset, byteCount);
            if (!segs.Succeeded)
            {
                span.Clear();
                int read = inode.ReadToHost(null, frame.File, span, readOffset);
                if (read <= 0) return;
            }
            else
            {
                int pos = 0;
                foreach (var seg in segs)
                {
                    seg.CopyTo(span[pos..]);
                    pos += seg.Length;
                }
            }
            texture.Update(new DisplayRect(uploadX, uploadY, uploadW, uploadH), span, frame.Stride);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private SurfaceRenderState EnsureSurfaceTexture(ulong id, int w, int h, DisplayPixelFormat format)
    {
        if (_surfaces.TryGetValue(id, out var existing) && existing.Texture != null &&
            existing.Width == w && existing.Height == h && existing.Format == format)
            return existing;

        var state = EnsureSurfaceState(id);
        state.Texture?.Dispose();
        state.Texture = _renderer!.CreateTexture(new DisplayTextureDescriptor(w, h, format));
        state.Width = w;
        state.Height = h;
        state.Format = format;
        return state;
    }

    private SurfaceRenderState EnsureSurfaceState(ulong id)
    {
        if (_surfaces.TryGetValue(id, out var existing)) return existing;
        var state = new SurfaceRenderState();
        _surfaces[id] = state;
        return state;
    }

    private void UpdateDecoration(ulong id, WaylandDecorationSceneState decoration)
    {
        var state = EnsureSurfaceState(id);
        if (!ReferenceEquals(state.Decoration.ApplicationIcon, decoration.ApplicationIcon))
        {
            state.AppIconTexture?.Dispose();
            state.AppIconTexture = CreateAppIconTexture(decoration.ApplicationIcon);
        }
        state.Decoration = decoration;
    }

    private IDisplayTexture? CreateAppIconTexture(WaylandApplicationIcon? icon)
    {
        if (icon == null) return null;
        int size = (int)((Math.Clamp(_theme.Spacing.TitlebarHeight - 8, 12, 24) * Scale120 + 60) / 120);
        var pixels = _callbacks.TryRasterizeIcon(icon, size);
        if (pixels == null) return null;
        var tex = _renderer!.CreateTexture(new DisplayTextureDescriptor(size, size, DisplayPixelFormat.Argb8888));
        tex.Update(pixels, size * 4);
        return tex;
    }

    private void SetSurfaceLayer(ulong id, WaylandSceneLayer layer)
    {
        var state = EnsureSurfaceState(id);
        state.Layer = layer;
        if (_zOrder.Contains(id))
            PlaceAtTopOfLayer(id, layer);
    }

    private void RaiseSurface(ulong id)
    {
        if (_surfaces.TryGetValue(id, out var state))
            PlaceAtTopOfLayer(id, state.Layer);
    }

    private void PlaceAtTopOfLayer(ulong id, WaylandSceneLayer layer)
    {
        _zOrder.Remove(id);
        int idx = _zOrder.FindIndex(other => _surfaces[other].Layer > layer);
        _zOrder.Insert(idx < 0 ? _zOrder.Count : idx, id);
    }

    private void RemoveSurface(ulong id)
    {
        if (!_surfaces.Remove(id, out var s)) return;
        _zOrder.Remove(id);
        s.Texture?.Dispose();
        s.TitleTexture?.Dispose();
        s.AppIconTexture?.Dispose();
    }

    private void NotifyTextInputRect()
    {
        if (_textInputSurfaceId == 0 || !_surfaces.TryGetValue(_textInputSurfaceId, out var s) || s.Hidden)
        {
            EmitTextInputState(false, 0, 0, 0, 0);
            return;
        }

        if (_textInputRect is not { } r)
        {
            EmitTextInputState(true, -1, -1, 0, 0);
            return;
        }

        int x = Math.Clamp(s.Bounds.X + r.X, 0, Math.Max(1, LogicalWidth - 1));
        int y = Math.Clamp(s.Bounds.Y + r.Y, 0, Math.Max(1, LogicalHeight - 1));
        int w = Math.Max(1, r.Width);
        int h = Math.Max(1, r.Height);
        EmitTextInputState(true, x, y, w, h);
    }

    private void EmitTextInputState(bool active, int x, int y, int w, int h)
    {
        if (_lastNotifiedTextInputActive == active &&
            _lastNotifiedTextInputX == x &&
            _lastNotifiedTextInputY == y &&
            _lastNotifiedTextInputW == w &&
            _lastNotifiedTextInputH == h)
        {
            return;
        }

        _lastNotifiedTextInputActive = active;
        _lastNotifiedTextInputX = x;
        _lastNotifiedTextInputY = y;
        _lastNotifiedTextInputW = w;
        _lastNotifiedTextInputH = h;
        _callbacks.NotifyTextInput(active, x, y, w, h);
    }

    private void DrawDecoration(ulong id, SurfaceRenderState surface)
    {
        if (!surface.Decoration.Visible || surface.Decoration.Minimized) return;

        var wb = WaylandDecorationLayout.GetWindowBounds(surface.Bounds, surface.Decoration, _theme);
        var chrome = surface.Decoration.Active ? _theme.WindowDecoration.Active : _theme.WindowDecoration.Inactive;
        var metrics = WaylandDecorationMetrics.FromTheme(_theme);

        _renderer!.DrawChrome(
            new DisplayRect(wb.X, wb.Y, wb.Width, wb.Height),
            ToColor(surface.Decoration.Active ? chrome.BorderActive : chrome.Border),
            ToColor(chrome.Background),
            WaylandDecorationLayout.GetWindowCornerRadius(wb, surface.Decoration, _theme),
            metrics.BorderThickness,
            metrics.TitlebarHeight);

        if (surface.AppIconTexture is { } appIcon)
        {
            var iconBounds = WaylandDecorationLayout.GetApplicationIconBounds(wb, _theme);
            if (iconBounds.Width > 0)
            {
                var px = _renderer.GetPixelBounds(new DisplayRect(iconBounds.X, iconBounds.Y, iconBounds.Width, iconBounds.Height));
                _renderer.BlitPixels(appIcon, new DisplayRect(px.X + (px.Width - appIcon.Width) / 2,
                    px.Y + (px.Height - appIcon.Height) / 2, appIcon.Width, appIcon.Height));
            }
        }

        DrawTitle(surface, wb, chrome);
        DrawButton(id, surface.Decoration, WaylandSceneHitKind.MinimizeButton, WaylandDecorationLayout.GetMinimizeButtonBounds(wb, _theme));
        DrawButton(id, surface.Decoration, WaylandSceneHitKind.MaximizeButton, WaylandDecorationLayout.GetMaximizeButtonBounds(wb, _theme));
        DrawButton(id, surface.Decoration, WaylandSceneHitKind.CloseButton, WaylandDecorationLayout.GetCloseButtonBounds(wb, _theme));
    }

    private void DrawTitle(SurfaceRenderState surface, WaylandSurfaceBounds wb, WaylandSurfaceChromeTheme chrome)
    {
        if (string.IsNullOrWhiteSpace(surface.Decoration.Title)) return;
        var tb = WaylandDecorationLayout.GetTitleTextBounds(wb, _theme, reserveIcon: surface.AppIconTexture != null);
        if (tb.Width <= 0 || tb.Height <= 0) return;
        var px = _renderer!.GetPixelBounds(new DisplayRect(tb.X, tb.Y, tb.Width, tb.Height));
        int w = Math.Min(px.Width, Math.Max(1, _pixelWidth));
        if (w <= 0 || px.Height <= 0) return;

        var title = surface.Decoration.Title.Trim();
        if (surface.TitleTexture == null || surface.CachedTitle != title ||
            surface.TitleTexture.Width != w || surface.TitleTexture.Height != px.Height ||
            surface.CachedTitleColor != chrome.Foreground)
        {
            surface.TitleTexture?.Dispose();
            surface.TitleTexture = null;
            surface.CachedTitle = title;
            surface.CachedTitleColor = chrome.Foreground;

            float fontPx = _theme.Typography.TitleFontSize * Scale120 / 120f;
            var pixels = _callbacks.TryRasterizeText(title, w, px.Height, fontPx, chrome.Foreground, _theme.Typography.Alignment);
            if (pixels != null)
            {
                var tex = _renderer.CreateTexture(new DisplayTextureDescriptor(w, px.Height, DisplayPixelFormat.Argb8888));
                tex.Update(pixels, w * 4);
                surface.TitleTexture = tex;
            }
        }
        if (surface.TitleTexture != null)
            _renderer.BlitPixels(surface.TitleTexture, px with { Width = w });
    }

    private void DrawButton(ulong id, WaylandDecorationSceneState deco, WaylandSceneHitKind kind, WaylandSurfaceBounds bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        bool hover = _hoveredDecoration.SceneSurfaceId == id && _hoveredDecoration.Kind == kind;
        bool pressed = hover && _pressedDecoration.SceneSurfaceId == id && _pressedDecoration.Kind == kind;
        bool toggled = kind == WaylandSceneHitKind.MaximizeButton && deco.Maximized;

        var appearance = _theme.WindowDecoration.GetButton(kind)?.GetAppearance(deco.Active, toggled, hover, pressed)
            ?? new WaylandButtonAppearance(_theme.WindowDecoration.Buttons.Foreground,
                pressed ? _theme.WindowDecoration.Buttons.BackgroundPressed :
                hover ? _theme.WindowDecoration.Buttons.BackgroundHover :
                kind == WaylandSceneHitKind.CloseButton ? _theme.WindowDecoration.Buttons.BackgroundClose :
                kind == WaylandSceneHitKind.MaximizeButton ? _theme.WindowDecoration.Buttons.BackgroundMaximize :
                _theme.WindowDecoration.Buttons.BackgroundMinimize, null);

        if (appearance.Background.A != 0)
            _renderer!.FillRect(new DisplayRect(bounds.X, bounds.Y, bounds.Width, bounds.Height), ToColor(appearance.Background));

        var tex = GetButtonTexture(kind, appearance);
        double scale = Math.Min(1.0, Math.Min((double)bounds.Width / tex.Width, (double)bounds.Height / tex.Height));
        int w = Math.Max(1, (int)(tex.Width * scale));
        int h = Math.Max(1, (int)(tex.Height * scale));
        _renderer!.Blit(tex, new DisplayRect(bounds.X + (bounds.Width - w) / 2, bounds.Y + (bounds.Height - h) / 2, w, h));
    }

    private IDisplayTexture GetButtonTexture(WaylandSceneHitKind kind, WaylandButtonAppearance appearance)
    {
        var key = (kind, appearance.Foreground, appearance.Bitmap);
        if (_buttonIcons.TryGetValue(key, out var tex)) return tex;

        int w = appearance.Bitmap?.Width ?? 16;
        int h = appearance.Bitmap?.Height ?? 16;
        int pitch = w * 4;
        var pixels = new byte[pitch * h];
        var c = appearance.Foreground;

        if (appearance.Bitmap is { } bmp)
        {
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (bmp.IsSet(x, y)) PutPixel(pixels, pitch, x, y, c);
        }
        else
        {
            int stroke = Math.Max(1, _theme.Spacing.IconStrokeWidth);
            if (kind == WaylandSceneHitKind.CloseButton)
            {
                for (int i = 4; i <= w - 5; i++)
                {
                    DrawDot(pixels, w, h, pitch, i, i, stroke, c);
                    DrawDot(pixels, w, h, pitch, w - 1 - i, i, stroke, c);
                }
            }
            else if (kind == WaylandSceneHitKind.MaximizeButton)
            {
                for (int x = 4; x <= w - 5; x++)
                {
                    DrawDot(pixels, w, h, pitch, x, 4, stroke, c);
                    DrawDot(pixels, w, h, pitch, x, h - 5, stroke, c);
                }
                for (int y = 4; y <= h - 5; y++)
                {
                    DrawDot(pixels, w, h, pitch, 4, y, stroke, c);
                    DrawDot(pixels, w, h, pitch, w - 5, y, stroke, c);
                }
            }
            else
            {
                for (int x = 4; x <= w - 5; x++)
                    DrawDot(pixels, w, h, pitch, x, h / 2 + 2, stroke, c);
            }
        }

        tex = _renderer!.CreateTexture(new DisplayTextureDescriptor(w, h, DisplayPixelFormat.Argb8888), nearest: true);
        tex.Update(pixels, pitch);
        _buttonIcons[key] = tex;
        return tex;
    }

    private static void DrawDot(byte[] pixels, int w, int h, int pitch, int cx, int cy, int stroke, WaylandColor c)
    {
        for (int dy = 0; dy < stroke; dy++)
        for (int dx = 0; dx < stroke; dx++)
        {
            int x = cx + dx, y = cy + dy;
            if (x >= 0 && y >= 0 && x < w && y < h)
                PutPixel(pixels, pitch, x, y, c);
        }
    }

    private static void PutPixel(byte[] pixels, int pitch, int x, int y, WaylandColor c)
    {
        int i = y * pitch + x * 4;
        pixels[i] = (byte)(c.B * c.A / 255);
        pixels[i + 1] = (byte)(c.G * c.A / 255);
        pixels[i + 2] = (byte)(c.R * c.A / 255);
        pixels[i + 3] = c.A;
    }

    private static DisplayColor ToColor(WaylandColor c) => new(c.R, c.G, c.B, c.A);

    private readonly record struct CardLayout(DisplayRect PreviewRect, DisplayRect HeaderRect, DisplayRect CloseBtnRect);

    private sealed class RecentsCardItem
    {
        public ulong SceneSurfaceId;
        public string AppId = string.Empty;
        public string Title = string.Empty;
        public bool IsActive;
        public bool IsMinimized;
    }

    private sealed class DismissingCardAnim
    {
        public required RecentsCardItem Card;
        public int CardSlotIndex;
        public float InitialOffsetY;
        public double StartTime;
        public float Progress;
    }

    private sealed class SurfaceRenderState
    {
        public IDisplayTexture? Texture;
        public IDisplayTexture? TitleTexture;
        public IDisplayTexture? AppIconTexture;
        public int Width;
        public int Height;
        public DisplayPixelFormat Format;
        public WaylandSurfaceBounds Bounds;
        public WaylandSurfaceMapping? Mapping;
        public WaylandDecorationSceneState Decoration;
        public WaylandSceneLayer Layer = WaylandSceneLayer.Window;
        public bool Hidden;
        public string CachedTitle = string.Empty;
        public WaylandColor CachedTitleColor;
    }
}
