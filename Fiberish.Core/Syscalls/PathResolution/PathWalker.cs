using Fiberish.Auth.Permission;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.VFS;

namespace Fiberish.Syscalls;

/// <summary>
///     Path resolution implementation using the NameData pattern.
///     Equivalent to Linux kernel's path_lookup() family of functions.
/// </summary>
public class PathWalker
{
    private static readonly byte[] DotPathBytes = "."u8.ToArray();
    private static readonly byte SlashByte = (byte)'/';
    private readonly SyscallManager _sm;

    /// <summary>
    ///     Creates a new PathWalker instance.
    /// </summary>
    /// <param name="syscallManager">The syscall manager providing VFS context.</param>
    public PathWalker(SyscallManager syscallManager)
    {
        _sm = syscallManager;
    }

    /// <summary>
    ///     Main path resolution entry point. Equivalent to Linux path_lookup().
    /// </summary>
    /// <param name="path">Path to resolve.</param>
    /// <param name="flags">Lookup flags.</param>
    /// <returns>Resolved path location.</returns>
    public PathLocation PathWalk(string path, LookupFlags flags = LookupFlags.FollowSymlink)
    {
        var bytes = FsEncoding.EncodeUtf8(path);
        return PathWalk(bytes, bytes.Length, _sm.CurrentWorkingDirectory, flags);
    }

    /// <summary>
    ///     Path resolution with explicit starting point.
    /// </summary>
    /// <param name="path">Path to resolve.</param>
    /// <param name="startAt">Starting location.</param>
    /// <param name="flags">Lookup flags.</param>
    /// <returns>Resolved path location.</returns>
    public PathLocation PathWalk(string path, PathLocation startAt, LookupFlags flags = LookupFlags.FollowSymlink)
    {
        var bytes = FsEncoding.EncodeUtf8(path);
        return PathWalk(bytes, bytes.Length, startAt, flags);
    }

    /// <summary>
    ///     Resolves a raw byte path relative to the current working directory.
    /// </summary>
    public PathLocation PathWalk(byte[] pathBytes, int pathLength, LookupFlags flags = LookupFlags.FollowSymlink)
    {
        return PathWalk(pathBytes, pathLength, _sm.CurrentWorkingDirectory, flags);
    }

    /// <summary>
    ///     Resolves a raw byte path relative to an explicit starting location.
    /// </summary>
    public PathLocation PathWalk(byte[] pathBytes, int pathLength, PathLocation startAt,
        LookupFlags flags = LookupFlags.FollowSymlink)
    {
        var nd = new NameData(startAt, flags);
        if (!PathInit(nd, pathBytes, pathLength))
            return PathLocation.None;
        if (!LinkPathWalk(nd))
            return PathLocation.None;
        return nd.Path;
    }

    /// <summary>
    ///     Path resolution with optional starting point.
    /// </summary>
    /// <param name="path">Path to resolve.</param>
    /// <param name="startAt">Starting location, or null for cwd.</param>
    /// <param name="followLink">Whether to follow symlinks on the final component.</param>
    /// <returns>Resolved path location.</returns>
    public PathLocation PathWalk(string path, PathLocation? startAt = null, bool followLink = true)
    {
        var flags = followLink ? LookupFlags.FollowSymlink : LookupFlags.None;
        var start = startAt ?? _sm.CurrentWorkingDirectory;
        var bytes = FsEncoding.EncodeUtf8(path);
        return PathWalk(bytes, bytes.Length, start, flags);
    }

    /// <summary>
    ///     Full path resolution with NameData for advanced use cases.
    ///     Returns the NameData with full resolution state including the last component.
    /// </summary>
    public NameData PathWalkWithData(string path, LookupFlags flags = LookupFlags.FollowSymlink)
    {
        var bytes = FsEncoding.EncodeUtf8(path);
        return PathWalkWithData(bytes, bytes.Length, null, flags);
    }

    /// <summary>
    ///     Full path resolution with NameData and explicit starting point.
    /// </summary>
    public NameData PathWalkWithData(string path, PathLocation? startAt, LookupFlags flags = LookupFlags.FollowSymlink)
    {
        var bytes = FsEncoding.EncodeUtf8(path);
        return PathWalkWithData(bytes, bytes.Length, startAt, flags);
    }

