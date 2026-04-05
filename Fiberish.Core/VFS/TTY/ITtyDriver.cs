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
    ///     Indicates whether output can be accepted without blocking.
    /// </summary>
    bool CanWrite { get; }

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

    /// <summary>
    ///     Registers a callback to be invoked when output might become writable.
    ///     Returns true when wait registration is armed; false when no wait is needed.
    /// </summary>
    bool RegisterWriteWait(Action callback, KernelScheduler scheduler);
}