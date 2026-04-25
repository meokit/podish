using System.Runtime.InteropServices;

namespace Podish.Pulse.Protocol;

/// <summary>
/// Special message types.
/// </summary>
[Flags]
public enum DescriptorFlags : uint
{
    /// <summary>
    /// No flags set.
    /// </summary>
    None = 0,
    
    /// <summary>
    /// Indicates a SHMRELEASE message.
    /// </summary>
    FlagShmRelease = 0x40000000,
    
    /// <summary>
    /// Indicates a SHMREVOKE message.
    /// </summary>
    FlagShmRevoke = 0xC0000000,
}

/// <summary>
/// Packet descriptor / header.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Descriptor
{
    /// <summary>
    /// Payload length in Bytes.
    /// </summary>
    public uint Length;
    
    /// <summary>
    /// The channel this packet belongs to, or -1 for a control packet.
    /// </summary>
    public uint Channel;
    
    /// <summary>
    /// Offset into the memblock, in Bytes.
    /// </summary>
    public ulong Offset;
    
    /// <summary>
    /// SHMRELEASE or SHMREVOKE to mark packet as such, or:
    /// For memblock packets: Lowest byte: Seek mode
    /// </summary>
    public DescriptorFlags Flags;
}

/// <summary>
/// Provides methods for reading and writing protocol descriptors.
/// </summary>
public static class DescriptorIO
{
    /// <summary>
    /// Reads a message header from a byte span.
    /// </summary>
    /// <param name="buffer">The buffer to read from (must be at least <see cref="Constants.DescriptorSize"/> bytes).</param>
    /// <returns>The parsed descriptor.</returns>
    /// <exception cref="InvalidProtocolMessageException">Thrown when the buffer is too small.</exception>
    public static Descriptor Read(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < Constants.DescriptorSize)
            throw new InvalidProtocolMessageException($"Buffer too small for descriptor: expected {Constants.DescriptorSize}, got {buffer.Length}");
        
        return new Descriptor
        {
            Length = ReadUInt32BE(buffer.Slice(0, 4)),
            Channel = ReadUInt32BE(buffer.Slice(4, 4)),
            Offset = ReadUInt64BE(buffer.Slice(8, 8)),
            Flags = (DescriptorFlags)ReadUInt32BE(buffer.Slice(16, 4)),
        };
    }
    
    /// <summary>
    /// Writes a message header to a byte span.
    /// </summary>
    /// <param name="buffer">The buffer to write to (must be at least <see cref="Constants.DescriptorSize"/> bytes).</param>
    /// <param name="descriptor">The descriptor to write.</param>
    /// <exception cref="InvalidProtocolMessageException">Thrown when the buffer is too small.</exception>
    public static void Write(Span<byte> buffer, Descriptor descriptor)
    {
        if (buffer.Length < Constants.DescriptorSize)
            throw new InvalidProtocolMessageException($"Buffer too small for descriptor: expected {Constants.DescriptorSize}, got {buffer.Length}");
        
        WriteUInt32BE(buffer.Slice(0, 4), descriptor.Length);
        WriteUInt32BE(buffer.Slice(4, 4), descriptor.Channel);
        WriteUInt64BE(buffer.Slice(8, 8), descriptor.Offset);
        WriteUInt32BE(buffer.Slice(16, 4), (uint)descriptor.Flags);
    }
    
    /// <summary>
    /// Encodes a descriptor to a fixed-size buffer.
    /// </summary>
    /// <param name="descriptor">The descriptor to encode.</param>
    /// <returns>A 20-byte array containing the encoded descriptor.</returns>
    public static byte[] Encode(Descriptor descriptor)
    {
        byte[] buffer = new byte[Constants.DescriptorSize];
        Write(buffer, descriptor);
        return buffer;
    }
    
    private static uint ReadUInt32BE(ReadOnlySpan<byte> buffer)
    {
        return (uint)(buffer[0] << 24 | buffer[1] << 16 | buffer[2] << 8 | buffer[3]);
    }
    
    private static ulong ReadUInt64BE(ReadOnlySpan<byte> buffer)
    {
        return ((ulong)buffer[0] << 56) | ((ulong)buffer[1] << 48) | ((ulong)buffer[2] << 40) | ((ulong)buffer[3] << 32) |
               ((ulong)buffer[4] << 24) | ((ulong)buffer[5] << 16) | ((ulong)buffer[6] << 8) | buffer[7];
    }
    
    private static void WriteUInt32BE(Span<byte> buffer, uint value)
    {
        buffer[0] = (byte)(value >> 24);
        buffer[1] = (byte)(value >> 16);
        buffer[2] = (byte)(value >> 8);
        buffer[3] = (byte)value;
    }
    
    private static void WriteUInt64BE(Span<byte> buffer, ulong value)
    {
        buffer[0] = (byte)(value >> 56);
        buffer[1] = (byte)(value >> 48);
        buffer[2] = (byte)(value >> 40);
        buffer[3] = (byte)(value >> 32);
        buffer[4] = (byte)(value >> 24);
        buffer[5] = (byte)(value >> 16);
        buffer[6] = (byte)(value >> 8);
        buffer[7] = (byte)value;
    }
}
