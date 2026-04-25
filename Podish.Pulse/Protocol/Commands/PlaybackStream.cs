namespace Podish.Pulse.Protocol.Commands;

/// <summary>
/// Flags for creating a playback stream.
/// </summary>
[Flags]
public enum PlaybackStreamFlags : uint
{
    None = 0,
    StartMuted = (1u << 0),
    StartCorked = (1u << 1),
    DontMove = (1u << 2),
    VariableRate = (1u << 3),
    StartPaused = (1u << 4),
    DontInhibitAutoSuspend = (1u << 5),
    EarlyRequests = (1u << 6),
    NoRemap = (1u << 7),
    NoRemix = (1u << 8),
    FixFormat = (1u << 9),
    FixRate = (1u << 10),
    FixChannels = (1u << 11),
    DontMoveOnSuspend = (1u << 12),
}

/// <summary>
/// Buffer attributes for a playback stream.
/// </summary>
public sealed class PlaybackBufferAttr : IEquatable<PlaybackBufferAttr>
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
    /// Minimum frame size for triggering a write in bytes.
    /// </summary>
    public uint MinIncrement;

    public bool Equals(PlaybackBufferAttr? other)
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
        return obj is PlaybackBufferAttr other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(MaxLength, TargetLength, MinRequest, Prebuffer, MinIncrement);
    }
}

/// <summary>
/// Parameters for creating a playback stream.
/// </summary>
public sealed class CreatePlaybackStreamParams : IEquatable<CreatePlaybackStreamParams>
{
    /// <summary>
    /// The device index to use, or null for default.
    /// </summary>
    public uint? DeviceIndex;

    /// <summary>
    /// The device name to use, or null for default.
    /// </summary>
    public string? DeviceName;

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
    public PlaybackStreamFlags Flags;

    /// <summary>
    /// The buffer attributes.
    /// </summary>
    public PlaybackBufferAttr? BufferAttr;

    /// <summary>
    /// Stream sync group id.
    /// </summary>
    public uint SyncId;

    /// <summary>
    /// The stream properties.
    /// </summary>
    public Props? Props;

    /// <summary>
    /// The stream volume.
    /// </summary>
    public ChannelVolume? Volume;

    public CreatePlaybackStreamParams()
    {
        SampleSpec = new SampleSpec();
        Flags = PlaybackStreamFlags.None;
    }

    public bool Equals(CreatePlaybackStreamParams? other)
    {
        if (other is null) return false;
        return DeviceIndex == other.DeviceIndex &&
               DeviceName == other.DeviceName &&
               SampleSpec == other.SampleSpec &&
               ChannelMap == other.ChannelMap &&
               Flags == other.Flags &&
               BufferAttr == other.BufferAttr &&
               SyncId == other.SyncId &&
               Props == other.Props &&
               Volume == other.Volume;
    }

    public override bool Equals(object? obj)
    {
        return obj is CreatePlaybackStreamParams other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            HashCode.Combine(DeviceIndex, DeviceName, SampleSpec, ChannelMap),
            HashCode.Combine(Flags, BufferAttr, SyncId, Props, Volume));
    }
}

/// <summary>
/// Response from creating a playback stream.
/// </summary>
public sealed class CreatePlaybackStreamResponse : IEquatable<CreatePlaybackStreamResponse>
{
    /// <summary>
    /// The channel index assigned to the stream.
    /// </summary>
    public uint ChannelIndex;

    /// <summary>
    /// The server-side stream index.
    /// </summary>
    public uint StreamIndex;

    /// <summary>
    /// The number of bytes the server is requesting immediately.
    /// </summary>
    public uint RequestedBytes;

    /// <summary>
    /// The negotiated buffer attributes.
    /// </summary>
    public PlaybackBufferAttr BufferAttr = new();

    /// <summary>
    /// The sample specification.
    /// </summary>
    public SampleSpec SampleSpec = new();

    /// <summary>
    /// The channel map.
    /// </summary>
    public ChannelMap? ChannelMap;

    /// <summary>
    /// The sink index.
    /// </summary>
    public uint SinkIndex;

    /// <summary>
    /// The sink name.
    /// </summary>
    public string? SinkName;

    /// <summary>
    /// Whether the stream is suspended.
    /// </summary>
    public bool Suspended;

    /// <summary>
    /// The current stream latency in usec.
    /// </summary>
    public ulong StreamLatencyUsec;

    public bool Equals(CreatePlaybackStreamResponse? other)
    {
        if (other is null) return false;
        return ChannelIndex == other.ChannelIndex &&
               StreamIndex == other.StreamIndex &&
               RequestedBytes == other.RequestedBytes &&
               Equals(BufferAttr, other.BufferAttr) &&
               SampleSpec == other.SampleSpec &&
               ChannelMap == other.ChannelMap &&
               SinkIndex == other.SinkIndex &&
               SinkName == other.SinkName &&
               Suspended == other.Suspended &&
               StreamLatencyUsec == other.StreamLatencyUsec;
    }

