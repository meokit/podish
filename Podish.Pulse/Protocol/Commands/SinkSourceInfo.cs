namespace Podish.Pulse.Protocol.Commands;

/// <summary>
/// State of a sink.
/// </summary>
/// <remarks>
/// The PulseAudio protocol uses 0 to represent the running state.
/// An uninitialized or invalid state should be tracked separately from the protocol value.
/// </remarks>
public enum SinkState : uint
{
    /// <summary>Running state (active playback/capture).</summary>
    Running = 0,
    /// <summary>Idle state (no active playback/capture).</summary>
    Idle = 1,
    /// <summary>Suspended state (device suspended to save power).</summary>
    Suspended = 2,
}

/// <summary>
/// State of a source.
/// </summary>
/// <remarks>
/// The PulseAudio protocol uses 0 to represent the running state.
/// An uninitialized or invalid state should be tracked separately from the protocol value.
/// </remarks>
public enum SourceState : uint
{
    /// <summary>Running state (active capture).</summary>
    Running = 0,
    /// <summary>Idle state (no active capture).</summary>
    Idle = 1,
    /// <summary>Suspended state (device suspended to save power).</summary>
    Suspended = 2,
}

/// <summary>
/// Port information for a sink or source.
/// </summary>
public sealed class PortInfo : IEquatable<PortInfo>
{
    /// <summary>
    /// The port name.
    /// </summary>
    public string Name;

    /// <summary>
    /// The port description.
    /// </summary>
    public string Description;

    /// <summary>
    /// The port priority.
    /// </summary>
    public uint Priority;

    public PortInfo()
    {
        Name = string.Empty;
        Description = string.Empty;
    }

    public bool Equals(PortInfo? other)
    {
        if (other is null) return false;
        return Name == other.Name &&
               Description == other.Description &&
               Priority == other.Priority;
    }

    public override bool Equals(object? obj)
    {
        return obj is PortInfo other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Name, Description, Priority);
    }
}

/// <summary>
/// Information about a sink.
/// </summary>
public sealed class SinkInfo : IEquatable<SinkInfo>
{
    /// <summary>
    /// The sink index.
    /// </summary>
    public uint Index;

    /// <summary>
    /// The sink name.
    /// </summary>
    public string Name;

    /// <summary>
    /// The sink description.
    /// </summary>
    public string Description;

    /// <summary>
    /// The sample specification.
    /// </summary>
    public SampleSpec SampleSpec;

    /// <summary>
    /// The channel map.
    /// </summary>
    public ChannelMap? ChannelMap;

    /// <summary>
    /// The monitor source index.
    /// </summary>
    public uint MonitorSourceIndex;

    /// <summary>
    /// The sink description.
    /// </summary>
    public string Description2;

    /// <summary>
    /// The sink flags.
    /// </summary>
    public uint Flags;

    /// <summary>
    /// The sink properties.
    /// </summary>
    public Props Props;

    /// <summary>
    /// The latency in microseconds.
    /// </summary>
    public ulong Latency;

    /// <summary>
    /// The driver name.
    /// </summary>
    public string Driver;

    /// <summary>
    /// The sink format.
    /// </summary>
    public SampleFormat Format;

    /// <summary>
    /// The channel volume.
    /// </summary>
    public ChannelVolume Volume;

    /// <summary>
    /// Whether the sink is muted.
    /// </summary>
    public bool Mute;

    /// <summary>
    /// The base volume.
    /// </summary>
    public Volume BaseVolume;

    /// <summary>
    /// The sink state.
    /// </summary>
    public SinkState State;

    /// <summary>
    /// The number of volume steps.
    /// </summary>
    public uint NVolumeSteps;

    /// <summary>
    /// The card index.
    /// </summary>
    public uint? CardIndex;

    /// <summary>
    /// The ports.
    /// </summary>
    public List<PortInfo> Ports;

    /// <summary>
    /// The active port index.
    /// </summary>
    public uint ActivePortIndex;

    /// <summary>
    /// The number of inputs.
    /// </summary>
    public uint NumInputs;

    /// <summary>
    /// The number of outputs.
    /// </summary>
    public uint NumOutputs;

    public SinkInfo()
    {
        Name = string.Empty;
        Description = string.Empty;
        SampleSpec = new SampleSpec();
        Description2 = string.Empty;
        Props = new Props();
        Driver = string.Empty;
        Format = SampleFormat.Invalid;
        Volume = new ChannelVolume();
        BaseVolume = Podish.Pulse.Protocol.Volume.Normal;
        State = default;
        NVolumeSteps = 0;
        Ports = new List<PortInfo>();
    }

