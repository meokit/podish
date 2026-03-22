namespace Podish.Pulse.Protocol.Commands;

/// <summary>
/// Authentication parameters for the protocol handshake.
/// </summary>
public sealed class AuthParams : IEquatable<AuthParams>
{
    /// <summary>
    /// Protocol version supported by the client.
    /// </summary>
    public ushort Version;
    
    /// <summary>
    /// Whether the client supports shared memory.
    /// </summary>
    public bool SupportsShm;
    
    /// <summary>
    /// Whether the client supports memfd.
    /// </summary>
    public bool SupportsMemfd;
    
    /// <summary>
    /// The authentication cookie.
    /// </summary>
    public byte[] Cookie;

    /// <summary>
    /// Creates a new AuthParams instance.
    /// </summary>
    public AuthParams()
    {
        Cookie = Array.Empty<byte>();
    }

    /// <summary>
    /// Creates a new AuthParams instance with the specified values.
    /// </summary>
    /// <param name="version">Protocol version.</param>
    /// <param name="supportsShm">Whether shared memory is supported.</param>
    /// <param name="supportsMemfd">Whether memfd is supported.</param>
    /// <param name="cookie">Authentication cookie.</param>
    public AuthParams(ushort version, bool supportsShm, bool supportsMemfd, byte[] cookie)
    {
        Version = version;
        SupportsShm = supportsShm;
        SupportsMemfd = supportsMemfd;
        Cookie = cookie ?? Array.Empty<byte>();
    }

    public bool Equals(AuthParams? other)
    {
        if (other is null) return false;
        return Version == other.Version && 
               SupportsShm == other.SupportsShm && 
               SupportsMemfd == other.SupportsMemfd && 
               Cookie.SequenceEqual(other.Cookie);
    }

    public override bool Equals(object? obj)
    {
        return obj is AuthParams other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Version, SupportsShm, SupportsMemfd, Cookie);
    }
}

/// <summary>
/// Authentication reply from the server.
/// </summary>
public sealed class AuthReply : IEquatable<AuthReply>
{
    /// <summary>
    /// Protocol version supported by the server.
    /// </summary>
    public ushort Version;

    /// <summary>
    /// Whether shared memory memblocks should be used.
    /// </summary>
    public bool UseShm;

    /// <summary>
    /// Whether memfd memblocks should be used.
    /// </summary>
    public bool UseMemfd;

    public bool Equals(AuthReply? other)
    {
        return other is not null &&
               Version == other.Version &&
               UseShm == other.UseShm &&
               UseMemfd == other.UseMemfd;
    }

    public override bool Equals(object? obj)
    {
        return obj is AuthReply other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Version, UseShm, UseMemfd);
    }
}

/// <summary>
/// Extension methods for reading and writing AuthParams.
/// </summary>
public static class AuthExtensions
{
    private const uint MemfdFlag = 0x40000000;
    private const uint ShmFlag = 0x80000000;
    private const uint VersionMask = 0x0000FFFF;

    /// <summary>
    /// Reads AuthParams from a tagstruct.
    /// </summary>
    /// <param name="reader">The reader.</param>
    /// <returns>The auth params.</returns>
    public static AuthParams ReadAuthParams(this TagStructReader reader)
    {
        uint packedVersion = reader.ReadU32();
        var auth = new AuthParams
        {
            Version = (ushort)(packedVersion & VersionMask),
            SupportsShm = (packedVersion & ShmFlag) != 0,
            SupportsMemfd = (packedVersion & MemfdFlag) != 0,
        };

        auth.Cookie = reader.ReadArbitrary();
        return auth;
    }

    /// <summary>
    /// Writes AuthParams to a tagstruct.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="auth">The auth params.</param>
    public static void WriteAuthParams(this TagStructWriter writer, AuthParams auth)
    {
        uint packedVersion = auth.Version;
        if (auth.SupportsShm)
            packedVersion |= ShmFlag;
        if (auth.SupportsMemfd)
            packedVersion |= MemfdFlag;

        writer.WriteU32(packedVersion);
        writer.WriteArbitrary(auth.Cookie);
    }

    /// <summary>
    /// Reads AuthReply from a tagstruct.
    /// </summary>
    /// <param name="reader">The reader.</param>
    /// <returns>The auth reply.</returns>
    public static AuthReply ReadAuthReply(this TagStructReader reader)
    {
        uint packedReply = reader.ReadU32();
        return new AuthReply
        {
            Version = (ushort)(packedReply & VersionMask),
            UseShm = (packedReply & ShmFlag) != 0,
            UseMemfd = (packedReply & MemfdFlag) != 0,
        };
    }

    /// <summary>
    /// Writes AuthReply to a tagstruct.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="reply">The auth reply.</param>
    public static void WriteAuthReply(this TagStructWriter writer, AuthReply reply)
    {
        uint packedReply = reply.Version;
        if (reply.UseShm)
            packedReply |= ShmFlag;
        if (reply.UseMemfd)
            packedReply |= MemfdFlag;

        writer.WriteU32(packedReply);
    }
}
