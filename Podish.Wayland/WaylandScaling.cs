namespace Podish.Wayland;

public sealed partial class WaylandServer
{
    private void RegisterScalingGlobals()
    {
        Globals.Add(WpViewporterProtocol.InterfaceName, WpViewporterProtocol.Version,
            static (client, id, version) => client.Register(new ViewporterResource(client, id, version)));
        Globals.Add(WpFractionalScaleManagerV1Protocol.InterfaceName, WpFractionalScaleManagerV1Protocol.Version,
            static (client, id, version) => client.Register(new FractionalScaleManagerResource(client, id, version)));
        Globals.Add(ZxdgOutputManagerV1Protocol.InterfaceName, ZxdgOutputManagerV1Protocol.Version,
            static (client, id, version) => client.Register(new XdgOutputManagerResource(client, id, version)));
    }
}

internal readonly record struct WaylandViewportSource(double X, double Y, double Width, double Height);

internal static class WaylandBufferMapping
{
    internal static WaylandSurfaceMapping Resolve(uint surfaceId, uint? viewportId, WlSurfaceState pending)
    {
        WlBufferResource buffer = pending.Buffer!;
        int scale = pending.BufferScale;
        if (buffer.Width % scale != 0 || buffer.Height % scale != 0)
            throw new WaylandProtocolException(surfaceId, (uint)WlSurfaceError.InvalidSize,
                "Buffer dimensions must be multiples of its scale.");

        bool transposed = ((uint)pending.Transform & 1) != 0;
        int width = (transposed ? buffer.Height : buffer.Width) / scale;
        int height = (transposed ? buffer.Width : buffer.Height) / scale;
        var source = pending.ViewportSource ?? new WaylandViewportSource(0, 0, width, height);
        if (source.X + source.Width > width || source.Y + source.Height > height)
            throw new WaylandProtocolException(viewportId ?? surfaceId, (uint)WpViewportError.OutOfBuffer,
                "The viewport source exceeds the transformed buffer.");
        if (pending.ViewportDestination == null &&
            (source.Width != Math.Truncate(source.Width) || source.Height != Math.Truncate(source.Height)))
            throw new WaylandProtocolException(viewportId ?? surfaceId, (uint)WpViewportError.BadSize,
                "Fractional source dimensions require an explicit viewport destination.");

        var destination = pending.ViewportDestination ?? ((int)source.Width, (int)source.Height);
        // Convert from the cropped surface to the original buffer's normalized coordinates.
        float left = (float)(source.X / width), top = (float)(source.Y / height);
        float right = (float)((source.X + source.Width) / width), bottom = (float)((source.Y + source.Height) / height);
        (float u, float v) = ToBuffer(left, top, pending.Transform);
        (float rightU, float rightV) = ToBuffer(right, top, pending.Transform);
        (float bottomU, float bottomV) = ToBuffer(left, bottom, pending.Transform);
        return new WaylandSurfaceMapping(destination.Item1, destination.Item2, u, v,
            rightU - u, rightV - v, bottomU - u, bottomV - v);
    }

    private static (float, float) ToBuffer(float u, float v, WlOutputTransform transform)
    {
        if (((uint)transform & 4) != 0)
            u = 1 - u;
        return ((uint)transform & 3) switch
        {
            1 => (v, 1 - u),
            2 => (1 - u, 1 - v),
            3 => (1 - v, u),
            _ => (u, v)
        };
    }
}

internal sealed class ViewporterResource(WaylandClient client, uint id, uint version)
    : WaylandResource(client, id, version, WpViewporterProtocol.InterfaceName)
{
    public override IReadOnlyList<WaylandMessageMetadata> Requests => WpViewporterProtocol.Requests;

    public override ValueTask DispatchAsync(WaylandIncomingMessage message)
    {
        if (message.Header.Opcode == 0)
            Destroy();
        else
        {
            var request = WpViewporterProtocol.DecodeGetViewport(message.Body, message.Fds);
            var surface = Client.Objects.Require<WlSurfaceResource>(request.Surface);
            if (surface.Viewport != null)
                throw new WaylandProtocolException(ObjectId, (uint)WpViewporterError.ViewportExists,
                    "The surface already has a viewport.");
            surface.Viewport = Client.Register(new ViewportResource(Client, request.Id, Version, surface));
        }
        return ValueTask.CompletedTask;
    }
}

