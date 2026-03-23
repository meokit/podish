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
    /// The owner module index.
    /// </summary>
    public uint? OwnerModuleIndex;

    /// <summary>
    /// The monitor source index.
    /// </summary>
    public uint MonitorSourceIndex;

    /// <summary>
    /// The monitor source name.
    /// </summary>
    public string? MonitorSourceName;

    /// <summary>
    /// The sink flags.
    /// </summary>
    public uint Flags;

    /// <summary>
    /// The sink properties.
    /// </summary>
    public Props Props;

    /// <summary>
    /// The actual latency in microseconds.
    /// </summary>
    public ulong ActualLatency;

    /// <summary>
    /// The configured latency in microseconds.
    /// </summary>
    public ulong ConfiguredLatency;

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
               OwnerModuleIndex == other.OwnerModuleIndex &&
               MonitorSourceIndex == other.MonitorSourceIndex &&
               MonitorSourceName == other.MonitorSourceName &&
               Flags == other.Flags &&
               Props == other.Props &&
               ActualLatency == other.ActualLatency &&
               ConfiguredLatency == other.ConfiguredLatency &&
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
            hash = hash * 31 + OwnerModuleIndex.GetHashCode();
            hash = hash * 31 + MonitorSourceIndex.GetHashCode();
            hash = hash * 31 + (MonitorSourceName?.GetHashCode() ?? 0);
            hash = hash * 31 + Flags.GetHashCode();
            hash = hash * 31 + (Props?.GetHashCode() ?? 0);
            hash = hash * 31 + ActualLatency.GetHashCode();
            hash = hash * 31 + ConfiguredLatency.GetHashCode();
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
    /// The owner module index.
    /// </summary>
    public uint? OwnerModuleIndex;

    /// <summary>
    /// The monitor sink index.
    /// </summary>
    public uint MonitorSinkIndex;

    /// <summary>
    /// The monitor sink name.
    /// </summary>
    public string? MonitorSinkName;

    /// <summary>
    /// The source flags.
    /// </summary>
    public uint Flags;

    /// <summary>
    /// The source properties.
    /// </summary>
    public Props Props;

    /// <summary>
    /// The actual latency in microseconds.
    /// </summary>
    public ulong ActualLatency;

    /// <summary>
    /// The configured latency in microseconds.
    /// </summary>
    public ulong ConfiguredLatency;

    /// <summary>
    /// The driver name.
    /// </summary>
    public string Driver;

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
    public SourceInfo()
    {
        Name = string.Empty;
        Description = string.Empty;
        SampleSpec = new SampleSpec();
        Props = new Props();
        Driver = string.Empty;
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
               OwnerModuleIndex == other.OwnerModuleIndex &&
               MonitorSinkIndex == other.MonitorSinkIndex &&
               MonitorSinkName == other.MonitorSinkName &&
               Flags == other.Flags &&
               Props == other.Props &&
               ActualLatency == other.ActualLatency &&
               ConfiguredLatency == other.ConfiguredLatency &&
               Driver == other.Driver &&
               Volume == other.Volume &&
               Mute == other.Mute &&
               BaseVolume == other.BaseVolume &&
               State == other.State &&
               NVolumeSteps == other.NVolumeSteps &&
               CardIndex == other.CardIndex &&
               ActivePortIndex == other.ActivePortIndex &&
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
            hash = hash * 31 + OwnerModuleIndex.GetHashCode();
            hash = hash * 31 + MonitorSinkIndex.GetHashCode();
            hash = hash * 31 + (MonitorSinkName?.GetHashCode() ?? 0);
            hash = hash * 31 + Flags.GetHashCode();
            hash = hash * 31 + (Props?.GetHashCode() ?? 0);
            hash = hash * 31 + ActualLatency.GetHashCode();
            hash = hash * 31 + ConfiguredLatency.GetHashCode();
            hash = hash * 31 + (Driver?.GetHashCode() ?? 0);
            hash = hash * 31 + (Volume?.GetHashCode() ?? 0);
            hash = hash * 31 + Mute.GetHashCode();
            hash = hash * 31 + BaseVolume.GetHashCode();
            hash = hash * 31 + State.GetHashCode();
            hash = hash * 31 + NVolumeSteps.GetHashCode();
            hash = hash * 31 + CardIndex.GetHashCode();
            hash = hash * 31 + ActivePortIndex.GetHashCode();
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
            Description = reader.ReadString() ?? string.Empty,
            SampleSpec = reader.ReadSampleSpec(),
            ChannelMap = reader.ReadChannelMap(),
            OwnerModuleIndex = reader.ReadIndex(),
            Volume = reader.ReadChannelVolume(),
            Mute = reader.ReadBool(),
            MonitorSourceIndex = reader.ReadIndex() ?? Constants.InvalidIndex,
            MonitorSourceName = reader.ReadString(),
            ActualLatency = reader.ReadUsec(),
            Driver = reader.ReadString() ?? string.Empty,
            Flags = reader.ReadU32(),
            Props = reader.ReadProps(),
            ConfiguredLatency = reader.ReadUsec(),
        };

        if (reader.ProtocolVersion >= 15)
        {
            info.BaseVolume = reader.ReadVolume();
            info.State = (SinkState)reader.ReadU32();
            info.NVolumeSteps = reader.ReadU32();
            info.CardIndex = reader.ReadIndex();
        }

        if (reader.ProtocolVersion >= 16)
        {
            uint numPorts = reader.ReadU32();
            for (uint i = 0; i < numPorts; i++)
                info.Ports.Add(reader.ReadPortInfo());

            string? activePortName = reader.ReadString();
            if (!string.IsNullOrEmpty(activePortName))
            {
                int activePortIndex = info.Ports.FindIndex(port => port.Name == activePortName);
                info.ActivePortIndex = activePortIndex >= 0 ? (uint)activePortIndex : 0;
            }
        }

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
        writer.WriteChannelMap(info.ChannelMap ?? new ChannelMap());
        writer.WriteIndex(info.OwnerModuleIndex);
        writer.WriteChannelVolume(info.Volume);
        writer.WriteBool(info.Mute);
        writer.WriteIndex(info.MonitorSourceIndex == Constants.InvalidIndex ? null : info.MonitorSourceIndex);
        writer.WriteString(info.MonitorSourceName);
        writer.WriteUsec(info.ActualLatency);
        writer.WriteString(info.Driver);
        writer.WriteU32(info.Flags);
        writer.WriteProps(info.Props);
        writer.WriteUsec(info.ConfiguredLatency);

        if (writer.ProtocolVersion >= 15)
        {
            writer.WriteVolume(info.BaseVolume);
            writer.WriteU32((uint)info.State);
            writer.WriteU32(info.NVolumeSteps);
            writer.WriteIndex(info.CardIndex);
        }

        if (writer.ProtocolVersion >= 16)
        {
            writer.WriteU32((uint)info.Ports.Count);
            foreach (var port in info.Ports)
                writer.WritePortInfo(port);

            string? activePortName = info.ActivePortIndex < info.Ports.Count
                ? info.Ports[(int)info.ActivePortIndex].Name
                : null;
            writer.WriteString(activePortName);
        }
    }

    /// <summary>
    /// Reads SourceInfo from a tagstruct.
    /// </summary>
    public static SourceInfo ReadSourceInfo(this TagStructReader reader)
    {
        var info = new SourceInfo
        {
            Index = reader.ReadU32(),
            Name = reader.ReadString() ?? string.Empty,
            Description = reader.ReadString() ?? string.Empty,
            SampleSpec = reader.ReadSampleSpec(),
            ChannelMap = reader.ReadChannelMap(),
            OwnerModuleIndex = reader.ReadIndex(),
            Volume = reader.ReadChannelVolume(),
            Mute = reader.ReadBool(),
            MonitorSinkIndex = reader.ReadIndex() ?? Constants.InvalidIndex,
            MonitorSinkName = reader.ReadString(),
            ActualLatency = reader.ReadUsec(),
            Driver = reader.ReadString() ?? string.Empty,
            Flags = reader.ReadU32(),
            Props = reader.ReadProps(),
        };

        if (reader.ProtocolVersion >= 13)
            info.ConfiguredLatency = reader.ReadUsec();

        if (reader.ProtocolVersion >= 15)
        {
            info.BaseVolume = reader.ReadVolume();
            info.State = (SourceState)reader.ReadU32();
            info.NVolumeSteps = reader.ReadU32();
            info.CardIndex = reader.ReadIndex();
        }

        if (reader.ProtocolVersion >= 16)
        {
            uint numPorts = reader.ReadU32();
            for (uint i = 0; i < numPorts; i++)
                info.Ports.Add(reader.ReadPortInfo());

            string? activePortName = reader.ReadString();
            if (!string.IsNullOrEmpty(activePortName))
            {
                int activePortIndex = info.Ports.FindIndex(port => port.Name == activePortName);
                info.ActivePortIndex = activePortIndex >= 0 ? (uint)activePortIndex : 0;
            }
        }

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
        writer.WriteChannelMap(info.ChannelMap ?? new ChannelMap());
        writer.WriteIndex(info.OwnerModuleIndex);
        writer.WriteChannelVolume(info.Volume);
        writer.WriteBool(info.Mute);
        writer.WriteIndex(info.MonitorSinkIndex == Constants.InvalidIndex ? null : info.MonitorSinkIndex);
        writer.WriteString(info.MonitorSinkName);
        writer.WriteUsec(info.ActualLatency);
        writer.WriteString(info.Driver);
        writer.WriteU32(info.Flags);
        writer.WriteProps(info.Props);

        if (writer.ProtocolVersion >= 13)
            writer.WriteUsec(info.ConfiguredLatency);

        if (writer.ProtocolVersion >= 15)
        {
            writer.WriteVolume(info.BaseVolume);
            writer.WriteU32((uint)info.State);
            writer.WriteU32(info.NVolumeSteps);
            writer.WriteIndex(info.CardIndex);
        }

        if (writer.ProtocolVersion >= 16)
        {
            writer.WriteU32((uint)info.Ports.Count);
            foreach (var port in info.Ports)
                writer.WritePortInfo(port);

            string? activePortName = info.ActivePortIndex < info.Ports.Count
                ? info.Ports[(int)info.ActivePortIndex].Name
                : null;
            writer.WriteString(activePortName);
        }
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
