namespace Podish.Pulse.Protocol;

/// <summary>
/// Channel position labels.
/// </summary>
public enum ChannelPosition : byte
{
    /// <summary>No position.</summary>
    Mono = 0,
    /// <summary>Apple, Dolby call this 'Left'.</summary>
    FrontLeft = 1,
    /// <summary>Apple, Dolby call this 'Right'.</summary>
    FrontRight = 2,
    /// <summary>Apple, Dolby call this 'Center'.</summary>
    FrontCenter = 3,
    /// <summary>Microsoft calls this 'Back Center', Apple calls this 'Center Surround', Dolby calls this 'Surround Rear Center'.</summary>
    RearCenter = 4,
    /// <summary>Microsoft calls this 'Back Left', Apple calls this 'Left Surround' (!), Dolby calls this 'Surround Rear Left'.</summary>
    RearLeft = 5,
    /// <summary>Microsoft calls this 'Back Right', Apple calls this 'Right Surround' (!), Dolby calls this 'Surround Rear Right'.</summary>
    RearRight = 6,
    /// <summary>Microsoft calls this 'Low Frequency', Apple calls this 'LFEScreen'.</summary>
    Lfe = 7,
    /// <summary>Apple, Dolby call this 'Left Center'.</summary>
    FrontLeftOfCenter = 8,
    /// <summary>Apple, Dolby call this 'Right Center'.</summary>
    FrontRightOfCenter = 9,
    /// <summary>Apple calls this 'Left Surround Direct', Dolby calls this 'Surround Left' (!).</summary>
    SideLeft = 10,
    /// <summary>Apple calls this 'Right Surround Direct', Dolby calls this 'Surround Right' (!).</summary>
    SideRight = 11,
    /// <summary>Auxiliary channel 0.</summary>
    Aux0 = 12,
    /// <summary>Auxiliary channel 1.</summary>
    Aux1 = 13,
    /// <summary>Auxiliary channel 2.</summary>
    Aux2 = 14,
    /// <summary>Auxiliary channel 3.</summary>
    Aux3 = 15,
    /// <summary>Auxiliary channel 4.</summary>
    Aux4 = 16,
    /// <summary>Auxiliary channel 5.</summary>
    Aux5 = 17,
    /// <summary>Auxiliary channel 6.</summary>
    Aux6 = 18,
    /// <summary>Auxiliary channel 7.</summary>
    Aux7 = 19,
    /// <summary>Auxiliary channel 8.</summary>
    Aux8 = 20,
    /// <summary>Auxiliary channel 9.</summary>
    Aux9 = 21,
    /// <summary>Auxiliary channel 10.</summary>
    Aux10 = 22,
    /// <summary>Auxiliary channel 11.</summary>
    Aux11 = 23,
    /// <summary>Auxiliary channel 12.</summary>
    Aux12 = 24,
    /// <summary>Auxiliary channel 13.</summary>
    Aux13 = 25,
    /// <summary>Auxiliary channel 14.</summary>
    Aux14 = 26,
    /// <summary>Auxiliary channel 15.</summary>
    Aux15 = 27,
    /// <summary>Auxiliary channel 16.</summary>
    Aux16 = 28,
    /// <summary>Auxiliary channel 17.</summary>
    Aux17 = 29,
    /// <summary>Auxiliary channel 18.</summary>
    Aux18 = 30,
    /// <summary>Auxiliary channel 19.</summary>
    Aux19 = 31,
    /// <summary>Auxiliary channel 20.</summary>
    Aux20 = 32,
    /// <summary>Auxiliary channel 21.</summary>
    Aux21 = 33,
    /// <summary>Auxiliary channel 22.</summary>
    Aux22 = 34,
    /// <summary>Auxiliary channel 23.</summary>
    Aux23 = 35,
    /// <summary>Auxiliary channel 24.</summary>
    Aux24 = 36,
    /// <summary>Auxiliary channel 25.</summary>
    Aux25 = 37,
    /// <summary>Auxiliary channel 26.</summary>
    Aux26 = 38,
    /// <summary>Auxiliary channel 27.</summary>
    Aux27 = 39,
    /// <summary>Auxiliary channel 28.</summary>
    Aux28 = 40,
    /// <summary>Auxiliary channel 29.</summary>
    Aux29 = 41,
    /// <summary>Auxiliary channel 30.</summary>
    Aux30 = 42,
    /// <summary>Auxiliary channel 31.</summary>
    Aux31 = 43,
    /// <summary>Apple calls this 'Top Center Surround'.</summary>
    TopCenter = 44,
    /// <summary>Apple calls this 'Vertical Height Left'.</summary>
    TopFrontLeft = 45,
    /// <summary>Apple calls this 'Vertical Height Right'.</summary>
    TopFrontRight = 46,
    /// <summary>Apple calls this 'Vertical Height Center'.</summary>
    TopFrontCenter = 47,
    /// <summary>Microsoft and Apple call this 'Top Back Left'.</summary>
    TopRearLeft = 48,
    /// <summary>Microsoft and Apple call this 'Top Back Right'.</summary>
    TopRearRight = 49,
    /// <summary>Microsoft and Apple call this 'Top Back Center'.</summary>
    TopRearCenter = 50,
}

