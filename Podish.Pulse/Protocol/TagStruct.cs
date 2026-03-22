using System.Text;

namespace Podish.Pulse.Protocol;

/// <summary>
/// A streaming reader for tagstruct-encoded data.
/// </summary>
public sealed class TagStructReader
{
    private readonly byte[] _buffer;
    private readonly int _start;
    private readonly int _end;
    internal int _position;
    private readonly ushort _protocolVersion;

    /// <summary>
    /// Creates a tagstruct reader from a byte array.
    /// </summary>
    /// <param name="buffer">The buffer to read from.</param>
    /// <param name="protocolVersion">The protocol version to use for parsing.</param>
    public TagStructReader(byte[] buffer, ushort protocolVersion)
        : this(buffer, 0, buffer.Length, protocolVersion)
    {
    }

    public TagStructReader(byte[] buffer, int offset, int length, ushort protocolVersion)
    {
        _buffer = buffer;
        _start = offset;
        _position = offset;
        _end = offset + length;
        _protocolVersion = protocolVersion;
    }

    /// <summary>
    /// Gets the current position in the buffer.
    /// </summary>
    public int Position => _position;

    /// <summary>
    /// Gets the protocol version being used.
    /// </summary>
    public ushort ProtocolVersion => _protocolVersion;

    /// <summary>
    /// Gets the remaining bytes in the buffer.
    /// </summary>
    public byte[] Remaining
    {
        get
        {
            byte[] result = new byte[_end - _position];
            Array.Copy(_buffer, _position, result, 0, result.Length);
            return result;
        }
    }

    /// <summary>
    /// Reads a tag from the buffer.
    /// </summary>
    /// <returns>The tag read from the buffer.</returns>
    /// <exception cref="InvalidProtocolMessageException">Thrown when an invalid tag is encountered.</exception>
    public Tag ReadTag()
    {
        EnsureBytes(1);
        byte value = _buffer[_position++];
        if (Enum.IsDefined(typeof(Tag), value))
            return (Tag)value;
        throw new InvalidProtocolMessageException($"Invalid tag 0x{value:X2} in tagstruct");
    }

    public Tag PeekTag()
    {
        EnsureBytes(1);
        byte value = _buffer[_position];
        if (Enum.IsDefined(typeof(Tag), value))
            return (Tag)value;
        throw new InvalidProtocolMessageException($"Invalid tag 0x{value:X2} in tagstruct");
    }

    /// <summary>
    /// Expects a specific tag and throws if it doesn't match.
    /// </summary>
    /// <param name="expected">The expected tag.</param>
    /// <exception cref="InvalidProtocolMessageException">Thrown when the tag doesn't match.</exception>
    public void ExpectTag(Tag expected)
    {
        Tag actual = ReadTag();
        if (actual != expected)
            throw new InvalidProtocolMessageException($"Expected {expected}, got {actual}");
    }

    /// <summary>
    /// Reads a single byte.
    /// </summary>
    /// <returns>The byte value.</returns>
    public byte ReadU8()
    {
        ExpectTag(Tag.U8);
        EnsureBytes(1);
        return _buffer[_position++];
    }

    /// <summary>
    /// Reads an unsigned 32-bit integer (big-endian).
    /// </summary>
    /// <returns>The uint value.</returns>
    public uint ReadU32()
    {
        ExpectTag(Tag.U32);
        EnsureBytes(4);
        uint value = (uint)(_buffer[_position] << 24 | _buffer[_position + 1] << 16 | 
                            _buffer[_position + 2] << 8 | _buffer[_position + 3]);
        _position += 4;
        return value;
    }

    /// <summary>
    /// Reads an unsigned 64-bit integer (big-endian).
    /// </summary>
    /// <returns>The ulong value.</returns>
    public ulong ReadU64()
    {
        ExpectTag(Tag.U64);
        EnsureBytes(8);
        ulong value = ((ulong)_buffer[_position] << 56) | ((ulong)_buffer[_position + 1] << 48) |
                      ((ulong)_buffer[_position + 2] << 40) | ((ulong)_buffer[_position + 3] << 32) |
                      ((ulong)_buffer[_position + 4] << 24) | ((ulong)_buffer[_position + 5] << 16) |
                      ((ulong)_buffer[_position + 6] << 8) | _buffer[_position + 7];
        _position += 8;
        return value;
    }