    public bool Equals(SinkInfo? other)
    {
        if (other is null) return false;
        return Index == other.Index &&
               Name == other.Name &&
               Description == other.Description &&
               SampleSpec == other.SampleSpec &&
               ChannelMap == other.ChannelMap &&
               MonitorSourceIndex == other.MonitorSourceIndex &&
               Description2 == other.Description2 &&
               Flags == other.Flags &&
               Props == other.Props &&
               Latency == other.Latency &&
               Driver == other.Driver &&
               Format == other.Format &&
               Volume == other.Volume &&
               Mute == other.Mute &&
               BaseVolume == other.BaseVolume &&
               State == other.State &&
               NVolumeSteps == other.NVolumeSteps &&
               CardIndex == other.CardIndex &&
               ActivePortIndex == other.ActivePortIndex &&
               NumInputs == other.NumInputs &&
               NumOutputs == other.NumOutputs &&
               Ports.SequenceEqual(other.Ports);
    }

    public override bool Equals(object? obj)
    {
        return obj is SinkInfo other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + Index.GetHashCode();
            hash = hash * 31 + (Name?.GetHashCode() ?? 0);
            hash = hash * 31 + (Description?.GetHashCode() ?? 0);
            hash = hash * 31 + SampleSpec.GetHashCode();
            hash = hash * 31 + (ChannelMap?.GetHashCode() ?? 0);
            hash = hash * 31 + MonitorSourceIndex.GetHashCode();
            hash = hash * 31 + (Description2?.GetHashCode() ?? 0);
            hash = hash * 31 + Flags.GetHashCode();
            hash = hash * 31 + (Props?.GetHashCode() ?? 0);
            hash = hash * 31 + Latency.GetHashCode();
            hash = hash * 31 + (Driver?.GetHashCode() ?? 0);
            hash = hash * 31 + Format.GetHashCode();
            hash = hash * 31 + (Volume?.GetHashCode() ?? 0);
            hash = hash * 31 + Mute.GetHashCode();
            hash = hash * 31 + BaseVolume.GetHashCode();
            hash = hash * 31 + State.GetHashCode();
            hash = hash * 31 + NVolumeSteps.GetHashCode();
            hash = hash * 31 + CardIndex.GetHashCode();
            hash = hash * 31 + ActivePortIndex.GetHashCode();
            hash = hash * 31 + NumInputs.GetHashCode();
            hash = hash * 31 + NumOutputs.GetHashCode();
            return hash;
        }
    }
}

/// <summary>
/// Information about a source.
/// </summary>
public sealed class SourceInfo : IEquatable<SourceInfo>
{
    /// <summary>
    /// The source index.
    /// </summary>
    public uint Index;

    /// <summary>
    /// The source name.
    /// </summary>
    public string Name;

    /// <summary>
    /// The source description.
    /// </summary>
    public string Description;

    /// <summary>
    /// The sample specification.
    /// </summary>
    public SampleSpec SampleSpec;

    /// <summary>
    /// The channel map.
    /// </summary>
    public ChannelMap? ChannelMap;

    /// <summary>
    /// The monitor sink index.
    /// </summary>
    public uint MonitorSinkIndex;

    /// <summary>
    /// The source description.
    /// </summary>
    public string Description2;

    /// <summary>
    /// The source flags.
    /// </summary>
    public uint Flags;

    /// <summary>
    /// The source properties.
    /// </summary>
    public Props Props;

    /// <summary>
    /// The latency in microseconds.
    /// </summary>
    public ulong Latency;

    /// <summary>
    /// The driver name.
    /// </summary>
    public string Driver;

    /// <summary>
    /// The source format.
    /// </summary>
    public SampleFormat Format;

    /// <summary>
    /// The channel volume.
    /// </summary>
    public ChannelVolume Volume;

    /// <summary>
    /// Whether the source is muted.
    /// </summary>
    public bool Mute;

    /// <summary>
    /// The base volume.
    /// </summary>
    public Volume BaseVolume;

    /// <summary>
    /// The source state.
    /// </summary>
    public SourceState State;

    /// <summary>
    /// The number of volume steps.
    /// </summary>
    public uint NVolumeSteps;

    /// <summary>
    /// The card index.
    /// </summary>
    public uint? CardIndex;

    /// <summary>
    /// The ports.
    /// </summary>
    public List<PortInfo> Ports;

    /// <summary>
    /// The active port index.
    /// </summary>
    public uint ActivePortIndex;

    /// <summary>
    /// The number of inputs.
    /// </summary>
    public uint NumInputs;

    /// <summary>
    /// The number of outputs.
    /// </summary>
    public uint NumOutputs;

