namespace Podish.Wayland;

public sealed partial class WaylandServer
{
    private WaylandSurfaceBounds? _workArea;

    internal WaylandSurfaceBounds OutputBounds => new(0, 0, Output.Width, Output.Height);
    internal WaylandSurfaceBounds DefaultWorkArea => new(0, 0, Output.Width, Math.Max(1, Output.Height - Math.Max(0, ReservedBottomInset)));
    internal WaylandSurfaceBounds WorkArea => _workArea ?? DefaultWorkArea;
    internal XdgToplevelResource[] MappedToplevels => _sceneSurfaces.Values
        .Select(static surface => surface.XdgSurface?.Toplevel)
        .OfType<XdgToplevelResource>()
        .Where(static toplevel => toplevel.IsMapped).ToArray();

    public ValueTask ResizeOutputAsync(int width, int height) =>
        ReconfigureOutputAsync(width, height, Output.Scale120, Output.ClientDpi);

    public async ValueTask ReconfigureOutputAsync(int width, int height, uint scale120, int clientDpi)
    {
        scale120 = Math.Clamp(scale120 == 0 ? 120u : scale120, 60u, 480u);
        clientDpi = Math.Max(0, clientDpi);
        if (width <= 0 || height <= 0)
            return;
        if (width == Output.Width && height == Output.Height && scale120 == Output.Scale120 && clientDpi == Output.ClientDpi)
            return;

        int intScale = Math.Max(1, (int)((scale120 + 119) / 120));
        Output = Output with
        {
            Width = width,
            Height = height,
            Scale = intScale,
            Scale120 = scale120,
            ClientDpi = clientDpi
        };
        if (FramePresenter is IWaylandDesktopSceneController scene)
            scene.ResizeDesktop(width, height);

        await ArrangeLayersAsync();
        await RefreshAllClientOutputsAsync();
    }

    internal async ValueTask ArrangeLayersAsync()
    {
        LayerSurfaceResource[] layers = _sceneSurfaces.Values
            .Select(static surface => surface.LayerSurface)
            .OfType<LayerSurfaceResource>()
            .Where(static layer => !layer.Destroyed && layer.HasCommitted)
            .OrderByDescending(static layer => layer.SceneLayer)
            .ToArray();
        WaylandSurfaceBounds available = DefaultWorkArea;
        foreach (LayerSurfaceResource layer in layers)
        {
            if (layer.ExclusiveZone <= 0)
                continue;
            await layer.ArrangeAsync(available);
            if (layer.IsMapped)
                available = layer.ReserveSpace(available);
        }

        foreach (LayerSurfaceResource layer in layers)
        {
            if (layer.ExclusiveZone <= 0)
                await layer.ArrangeAsync(layer.ExclusiveZone < 0 ? OutputBounds : available);
        }

        if (_workArea != available)
        {
            _workArea = available;
            foreach (XdgToplevelResource toplevel in MappedToplevels)
            {
                if (toplevel.IsMaximized && !toplevel.IsFullscreen)
                    await toplevel.UpdateMaximizedBoundsAsync();
            }

            foreach (WaylandClient client in _clients.ToArray())
            {
                foreach (XdgPopupResource popup in client.Objects.All.OfType<XdgPopupResource>().ToArray())
                    await popup.ReconfigureReactiveAsync();
            }
        }

        LayerSurfaceResource? exclusive = layers.FirstOrDefault(static layer => layer.IsMapped &&
            layer.SceneLayer >= WaylandSceneLayer.Top &&
            layer.KeyboardInteractivity == ZwlrLayerSurfaceV1KeyboardInteractivity.Exclusive);
        await Focus.SetExclusiveLayerAsync(exclusive);
    }

    internal XdgPopupResource[] GetChildPopups(WlSurfaceResource parent) => parent.Client.Objects.All
        .OfType<XdgPopupResource>().Where(popup => ReferenceEquals(popup.Parent, parent)).ToArray();

    internal async ValueTask DismissChildPopupsAsync(WlSurfaceResource parent)
    {
        foreach (XdgPopupResource popup in GetChildPopups(parent).Reverse())
            await popup.DismissAsync();
    }

    internal async ValueTask ReconfigureChildPopupsAsync(WlSurfaceResource parent)
    {
        foreach (XdgPopupResource popup in GetChildPopups(parent))
            await popup.ReconfigureReactiveAsync();
    }

    internal async ValueTask UpdateForeignToplevelAsync(XdgToplevelResource toplevel)
    {
        if (!toplevel.IsMapped)
        {
            await CloseForeignToplevelAsync(toplevel);
            return;
        }

        foreach (ForeignToplevelManagerResource manager in SnapshotForeignManagers())
            await manager.UpdateAsync(toplevel);
        NotifyToplevelsChanged();
    }

    internal async ValueTask CloseForeignToplevelAsync(XdgToplevelResource toplevel)
    {
        foreach (ForeignToplevelManagerResource manager in SnapshotForeignManagers())
            await manager.CloseToplevelAsync(toplevel);
        NotifyToplevelsChanged();
    }

    private ForeignToplevelManagerResource[] SnapshotForeignManagers() => _clients
        .SelectMany(static client => client.Objects.All.OfType<ForeignToplevelManagerResource>()).ToArray();
}