    /// <summary>
    ///     Full path resolution using a raw byte path buffer.
    /// </summary>
    public NameData PathWalkWithData(byte[] pathBytes, int pathLength, PathLocation? startAt,
        LookupFlags flags = LookupFlags.FollowSymlink)
    {
        var nd = new NameData(startAt ?? _sm.CurrentWorkingDirectory, flags);
        if (!PathInit(nd, pathBytes, pathLength))
            return nd;
        LinkPathWalk(nd);
        return nd;
    }

    /// <summary>
    ///     Lookup for create operations using a raw byte path.
    /// </summary>
    public (PathLocation parent, FsName name, int error) PathWalkForCreate(byte[] pathBytes, int pathLength)
    {
        return PathWalkForCreate(pathBytes, pathLength, null);
    }

    /// <summary>
    ///     Lookup for create operations using a raw byte path and explicit starting point.
    /// </summary>
    public (PathLocation parent, FsName name, int error) PathWalkForCreate(byte[] pathBytes, int pathLength,
        PathLocation? startAt)
    {
        return PrepareCreate(pathBytes, pathLength, startAt);
    }

    /// <summary>
    ///     Lookup for create operations - returns parent directory and final name.
    /// </summary>
    /// <param name="path">Path to resolve.</param>
    /// <returns>Tuple of (parent location, final name, error code).</returns>
    public (PathLocation parent, FsName name, int error) PathWalkForCreate(string path)
    {
        return PathWalkForCreate(path, null);
    }

    /// <summary>
    ///     Lookup for create operations with explicit starting point.
    /// </summary>
    public (PathLocation parent, FsName name, int error) PathWalkForCreate(string path, PathLocation? startAt)
    {
        var bytes = FsEncoding.EncodeUtf8(path);
        return PrepareCreate(bytes, bytes.Length, startAt);
    }

    /// <summary>
    ///     Initialize path resolution. Equivalent to Linux path_init().
    ///     Sets up the starting point based on path type (absolute or relative).
    /// </summary>
    public bool PathInit(NameData nd, byte[] pathBytes, int pathLength)
    {
        if (pathBytes == null || pathLength <= 0)
            return nd.SetError(-(int)Errno.ENOENT);

        var validationError = ValidatePath(pathBytes, pathLength);
        if (validationError != 0)
            return nd.SetError(validationError);

        nd.PathBytes = pathBytes;
        nd.PathLength = pathLength;
        nd.PathPosition = 0;
        nd.LastName = null;
        nd.LastType = LastType.None;

        if (pathBytes[0] == SlashByte)
        {
            nd.Path = _sm.ProcessRoot;
            nd.PathPosition = 1;
        }
        else if (!nd.IsValid)
        {
            return nd.SetError(-(int)Errno.ENOENT);
        }

        return nd.IsValid;
    }

    /// <summary>
    ///     Walk through path components. Equivalent to Linux link_path_walk().
    /// </summary>
    public bool LinkPathWalk(NameData nd)
    {
        while (nd.PathPosition < nd.PathLength)
        {
            while (nd.PathPosition < nd.PathLength && nd.PathBytes[nd.PathPosition] == SlashByte)
                nd.PathPosition++;

            if (nd.PathPosition >= nd.PathLength)
                break;

            var start = nd.PathPosition;
            while (nd.PathPosition < nd.PathLength && nd.PathBytes[nd.PathPosition] != SlashByte)
                nd.PathPosition++;

            var component = nd.PathBytes.AsSpan(start, nd.PathPosition - start);
            var isLast = true;
            for (var i = nd.PathPosition; i < nd.PathLength; i++)
                if (nd.PathBytes[i] != SlashByte)
                {
                    isLast = false;
                    break;
                }

            if (component.Length == 1 && component[0] == (byte)'.')
            {
                nd.LastType = LastType.Dot;
                continue;
            }

            if (component.Length == 2 && component[0] == (byte)'.' && component[1] == (byte)'.')
            {
                if (!HandleDotDot(nd))
                    return false;
            }
            else
            {
                if (!WalkComponent(nd, component, isLast))
                    return false;
            }
        }

        if ((nd.PathLength > 0 && nd.PathBytes[nd.PathLength - 1] == SlashByte) ||
            (nd.Flags & LookupFlags.Directory) != 0)
            if (nd.Dentry?.Inode?.Type != InodeType.Directory)
                return nd.SetError(-(int)Errno.ENOTDIR);

        return true;
    }

