namespace Podish.Pulse.Protocol.Commands;

/// <summary>
/// Server information returned by the GetServerInfo command.
/// </summary>
public sealed class ServerInfo : IEquatable<ServerInfo>
{
    /// <summary>
    /// Server "package name" (usually "pulseaudio").
    /// </summary>
    public string? ServerName { get; set; }

    /// <summary>
    /// Version string of the daemon.
    /// </summary>
    public string? ServerVersion { get; set; }

    /// <summary>
    /// User name of the daemon process.
    /// </summary>
    public string? UserName { get; set; }

    /// <summary>
    /// Host name the daemon is running on.
    /// </summary>
    public string? HostName { get; set; }

    /// <summary>
    /// The default sample specification.
    /// </summary>
    public SampleSpec SampleSpec { get; set; }

    /// <summary>
    /// Name of the current default sink.
    /// </summary>
    public string? DefaultSinkName { get; set; }

    /// <summary>
    /// Name of the current default source.
    /// </summary>
    public string? DefaultSourceName { get; set; }

    /// <summary>
    /// A random ID to identify the server.
    /// </summary>
    public uint Cookie { get; set; }

    /// <summary>
    /// Channel map for the default sink.
    /// </summary>
    public ChannelMap ChannelMap { get; set; }

    /// <summary>
    /// Creates a new ServerInfo instance.
    /// </summary>
    public ServerInfo()
    {
        SampleSpec = new SampleSpec();
        ChannelMap = new ChannelMap();
    }

    public bool Equals(ServerInfo? other)
    {
        if (other is null) return false;
        return ServerName == other.ServerName &&
               ServerVersion == other.ServerVersion &&
               UserName == other.UserName &&
               HostName == other.HostName &&
               SampleSpec == other.SampleSpec &&
               DefaultSinkName == other.DefaultSinkName &&
               DefaultSourceName == other.DefaultSourceName &&
               Cookie == other.Cookie &&
               ChannelMap.Equals(other.ChannelMap);
    }

    public override bool Equals(object? obj)
    {
        return obj is ServerInfo other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + (ServerName?.GetHashCode() ?? 0);
            hash = hash * 31 + (ServerVersion?.GetHashCode() ?? 0);
            hash = hash * 31 + (UserName?.GetHashCode() ?? 0);
            hash = hash * 31 + (HostName?.GetHashCode() ?? 0);
            hash = hash * 31 + SampleSpec.GetHashCode();
            hash = hash * 31 + (DefaultSinkName?.GetHashCode() ?? 0);
            hash = hash * 31 + (DefaultSourceName?.GetHashCode() ?? 0);
            hash = hash * 31 + Cookie.GetHashCode();
            hash = hash * 31 + ChannelMap.GetHashCode();
            return hash;
        }
    }
}

/// <summary>
/// Extension methods for reading and writing ServerInfo.
/// </summary>
public static class ServerInfoExtensions
{
    /// <summary>
    /// Reads ServerInfo from a tagstruct.
    /// </summary>
    /// <param name="reader">The reader.</param>
    /// <returns>The server info.</returns>
    public static ServerInfo ReadServerInfo(this TagStructReader reader)
    {
        var info = new ServerInfo
        {
            ServerName = reader.ReadString(),
            ServerVersion = reader.ReadString(),
            UserName = reader.ReadString(),
            HostName = reader.ReadString(),
            SampleSpec = reader.ReadSampleSpec(),
            DefaultSinkName = reader.ReadString(),
            DefaultSourceName = reader.ReadString(),
            Cookie = reader.ReadU32(),
        };

        if (reader.ProtocolVersion >= 15 && reader.HasDataLeft())
            info.ChannelMap = reader.ReadChannelMap();

        return info;
    }

    /// <summary>
    /// Writes ServerInfo to a tagstruct.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="info">The server info.</param>
    public static void WriteServerInfo(this TagStructWriter writer, ServerInfo info)
    {
        writer.WriteString(info.ServerName);
        writer.WriteString(info.ServerVersion);
        writer.WriteString(info.UserName);
        writer.WriteString(info.HostName);
        writer.WriteSampleSpec(info.SampleSpec);
        writer.WriteString(info.DefaultSinkName);
        writer.WriteString(info.DefaultSourceName);
        writer.WriteU32(info.Cookie);

        if (writer.ProtocolVersion >= 15)
            writer.WriteChannelMap(info.ChannelMap);
    }
}
