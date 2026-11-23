namespace Podish.Wayland;

internal sealed class ForeignToplevelManagerResource : WaylandResource
{
    private readonly Dictionary<XdgToplevelResource, ForeignToplevelHandleResource> _handles = [];

    public ForeignToplevelManagerResource(WaylandClient client, uint id, uint version)
        : base(client, id, version, ZwlrForeignToplevelManagerV1Protocol.InterfaceName)
    {
    }

    public override IReadOnlyList<WaylandMessageMetadata> Requests => ZwlrForeignToplevelManagerV1Protocol.Requests;

    public async ValueTask AdvertiseAsync()
    {
        foreach (XdgToplevelResource toplevel in Client.Server.MappedToplevels)
            await UpdateAsync(toplevel);
    }

    public async ValueTask UpdateAsync(XdgToplevelResource toplevel)
    {
        if (!_handles.TryGetValue(toplevel, out ForeignToplevelHandleResource? handle))
        {
            handle = Client.Register(new ForeignToplevelHandleResource(Client, Client.AllocateServerObjectId(), Version, toplevel, this));
            _handles.Add(toplevel, handle);
            await ZwlrForeignToplevelManagerV1EventWriter.ToplevelAsync(Client, ObjectId, handle.ObjectId);
        }

        await handle.SyncAsync();
        await SyncChildrenAsync(toplevel);
    }

    public async ValueTask CloseToplevelAsync(XdgToplevelResource toplevel)
    {
        if (_handles.Remove(toplevel, out ForeignToplevelHandleResource? handle))
        {
            await handle.CloseAsync();
            await SyncChildrenAsync(toplevel);
        }
    }

    public uint GetHandleId(XdgToplevelResource? toplevel) =>
        toplevel != null && _handles.TryGetValue(toplevel, out ForeignToplevelHandleResource? handle) ? handle.ObjectId : 0;

    private async ValueTask SyncChildrenAsync(XdgToplevelResource parent)
    {
        foreach (ForeignToplevelHandleResource child in _handles.Values.Where(handle => ReferenceEquals(handle.Parent, parent)).ToArray())
            await child.SyncAsync();
    }

    public void Forget(XdgToplevelResource toplevel, ForeignToplevelHandleResource handle)
    {
        if (_handles.TryGetValue(toplevel, out ForeignToplevelHandleResource? existing) && ReferenceEquals(existing, handle))
            _handles.Remove(toplevel);
    }

    public override async ValueTask DispatchAsync(WaylandIncomingMessage message)
    {
        await ZwlrForeignToplevelManagerV1EventWriter.FinishedAsync(Client, ObjectId);
        foreach (ForeignToplevelHandleResource handle in _handles.Values)
            handle.StopUpdates();
        _handles.Clear();
        Destroy();
        await Client.DeleteIdAsync(ObjectId);
    }
}

internal sealed class ForeignToplevelHandleResource : WaylandResource
{
    private readonly ForeignToplevelManagerResource _manager;
    private XdgToplevelResource? _toplevel;
    private readonly Dictionary<uint, WlOutputResource> _outputs = [];
    private string? _title;
    private string? _appId;
    private uint? _parentId;
    private (bool Maximized, bool Minimized, bool Activated, bool Fullscreen)? _state;
    private bool _stopped;

    public ForeignToplevelHandleResource(WaylandClient client, uint id, uint version,
        XdgToplevelResource toplevel, ForeignToplevelManagerResource manager)
        : base(client, id, version, ZwlrForeignToplevelHandleV1Protocol.InterfaceName)
    {
        _toplevel = toplevel;
        _manager = manager;
    }

    public override IReadOnlyList<WaylandMessageMetadata> Requests => ZwlrForeignToplevelHandleV1Protocol.Requests;
    public XdgToplevelResource? Parent => _toplevel?.Parent;

