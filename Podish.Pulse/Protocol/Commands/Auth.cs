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

    public bool Equals(AuthReply? other)
    {
        return other is not null && Version == other.Version;
    }

    public override bool Equals(object? obj)
    {
        return obj is AuthReply other && Equals(other);
    }

    public override int GetHashCode()
    {
        return Version.GetHashCode();
    }
}

/// <summary>
/// Extension methods for reading and writing AuthParams.
/// </summary>
public static class AuthExtensions
{
    /// <summary>
    /// Reads AuthParams from a tagstruct.
    /// </summary>
    /// <param name="reader">The reader.</param>
    /// <returns>The auth params.</returns>
    public static AuthParams ReadAuthParams(this TagStructReader reader)
    {
        var auth = new AuthParams
        {
            Version = (ushort)reader.ReadU32(),
            SupportsShm = reader.ReadBool(),
            SupportsMemfd = reader.ReadBool(),
        };
        
        // Read the cookie as arbitrary data
        byte[] cookie = reader.ReadArbitrary();
        auth.Cookie = cookie;
        
        return auth;
    }

    /// <summary>
    /// Writes AuthParams to a tagstruct.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="auth">The auth params.</param>
    public static void WriteAuthParams(this TagStructWriter writer, AuthParams auth)
    {
        writer.WriteU32(auth.Version);
        writer.WriteBool(auth.SupportsShm);
        writer.WriteBool(auth.SupportsMemfd);
        writer.WriteArbitrary(auth.Cookie);
    }

    /// <summary>
    /// Reads AuthReply from a tagstruct.
    /// </summary>
    /// <param name="reader">The reader.</param>
    /// <returns>The auth reply.</returns>
    public static AuthReply ReadAuthReply(this TagStructReader reader)
    {
        return new AuthReply
        {
            Version = (ushort)reader.ReadU32(),
        };
    }

    /// <summary>
    /// Writes AuthReply to a tagstruct.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="reply">The auth reply.</param>
    public static void WriteAuthReply(this TagStructWriter writer, AuthReply reply)
    {
        writer.WriteU32(reply.Version);
    }
}
