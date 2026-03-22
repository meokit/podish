namespace Podish.Pulse.Protocol;

/// <summary>
/// Protocol constants.
/// </summary>
public static class Constants
{
    /// <summary>
    /// Minimum protocol version understood by the library.
    /// </summary>
    public const ushort MinVersion = 13;

    /// <summary>
    /// PulseAudio protocol version implemented by this library.
    /// This library can still work with clients and servers down to <see cref="MinVersion"/> 
    /// and up to any higher version, but features added by versions higher than this are not supported.
    /// </summary>
    public const ushort MaxVersion = 35;

    /// <summary>
    /// The size of a message header in bytes.
    /// </summary>
    public const int DescriptorSize = 20; // 5 * 4 bytes

    /// <summary>
    /// Maximum memblockq length from the Pulse source. This is the maximum packet size
    /// for stream data, as well as the maximum buffer size, in bytes.
    /// </summary>
    public const int MaxMemblockqLength = 4 * 1024 * 1024;

    /// <summary>
    /// Maximum sample rate in Hz.
    /// </summary>
    public const uint MaxRate = 48000 * 16;

    /// <summary>
    /// Maximum number of channels supported for streams.
    /// </summary>
    public const byte MaxChannels = 32;

    /// <summary>
    /// Maximum size of a proplist value in bytes.
    /// </summary>
    public const uint MaxPropSize = 64 * 1024;

    /// <summary>
    /// The protocol uses this sink name to indicate the default sink.
    /// </summary>
    public const string DefaultSink = "@DEFAULT_SINK@";

    /// <summary>
    /// The protocol uses this source name to indicate the default source.
    /// </summary>
    public const string DefaultSource = "@DEFAULT_SOURCE@";

    /// <summary>
    /// Invalid index constant (-1 when cast to signed).
    /// </summary>
    public const uint InvalidIndex = uint.MaxValue;
}
