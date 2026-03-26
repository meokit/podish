using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Podish.Wayland;

public enum WaylandArgKind
{
    Int,
    Uint,
    Fixed,
    String,
    Object,
    NewId,
    Array,
    Fd
}

public sealed record WaylandArgumentMetadata(string Name, WaylandArgKind Kind, string? Interface = null,
    bool AllowNull = false);

public sealed record WaylandMessageMetadata(string Interface, string Name, ushort Opcode, uint Since,
    IReadOnlyList<WaylandArgumentMetadata> Arguments);

public readonly record struct WaylandMessageHeader(uint ObjectId, ushort Opcode, ushort Size)
{
    public const int SizeInBytes = 8;

    public static WaylandMessageHeader Decode(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < SizeInBytes)
            throw new InvalidDataException($"Wayland header requires {SizeInBytes} bytes, got {buffer.Length}.");

        uint objectId = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        uint word = BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..8]);
        return new WaylandMessageHeader(objectId, (ushort)(word & 0xffff), (ushort)(word >> 16));
    }

    public void Encode(Span<byte> buffer)
    {
        if (buffer.Length < SizeInBytes)
            throw new InvalidDataException($"Wayland header requires {SizeInBytes} bytes, got {buffer.Length}.");

        BinaryPrimitives.WriteUInt32LittleEndian(buffer, ObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[4..8], ((uint)Size << 16) | Opcode);
    }
}

public sealed record WaylandIncomingMessage(WaylandMessageHeader Header, byte[] Body, IReadOnlyList<LinuxFile> Fds);

public sealed record WaylandOutgoingMessage(byte[] Buffer, IReadOnlyList<LinuxFile>? Fds = null);

public sealed class WaylandProtocolException : Exception
{
    public WaylandProtocolException(uint objectId, uint errorCode, string message)
        : base(message)
    {
        ObjectId = objectId;
        ErrorCode = errorCode;
    }

    public uint ObjectId { get; }
    public uint ErrorCode { get; }
}

public sealed class WaylandWireReader
{
    private readonly byte[] _buffer;
    private readonly Queue<LinuxFile> _fds;
    private int _offset;

    public WaylandWireReader(byte[] buffer, IReadOnlyList<LinuxFile>? fds = null)
    {
        _buffer = buffer;
        _fds = new Queue<LinuxFile>(fds ?? Array.Empty<LinuxFile>());
    }

    public int RemainingBytes => _buffer.Length - _offset;

    public int ReadInt()
    {
        Ensure(4);
        int value = BinaryPrimitives.ReadInt32LittleEndian(_buffer.AsSpan(_offset, 4));
        _offset += 4;
        return value;
    }

    public uint ReadUInt()
    {
        Ensure(4);
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.AsSpan(_offset, 4));
        _offset += 4;
        return value;
    }

    public int ReadFixed() => ReadInt() >> 8;

    public uint ReadObjectId() => ReadUInt();

    public uint ReadNewId() => ReadUInt();

    public string? ReadString()
    {
        uint length = ReadUInt();
        if (length == 0)
            return null;

        int byteCount = checked((int)length);
        Ensure(byteCount);
        var payload = _buffer.AsSpan(_offset, byteCount);
        _offset += Align4(byteCount);

        int terminator = payload.IndexOf((byte)0);
        if (terminator < 0)
            terminator = payload.Length;
        return System.Text.Encoding.UTF8.GetString(payload[..terminator]);
    }

    public byte[] ReadArray()
    {
        uint length = ReadUInt();
        int byteCount = checked((int)length);
        Ensure(byteCount);
        byte[] payload = _buffer.AsSpan(_offset, byteCount).ToArray();
        _offset += Align4(byteCount);
        return payload;
    }

    public LinuxFile ReadFd()
    {
        if (!_fds.TryDequeue(out LinuxFile? fd))
            throw new WaylandProtocolException(1, 1, "Wayland request is missing required file descriptor.");
        return fd;
    }

    public void EnsureExhausted()
    {
        if (RemainingBytes != 0)
            throw new WaylandProtocolException(1, 1, $"Wayland request has {RemainingBytes} unread bytes.");
    }

    private void Ensure(int bytes)
    {
        if (bytes < 0 || _offset + bytes > _buffer.Length)
            throw new WaylandProtocolException(1, 1, "Wayland request body is truncated.");
    }

    private static int Align4(int length) => (length + 3) & ~3;
}