    /// <summary>
    ///     Walk a single path component. Equivalent to Linux walk_component().
    /// </summary>
    public bool WalkComponent(NameData nd, ReadOnlySpan<byte> name, bool isLast)
    {
        var current = nd.Path.Dentry;
        var mount = nd.Path.Mount;
        var currentTask = _sm.CurrentTask;

        if (current == null || mount == null)
            return nd.SetError(-(int)Errno.ENOENT);
        if (current.Inode == null || current.Inode.Type != InodeType.Directory)
            return nd.SetError(-(int)Errno.ENOTDIR);

        if (currentTask != null)
        {
            if (current.Inode is HostInode hostInode)
                hostInode.RefreshProjectedMetadata(currentTask.Process.EUID, currentTask.Process.EGID);

            var searchRc = DacPolicy.CheckPathAccess(currentTask.Process, current.Inode, AccessMode.MayExec, true);
            if (searchRc != 0)
                return nd.SetError(searchRc);
        }

        if (isLast && (nd.Flags & LookupFlags.Parent) != 0)
        {
            nd.LastName = FsName.FromBytes(name);
            nd.LastType = LastType.Normal;
            return true;
        }

        Dentry? next = null;
        if (current.TryGetCachedChild(name, out var cached))
        {
            var valid = currentTask != null && current.Inode is IContextualDirectoryInode contextualDirectoryInode
                ? contextualDirectoryInode.RevalidateCachedChild(currentTask, current, name, cached)
                : current.Inode.RevalidateCachedChild(current, name, cached);
            if (!valid)
            {
                _ = current.TryUncacheChild(name, "PathWalker.WalkComponent.revalidate-fail", out _);
            }
            else
            {
                next = cached;
            }
        }

        if (next == null)
        {
            next = currentTask != null && current.Inode is IContextualDirectoryInode contextualDirectoryLookup
                ? contextualDirectoryLookup.Lookup(currentTask, name)
                : current.Inode.Lookup(name);
            if (next == null)
            {
                var lookupError = current.Inode.ConsumeLookupFailureError(FsEncoding.DecodeUtf8Lossy(name));
                if (isLast && (nd.Flags & LookupFlags.Create) != 0 && lookupError == -(int)Errno.ENOENT)
                {
                    nd.LastName = FsName.FromBytes(name);
                    nd.LastType = LastType.Normal;
                    return true;
                }

                return nd.SetError(lookupError);
            }

            current.CacheChild(next, "PathWalker.WalkComponent.lookup-cache");
        }

        if (next.IsMounted)
        {
            if ((nd.Flags & LookupFlags.NoXdev) != 0)
                return nd.SetError(-(int)Errno.EXDEV);

            var childMount = _sm.FindMount(mount, next);
            if (childMount != null)
            {
                mount = childMount;
                next = childMount.Root;
            }
        }

        if (next.Inode?.Type == InodeType.Symlink)
        {
            var shouldFollow = (nd.Flags & LookupFlags.FollowSymlink) != 0 &&
                               (nd.Flags & LookupFlags.NoSymlinks) == 0;
            var mustFollow = !isLast;

            if ((nd.Flags & LookupFlags.NoFollow) != 0 && isLast)
                shouldFollow = false;

            if (next.Inode is IMagicSymlinkInode magicLink)
            {
                if ((nd.Flags & LookupFlags.NoMagiclinks) != 0)
                {
                    if (mustFollow)
                        return nd.SetError(-(int)Errno.ELOOP);
                    if (isLast)
                    {
                        nd.LastName = FsName.FromBytes(name);
                        nd.LastType = LastType.Normal;
                        nd.Path = new PathLocation(next, mount);
                    }

                    return true;
                }

                if ((shouldFollow || mustFollow) && currentTask != null &&
                    next.Inode is IContextualMagicSymlinkInode contextualMagicLink &&
                    contextualMagicLink.TryResolveLink(currentTask, out var contextualResolvedPath))
                {
                    nd.Path = contextualResolvedPath;
                    return true;
                }

                if ((shouldFollow || mustFollow) && magicLink.TryResolveLink(out var resolvedPath))
                {
                    nd.Path = resolvedPath;
                    return true;
                }
            }

            if (shouldFollow || mustFollow)
            {
                if (!FollowSymlink(nd, next, mount, currentTask, mustFollow))
                    return false;
                return true;
            }

            if (isLast)
            {
                nd.LastName = FsName.FromBytes(name);
                nd.LastType = LastType.Normal;
            }
        }

        if (isLast)
        {
            nd.LastName = FsName.FromBytes(name);
            nd.LastType = LastType.Normal;
        }

        nd.Path = new PathLocation(next, mount);
        return true;
    }

