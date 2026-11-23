using Fiberish.VFS;

namespace Podish.Wayland;

internal sealed class LinuxDmabufResource(WaylandClient client, uint objectId, uint version)
    : WaylandResource(client, objectId, version, ZwpLinuxDmabufV1Protocol.InterfaceName)
{
    internal const uint Xrgb8888 = 0x34325258, Argb8888 = 0x34325241;
    internal const ulong ImplicitModifier = 0x00ff_ffff_ffff_ffff;

    public override IReadOnlyList<WaylandMessageMetadata> Requests => ZwpLinuxDmabufV1Protocol.Requests;

    internal async ValueTask AdvertiseAsync()
    {
        foreach (uint format in new[] { Xrgb8888, Argb8888 })
        {
            if (Version >= 3)
                await ZwpLinuxDmabufV1EventWriter.ModifierAsync(Client, ObjectId, format,
                    (uint)(ImplicitModifier >> 32), unchecked((uint)ImplicitModifier));
            else
                await ZwpLinuxDmabufV1EventWriter.FormatAsync(Client, ObjectId, format);
        }
    }

    public override ValueTask DispatchAsync(WaylandIncomingMessage message)
    {
        if (message.Header.Opcode == 0)
            Destroy();
        else
        {
            var request = ZwpLinuxDmabufV1Protocol.DecodeCreateParams(message.Body, message.Fds);
            Client.Register(new LinuxBufferParamsResource(Client, request.ParamsId, Version));
        }
        return ValueTask.CompletedTask;
    }
}

internal sealed class LinuxBufferParamsResource(WaylandClient client, uint objectId, uint version)
    : WaylandResource(client, objectId, version, ZwpLinuxBufferParamsV1Protocol.InterfaceName)
{
    private LinuxFile? _file;
    private uint _offset;
    private uint _stride;
    private ulong _modifier;
    private bool _used;

    public override IReadOnlyList<WaylandMessageMetadata> Requests => ZwpLinuxBufferParamsV1Protocol.Requests;

    public override async ValueTask DispatchAsync(WaylandIncomingMessage message)
    {
        if (message.Header.Opcode == 0)
        {
            Destroy();
            return;
        }
        if (_used)
            throw Error(ZwpLinuxBufferParamsV1Error.AlreadyUsed, "The buffer params have already been used.");

        switch (message.Header.Opcode)
        {
            case 1:
            {
                var request = ZwpLinuxBufferParamsV1Protocol.DecodeAdd(message.Body, message.Fds);
                if (request.PlaneIdx != 0)
                    throw Error(ZwpLinuxBufferParamsV1Error.PlaneIdx, "Podish PRIME buffers have one plane.");
                if (_file != null)
                    throw Error(ZwpLinuxBufferParamsV1Error.PlaneSet, "The plane has already been set.");
                request.Fd.Get();
                _file = request.Fd;
                _offset = request.Offset;
                _stride = request.Stride;
                _modifier = ((ulong)request.ModifierHi << 32) | request.ModifierLo;
                break;
            }
            case 2:
            {
                var request = ZwpLinuxBufferParamsV1Protocol.DecodeCreate(message.Body, message.Fds);
                await CreateAsync(null, request.Width, request.Height, request.Format, request.Flags);
                break;
            }
            case 3:
            {
                var request = ZwpLinuxBufferParamsV1Protocol.DecodeCreateImmed(message.Body, message.Fds);
                await CreateAsync(request.BufferId, request.Width, request.Height, request.Format, request.Flags);
                break;
            }
        }
    }

    private async ValueTask CreateAsync(uint? requestedId, int width, int height, uint format, ZwpLinuxBufferParamsV1Flags flags)
    {
        _used = true;
        var file = _file;
        _file = null;
        try
        {
            if (file == null)
                throw Error(ZwpLinuxBufferParamsV1Error.Incomplete, "Plane 0 is required.");
            if (width <= 0 || height <= 0)
                throw Error(ZwpLinuxBufferParamsV1Error.InvalidDimensions, "Positive buffer dimensions are required.");
            if (format is not (LinuxDmabufResource.Xrgb8888 or LinuxDmabufResource.Argb8888) ||
                (flags & ~ZwpLinuxBufferParamsV1Flags.YInvert) != 0)
                throw Error(ZwpLinuxBufferParamsV1Error.InvalidFormat, "Unsupported PRIME format or flags.");
            if (_offset != 0 || _stride < (ulong)width * 4 ||
                (ulong)_stride * (uint)height > (file.OpenedInode?.Size ?? 0))
                throw Error(ZwpLinuxBufferParamsV1Error.OutOfBounds, "Plane bounds do not match the descriptor.");

            var buffer = DrmCardInode.GetGpuPrimeBuffer(file, (uint)width, (uint)height, _stride, format);
            if (_modifier != LinuxDmabufResource.ImplicitModifier || buffer == null ||
                Client.Server.FramePresenter is not IWaylandGpuFramePresenter presenter || !presenter.CanImportGpuBuffer(buffer))
            {
                if (requestedId.HasValue)
                    throw Error(ZwpLinuxBufferParamsV1Error.InvalidWlBuffer, "The compositor could not import this Podish PRIME buffer.");
                await ZwpLinuxBufferParamsV1EventWriter.FailedAsync(Client, ObjectId);
                return;
            }

            uint id = requestedId ?? Client.AllocateServerObjectId();
            Client.Register(new WlBufferResource(Client, id, file, buffer, (flags & ZwpLinuxBufferParamsV1Flags.YInvert) != 0));
            file.Get();
            if (!requestedId.HasValue)
                await ZwpLinuxBufferParamsV1EventWriter.CreatedAsync(Client, ObjectId, id);
        }
        finally
        {
            file?.Close();
        }
    }

    private WaylandProtocolException Error(ZwpLinuxBufferParamsV1Error code, string message) =>
        new(ObjectId, (uint)code, message);

    public override void Destroy()
    {
        _file?.Close();
        _file = null;
        base.Destroy();
    }
}
