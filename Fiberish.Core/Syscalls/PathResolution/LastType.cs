namespace Fiberish.Syscalls;

/// <summary>
///     Type of the last path component during path resolution.
///     Equivalent to Linux kernel's last_type enum.
/// </summary>
public enum LastType
{
    /// <summary>
    ///     Normal filename component.
    /// </summary>
    Normal,

    /// <summary>
    ///     Current directory (.).
    /// </summary>
    Dot,

    /// <summary>
    ///     Parent directory (..).
    /// </summary>
    DotDot,

    /// <summary>
    ///     Root directory (/).
    /// </summary>
    Root,

    /// <summary>
    ///     Bind mount boundary.
    /// </summary>
    Bind,

    /// <summary>
    ///     No component (empty path or trailing slash).
    /// </summary>
    None
}