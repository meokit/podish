namespace Podish.Wayland;

internal readonly record struct WaylandPopupRules(
    int Width,
    int Height,
    WaylandSurfaceBounds AnchorRect,
    XdgPositionerAnchor Anchor,
    XdgPositionerGravity Gravity,
    XdgPositionerConstraintAdjustment Adjustment,
    int OffsetX,
    int OffsetY,
    bool Reactive)
{
    public WaylandSurfaceBounds Place(WaylandSurfaceBounds parent, WaylandSurfaceBounds available)
    {
        (int ax, int ay) = Direction(Anchor);
        (int gx, int gy) = Direction((XdgPositionerAnchor)Gravity);
        (int x, int w) = PlaceAxis(Width, AnchorRect.X, AnchorRect.Width, ax, gx, OffsetX,
            (long)available.X - parent.X, available.Width, Adjustment);
        (int y, int h) = PlaceAxis(Height, AnchorRect.Y, AnchorRect.Height, ay, gy, OffsetY,
            (long)available.Y - parent.Y, available.Height, (XdgPositionerConstraintAdjustment)((uint)Adjustment >> 1));
        return new WaylandSurfaceBounds(x, y, w, h);
    }

    private static (int X, int Y) Direction(XdgPositionerAnchor anchor) => anchor switch
    {
        XdgPositionerAnchor.Top => (0, -1),
        XdgPositionerAnchor.Bottom => (0, 1),
        XdgPositionerAnchor.Left => (-1, 0),
        XdgPositionerAnchor.Right => (1, 0),
        XdgPositionerAnchor.TopLeft => (-1, -1),
        XdgPositionerAnchor.TopRight => (1, -1),
        XdgPositionerAnchor.BottomLeft => (-1, 1),
        XdgPositionerAnchor.BottomRight => (1, 1),
        _ => (0, 0)
    };

    private static (int Position, int Size) PlaceAxis(int size, int anchorStart, int anchorSize,
        int anchor, int gravity, int offset, long lower, int available,
        XdgPositionerConstraintAdjustment adjustment)
    {
        long Position(int direction) => (long)anchorStart + ((anchor * direction + 1L) * anchorSize / 2) -
            ((1L - gravity * direction) * size / 2) + offset;

        long position = Position(1);
        long upper = lower + available;
        if (position < lower || position + size > upper)
        {
            if ((adjustment & XdgPositionerConstraintAdjustment.FlipX) != 0)
            {
                long flipped = Position(-1);
                if (flipped >= lower && flipped + size <= upper)
                    position = flipped;
            }

            if ((adjustment & XdgPositionerConstraintAdjustment.SlideX) != 0)
                position = Math.Clamp(position, Math.Min(lower, upper - size), Math.Max(lower, upper - size));

            if ((adjustment & XdgPositionerConstraintAdjustment.ResizeX) != 0)
            {
                long end = Math.Clamp(position + size, lower + 1, upper);
                position = Math.Clamp(position, lower, end - 1);
                size = (int)(end - position);
            }
        }

        return ((int)Math.Clamp(position, int.MinValue, int.MaxValue), size);
    }
}

internal sealed class XdgPositionerResource : WaylandResource
{
    private WaylandPopupRules _rules;

    public XdgPositionerResource(WaylandClient client, uint objectId, uint version)
        : base(client, objectId, version, XdgPositionerProtocol.InterfaceName)
    {
    }

    public override IReadOnlyList<WaylandMessageMetadata> Requests => XdgPositionerProtocol.Requests;

    public WaylandPopupRules GetRules()
    {
        if (_rules.Width <= 0 || _rules.Height <= 0 || _rules.AnchorRect.Width <= 0 || _rules.AnchorRect.Height <= 0)
            throw InvalidInput("Positioner size and anchor rectangle are required.");
        return _rules;
    }