    /// <summary>
    ///     Handle <c>..</c> traversal. Equivalent to Linux handle_dots().
    /// </summary>
    public bool HandleDotDot(NameData nd)
    {
        var current = nd.Path.Dentry;
        var mount = nd.Path.Mount;

        if (current == null || mount == null)
            return nd.SetError(-(int)Errno.ENOENT);

        if (current == _sm.ProcessRoot.Dentry && mount == _sm.ProcessRoot.Mount)
        {
            nd.LastType = LastType.DotDot;
            return true;
        }

        while (current == mount.Root && mount.Parent != null)
        {
            if ((nd.Flags & LookupFlags.NoXdev) != 0)
                return nd.SetError(-(int)Errno.EXDEV);

            current = mount.MountPoint;
            mount = mount.Parent;
        }

        if (current?.Parent != null)
            nd.Path = new PathLocation(current.Parent, mount);

        nd.LastType = LastType.DotDot;
        return true;
    }

    /// <summary>
    ///     Follow a symbolic link. Equivalent to Linux follow_symlink().
    /// </summary>
    public bool FollowSymlink(NameData nd, Dentry symlink, Mount? currentMount, FiberTask? task = null,
        bool forceFollowFinal = false)
    {
        if (nd.Depth >= NameData.MaxSymlinkDepth)
            return nd.SetError(-(int)Errno.ELOOP);

        byte[]? target = null;
        if (task != null && symlink.Inode is IContextualSymlinkInode contextualSymlink)
        {
            target = contextualSymlink.Readlink(task);
        }
        else
        {
            var readlinkRc = symlink.Inode?.Readlink(out target) ?? -(int)Errno.ENOENT;
            if (readlinkRc < 0)
                return nd.SetError(readlinkRc);
        }

        if (target == null || target.Length == 0)
            return nd.SetError(-(int)Errno.ENOENT);

        var savedBytes = nd.PathBytes;
        var savedLength = nd.PathLength;
        var savedPos = nd.PathPosition;
        var savedLocation = nd.Path;

        nd.SymlinkStack.Push(new SymlinkStackEntry
        {
            LinkPath = nd.Path,
            TargetPath = target,
            Position = 0,
            SymlinkDentry = symlink
        });

        nd.Depth++;
        nd.TotalLinkCount++;

        PathLocation startLocation;
        if (target[0] == SlashByte)
        {
            startLocation = _sm.ProcessRoot;
        }
        else
        {
            var parentDentry = symlink.Parent ?? nd.Dentry!;
            startLocation = new PathLocation(parentDentry, currentMount ?? nd.Mount!);
        }

        nd.PathBytes = target;
        nd.PathLength = target.Length;
        nd.PathPosition = target[0] == SlashByte ? 1 : 0;
        nd.Path = startLocation;
        var savedFlags = nd.Flags;
        if (forceFollowFinal)
            nd.Flags = (nd.Flags | LookupFlags.FollowSymlink) & ~LookupFlags.NoFollow;

        var result = LinkPathWalk(nd);

        nd.PathBytes = savedBytes;
        nd.PathLength = savedLength;
        nd.PathPosition = savedPos;
        nd.Flags = savedFlags;
        nd.Depth--;
        nd.SymlinkStack.Pop();

        if (!result)
        {
            nd.Path = savedLocation;
            return false;
        }

        return true;
    }

