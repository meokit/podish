using System.Collections.Concurrent;

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

    public int ReadFixed() => ReadInt();

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

    public void WriteFixed(int value) => WriteInt(value);

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

        List<LinuxFile> fds = [.. _pendingFds];
        _pendingFds.Clear();
        return new WaylandIncomingMessage(header, bodyLength == 0 ? Array.Empty<byte>() : _body[..bodyLength].ToArray(), fds);
    }

    public ValueTask<int> SendAsync(WaylandOutgoingMessage message)
    {
        return _connection.SendMsgAsync(message.Buffer, message.Fds?.ToList());
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
    public WaylandServer()
    {
        Globals = new WaylandGlobalRegistry();
        RegisterCoreGlobals();
    }

    public WaylandGlobalRegistry Globals { get; }

    public WaylandClient CreateClient(Func<WaylandOutgoingMessage, ValueTask<int>> sendAsync)
    {
        return new WaylandClient(this, sendAsync);
    }

    private void RegisterCoreGlobals()
    {
        Globals.Add(WlCompositorProtocol.InterfaceName, WlCompositorProtocol.Version,
            static (client, objectId, version) => client.Register(new WlCompositorResource(client, objectId, version)));
        Globals.Add(WlShmProtocol.InterfaceName, WlShmProtocol.Version,
            static (client, objectId, version) => client.Register(new WlShmResource(client, objectId, version)));
        Globals.Add(XdgWmBaseProtocol.InterfaceName, XdgWmBaseProtocol.Version,
            static (client, objectId, version) => client.Register(new XdgWmBaseResource(client, objectId, version)));
    }
}

public sealed class WaylandClient
{
    private readonly Func<WaylandOutgoingMessage, ValueTask<int>> _sendAsync;
    private readonly WaylandDispatcher _dispatcher;
    private uint _nextSerial = 1;

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
