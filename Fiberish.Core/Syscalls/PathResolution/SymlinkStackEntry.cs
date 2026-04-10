using Fiberish.VFS;

namespace Fiberish.Syscalls;

/// <summary>
///     Entry in the symlink resolution stack.
///     Tracks state when following symbolic links for proper backtracking.
/// </summary>
public class SymlinkStackEntry
{
    /// <summary>
    ///     The path location where the symlink was found.
    /// </summary>
    public PathLocation LinkPath { get; set; }

    /// <summary>
    ///     The raw target path the symlink points to.
    /// </summary>
    public byte[] TargetPath { get; set; } = Array.Empty<byte>();

    /// <summary>
    ///     Current position in the target path buffer.
    /// </summary>
    public int Position { get; set; }

    /// <summary>
    ///     The symlink dentry that was being followed.
    /// </summary>
    public Dentry? SymlinkDentry { get; set; }
}
