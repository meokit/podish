namespace Podish.Pulse.Protocol;

/// <summary>
/// Volume specification for a single channel.
/// The volume scale goes from 0 (mute) to VOLUME_NORM (100%, 0 dB) to VOLUME_MAX (amplification).
/// </summary>
public readonly struct Volume : IEquatable<Volume>
{
    /// <summary>
    /// The normal volume (100%, 0 dB, no attenuation, no amplification).
    /// </summary>
    public const uint Norm = 0x10000;
    
    /// <summary>
    /// The muted volume (0%, -Inf dB).
    /// </summary>
    public const uint Muted = 0;
    
    /// <summary>
    /// The maximum valid volume.
    /// </summary>
    public const uint Max = uint.MaxValue / 2;
    
    private readonly uint _value;

    /// <summary>
    /// Creates a new volume from a raw value.
    /// </summary>
    /// <param name="value">The raw volume value.</param>
    private Volume(uint value)
    {
        _value = value;
    }

    /// <summary>
    /// Creates a volume from the normal volume constant.
    /// </summary>
    public static Volume Normal => new(Norm);
    
    /// <summary>
    /// Creates a muted volume.
    /// </summary>
    public static Volume Mute => new(Muted);

    /// <summary>
    /// Gets the raw volume value as a uint.
    /// This is not useful for user presentation.
    /// </summary>
    public uint AsUInt32 => _value;

    /// <summary>
    /// Creates a volume specification from a raw uint sent over the wire.
    /// If the raw value is out of the valid range, it will be clamped.
    /// </summary>
    /// <param name="raw">The raw value.</param>
    /// <returns>The volume.</returns>
    public static Volume FromUInt32Clamped(uint raw)
    {
        return new Volume(Math.Min(raw, Max));
    }

    /// <summary>
    /// Gets the amplification/attenuation in decibel (dB) corresponding to this volume.
    /// </summary>
    public float ToDb()
    {
        float linear = ToLinear();
        return linear == 0 ? float.NegativeInfinity : MathF.Log10(linear) * 20.0f;
    }

    /// <summary>
    /// Convert the volume to a linear volume.
    /// The range of the returned number goes from 0.0 (mute) over 1.0 (0 dB, 100%) and can go
    /// beyond 1.0 to indicate that the signal should be amplified.
    /// </summary>
    public float ToLinear()
    {
        // Like PulseAudio, we use a cubic scale.
        float f = (float)_value / Norm;
        return f * f * f;
    }

    /// <summary>
    /// Convert from a linear volume.
    /// Volumes outside the valid range will be clamped.
    /// </summary>
    /// <param name="linear">The linear volume (0.0 = mute, 1.0 = 100%).</param>
    /// <returns>The volume.</returns>
    public static Volume FromLinear(float linear)
    {
        if (linear <= 0) return Mute;
        float raw = MathF.Cbrt(linear) * Norm;
        return new Volume(raw > Max ? Max : (uint)raw);
    }

    /// <summary>
    /// Creates a volume from a percentage (0-100).
    /// </summary>
    /// <param name="percent">The percentage.</param>
    /// <returns>The volume.</returns>
    public static Volume FromPercent(float percent)
    {
        return FromLinear(Math.Clamp(percent / 100.0f, 0, 1));
    }

    /// <summary>
    /// Gets the volume as a percentage (0-100).
    /// </summary>
    public float ToPercent()
    {
        return ToLinear() * 100.0f;
    }

    public bool Equals(Volume other)
    {
        return _value == other._value;
    }

    public override bool Equals(object? obj)
    {
        return obj is Volume other && Equals(other);
    }

    public override int GetHashCode()
    {
        return _value.GetHashCode();
    }

    public static bool operator ==(Volume left, Volume right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(Volume left, Volume right)
    {
        return !(left == right);
    }

    public override string ToString()
    {
        return $"{ToDb():F1} dB ({ToPercent():F1}%)";
    }
}

/// <summary>
/// Per-channel volume setting.
/// </summary>
public sealed class ChannelVolume : IEquatable<ChannelVolume>
{
    private readonly Volume[] _volumes;
    private byte _channels;

    /// <summary>
    /// Creates an empty ChannelVolume specifying no volumes for any channel.
    /// </summary>
    public ChannelVolume()
    {
        _volumes = new Volume[Constants.MaxChannels];
        _channels = 0;
    }

    /// <summary>
    /// Create a ChannelVolume with N channels, all muted.
    /// </summary>
    /// <param name="channels">The number of channels.</param>
    public ChannelVolume(byte channels)
    {
        _volumes = new Volume[Constants.MaxChannels];
        _channels = 0;
        for (byte i = 0; i < channels; i++)
        {
            Push(Volume.Mute);
        }
    }

    /// <summary>
    /// Create a ChannelVolume with N channels, all at normal volume.
    /// </summary>
    /// <param name="channels">The number of channels.</param>
    public static ChannelVolume Norm(byte channels)
    {
        var cv = new ChannelVolume();
        for (byte i = 0; i < channels; i++)
        {
            cv.Push(Volume.Normal);
        }
        return cv;
    }

    /// <summary>
    /// Gets the number of channels.
    /// </summary>
    public byte Channels => _channels;

    /// <summary>
    /// Gets the volume for the specified channel.
    /// </summary>
    /// <param name="index">The channel index.</param>
    /// <returns>The volume.</returns>
    public Volume this[int index]
    {
        get
        {
            if (index < 0 || index >= _channels)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _volumes[index];
        }
        set
        {
            if (index < 0 || index >= _channels)
                throw new ArgumentOutOfRangeException(nameof(index));
            _volumes[index] = value;
        }
    }

    /// <summary>
    /// Append a new volume to the list.
    /// </summary>
    /// <param name="volume">The volume.</param>
    /// <returns>The channel index for which the volume was added.</returns>
    public int Push(Volume volume)
    {
        if (_channels < Constants.MaxChannels)
        {
            _volumes[_channels++] = volume;
            return _channels - 1;
        }
        throw new InvalidOperationException("Channel volume is full");
    }

    /// <summary>
    /// Gets all volumes as an array.
    /// </summary>
    public Volume[] GetVolumes()
    {
        return _volumes.Take(_channels).ToArray();
    }

    public bool Equals(ChannelVolume? other)
    {
        if (other is null) return false;
        if (_channels != other._channels) return false;
        for (int i = 0; i < _channels; i++)
        {
            if (_volumes[i] != other._volumes[i]) return false;
        }
        return true;
    }

    public override bool Equals(object? obj)
    {
        return obj is ChannelVolume other && Equals(other);
    }

    public override int GetHashCode()
    {
        int hash = _channels.GetHashCode();
        for (int i = 0; i < _channels; i++)
        {
            hash = HashCode.Combine(hash, _volumes[i]);
        }
        return hash;
    }

    public static bool operator ==(ChannelVolume? left, ChannelVolume? right)
    {
        if (left is null) return right is null;
        return left.Equals(right);
    }

    public static bool operator !=(ChannelVolume? left, ChannelVolume? right)
    {
        return !(left == right);
    }
}

/// <summary>
/// Extension methods for TagStructReader and TagStructWriter to read/write Volume and ChannelVolume.
/// </summary>
public static class VolumeExtensions
{
    /// <summary>
    /// Reads a Volume from a tagstruct.
    /// </summary>
    /// <param name="reader">The reader.</param>
    /// <returns>The volume.</returns>
    public static Volume ReadVolume(this TagStructReader reader)
    {
        reader.ExpectTag(Tag.Volume);
        uint raw = reader.ReadU32();
        return Volume.FromUInt32Clamped(raw);
    }

    /// <summary>
    /// Writes a Volume to a tagstruct.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="volume">The volume.</param>
    public static void WriteVolume(this TagStructWriter writer, Volume volume)
    {
        writer.WriteTag(Tag.Volume);
        writer.WriteU32(volume.AsUInt32);
    }

    /// <summary>
    /// Reads a ChannelVolume from a tagstruct.
    /// </summary>
    /// <param name="reader">The reader.</param>
    /// <returns>The channel volume.</returns>
    public static ChannelVolume ReadChannelVolume(this TagStructReader reader)
    {
        reader.ExpectTag(Tag.CVolume);

        reader.EnsureBytes(1);
        byte nChannels = reader.Remaining[0];
        reader._position++;
        
        if (nChannels == 0 || nChannels > Constants.MaxChannels)
            throw new InvalidProtocolMessageException($"Invalid cvolume channel count {nChannels}, must be between 1 and {Constants.MaxChannels}");
        
        var cvolume = new ChannelVolume();
        for (byte i = 0; i < nChannels; i++)
        {
            reader.EnsureBytes(4);
            uint raw = (uint)(reader.Remaining[0] << 24 | reader.Remaining[1] << 16 |
                              reader.Remaining[2] << 8 | reader.Remaining[3]);
            reader._position += 4;
            cvolume.Push(Volume.FromUInt32Clamped(raw));
        }
        
        return cvolume;
    }

    /// <summary>
    /// Writes a ChannelVolume to a tagstruct.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="cvolume">The channel volume.</param>
    public static void WriteChannelVolume(this TagStructWriter writer, ChannelVolume cvolume)
    {
        writer.WriteTag(Tag.CVolume);
        writer.Stream.WriteByte(cvolume.Channels);
        foreach (var volume in cvolume.GetVolumes())
        {
            writer.Stream.WriteByte((byte)(volume.AsUInt32 >> 24));
            writer.Stream.WriteByte((byte)(volume.AsUInt32 >> 16));
            writer.Stream.WriteByte((byte)(volume.AsUInt32 >> 8));
            writer.Stream.WriteByte((byte)volume.AsUInt32);
        }
    }

}
