namespace Podish.Pulse.Protocol.Commands;

/// <summary>
/// Server information returned by the GetServerInfo command.
/// </summary>
public sealed class ServerInfo : IEquatable<ServerInfo>
{
    /// <summary>
    /// The server name (e.g., "PulseAudio on 1.2.3").
    /// </summary>
    public string? ServerName { get; set; }
    
    /// <summary>
    /// The index of the default sink.
    /// </summary>
    public uint? DefaultSinkIndex { get; set; }
    
    /// <summary>
    /// The index of the default source.
    /// </summary>
    public uint? DefaultSourceIndex { get; set; }
    
    /// <summary>
    /// A magic cookie for authentication.
    /// </summary>
    public byte[]? Cookie { get; set; }
    
    /// <summary>
    /// The default sample specification.
    /// </summary>
    public SampleSpec DefaultSampleSpec { get; set; }
    
    /// <summary>
    /// The default channel map.
    /// </summary>
    public ChannelMap? DefaultChannelMap { get; set; }

    /// <summary>
    /// Creates a new ServerInfo instance.
    /// </summary>
    public ServerInfo()
    {
        DefaultSampleSpec = new SampleSpec();
    }

    public bool Equals(ServerInfo? other)
    {
        if (other is null) return false;
        return ServerName == other.ServerName &&
               DefaultSinkIndex == other.DefaultSinkIndex &&
               DefaultSourceIndex == other.DefaultSourceIndex &&
               (Cookie == null && other.Cookie == null || Cookie != null && other.Cookie != null && Cookie.SequenceEqual(other.Cookie)) &&
               DefaultSampleSpec == other.DefaultSampleSpec &&
               (DefaultChannelMap == null && other.DefaultChannelMap == null || DefaultChannelMap?.Equals(other.DefaultChannelMap) == true);
    }

    public override bool Equals(object? obj)
    {
        return obj is ServerInfo other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(ServerName, DefaultSinkIndex, DefaultSourceIndex, DefaultSampleSpec);
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
            ServerName = reader.ReadStringNonNull(),
            DefaultSinkIndex = reader.ReadU32(),
            DefaultSourceIndex = reader.ReadU32(),
        };
        
        // Read cookie
        uint cookieLen = reader.ReadU32();
        if (cookieLen > 0)
        {
            info.Cookie = reader.ReadArbitrary();
        }
        
        info.DefaultSampleSpec = reader.ReadSampleSpec();
        
        // Default channel map is optional in older protocol versions
        if (reader.HasDataLeft() && reader.Remaining[0] == (byte)Tag.ChannelMap)
        {
            info.DefaultChannelMap = reader.ReadChannelMap();
        }
        
        return info;
    }

    /// <summary>
    /// Writes ServerInfo to a tagstruct.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="info">The server info.</param>
    public static void WriteServerInfo(this TagStructWriter writer, ServerInfo info)
    {
        writer.WriteString(info.ServerName ?? "");
        writer.WriteU32(info.DefaultSinkIndex ?? Constants.InvalidIndex);
        writer.WriteU32(info.DefaultSourceIndex ?? Constants.InvalidIndex);
        
        if (info.Cookie != null && info.Cookie.Length > 0)
        {
            writer.WriteU32((uint)info.Cookie.Length);
            writer.WriteArbitrary(info.Cookie);
        }
        else
        {
            writer.WriteU32(0);
        }
        
        writer.WriteSampleSpec(info.DefaultSampleSpec);
        
        if (info.DefaultChannelMap != null)
        {
            writer.WriteChannelMap(info.DefaultChannelMap);
        }
    }
}
