namespace Podish.Pulse.Protocol;

/// <summary>
/// Represents a PulseAudio protocol message with a command tag, sequence number, and payload.
/// </summary>
public sealed class ProtocolMessage
{
    /// <summary>
    /// The command tag identifying the type of message.
    /// </summary>
    public CommandTag CommandTag { get; set; }
    
    /// <summary>
    /// The sequence number of the message.
    /// </summary>
    public uint Sequence { get; set; }
    
    /// <summary>
    /// The payload of the message as a byte array.
    /// </summary>
    public byte[] Payload { get; set; }

    /// <summary>
    /// Creates a new ProtocolMessage instance.
    /// </summary>
    public ProtocolMessage()
    {
        Payload = Array.Empty<byte>();
    }

    /// <summary>
    /// Creates a new ProtocolMessage instance with the specified values.
    /// </summary>
    /// <param name="commandTag">The command tag.</param>
    /// <param name="sequence">The sequence number.</param>
    /// <param name="payload">The payload.</param>
    public ProtocolMessage(CommandTag commandTag, uint sequence, byte[] payload)
    {
        CommandTag = commandTag;
        Sequence = sequence;
        Payload = payload ?? Array.Empty<byte>();
    }

    /// <summary>
    /// Creates a new ProtocolMessage instance with a tagstruct payload.
    /// </summary>
    /// <param name="commandTag">The command tag.</param>
    /// <param name="sequence">The sequence number.</param>
    /// <param name="writerAction">An action to write the tagstruct payload.</param>
    /// <param name="protocolVersion">The protocol version.</param>
    /// <returns>The protocol message.</returns>
    public static ProtocolMessage Create(CommandTag commandTag, uint sequence, Action<TagStructWriter> writerAction, ushort protocolVersion = Constants.MaxVersion)
    {
        var writer = new TagStructWriter(protocolVersion);
        writerAction(writer);
        return new ProtocolMessage(commandTag, sequence, writer.ToArray());
    }

    /// <summary>
    /// Reads the payload as a tagstruct.
    /// </summary>
    /// <param name="protocolVersion">The protocol version.</param>
    /// <returns>A TagStructReader for the payload.</returns>
    public TagStructReader ReadPayload(ushort protocolVersion = Constants.MaxVersion)
    {
        return new TagStructReader(Payload, protocolVersion);
    }
}

/// <summary>
/// Provides methods for encoding and decoding protocol messages.
/// </summary>
public static class ProtocolMessageIO
{
    /// <summary>
    /// Encodes a protocol message to a byte array with the descriptor header.
    /// </summary>
    /// <param name="message">The message to encode.</param>
    /// <param name="protocolVersion">The protocol version.</param>
    /// <returns>The encoded byte array.</returns>
    public static byte[] Encode(ProtocolMessage message, ushort protocolVersion = Constants.MaxVersion)
    {
        // Create tagstruct payload with command tag and sequence
        var writer = new TagStructWriter(protocolVersion);
        writer.WriteU32((uint)message.CommandTag);
        writer.WriteU32(message.Sequence);
        writer.Stream.Write(message.Payload);
        
        byte[] payload = writer.ToArray();
        
        // Create descriptor
        var descriptor = new Descriptor
        {
            Length = (uint)payload.Length,
            Channel = uint.MaxValue, // Control packet
            Offset = 0,
            Flags = DescriptorFlags.None,
        };
        
        // Combine descriptor and payload
        byte[] result = new byte[Constants.DescriptorSize + payload.Length];
        DescriptorIO.Write(result, descriptor);
        Array.Copy(payload, 0, result, Constants.DescriptorSize, payload.Length);
        
        return result;
    }

    /// <summary>
    /// Decodes a protocol message from a byte array with the descriptor header.
    /// </summary>
    /// <param name="buffer">The buffer to decode.</param>
    /// <param name="protocolVersion">The protocol version.</param>
    /// <returns>The decoded protocol message.</returns>
    /// <exception cref="InvalidProtocolMessageException">Thrown when the message is invalid.</exception>
    public static ProtocolMessage Decode(ReadOnlySpan<byte> buffer, ushort protocolVersion = Constants.MaxVersion)
    {
        if (buffer.Length < Constants.DescriptorSize)
            throw new InvalidProtocolMessageException("Buffer too small for descriptor");
        
        Descriptor descriptor = DescriptorIO.Read(buffer);
        
        if (descriptor.Length > buffer.Length - Constants.DescriptorSize)
            throw new InvalidProtocolMessageException($"Payload length {descriptor.Length} exceeds buffer size {buffer.Length - Constants.DescriptorSize}");
        
        ReadOnlySpan<byte> payload = buffer.Slice(Constants.DescriptorSize, (int)descriptor.Length);
        
        var reader = new TagStructReader(payload.ToArray(), protocolVersion);
        
        CommandTag commandTag = reader.ReadEnum<CommandTag>();
        uint sequence = reader.ReadU32();
        
        // Remaining data is the command-specific payload
        byte[] commandPayload = reader.Remaining.ToArray();
        
        return new ProtocolMessage(commandTag, sequence, commandPayload);
    }