    public SourceInfo()
    {
        Name = string.Empty;
        Description = string.Empty;
        SampleSpec = new SampleSpec();
        Description2 = string.Empty;
        Props = new Props();
        Driver = string.Empty;
        Format = SampleFormat.Invalid;
        Volume = new ChannelVolume();
        BaseVolume = Podish.Pulse.Protocol.Volume.Normal;
        State = default;
        NVolumeSteps = 0;
        Ports = new List<PortInfo>();
    }

    public bool Equals(SourceInfo? other)
    {
        if (other is null) return false;
        return Index == other.Index &&
               Name == other.Name &&
               Description == other.Description &&
               SampleSpec == other.SampleSpec &&
               ChannelMap == other.ChannelMap &&
               MonitorSinkIndex == other.MonitorSinkIndex &&
               Description2 == other.Description2 &&
               Flags == other.Flags &&
               Props == other.Props &&
               Latency == other.Latency &&
               Driver == other.Driver &&
               Format == other.Format &&
               Volume == other.Volume &&
               Mute == other.Mute &&
               BaseVolume == other.BaseVolume &&
               State == other.State &&
               NVolumeSteps == other.NVolumeSteps &&
               CardIndex == other.CardIndex &&
               ActivePortIndex == other.ActivePortIndex &&
               NumInputs == other.NumInputs &&
               NumOutputs == other.NumOutputs &&
               Ports.SequenceEqual(other.Ports);
    }

    public override bool Equals(object? obj)
    {
        return obj is SourceInfo other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + Index.GetHashCode();
            hash = hash * 31 + (Name?.GetHashCode() ?? 0);
            hash = hash * 31 + (Description?.GetHashCode() ?? 0);
            hash = hash * 31 + SampleSpec.GetHashCode();
            hash = hash * 31 + (ChannelMap?.GetHashCode() ?? 0);
            hash = hash * 31 + MonitorSinkIndex.GetHashCode();
            hash = hash * 31 + (Description2?.GetHashCode() ?? 0);
            hash = hash * 31 + Flags.GetHashCode();
            hash = hash * 31 + (Props?.GetHashCode() ?? 0);
            hash = hash * 31 + Latency.GetHashCode();
            hash = hash * 31 + (Driver?.GetHashCode() ?? 0);
            hash = hash * 31 + Format.GetHashCode();
            hash = hash * 31 + (Volume?.GetHashCode() ?? 0);
            hash = hash * 31 + Mute.GetHashCode();
            hash = hash * 31 + BaseVolume.GetHashCode();
            hash = hash * 31 + State.GetHashCode();
            hash = hash * 31 + NVolumeSteps.GetHashCode();
            hash = hash * 31 + CardIndex.GetHashCode();
            hash = hash * 31 + ActivePortIndex.GetHashCode();
            hash = hash * 31 + NumInputs.GetHashCode();
            hash = hash * 31 + NumOutputs.GetHashCode();
            return hash;
        }
    }
}

/// <summary>
/// Extension methods for sink and source info commands.
/// </summary>
public static class SinkSourceInfoExtensions
{
    /// <summary>
    /// Reads SinkInfo from a tagstruct.
    /// </summary>
    public static SinkInfo ReadSinkInfo(this TagStructReader reader)
    {
        var info = new SinkInfo
        {
            Index = reader.ReadU32(),
            Name = reader.ReadStringNonNull(),
            Description = reader.ReadStringNonNull(),
            SampleSpec = reader.ReadSampleSpec(),
            ChannelMap = reader.ReadChannelMap(),
            MonitorSourceIndex = reader.ReadU32(),
            Description2 = reader.ReadStringNonNull(),
            Flags = reader.ReadU32(),
            Props = reader.ReadProps(),
            Latency = reader.ReadU64(),
            Driver = reader.ReadStringNonNull(),
            Format = (SampleFormat)reader.ReadU32(),
            Volume = reader.ReadChannelVolume(),
            Mute = reader.ReadBool(),
            BaseVolume = reader.ReadVolume(),
            State = (SinkState)reader.ReadU32(),
            NVolumeSteps = reader.ReadU32(),
            CardIndex = reader.ReadIndex(),
        };

        // Read ports
        uint numPorts = reader.ReadU32();
        for (uint i = 0; i < numPorts; i++)
        {
            info.Ports.Add(reader.ReadPortInfo());
        }

        info.ActivePortIndex = reader.ReadU32();
        info.NumInputs = reader.ReadU32();
        info.NumOutputs = reader.ReadU32();

        return info;
    }