    public async ValueTask SyncAsync()
    {
        if (_stopped || _toplevel is not { } toplevel)
            return;

        bool changed = false;
        if (_title != toplevel.Title)
        {
            _title = toplevel.Title;
            await ZwlrForeignToplevelHandleV1EventWriter.TitleAsync(Client, ObjectId, _title);
            changed = true;
        }

        if (_appId != toplevel.AppId)
        {
            _appId = toplevel.AppId;
            await ZwlrForeignToplevelHandleV1EventWriter.AppIdAsync(Client, ObjectId, _appId);
            changed = true;
        }

        foreach (WlOutputResource output in Client.Objects.All.OfType<WlOutputResource>().ToArray())
        {
            if (_outputs.TryGetValue(output.ObjectId, out WlOutputResource? existing) && ReferenceEquals(existing, output))
                continue;
            _outputs[output.ObjectId] = output;
            await ZwlrForeignToplevelHandleV1EventWriter.OutputEnterAsync(Client, ObjectId, output.ObjectId);
            changed = true;
        }

        var state = (toplevel.IsMaximized, toplevel.IsMinimized, toplevel.Activated, toplevel.IsFullscreen);
        if (_state != state)
        {
            _state = state;
            var encoded = new List<byte>(16);
            if (toplevel.IsMaximized)
                encoded.AddRange(BitConverter.GetBytes((uint)ZwlrForeignToplevelHandleV1State.Maximized));
            if (toplevel.IsMinimized)
                encoded.AddRange(BitConverter.GetBytes((uint)ZwlrForeignToplevelHandleV1State.Minimized));
            if (toplevel.Activated)
                encoded.AddRange(BitConverter.GetBytes((uint)ZwlrForeignToplevelHandleV1State.Activated));
            if (toplevel.IsFullscreen && Version >= 2)
                encoded.AddRange(BitConverter.GetBytes((uint)ZwlrForeignToplevelHandleV1State.Fullscreen));
            await ZwlrForeignToplevelHandleV1EventWriter.StateAsync(Client, ObjectId, [.. encoded]);
            changed = true;
        }

        if (Version >= 3)
        {
            uint parentId = _manager.GetHandleId(toplevel.Parent);
            if (_parentId != parentId)
            {
                _parentId = parentId;
                await ZwlrForeignToplevelHandleV1EventWriter.ParentAsync(Client, ObjectId, parentId);
                changed = true;
            }
        }

        if (changed)
            await ZwlrForeignToplevelHandleV1EventWriter.DoneAsync(Client, ObjectId);
    }

    public void StopUpdates() => _stopped = true;

    public async ValueTask CloseAsync()
    {
        _toplevel = null;
        if (!_stopped)
            await ZwlrForeignToplevelHandleV1EventWriter.ClosedAsync(Client, ObjectId);
    }

    public override async ValueTask DispatchAsync(WaylandIncomingMessage message)
    {
        if (message.Header.Opcode == 7)
        {
            if (_toplevel is { } tracked)
                _manager.Forget(tracked, this);
            Destroy();
            return;
        }

        if (_toplevel is not { Destroyed: false } toplevel || !toplevel.IsMapped)
            return;

        switch (message.Header.Opcode)
        {
            case 0:
            case 1:
                await toplevel.SetMaximizedAsync(message.Header.Opcode == 0);
                break;
            case 2:
            case 3:
                await toplevel.SetMinimizedAsync(message.Header.Opcode == 2);
                break;
            case 4:
            {
                var request = ZwlrForeignToplevelHandleV1Protocol.DecodeActivate(message.Body, message.Fds);
                Client.Objects.Require<WlSeatResource>(request.Seat);
                await toplevel.SetMinimizedAsync(false);
                await Client.Server.Focus.FocusKeyboardSurfaceAsync(toplevel.Surface.Surface.SceneSurfaceId);
                break;
            }
            case 5:
                await toplevel.SendCloseAsync();
                break;
            case 6:
            {
                var request = ZwlrForeignToplevelHandleV1Protocol.DecodeSetRectangle(message.Body, message.Fds);
                Client.Objects.Require<WlSurfaceResource>(request.Surface);
                if (request.Width < 0 || request.Height < 0)
                    throw new WaylandProtocolException(ObjectId, (uint)ZwlrForeignToplevelHandleV1Error.InvalidRectangle,
                        "Negative taskbar rectangle size.");
                break;
            }
            case 8:
            {
                var request = ZwlrForeignToplevelHandleV1Protocol.DecodeSetFullscreen(message.Body, message.Fds);
                if (request.Output != 0)
                    Client.Objects.Require<WlOutputResource>(request.Output);
                await toplevel.SetFullscreenAsync(true);
                break;
            }
            case 9:
                await toplevel.SetFullscreenAsync(false);
                break;
        }
    }
}