    /// <summary>
    /// Encodes a reply message to a byte array with the descriptor header.
    /// </summary>
    /// <typeparam name="T">The reply type.</typeparam>
    /// <param name="sequence">The sequence number.</param>
    /// <param name="reply">The reply data.</param>
    /// <param name="writerAction">An action to write the reply data.</param>
    /// <returns>The encoded byte array.</returns>
    public static byte[] EncodeReply<T>(uint sequence, T reply, Action<TagStructWriter, T> writerAction)
    {
        var writer = new TagStructWriter(Constants.MaxVersion);
        writer.WriteU32((uint)CommandTag.Reply);
        writer.WriteU32(sequence);
        writerAction(writer, reply);
        
        byte[] payload = writer.ToArray();
        
        var descriptor = new Descriptor
        {
            Length = (uint)payload.Length,
            Channel = uint.MaxValue,
            Offset = 0,
            Flags = DescriptorFlags.None,
        };
        
        byte[] result = new byte[Constants.DescriptorSize + payload.Length];
        DescriptorIO.Write(result, descriptor);
        Array.Copy(payload, 0, result, Constants.DescriptorSize, payload.Length);
        
        return result;
    }

    /// <summary>
    /// Decodes a reply message from a byte array.
    /// </summary>
    /// <typeparam name="T">The reply type.</typeparam>
    /// <param name="buffer">The buffer to decode.</param>
    /// <param name="readerAction">An action to read the reply data.</param>
    /// <param name="protocolVersion">The protocol version.</param>
    /// <returns>The sequence number and reply data.</returns>
    /// <exception cref="ServerErrorException">Thrown when the server returns an error.</exception>
    /// <exception cref="InvalidProtocolMessageException">Thrown when the message is invalid.</exception>
    public static (uint Sequence, T Reply) DecodeReply<T>(ReadOnlySpan<byte> buffer, Func<TagStructReader, T> readerAction, ushort protocolVersion = Constants.MaxVersion)
    {
        if (buffer.Length < Constants.DescriptorSize)
            throw new InvalidProtocolMessageException("Buffer too small for descriptor");
        
        Descriptor descriptor = DescriptorIO.Read(buffer);
        
        if (descriptor.Length > buffer.Length - Constants.DescriptorSize)
            throw new InvalidProtocolMessageException($"Payload length {descriptor.Length} exceeds buffer size");
        
        ReadOnlySpan<byte> payload = buffer.Slice(Constants.DescriptorSize, (int)descriptor.Length);
        var reader = new TagStructReader(payload.ToArray(), protocolVersion);
        
        CommandTag commandTag = reader.ReadEnum<CommandTag>();
        uint sequence = reader.ReadU32();
        
        if (commandTag == CommandTag.Error)
        {
            PulseError error = reader.ReadEnum<PulseError>();
            throw new ServerErrorException(error);
        }
        
        if (commandTag != CommandTag.Reply)
        {
            throw new InvalidProtocolMessageException($"Expected Reply, got {commandTag}");
        }
        
        T reply = readerAction(reader);
        return (sequence, reply);
    }

    /// <summary>
    /// Encodes an acknowledgment message.
    /// </summary>
    /// <param name="sequence">The sequence number.</param>
    /// <returns>The encoded byte array.</returns>
    public static byte[] EncodeAck(uint sequence)
    {
        var writer = new TagStructWriter(Constants.MaxVersion);
        writer.WriteU32((uint)CommandTag.Reply);
        writer.WriteU32(sequence);
        
        byte[] payload = writer.ToArray();
        
        var descriptor = new Descriptor
        {
            Length = (uint)payload.Length,
            Channel = uint.MaxValue,
            Offset = 0,
            Flags = DescriptorFlags.None,
        };
        
        byte[] result = new byte[Constants.DescriptorSize + payload.Length];
        DescriptorIO.Write(result, descriptor);
        Array.Copy(payload, 0, result, Constants.DescriptorSize, payload.Length);
        
        return result;
    }

    /// <summary>
    /// Decodes an acknowledgment message.
    /// </summary>
    /// <param name="buffer">The buffer to decode.</param>
    /// <param name="protocolVersion">The protocol version.</param>
    /// <returns>The sequence number.</returns>
    /// <exception cref="ServerErrorException">Thrown when the server returns an error.</exception>
    public static uint DecodeAck(ReadOnlySpan<byte> buffer, ushort protocolVersion = Constants.MaxVersion)
    {
        if (buffer.Length < Constants.DescriptorSize)
            throw new InvalidProtocolMessageException("Buffer too small for descriptor");
        
        Descriptor descriptor = DescriptorIO.Read(buffer);
        ReadOnlySpan<byte> payload = buffer.Slice(Constants.DescriptorSize, (int)descriptor.Length);
        var reader = new TagStructReader(payload.ToArray(), protocolVersion);
        
        CommandTag commandTag = reader.ReadEnum<CommandTag>();
        uint sequence = reader.ReadU32();
        
        if (commandTag == CommandTag.Error)
        {
            PulseError error = reader.ReadEnum<PulseError>();
            throw new ServerErrorException(error);
        }
        
        if (commandTag != CommandTag.Reply)
        {
            throw new InvalidProtocolMessageException($"Expected Reply, got {commandTag}");
        }
        
        return sequence;
    }
}