public sealed class WaylandWireWriter
{
    private readonly MemoryStream _stream = new();
    private readonly List<LinuxFile> _fds = [];

    public void WriteInt(int value)
    {
        Span<byte> tmp = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(tmp, value);
        _stream.Write(tmp);
    }

    public void WriteUInt(uint value)
    {
        Span<byte> tmp = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(tmp, value);
        _stream.Write(tmp);
    }

    public void WriteFixed(int value) => WriteInt(checked(value << 8));

    public void WriteObjectId(uint value) => WriteUInt(value);

    public void WriteNewId(uint value) => WriteUInt(value);

    public void WriteString(string? value)
    {
        if (value == null)
        {
            WriteUInt(0);
            return;
        }

        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(value);
        int length = utf8.Length + 1;
        WriteUInt((uint)length);
        _stream.Write(utf8);
        _stream.WriteByte(0);
        WritePadding(length);
    }

    public void WriteArray(byte[] value)
    {
        WriteUInt((uint)value.Length);
        _stream.Write(value);
        WritePadding(value.Length);
    }

    public void WriteFd(LinuxFile fd)
    {
        _fds.Add(fd);
    }

    public WaylandOutgoingMessage ToOutgoingMessage(uint objectId, ushort opcode)
    {
        byte[] body = _stream.ToArray();
        ushort size = checked((ushort)(WaylandMessageHeader.SizeInBytes + body.Length));
        byte[] buffer = new byte[size];
        new WaylandMessageHeader(objectId, opcode, size).Encode(buffer);
        Buffer.BlockCopy(body, 0, buffer, WaylandMessageHeader.SizeInBytes, body.Length);
        return new WaylandOutgoingMessage(buffer, _fds.Count == 0 ? null : new ReadOnlyCollection<LinuxFile>(_fds));
    }

    private void WritePadding(int length)
    {
        int pad = ((length + 3) & ~3) - length;
        for (var i = 0; i < pad; i++)
            _stream.WriteByte(0);
    }
}

public sealed class WaylandConnection
{
    private readonly VirtualDaemonConnection _connection;
    private readonly byte[] _headerBuffer = new byte[WaylandMessageHeader.SizeInBytes];
    private byte[] _scratch = new byte[4096];
    private byte[] _body = Array.Empty<byte>();
    private readonly List<LinuxFile> _pendingFds = [];

    public WaylandConnection(VirtualDaemonConnection connection)
    {
        _connection = connection;
    }

    public VirtualDaemonConnection RawConnection => _connection;

    public async ValueTask<WaylandIncomingMessage?> ReceiveAsync()
    {
        int headerRead = await ReadExactAsync(_headerBuffer, _headerBuffer.Length);
        if (headerRead == 0)
            return null;
        if (headerRead != _headerBuffer.Length)
            throw new InvalidDataException("Unexpected EOF while reading Wayland header.");

        WaylandMessageHeader header = WaylandMessageHeader.Decode(_headerBuffer);
        if (header.Size < WaylandMessageHeader.SizeInBytes)
            throw new WaylandProtocolException(header.ObjectId, 1, "Wayland message size is smaller than the header.");

        int bodyLength = header.Size - WaylandMessageHeader.SizeInBytes;
        EnsureBodyCapacity(bodyLength);
        if (bodyLength > 0)
        {
            int bodyRead = await ReadExactAsync(_body, bodyLength);
            if (bodyRead != bodyLength)
                throw new InvalidDataException("Unexpected EOF while reading Wayland body.");
        }

        return new WaylandIncomingMessage(header, bodyLength == 0 ? Array.Empty<byte>() : _body[..bodyLength].ToArray(),
            Array.Empty<LinuxFile>());
    }

    public ValueTask<int> SendAsync(WaylandOutgoingMessage message)
    {
        return _connection.SendMsgAsync(message.Buffer, message.Fds?.ToList());
    }

