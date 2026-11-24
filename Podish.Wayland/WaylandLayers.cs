using static Podish.Wayland.ZwlrLayerSurfaceV1Anchor;

namespace Podish.Wayland;

internal sealed class LayerShellResource : WaylandResource
{
    public LayerShellResource(WaylandClient client, uint id, uint version)
        : base(client, id, version, ZwlrLayerShellV1Protocol.InterfaceName)
    {
    }

    public override IReadOnlyList<WaylandMessageMetadata> Requests => ZwlrLayerShellV1Protocol.Requests;

    public override ValueTask DispatchAsync(WaylandIncomingMessage message)
    {
        if (message.Header.Opcode == 1)
        {
            Destroy();
            return Client.DeleteIdAsync(ObjectId);
        }

        var request = ZwlrLayerShellV1Protocol.DecodeGetLayerSurface(message.Body, message.Fds);
        if (request.Output != 0)
            Client.Objects.Require<WlOutputResource>(request.Output);
        if (!Enum.IsDefined(request.Layer))
            throw new WaylandProtocolException(ObjectId, (uint)ZwlrLayerShellV1Error.InvalidLayer, "Unknown layer.");
        var surface = Client.Objects.Require<WlSurfaceResource>(request.Surface);
        var layer = new LayerSurfaceResource(Client, request.Id, Version, surface, request.Layer);
        surface.AttachLayerSurface(this, layer);
        Client.Register(layer);
        return ValueTask.CompletedTask;
    }
}

internal sealed class LayerSurfaceResource : WaylandResource
{
    private readonly record struct State(ZwlrLayerShellV1Layer Layer, int Width = 0, int Height = 0,
        ZwlrLayerSurfaceV1Anchor Anchor = 0, int Zone = 0, int TopMargin = 0, int RightMargin = 0,
        int BottomMargin = 0, int LeftMargin = 0, ZwlrLayerSurfaceV1KeyboardInteractivity Keyboard = 0);

    private const ZwlrLayerSurfaceV1Anchor Vertical = Top | Bottom;
    private const ZwlrLayerSurfaceV1Anchor Horizontal = Left | Right;
    private readonly List<uint> _configureSerials = [];
    private readonly ZwlrLayerShellV1Layer _initialLayer;
    private State _pending;
    private State _current;
    private (int Width, int Height)? _configuredSize;
    private bool _configured;

    public LayerSurfaceResource(WaylandClient client, uint id, uint version, WlSurfaceResource surface,
        ZwlrLayerShellV1Layer layer)
        : base(client, id, version, ZwlrLayerSurfaceV1Protocol.InterfaceName)
    {
        Surface = surface;
        _initialLayer = layer;
        _pending = _current = new State(layer);
    }

    public WlSurfaceResource Surface { get; }
    public bool HasCommitted { get; private set; }
    public bool IsMapped => !Destroyed && Surface.CurrentBuffer != null;
    public int ExclusiveZone => _current.Zone;
    public ZwlrLayerSurfaceV1KeyboardInteractivity KeyboardInteractivity => _current.Keyboard;
    public WaylandSceneLayer SceneLayer => _current.Layer switch
    {
        ZwlrLayerShellV1Layer.Background => WaylandSceneLayer.Background,
        ZwlrLayerShellV1Layer.Bottom => WaylandSceneLayer.Bottom,
        ZwlrLayerShellV1Layer.Top => WaylandSceneLayer.Top,
        _ => WaylandSceneLayer.Overlay
    };

    public override IReadOnlyList<WaylandMessageMetadata> Requests => ZwlrLayerSurfaceV1Protocol.Requests;