    /// <summary>
    /// Reads a signed 64-bit integer (big-endian).
    /// </summary>
    /// <returns>The long value.</returns>
    public long ReadI64()
    {
        ExpectTag(Tag.S64);
        EnsureBytes(8);
        ulong value = ((ulong)_buffer[_position] << 56) | ((ulong)_buffer[_position + 1] << 48) |
                      ((ulong)_buffer[_position + 2] << 40) | ((ulong)_buffer[_position + 3] << 32) |
                      ((ulong)_buffer[_position + 4] << 24) | ((ulong)_buffer[_position + 5] << 16) |
                      ((ulong)_buffer[_position + 6] << 8) | _buffer[_position + 7];
        _position += 8;
        return (long)value;
    }

    /// <summary>
    /// Reads a boolean value.
    /// </summary>
    /// <returns>The boolean value.</returns>
    public bool ReadBool()
    {
        Tag tag = ReadTag();
        return tag switch
        {
            Tag.BooleanTrue => true,
            Tag.BooleanFalse => false,
            _ => throw new InvalidProtocolMessageException($"Expected boolean, got {tag}"),
        };
    }

    /// <summary>
    /// Reads a "usec" value, which is a 64-bit unsigned integer representing a number of microseconds.
    /// </summary>
    /// <returns>The microseconds value.</returns>
    public ulong ReadUsec()
    {
        ExpectTag(Tag.Usec);
        EnsureBytes(8);
        ulong value = ((ulong)_buffer[_position] << 56) | ((ulong)_buffer[_position + 1] << 48) |
                      ((ulong)_buffer[_position + 2] << 40) | ((ulong)_buffer[_position + 3] << 32) |
                      ((ulong)_buffer[_position + 4] << 24) | ((ulong)_buffer[_position + 5] << 16) |
                      ((ulong)_buffer[_position + 6] << 8) | _buffer[_position + 7];
        _position += 8;
        return value;
    }

    /// <summary>
    /// Reads a timestamp with microsecond precision.
    /// </summary>
    /// <returns>The timestamp.</returns>
    public DateTimeOffset ReadTimeVal()
    {
        ExpectTag(Tag.TimeVal);
        EnsureBytes(8);
        uint secs = (uint)(_buffer[_position] << 24 | _buffer[_position + 1] << 16 |
                          _buffer[_position + 2] << 8 | _buffer[_position + 3]);
        _position += 4;
        uint usecs = (uint)(_buffer[_position] << 24 | _buffer[_position + 1] << 16 |
                           _buffer[_position + 2] << 8 | _buffer[_position + 3]);
        _position += 4;
        return DateTimeOffset.FromUnixTimeSeconds(secs).Add(TimeSpan.FromMicroseconds(usecs));
    }

    /// <summary>
    /// Reads an "arbitrary" byte blob with length prefix.
    /// </summary>
    /// <returns>The byte array.</returns>
    public byte[] ReadArbitrary()
    {
        ExpectTag(Tag.Arbitrary);
        uint len = ReadU32Raw();
        if (len > Constants.MaxPropSize)
            throw new InvalidProtocolMessageException($"Arbitrary data too large: {len} bytes");
        EnsureBytes((int)len);
        byte[] result = new byte[len];
        Array.Copy(_buffer, _position, result, 0, len);
        _position += (int)len;
        return result;
    }

    private uint ReadU32Raw()
    {
        EnsureBytes(4);
        uint value = (uint)(_buffer[_position] << 24 | _buffer[_position + 1] << 16 |
                            _buffer[_position + 2] << 8 | _buffer[_position + 3]);
        _position += 4;
        return value;
    }

    /// <summary>
    /// Reads a null-terminated string (which may be a special null string tag).
    /// </summary>
    /// <returns>The string, or null if the null string tag was encountered.</returns>
    public string? ReadString()
    {
        Tag tag = ReadTag();
        return tag switch
        {
            Tag.String => ReadNullTerminatedString(),
            Tag.StringNull => null,
            _ => throw new InvalidProtocolMessageException($"Expected string or null string, got {tag}"),
        };
    }

    /// <summary>
    /// Reads a non-null null-terminated string.
    /// </summary>
    /// <returns>The string.</returns>
    /// <exception cref="InvalidProtocolMessageException">Thrown when a null string is encountered.</exception>
    public string ReadStringNonNull()
    {
        ExpectTag(Tag.String);
        return ReadNullTerminatedString();
    }