    public IReadOnlyList<LinuxFile> TakePendingFds(int count)
    {
        if (count <= 0)
            return Array.Empty<LinuxFile>();

        int take = Math.Min(count, _pendingFds.Count);
        if (take == 0)
            return Array.Empty<LinuxFile>();

        List<LinuxFile> fds = _pendingFds.GetRange(0, take);
        _pendingFds.RemoveRange(0, take);
        return fds;
    }

    private async ValueTask<int> ReadExactAsync(byte[] buffer, int needed)
    {
        int total = 0;
        while (total < needed)
        {
            int requested = needed - total;
            EnsureScratchCapacity(requested);
            RecvMessageResult recv = await _connection.RecvMsgAsync(_scratch, 0, requested);
            if (recv.Fds is { Count: > 0 })
                _pendingFds.AddRange(recv.Fds);

            if (recv.BytesRead == 0)
                return total;
            if (recv.BytesRead < 0)
                throw new IOException($"Wayland socket read failed rc={recv.BytesRead}.");

            Buffer.BlockCopy(_scratch, 0, buffer, total, recv.BytesRead);
            total += recv.BytesRead;
        }

        return total;
    }

    private void EnsureScratchCapacity(int size)
    {
        if (_scratch.Length < size)
            _scratch = new byte[Math.Max(size, _scratch.Length * 2)];
    }

    private void EnsureBodyCapacity(int size)
    {
        if (_body.Length < size)
            _body = new byte[Math.Max(size, _body.Length * 2 + 64)];
    }
}

public sealed class WaylandObjectTable
{
    private readonly Dictionary<uint, WaylandResource> _resources = [];

    public IEnumerable<WaylandResource> All => _resources.Values;

    public void Register(WaylandResource resource)
    {
        if (!_resources.TryAdd(resource.ObjectId, resource))
            throw new WaylandProtocolException(resource.ObjectId, 0, $"Wayland object id {resource.ObjectId} is already in use.");
    }

    public T Require<T>(uint id) where T : WaylandResource
    {
        if (!_resources.TryGetValue(id, out WaylandResource? resource))
            throw new WaylandProtocolException(id, 0, $"Wayland object {id} does not exist.");
        if (resource is not T typed)
            throw new WaylandProtocolException(id, 0, $"Wayland object {id} is a {resource.InterfaceName}, not {typeof(T).Name}.");
        return typed;
    }

    public bool TryGetValue(uint id, out WaylandResource? resource)
    {
        return _resources.TryGetValue(id, out resource);
    }

    public WaylandResource Require(uint id)
    {
        if (_resources.TryGetValue(id, out WaylandResource? resource))
            return resource;
        throw new WaylandProtocolException(id, 0, $"Wayland object {id} does not exist.");
    }

    public bool TryGet(uint id, out WaylandResource? resource) => _resources.TryGetValue(id, out resource);

    public void Remove(uint id)
    {
        _resources.Remove(id);
    }
}

public sealed class WaylandGlobal
{
    public required uint Name { get; init; }
    public required string Interface { get; init; }
    public required uint Version { get; init; }
    public required Func<WaylandClient, uint, uint, WaylandResource> Bind { get; init; }
}

public sealed class WaylandGlobalRegistry
{
    private readonly List<WaylandGlobal> _globals = [];
    private uint _nextName = 1;

    public IReadOnlyList<WaylandGlobal> Globals => _globals;

    public WaylandGlobal Add(string @interface, uint version, Func<WaylandClient, uint, uint, WaylandResource> bind)
    {
        var global = new WaylandGlobal
        {
            Name = _nextName++,
            Interface = @interface,
            Version = version,
            Bind = bind
        };
        _globals.Add(global);
        return global;
    }

    public WaylandGlobal Require(uint name)
    {
        foreach (WaylandGlobal global in _globals)
        {
            if (global.Name == name)
                return global;
        }

        throw new WaylandProtocolException(1, 0, $"Unknown Wayland global {name}.");
    }
}

public abstract class WaylandResource
{
    protected WaylandResource(WaylandClient client, uint objectId, uint version, string interfaceName)
    {
        Client = client;
        ObjectId = objectId;
        Version = version;
        InterfaceName = interfaceName;
    }

