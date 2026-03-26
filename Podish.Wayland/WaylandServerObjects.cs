namespace Podish.Wayland;

public sealed record WaylandRect(int X, int Y, int Width, int Height);

internal sealed class WlDisplayResource : WaylandResource
{
    public WlDisplayResource(WaylandClient client, uint objectId, uint version)
        : base(client, objectId, version, WlDisplayProtocol.InterfaceName)
    {
    }

    public override IReadOnlyList<WaylandMessageMetadata> Requests => WlDisplayProtocol.Requests;

    public override async ValueTask DispatchAsync(WaylandIncomingMessage message)
    {
        switch (message.Header.Opcode)
        {
            case 0:
            {
                var request = WlDisplayProtocol.DecodeSync(message.Body, message.Fds);
                var callback = Client.Register(new WlCallbackResource(Client, request.Callback, 1));
                await callback.SendDoneAndDisposeAsync(Client.NextSerial());
                break;
            }
            case 1:
            {
                var request = WlDisplayProtocol.DecodeGetRegistry(message.Body, message.Fds);
                var registry = Client.Register(new WlRegistryResource(Client, request.Registry, 1));
                await registry.AdvertiseGlobalsAsync();
                break;
            }
            default:
                throw new WaylandProtocolException(ObjectId, 0, $"Unsupported wl_display opcode {message.Header.Opcode}.");
        }
    }
}

internal sealed class WlRegistryResource : WaylandResource
{
    public WlRegistryResource(WaylandClient client, uint objectId, uint version)
        : base(client, objectId, version, WlRegistryProtocol.InterfaceName)
    {
    }

    public override IReadOnlyList<WaylandMessageMetadata> Requests => WlRegistryProtocol.Requests;

    public async ValueTask AdvertiseGlobalsAsync()
    {
        foreach (WaylandGlobal global in Client.Server.Globals.Globals)
            await WlRegistryEventWriter.GlobalAsync(Client, ObjectId, global.Name, global.Interface, global.Version);
    }

    public override ValueTask DispatchAsync(WaylandIncomingMessage message)
    {
        var request = WlRegistryProtocol.DecodeBind(message.Body, message.Fds);
        WaylandGlobal global = Client.Server.Globals.Require(request.Name);
        if (!string.Equals(global.Interface, request.Interface, StringComparison.Ordinal))
            throw new WaylandProtocolException(ObjectId, 0,
                $"Global {request.Name} is {global.Interface}, not {request.Interface}.");
        if (request.Version == 0 || request.Version > global.Version)
            throw new WaylandProtocolException(ObjectId, 0,
                $"Requested version {request.Version} is invalid for global {global.Interface}@{global.Version}.");

        WaylandResource resource = global.Bind(Client, request.Id, request.Version);
        if (resource is WlShmResource shm)
            return shm.AdvertiseFormatsAsync();
        if (resource is WlSeatResource seat)
            return seat.AdvertiseCapabilitiesAsync();
        if (resource is WlOutputResource output)
            return output.AdvertiseAsync();
        if (resource is XdgWmBaseResource wmBase)
            return wmBase.PingAsync();
        return ValueTask.CompletedTask;
    }
}

internal sealed class WlCallbackResource : WaylandResource
{
    public WlCallbackResource(WaylandClient client, uint objectId, uint version)
        : base(client, objectId, version, WlCallbackProtocol.InterfaceName)
    {
    }

    public override IReadOnlyList<WaylandMessageMetadata> Requests => WlCallbackProtocol.Requests;

    public override ValueTask DispatchAsync(WaylandIncomingMessage message)
    {
        throw new WaylandProtocolException(ObjectId, 0, "wl_callback has no requests.");
    }

    public async ValueTask SendDoneAndDisposeAsync(uint callbackData)
    {
        await WlCallbackEventWriter.DoneAsync(Client, ObjectId, callbackData);
        Destroy();
        await Client.DeleteIdAsync(ObjectId);
    }
}

internal sealed class WlCompositorResource : WaylandResource
{
    public WlCompositorResource(WaylandClient client, uint objectId, uint version)
        : base(client, objectId, version, WlCompositorProtocol.InterfaceName)
    {
    }

    public override IReadOnlyList<WaylandMessageMetadata> Requests => WlCompositorProtocol.Requests;

