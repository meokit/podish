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
    public const int MaxSymlinkDepth = 40;

    /// <summary>
    ///     Current path location (dentry + mount).
    /// </summary>
    public PathLocation Path { get; set; }

    /// <summary>
    ///     The last component name encountered during path resolution.
    ///     Used for create operations to get the filename.
    /// </summary>
    public string? LastName { get; set; }

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
    ///     The original path string being resolved.
    /// </summary>
    public string PathString { get; set; } = "";

    /// <summary>
    ///     Current position in the path string.
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
    ///     Whether we're at the final component of the path.
    /// </summary>
    public bool IsAtEnd => PathPosition >= PathString.Length;

    /// <summary>
    ///     Creates a new NameData with the specified starting location.
    /// </summary>
    /// <param name="start">Starting path location</param>
    /// <param name="flags">Lookup flags</param>
    public NameData(PathLocation start, LookupFlags flags = LookupFlags.FollowSymlink)
    {
        Path = start;
        Flags = flags;
        LastType = LastType.None;
        ErrorCode = 0;
    }

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
    ///     Creates a NameData that doesn't follow symlinks on final component.
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
        PathString = "";
        PathPosition = 0;
        ErrorCode = 0;
        SymlinkStack.Clear();
    }
}