    public override ValueTask DispatchAsync(WaylandIncomingMessage message)
    {
        switch (message.Header.Opcode)
        {
            case 0:
                Destroy();
                return Client.DeleteIdAsync(ObjectId);
            case 1:
            {
                var request = XdgPositionerProtocol.DecodeSetSize(message.Body, message.Fds);
                if (request.Width <= 0 || request.Height <= 0)
                    throw InvalidInput("Positioner size must be positive.");
                _rules = _rules with { Width = request.Width, Height = request.Height };
                break;
            }
            case 2:
            {
                var request = XdgPositionerProtocol.DecodeSetAnchorRect(message.Body, message.Fds);
                if (request.Width <= 0 || request.Height <= 0)
                    throw InvalidInput("Positioner anchor rectangle must have a positive size.");
                _rules = _rules with { AnchorRect = new(request.X, request.Y, request.Width, request.Height) };
                break;
            }
            case 3:
            {
                var request = XdgPositionerProtocol.DecodeSetAnchor(message.Body, message.Fds);
                if (!Enum.IsDefined(request.Anchor))
                    throw InvalidInput("Unknown positioner anchor.");
                _rules = _rules with { Anchor = request.Anchor };
                break;
            }
            case 4:
            {
                var request = XdgPositionerProtocol.DecodeSetGravity(message.Body, message.Fds);
                if (!Enum.IsDefined(request.Gravity))
                    throw InvalidInput("Unknown positioner gravity.");
                _rules = _rules with { Gravity = request.Gravity };
                break;
            }
            case 5:
            {
                var request = XdgPositionerProtocol.DecodeSetConstraintAdjustment(message.Body, message.Fds);
                _rules = _rules with { Adjustment = request.ConstraintAdjustment };
                break;
            }
            case 6:
            {
                var request = XdgPositionerProtocol.DecodeSetOffset(message.Body, message.Fds);
                _rules = _rules with { OffsetX = request.X, OffsetY = request.Y };
                break;
            }
            case 7:
                _rules = _rules with { Reactive = true };
                break;
            case 8:
                _ = XdgPositionerProtocol.DecodeSetParentSize(message.Body, message.Fds);
                break;
            case 9:
                _ = XdgPositionerProtocol.DecodeSetParentConfigure(message.Body, message.Fds);
                break;
        }
        return ValueTask.CompletedTask;
    }

    private WaylandProtocolException InvalidInput(string reason) =>
        new(ObjectId, (uint)XdgPositionerError.InvalidInput, reason);
}

internal sealed class XdgPopupResource : WaylandResource
{
    private readonly Dictionary<uint, WaylandSurfaceBounds> _configures = [];
    private WaylandPopupRules _rules;
    private WaylandSurfaceBounds? _pendingPlacement;
    private WaylandSurfaceBounds? _placement;
    private WaylandSurfaceBounds? _lastConfigured;
    private bool _dismissed;
    private bool _hasCommitted;

    public XdgPopupResource(WaylandClient client, uint id, uint version, XdgSurfaceResource xdgSurface,
        WlSurfaceResource? parent, WaylandPopupRules rules)
        : base(client, id, version, XdgPopupProtocol.InterfaceName)
    {
        XdgSurface = xdgSurface;
        Parent = parent;
        _rules = rules;
    }

    public XdgSurfaceResource XdgSurface { get; }
    public WlSurfaceResource? Parent { get; private set; }
    public bool CanPresent => !_dismissed && !Destroyed && _placement != null;
    public bool IsGrabbing { get; private set; }
    public override IReadOnlyList<WaylandMessageMetadata> Requests => XdgPopupProtocol.Requests;

    public void AttachLayerParent(WlSurfaceResource parent)
    {
        if (Parent != null || _hasCommitted)
            throw new WaylandProtocolException(ObjectId, 0, "The popup already has a parent or has been committed.");
        Parent = parent;
    }

    public override async ValueTask DispatchAsync(WaylandIncomingMessage message)
    {
        switch (message.Header.Opcode)
        {
            case 0:
                if (Client.Server.GetChildPopups(XdgSurface.Surface).Any(static popup => !popup.Destroyed))
                    throw new WaylandProtocolException(ObjectId, (uint)XdgWmBaseError.NotTheTopmostPopup,
                        "A child popup must be destroyed before its parent.");
                Destroy();
                await Client.DeleteIdAsync(ObjectId);
                break;
            case 1:
            {
                var request = XdgPopupProtocol.DecodeGrab(message.Body, message.Fds);
                Client.Objects.Require<WlSeatResource>(request.Seat);
                if (XdgSurface.Surface.CurrentBuffer != null ||
                    Parent?.XdgSurface?.Popup is { IsGrabbing: false })
                    throw new WaylandProtocolException(ObjectId, (uint)XdgPopupError.InvalidGrab, "Invalid popup grab.");
                IsGrabbing = await Client.Server.Focus.TryGrabPopupAsync(this, request.Serial);
                if (!IsGrabbing)
                    await DismissAsync();
                break;
            }
            case 2:
            {
                var request = XdgPopupProtocol.DecodeReposition(message.Body, message.Fds);
                _rules = Client.Objects.Require<XdgPositionerResource>(request.Positioner).GetRules();
                if (!_dismissed)
                    await ConfigureAsync(request.Token);
                break;
            }
        }
    }

    public void AcknowledgeConfigure(IEnumerable<uint> serials)
    {
        foreach (uint serial in serials)
        {
            if (_configures.Remove(serial, out WaylandSurfaceBounds placement))
                _pendingPlacement = placement;
        }
    }

