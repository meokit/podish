namespace Fiberish.Core.VFS.TTY;

public enum TtyEndpointKind
{
    Stdin,
    Stdout,
    Stderr
}

public interface ITtyDriver
{
    /// <summary>
    ///     Writes data to the TTY output.
    /// </summary>
    /// <param name="kind">The endpoint to write to (Stdout/Stderr).</param>
    /// <param name="buffer">The data to write.</param>
    /// <returns>The number of bytes written, or a negative error code.</returns>
    int Write(TtyEndpointKind kind, ReadOnlySpan<byte> buffer);

    /// <summary>
    ///     Flushes any buffered output.
    /// </summary>
    void Flush();
}