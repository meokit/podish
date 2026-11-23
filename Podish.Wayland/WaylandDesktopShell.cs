namespace Podish.Wayland;

public sealed partial class WaylandServer
{
    private WaylandSurfaceBounds? _workArea;

    internal WaylandSurfaceBounds OutputBounds => new(0, 0, Output.Width, Output.Height);
    internal WaylandSurfaceBounds WorkArea => _workArea ?? OutputBounds;
    internal XdgToplevelResource[] MappedToplevels => _sceneSurfaces.Values
        .Select(static surface => surface.XdgSurface?.Toplevel)
        .OfType<XdgToplevelResource>()
        .Where(static toplevel => toplevel.IsMapped).ToArray();

    public async ValueTask ResizeOutputAsync(int width, int height)
    {
        if (width <= 0 || height <= 0 || (width == Output.Width && height == Output.Height))
            return;
        Output = Output with { Width = width, Height = height };
        if (FramePresenter is IWaylandDesktopSceneController scene)
            scene.ResizeDesktop(width, height);

        foreach (WaylandClient client in _clients.ToArray())
        {
            foreach (WlOutputResource output in client.Objects.All.OfType<WlOutputResource>().ToArray())
                await output.AdvertiseAsync();
        }

        await ArrangeLayersAsync();
        foreach (XdgToplevelResource toplevel in MappedToplevels)
        {
            if (toplevel.IsFullscreen)
                await toplevel.UpdateMaximizedBoundsAsync();
        }
        foreach (WlSurfaceResource surface in _sceneSurfaces.Values.ToArray())
            surface.UpdateChildSubsurfacePlacements();
    }

    internal async ValueTask ArrangeLayersAsync()
    {
        LayerSurfaceResource[] layers = _sceneSurfaces.Values
            .Select(static surface => surface.LayerSurface)
            .OfType<LayerSurfaceResource>()
            .Where(static layer => !layer.Destroyed && layer.HasCommitted)
            .OrderByDescending(static layer => layer.SceneLayer)
            .ToArray();
        WaylandSurfaceBounds available = OutputBounds;
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
    }

    internal async ValueTask CloseForeignToplevelAsync(XdgToplevelResource toplevel)
    {
        foreach (ForeignToplevelManagerResource manager in SnapshotForeignManagers())
            await manager.CloseToplevelAsync(toplevel);
    }

    private ForeignToplevelManagerResource[] SnapshotForeignManagers() => _clients
        .SelectMany(static client => client.Objects.All.OfType<ForeignToplevelManagerResource>()).ToArray();
}
