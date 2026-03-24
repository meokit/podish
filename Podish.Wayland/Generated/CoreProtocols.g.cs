namespace Podish.Wayland;

public enum WlShmFormat : uint
{
    Argb8888 = 0,
    Xrgb8888 = 1
}

public static class WlDisplayProtocol
{
    public const string InterfaceName = "wl_display";
    public const uint Version = 1;

    public static readonly ReadOnlyCollection<WaylandMessageMetadata> Requests = new([
        new WaylandMessageMetadata(InterfaceName, "sync", 0, 1, [
            new WaylandArgumentMetadata("callback", WaylandArgKind.NewId, "wl_callback")
        ]),
        new WaylandMessageMetadata(InterfaceName, "get_registry", 1, 1, [
            new WaylandArgumentMetadata("registry", WaylandArgKind.NewId, "wl_registry")
        ])
    ]);

    public static SyncRequest DecodeSync(byte[] body, IReadOnlyList<LinuxFile> fds)
    {
        var reader = new WaylandWireReader(body, fds);
        var request = new SyncRequest(reader.ReadNewId());
        reader.EnsureExhausted();
        return request;
    }

    public static GetRegistryRequest DecodeGetRegistry(byte[] body, IReadOnlyList<LinuxFile> fds)
    {
        var reader = new WaylandWireReader(body, fds);
        var request = new GetRegistryRequest(reader.ReadNewId());
        reader.EnsureExhausted();
        return request;
    }
}

public readonly record struct SyncRequest(uint CallbackId);
public readonly record struct GetRegistryRequest(uint RegistryId);

public static class WlDisplayEventWriter
{
    public static ValueTask ErrorAsync(WaylandClient client, uint objectId, uint failingObjectId, uint code, string message)
    {
        return client.SendEventAsync(objectId, 0, writer =>
        {
            writer.WriteObjectId(failingObjectId);
            writer.WriteUInt(code);
            writer.WriteString(message);
        });
    }

    public static ValueTask DeleteIdAsync(WaylandClient client, uint objectId, uint deletedId)
    {
        return client.SendEventAsync(objectId, 1, writer => writer.WriteUInt(deletedId));
    }
}

public static class WlRegistryProtocol
{
    public const string InterfaceName = "wl_registry";
    public const uint Version = 1;

    public static readonly ReadOnlyCollection<WaylandMessageMetadata> Requests = new([
        new WaylandMessageMetadata(InterfaceName, "bind", 0, 1, [
            new WaylandArgumentMetadata("name", WaylandArgKind.Uint),
            new WaylandArgumentMetadata("interface", WaylandArgKind.String),
            new WaylandArgumentMetadata("version", WaylandArgKind.Uint),
            new WaylandArgumentMetadata("id", WaylandArgKind.NewId)
        ])
    ]);

    public static BindRequest DecodeBind(byte[] body, IReadOnlyList<LinuxFile> fds)
    {
        var reader = new WaylandWireReader(body, fds);
        var request = new BindRequest(reader.ReadUInt(), reader.ReadString() ?? string.Empty, reader.ReadUInt(), reader.ReadNewId());
        reader.EnsureExhausted();
        return request;
    }
}

public readonly record struct BindRequest(uint Name, string Interface, uint Version, uint Id);

public static class WlRegistryEventWriter
{
    public static ValueTask GlobalAsync(WaylandClient client, uint objectId, uint name, string iface, uint version)
    {
        return client.SendEventAsync(objectId, 0, writer =>
        {
            writer.WriteUInt(name);
            writer.WriteString(iface);
            writer.WriteUInt(version);
        });
    }

    public static ValueTask GlobalRemoveAsync(WaylandClient client, uint objectId, uint name)
    {
        return client.SendEventAsync(objectId, 1, writer => writer.WriteUInt(name));
    }
}

public static class WlCallbackProtocol
{
    public const string InterfaceName = "wl_callback";
    public const uint Version = 1;
    public static readonly ReadOnlyCollection<WaylandMessageMetadata> Requests = new([]);
}

public static class WlCallbackEventWriter
{
    public static ValueTask DoneAsync(WaylandClient client, uint objectId, uint callbackData)
    {
        return client.SendEventAsync(objectId, 0, writer => writer.WriteUInt(callbackData));
    }
}

public static class WlCompositorProtocol
{
    public const string InterfaceName = "wl_compositor";
    public const uint Version = 4;

    public static readonly ReadOnlyCollection<WaylandMessageMetadata> Requests = new([
        new WaylandMessageMetadata(InterfaceName, "create_surface", 0, 1, [
            new WaylandArgumentMetadata("id", WaylandArgKind.NewId, "wl_surface")
        ]),
        new WaylandMessageMetadata(InterfaceName, "create_region", 1, 1, [
            new WaylandArgumentMetadata("id", WaylandArgKind.NewId, "wl_region")
        ])
    ]);

