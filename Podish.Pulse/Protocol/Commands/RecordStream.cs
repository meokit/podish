namespace Podish.Pulse.Protocol.Commands;

/// <summary>
/// Flags for creating a record stream.
/// </summary>
[Flags]
public enum RecordStreamFlags : uint
{
    None = 0,
    StartCorked = (1u << 1),
    DontMove = (1u << 2),
    VariableRate = (1u << 3),
    StartPaused = (1u << 4),
    DontInhibitAutoSuspend = (1u << 5),
    NoRemap = (1u << 7),
    NoRemix = (1u << 8),
    FixFormat = (1u << 9),
    FixRate = (1u << 10),
    FixChannels = (1u << 11),
    DontMoveOnSuspend = (1u << 12),
}

/// <summary>
/// Buffer attributes for a record stream.
/// </summary>
public sealed class RecordBufferAttr : IEquatable<RecordBufferAttr>
{
    /// <summary>
    /// Maximum length of the buffer in bytes.
    /// </summary>
    public uint MaxLength;

    /// <summary>
    /// Target length of the buffer in bytes.
    /// </summary>
    public uint TargetLength;

    /// <summary>
    /// Minimum request length in bytes.
    /// </summary>
    public uint MinRequest;

    /// <summary>
    /// Prebuffer size in bytes.
    /// </summary>
    public uint Prebuffer;

    /// <summary>
    /// Minimum frame size for triggering a read in bytes.
    /// </summary>
    public uint MinIncrement;

    public bool Equals(RecordBufferAttr? other)
    {
        if (other is null) return false;
        return MaxLength == other.MaxLength &&
               TargetLength == other.TargetLength &&
               MinRequest == other.MinRequest &&
               Prebuffer == other.Prebuffer &&
               MinIncrement == other.MinIncrement;
    }

    public override bool Equals(object? obj)
    {
        return obj is RecordBufferAttr other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(MaxLength, TargetLength, MinRequest, Prebuffer, MinIncrement);
    }
}

/// <summary>
/// Parameters for creating a record stream.
/// </summary>
public sealed class CreateRecordStreamParams : IEquatable<CreateRecordStreamParams>
{
    /// <summary>
    /// The device index to use, or null for default.
    /// </summary>
    public uint? DeviceIndex;

    /// <summary>
    /// The sample specification.
    /// </summary>
    public SampleSpec SampleSpec;

    /// <summary>
    /// The channel map, or null for default.
    /// </summary>
    public ChannelMap? ChannelMap;

    /// <summary>
    /// The stream flags.
    /// </summary>
    public RecordStreamFlags Flags;

    /// <summary>
    /// The buffer attributes.
    /// </summary>
    public RecordBufferAttr? BufferAttr;

    /// <summary>
    /// The stream properties.
    /// </summary>
    public Props? Props;

    /// <summary>
    /// The stream volume.
    /// </summary>
    public ChannelVolume? Volume;

    public CreateRecordStreamParams()
    {
        SampleSpec = new SampleSpec();
        Flags = RecordStreamFlags.None;
    }

    public bool Equals(CreateRecordStreamParams? other)
    {
        if (other is null) return false;
        return DeviceIndex == other.DeviceIndex &&
               SampleSpec == other.SampleSpec &&
               ChannelMap == other.ChannelMap &&
               Flags == other.Flags &&
               BufferAttr == other.BufferAttr &&
               Props == other.Props &&
               Volume == other.Volume;
    }

    public override bool Equals(object? obj)
    {
        return obj is CreateRecordStreamParams other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(DeviceIndex, SampleSpec, ChannelMap, Flags, BufferAttr, Props, Volume);
    }
}

/// <summary>
/// Response from creating a record stream.
/// </summary>
public sealed class CreateRecordStreamResponse : IEquatable<CreateRecordStreamResponse>
{
    /// <summary>
    /// The channel index assigned to the stream.
    /// </summary>
    public uint ChannelIndex;

    /// <summary>
    /// The device index.
    /// </summary>
    public uint DeviceIndex;

    /// <summary>
    /// The sample specification.
    /// </summary>
    public SampleSpec SampleSpec;

    /// <summary>
    /// The channel map.
    /// </summary>
    public ChannelMap? ChannelMap;