    public async ValueTask BeforeSurfaceCommitAsync()
    {
        if (_dismissed || Destroyed)
            return;
        if (!_hasCommitted)
        {
            if (Parent?.CurrentBuffer == null || Parent.Destroyed)
                throw new WaylandProtocolException(ObjectId, (uint)XdgWmBaseError.InvalidPopupParent,
                    "A popup requires a mapped parent before its initial commit.");
            _hasCommitted = true;
            await ConfigureAsync();
        }

        if (_pendingPlacement is { } placement)
        {
            _placement = placement;
            _pendingPlacement = null;
        }
        UpdatePlacement();
    }

    public async ValueTask AfterSurfaceCommitAsync(bool wasMapped)
    {
        if (CanPresent && XdgSurface.Surface.CurrentBuffer != null)
        {
            UpdatePlacement();
            if (!wasMapped && IsGrabbing)
                await Client.Server.Focus.FocusKeyboardSurfaceAsync(XdgSurface.Surface.SceneSurfaceId);
        }
        else if (wasMapped)
        {
            await DismissAsync();
        }
    }

    public void UpdatePlacement()
    {
        WaylandServer server = Client.Server;
        if (!CanPresent || _placement is not { } placement || Parent == null ||
            server.FramePresenter is not IWaylandDesktopSceneController scene)
            return;

        WaylandSurfaceBounds parent = GetParentGeometry();
        WaylandSurfaceBounds ownGeometry = XdgSurface.WindowGeometry ?? default;
        var bounds = placement with
        {
            X = SaturatingAdd((long)parent.X + placement.X - ownGeometry.X),
            Y = SaturatingAdd((long)parent.Y + placement.Y - ownGeometry.Y)
        };
        if (XdgSurface.Surface.CurrentBuffer != null)
            bounds = bounds with { Width = XdgSurface.Surface.LogicalWidth, Height = XdgSurface.Surface.LogicalHeight };
        scene.SetSurfaceBounds(XdgSurface.Surface.SceneSurfaceId, bounds);
        scene.SetSurfaceLayer(XdgSurface.Surface.SceneSurfaceId, Parent.SceneLayer);
        scene.RaiseSurface(XdgSurface.Surface.SceneSurfaceId);
        XdgSurface.Surface.UpdateChildSubsurfacePlacements();
    }

    public async ValueTask ReconfigureReactiveAsync()
    {
        if (_rules.Reactive && _hasCommitted && !_dismissed && !Destroyed)
            await ConfigureAsync();
    }

    public async ValueTask DismissAsync()
    {
        if (_dismissed)
            return;
        _dismissed = true;
        await Client.Server.DismissChildPopupsAsync(XdgSurface.Surface);
        await UnmapAsync();
        if (!Destroyed)
            await XdgPopupEventWriter.PopupDoneAsync(Client, ObjectId);
    }

    public override void Destroy()
    {
        if (Destroyed)
            return;
        base.Destroy();
        UnmapAsync().AsTask().GetAwaiter().GetResult();
    }

    private async ValueTask UnmapAsync()
    {
        WaylandServer server = Client.Server;
        await server.Focus.ReleasePopupGrabAsync(this);
        if (server.FramePresenter != null)
            await server.FramePresenter.PresentSurfaceAsync(XdgSurface.Surface.SceneSurfaceId, null);
        await server.Focus.HandleSurfaceDestroyedAsync(XdgSurface.Surface.SceneSurfaceId);
    }

    private WaylandSurfaceBounds GetParentGeometry()
    {
        WaylandSurfaceBounds surface = default;
        if (Parent != null && Client.Server.FramePresenter is IWaylandSceneView view)
            view.TryGetSurfaceBounds(Parent.SceneSurfaceId, out surface);
        if (Parent?.XdgSurface?.WindowGeometry is not { } geometry)
            return surface;
        return geometry with
        {
            X = SaturatingAdd((long)surface.X + geometry.X),
            Y = SaturatingAdd((long)surface.Y + geometry.Y)
        };
    }

    private async ValueTask ConfigureAsync(uint? token = null)
    {
        WaylandSurfaceBounds available = Parent?.LayerSurface != null || Parent?.SceneLayer != WaylandSceneLayer.Window
            ? Client.Server.OutputBounds
            : Client.Server.WorkArea;
        WaylandSurfaceBounds placement = _rules.Place(GetParentGeometry(), available);
        if (token == null && _lastConfigured == placement)
            return;
        _lastConfigured = placement;
        if (token is uint value)
            await XdgPopupEventWriter.RepositionedAsync(Client, ObjectId, value);
        await XdgPopupEventWriter.ConfigureAsync(Client, ObjectId, placement.X, placement.Y, placement.Width, placement.Height);
        _configures.Add(await XdgSurface.SendConfigureAsync(), placement);
    }

    private static int SaturatingAdd(long value) => (int)Math.Clamp(value, int.MinValue, int.MaxValue);
}