    public static CreateSurfaceRequest DecodeCreateSurface(byte[] body, IReadOnlyList<LinuxFile> fds)
    {
        var reader = new WaylandWireReader(body, fds);
        var request = new CreateSurfaceRequest(reader.ReadNewId());
        reader.EnsureExhausted();
        return request;
    }

    public static CreateRegionRequest DecodeCreateRegion(byte[] body, IReadOnlyList<LinuxFile> fds)
    {
        var reader = new WaylandWireReader(body, fds);
        var request = new CreateRegionRequest(reader.ReadNewId());
        reader.EnsureExhausted();
        return request;
    }
}

public readonly record struct CreateSurfaceRequest(uint Id);
public readonly record struct CreateRegionRequest(uint Id);

public static class WlSurfaceProtocol
{
    public const string InterfaceName = "wl_surface";
    public const uint Version = 4;

    public static readonly ReadOnlyCollection<WaylandMessageMetadata> Requests = new([
        new WaylandMessageMetadata(InterfaceName, "destroy", 0, 1, []),
        new WaylandMessageMetadata(InterfaceName, "attach", 1, 1, [
            new WaylandArgumentMetadata("buffer", WaylandArgKind.Object, "wl_buffer", true),
            new WaylandArgumentMetadata("x", WaylandArgKind.Int),
            new WaylandArgumentMetadata("y", WaylandArgKind.Int)
        ]),
        new WaylandMessageMetadata(InterfaceName, "damage", 2, 1, [
            new WaylandArgumentMetadata("x", WaylandArgKind.Int),
            new WaylandArgumentMetadata("y", WaylandArgKind.Int),
            new WaylandArgumentMetadata("width", WaylandArgKind.Int),
            new WaylandArgumentMetadata("height", WaylandArgKind.Int)
        ]),
        new WaylandMessageMetadata(InterfaceName, "frame", 3, 1, [
            new WaylandArgumentMetadata("callback", WaylandArgKind.NewId, "wl_callback")
        ]),
        new WaylandMessageMetadata(InterfaceName, "commit", 4, 1, []),
        new WaylandMessageMetadata(InterfaceName, "damage_buffer", 5, 4, [
            new WaylandArgumentMetadata("x", WaylandArgKind.Int),
            new WaylandArgumentMetadata("y", WaylandArgKind.Int),
            new WaylandArgumentMetadata("width", WaylandArgKind.Int),
            new WaylandArgumentMetadata("height", WaylandArgKind.Int)
        ])
    ]);

    public static AttachRequest DecodeAttach(byte[] body, IReadOnlyList<LinuxFile> fds)
    {
        var reader = new WaylandWireReader(body, fds);
        var request = new AttachRequest(reader.ReadObjectId(), reader.ReadInt(), reader.ReadInt());
        reader.EnsureExhausted();
        return request;
    }

    public static DamageRequest DecodeDamage(byte[] body, IReadOnlyList<LinuxFile> fds)
    {
        var reader = new WaylandWireReader(body, fds);
        var request = new DamageRequest(reader.ReadInt(), reader.ReadInt(), reader.ReadInt(), reader.ReadInt());
        reader.EnsureExhausted();
        return request;
    }

    public static FrameRequest DecodeFrame(byte[] body, IReadOnlyList<LinuxFile> fds)
    {
        var reader = new WaylandWireReader(body, fds);
        var request = new FrameRequest(reader.ReadNewId());
        reader.EnsureExhausted();
        return request;
    }
}

public readonly record struct AttachRequest(uint BufferId, int X, int Y);
public readonly record struct DamageRequest(int X, int Y, int Width, int Height);
public readonly record struct FrameRequest(uint CallbackId);

public static class WlRegionProtocol
{
    public const string InterfaceName = "wl_region";
    public const uint Version = 1;
    public static readonly ReadOnlyCollection<WaylandMessageMetadata> Requests = new([
        new WaylandMessageMetadata(InterfaceName, "destroy", 0, 1, []),
        new WaylandMessageMetadata(InterfaceName, "add", 1, 1, [
            new WaylandArgumentMetadata("x", WaylandArgKind.Int),
            new WaylandArgumentMetadata("y", WaylandArgKind.Int),
            new WaylandArgumentMetadata("width", WaylandArgKind.Int),
            new WaylandArgumentMetadata("height", WaylandArgKind.Int)
        ]),
        new WaylandMessageMetadata(InterfaceName, "subtract", 2, 1, [
            new WaylandArgumentMetadata("x", WaylandArgKind.Int),
            new WaylandArgumentMetadata("y", WaylandArgKind.Int),
            new WaylandArgumentMetadata("width", WaylandArgKind.Int),
            new WaylandArgumentMetadata("height", WaylandArgKind.Int)
        ])
    ]);
}