    public WaylandClient Client { get; }
    public uint ObjectId { get; }
    public uint Version { get; }
    public string InterfaceName { get; }
    public bool Destroyed { get; private set; }

    public abstract IReadOnlyList<WaylandMessageMetadata> Requests { get; }

    public abstract ValueTask DispatchAsync(WaylandIncomingMessage message);

    public virtual void Destroy()
    {
        if (Destroyed)
            return;

        Destroyed = true;
        Client.Objects.Remove(ObjectId);
    }
}

public sealed class WaylandDispatcher
{
    private readonly WaylandClient _client;

    public WaylandDispatcher(WaylandClient client)
    {
        _client = client;
    }

    public async ValueTask DispatchAsync(WaylandIncomingMessage message)
    {
        WaylandResource resource = _client.Objects.Require(message.Header.ObjectId);
        if (resource.Destroyed)
            throw new WaylandProtocolException(message.Header.ObjectId, 0, $"Wayland object {message.Header.ObjectId} is already destroyed.");

        if (message.Header.Opcode >= resource.Requests.Count)
            throw new WaylandProtocolException(message.Header.ObjectId, 0,
                $"Unsupported opcode {message.Header.Opcode} for {resource.InterfaceName}.");

        WaylandMessageMetadata metadata = resource.Requests[message.Header.Opcode];
        if (resource.Version < metadata.Since)
            throw new WaylandProtocolException(message.Header.ObjectId, 0,
                $"Opcode {metadata.Name} requires version {metadata.Since}, but object is version {resource.Version}.");

        await resource.DispatchAsync(message);
    }
}

public sealed class WaylandServer
{
    public sealed record OutputInfo(int Width = 1024, int Height = 768, int Scale = 1, string Name = "WL-1",
        string Description = "Podish Virtual Output", string Make = "Podish", string Model = "Virtual Display");

    private readonly List<WaylandClient> _clients = [];
    private readonly List<WlCallbackResource> _pendingFrameCallbacks = [];
    private readonly Dictionary<ulong, WlSurfaceResource> _sceneSurfaces = [];
    private readonly WaylandKeyboardKeymap _keyboardKeymap = new();
    private WlDataSourceResource? _selectionSource;
    private ZwpPrimarySelectionSourceV1Resource? _primarySelectionSource;
    private uint _nextTextInputDoneSerial = 1;
    private bool _hostTextInputFocused = true;
    private long _nextDisplayLeaseToken;
    private long _nextSceneSurfaceId;

    public WaylandServer(IWaylandFramePresenter? framePresenter = null, OutputInfo? output = null)
    {
        FramePresenter = framePresenter;
        Output = output ?? new OutputInfo();
        Globals = new WaylandGlobalRegistry();
        Focus = new WaylandFocusManager(this);
        RegisterCoreGlobals();
    }

    public IWaylandFramePresenter? FramePresenter { get; private set; }
    public OutputInfo Output { get; }
    public WaylandGlobalRegistry Globals { get; }
    internal WaylandKeyboardKeymap KeyboardKeymap => _keyboardKeymap;
    internal WaylandFocusManager Focus { get; }
    internal IReadOnlyList<WaylandClient> Clients => _clients;

    public WaylandClient CreateClient(Func<WaylandOutgoingMessage, ValueTask<int>> sendAsync)
    {
        var client = new WaylandClient(this, sendAsync);
        _clients.Add(client);
        return client;
    }

    public async ValueTask DisconnectClientAsync(WaylandClient client)
    {
        if (!_clients.Remove(client))
            return;

        _pendingFrameCallbacks.RemoveAll(callback => ReferenceEquals(callback.Client, client));

        foreach (WlSurfaceResource surface in client.Objects.All.OfType<WlSurfaceResource>().ToList())
        {
            if (surface.IsCursorRole && FramePresenter is IWaylandCursorPresenter cursorPresenter)
                await cursorPresenter.UpdateCursorAsync(surface.SceneSurfaceId, null);
            else if (FramePresenter != null)
                await FramePresenter.PresentSurfaceAsync(surface.SceneSurfaceId, null);

            UnregisterSceneSurface(surface);
            await Focus.HandleSurfaceDestroyedAsync(surface.SceneSurfaceId);
        }
    }

