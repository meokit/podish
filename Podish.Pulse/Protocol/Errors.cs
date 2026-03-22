namespace Podish.Pulse.Protocol;

/// <summary>
/// A generic protocol error.
/// </summary>
public class ProtocolException : Exception
{
    public ProtocolException(string message) : base(message) { }
    public ProtocolException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// The version is not supported by this library.
/// </summary>
public sealed class UnsupportedVersionException : ProtocolException
{
    public ushort Version { get; }
    public UnsupportedVersionException(ushort version) 
        : base($"Unsupported protocol version: {version}") 
        => Version = version;
}

/// <summary>
/// A command other than what we were expecting was received.
/// </summary>
public sealed class UnexpectedCommandException : ProtocolException
{
    public CommandTag Expected { get; }
    public CommandTag Actual { get; }
    public UnexpectedCommandException(CommandTag expected, CommandTag actual) 
        : base($"Unexpected command: expected {expected}, got {actual}") 
        => (Expected, Actual) = (expected, actual);
}

/// <summary>
/// The message is invalid.
/// </summary>
public sealed class InvalidProtocolMessageException : ProtocolException
{
    public InvalidProtocolMessageException(string message) : base($"Invalid IPC message: {message}") { }
}

/// <summary>
/// The command is not yet implemented.
/// </summary>
public sealed class UnimplementedCommandException : ProtocolException
{
    public uint Sequence { get; }
    public CommandTag Command { get; }
    public UnimplementedCommandException(uint sequence, CommandTag command) 
        : base($"Unimplemented command: {command} (seq {sequence})") 
        => (Sequence, Command) = (sequence, command);
}

/// <summary>
/// An error from a remote server.
/// </summary>
public sealed class ServerErrorException : ProtocolException
{
    public PulseError Error { get; }
    public ServerErrorException(PulseError error) 
        : base($"Server error: {error}") 
        => Error = error;
}

/// <summary>
/// Timeout received from server.
/// </summary>
public sealed class TimeoutException : ProtocolException
{
    public TimeoutException() : base("Timeout received from server") { }
}

/// <summary>
/// An error code understood by the PulseAudio protocol.
/// Can be sent to clients to inform them of a specific error.
/// </summary>
public enum PulseError : uint
{
    /// <summary>Access failure</summary>
    AccessDenied = 1,
    /// <summary>Unknown command</summary>
    Command = 2,
    /// <summary>Invalid argument</summary>
    Invalid = 3,
    /// <summary>Entity exists</summary>
    Exist = 4,
    /// <summary>No such entity</summary>
    NoEntity = 5,
    /// <summary>Connection refused</summary>
    ConnectionRefused = 6,
    /// <summary>Protocol error</summary>
    Protocol = 7,
    /// <summary>Timeout</summary>
    Timeout = 8,
    /// <summary>No authentication key</summary>
    AuthKey = 9,
    /// <summary>Internal error</summary>
    Internal = 10,
    /// <summary>Connection terminated</summary>
    ConnectionTerminated = 11,
    /// <summary>Entity killed</summary>
    Killed = 12,
    /// <summary>Invalid server</summary>
    InvalidServer = 13,
    /// <summary>Module initialization failed</summary>
    ModInitFailed = 14,
    /// <summary>Bad state</summary>
    BadState = 15,
    /// <summary>No data</summary>
    NoData = 16,
    /// <summary>Incompatible protocol version</summary>
    Version = 17,
    /// <summary>Data too large</summary>
    TooLarge = 18,
    /// <summary>Operation not supported (since 0.9.5)</summary>
    NotSupported = 19,
    /// <summary>The error code was unknown to the client</summary>
    Unknown = 20,
    /// <summary>Extension does not exist. (since 0.9.12)</summary>
    NoExtension = 21,
    /// <summary>Obsolete functionality. (since 0.9.15)</summary>
    Obsolete = 22,
    /// <summary>Missing implementation. (since 0.9.15)</summary>
    NotImplemented = 23,
    /// <summary>The caller forked without calling execve() and tried to reuse the context.</summary>
    Forked = 24,
    /// <summary>An IO error happened. (since 0.9.16)</summary>
    Io = 25,
    /// <summary>Device or resource busy. (since 0.9.17)</summary>
    Busy = 26,
}