    public override bool Equals(object? obj)
    {
        return obj is CreatePlaybackStreamResponse other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            HashCode.Combine(ChannelIndex, StreamIndex, RequestedBytes, BufferAttr, SampleSpec),
            HashCode.Combine(ChannelMap, SinkIndex, SinkName, Suspended, StreamLatencyUsec));
    }
}

/// <summary>
/// Extension methods for playback stream commands.
/// </summary>
public static class PlaybackStreamExtensions
{
    /// <summary>
    /// Reads CreatePlaybackStreamParams from a tagstruct.
    /// </summary>
    public static CreatePlaybackStreamParams ReadCreatePlaybackStreamParams(this TagStructReader reader)
    {
        if (reader.PeekTag() == Tag.U32)
        {
            var legacyParams = new CreatePlaybackStreamParams
            {
                DeviceIndex = reader.ReadIndex(),
                SampleSpec = reader.ReadSampleSpec(),
                ChannelMap = reader.ReadChannelMap(),
                Flags = (PlaybackStreamFlags)reader.ReadU32(),
            };

            if (reader.HasDataLeft())
            {
                legacyParams.BufferAttr = reader.ReadPlaybackBufferAttr();
            }

            if (reader.HasDataLeft())
            {
                legacyParams.Props = reader.ReadProps();
            }

            if (reader.HasDataLeft())
            {
                legacyParams.Volume = reader.ReadChannelVolume();
            }

            return legacyParams;
        }

        var params_ = new CreatePlaybackStreamParams
        {
            SampleSpec = reader.ReadSampleSpec(),
            ChannelMap = reader.ReadChannelMap(),
            DeviceIndex = reader.ReadIndex(),
            DeviceName = reader.ReadString(),
        };

        uint maxLength = reader.ReadU32();
        if (reader.ReadBool())
            params_.Flags |= PlaybackStreamFlags.StartCorked;

        params_.BufferAttr = new PlaybackBufferAttr
        {
            MaxLength = maxLength,
            TargetLength = reader.ReadU32(),
            Prebuffer = reader.ReadU32(),
            MinRequest = reader.ReadU32(),
            MinIncrement = 0,
        };

        params_.SyncId = reader.ReadU32();
        params_.Volume = reader.ReadChannelVolume();

        if (reader.ReadBool())
            params_.Flags |= PlaybackStreamFlags.NoRemap;
        if (reader.ReadBool())
            params_.Flags |= PlaybackStreamFlags.NoRemix;
        if (reader.ReadBool())
            params_.Flags |= PlaybackStreamFlags.FixFormat;
        if (reader.ReadBool())
            params_.Flags |= PlaybackStreamFlags.FixRate;
        if (reader.ReadBool())
            params_.Flags |= PlaybackStreamFlags.FixChannels;
        if (reader.ReadBool())
            params_.Flags |= PlaybackStreamFlags.DontMove;
        if (reader.ReadBool())
            params_.Flags |= PlaybackStreamFlags.VariableRate;
        if (reader.ReadBool())
            params_.Flags |= PlaybackStreamFlags.StartMuted;

        reader.ReadBool(); // adjust_latency
        params_.Props = reader.ReadProps();

        if (reader.ProtocolVersion >= 14)
        {
            bool hasVolume = reader.ReadBool();
            if (!hasVolume)
                params_.Volume = null;

            if (reader.ReadBool())
                params_.Flags |= PlaybackStreamFlags.EarlyRequests;
        }

        if (reader.ProtocolVersion >= 15)
        {
            reader.ReadBool(); // start_muted explicitly set
            if (reader.ReadBool())
                params_.Flags |= PlaybackStreamFlags.DontInhibitAutoSuspend;

            reader.ReadBool(); // fail_on_suspend
        }

        if (reader.ProtocolVersion >= 17)
        {
            reader.ReadBool(); // relative_volume
        }

        if (reader.ProtocolVersion >= 18)
        {
            reader.ReadBool(); // passthrough
        }

        if (reader.ProtocolVersion >= 21 && reader.HasDataLeft())
        {
            byte count = reader.ReadU8();
            if (count != 0)
                throw new InvalidProtocolMessageException("FormatInfo in playback stream params is not supported yet");
        }

        return params_;
    }