internal sealed class ViewportResource(WaylandClient client, uint id, uint version, WlSurfaceResource surface)
    : WaylandResource(client, id, version, WpViewportProtocol.InterfaceName)
{
    public override IReadOnlyList<WaylandMessageMetadata> Requests => WpViewportProtocol.Requests;

    public override ValueTask DispatchAsync(WaylandIncomingMessage message)
    {
        if (message.Header.Opcode == 0)
        {
            Destroy();
            return ValueTask.CompletedTask;
        }
        if (surface.Destroyed)
            throw new WaylandProtocolException(ObjectId, (uint)WpViewportError.NoSurface, "The surface was destroyed.");

        if (message.Header.Opcode == 1)
        {
            // wl_fixed is 24.8 fixed point. Keep all eight fractional bits when cropping.
            var reader = new WaylandWireReader(message.Body, message.Fds);
            double x = reader.ReadInt() / 256d, y = reader.ReadInt() / 256d;
            double width = reader.ReadInt() / 256d, height = reader.ReadInt() / 256d;
            reader.EnsureExhausted();
            if (x == -1 && y == -1 && width == -1 && height == -1)
                surface.SetViewportSource(null);
            else if (x < 0 || y < 0 || width <= 0 || height <= 0)
                throw BadValue();
            else
                surface.SetViewportSource(new WaylandViewportSource(x, y, width, height));
        }
        else
        {
            var request = WpViewportProtocol.DecodeSetDestination(message.Body, message.Fds);
            if (request.Width == -1 && request.Height == -1)
                surface.SetViewportDestination(null);
            else if (request.Width <= 0 || request.Height <= 0)
                throw BadValue();
            else
                surface.SetViewportDestination((request.Width, request.Height));
        }
        return ValueTask.CompletedTask;
    }

    private WaylandProtocolException BadValue() =>
        new(ObjectId, (uint)WpViewportError.BadValue, "Invalid viewport coordinates or dimensions.");

    public override void Destroy()
    {
        if (surface.Viewport == this)
        {
            surface.Viewport = null;
            surface.SetViewportSource(null);
            surface.SetViewportDestination(null);
        }
        base.Destroy();
    }
}

internal sealed class FractionalScaleManagerResource(WaylandClient client, uint id, uint version)
    : WaylandResource(client, id, version, WpFractionalScaleManagerV1Protocol.InterfaceName)
{
    public override IReadOnlyList<WaylandMessageMetadata> Requests => WpFractionalScaleManagerV1Protocol.Requests;

    public override ValueTask DispatchAsync(WaylandIncomingMessage message)
    {
        if (message.Header.Opcode == 0)
        {
            Destroy();
            return ValueTask.CompletedTask;
        }

        var request = WpFractionalScaleManagerV1Protocol.DecodeGetFractionalScale(message.Body, message.Fds);
        var surface = Client.Objects.Require<WlSurfaceResource>(request.Surface);
        if (surface.FractionalScale != null)
            throw new WaylandProtocolException(ObjectId, (uint)WpFractionalScaleManagerV1Error.FractionalScaleExists,
                "The surface already has a fractional scale object.");
        var resource = Client.Register(new FractionalScaleResource(Client, request.Id, Version, surface));
        surface.FractionalScale = resource;
        return WpFractionalScaleV1EventWriter.PreferredScaleAsync(Client, resource.ObjectId, Client.EffectiveOutput.Scale120);
    }
}

internal sealed class FractionalScaleResource(WaylandClient client, uint id, uint version, WlSurfaceResource surface)
    : WaylandResource(client, id, version, WpFractionalScaleV1Protocol.InterfaceName)
{
    public override IReadOnlyList<WaylandMessageMetadata> Requests => WpFractionalScaleV1Protocol.Requests;

    public override ValueTask DispatchAsync(WaylandIncomingMessage message)
    {
        Destroy();
        return ValueTask.CompletedTask;
    }

    public override void Destroy()
    {
        if (surface.FractionalScale == this)
            surface.FractionalScale = null;
        base.Destroy();
    }
}

internal sealed class XdgOutputManagerResource(WaylandClient client, uint id, uint version)
    : WaylandResource(client, id, version, ZxdgOutputManagerV1Protocol.InterfaceName)
{
    public override IReadOnlyList<WaylandMessageMetadata> Requests => ZxdgOutputManagerV1Protocol.Requests;

    public override async ValueTask DispatchAsync(WaylandIncomingMessage message)
    {
        if (message.Header.Opcode == 0)
        {
            Destroy();
            return;
        }

        var request = ZxdgOutputManagerV1Protocol.DecodeGetXdgOutput(message.Body, message.Fds);
        var output = Client.Objects.Require<WlOutputResource>(request.Output);
        var resource = Client.Register(new XdgOutputResource(Client, request.Id, Version, output));
        await resource.AdvertiseAsync(initial: true);
        if (Version >= 3 && output.Version >= 2)
            await WlOutputEventWriter.DoneAsync(Client, output.ObjectId);
    }
}

internal sealed class XdgOutputResource(WaylandClient client, uint id, uint version, WlOutputResource output)
    : WaylandResource(client, id, version, ZxdgOutputV1Protocol.InterfaceName)
{
    public override IReadOnlyList<WaylandMessageMetadata> Requests => ZxdgOutputV1Protocol.Requests;
    internal WlOutputResource Output => output;

    internal async ValueTask AdvertiseAsync(bool initial)
    {
        var info = Client.EffectiveOutput;
        await ZxdgOutputV1EventWriter.LogicalPositionAsync(Client, ObjectId, 0, 0);
        await ZxdgOutputV1EventWriter.LogicalSizeAsync(Client, ObjectId, info.Width, info.Height);
        if (initial && Version >= 2)
        {
            await ZxdgOutputV1EventWriter.NameAsync(Client, ObjectId, info.Name);
            await ZxdgOutputV1EventWriter.DescriptionAsync(Client, ObjectId, info.Description);
        }
        if (Version < 3 || output.Version < 2)
            await ZxdgOutputV1EventWriter.DoneAsync(Client, ObjectId);
    }

    public override ValueTask DispatchAsync(WaylandIncomingMessage message)
    {
        Destroy();
        return ValueTask.CompletedTask;
    }
}