    public void SetFramePresenter(IWaylandFramePresenter? framePresenter)
    {
        FramePresenter = framePresenter;
    }

    private void RegisterCoreGlobals()
    {
        Globals.Add(WlCompositorProtocol.InterfaceName, WlCompositorProtocol.Version,
            static (client, objectId, version) => client.Register(new WlCompositorResource(client, objectId, version)));
        Globals.Add(WlSubcompositorProtocol.InterfaceName, WlSubcompositorProtocol.Version,
            static (client, objectId, version) => client.Register(new WlSubcompositorResource(client, objectId, version)));
        Globals.Add(WlShmProtocol.InterfaceName, WlShmProtocol.Version,
            static (client, objectId, version) => client.Register(new WlShmResource(client, objectId, version)));
        Globals.Add(WlDataDeviceManagerProtocol.InterfaceName, WlDataDeviceManagerProtocol.Version,
            static (client, objectId, version) => client.Register(new WlDataDeviceManagerResource(client, objectId, version)));
        Globals.Add(ZwpPrimarySelectionDeviceManagerV1Protocol.InterfaceName, ZwpPrimarySelectionDeviceManagerV1Protocol.Version,
            static (client, objectId, version) => client.Register(new ZwpPrimarySelectionDeviceManagerV1Resource(client, objectId, version)));
        Globals.Add(ZwpTextInputManagerV3Protocol.InterfaceName, ZwpTextInputManagerV3Protocol.Version,
            static (client, objectId, version) => client.Register(new ZwpTextInputManagerV3Resource(client, objectId, version)));
        Globals.Add(WlSeatProtocol.InterfaceName, WlSeatProtocol.Version,
            static (client, objectId, version) => client.Register(new WlSeatResource(client, objectId, version)));
        Globals.Add(WlOutputProtocol.InterfaceName, WlOutputProtocol.Version,
            static (client, objectId, version) => client.Register(new WlOutputResource(client, objectId, version)));
        Globals.Add(XdgWmBaseProtocol.InterfaceName, XdgWmBaseProtocol.Version,
            static (client, objectId, version) => client.Register(new XdgWmBaseResource(client, objectId, version)));
        Globals.Add(WpCursorShapeManagerV1Protocol.InterfaceName, WpCursorShapeManagerV1Protocol.Version,
            static (client, objectId, version) => client.Register(new WpCursorShapeManagerV1Resource(client, objectId, version)));
        Globals.Add(ZxdgDecorationManagerV1Protocol.InterfaceName, ZxdgDecorationManagerV1Protocol.Version,
            static (client, objectId, version) => client.Register(new ZxdgDecorationManagerV1Resource(client, objectId, version)));
    }

    public async ValueTask HandlePointerMotionAsync(int desktopX, int desktopY, uint time)
    {
        await Focus.HandlePointerMotionAsync(desktopX, desktopY, time);
    }

    public async ValueTask HandlePointerButtonAsync(uint button, bool pressed, uint time)
    {
        await Focus.HandlePointerButtonAsync(button, pressed, time);
    }

    public async ValueTask ClearPointerFocusAsync()
    {
        await Focus.ClearPointerFocusAsync();
    }

    public async ValueTask HandleKeyboardKeyAsync(uint key, bool pressed, uint time)
    {
        if (ShouldSuppressKeyboardKey(key))
            return;

        await Focus.HandleKeyboardKeyAsync(key, pressed, time);
    }

    public async ValueTask HandleTextInputCommitAsync(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        ZwpTextInputV3Resource? textInput = GetFocusedEnabledTextInput();
        if (textInput == null)
            return;

        textInput.SetHasActivePreedit(false);
        await textInput.SendCommitStringAsync(text, NextTextInputDoneSerial());
    }

