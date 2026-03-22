namespace Podish.Pulse.Protocol.Commands;

/// <summary>
/// Statistics returned by the Stat command.
/// </summary>
public sealed class StatReply : IEquatable<StatReply>
{
    /// <summary>
    /// Total number of bytes allocated for memblocks.
    /// </summary>
    public ulong MemblockTotal { get; set; }
    
    /// <summary>
    /// Number of bytes currently in use.
    /// </summary>
    public ulong MemblockUsed { get; set; }
    
    /// <summary>
    /// Total number of bytes allocated for memblocks on the server.
    /// </summary>
    public ulong MemblockTotalServer { get; set; }
    
    /// <summary>
    /// Number of bytes currently in use on the server.
    /// </summary>
    public ulong MemblockUsedServer { get; set; }
    
    /// <summary>
    /// Number of bytes received.
    /// </summary>
    public ulong BytesReceived { get; set; }
    
    /// <summary>
    /// Number of bytes sent.
    /// </summary>
    public ulong BytesSent { get; set; }
    
    /// <summary>
    /// Number of entries in the memblock pool.
    /// </summary>
    public uint MemblockPoolSize { get; set; }
    
    /// <summary>
    /// Number of bytes currently in the memblock pool.
    /// </summary>
    public uint MemblockPoolUsed { get; set; }
    
    /// <summary>
    /// Number of memblocks in the pool.
    /// </summary>
    public uint MemblockPoolAllocated { get; set; }
    
    /// <summary>
    /// Total number of samples.
    /// </summary>
    public uint SamplesTotal { get; set; }
    
    /// <summary>
    /// Number of bytes allocated for input buffers.
    /// </summary>
    public ulong InputBytesTotal { get; set; }
    
    /// <summary>
    /// Number of bytes allocated for output buffers.
    /// </summary>
    public ulong OutputBytesTotal { get; set; }

    public bool Equals(StatReply? other)
    {
        if (other is null) return false;
        return MemblockTotal == other.MemblockTotal &&
               MemblockUsed == other.MemblockUsed &&
               MemblockTotalServer == other.MemblockTotalServer &&
               MemblockUsedServer == other.MemblockUsedServer &&
               BytesReceived == other.BytesReceived &&
               BytesSent == other.BytesSent &&
               MemblockPoolSize == other.MemblockPoolSize &&
               MemblockPoolUsed == other.MemblockPoolUsed &&
               MemblockPoolAllocated == other.MemblockPoolAllocated &&
               SamplesTotal == other.SamplesTotal &&
               InputBytesTotal == other.InputBytesTotal &&
               OutputBytesTotal == other.OutputBytesTotal;
    }

    public override bool Equals(object? obj)
    {
        return obj is StatReply other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(MemblockTotal, MemblockUsed, BytesReceived, BytesSent);
    }
}

/// <summary>
/// Extension methods for reading and writing StatReply.
/// </summary>
public static class StatExtensions
{
    /// <summary>
    /// Reads StatReply from a tagstruct.
    /// </summary>
    /// <param name="reader">The reader.</param>
    /// <returns>The stat reply.</returns>
    public static StatReply ReadStatReply(this TagStructReader reader)
    {
        return new StatReply
        {
            MemblockTotal = reader.ReadU64(),
            MemblockUsed = reader.ReadU64(),
            MemblockTotalServer = reader.ReadU64(),
            MemblockUsedServer = reader.ReadU64(),
            BytesReceived = reader.ReadU64(),
            BytesSent = reader.ReadU64(),
            MemblockPoolSize = reader.ReadU32(),
            MemblockPoolUsed = reader.ReadU32(),
            MemblockPoolAllocated = reader.ReadU32(),
            SamplesTotal = reader.ReadU32(),
            InputBytesTotal = reader.ReadU64(),
            OutputBytesTotal = reader.ReadU64(),
        };
    }

    /// <summary>
    /// Writes StatReply to a tagstruct.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="reply">The stat reply.</param>
    public static void WriteStatReply(this TagStructWriter writer, StatReply reply)
    {
        writer.WriteU64(reply.MemblockTotal);
        writer.WriteU64(reply.MemblockUsed);
        writer.WriteU64(reply.MemblockTotalServer);
        writer.WriteU64(reply.MemblockUsedServer);
        writer.WriteU64(reply.BytesReceived);
        writer.WriteU64(reply.BytesSent);
        writer.WriteU32(reply.MemblockPoolSize);
        writer.WriteU32(reply.MemblockPoolUsed);
        writer.WriteU32(reply.MemblockPoolAllocated);
        writer.WriteU32(reply.SamplesTotal);
        writer.WriteU64(reply.InputBytesTotal);
        writer.WriteU64(reply.OutputBytesTotal);
    }
}
