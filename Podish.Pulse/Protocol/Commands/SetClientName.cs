namespace Podish.Pulse.Protocol.Commands;

/// <summary>
/// Parameters for the SetClientName command.
/// This is used to set client properties including the application name.
/// </summary>
public sealed class SetClientNameParams : IEquatable<SetClientNameParams>
{
    /// <summary>
    /// The client properties.
    /// </summary>
    public Props Properties { get; set; }

    /// <summary>
    /// Creates a new SetClientNameParams instance.
    /// </summary>
    public SetClientNameParams()
    {
        Properties = new Props();
    }

    /// <summary>
    /// Creates a new SetClientNameParams instance with the specified properties.
    /// </summary>
    /// <param name="properties">The client properties.</param>
    public SetClientNameParams(Props properties)
    {
        Properties = properties;
    }

    public bool Equals(SetClientNameParams? other)
    {
        if (other is null) return false;
        // Compare properties by their content
        if (Properties.Count != other.Properties.Count) return false;
        foreach (var kvp in Properties)
        {
            if (!other.Properties.Contains(kvp.Key)) return false;
            if (!kvp.Value.SequenceEqual(other.Properties.GetBytes(kvp.Key)!)) return false;
        }
        return true;
    }

    public override bool Equals(object? obj)
    {
        return obj is SetClientNameParams other && Equals(other);
    }

    public override int GetHashCode()
    {
        return Properties.Count.GetHashCode();
    }
}

/// <summary>
/// Reply for the SetClientName command.
/// </summary>
public sealed class SetClientNameReply : IEquatable<SetClientNameReply>
{
    /// <summary>
    /// The client index assigned by the server.
    /// </summary>
    public uint ClientIndex { get; set; }

    public bool Equals(SetClientNameReply? other)
    {
        return other is not null && ClientIndex == other.ClientIndex;
    }

    public override bool Equals(object? obj)
    {
        return obj is SetClientNameReply other && Equals(other);
    }

    public override int GetHashCode()
    {
        return ClientIndex.GetHashCode();
    }
}

/// <summary>
/// Extension methods for reading and writing SetClientName commands.
/// </summary>
public static class SetClientNameExtensions
{
    /// <summary>
    /// Reads SetClientNameParams from a tagstruct.
    /// </summary>
    /// <param name="reader">The reader.</param>
    /// <returns>The set client name params.</returns>
    public static SetClientNameParams ReadSetClientNameParams(this TagStructReader reader)
    {
        return new SetClientNameParams
        {
            Properties = reader.ReadProps(),
        };
    }

    /// <summary>
    /// Writes SetClientNameParams to a tagstruct.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="params">The set client name params.</param>
    public static void WriteSetClientNameParams(this TagStructWriter writer, SetClientNameParams @params)
    {
        writer.WriteProps(@params.Properties);
    }

    /// <summary>
    /// Reads SetClientNameReply from a tagstruct.
    /// </summary>
    /// <param name="reader">The reader.</param>
    /// <returns>The set client name reply.</returns>
    public static SetClientNameReply ReadSetClientNameReply(this TagStructReader reader)
    {
        return new SetClientNameReply
        {
            ClientIndex = reader.ReadU32(),
        };
    }

    /// <summary>
    /// Writes SetClientNameReply to a tagstruct.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="reply">The set client name reply.</param>
    public static void WriteSetClientNameReply(this TagStructWriter writer, SetClientNameReply reply)
    {
        writer.WriteU32(reply.ClientIndex);
    }
}