    /// <summary>
    /// Reads a u32 and checks it against PA_INVALID_INDEX (-1).
    /// </summary>
    /// <returns>The index, or null if invalid.</returns>
    public uint? ReadIndex()
    {
        uint value = ReadU32();
        return value == Constants.InvalidIndex ? null : value;
    }

    /// <summary>
    /// Reads a u32 and casts it to an enum value.
    /// </summary>
    /// <typeparam name="T">The enum type.</typeparam>
    /// <returns>The enum value.</returns>
    /// <exception cref="InvalidProtocolMessageException">Thrown when the value is not valid for the enum.</exception>
    public T ReadEnum<T>() where T : struct, Enum
    {
        uint value = ReadU32();
        if (Enum.IsDefined(typeof(T), value))
            return (T)(object)value;
        throw new InvalidProtocolMessageException($"Invalid enum value {value} for {typeof(T).Name}");
    }

    /// <summary>
    /// Returns whether there is any data left in the buffer.
    /// </summary>
    public bool HasDataLeft() => _position < _end;

    internal void EnsureBytes(int count)
    {
        if (_position + count > _end)
            throw new InvalidProtocolMessageException($"Unexpected end of buffer: need {count} bytes at position {_position - _start}, but only {_end - _position} available");
    }

    private string ReadNullTerminatedString()
    {
        int start = _position;
        while (_position < _end && _buffer[_position] != 0)
            _position++;
        
        if (_position >= _end)
            throw new InvalidProtocolMessageException("Unterminated string");
        
        string result = Encoding.UTF8.GetString(_buffer, start, _position - start);
        _position++; // Skip null terminator
        return result;
    }
}

/// <summary>
/// A streaming writer for tagstruct-encoded data.
/// </summary>
public sealed class TagStructWriter
{
    private readonly MemoryStream _stream;
    private readonly ushort _protocolVersion;

    /// <summary>
    /// Creates a tagstruct writer.
    /// </summary>
    /// <param name="protocolVersion">The protocol version to use for writing.</param>
    public TagStructWriter(ushort protocolVersion)
    {
        _stream = new MemoryStream();
        _protocolVersion = protocolVersion;
    }

    /// <summary>
    /// Gets the protocol version being used.
    /// </summary>
    public ushort ProtocolVersion => _protocolVersion;

    /// <summary>
    /// Gets the written bytes.
    /// </summary>
    public byte[] Buffer => _stream.ToArray();

    /// <summary>
    /// Gets the length of the written data.
    /// </summary>
    public int Length => (int)_stream.Length;

    /// <summary>
    /// Gets the current position in the buffer.
    /// </summary>
    public int Position => (int)_stream.Position;

    internal MemoryStream Stream => _stream;

    /// <summary>
    /// Writes a tag to the buffer.
    /// </summary>
    /// <param name="tag">The tag to write.</param>
    public void WriteTag(Tag tag)
    {
        _stream.WriteByte((byte)tag);
    }

    /// <summary>
    /// Writes a single byte.
    /// </summary>
    /// <param name="value">The byte value.</param>
    public void WriteU8(byte value)
    {
        WriteTag(Tag.U8);
        _stream.WriteByte(value);
    }

    /// <summary>
    /// Writes an unsigned 32-bit integer (big-endian).
    /// </summary>
    /// <param name="value">The uint value.</param>
    public void WriteU32(uint value)
    {
        WriteTag(Tag.U32);
        _stream.WriteByte((byte)(value >> 24));
        _stream.WriteByte((byte)(value >> 16));
        _stream.WriteByte((byte)(value >> 8));
        _stream.WriteByte((byte)value);
    }

    /// <summary>
    /// Writes an unsigned 64-bit integer (big-endian).
    /// </summary>
    /// <param name="value">The ulong value.</param>
    public void WriteU64(ulong value)
    {
        WriteTag(Tag.U64);
        _stream.WriteByte((byte)(value >> 56));
        _stream.WriteByte((byte)(value >> 48));
        _stream.WriteByte((byte)(value >> 40));
        _stream.WriteByte((byte)(value >> 32));
        _stream.WriteByte((byte)(value >> 24));
        _stream.WriteByte((byte)(value >> 16));
        _stream.WriteByte((byte)(value >> 8));
        _stream.WriteByte((byte)value);
    }