    public override ValueTask DispatchAsync(WaylandIncomingMessage message)
    {
        switch (message.Header.Opcode)
        {
            case 0:
            {
                var request = WlCompositorProtocol.DecodeCreateSurface(message.Body, message.Fds);
                Client.Register(new WlSurfaceResource(Client, request.Id, Math.Min(Version, WlSurfaceProtocol.Version)));
                break;
            }
            case 1:
            {
                var request = WlCompositorProtocol.DecodeCreateRegion(message.Body, message.Fds);
                Client.Register(new WlRegionResource(Client, request.Id, 1));
                break;
            }
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class WlSurfaceState
{
    public WlBufferResource? Buffer { get; set; }
    public List<WaylandRect> Damages { get; } = [];
    public List<WlCallbackResource> FrameCallbacks { get; } = [];
}

internal enum WlSurfaceRole
{
    None,
    Xdg,
    Cursor,
    Subsurface
}

internal sealed class WlSurfaceResource : WaylandResource
{
    private readonly WlSurfaceState _pending = new();
    private readonly WlSurfaceState _current = new();
    private readonly HashSet<uint> _enteredOutputIds = [];
    private readonly List<WlSubsurfaceResource> _childSubsurfaces = [];
    private WlSurfaceRole _role;
    private WlPointerResource? _cursorOwner;
    private int _cursorHotspotX;
    private int _cursorHotspotY;
    private WlSubsurfaceResource? _subsurfaceRole;

    public WlSurfaceResource(WaylandClient client, uint objectId, uint version)
        : base(client, objectId, version, WlSurfaceProtocol.InterfaceName)
    {
        SceneSurfaceId = client.Server.AllocateSceneSurfaceId();
        client.Server.RegisterSceneSurface(this);
    }

    public override IReadOnlyList<WaylandMessageMetadata> Requests => WlSurfaceProtocol.Requests;
    public ulong SceneSurfaceId { get; }
    public WlBufferResource? CurrentBuffer => _current.Buffer;
    public XdgSurfaceResource? XdgSurface { get; private set; }
    public bool IsCursorRole => _role == WlSurfaceRole.Cursor;
    public WlSubsurfaceResource? Subsurface => _subsurfaceRole;

    public void AttachXdgSurface(XdgSurfaceResource xdgSurface)
    {
        if (_role != WlSurfaceRole.None)
            throw new WaylandProtocolException(ObjectId, 0, "wl_surface already has a role.");
        _role = WlSurfaceRole.Xdg;
        XdgSurface = xdgSurface;
    }

    public async ValueTask SetCursorRoleAsync(WlPointerResource owner, int hotspotX, int hotspotY)
    {
        if (_role != WlSurfaceRole.None && _role != WlSurfaceRole.Cursor)
            throw new WaylandProtocolException(ObjectId, 0, "wl_surface already has a role.");

        if (_role == WlSurfaceRole.None)
            _role = WlSurfaceRole.Cursor;

        _cursorOwner = owner;
        _cursorHotspotX = hotspotX;
        _cursorHotspotY = hotspotY;

        if (Client.Server.FramePresenter != null)
            await Client.Server.FramePresenter.PresentSurfaceAsync(SceneSurfaceId, null);

        if (_current.Buffer != null && Client.Server.FramePresenter is IWaylandCursorPresenter cursorPresenter)
        {
            await cursorPresenter.UpdateCursorAsync(
                SceneSurfaceId,
                new WaylandCursorFrame(SceneSurfaceId, _current.Buffer.ToFrame(SceneSurfaceId), _cursorHotspotX, _cursorHotspotY));
        }
    }

    public async ValueTask ClearCursorRoleAsync(WlPointerResource owner)
    {
        if (_role != WlSurfaceRole.Cursor || !ReferenceEquals(_cursorOwner, owner))
            return;

        _cursorOwner = null;
        if (Client.Server.FramePresenter is IWaylandCursorPresenter cursorPresenter)
            await cursorPresenter.UpdateCursorAsync(SceneSurfaceId, null);
    }

    public void AttachSubsurface(WlSubsurfaceResource subsurface)
    {
        if (_role != WlSurfaceRole.None)
            throw new WaylandProtocolException(ObjectId, (uint)WlSubcompositorError.BadSurface,
                "wl_surface already has a role.");

        _role = WlSurfaceRole.Subsurface;
        _subsurfaceRole = subsurface;
        subsurface.ParentSurface.AddChildSubsurface(subsurface);
    }

    public void ClearSubsurfaceAssociation(WlSubsurfaceResource subsurface)
    {
        if (ReferenceEquals(_subsurfaceRole, subsurface))
            _subsurfaceRole = null;
    }

    public void AddChildSubsurface(WlSubsurfaceResource subsurface)
    {
        if (!_childSubsurfaces.Contains(subsurface))
            _childSubsurfaces.Add(subsurface);
    }

    public void RemoveChildSubsurface(WlSubsurfaceResource subsurface)
    {
        _childSubsurfaces.Remove(subsurface);
    }

    public void UpdateChildSubsurfacePlacements()
    {
        foreach (WlSubsurfaceResource subsurface in _childSubsurfaces.ToArray())
            subsurface.UpdatePlacementRecursive();
    }

    public override async ValueTask DispatchAsync(WaylandIncomingMessage message)
    {
        switch (message.Header.Opcode)
        {
            case 0:
                Destroy();
                break;
            case 1:
            {
                var request = WlSurfaceProtocol.DecodeAttach(message.Body, message.Fds);
                _pending.Buffer = request.Buffer == 0 ? null : Client.Objects.Require<WlBufferResource>(request.Buffer);
                break;
            }
            case 2:
            {
                var request = WlSurfaceProtocol.DecodeDamage(message.Body, message.Fds);
                _pending.Damages.Add(new WaylandRect(request.X, request.Y, request.Width, request.Height));
                break;
            }
            case 3:
            {
                var request = WlSurfaceProtocol.DecodeFrame(message.Body, message.Fds);
                _pending.FrameCallbacks.Add(Client.Register(new WlCallbackResource(Client, request.Callback, 1)));
                break;
            }
            case 4:
            case 5:
            {
                var reader = new WaylandWireReader(message.Body, message.Fds);
                uint regionId = reader.ReadObjectId();
                if (regionId != 0)
                    Client.Objects.Require<WlRegionResource>(regionId);
                reader.EnsureExhausted();
                break;
            }
            case 6:
                await CommitAsync();
                break;
            case 7:
            case 8:
            {
                var reader = new WaylandWireReader(message.Body, message.Fds);
                _ = reader.ReadInt();
                reader.EnsureExhausted();
                break;
            }
            case 9:
            {
                var request = WlSurfaceProtocol.DecodeDamageBuffer(message.Body, message.Fds);
                _pending.Damages.Add(new WaylandRect(request.X, request.Y, request.Width, request.Height));
                break;
            }
            default:
                throw new WaylandProtocolException(ObjectId, 0, $"Unsupported wl_surface opcode {message.Header.Opcode}.");
        }
    }

    private async ValueTask CommitAsync()
    {
        if (XdgSurface != null && _pending.Buffer != null && !XdgSurface.IsConfigured)
            throw new WaylandProtocolException(ObjectId, 0, "xdg_surface must ack_configure before first buffer commit.");

        _current.Buffer = _pending.Buffer;
        _current.Damages.Clear();
        _current.Damages.AddRange(_pending.Damages);
        _pending.Damages.Clear();

        if (_role == WlSurfaceRole.Cursor)
        {
            if (Client.Server.FramePresenter is IWaylandCursorPresenter cursorPresenter)
            {
                WaylandCursorFrame? cursor = _current.Buffer == null
                    ? null
                    : new WaylandCursorFrame(SceneSurfaceId, _current.Buffer.ToFrame(SceneSurfaceId), _cursorHotspotX, _cursorHotspotY);
                await cursorPresenter.UpdateCursorAsync(SceneSurfaceId, cursor);
            }
        }
        else if (Client.Server.FramePresenter != null)
        {
            await Client.Server.FramePresenter.PresentSurfaceAsync(SceneSurfaceId, _current.Buffer?.ToFrame(SceneSurfaceId));
        }

        _subsurfaceRole?.UpdatePlacementRecursive();
        UpdateChildSubsurfacePlacements();

        await SyncOutputPresenceAsync(_role != WlSurfaceRole.Cursor && _current.Buffer != null);

        List<WlCallbackResource> callbacks = [.. _pending.FrameCallbacks];
        _pending.FrameCallbacks.Clear();
        Client.Server.EnqueueFrameCallbacks(callbacks);
    }

    private async ValueTask SyncOutputPresenceAsync(bool entered)
    {
        List<WlOutputResource> outputs = Client.Objects.All.OfType<WlOutputResource>().ToList();
        if (entered)
        {
            foreach (WlOutputResource output in outputs)
            {
                if (_enteredOutputIds.Add(output.ObjectId))
                    await WlSurfaceEventWriter.EnterAsync(Client, ObjectId, output.ObjectId);
            }

            return;
        }

        foreach (uint outputId in _enteredOutputIds.ToArray())
        {
            await WlSurfaceEventWriter.LeaveAsync(Client, ObjectId, outputId);
            _enteredOutputIds.Remove(outputId);
        }
    }

    public override void Destroy()
    {
        foreach (WlSubsurfaceResource childSubsurface in _childSubsurfaces.ToArray())
            childSubsurface.UpdatePlacementRecursive();
        if (_enteredOutputIds.Count > 0)
            SyncOutputPresenceAsync(false).AsTask().GetAwaiter().GetResult();
        if (_role == WlSurfaceRole.Cursor && Client.Server.FramePresenter is IWaylandCursorPresenter cursorPresenter)
            cursorPresenter.UpdateCursorAsync(SceneSurfaceId, null).AsTask().GetAwaiter().GetResult();
        else if (Client.Server.FramePresenter != null)
            Client.Server.FramePresenter.PresentSurfaceAsync(SceneSurfaceId, null).AsTask().GetAwaiter().GetResult();
        Client.Server.Focus.HandleSurfaceDestroyedAsync(SceneSurfaceId).AsTask().GetAwaiter().GetResult();
        Client.Server.UnregisterSceneSurface(this);
        base.Destroy();
    }
}

internal sealed class WlRegionResource : WaylandResource
{
    public WlRegionResource(WaylandClient client, uint objectId, uint version)
        : base(client, objectId, version, WlRegionProtocol.InterfaceName)
    {
    }

    public List<WaylandRect> Added { get; } = [];
    public List<WaylandRect> Subtracted { get; } = [];

    public override IReadOnlyList<WaylandMessageMetadata> Requests => WlRegionProtocol.Requests;

    public override ValueTask DispatchAsync(WaylandIncomingMessage message)
    {
        switch (message.Header.Opcode)
        {
            case 0:
                Destroy();
                break;
            case 1:
            case 2:
            {
                var reader = new WaylandWireReader(message.Body, message.Fds);
                var rect = new WaylandRect(reader.ReadInt(), reader.ReadInt(), reader.ReadInt(), reader.ReadInt());
                reader.EnsureExhausted();
                if (message.Header.Opcode == 1)
                    Added.Add(rect);
                else
                    Subtracted.Add(rect);
                break;
            }
            default:
                throw new WaylandProtocolException(ObjectId, 0, $"Unsupported wl_region opcode {message.Header.Opcode}.");
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class WlSubcompositorResource : WaylandResource
{
    public WlSubcompositorResource(WaylandClient client, uint objectId, uint version)
        : base(client, objectId, version, WlSubcompositorProtocol.InterfaceName)
    {
    }

    public override IReadOnlyList<WaylandMessageMetadata> Requests => WlSubcompositorProtocol.Requests;

    public override ValueTask DispatchAsync(WaylandIncomingMessage message)
    {
        switch (message.Header.Opcode)
        {
            case 0:
                Destroy();
                break;
            case 1:
            {
                var request = WlSubcompositorProtocol.DecodeGetSubsurface(message.Body, message.Fds);
                WlSurfaceResource surface = Client.Objects.Require<WlSurfaceResource>(request.Surface);
                WlSurfaceResource parent = Client.Objects.Require<WlSurfaceResource>(request.Parent);
                if (ReferenceEquals(surface, parent))
                {
                    throw new WaylandProtocolException(ObjectId, (uint)WlSubcompositorError.BadParent,
                        "wl_subsurface parent must be a different wl_surface.");
                }

                var subsurface = Client.Register(new WlSubsurfaceResource(Client, request.Id, 1, surface, parent));
                surface.AttachSubsurface(subsurface);
                subsurface.UpdatePlacementRecursive();
                break;
            }
            default:
                throw new WaylandProtocolException(ObjectId, 0,
                    $"Unsupported wl_subcompositor opcode {message.Header.Opcode}.");
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class WlSubsurfaceResource : WaylandResource
{
    private int _positionX;
    private int _positionY;
    private bool _sync = true;

    public WlSubsurfaceResource(WaylandClient client, uint objectId, uint version, WlSurfaceResource surface,
        WlSurfaceResource parentSurface)
        : base(client, objectId, version, WlSubsurfaceProtocol.InterfaceName)
    {
        Surface = surface;
        ParentSurface = parentSurface;
    }

    public WlSurfaceResource Surface { get; }
    public WlSurfaceResource ParentSurface { get; }
    public override IReadOnlyList<WaylandMessageMetadata> Requests => WlSubsurfaceProtocol.Requests;

    public override ValueTask DispatchAsync(WaylandIncomingMessage message)
    {
        switch (message.Header.Opcode)
        {
            case 0:
                Destroy();
                break;
            case 1:
            {
                var request = WlSubsurfaceProtocol.DecodeSetPosition(message.Body, message.Fds);
                _positionX = request.X;
                _positionY = request.Y;
                UpdatePlacementRecursive();
                break;
            }
            case 2:
            {
                WlSurfaceResource sibling = Client.Objects.Require<WlSurfaceResource>(
                    WlSubsurfaceProtocol.DecodePlaceAbove(message.Body, message.Fds).Sibling);
                ValidateSiblingRelationship(sibling);
                UpdatePlacementRecursive();
                break;
            }
            case 3:
            {
                WlSurfaceResource sibling = Client.Objects.Require<WlSurfaceResource>(
                    WlSubsurfaceProtocol.DecodePlaceBelow(message.Body, message.Fds).Sibling);
                ValidateSiblingRelationship(sibling);
                break;
            }
            case 4:
                _sync = true;
                break;
            case 5:
                _sync = false;
                break;
            default:
                throw new WaylandProtocolException(ObjectId, 0, $"Unsupported wl_subsurface opcode {message.Header.Opcode}.");
        }

        return ValueTask.CompletedTask;
    }

    public void UpdatePlacementRecursive()
    {
        if (Client.Server.FramePresenter is not IWaylandDesktopSceneController sceneController ||
            Client.Server.FramePresenter is not IWaylandSceneView sceneView ||
            !sceneView.TryGetSurfaceBounds(ParentSurface.SceneSurfaceId, out WaylandSurfaceBounds parentBounds))
            return;

        int width = Surface.CurrentBuffer?.Width ?? 0;
        int height = Surface.CurrentBuffer?.Height ?? 0;
        if (sceneView.TryGetSurfaceBounds(Surface.SceneSurfaceId, out WaylandSurfaceBounds currentBounds))
        {
            width = currentBounds.Width;
            height = currentBounds.Height;
        }

        if (width <= 0 || height <= 0)
            return;

        sceneController.SetSurfaceBounds(
            Surface.SceneSurfaceId,
            new WaylandSurfaceBounds(parentBounds.X + _positionX, parentBounds.Y + _positionY, width, height));

        if (!_sync)
            sceneController.RaiseSurface(Surface.SceneSurfaceId);

        Surface.UpdateChildSubsurfacePlacements();
    }

    public override void Destroy()
    {
        if (Destroyed)
            return;

        ParentSurface.RemoveChildSubsurface(this);
        Surface.ClearSubsurfaceAssociation(this);
        if (!Surface.Destroyed)
        {
            if (Client.Server.FramePresenter != null)
                Client.Server.FramePresenter.PresentSurfaceAsync(Surface.SceneSurfaceId, null).AsTask().GetAwaiter().GetResult();
            Surface.UpdateChildSubsurfacePlacements();
        }

        base.Destroy();
    }

    private void ValidateSiblingRelationship(WlSurfaceResource sibling)
    {
        if (ReferenceEquals(sibling, ParentSurface))
            return;

        if (!ReferenceEquals(sibling.Subsurface?.ParentSurface, ParentSurface))
        {
            throw new WaylandProtocolException(ObjectId, (uint)WlSubsurfaceError.BadSurface,
                "wl_subsurface sibling must be the parent or another child of the same parent.");
        }
    }
}

internal sealed class WlShmResource : WaylandResource
{
    public WlShmResource(WaylandClient client, uint objectId, uint version)
        : base(client, objectId, version, WlShmProtocol.InterfaceName)
    {
    }

    public override IReadOnlyList<WaylandMessageMetadata> Requests => WlShmProtocol.Requests;

    public async ValueTask AdvertiseFormatsAsync()
    {
        await WlShmEventWriter.FormatAsync(Client, ObjectId, WlShmFormat.Argb8888);
        await WlShmEventWriter.FormatAsync(Client, ObjectId, WlShmFormat.Xrgb8888);
    }

    public override async ValueTask DispatchAsync(WaylandIncomingMessage message)
    {
        var request = WlShmProtocol.DecodeCreatePool(message.Body, message.Fds);
        if (request.Size <= 0)
            throw new WaylandProtocolException(ObjectId, 0, "wl_shm_pool size must be positive.");

        Client.Register(new WlShmPoolResource(Client, request.Id, 1, request.Fd, request.Size));
    }
}

internal sealed class WlDataOfferResource : WaylandResource
{
    public WlDataOfferResource(WaylandClient client, uint objectId, uint version)
        : base(client, objectId, version, WlDataOfferProtocol.InterfaceName)
    {
    }

    public override IReadOnlyList<WaylandMessageMetadata> Requests => WlDataOfferProtocol.Requests;

    public override ValueTask DispatchAsync(WaylandIncomingMessage message)
    {
        switch (message.Header.Opcode)
        {
            case 0:
                _ = WlDataOfferProtocol.DecodeAccept(message.Body, message.Fds);
                break;
            case 1:
            {
                var request = WlDataOfferProtocol.DecodeReceive(message.Body, message.Fds);
                request.Fd.Close();
                break;
            }
            case 2:
            case 3:
                Destroy();
                break;
            case 4:
                _ = WlDataOfferProtocol.DecodeSetActions(message.Body, message.Fds);
                break;
            default:
                throw new WaylandProtocolException(ObjectId, 0, $"Unsupported wl_data_offer opcode {message.Header.Opcode}.");
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class WlDataSourceResource : WaylandResource
{
    public WlDataSourceResource(WaylandClient client, uint objectId, uint version)
        : base(client, objectId, version, WlDataSourceProtocol.InterfaceName)
    {
    }

    public List<string> MimeTypes { get; } = [];
    public WlDataDeviceManagerDndAction DndActions { get; private set; }
    public override IReadOnlyList<WaylandMessageMetadata> Requests => WlDataSourceProtocol.Requests;

    public override ValueTask DispatchAsync(WaylandIncomingMessage message)
    {
        switch (message.Header.Opcode)
        {
            case 0:
            {
                string mimeType = WlDataSourceProtocol.DecodeOffer(message.Body, message.Fds).MimeType;
                if (!string.IsNullOrWhiteSpace(mimeType))
                    MimeTypes.Add(mimeType);
                break;
            }
            case 1:
                Destroy();
                break;
            case 2:
                DndActions = WlDataSourceProtocol.DecodeSetActions(message.Body, message.Fds).DndActions;
                break;
            default:
                throw new WaylandProtocolException(ObjectId, 0, $"Unsupported wl_data_source opcode {message.Header.Opcode}.");
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class WlDataDeviceResource : WaylandResource
{
    public WlDataDeviceResource(WaylandClient client, uint objectId, uint version, WlSeatResource seat)
        : base(client, objectId, version, WlDataDeviceProtocol.InterfaceName)
    {
        Seat = seat;
    }

    public WlSeatResource Seat { get; }
    public override IReadOnlyList<WaylandMessageMetadata> Requests => WlDataDeviceProtocol.Requests;

    public ValueTask SendInitialSelectionAsync()
    {
        return WlDataDeviceEventWriter.SelectionAsync(Client, ObjectId, 0);
    }

    public override ValueTask DispatchAsync(WaylandIncomingMessage message)
    {
        switch (message.Header.Opcode)
        {
            case 0:
            {
                var request = WlDataDeviceProtocol.DecodeStartDrag(message.Body, message.Fds);
                if (request.Source != 0)
                    Client.Objects.Require<WlDataSourceResource>(request.Source);
                Client.Objects.Require<WlSurfaceResource>(request.Origin);
                if (request.Icon != 0)
                    Client.Objects.Require<WlSurfaceResource>(request.Icon);
                break;
            }
            case 1:
            {
                var request = WlDataDeviceProtocol.DecodeSetSelection(message.Body, message.Fds);
                if (request.Source != 0)
                    Client.Objects.Require<WlDataSourceResource>(request.Source);
                break;
            }
            case 2:
                Destroy();
                break;
            default:
                throw new WaylandProtocolException(ObjectId, 0, $"Unsupported wl_data_device opcode {message.Header.Opcode}.");
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class WlDataDeviceManagerResource : WaylandResource
{
    public WlDataDeviceManagerResource(WaylandClient client, uint objectId, uint version)
        : base(client, objectId, version, WlDataDeviceManagerProtocol.InterfaceName)
    {
    }

    public override IReadOnlyList<WaylandMessageMetadata> Requests => WlDataDeviceManagerProtocol.Requests;

    public override ValueTask DispatchAsync(WaylandIncomingMessage message)
    {
        switch (message.Header.Opcode)
        {
            case 0:
            {
                var request = WlDataDeviceManagerProtocol.DecodeCreateDataSource(message.Body, message.Fds);
                Client.Register(new WlDataSourceResource(Client, request.Id, 3));
                break;
            }
            case 1:
            {
                var request = WlDataDeviceManagerProtocol.DecodeGetDataDevice(message.Body, message.Fds);
                WlSeatResource seat = Client.Objects.Require<WlSeatResource>(request.Seat);
                var dataDevice = Client.Register(new WlDataDeviceResource(Client, request.Id, 3, seat));
                dataDevice.SendInitialSelectionAsync().AsTask().GetAwaiter().GetResult();
                break;
            }
            default:
                throw new WaylandProtocolException(ObjectId, 0,
                    $"Unsupported wl_data_device_manager opcode {message.Header.Opcode}.");
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class WlShmPoolResource : WaylandResource
{
    public WlShmPoolResource(WaylandClient client, uint objectId, uint version, LinuxFile fd, int size)
        : base(client, objectId, version, WlShmPoolProtocol.InterfaceName)
    {
        Fd = fd;
        Size = size;
    }

    public LinuxFile Fd { get; }
    public int Size { get; private set; }
    public override IReadOnlyList<WaylandMessageMetadata> Requests => WlShmPoolProtocol.Requests;

    public bool Owns(LinuxFile fd) => ReferenceEquals(Fd, fd);

    public override ValueTask DispatchAsync(WaylandIncomingMessage message)
    {
        switch (message.Header.Opcode)
        {
            case 0:
            {
                var request = WlShmPoolProtocol.DecodeCreateBuffer(message.Body, message.Fds);
                if (request.Width <= 0 || request.Height <= 0 || request.Stride <= 0 || request.Offset < 0)
                    throw new WaylandProtocolException(ObjectId, 0, "wl_buffer parameters must be positive.");
                Client.Register(new WlBufferResource(Client, request.Id, 1, this, request.Offset, request.Width,
                    request.Height, request.Stride, request.Format));
                break;
            }
            case 1:
                Destroy();
                break;
            case 2:
            {
                var request = WlShmPoolProtocol.DecodeResize(message.Body, message.Fds);
                if (request.Size <= 0)
                    throw new WaylandProtocolException(ObjectId, 0, "wl_shm_pool size must stay positive.");
                Size = request.Size;
                break;
            }
            default:
                throw new WaylandProtocolException(ObjectId, 0, $"Unsupported wl_shm_pool opcode {message.Header.Opcode}.");
        }

        return ValueTask.CompletedTask;
    }

    public override void Destroy()
    {
        base.Destroy();
        Fd.Close();
    }
}

internal sealed class WlBufferResource : WaylandResource
{
    private readonly HashSet<ulong> _displayLeaseTokens = [];

    public WlBufferResource(WaylandClient client, uint objectId, uint version, WlShmPoolResource pool, int offset,
        int width, int height, int stride, WlShmFormat format)
        : base(client, objectId, version, WlBufferProtocol.InterfaceName)
    {
        Pool = pool;
        Offset = offset;
        Width = width;
        Height = height;
        Stride = stride;
        Format = format;
    }

    public WlShmPoolResource Pool { get; }
    public int Offset { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public WlShmFormat Format { get; }

    public override IReadOnlyList<WaylandMessageMetadata> Requests => WlBufferProtocol.Requests;

    public WaylandShmFrame ToFrame(ulong sceneSurfaceId)
    {
        ulong leaseToken = Client.Server.AllocateDisplayLeaseToken();
        _displayLeaseTokens.Add(leaseToken);
        return new WaylandShmFrame(sceneSurfaceId, leaseToken, Pool.Fd, Offset, Width, Height, Stride, Format);
    }

    public ValueTask SendReleaseAsync()
    {
        if (Destroyed)
            return ValueTask.CompletedTask;
        return WlBufferEventWriter.ReleaseAsync(Client, ObjectId);
    }

    public bool HasDisplayLease(ulong leaseToken)
    {
        return _displayLeaseTokens.Contains(leaseToken);
    }

    public async ValueTask CompleteDisplayLeaseAsync(ulong leaseToken)
    {
        if (!_displayLeaseTokens.Remove(leaseToken))
            return;

        await SendReleaseAsync();
    }

    public override ValueTask DispatchAsync(WaylandIncomingMessage message)
    {
        Destroy();
        return ValueTask.CompletedTask;
    }
}

internal sealed class WlSeatResource : WaylandResource
{
    public WlSeatResource(WaylandClient client, uint objectId, uint version)
        : base(client, objectId, version, WlSeatProtocol.InterfaceName)
    {
    }

    public override IReadOnlyList<WaylandMessageMetadata> Requests => WlSeatProtocol.Requests;

    public async ValueTask AdvertiseCapabilitiesAsync()
    {
        await WlSeatEventWriter.CapabilitiesAsync(Client, ObjectId, WlSeatCapability.Pointer | WlSeatCapability.Keyboard);
        if (Version >= 2)
            await WlSeatEventWriter.NameAsync(Client, ObjectId, "seat0");
    }

    public override async ValueTask DispatchAsync(WaylandIncomingMessage message)
    {
        switch (message.Header.Opcode)
        {
            case 0:
            {
                var request = WlSeatProtocol.DecodeGetPointer(message.Body, message.Fds);
                Client.Register(new WlPointerResource(Client, request.Id, Math.Min(Version, WlPointerProtocol.Version)));
                break;
            }
            case 1:
            {
                var request = WlSeatProtocol.DecodeGetKeyboard(message.Body, message.Fds);
                var keyboard = Client.Register(new WlKeyboardResource(Client, request.Id, Math.Min(Version, WlKeyboardProtocol.Version)));
                await keyboard.SendInitialStateAsync();
                break;
            }
            case 2:
                throw new WaylandProtocolException(ObjectId, 0, "wl_touch is not implemented.");
            case 3:
                Destroy();
                break;
            default:
                throw new WaylandProtocolException(ObjectId, 0, $"Unsupported wl_seat opcode {message.Header.Opcode}.");
        }

        await ValueTask.CompletedTask;
    }
}

internal sealed class WlOutputResource : WaylandResource
{
    public WlOutputResource(WaylandClient client, uint objectId, uint version)
        : base(client, objectId, version, WlOutputProtocol.InterfaceName)
    {
    }

    public override IReadOnlyList<WaylandMessageMetadata> Requests => WlOutputProtocol.Requests;

    public async ValueTask AdvertiseAsync()
    {
        WaylandServer.OutputInfo output = Client.Server.Output;
        await WlOutputEventWriter.GeometryAsync(
            Client,
            ObjectId,
            0,
            0,
            0,
            0,
            WlOutputSubpixel.Unknown,
            output.Make,
            output.Model,
            WlOutputTransform.Normal);
        await WlOutputEventWriter.ModeAsync(
            Client,
            ObjectId,
            WlOutputMode.Current | WlOutputMode.Preferred,
            output.Width,
            output.Height,
            60000);
        if (Version >= 2)
        {
            await WlOutputEventWriter.ScaleAsync(Client, ObjectId, output.Scale);
            await WlOutputEventWriter.DoneAsync(Client, ObjectId);
        }

        if (Version >= 4)
        {
            await WlOutputEventWriter.NameAsync(Client, ObjectId, output.Name);
            await WlOutputEventWriter.DescriptionAsync(Client, ObjectId, output.Description);
            await WlOutputEventWriter.DoneAsync(Client, ObjectId);
        }
    }

    public override ValueTask DispatchAsync(WaylandIncomingMessage message)
    {
        switch (message.Header.Opcode)
        {
            case 0:
                Destroy();
                break;
            default:
                throw new WaylandProtocolException(ObjectId, 0, $"Unsupported wl_output opcode {message.Header.Opcode}.");
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class WlPointerResource : WaylandResource
{
    private uint? _focusedSurfaceId;
    private uint? _cursorSurfaceId;

    public WlPointerResource(WaylandClient client, uint objectId, uint version)
        : base(client, objectId, version, WlPointerProtocol.InterfaceName)
    {
    }

    public override IReadOnlyList<WaylandMessageMetadata> Requests => WlPointerProtocol.Requests;

    public override ValueTask DispatchAsync(WaylandIncomingMessage message)
    {
        switch (message.Header.Opcode)
        {
            case 0:
                return HandleSetCursorAsync(WlPointerProtocol.DecodeSetCursor(message.Body, message.Fds));
            case 1:
                Destroy();
                break;
            default:
                throw new WaylandProtocolException(ObjectId, 0, $"Unsupported wl_pointer opcode {message.Header.Opcode}.");
        }

        return ValueTask.CompletedTask;
    }

    private async ValueTask HandleSetCursorAsync(WlPointerSetCursorRequest request)
    {
        if (_cursorSurfaceId is uint oldCursorSurfaceId &&
            Client.Objects.TryGetValue(oldCursorSurfaceId, out WaylandResource? oldCursorResource) &&
            oldCursorResource is WlSurfaceResource oldCursorSurface)
            await oldCursorSurface.ClearCursorRoleAsync(this);

        _cursorSurfaceId = request.Surface == 0 ? null : request.Surface;

        if (request.Surface == 0)
        {
            if (Client.Server.FramePresenter is IWaylandCursorPresenter cursorPresenter)
                await cursorPresenter.UpdateCursorAsync(0, null);
            return;
        }

        WlSurfaceResource surface = Client.Objects.Require<WlSurfaceResource>(request.Surface);
        await surface.SetCursorRoleAsync(this, request.HotspotX, request.HotspotY);
    }

    public async ValueTask HandleMotionAsync(uint surfaceId, int surfaceX, int surfaceY, uint time)
    {
        if (_focusedSurfaceId != surfaceId)
        {
            if (_focusedSurfaceId is uint oldSurfaceId)
                await WlPointerEventWriter.LeaveAsync(Client, ObjectId, Client.NextSerial(), oldSurfaceId);

            await WlPointerEventWriter.EnterAsync(Client, ObjectId, Client.NextSerial(), surfaceId, surfaceX, surfaceY);
            _focusedSurfaceId = surfaceId;
        }

        await WlPointerEventWriter.MotionAsync(Client, ObjectId, time, surfaceX, surfaceY);
        if (Version >= 5)
            await WlPointerEventWriter.FrameAsync(Client, ObjectId);
    }

    public async ValueTask HandleButtonAsync(uint button, bool pressed, uint time)
    {
        if (_focusedSurfaceId == null)
            return;

        await WlPointerEventWriter.ButtonAsync(
            Client,
            ObjectId,
            Client.NextSerial(),
            time,
            button,
            pressed ? WlPointerButtonState.Pressed : WlPointerButtonState.Released);
        if (Version >= 5)
            await WlPointerEventWriter.FrameAsync(Client, ObjectId);
    }

    public async ValueTask ClearFocusAsync()
    {
        if (_focusedSurfaceId is not uint surfaceId)
            return;

        _focusedSurfaceId = null;
        await WlPointerEventWriter.LeaveAsync(Client, ObjectId, Client.NextSerial(), surfaceId);
        if (Version >= 5)
            await WlPointerEventWriter.FrameAsync(Client, ObjectId);
    }
}

internal sealed class WlKeyboardResource : WaylandResource
{
    private const uint ModShift = 1u << 0;
    private const uint ModLock = 1u << 1;
    private const uint ModControl = 1u << 2;
    private const uint Mod1 = 1u << 3;
    private const uint Mod2 = 1u << 4;
    private const uint Mod4 = 1u << 6;

    private uint? _focusedSurfaceId;
    private readonly HashSet<uint> _pressedKeys = [];
    private uint _modsDepressed;
    private uint _modsLocked;

    public WlKeyboardResource(WaylandClient client, uint objectId, uint version)
        : base(client, objectId, version, WlKeyboardProtocol.InterfaceName)
    {
    }

    public override IReadOnlyList<WaylandMessageMetadata> Requests => WlKeyboardProtocol.Requests;

    public override ValueTask DispatchAsync(WaylandIncomingMessage message)
    {
        switch (message.Header.Opcode)
        {
            case 0:
                Destroy();
                break;
            default:
                throw new WaylandProtocolException(ObjectId, 0, $"Unsupported wl_keyboard opcode {message.Header.Opcode}.");
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask SendInitialStateAsync()
    {
        LinuxFile keymap = Client.Server.KeyboardKeymap.OpenReadOnly();
        await WlKeyboardEventWriter.KeymapAsync(Client, ObjectId, WlKeyboardKeymapFormat.XkbV1, keymap,
            Client.Server.KeyboardKeymap.Size);
        if (Version >= 4)
            await WlKeyboardEventWriter.RepeatInfoAsync(Client, ObjectId, 25, 600);
    }

    public async ValueTask FocusAsync(uint surfaceId)
    {
        if (_focusedSurfaceId == surfaceId)
            return;

        if (_focusedSurfaceId is uint oldSurfaceId)
            await WlKeyboardEventWriter.LeaveAsync(Client, ObjectId, Client.NextSerial(), oldSurfaceId);

        _focusedSurfaceId = surfaceId;
        await WlKeyboardEventWriter.EnterAsync(Client, ObjectId, Client.NextSerial(), surfaceId, EncodePressedKeys());
        await WlKeyboardEventWriter.ModifiersAsync(Client, ObjectId, Client.NextSerial(), _modsDepressed, 0, _modsLocked, 0);
    }

    public async ValueTask HandleKeyAsync(uint key, bool pressed, uint time)
    {
        if (_focusedSurfaceId == null)
            return;

        bool modifiersChanged = UpdateModifierState(key, pressed);
        if (pressed)
            _pressedKeys.Add(key);
        else
            _pressedKeys.Remove(key);

        await WlKeyboardEventWriter.KeyAsync(
            Client,
            ObjectId,
            Client.NextSerial(),
            time,
            key,
            pressed ? WlKeyboardKeyState.Pressed : WlKeyboardKeyState.Released);

        if (modifiersChanged)
            await WlKeyboardEventWriter.ModifiersAsync(Client, ObjectId, Client.NextSerial(), _modsDepressed, 0, _modsLocked, 0);
    }

    public async ValueTask ClearFocusAsync()
    {
        if (_focusedSurfaceId is not uint surfaceId)
            return;

        _focusedSurfaceId = null;
        await WlKeyboardEventWriter.LeaveAsync(Client, ObjectId, Client.NextSerial(), surfaceId);
    }

    private bool UpdateModifierState(uint key, bool pressed)
    {
        if (!WaylandKeyboardLayout.TryGetByEvdevKey(key, out WaylandKeyboardKeyDescriptor descriptor))
            return false;

        uint oldDepressed = _modsDepressed;
        uint oldLocked = _modsLocked;
        uint mask = descriptor.ModifierRole switch
        {
            WaylandKeyboardModifierRole.Shift => ModShift,
            WaylandKeyboardModifierRole.Control => ModControl,
            WaylandKeyboardModifierRole.Alt => Mod1,
            WaylandKeyboardModifierRole.Super => Mod4,
            WaylandKeyboardModifierRole.CapsLock => ModLock,
            WaylandKeyboardModifierRole.NumLock => Mod2,
            _ => 0
        };

        if (mask == 0)
            return false;

        switch (descriptor.ModifierRole)
        {
            case WaylandKeyboardModifierRole.CapsLock:
            case WaylandKeyboardModifierRole.NumLock:
                if (pressed)
                    _modsLocked ^= mask;
                break;
            default:
                if (pressed)
                    _modsDepressed |= mask;
                else
                    _modsDepressed &= ~mask;
                break;
        }

        return oldDepressed != _modsDepressed || oldLocked != _modsLocked;
    }

    private byte[] EncodePressedKeys()
    {
        if (_pressedKeys.Count == 0)
            return [];

        byte[] bytes = new byte[_pressedKeys.Count * sizeof(uint)];
        int offset = 0;
        foreach (uint key in _pressedKeys.OrderBy(static x => x))
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(offset, sizeof(uint)), key);
            offset += sizeof(uint);
        }

        return bytes;
    }
}

internal sealed class XdgWmBaseResource : WaylandResource
{
    public XdgWmBaseResource(WaylandClient client, uint objectId, uint version)
        : base(client, objectId, version, XdgWmBaseProtocol.InterfaceName)
    {
    }

    public uint? LastPingSerial { get; private set; }

    public override IReadOnlyList<WaylandMessageMetadata> Requests => XdgWmBaseProtocol.Requests;

    public override async ValueTask DispatchAsync(WaylandIncomingMessage message)
    {
        switch (message.Header.Opcode)
        {
            case 0:
                Destroy();
                break;
            case 1:
                throw new WaylandProtocolException(ObjectId, 0, "xdg_positioner is not implemented in phase one.");
            case 2:
            {
                var request = XdgWmBaseProtocol.DecodeGetXdgSurface(message.Body, message.Fds);
                var surface = Client.Objects.Require<WlSurfaceResource>(request.Surface);
                var xdgSurface = Client.Register(new XdgSurfaceResource(Client, request.Id, 1, surface));
                surface.AttachXdgSurface(xdgSurface);
                break;
            }
            case 3:
            {
                var request = XdgWmBaseProtocol.DecodePong(message.Body, message.Fds);
                LastPingSerial = request.Serial;
                break;
            }
            default:
                throw new WaylandProtocolException(ObjectId, 0, $"Unsupported xdg_wm_base opcode {message.Header.Opcode}.");
        }
    }

    public async ValueTask PingAsync()
    {
        uint serial = Client.NextSerial();
        await XdgWmBaseEventWriter.PingAsync(Client, ObjectId, serial);
        LastPingSerial = serial;
    }
}

internal sealed class XdgSurfaceResource : WaylandResource
{
    private readonly List<uint> _outstandingConfigureSerials = [];

    public XdgSurfaceResource(WaylandClient client, uint objectId, uint version, WlSurfaceResource surface)
        : base(client, objectId, version, XdgSurfaceProtocol.InterfaceName)
    {
        Surface = surface;
    }

    public WlSurfaceResource Surface { get; }
    public XdgToplevelResource? Toplevel { get; private set; }
    public uint PendingConfigureSerial { get; private set; }
    public uint AckedConfigureSerial { get; private set; }
    public bool IsConfigured => AckedConfigureSerial != 0;

    public override IReadOnlyList<WaylandMessageMetadata> Requests => XdgSurfaceProtocol.Requests;

    public override async ValueTask DispatchAsync(WaylandIncomingMessage message)
    {
        switch (message.Header.Opcode)
        {
            case 0:
                Destroy();
                break;
            case 1:
            {
                var request = XdgSurfaceProtocol.DecodeGetToplevel(message.Body, message.Fds);
                if (Toplevel != null)
                    throw new WaylandProtocolException(ObjectId, 0, "xdg_surface already has a toplevel.");
                Toplevel = Client.Register(new XdgToplevelResource(Client, request.Id, 1, this));
                await Toplevel.SendInitialConfigureAsync();
                break;
            }
            case 2:
                throw new WaylandProtocolException(ObjectId, 0, "xdg_popup is not implemented in phase one.");
            case 3:
            {
                var reader = new WaylandWireReader(message.Body, message.Fds);
                _ = reader.ReadInt();
                _ = reader.ReadInt();
                _ = reader.ReadInt();
                _ = reader.ReadInt();
                reader.EnsureExhausted();
                break;
            }
            case 4:
            {
                var request = XdgSurfaceProtocol.DecodeAckConfigure(message.Body, message.Fds);
                int serialIndex = _outstandingConfigureSerials.IndexOf(request.Serial);
                if (serialIndex < 0)
                    throw new WaylandProtocolException(ObjectId, 0, $"Unknown configure serial {request.Serial}.");

                AckedConfigureSerial = request.Serial;
                _outstandingConfigureSerials.RemoveRange(0, serialIndex + 1);
                PendingConfigureSerial = _outstandingConfigureSerials.Count > 0
                    ? _outstandingConfigureSerials[^1]
                    : 0;
                break;
            }
            default:
                throw new WaylandProtocolException(ObjectId, 0, $"Unsupported xdg_surface opcode {message.Header.Opcode}.");
        }
    }

    public async ValueTask SendConfigureAsync()
    {
        PendingConfigureSerial = Client.NextSerial();
        _outstandingConfigureSerials.Add(PendingConfigureSerial);
        await XdgSurfaceEventWriter.ConfigureAsync(Client, ObjectId, PendingConfigureSerial);
    }
}

internal sealed class XdgToplevelResource : WaylandResource
{
    private WaylandSurfaceBounds? _restoreBounds;

    public XdgToplevelResource(WaylandClient client, uint objectId, uint version, XdgSurfaceResource surface)
        : base(client, objectId, version, XdgToplevelProtocol.InterfaceName)
    {
        Surface = surface;
    }

    public XdgSurfaceResource Surface { get; }
    public string Title { get; private set; } = string.Empty;
    public string AppId { get; private set; } = string.Empty;
    public bool Activated { get; private set; }
    public bool IsMaximized { get; private set; }
    public bool IsMinimized { get; private set; }
    public WaylandDecorationMetrics DecorationMetrics { get; } = WaylandDecorationMetrics.Default;
    public ZxdgToplevelDecorationV1Mode DecorationMode { get; private set; } = ZxdgToplevelDecorationV1Mode.ServerSide;
    public ZxdgToplevelDecorationV1Resource? DecorationResource { get; private set; }
    public bool IsServerSideDecorated => DecorationResource != null && DecorationMode == ZxdgToplevelDecorationV1Mode.ServerSide;

    public override IReadOnlyList<WaylandMessageMetadata> Requests => XdgToplevelProtocol.Requests;

    public override async ValueTask DispatchAsync(WaylandIncomingMessage message)
    {
        switch (message.Header.Opcode)
        {
            case 0:
                Destroy();
                break;
            case 1:
                _ = XdgToplevelProtocol.DecodeSetParent(message.Body, message.Fds);
                break;
            case 2:
                Title = XdgToplevelProtocol.DecodeSetTitle(message.Body, message.Fds).Title;
                SyncSceneDecoration();
                break;
            case 3:
                AppId = XdgToplevelProtocol.DecodeSetAppId(message.Body, message.Fds).AppId;
                break;
            case 4:
            {
                var request = XdgToplevelProtocol.DecodeShowWindowMenu(message.Body, message.Fds);
                Client.Objects.Require<WlSeatResource>(request.Seat);
                break;
            }
            case 5:
            {
                var request = XdgToplevelProtocol.DecodeMove(message.Body, message.Fds);
                Client.Objects.Require<WlSeatResource>(request.Seat);
                await Client.Server.Focus.BeginInteractiveMoveAsync(this);
                break;
            }
            case 6:
            {
                var request = XdgToplevelProtocol.DecodeResize(message.Body, message.Fds);
                Client.Objects.Require<WlSeatResource>(request.Seat);
                await Client.Server.Focus.BeginInteractiveResizeAsync(this, request.Edges);
                break;
            }
            case 7:
                _ = XdgToplevelProtocol.DecodeSetMaxSize(message.Body, message.Fds);
                break;
            case 8:
                _ = XdgToplevelProtocol.DecodeSetMinSize(message.Body, message.Fds);
                break;
            case 9:
                await SetMaximizedAsync(true);
                break;
            case 10:
                await SetMaximizedAsync(false);
                break;
            case 11:
                _ = XdgToplevelProtocol.DecodeSetFullscreen(message.Body, message.Fds);
                break;
            case 12:
                break;
            case 13:
                await SetMinimizedAsync(true);
                break;
            default:
                throw new WaylandProtocolException(ObjectId, 0, $"Unsupported xdg_toplevel opcode {message.Header.Opcode}.");
        }
    }

    public async ValueTask SendInitialConfigureAsync()
    {
        await SendConfigureAsync(0, 0);
    }

    public async ValueTask SetActivatedAsync(bool activated)
    {
        if (Activated == activated)
            return;

        Activated = activated;
        SyncSceneDecoration();
        await SendConfigureAsync(0, 0);
    }

    public async ValueTask SendConfigureAsync(int width, int height, bool resizing = false)
    {
        if (DecorationResource != null)
            await DecorationResource.SendConfigureAsync(DecorationMode);
        await XdgToplevelEventWriter.ConfigureAsync(Client, ObjectId, width, height, BuildStates(resizing));
        await Surface.SendConfigureAsync();
    }

    public void AttachDecoration(ZxdgToplevelDecorationV1Resource decoration)
    {
        if (DecorationResource != null)
        {
            throw new WaylandProtocolException(ObjectId,
                (uint)ZxdgToplevelDecorationV1Error.AlreadyConstructed,
                "xdg_toplevel already has a decoration object.");
        }

        DecorationResource = decoration;
        DecorationMode = ZxdgToplevelDecorationV1Mode.ServerSide;
        SyncSceneDecoration();
    }

    public void DetachDecoration(ZxdgToplevelDecorationV1Resource decoration)
    {
        if (!ReferenceEquals(DecorationResource, decoration))
            return;

        DecorationResource = null;
        SyncSceneDecoration();
    }

    public async ValueTask SetDecorationModeAsync(ZxdgToplevelDecorationV1Mode? requestedMode)
    {
        DecorationMode = requestedMode ?? ZxdgToplevelDecorationV1Mode.ServerSide;
        SyncSceneDecoration();
        await SendConfigureAsync(0, 0);
    }

    public async ValueTask SendCloseAsync()
    {
        await XdgToplevelEventWriter.CloseAsync(Client, ObjectId);
    }

    public async ValueTask SetMaximizedAsync(bool maximized)
    {
        if (IsMaximized == maximized)
            return;

        if (Client.Server.FramePresenter is IWaylandDesktopSceneController sceneController &&
            Client.Server.FramePresenter is IWaylandSceneView sceneView)
        {
            if (maximized)
            {
                if (sceneView.TryGetSurfaceBounds(Surface.Surface.SceneSurfaceId, out WaylandSurfaceBounds currentBounds))
                    _restoreBounds ??= currentBounds;

                WaylandSurfaceBounds desktopWindowBounds = new(0, 0, Client.Server.Output.Width, Client.Server.Output.Height);
                WaylandDecorationSceneState decoration = BuildDecorationSceneState() with { Maximized = true };
                sceneController.SetWindowBounds(Surface.Surface.SceneSurfaceId, desktopWindowBounds);
                sceneController.SetSurfaceDecoration(Surface.Surface.SceneSurfaceId, decoration);
            }
            else
            {
                if (_restoreBounds is { } restoreBounds)
                    sceneController.SetSurfaceBounds(Surface.Surface.SceneSurfaceId, restoreBounds);

                sceneController.SetSurfaceDecoration(Surface.Surface.SceneSurfaceId, BuildDecorationSceneState() with { Maximized = false });
            }
        }

        IsMaximized = maximized;
        IsMinimized = false;
        SyncSceneDecoration();

        if (Client.Server.FramePresenter is IWaylandSceneView updatedScene &&
            updatedScene.TryGetSurfaceBounds(Surface.Surface.SceneSurfaceId, out WaylandSurfaceBounds contentBounds))
        {
            await SendConfigureAsync(contentBounds.Width, contentBounds.Height);
        }
        else
        {
            await SendConfigureAsync(0, 0);
        }
    }

    public async ValueTask SetMinimizedAsync(bool minimized)
    {
        if (IsMinimized == minimized)
            return;

        IsMinimized = minimized;
        if (minimized)
            Activated = false;

        SyncSceneDecoration();
        if (!minimized)
            await SendConfigureAsync(0, 0);
    }

    private byte[] BuildStates(bool resizing)
    {
        var states = new List<byte>(8);

        if (Activated)
            states.AddRange(BitConverter.GetBytes((uint)XdgToplevelState.Activated));
        if (IsMaximized)
            states.AddRange(BitConverter.GetBytes((uint)XdgToplevelState.Maximized));
        if (resizing)
            states.AddRange(BitConverter.GetBytes((uint)XdgToplevelState.Resizing));

        return [.. states];
    }

    private WaylandDecorationSceneState BuildDecorationSceneState()
    {
        return new WaylandDecorationSceneState(
            IsServerSideDecorated,
            Activated,
            IsMaximized,
            IsMinimized,
            string.IsNullOrWhiteSpace(Title) ? AppId : Title,
            DecorationMetrics);
    }

    private void SyncSceneDecoration()
    {
        if (Client.Server.FramePresenter is not IWaylandDesktopSceneController sceneController)
            return;

        sceneController.SetSurfaceDecoration(Surface.Surface.SceneSurfaceId, BuildDecorationSceneState());
        sceneController.SetSurfaceHidden(Surface.Surface.SceneSurfaceId, IsMinimized);
    }
}

internal sealed class ZxdgDecorationManagerV1Resource : WaylandResource
{
    public ZxdgDecorationManagerV1Resource(WaylandClient client, uint objectId, uint version)
        : base(client, objectId, version, ZxdgDecorationManagerV1Protocol.InterfaceName)
    {
    }

    public override IReadOnlyList<WaylandMessageMetadata> Requests => ZxdgDecorationManagerV1Protocol.Requests;

    public override async ValueTask DispatchAsync(WaylandIncomingMessage message)
    {
        switch (message.Header.Opcode)
        {
            case 0:
                Destroy();
                break;
            case 1:
            {
                var request = ZxdgDecorationManagerV1Protocol.DecodeGetToplevelDecoration(message.Body, message.Fds);
                XdgToplevelResource toplevel = Client.Objects.Require<XdgToplevelResource>(request.Toplevel);
                var decoration = Client.Register(new ZxdgToplevelDecorationV1Resource(Client, request.Id, 1, toplevel));
                toplevel.AttachDecoration(decoration);
                await decoration.SendConfigureAsync(toplevel.DecorationMode);
                await toplevel.SendConfigureAsync(0, 0);
                break;
            }
            default:
                throw new WaylandProtocolException(ObjectId, 0,
                    $"Unsupported zxdg_decoration_manager_v1 opcode {message.Header.Opcode}.");
        }
    }
}

internal sealed class ZxdgToplevelDecorationV1Resource : WaylandResource
{
    public ZxdgToplevelDecorationV1Resource(WaylandClient client, uint objectId, uint version, XdgToplevelResource toplevel)
        : base(client, objectId, version, ZxdgToplevelDecorationV1Protocol.InterfaceName)
    {
        Toplevel = toplevel;
    }

    public XdgToplevelResource Toplevel { get; }
    public override IReadOnlyList<WaylandMessageMetadata> Requests => ZxdgToplevelDecorationV1Protocol.Requests;

    public override async ValueTask DispatchAsync(WaylandIncomingMessage message)
    {
        switch (message.Header.Opcode)
        {
            case 0:
                Destroy();
                break;
            case 1:
            {
                ZxdgToplevelDecorationV1SetModeRequest request = ZxdgToplevelDecorationV1Protocol.DecodeSetMode(message.Body, message.Fds);
                await Toplevel.SetDecorationModeAsync(request.Mode);
                break;
            }
            case 2:
                await Toplevel.SetDecorationModeAsync(null);
                break;
            default:
                throw new WaylandProtocolException(ObjectId, 0,
                    $"Unsupported zxdg_toplevel_decoration_v1 opcode {message.Header.Opcode}.");
        }
    }

    public async ValueTask SendConfigureAsync(ZxdgToplevelDecorationV1Mode mode)
    {
        await ZxdgToplevelDecorationV1EventWriter.ConfigureAsync(Client, ObjectId, mode);
    }

    public override void Destroy()
    {
        if (Destroyed)
            return;

        Toplevel.DetachDecoration(this);
        base.Destroy();
    }
}