    /// <summary>
    /// Writes SinkInfo to a tagstruct.
    /// </summary>
    public static void WriteSinkInfo(this TagStructWriter writer, SinkInfo info)
    {
        writer.WriteU32(info.Index);
        writer.WriteString(info.Name);
        writer.WriteString(info.Description);
        writer.WriteSampleSpec(info.SampleSpec);
        if (info.ChannelMap != null)
        {
            writer.WriteChannelMap(info.ChannelMap);
        }
        writer.WriteU32(info.MonitorSourceIndex);
        writer.WriteString(info.Description2);
        writer.WriteU32(info.Flags);
        writer.WriteProps(info.Props);
        writer.WriteU64(info.Latency);
        writer.WriteString(info.Driver);
        writer.WriteU32((uint)info.Format);
        writer.WriteChannelVolume(info.Volume);
        writer.WriteBool(info.Mute);
        writer.WriteVolume(info.BaseVolume);
        writer.WriteU32((uint)info.State);
        writer.WriteU32(info.NVolumeSteps);
        writer.WriteIndex(info.CardIndex);

        writer.WriteU32((uint)info.Ports.Count);
        foreach (var port in info.Ports)
        {
            writer.WritePortInfo(port);
        }

        writer.WriteU32(info.ActivePortIndex);
        writer.WriteU32(info.NumInputs);
        writer.WriteU32(info.NumOutputs);
    }

    /// <summary>
    /// Reads SourceInfo from a tagstruct.
    /// </summary>
    public static SourceInfo ReadSourceInfo(this TagStructReader reader)
    {
        var info = new SourceInfo
        {
            Index = reader.ReadU32(),
            Name = reader.ReadStringNonNull(),
            Description = reader.ReadStringNonNull(),
            SampleSpec = reader.ReadSampleSpec(),
            ChannelMap = reader.ReadChannelMap(),
            MonitorSinkIndex = reader.ReadU32(),
            Description2 = reader.ReadStringNonNull(),
            Flags = reader.ReadU32(),
            Props = reader.ReadProps(),
            Latency = reader.ReadU64(),
            Driver = reader.ReadStringNonNull(),
            Format = (SampleFormat)reader.ReadU32(),
            Volume = reader.ReadChannelVolume(),
            Mute = reader.ReadBool(),
            BaseVolume = reader.ReadVolume(),
            State = (SourceState)reader.ReadU32(),
            NVolumeSteps = reader.ReadU32(),
            CardIndex = reader.ReadIndex(),
        };

        // Read ports
        uint numPorts = reader.ReadU32();
        for (uint i = 0; i < numPorts; i++)
        {
            info.Ports.Add(reader.ReadPortInfo());
        }

        info.ActivePortIndex = reader.ReadU32();
        info.NumInputs = reader.ReadU32();
        info.NumOutputs = reader.ReadU32();

        return info;
    }

    /// <summary>
    /// Writes SourceInfo to a tagstruct.
    /// </summary>
    public static void WriteSourceInfo(this TagStructWriter writer, SourceInfo info)
    {
        writer.WriteU32(info.Index);
        writer.WriteString(info.Name);
        writer.WriteString(info.Description);
        writer.WriteSampleSpec(info.SampleSpec);
        if (info.ChannelMap != null)
        {
            writer.WriteChannelMap(info.ChannelMap);
        }
        writer.WriteU32(info.MonitorSinkIndex);
        writer.WriteString(info.Description2);
        writer.WriteU32(info.Flags);
        writer.WriteProps(info.Props);
        writer.WriteU64(info.Latency);
        writer.WriteString(info.Driver);
        writer.WriteU32((uint)info.Format);
        writer.WriteChannelVolume(info.Volume);
        writer.WriteBool(info.Mute);
        writer.WriteVolume(info.BaseVolume);
        writer.WriteU32((uint)info.State);
        writer.WriteU32(info.NVolumeSteps);
        writer.WriteIndex(info.CardIndex);

        writer.WriteU32((uint)info.Ports.Count);
        foreach (var port in info.Ports)
        {
            writer.WritePortInfo(port);
        }

        writer.WriteU32(info.ActivePortIndex);
        writer.WriteU32(info.NumInputs);
        writer.WriteU32(info.NumOutputs);
    }

    /// <summary>
    /// Reads PortInfo from a tagstruct.
    /// </summary>
    public static PortInfo ReadPortInfo(this TagStructReader reader)
    {
        return new PortInfo
        {
            Name = reader.ReadStringNonNull(),
            Description = reader.ReadStringNonNull(),
            Priority = reader.ReadU32(),
        };
    }

    /// <summary>
    /// Writes PortInfo to a tagstruct.
    /// </summary>
    public static void WritePortInfo(this TagStructWriter writer, PortInfo port)
    {
        writer.WriteString(port.Name);
        writer.WriteString(port.Description);
        writer.WriteU32(port.Priority);
    }
}