    public async ValueTask HandleTextInputPreeditAsync(string text, int cursorBegin, int cursorEnd)
    {
        ZwpTextInputV3Resource? textInput = GetFocusedEnabledTextInput();
        if (textInput == null)
            return;

        textInput.SetHasActivePreedit(!string.IsNullOrEmpty(text));
        await textInput.SendPreeditStringAsync(text, cursorBegin, cursorEnd, NextTextInputDoneSerial());
    }

    internal async ValueTask SetClipboardSelectionAsync(WlDataSourceResource? source)
    {
        if (ReferenceEquals(_selectionSource, source))
            return;

        WlDataSourceResource? previous = _selectionSource;
        _selectionSource = source;

        if (previous is { Destroyed: false } && !ReferenceEquals(previous, source))
            await previous.SendCancelledAsync();

        await BroadcastClipboardSelectionAsync();
    }

    internal async ValueTask SetPrimarySelectionAsync(ZwpPrimarySelectionSourceV1Resource? source)
    {
        if (ReferenceEquals(_primarySelectionSource, source))
            return;

        ZwpPrimarySelectionSourceV1Resource? previous = _primarySelectionSource;
        _primarySelectionSource = source;

        if (previous is { Destroyed: false } && !ReferenceEquals(previous, source))
            await previous.SendCancelledAsync();

        await BroadcastPrimarySelectionAsync();
    }

    internal ValueTask SendClipboardSelectionAsync(WlDataDeviceResource device)
    {
        return device.SendSelectionAsync(ReferenceEquals(device.Client, GetFocusedKeyboardClient()) ? _selectionSource : null);
    }

    internal ValueTask SendPrimarySelectionAsync(ZwpPrimarySelectionDeviceV1Resource device)
    {
        return device.SendSelectionAsync(ReferenceEquals(device.Client, GetFocusedKeyboardClient()) ? _primarySelectionSource : null);
    }

    internal async ValueTask HandleKeyboardFocusSelectionChangedAsync()
    {
        await BroadcastClipboardSelectionAsync();
        await BroadcastPrimarySelectionAsync();
    }

    internal async ValueTask HandleKeyboardFocusTextInputChangedAsync()
    {
        WaylandClient? focusedClient = GetFocusedKeyboardClient();
        uint? focusedSurfaceId = GetFocusedKeyboardSurfaceObjectId();

        foreach (WaylandClient client in _clients.ToArray())
        {
            ZwpTextInputV3Resource[] textInputs = [.. client.Objects.All.OfType<ZwpTextInputV3Resource>()];
            foreach (ZwpTextInputV3Resource textInput in textInputs)
            {
                if (ReferenceEquals(client, focusedClient) && focusedSurfaceId is uint surfaceId)
                    await textInput.SetFocusAsync(surfaceId);
                else
                    await textInput.ClearFocusAsync();
            }
        }
    }

    public async ValueTask HandleHostTextInputFocusLostAsync()
    {
        _hostTextInputFocused = false;

        WaylandClient[] clients = [.. _clients];
        foreach (WaylandClient client in clients)
        {
            ZwpTextInputV3Resource[] textInputs = [.. client.Objects.All.OfType<ZwpTextInputV3Resource>()];
            foreach (ZwpTextInputV3Resource textInput in textInputs)
                await textInput.ClearFocusAsync();
        }
    }

    public async ValueTask HandleHostTextInputFocusGainedAsync()
    {
        _hostTextInputFocused = true;
        await HandleKeyboardFocusTextInputChangedAsync();
    }

    internal async ValueTask HandleTextInputStateChangedAsync()
    {
        if (FramePresenter is not IWaylandTextInputPresenter textInputPresenter)
            return;

        ZwpTextInputV3Resource? textInput = _hostTextInputFocused ? GetFocusedEnabledTextInput() : null;
        if (textInput == null)
        {
            await textInputPresenter.UpdateTextInputAsync(false, null);
            return;
        }

        await textInputPresenter.UpdateTextInputAsync(true, textInput.CursorRectangle);
    }

    internal void HandleClipboardSourceDestroyed(WlDataSourceResource source)
    {
        if (!ReferenceEquals(_selectionSource, source))
            return;

        _selectionSource = null;
        BroadcastClipboardSelectionAsync().AsTask().GetAwaiter().GetResult();
    }

