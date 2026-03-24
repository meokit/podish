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
                SyncRequest request = WlDisplayProtocol.DecodeSync(message.Body, message.Fds);
                var callback = Client.Register(new WlCallbackResource(Client, request.CallbackId, 1));
                await callback.SendDoneAndDisposeAsync(Client.NextSerial());
                break;
            }
            case 1:
            {
                GetRegistryRequest request = WlDisplayProtocol.DecodeGetRegistry(message.Body, message.Fds);
                var registry = Client.Register(new WlRegistryResource(Client, request.RegistryId, 1));
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
        BindRequest request = WlRegistryProtocol.DecodeBind(message.Body, message.Fds);
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
                CreateSurfaceRequest request = WlCompositorProtocol.DecodeCreateSurface(message.Body, message.Fds);
                Client.Register(new WlSurfaceResource(Client, request.Id, Math.Min(Version, WlSurfaceProtocol.Version)));
                break;
            }
            case 1:
            {
                CreateRegionRequest request = WlCompositorProtocol.DecodeCreateRegion(message.Body, message.Fds);
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

internal sealed class WlSurfaceResource : WaylandResource
{
    private readonly WlSurfaceState _pending = new();
    private readonly WlSurfaceState _current = new();

    public WlSurfaceResource(WaylandClient client, uint objectId, uint version)
        : base(client, objectId, version, WlSurfaceProtocol.InterfaceName)
    {
    }

    public override IReadOnlyList<WaylandMessageMetadata> Requests => WlSurfaceProtocol.Requests;
    public WlBufferResource? CurrentBuffer => _current.Buffer;
    public XdgSurfaceResource? XdgSurface { get; private set; }

    public void AttachXdgSurface(XdgSurfaceResource xdgSurface)
    {
        if (XdgSurface != null)
            throw new WaylandProtocolException(ObjectId, 0, "wl_surface already has a role.");
        XdgSurface = xdgSurface;
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
                AttachRequest request = WlSurfaceProtocol.DecodeAttach(message.Body, message.Fds);
                _pending.Buffer = request.BufferId == 0 ? null : Client.Objects.Require<WlBufferResource>(request.BufferId);
                break;
            }
            case 2:
            {
                DamageRequest request = WlSurfaceProtocol.DecodeDamage(message.Body, message.Fds);
                _pending.Damages.Add(new WaylandRect(request.X, request.Y, request.Width, request.Height));
                break;
            }
            case 3:
            {
                FrameRequest request = WlSurfaceProtocol.DecodeFrame(message.Body, message.Fds);
                _pending.FrameCallbacks.Add(Client.Register(new WlCallbackResource(Client, request.CallbackId, 1)));
                break;
            }
            case 4:
                await CommitAsync();
                break;
            case 5:
            {
                DamageRequest request = WlSurfaceProtocol.DecodeDamage(message.Body, message.Fds);
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

        List<WlCallbackResource> callbacks = [.. _pending.FrameCallbacks];
        _pending.FrameCallbacks.Clear();
        foreach (WlCallbackResource callback in callbacks)
            await callback.SendDoneAndDisposeAsync(Client.NextSerial());
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
        CreatePoolRequest request = WlShmProtocol.DecodeCreatePool(message.Body, message.Fds);
        if (request.Size <= 0)
            throw new WaylandProtocolException(ObjectId, 0, "wl_shm_pool size must be positive.");

        Client.Register(new WlShmPoolResource(Client, request.Id, 1, request.Fd, request.Size));
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
                CreateBufferRequest request = WlShmPoolProtocol.DecodeCreateBuffer(message.Body, message.Fds);
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
                ResizePoolRequest request = WlShmPoolProtocol.DecodeResize(message.Body, message.Fds);
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

    public override ValueTask DispatchAsync(WaylandIncomingMessage message)
    {
        Destroy();
        return ValueTask.CompletedTask;
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
                GetXdgSurfaceRequest request = XdgWmBaseProtocol.DecodeGetXdgSurface(message.Body, message.Fds);
                var surface = Client.Objects.Require<WlSurfaceResource>(request.SurfaceId);
                var xdgSurface = Client.Register(new XdgSurfaceResource(Client, request.Id, 1, surface));
                surface.AttachXdgSurface(xdgSurface);
                break;
            }
            case 3:
            {
                PongRequest request = XdgWmBaseProtocol.DecodePong(message.Body, message.Fds);
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
                GetToplevelRequest request = XdgSurfaceProtocol.DecodeGetToplevel(message.Body, message.Fds);
                if (Toplevel != null)
                    throw new WaylandProtocolException(ObjectId, 0, "xdg_surface already has a toplevel.");
                Toplevel = Client.Register(new XdgToplevelResource(Client, request.Id, 1, this));
                await Toplevel.SendInitialConfigureAsync();
                break;
            }
            case 2:
            {
                AckConfigureRequest request = XdgSurfaceProtocol.DecodeAckConfigure(message.Body, message.Fds);
                if (request.Serial != PendingConfigureSerial)
                    throw new WaylandProtocolException(ObjectId, 0, $"Unknown configure serial {request.Serial}.");
                AckedConfigureSerial = request.Serial;
                break;
            }
            default:
                throw new WaylandProtocolException(ObjectId, 0, $"Unsupported xdg_surface opcode {message.Header.Opcode}.");
        }
    }

    public async ValueTask SendConfigureAsync()
    {
        PendingConfigureSerial = Client.NextSerial();
        await XdgSurfaceEventWriter.ConfigureAsync(Client, ObjectId, PendingConfigureSerial);
    }
}

internal sealed class XdgToplevelResource : WaylandResource
{
    public XdgToplevelResource(WaylandClient client, uint objectId, uint version, XdgSurfaceResource surface)
        : base(client, objectId, version, XdgToplevelProtocol.InterfaceName)
    {
        Surface = surface;
    }

    public XdgSurfaceResource Surface { get; }
    public string Title { get; private set; } = string.Empty;
    public string AppId { get; private set; } = string.Empty;

    public override IReadOnlyList<WaylandMessageMetadata> Requests => XdgToplevelProtocol.Requests;

    public override ValueTask DispatchAsync(WaylandIncomingMessage message)
    {
        switch (message.Header.Opcode)
        {
            case 0:
                Destroy();
                break;
            case 1:
                break;
            case 2:
                Title = XdgToplevelProtocol.DecodeSetTitle(message.Body, message.Fds).Title;
                break;
            case 3:
                AppId = XdgToplevelProtocol.DecodeSetAppId(message.Body, message.Fds).AppId;
                break;
            default:
                throw new WaylandProtocolException(ObjectId, 0, $"Unsupported xdg_toplevel opcode {message.Header.Opcode}.");
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask SendInitialConfigureAsync()
    {
        await XdgToplevelEventWriter.ConfigureAsync(Client, ObjectId, 0, 0, []);
        await Surface.SendConfigureAsync();
    }
}
