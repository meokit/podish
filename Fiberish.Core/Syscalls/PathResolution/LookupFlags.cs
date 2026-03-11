namespace Fiberish.Syscalls;

/// <summary>
///     Flags controlling path lookup behavior.
///     Equivalent to Linux kernel's LOOKUP_* flags.
/// </summary>
[Flags]
public enum LookupFlags
{
    /// <summary>
    ///     No special flags.
    /// </summary>
    None = 0,

    /// <summary>
    ///     Follow symbolic links during traversal.
    ///     Equivalent to Linux LOOKUP_FOLLOW.
    /// </summary>
    FollowSymlink = 1 << 0,

    /// <summary>
    ///     The final component must be a directory.
    ///     Equivalent to Linux LOOKUP_DIRECTORY.
    /// </summary>
    Directory = 1 << 1,

    /// <summary>
    ///     Do not follow symbolic links on the final component.
    ///     Equivalent to Linux LOOKUP_NOFOLLOW.
    /// </summary>
    NoFollow = 1 << 2,

    /// <summary>
    ///     Intent is to create a file (lookup parent directory).
    ///     Equivalent to Linux LOOKUP_CREATE.
    /// </summary>
    Create = 1 << 3,

    /// <summary>
    ///     Exclusive create - fail if file exists.
    ///     Equivalent to Linux LOOKUP_EXCL.
    /// </summary>
    Exclusive = 1 << 4,

    /// <summary>
    ///     Looking up the target of a rename operation.
    ///     Equivalent to Linux LOOKUP_RENAME_TARGET.
    /// </summary>
    RenameTarget = 1 << 5,

    /// <summary>
    ///     Intent is to open the file.
    ///     Equivalent to Linux LOOKUP_OPEN.
    /// </summary>
    Open = 1 << 6,

    /// <summary>
    ///     Allow automount on directories.
    ///     Equivalent to Linux LOOKUP_AUTOMOUNT.
    /// </summary>
    Automount = 1 << 7,

    /// <summary>
    ///     Return parent directory and last component name.
    ///     Equivalent to Linux LOOKUP_PARENT.
    /// </summary>
    Parent = 1 << 8,

    /// <summary>
    ///     Jump to root of the mount (for openat2).
    ///     Equivalent to Linux LOOKUP_IN_ROOT.
    /// </summary>
    InRoot = 1 << 9,

    /// <summary>
    ///     Do not cross mount points.
    ///     Equivalent to Linux LOOKUP_NO_XDEV.
    /// </summary>
    NoXdev = 1 << 10,

    /// <summary>
    ///     Do not follow magic links (procfs, etc).
    ///     Equivalent to Linux LOOKUP_NO_MAGICLINKS.
    /// </summary>
    NoMagiclinks = 1 << 11,

    /// <summary>
    ///     Do not follow symlinks at all.
    ///     Equivalent to Linux LOOKUP_NO_SYMLINKS.
    /// </summary>
    NoSymlinks = 1 << 12
}