    internal void HandlePrimarySelectionSourceDestroyed(ZwpPrimarySelectionSourceV1Resource source)
    {
        if (!ReferenceEquals(_primarySelectionSource, source))
            return;

        _primarySelectionSource = null;
        BroadcastPrimarySelectionAsync().AsTask().GetAwaiter().GetResult();
    }

    internal ulong AllocateSceneSurfaceId()
    {
        return unchecked((ulong)Interlocked.Increment(ref _nextSceneSurfaceId));
    }

    internal ulong AllocateDisplayLeaseToken()
    {
        return unchecked((ulong)Interlocked.Increment(ref _nextDisplayLeaseToken));
    }

    internal void RegisterSceneSurface(WlSurfaceResource surface)
    {
        _sceneSurfaces.Add(surface.SceneSurfaceId, surface);
    }

    internal void UnregisterSceneSurface(WlSurfaceResource surface)
    {
        _sceneSurfaces.Remove(surface.SceneSurfaceId);
    }

    internal bool TryGetSceneSurface(ulong sceneSurfaceId, [NotNullWhen(true)] out WlSurfaceResource? surface)
    {
        return _sceneSurfaces.TryGetValue(sceneSurfaceId, out surface);
    }

    private async ValueTask BroadcastClipboardSelectionAsync()
    {
        WaylandClient[] clients = [.. _clients];

        foreach (WaylandClient client in clients)
        {
            WlDataDeviceResource[] devices = [.. client.Objects.All.OfType<WlDataDeviceResource>()];

            foreach (WlDataDeviceResource device in devices)
                await SendClipboardSelectionAsync(device);
        }
    }

    private async ValueTask BroadcastPrimarySelectionAsync()
    {
        WaylandClient[] clients = [.. _clients];

        foreach (WaylandClient client in clients)
        {
            ZwpPrimarySelectionDeviceV1Resource[] devices = [.. client.Objects.All.OfType<ZwpPrimarySelectionDeviceV1Resource>()];

            foreach (ZwpPrimarySelectionDeviceV1Resource device in devices)
                await SendPrimarySelectionAsync(device);
        }
    }

    private WaylandClient? GetFocusedKeyboardClient()
    {
        if (Focus.FocusedKeyboardSceneSurfaceId is not ulong sceneSurfaceId)
            return null;

        if (!TryGetSceneSurface(sceneSurfaceId, out WlSurfaceResource? surface))
            return null;

        return surface.Client;
    }

    private uint? GetFocusedKeyboardSurfaceObjectId()
    {
        if (Focus.FocusedKeyboardSceneSurfaceId is not ulong sceneSurfaceId)
            return null;

        return TryGetSceneSurface(sceneSurfaceId, out WlSurfaceResource? surface) ? surface.ObjectId : null;
    }

    private ZwpTextInputV3Resource? GetFocusedEnabledTextInput()
    {
        WaylandClient? focusedClient = GetFocusedKeyboardClient();
        if (focusedClient == null)
            return null;

        return focusedClient.Objects.All.OfType<ZwpTextInputV3Resource>()
            .FirstOrDefault(static textInput => textInput.IsFocused && textInput.IsEnabled);
    }

    private uint NextTextInputDoneSerial()
    {
        return _nextTextInputDoneSerial++;
    }

    internal uint NextTextInputDoneSerialForResource()
    {
        return NextTextInputDoneSerial();
    }

    private bool ShouldSuppressKeyboardKey(uint key)
    {
        ZwpTextInputV3Resource? textInput = GetFocusedEnabledTextInput();
        if (textInput is not { HasActivePreedit: true })
            return false;

        if (WaylandKeyboardLayout.TryGetByEvdevKey(key, out WaylandKeyboardKeyDescriptor descriptor) &&
            descriptor.ModifierRole != WaylandKeyboardModifierRole.None)
            return false;

        return true;
    }

    internal void EnqueueFrameCallbacks(IEnumerable<WlCallbackResource> callbacks)
    {
        foreach (WlCallbackResource callback in callbacks)
        {
            if (!callback.Destroyed)
                _pendingFrameCallbacks.Add(callback);
        }
    }