    public override ValueTask DispatchAsync(WaylandIncomingMessage message)
    {
        switch (message.Header.Opcode)
        {
            case 0:
            {
                var request = ZwlrLayerSurfaceV1Protocol.DecodeSetSize(message.Body, message.Fds);
                if (request.Width > int.MaxValue || request.Height > int.MaxValue)
                    throw Error(ZwlrLayerSurfaceV1Error.InvalidSize, "The requested layer size exceeds the supported range.");
                _pending = _pending with { Width = (int)request.Width, Height = (int)request.Height };
                break;
            }
            case 1:
            {
                var request = ZwlrLayerSurfaceV1Protocol.DecodeSetAnchor(message.Body, message.Fds);
                if ((request.Anchor & ~(Vertical | Horizontal)) != 0)
                    throw Error(ZwlrLayerSurfaceV1Error.InvalidAnchor, "Unknown layer anchor.");
                _pending = _pending with { Anchor = request.Anchor };
                break;
            }
            case 2:
                _pending = _pending with { Zone = ZwlrLayerSurfaceV1Protocol.DecodeSetExclusiveZone(message.Body, message.Fds).Zone };
                break;
            case 3:
            {
                var request = ZwlrLayerSurfaceV1Protocol.DecodeSetMargin(message.Body, message.Fds);
                _pending = _pending with
                {
                    TopMargin = request.Top,
                    RightMargin = request.Right,
                    BottomMargin = request.Bottom,
                    LeftMargin = request.Left
                };
                break;
            }
            case 4:
            {
                var request = ZwlrLayerSurfaceV1Protocol.DecodeSetKeyboardInteractivity(message.Body, message.Fds);
                if (!Enum.IsDefined(request.KeyboardInteractivity) ||
                    (Version < 4 && request.KeyboardInteractivity == ZwlrLayerSurfaceV1KeyboardInteractivity.OnDemand))
                    throw Error(ZwlrLayerSurfaceV1Error.InvalidKeyboardInteractivity, "Unknown layer keyboard mode.");
                _pending = _pending with { Keyboard = request.KeyboardInteractivity };
                break;
            }
            case 5:
            {
                var request = ZwlrLayerSurfaceV1Protocol.DecodeGetPopup(message.Body, message.Fds);
                Client.Objects.Require<XdgPopupResource>(request.Popup).AttachLayerParent(Surface);
                break;
            }
            case 6:
            {
                var request = ZwlrLayerSurfaceV1Protocol.DecodeAckConfigure(message.Body, message.Fds);
                int index = _configureSerials.IndexOf(request.Serial);
                if (index < 0)
                    throw Error(ZwlrLayerSurfaceV1Error.InvalidSurfaceState, "Unknown layer configure serial.");
                _configureSerials.RemoveRange(0, index + 1);
                _configured = true;
                break;
            }
            case 7:
                Destroy();
                return Client.DeleteIdAsync(ObjectId);
            case 8:
            {
                var request = ZwlrLayerSurfaceV1Protocol.DecodeSetLayer(message.Body, message.Fds);
                if (!Enum.IsDefined(request.Layer))
                    throw Error(ZwlrLayerSurfaceV1Error.InvalidSurfaceState, "Unknown layer.");
                _pending = _pending with { Layer = request.Layer };
                break;
            }
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask BeforeSurfaceCommitAsync(bool hasBuffer)
    {
        if (Destroyed)
            return ValueTask.CompletedTask;
        if (hasBuffer && !_configured)
            throw Error(ZwlrLayerSurfaceV1Error.InvalidSurfaceState, "A layer surface must acknowledge its configure before mapping.");
        if ((_pending.Width == 0 && (_pending.Anchor & Horizontal) != Horizontal) ||
            (_pending.Height == 0 && (_pending.Anchor & Vertical) != Vertical))
            throw Error(ZwlrLayerSurfaceV1Error.InvalidSize, "An automatic size requires opposite layer anchors.");
        _current = _pending;
        HasCommitted = true;
        return ValueTask.CompletedTask;
    }

    public async ValueTask AfterSurfaceCommitAsync(bool wasMapped)
    {
        if (wasMapped && !IsMapped)
        {
            _pending = _current = new State(_initialLayer);
            _configuredSize = null;
            _configured = false;
            HasCommitted = false;
            _configureSerials.Clear();
            await Client.Server.Focus.HandleSurfaceDestroyedAsync(Surface.SceneSurfaceId);
        }
        await Client.Server.ArrangeLayersAsync();
    }

    public WaylandSurfaceBounds ReserveSpace(WaylandSurfaceBounds area)
    {
        if (_current.Zone <= 0)
            return area;
        int horizontalZone = Math.Clamp(_current.Zone, 0, area.Width - 1);
        int verticalZone = Math.Clamp(_current.Zone, 0, area.Height - 1);
        return _current.Anchor switch
        {
            Top or (Top | Horizontal) => area with { Y = area.Y + verticalZone, Height = area.Height - verticalZone },
            Bottom or (Bottom | Horizontal) => area with { Height = area.Height - verticalZone },
            Left or (Left | Vertical) => area with { X = area.X + horizontalZone, Width = area.Width - horizontalZone },
            Right or (Right | Vertical) => area with { Width = area.Width - horizontalZone },
            _ => area
        };
    }

    public async ValueTask ArrangeAsync(WaylandSurfaceBounds available)
    {
        int left = (_current.Anchor & Left) != 0 ? _current.LeftMargin : 0;
        int right = (_current.Anchor & Right) != 0 ? _current.RightMargin : 0;
        int top = (_current.Anchor & Top) != 0 ? _current.TopMargin : 0;
        int bottom = (_current.Anchor & Bottom) != 0 ? _current.BottomMargin : 0;
        int width = _current.Width > 0 ? _current.Width : Saturate((long)available.Width - left - right, 1);
        int height = _current.Height > 0 ? _current.Height : Saturate((long)available.Height - top - bottom, 1);

        if (_configuredSize != (width, height))
        {
            _configuredSize = (width, height);
            uint serial = Client.NextSerial();
            _configureSerials.Add(serial);
            await ZwlrLayerSurfaceV1EventWriter.ConfigureAsync(Client, ObjectId, serial, (uint)width, (uint)height);
        }

        if (Surface.CurrentBuffer == null || Client.Server.FramePresenter is not IWaylandDesktopSceneController scene)
            return;
        int surfaceWidth = Surface.LogicalWidth;
        int surfaceHeight = Surface.LogicalHeight;
        int x = PlaceAxis(available.X, available.Width, surfaceWidth, left, right, _current.Anchor & Horizontal, Left, Right);
        int y = PlaceAxis(available.Y, available.Height, surfaceHeight, top, bottom, _current.Anchor & Vertical, Top, Bottom);
        scene.SetSurfaceLayer(Surface.SceneSurfaceId, SceneLayer);
        scene.SetSurfaceBounds(Surface.SceneSurfaceId, new WaylandSurfaceBounds(x, y, surfaceWidth, surfaceHeight));
        Surface.UpdateChildSubsurfacePlacements();
    }

    public override void Destroy()
    {
        if (Destroyed)
            return;
        base.Destroy();
        RemoveFromSceneAsync().AsTask().GetAwaiter().GetResult();
    }

    private async ValueTask RemoveFromSceneAsync()
    {
        await Client.Server.DismissChildPopupsAsync(Surface);
        if (Client.Server.FramePresenter != null)
            await Client.Server.FramePresenter.PresentSurfaceAsync(Surface.SceneSurfaceId, null);
        await Client.Server.Focus.HandleSurfaceDestroyedAsync(Surface.SceneSurfaceId);
        await Client.Server.ArrangeLayersAsync();
    }

    private WaylandProtocolException Error(ZwlrLayerSurfaceV1Error reason, string message) =>
        new(ObjectId, (uint)reason, message);

    private static int PlaceAxis(int start, int available, int size, int lowMargin, int highMargin,
        ZwlrLayerSurfaceV1Anchor anchors, ZwlrLayerSurfaceV1Anchor low, ZwlrLayerSurfaceV1Anchor high)
    {
        long alignedStart = (long)start + lowMargin;
        long alignedEnd = (long)start + available - highMargin;
        long position = anchors == low ? alignedStart : anchors == high ? alignedEnd - size :
            alignedStart + (alignedEnd - alignedStart - size) / 2;
        return Saturate(position);
    }

    private static int Saturate(long value, int minimum = int.MinValue) => (int)Math.Clamp(value, minimum, int.MaxValue);
}
