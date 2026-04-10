using Fiberish.Native;
using Fiberish.VFS;

namespace Fiberish.Syscalls;

/// <summary>
///     Path resolution state container, equivalent to Linux kernel's struct nameidata.
///     Maintains state during path walking including current location,
///     symlink recursion tracking, and lookup intent.
/// </summary>
public class NameData
{
    /// <summary>
    ///     Maximum symlink recursion depth (Linux default is 40).
    /// </summary>
    public const int MaxSymlinkDepth = LinuxConstants.MaxSymlinkDepth;

    /// <summary>
    ///     Creates a new NameData with the specified starting location.
    /// </summary>
    /// <param name="start">Starting path location.</param>
    /// <param name="flags">Lookup flags.</param>
    public NameData(PathLocation start, LookupFlags flags = LookupFlags.FollowSymlink)
    {
        Path = start;
        Flags = flags;
        LastType = LastType.None;
        ErrorCode = 0;
    }

    /// <summary>
    ///     Current path location (dentry + mount).
    /// </summary>
    public PathLocation Path { get; set; }

    /// <summary>
    ///     The last component name encountered during path resolution.
    ///     Used for create operations to expose the raw filename component.
    /// </summary>
    public FsName? LastName { get; set; }

    /// <summary>
    ///     Type of the last path component.
    /// </summary>
    public LastType LastType { get; set; }

    /// <summary>
    ///     Flags controlling lookup behavior.
    /// </summary>
    public LookupFlags Flags { get; set; }

    /// <summary>
    ///     Current symlink recursion depth.
    /// </summary>
    public int Depth { get; set; }

    /// <summary>
    ///     Total number of symlinks traversed.
    /// </summary>
    public int TotalLinkCount { get; set; }

    /// <summary>
    ///     Raw path bytes currently being resolved.
    /// </summary>
    public byte[] PathBytes { get; set; } = Array.Empty<byte>();

    /// <summary>
    ///     Effective length within <see cref="PathBytes"/> to consider during lookup.
    /// </summary>
    public int PathLength { get; set; }

    /// <summary>
    ///     Current byte position in the active path buffer.
    /// </summary>
    public int PathPosition { get; set; }

    /// <summary>
    ///     Stack for tracking symlink resolution state.
    /// </summary>
    public Stack<SymlinkStackEntry> SymlinkStack { get; } = new();

    /// <summary>
    ///     Error code if resolution failed (negative errno).
    /// </summary>
    public int ErrorCode { get; set; }

    /// <summary>
    ///     Whether resolution encountered an error.
    /// </summary>
    public bool HasError => ErrorCode != 0;

    /// <summary>
    ///     Current dentry in path resolution.
    /// </summary>
    public Dentry? Dentry => Path.Dentry;

    /// <summary>
    ///     Current mount in path resolution.
    /// </summary>
    public Mount? Mount => Path.Mount;

    /// <summary>
    ///     Whether the current path location is valid.
    /// </summary>
    public bool IsValid => Path.IsValid;

    /// <summary>
    ///     Whether resolution has reached the end of the active path buffer.
    /// </summary>
    public bool IsAtEnd => PathPosition >= PathLength;

    /// <summary>
    ///     Creates a NameData for create operations.
    /// </summary>
    public static NameData ForCreate(PathLocation start)
    {
        return new NameData(start, LookupFlags.FollowSymlink | LookupFlags.Create | LookupFlags.Parent);
    }

    /// <summary>
    ///     Creates a NameData for directory lookup.
    /// </summary>
    public static NameData ForDirectory(PathLocation start)
    {
        return new NameData(start, LookupFlags.FollowSymlink | LookupFlags.Directory);
    }

    /// <summary>
    ///     Creates a NameData that doesn't follow symlinks on the final component.
    /// </summary>
    public static NameData ForNoFollow(PathLocation start)
    {
        return new NameData(start, LookupFlags.None);
    }

    /// <summary>
    ///     Sets an error and returns false for convenient error handling.
    /// </summary>
    public bool SetError(int errno)
    {
        ErrorCode = errno;
        return false;
    }

    /// <summary>
    ///     Resets the state for a new path resolution.
    /// </summary>
    public void Reset()
    {
        LastName = null;
        LastType = LastType.None;
        Depth = 0;
        TotalLinkCount = 0;
        PathBytes = Array.Empty<byte>();
        PathLength = 0;
        PathPosition = 0;
        ErrorCode = 0;
        SymlinkStack.Clear();
    }
}