    public bool Equals(CreateRecordStreamResponse? other)
    {
        if (other is null) return false;
        return ChannelIndex == other.ChannelIndex &&
               DeviceIndex == other.DeviceIndex &&
               SampleSpec == other.SampleSpec &&
               ChannelMap == other.ChannelMap;
    }

    public override bool Equals(object? obj)
    {
        return obj is CreateRecordStreamResponse other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(ChannelIndex, DeviceIndex, SampleSpec, ChannelMap);
    }
}

/// <summary>
/// Extension methods for record stream commands.
/// </summary>
public static class RecordStreamExtensions
{
    /// <summary>
    /// Reads CreateRecordStreamParams from a tagstruct.
    /// </summary>
    public static CreateRecordStreamParams ReadCreateRecordStreamParams(this TagStructReader reader)
    {
        var params_ = new CreateRecordStreamParams
        {
            DeviceIndex = reader.ReadIndex(),
            SampleSpec = reader.ReadSampleSpec(),
            ChannelMap = reader.ReadChannelMap(),
            Flags = (RecordStreamFlags)reader.ReadU32(),
        };

        if (reader.HasDataLeft())
        {
            params_.BufferAttr = reader.ReadRecordBufferAttr();
        }

        if (reader.HasDataLeft())
        {
            params_.Props = reader.ReadProps();
        }

        if (reader.HasDataLeft())
        {
            params_.Volume = reader.ReadChannelVolume();
        }

        return params_;
    }

    /// <summary>
    /// Writes CreateRecordStreamParams to a tagstruct.
    /// </summary>
    public static void WriteCreateRecordStreamParams(this TagStructWriter writer, CreateRecordStreamParams params_)
    {
        writer.WriteIndex(params_.DeviceIndex);
        writer.WriteSampleSpec(params_.SampleSpec);
        if (params_.ChannelMap != null)
        {
            writer.WriteChannelMap(params_.ChannelMap);
        }
        writer.WriteU32((uint)params_.Flags);

        if (params_.BufferAttr != null)
        {
            writer.WriteRecordBufferAttr(params_.BufferAttr);
        }

        if (params_.Props != null)
        {
            writer.WriteProps(params_.Props);
        }

        if (params_.Volume != null)
        {
            writer.WriteChannelVolume(params_.Volume);
        }
    }

    /// <summary>
    /// Reads CreateRecordStreamResponse from a tagstruct.
    /// </summary>
    public static CreateRecordStreamResponse ReadCreateRecordStreamResponse(this TagStructReader reader)
    {
        return new CreateRecordStreamResponse
        {
            ChannelIndex = reader.ReadU32(),
            DeviceIndex = reader.ReadU32(),
            SampleSpec = reader.ReadSampleSpec(),
            ChannelMap = reader.ReadChannelMap(),
        };
    }

    /// <summary>
    /// Writes CreateRecordStreamResponse to a tagstruct.
    /// </summary>
    public static void WriteCreateRecordStreamResponse(this TagStructWriter writer, CreateRecordStreamResponse response)
    {
        writer.WriteU32(response.ChannelIndex);
        writer.WriteU32(response.DeviceIndex);
        writer.WriteSampleSpec(response.SampleSpec);
        if (response.ChannelMap != null)
        {
            writer.WriteChannelMap(response.ChannelMap);
        }
    }

    /// <summary>
    /// Reads RecordBufferAttr from a tagstruct.
    /// </summary>
    public static RecordBufferAttr ReadRecordBufferAttr(this TagStructReader reader)
    {
        return new RecordBufferAttr
        {
            MaxLength = reader.ReadU32(),
            TargetLength = reader.ReadU32(),
            MinRequest = reader.ReadU32(),
            Prebuffer = reader.ReadU32(),
            MinIncrement = reader.ReadU32(),
        };
    }

    /// <summary>
    /// Writes RecordBufferAttr to a tagstruct.
    /// </summary>
    public static void WriteRecordBufferAttr(this TagStructWriter writer, RecordBufferAttr attr)
    {
        writer.WriteU32(attr.MaxLength);
        writer.WriteU32(attr.TargetLength);
        writer.WriteU32(attr.MinRequest);
        writer.WriteU32(attr.Prebuffer);
        writer.WriteU32(attr.MinIncrement);
    }
}
