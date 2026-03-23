namespace Podish.Pulse.Protocol.Commands;

public sealed class ClientInfo
{
    public uint Index;
    public string Name = string.Empty;
    public uint? OwnerModuleIndex;
    public string Driver = string.Empty;
    public Props Props = new();
}

public sealed class SinkInputInfo
{
    public uint Index;
    public string? Name;
    public uint? OwnerModuleIndex;
    public uint ClientIndex;
    public uint SinkIndex;
    public SampleSpec SampleSpec = new();
    public ChannelMap ChannelMap = new();
    public ChannelVolume Volume = new();
    public ulong BufferLatencyUsec;
    public ulong SinkLatencyUsec;
    public string ResampleMethod = string.Empty;
    public string Driver = string.Empty;
    public bool Mute;
    public Props Props = new();
    public bool Corked;
    public bool HasVolume = true;
    public bool VolumeWritable = true;
}

public static class ClientSinkInputInfoExtensions
{
    public static void WriteClientInfo(this TagStructWriter writer, ClientInfo info)
    {
        writer.WriteU32(info.Index);
        writer.WriteString(info.Name);
        writer.WriteIndex(info.OwnerModuleIndex);
        writer.WriteString(info.Driver);

        if (writer.ProtocolVersion >= 13)
            writer.WriteProps(info.Props);
    }

    public static void WriteSinkInputInfo(this TagStructWriter writer, SinkInputInfo info)
    {
        writer.WriteU32(info.Index);
        writer.WriteString(info.Name);
        writer.WriteIndex(info.OwnerModuleIndex);
        writer.WriteU32(info.ClientIndex);
        writer.WriteU32(info.SinkIndex);
        writer.WriteSampleSpec(info.SampleSpec);
        writer.WriteChannelMap(info.ChannelMap);
        writer.WriteChannelVolume(info.Volume);
        writer.WriteUsec(info.BufferLatencyUsec);
        writer.WriteUsec(info.SinkLatencyUsec);
        writer.WriteString(info.ResampleMethod);
        writer.WriteString(info.Driver);

        if (writer.ProtocolVersion >= 11)
            writer.WriteBool(info.Mute);

        if (writer.ProtocolVersion >= 13)
            writer.WriteProps(info.Props);

        if (writer.ProtocolVersion >= 19)
            writer.WriteBool(info.Corked);

        if (writer.ProtocolVersion >= 20)
        {
            writer.WriteBool(info.HasVolume);
            writer.WriteBool(info.VolumeWritable);
        }
    }
}
