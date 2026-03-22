namespace Podish.Pulse.Protocol;

/// <summary>
/// Describes how individual samples are encoded.
/// </summary>
public enum SampleFormat : byte
{
    /// <summary>Invalid or unspecified.</summary>
    Invalid = 0xFF,
    /// <summary>Unsigned 8 Bit PCM</summary>
    U8 = 0,
    /// <summary>8 Bit a-Law</summary>
    Alaw = 1,
    /// <summary>8 Bit mu-Law</summary>
    Ulaw = 2,
    /// <summary>Signed 16 Bit PCM, little endian (PC)</summary>
    S16Le = 3,
    /// <summary>Signed 16 Bit PCM, big endian</summary>
    S16Be = 4,
    /// <summary>32 Bit IEEE floating point, little endian (PC), range -1.0 to 1.0</summary>
    Float32Le = 5,
    /// <summary>32 Bit IEEE floating point, big endian, range -1.0 to 1.0</summary>
    Float32Be = 6,
    /// <summary>Signed 32 Bit PCM, little endian (PC)</summary>
    S32Le = 7,
    /// <summary>Signed 32 Bit PCM, big endian</summary>
    S32Be = 8,
    /// <summary>Signed 24 Bit PCM packed, little endian (PC).</summary>
    S24Le = 9,
    /// <summary>Signed 24 Bit PCM packed, big endian.</summary>
    S24Be = 10,
    /// <summary>Signed 24 Bit PCM in LSB of 32 Bit words, little endian (PC).</summary>
    S24In32Le = 11,
    /// <summary>Signed 24 Bit PCM in LSB of 32 Bit words, big endian.</summary>
    S24In32Be = 12,
}

/// <summary>
/// A sample specification that fully describes the format of a sample stream between 2 endpoints.
/// </summary>
public struct SampleSpec : IEquatable<SampleSpec>
{
    /// <summary>
    /// Format / Encoding of individual samples.
    /// </summary>
    public SampleFormat Format;
    
    /// <summary>
    /// Number of independent channels.
    /// </summary>
    public byte Channels;
    
    /// <summary>
    /// Number of samples per second (and per channel).
    /// </summary>
    public uint SampleRate;

    /// <summary>
    /// Creates a new sample specification.
    /// </summary>
    /// <param name="format">The sample format.</param>
    /// <param name="channels">The number of channels.</param>
    /// <param name="sampleRate">The sample rate in Hz.</param>
    public SampleSpec(SampleFormat format, byte channels, uint sampleRate)
    {
        Format = format;
        Channels = channels;
        SampleRate = sampleRate;
    }

    /// <summary>
    /// Returns the number of bytes used to store a single sample.
    /// </summary>
    public int BytesPerSample => Format switch
    {
        SampleFormat.Invalid => 0,
        SampleFormat.U8 => 1,
        SampleFormat.Alaw => 1,
        SampleFormat.Ulaw => 1,
        SampleFormat.S16Le => 2,
        SampleFormat.S16Be => 2,
        SampleFormat.Float32Le => 4,
        SampleFormat.Float32Be => 4,
        SampleFormat.S32Le => 4,
        SampleFormat.S32Be => 4,
        SampleFormat.S24Le => 3,
        SampleFormat.S24Be => 3,
        SampleFormat.S24In32Le => 4,
        SampleFormat.S24In32Be => 4,
        _ => 0,
    };

    /// <summary>
    /// For a given byte length, calculates how many samples it contains, divided by the sample rate.
    /// </summary>
    /// <param name="len">The byte length.</param>
    /// <returns>The duration as a TimeSpan.</returns>
    public TimeSpan BytesToDuration(int len)
    {
        int bytesPerSample = BytesPerSample;
        if (bytesPerSample == 0 || Channels == 0 || SampleRate == 0)
            return TimeSpan.Zero;
        
        int frames = len / bytesPerSample / Channels;
        long totalMicroseconds = (long)frames * 1_000_000L / SampleRate;
        return TimeSpan.FromMicroseconds(totalMicroseconds);
    }

    /// <summary>
    /// Modifies a SampleSpec to be compatible with a different protocol_version so that older
    /// clients can understand it.
    /// </summary>
    /// <param name="protocolVersion">The protocol version to downgrade to.</param>
    /// <returns>The downgraded sample spec.</returns>
    public SampleSpec ProtocolDowngrade(ushort protocolVersion)
    {
        SampleSpec fixedSpec = this;
        
        // S24 samples were added in version 15, downgrade them to something similar
        if (protocolVersion < 15)
        {
            fixedSpec.Format = Format switch
            {
                SampleFormat.S24Le or SampleFormat.S24In32Le => SampleFormat.Float32Le,
                SampleFormat.S24Be or SampleFormat.S24In32Be => SampleFormat.Float32Be,
                _ => Format,
            };
        }
        
        return fixedSpec;
    }

    public bool Equals(SampleSpec other)
    {
        return Format == other.Format && Channels == other.Channels && SampleRate == other.SampleRate;
    }

    public override bool Equals(object? obj)
    {
        return obj is SampleSpec other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Format, Channels, SampleRate);
    }

    public static bool operator ==(SampleSpec left, SampleSpec right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(SampleSpec left, SampleSpec right)
    {
        return !(left == right);
    }
}

/// <summary>
/// Extension methods for TagStructReader and TagStructWriter to read/write SampleSpec.
/// </summary>
public static class SampleSpecExtensions
{
    /// <summary>
    /// Reads a SampleSpec from a tagstruct.
    /// </summary>
    /// <param name="reader">The reader.</param>
    /// <returns>The sample spec.</returns>
    public static SampleSpec ReadSampleSpec(this TagStructReader reader)
    {
        reader.ExpectTag(Tag.SampleSpec);
        reader.EnsureBytes(2);
        byte formatByte = reader.Remaining[0];
        byte channels = reader.Remaining[1];
        reader._position += 2; // Manual read to avoid recursion
        
        SampleFormat format = formatByte <= 12 || formatByte == 0xFF
            ? (SampleFormat)formatByte
            : throw new InvalidProtocolMessageException($"Invalid sample format {formatByte}");
        
        uint sampleRate = reader.ReadU32();
        
        return new SampleSpec(format, channels, sampleRate);
    }

    /// <summary>
    /// Writes a SampleSpec to a tagstruct.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="spec">The sample spec.</param>
    public static void WriteSampleSpec(this TagStructWriter writer, SampleSpec spec)
    {
        writer.WriteTag(Tag.SampleSpec);
        writer.Stream.WriteByte((byte)spec.Format);
        writer.Stream.WriteByte(spec.Channels);
        writer.WriteU32(spec.SampleRate);
    }
}