    /// <summary>
    ///     Handle the final component for create operations.
    ///     Returns the parent directory and the raw name to create.
    /// </summary>
    public (PathLocation parent, FsName name, int error) PrepareCreate(byte[] pathBytes, int pathLength,
        PathLocation? startAt = null)
    {
        if (pathBytes == null || pathLength <= 0)
            return (PathLocation.None, FsName.Empty, -(int)Errno.ENOENT);

        var validationError = ValidatePath(pathBytes, pathLength);
        if (validationError != 0)
            return (PathLocation.None, FsName.Empty, validationError);

        while (pathLength > 1 && pathBytes[pathLength - 1] == SlashByte)
            pathLength--;

        var lastSlash = -1;
        for (var i = pathLength - 1; i >= 0; i--)
            if (pathBytes[i] == SlashByte)
            {
                lastSlash = i;
                break;
            }

        var nameSpan = lastSlash == -1
            ? pathBytes.AsSpan(0, pathLength)
            : pathBytes.AsSpan(lastSlash + 1, pathLength - lastSlash - 1);
        if (nameSpan.IsEmpty)
            return (PathLocation.None, FsName.Empty, -(int)Errno.ENOENT);
        if (nameSpan.Length > LinuxConstants.NameMax)
            return (PathLocation.None, FsName.Empty, -(int)Errno.ENAMETOOLONG);

        var parentBytes = lastSlash switch
        {
            -1 => (DotPathBytes, DotPathBytes.Length),
            0 => (pathBytes, 1),
            _ => (pathBytes, lastSlash)
        };

        var parentLookup = PathWalkWithData(parentBytes.Item1, parentBytes.Item2, startAt,
            LookupFlags.FollowSymlink | LookupFlags.Directory);

        if (parentLookup.HasError)
            return (PathLocation.None, FsName.FromBytes(nameSpan), parentLookup.ErrorCode);

        var parentLoc = parentLookup.Path;
        if (!parentLoc.IsValid)
            return (PathLocation.None, FsName.FromBytes(nameSpan), -(int)Errno.ENOENT);
        if (parentLoc.Dentry?.Inode?.Type != InodeType.Directory)
            return (PathLocation.None, FsName.FromBytes(nameSpan), -(int)Errno.ENOTDIR);

        var currentTask = _sm.CurrentTask;
        if (currentTask != null)
        {
            if (parentLoc.Dentry.Inode is HostInode hostInode)
                hostInode.RefreshProjectedMetadata(currentTask.Process.EUID, currentTask.Process.EGID);

            var accessRc = DacPolicy.CheckPathAccess(
                currentTask.Process,
                parentLoc.Dentry.Inode,
                AccessMode.MayWrite | AccessMode.MayExec,
                true);
            if (accessRc != 0)
                return (PathLocation.None, FsName.FromBytes(nameSpan), accessRc);
        }

        return (parentLoc, FsName.FromBytes(nameSpan), 0);
    }

    /// <summary>
    ///     Validate a raw path buffer for pathname and component length limits.
    /// </summary>
    private static int ValidatePath(byte[] pathBytes, int pathLength)
    {
        if (pathLength >= LinuxConstants.PathMax)
            return -(int)Errno.ENAMETOOLONG;

        var componentLength = 0;
        for (var i = 0; i < pathLength; i++)
        {
            var current = pathBytes[i];
            if (current == 0)
                return -(int)Errno.ENOENT;
            if (current == SlashByte)
            {
                if (componentLength > LinuxConstants.NameMax)
                    return -(int)Errno.ENAMETOOLONG;
                componentLength = 0;
                continue;
            }

            componentLength++;
            if (componentLength > LinuxConstants.NameMax)
                return -(int)Errno.ENAMETOOLONG;
        }

        return 0;
    }
}