public static class WlShmProtocol
{
    public const string InterfaceName = "wl_shm";
    public const uint Version = 1;
    public static readonly ReadOnlyCollection<WaylandMessageMetadata> Requests = new([
        new WaylandMessageMetadata(InterfaceName, "create_pool", 0, 1, [
            new WaylandArgumentMetadata("id", WaylandArgKind.NewId, "wl_shm_pool"),
            new WaylandArgumentMetadata("fd", WaylandArgKind.Fd),
            new WaylandArgumentMetadata("size", WaylandArgKind.Int)
        ])
    ]);

    public static CreatePoolRequest DecodeCreatePool(byte[] body, IReadOnlyList<LinuxFile> fds)
    {
        var reader = new WaylandWireReader(body, fds);
        uint id = reader.ReadNewId();
        LinuxFile fd = reader.ReadFd();
        var request = new CreatePoolRequest(id, fd, reader.ReadInt());
        reader.EnsureExhausted();
        return request;
    }
}

public readonly record struct CreatePoolRequest(uint Id, LinuxFile Fd, int Size);

public static class WlShmEventWriter
{
    public static ValueTask FormatAsync(WaylandClient client, uint objectId, WlShmFormat format)
    {
        return client.SendEventAsync(objectId, 0, writer => writer.WriteUInt((uint)format));
    }
}

public static class WlShmPoolProtocol
{
    public const string InterfaceName = "wl_shm_pool";
    public const uint Version = 1;
    public static readonly ReadOnlyCollection<WaylandMessageMetadata> Requests = new([
        new WaylandMessageMetadata(InterfaceName, "create_buffer", 0, 1, [
            new WaylandArgumentMetadata("id", WaylandArgKind.NewId, "wl_buffer"),
            new WaylandArgumentMetadata("offset", WaylandArgKind.Int),
            new WaylandArgumentMetadata("width", WaylandArgKind.Int),
            new WaylandArgumentMetadata("height", WaylandArgKind.Int),
            new WaylandArgumentMetadata("stride", WaylandArgKind.Int),
            new WaylandArgumentMetadata("format", WaylandArgKind.Uint)
        ]),
        new WaylandMessageMetadata(InterfaceName, "destroy", 1, 1, []),
        new WaylandMessageMetadata(InterfaceName, "resize", 2, 1, [
            new WaylandArgumentMetadata("size", WaylandArgKind.Int)
        ])
    ]);

    public static CreateBufferRequest DecodeCreateBuffer(byte[] body, IReadOnlyList<LinuxFile> fds)
    {
        var reader = new WaylandWireReader(body, fds);
        var request = new CreateBufferRequest(reader.ReadNewId(), reader.ReadInt(), reader.ReadInt(), reader.ReadInt(), reader.ReadInt(),
            (WlShmFormat)reader.ReadUInt());
        reader.EnsureExhausted();
        return request;
    }

    public static ResizePoolRequest DecodeResize(byte[] body, IReadOnlyList<LinuxFile> fds)
    {
        var reader = new WaylandWireReader(body, fds);
        var request = new ResizePoolRequest(reader.ReadInt());
        reader.EnsureExhausted();
        return request;
    }
}

public readonly record struct CreateBufferRequest(uint Id, int Offset, int Width, int Height, int Stride, WlShmFormat Format);
public readonly record struct ResizePoolRequest(int Size);

public static class WlBufferProtocol
{
    public const string InterfaceName = "wl_buffer";
    public const uint Version = 1;
    public static readonly ReadOnlyCollection<WaylandMessageMetadata> Requests = new([
        new WaylandMessageMetadata(InterfaceName, "destroy", 0, 1, [])
    ]);
}

public static class WlBufferEventWriter
{
    public static ValueTask ReleaseAsync(WaylandClient client, uint objectId)
    {
        return client.SendEventAsync(objectId, 0, static _ => { });
    }
}

public static class XdgWmBaseProtocol
{
    public const string InterfaceName = "xdg_wm_base";
    public const uint Version = 1;
    public static readonly ReadOnlyCollection<WaylandMessageMetadata> Requests = new([
        new WaylandMessageMetadata(InterfaceName, "destroy", 0, 1, []),
        new WaylandMessageMetadata(InterfaceName, "create_positioner", 1, 1, [
            new WaylandArgumentMetadata("id", WaylandArgKind.NewId, "xdg_positioner")
        ]),
        new WaylandMessageMetadata(InterfaceName, "get_xdg_surface", 2, 1, [
            new WaylandArgumentMetadata("id", WaylandArgKind.NewId, "xdg_surface"),
            new WaylandArgumentMetadata("surface", WaylandArgKind.Object, "wl_surface")
        ]),
        new WaylandMessageMetadata(InterfaceName, "pong", 3, 1, [
            new WaylandArgumentMetadata("serial", WaylandArgKind.Uint)
        ])
    ]);