/// <summary>
/// A map from stream channels to speaker positions.
/// These values are relevant for conversion and mixing of streams.
/// </summary>
public sealed class ChannelMap : IEquatable<ChannelMap>, IEnumerable<ChannelPosition>
{
    private readonly ChannelPosition[] _map;
    private byte _channels;

    /// <summary>
    /// Creates an empty channel map.
    /// </summary>
    public ChannelMap()
    {
        _map = new ChannelPosition[Constants.MaxChannels];
        _channels = 0;
    }

    /// <summary>
    /// Creates a channel map with the given channels.
    /// </summary>
    /// <param name="channels">The channel positions.</param>
    public ChannelMap(IEnumerable<ChannelPosition> channels) : this()
    {
        foreach (var channel in channels)
        {
            Push(channel);
        }
    }

    /// <summary>
    /// Gets the number of channel mappings stored in this ChannelMap.
    /// </summary>
    public byte NumChannels => _channels;

    /// <summary>
    /// Creates a channel map with a single channel (mono).
    /// </summary>
    public static ChannelMap Mono()
    {
        var map = new ChannelMap();
        map._channels = 1;
        return map;
    }

    /// <summary>
    /// Creates a channel map with two channels in the standard stereo positions.
    /// </summary>
    public static ChannelMap Stereo()
    {
        var map = new ChannelMap();
        map.Push(ChannelPosition.FrontLeft);
        map.Push(ChannelPosition.FrontRight);
        return map;
    }

    /// <summary>
    /// Tries to append another ChannelPosition to the end of this map.
    /// </summary>
    /// <param name="position">The channel position to add.</param>
    /// <exception cref="InvalidOperationException">Thrown when the map is full.</exception>
    public void Push(ChannelPosition position)
    {
        if (_channels < Constants.MaxChannels)
        {
            _map[_channels++] = position;
        }
        else
        {
            throw new InvalidOperationException("Channel map is full");
        }
    }

    /// <summary>
    /// Gets the channel position at the specified index.
    /// </summary>
    /// <param name="index">The index.</param>
    /// <returns>The channel position.</returns>
    public ChannelPosition this[int index]
    {
        get
        {
            if (index < 0 || index >= _channels)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _map[index];
        }
    }

    public bool Equals(ChannelMap? other)
    {
        if (other is null) return false;
        if (_channels != other._channels) return false;
        for (int i = 0; i < _channels; i++)
        {
            if (_map[i] != other._map[i]) return false;
        }
        return true;
    }

    public override bool Equals(object? obj)
    {
        return obj is ChannelMap other && Equals(other);
    }

    public override int GetHashCode()
    {
        int hash = _channels.GetHashCode();
        for (int i = 0; i < _channels; i++)
        {
            hash = HashCode.Combine(hash, _map[i]);
        }
        return hash;
    }

    public IEnumerator<ChannelPosition> GetEnumerator()
    {
        for (int i = 0; i < _channels; i++)
        {
            yield return _map[i];
        }
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}

/// <summary>
/// Extension methods for TagStructReader and TagStructWriter to read/write ChannelMap.
/// </summary>
public static class ChannelMapExtensions
{
    /// <summary>
    /// Reads a ChannelMap from a tagstruct.
    /// </summary>
    /// <param name="reader">The reader.</param>
    /// <returns>The channel map.</returns>
    public static ChannelMap ReadChannelMap(this TagStructReader reader)
    {
        reader.ExpectTag(Tag.ChannelMap);
        
        reader.EnsureBytes(1);
        byte channels = reader.Remaining[0];
        reader._position++; // Manual read to avoid recursion
        
        if (channels > Constants.MaxChannels)
            throw new InvalidProtocolMessageException($"Channel map too large: {channels} channels (max is {Constants.MaxChannels})");
        
        var map = new ChannelMap();
        for (byte i = 0; i < channels; i++)
        {
            reader.EnsureBytes(1);
            byte raw = reader.Remaining[0];
            reader._position++;
            
            ChannelPosition position = raw <= 50 ? (ChannelPosition)raw :
                throw new InvalidProtocolMessageException($"Invalid channel position {raw}");
            map.Push(position);
        }
        
        return map;
    }

    /// <summary>
    /// Writes a ChannelMap to a tagstruct.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="map">The channel map.</param>
    public static void WriteChannelMap(this TagStructWriter writer, ChannelMap map)
    {
        writer.WriteTag(Tag.ChannelMap);
        writer.Stream.WriteByte(map.NumChannels);
        foreach (var position in map)
        {
            writer.Stream.WriteByte((byte)position);
        }
    }
}