    public async ValueTask HandlePresentationTickAsync(uint timeMs)
    {
        if (_pendingFrameCallbacks.Count == 0)
            return;

        List<WlCallbackResource> callbacks = [.. _pendingFrameCallbacks];
        _pendingFrameCallbacks.Clear();

        foreach (WlCallbackResource callback in callbacks)
        {
            if (callback.Destroyed)
                continue;

            await callback.SendDoneAndDisposeAsync(timeMs);
        }
    }

    public async ValueTask HandleBufferConsumedAsync(ulong leaseToken)
    {
        foreach (WaylandClient client in _clients)
        {
            foreach (WlBufferResource buffer in client.Objects.All.OfType<WlBufferResource>())
            {
                if (!buffer.HasDisplayLease(leaseToken))
                    continue;

                await buffer.CompleteDisplayLeaseAsync(leaseToken);
                return;
            }
        }
    }
}

public sealed class WaylandClient
{
    private readonly Func<WaylandOutgoingMessage, ValueTask<int>> _sendAsync;
    private readonly WaylandDispatcher _dispatcher;
    private uint _nextSerial = 1;
    private uint _nextServerObjectId = 0xfeffffff;

    public WaylandClient(WaylandServer server, Func<WaylandOutgoingMessage, ValueTask<int>> sendAsync)
    {
        Server = server;
        _sendAsync = sendAsync;
        Objects = new WaylandObjectTable();
        _dispatcher = new WaylandDispatcher(this);
        Register(new WlDisplayResource(this, 1, WlDisplayProtocol.Version));
    }

    public WaylandServer Server { get; }
    public WaylandObjectTable Objects { get; }

    public uint NextSerial() => _nextSerial++;

    public uint AllocateServerObjectId()
    {
        while (_nextServerObjectId < uint.MaxValue)
        {
            uint candidate = ++_nextServerObjectId;
            if (!Objects.TryGet(candidate, out _))
                return candidate;
        }

        throw new InvalidOperationException("No free server-side Wayland object ids remain.");
    }

    public T Register<T>(T resource) where T : WaylandResource
    {
        Objects.Register(resource);
        return resource;
    }

    public async ValueTask ProcessMessageAsync(WaylandIncomingMessage message)
    {
        try
        {
            await _dispatcher.DispatchAsync(message);
        }
        catch (WaylandProtocolException)
        {
            throw;
        }
        finally
        {
            foreach (LinuxFile fd in message.Fds)
            {
                if (!Objects.All.OfType<WlShmPoolResource>().Any(pool => pool.Owns(fd)))
                    fd.Close();
            }
        }
    }

    public int GetExpectedFdCount(WaylandMessageHeader header)
    {
        if (!Objects.TryGetValue(header.ObjectId, out WaylandResource? resource) || resource is null)
            return 0;
        if (resource.Destroyed || header.Opcode >= resource.Requests.Count)
            return 0;

        return resource.Requests[header.Opcode].Arguments.Count(static arg => arg.Kind == WaylandArgKind.Fd);
    }

    public async ValueTask SendEventAsync(uint objectId, ushort opcode, Action<WaylandWireWriter> writePayload,
        List<LinuxFile>? fds = null)
    {
        var writer = new WaylandWireWriter();
        writePayload(writer);
        WaylandOutgoingMessage outgoing = writer.ToOutgoingMessage(objectId, opcode);
        if (fds is { Count: > 0 })
            outgoing = outgoing with { Fds = fds };
        int sent = await _sendAsync(outgoing);
        if (sent <= 0)
            throw new IOException("Wayland socket closed while sending event.");
    }

    public async ValueTask SendRawAsync(WaylandOutgoingMessage outgoing)
    {
        int sent = await _sendAsync(outgoing);
        if (sent <= 0)
            throw new IOException("Wayland socket closed while sending message.");
    }

    public async ValueTask SendProtocolErrorAsync(uint objectId, uint code, string message)
    {
        await WlDisplayEventWriter.ErrorAsync(this, 1, objectId, code, message);
    }

    public ValueTask DeleteIdAsync(uint id)
    {
        return WlDisplayEventWriter.DeleteIdAsync(this, 1, id);
    }
}