    /// <summary>
    /// Writes a signed 64-bit integer (big-endian).
    /// </summary>
    /// <param name="value">The long value.</param>
    public void WriteI64(long value)
    {
        WriteTag(Tag.S64);
        ulong uvalue = (ulong)value;
        _stream.WriteByte((byte)(uvalue >> 56));
        _stream.WriteByte((byte)(uvalue >> 48));
        _stream.WriteByte((byte)(uvalue >> 40));
        _stream.WriteByte((byte)(uvalue >> 32));
        _stream.WriteByte((byte)(uvalue >> 24));
        _stream.WriteByte((byte)(uvalue >> 16));
        _stream.WriteByte((byte)(uvalue >> 8));
        _stream.WriteByte((byte)uvalue);
    }

    /// <summary>
    /// Writes a boolean value.
    /// </summary>
    /// <param name="value">The boolean value.</param>
    public void WriteBool(bool value)
    {
        WriteTag(value ? Tag.BooleanTrue : Tag.BooleanFalse);
    }

    /// <summary>
    /// Writes a "usec" value.
    /// </summary>
    /// <param name="usec">The microseconds value.</param>
    public void WriteUsec(ulong usec)
    {
        WriteTag(Tag.Usec);
        _stream.WriteByte((byte)(usec >> 56));
        _stream.WriteByte((byte)(usec >> 48));
        _stream.WriteByte((byte)(usec >> 40));
        _stream.WriteByte((byte)(usec >> 32));
        _stream.WriteByte((byte)(usec >> 24));
        _stream.WriteByte((byte)(usec >> 16));
        _stream.WriteByte((byte)(usec >> 8));
        _stream.WriteByte((byte)usec);
    }

    /// <summary>
    /// Writes a timestamp.
    /// </summary>
    /// <param name="dateTimeOffset">The timestamp.</param>
    public void WriteTimeVal(DateTimeOffset dateTimeOffset)
    {
        WriteTag(Tag.TimeVal);
        long secs = dateTimeOffset.ToUnixTimeSeconds();
        // Get microseconds from ticks - each tick is 100 nanoseconds
        long usecs = (dateTimeOffset.Ticks % TimeSpan.TicksPerSecond) / 10;
        // Write raw U32 values without tags (matching ReadTimeVal)
        WriteU32Raw((uint)secs);
        WriteU32Raw((uint)usecs);
    }

    /// <summary>
    /// Writes a raw unsigned 32-bit integer (big-endian) without a tag.
    /// </summary>
    /// <param name="value">The uint value.</param>
    private void WriteU32Raw(uint value)
    {
        _stream.WriteByte((byte)(value >> 24));
        _stream.WriteByte((byte)(value >> 16));
        _stream.WriteByte((byte)(value >> 8));
        _stream.WriteByte((byte)value);
    }

    /// <summary>
    /// Writes an "arbitrary" byte blob with length prefix.
    /// </summary>
    /// <param name="data">The byte array.</param>
    public void WriteArbitrary(ReadOnlySpan<byte> data)
    {
        WriteTag(Tag.Arbitrary);
        WriteU32Raw((uint)data.Length);
        _stream.Write(data);
    }

    /// <summary>
    /// Writes a string, or a special "null string" tag.
    /// </summary>
    /// <param name="value">The string value, or null.</param>
    public void WriteString(string? value)
    {
        if (value != null)
        {
            WriteTag(Tag.String);
            WriteNullTerminatedString(value);
        }
        else
        {
            WriteNullString();
        }
    }

    /// <summary>
    /// Writes a special "null string" tag.
    /// </summary>
    public void WriteNullString()
    {
        WriteTag(Tag.StringNull);
    }

    /// <summary>
    /// Writes an index as a u32, or PA_INVALID_INDEX (-1) in the case of null.
    /// </summary>
    /// <param name="index">The index value.</param>
    public void WriteIndex(uint? index)
    {
        WriteU32(index ?? Constants.InvalidIndex);
    }

    /// <summary>
    /// Gets the written data as a byte array.
    /// </summary>
    /// <returns>The byte array.</returns>
    public byte[] ToArray() => _stream.ToArray();

    private void WriteNullTerminatedString(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        _stream.Write(bytes);
        _stream.WriteByte(0);
    }
}