    /// <summary>
    /// Writes CreatePlaybackStreamParams to a tagstruct.
    /// </summary>
    public static void WriteCreatePlaybackStreamParams(this TagStructWriter writer, CreatePlaybackStreamParams params_)
    {
        writer.WriteSampleSpec(params_.SampleSpec);
        writer.WriteChannelMap(params_.ChannelMap ?? new ChannelMap());
        writer.WriteIndex(params_.DeviceIndex);
        writer.WriteString(params_.DeviceName);

        PlaybackBufferAttr bufferAttr = params_.BufferAttr ?? new PlaybackBufferAttr();
        writer.WriteU32(bufferAttr.MaxLength);
        writer.WriteBool(params_.Flags.HasFlag(PlaybackStreamFlags.StartCorked));
        writer.WriteU32(bufferAttr.TargetLength);
        writer.WriteU32(bufferAttr.Prebuffer);
        writer.WriteU32(bufferAttr.MinRequest);
        writer.WriteU32(params_.SyncId);
        writer.WriteChannelVolume(params_.Volume ?? ChannelVolume.Norm(params_.SampleSpec.Channels));
        writer.WriteBool(params_.Flags.HasFlag(PlaybackStreamFlags.NoRemap));
        writer.WriteBool(params_.Flags.HasFlag(PlaybackStreamFlags.NoRemix));
        writer.WriteBool(params_.Flags.HasFlag(PlaybackStreamFlags.FixFormat));
        writer.WriteBool(params_.Flags.HasFlag(PlaybackStreamFlags.FixRate));
        writer.WriteBool(params_.Flags.HasFlag(PlaybackStreamFlags.FixChannels));
        writer.WriteBool(params_.Flags.HasFlag(PlaybackStreamFlags.DontMove));
        writer.WriteBool(params_.Flags.HasFlag(PlaybackStreamFlags.VariableRate));
        writer.WriteBool(params_.Flags.HasFlag(PlaybackStreamFlags.StartMuted));
        writer.WriteBool(false); // adjust_latency
        writer.WriteProps(params_.Props ?? new Props());

        if (writer.ProtocolVersion >= 14)
        {
            writer.WriteBool(params_.Volume != null);
            writer.WriteBool(params_.Flags.HasFlag(PlaybackStreamFlags.EarlyRequests));
        }

        if (writer.ProtocolVersion >= 15)
        {
            writer.WriteBool(params_.Flags.HasFlag(PlaybackStreamFlags.StartMuted));
            writer.WriteBool(params_.Flags.HasFlag(PlaybackStreamFlags.DontInhibitAutoSuspend));
            writer.WriteBool(false); // fail_on_suspend
        }

        if (writer.ProtocolVersion >= 17)
            writer.WriteBool(false); // relative_volume

        if (writer.ProtocolVersion >= 18)
            writer.WriteBool(false); // passthrough

        if (writer.ProtocolVersion >= 21)
            writer.WriteU8(0); // no additional formats yet
    }

    /// <summary>
    /// Reads CreatePlaybackStreamResponse from a tagstruct.
    /// </summary>
    public static CreatePlaybackStreamResponse ReadCreatePlaybackStreamResponse(this TagStructReader reader)
    {
        return new CreatePlaybackStreamResponse
        {
            ChannelIndex = reader.ReadU32(),
            StreamIndex = reader.ReadU32(),
            RequestedBytes = reader.ReadU32(),
            BufferAttr = new PlaybackBufferAttr
            {
                MaxLength = reader.ReadU32(),
                TargetLength = reader.ReadU32(),
                Prebuffer = reader.ReadU32(),
                MinRequest = reader.ReadU32(),
                MinIncrement = 0,
            },
            SampleSpec = reader.ReadSampleSpec(),
            ChannelMap = reader.ReadChannelMap(),
            SinkIndex = reader.ReadU32(),
            SinkName = reader.ReadString(),
            Suspended = reader.ReadBool(),
            StreamLatencyUsec = reader.ReadUsec(),
        };
    }

    /// <summary>
    /// Writes CreatePlaybackStreamResponse to a tagstruct.
    /// </summary>
    public static void WriteCreatePlaybackStreamResponse(this TagStructWriter writer, CreatePlaybackStreamResponse response)
    {
        writer.WriteU32(response.ChannelIndex);
        writer.WriteU32(response.StreamIndex);
        writer.WriteU32(response.RequestedBytes);
        writer.WriteU32(response.BufferAttr.MaxLength);
        writer.WriteU32(response.BufferAttr.TargetLength);
        writer.WriteU32(response.BufferAttr.Prebuffer);
        writer.WriteU32(response.BufferAttr.MinRequest);
        writer.WriteSampleSpec(response.SampleSpec);
        if (response.ChannelMap != null)
        {
            writer.WriteChannelMap(response.ChannelMap);
        }
        else
        {
            writer.WriteChannelMap(new ChannelMap());
        }
        writer.WriteU32(response.SinkIndex);
        writer.WriteString(response.SinkName);
        writer.WriteBool(response.Suspended);
        writer.WriteUsec(response.StreamLatencyUsec);
    }

    /// <summary>
    /// Reads PlaybackBufferAttr from a tagstruct.
    /// </summary>
    public static PlaybackBufferAttr ReadPlaybackBufferAttr(this TagStructReader reader)
    {
        return new PlaybackBufferAttr
        {
            MaxLength = reader.ReadU32(),
            TargetLength = reader.ReadU32(),
            MinRequest = reader.ReadU32(),
            Prebuffer = reader.ReadU32(),
            MinIncrement = reader.ReadU32(),
        };
    }

    /// <summary>
    /// Writes PlaybackBufferAttr to a tagstruct.
    /// </summary>
    public static void WritePlaybackBufferAttr(this TagStructWriter writer, PlaybackBufferAttr attr)
    {
        writer.WriteU32(attr.MaxLength);
        writer.WriteU32(attr.TargetLength);
        writer.WriteU32(attr.MinRequest);
        writer.WriteU32(attr.Prebuffer);
        writer.WriteU32(attr.MinIncrement);
    }
}