    public static GetXdgSurfaceRequest DecodeGetXdgSurface(byte[] body, IReadOnlyList<LinuxFile> fds)
    {
        var reader = new WaylandWireReader(body, fds);
        var request = new GetXdgSurfaceRequest(reader.ReadNewId(), reader.ReadObjectId());
        reader.EnsureExhausted();
        return request;
    }

    public static PongRequest DecodePong(byte[] body, IReadOnlyList<LinuxFile> fds)
    {
        var reader = new WaylandWireReader(body, fds);
        var request = new PongRequest(reader.ReadUInt());
        reader.EnsureExhausted();
        return request;
    }
}

public readonly record struct GetXdgSurfaceRequest(uint Id, uint SurfaceId);
public readonly record struct PongRequest(uint Serial);

public static class XdgWmBaseEventWriter
{
    public static ValueTask PingAsync(WaylandClient client, uint objectId, uint serial)
    {
        return client.SendEventAsync(objectId, 0, writer => writer.WriteUInt(serial));
    }
}

public static class XdgSurfaceProtocol
{
    public const string InterfaceName = "xdg_surface";
    public const uint Version = 1;
    public static readonly ReadOnlyCollection<WaylandMessageMetadata> Requests = new([
        new WaylandMessageMetadata(InterfaceName, "destroy", 0, 1, []),
        new WaylandMessageMetadata(InterfaceName, "get_toplevel", 1, 1, [
            new WaylandArgumentMetadata("id", WaylandArgKind.NewId, "xdg_toplevel")
        ]),
        new WaylandMessageMetadata(InterfaceName, "ack_configure", 2, 1, [
            new WaylandArgumentMetadata("serial", WaylandArgKind.Uint)
        ])
    ]);

    public static GetToplevelRequest DecodeGetToplevel(byte[] body, IReadOnlyList<LinuxFile> fds)
    {
        var reader = new WaylandWireReader(body, fds);
        var request = new GetToplevelRequest(reader.ReadNewId());
        reader.EnsureExhausted();
        return request;
    }

    public static AckConfigureRequest DecodeAckConfigure(byte[] body, IReadOnlyList<LinuxFile> fds)
    {
        var reader = new WaylandWireReader(body, fds);
        var request = new AckConfigureRequest(reader.ReadUInt());
        reader.EnsureExhausted();
        return request;
    }
}

public readonly record struct GetToplevelRequest(uint Id);
public readonly record struct AckConfigureRequest(uint Serial);

public static class XdgSurfaceEventWriter
{
    public static ValueTask ConfigureAsync(WaylandClient client, uint objectId, uint serial)
    {
        return client.SendEventAsync(objectId, 0, writer => writer.WriteUInt(serial));
    }
}

public static class XdgToplevelProtocol
{
    public const string InterfaceName = "xdg_toplevel";
    public const uint Version = 1;
    public static readonly ReadOnlyCollection<WaylandMessageMetadata> Requests = new([
        new WaylandMessageMetadata(InterfaceName, "destroy", 0, 1, []),
        new WaylandMessageMetadata(InterfaceName, "set_parent", 1, 1, [
            new WaylandArgumentMetadata("parent", WaylandArgKind.Object, "xdg_toplevel", true)
        ]),
        new WaylandMessageMetadata(InterfaceName, "set_title", 2, 1, [
            new WaylandArgumentMetadata("title", WaylandArgKind.String)
        ]),
        new WaylandMessageMetadata(InterfaceName, "set_app_id", 3, 1, [
            new WaylandArgumentMetadata("app_id", WaylandArgKind.String)
        ])
    ]);

    public static SetTitleRequest DecodeSetTitle(byte[] body, IReadOnlyList<LinuxFile> fds)
    {
        var reader = new WaylandWireReader(body, fds);
        var request = new SetTitleRequest(reader.ReadString() ?? string.Empty);
        reader.EnsureExhausted();
        return request;
    }

    public static SetAppIdRequest DecodeSetAppId(byte[] body, IReadOnlyList<LinuxFile> fds)
    {
        var reader = new WaylandWireReader(body, fds);
        var request = new SetAppIdRequest(reader.ReadString() ?? string.Empty);
        reader.EnsureExhausted();
        return request;
    }
}

public readonly record struct SetTitleRequest(string Title);
public readonly record struct SetAppIdRequest(string AppId);

public static class XdgToplevelEventWriter
{
    public static ValueTask ConfigureAsync(WaylandClient client, uint objectId, int width, int height, byte[] states)
    {
        return client.SendEventAsync(objectId, 0, writer =>
        {
            writer.WriteInt(width);
            writer.WriteInt(height);
            writer.WriteArray(states);
        });
    }
}
