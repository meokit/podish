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
    private readonly SyscallManager _sm;

    /// <summary>
    ///     Creates a new PathWalker instance.
    /// </summary>
    /// <param name="syscallManager">The syscall manager providing VFS context</param>
    public PathWalker(SyscallManager syscallManager)
    {
        _sm = syscallManager;
    }

    /// <summary>
    ///     Main path resolution entry point. Equivalent to Linux path_lookup().
    /// </summary>
    /// <param name="path">Path to resolve</param>
    /// <param name="flags">Lookup flags</param>
    /// <returns>Resolved path location</returns>
    public PathLocation PathWalk(string path, LookupFlags flags = LookupFlags.FollowSymlink)
    {
        return PathWalk(path, _sm.CurrentWorkingDirectory, flags);
    }

    /// <summary>
    ///     Path resolution with explicit starting point.
    /// </summary>
    /// <param name="path">Path to resolve</param>
    /// <param name="startAt">Starting location (default for cwd)</param>
    /// <param name="flags">Lookup flags</param>
    /// <returns>Resolved path location</returns>
    public PathLocation PathWalk(string path, PathLocation startAt, LookupFlags flags = LookupFlags.FollowSymlink)
    {
        var nd = new NameData(startAt, flags);

        if (!PathInit(nd, path))
            return PathLocation.None;

        if (!LinkPathWalk(nd))
            return PathLocation.None;

        return nd.Path;
    }

    /// <summary>
    ///     Path resolution with optional starting point.
    /// </summary>
    /// <param name="path">Path to resolve</param>
    /// <param name="startAt">Starting location (null for cwd)</param>
    /// <param name="followLink">Whether to follow symlinks</param>
    /// <returns>Resolved path location</returns>
    public PathLocation PathWalk(string path, PathLocation? startAt = null, bool followLink = true)
    {
        var flags = followLink ? LookupFlags.FollowSymlink : LookupFlags.None;
        var start = startAt ?? _sm.CurrentWorkingDirectory;
        return PathWalk(path, start, flags);
    }

    /// <summary>
    ///     Full path resolution with NameData for advanced use cases.
    ///     Returns the NameData with full resolution state including last component.
    /// </summary>
    public NameData PathWalkWithData(string path, LookupFlags flags = LookupFlags.FollowSymlink)
    {
        return PathWalkWithData(path, null, flags);
    }

    /// <summary>
    ///     Full path resolution with NameData and explicit starting point.
    /// </summary>
    public NameData PathWalkWithData(string path, PathLocation? startAt, LookupFlags flags = LookupFlags.FollowSymlink)
    {
        var nd = new NameData(
            startAt ?? _sm.CurrentWorkingDirectory,
            flags);

        if (!PathInit(nd, path))
            return nd;

        LinkPathWalk(nd);
        return nd;
    }

    /// <summary>
    ///     Lookup for create operations - returns parent directory and final name.
    /// </summary>
    /// <param name="path">Path to resolve</param>
    /// <returns>Tuple of (parent location, final name, error code)</returns>
    public (PathLocation parent, string name, int error) PathWalkForCreate(string path)
    {
        return PathWalkForCreate(path, null);
    }

    /// <summary>
    ///     Lookup for create operations with explicit starting point.
    /// </summary>
    public (PathLocation parent, string name, int error) PathWalkForCreate(string path, PathLocation? startAt)
    {
        return PrepareCreate(path, startAt);
    }

    /// <summary>
    ///     Initialize path resolution. Equivalent to Linux path_init().
    ///     Sets up the starting point based on path type (absolute/relative).
    /// </summary>
    public bool PathInit(NameData nd, string path)
    {
        if (string.IsNullOrEmpty(path))
            return nd.SetError(-(int)Errno.ENOENT);

        nd.PathString = path;
        nd.PathPosition = 0;
        nd.LastName = null;
        nd.LastType = LastType.None;

        // Determine starting point based on path type
        if (path[0] == '/')
        {
            nd.Path = _sm.ProcessRoot;
            nd.PathPosition = 1; // Skip leading /
        }
        else
        {
            // Already set in constructor, but ensure it's valid
            if (!nd.IsValid)
                return nd.SetError(-(int)Errno.ENOENT);
        }

        return nd.IsValid;
    }

    /// <summary>
    ///     Walk through path components. Equivalent to Linux link_path_walk().
    /// </summary>
    public bool LinkPathWalk(NameData nd)
    {
        while (nd.PathPosition < nd.PathString.Length)
        {
            // Skip leading/consecutive slashes
            while (nd.PathPosition < nd.PathString.Length &&
                   nd.PathString[nd.PathPosition] == '/')
                nd.PathPosition++;

            if (nd.PathPosition >= nd.PathString.Length)
                break;

            // Extract next component
            var start = nd.PathPosition;
            while (nd.PathPosition < nd.PathString.Length &&
                   nd.PathString[nd.PathPosition] != '/')
                nd.PathPosition++;

            var component = nd.PathString[start..nd.PathPosition];

            // A component is the last one if everything after it is just slashes
            var isLast = true;
            for (var i = nd.PathPosition; i < nd.PathString.Length; i++)
                if (nd.PathString[i] != '/')
                {
                    isLast = false;
                    break;
                }

            // Handle the component
            if (component == "." || component == "")
            {
                nd.LastType = LastType.Dot;
                continue;
            }

            if (component == "..")
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

        // Handle trailing slash or LOOKUP_DIRECTORY flag
        if ((nd.PathString.Length > 0 && nd.PathString[^1] == '/') || (nd.Flags & LookupFlags.Directory) != 0)
            if (nd.Dentry?.Inode?.Type != InodeType.Directory)
                return nd.SetError(-(int)Errno.ENOTDIR);

        return true;
    }

    /// <summary>
    ///     Walk a single path component. Equivalent to Linux walk_component().
    /// </summary>
    public bool WalkComponent(NameData nd, string name, bool isLast)
    {
        var current = nd.Path.Dentry;
        var mount = nd.Path.Mount;
        var currentTask = _sm.CurrentTask;

        if (current == null || mount == null)
            return nd.SetError(-(int)Errno.ENOENT);

        // Check if current is a directory
        if (current.Inode == null || current.Inode.Type != InodeType.Directory)
            return nd.SetError(-(int)Errno.ENOTDIR);

        // LOOKUP_PARENT: For the last component, just record the name and stop.
        // nd.Path stays as the parent directory. This mirrors Linux LOOKUP_PARENT.
        if (isLast && (nd.Flags & LookupFlags.Parent) != 0)
        {
            nd.LastName = name;
            nd.LastType = LastType.Normal;
            return true;
        }

        // Lookup or get from cache
        Dentry? next = null;
        if (current.TryGetCachedChild(name, out var cached))
        {
            var valid = currentTask != null && current.Inode is IContextualDirectoryInode contextualDirectoryInode
                ? contextualDirectoryInode.RevalidateCachedChild(currentTask, current, name, cached)
                : current.Inode.RevalidateCachedChild(current, name, cached);
            if (!valid)
            {
                _ = current.TryUncacheChild(name, "PathWalker.WalkComponent.revalidate-fail", out _);
                next = null;
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
                var lookupError = current.Inode.ConsumeLookupFailureError(name);
                // For create operations, save the last component and return success
                if (isLast && (nd.Flags & LookupFlags.Create) != 0 && lookupError == -(int)Errno.ENOENT)
                {
                    nd.LastName = name;
                    nd.LastType = LastType.Normal;
                    return true;
                }

                return nd.SetError(lookupError);
            }

            current.CacheChild(next, "PathWalker.WalkComponent.lookup-cache");
        }

        // Handle mount points
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

        // Handle symlinks
        if (next.Inode?.Type == InodeType.Symlink)
        {
            // Check if we should follow the symlink
            var shouldFollow = (nd.Flags & LookupFlags.FollowSymlink) != 0 &&
                               (nd.Flags & LookupFlags.NoSymlinks) == 0;
            var mustFollow = !isLast; // Must follow if not last component

            if ((nd.Flags & LookupFlags.NoFollow) != 0 && isLast)
                // AT_SYMLINK_NOFOLLOW - don't follow final component
                shouldFollow = false;

            // Handle magic links first
            if (next.Inode is IMagicSymlinkInode magicLink)
            {
                // NoMagiclinks: Do not follow magic links at all
                if ((nd.Flags & LookupFlags.NoMagiclinks) != 0)
                {
                    if (mustFollow)
                        return nd.SetError(-(int)Errno.ELOOP);
                    // For final component, just return the symlink dentry itself
                    if (isLast)
                    {
                        nd.LastName = name;
                        nd.LastType = LastType.Normal;
                        nd.Path = new PathLocation(next, mount);
                    }

                    return true;
                }

                // Try to resolve magic link
                if ((shouldFollow || mustFollow) && currentTask != null &&
                    next.Inode is IContextualMagicSymlinkInode contextualMagicLink &&
                    contextualMagicLink.TryResolveLink(currentTask, out var contextualResolvedFile))
                {
                    nd.Path = new PathLocation(contextualResolvedFile.Dentry, contextualResolvedFile.Mount);
                    return true;
                }

                if ((shouldFollow || mustFollow) && magicLink.TryResolveLink(out var resolvedFile))
                {
                    nd.Path = new PathLocation(resolvedFile.Dentry, resolvedFile.Mount);
                    return true;
                }
            }

            if (shouldFollow || mustFollow)
            {
                if (!FollowSymlink(nd, next, mount, currentTask, mustFollow))
                    return false;
                return true;
            }

            // Save as last component for create/etc if it's the last part
            if (isLast)
            {
                nd.LastName = name;
                nd.LastType = LastType.Normal;
            }
        }

        if (isLast)
        {
            nd.LastName = name;
            nd.LastType = LastType.Normal;
        }

        nd.Path = new PathLocation(next, mount);
        return true;
    }

    /// <summary>
    ///     Handle .. traversal. Equivalent to Linux handle_dots().
    /// </summary>
    public bool HandleDotDot(NameData nd)
    {
        var current = nd.Path.Dentry;
        var mount = nd.Path.Mount;

        if (current == null || mount == null)
            return nd.SetError(-(int)Errno.ENOENT);

        // Can't go above process root
        if (current == _sm.ProcessRoot.Dentry && mount == _sm.ProcessRoot.Mount)
        {
            nd.LastType = LastType.DotDot;
            return true;
        }

        // If at mount root, ascend to mount point in parent mount
        while (current == mount.Root && mount.Parent != null)
        {
            // Check for NO_XDEV flag when leaving a mount via ..
            if ((nd.Flags & LookupFlags.NoXdev) != 0)
                return nd.SetError(-(int)Errno.EXDEV);

            current = mount.MountPoint;
            mount = mount.Parent;
        }

        // Go to parent directory
        if (current?.Parent != null) nd.Path = new PathLocation(current.Parent, mount);

        nd.LastType = LastType.DotDot;
        return true;
    }

    /// <summary>
    ///     Follow a symbolic link. Equivalent to Linux follow_symlink().
    /// </summary>
    public bool FollowSymlink(NameData nd, Dentry symlink, Mount? currentMount, FiberTask? task = null,
        bool forceFollowFinal = false)
    {
        // Check recursion limit
        if (nd.Depth >= NameData.MaxSymlinkDepth)
            return nd.SetError(-(int)Errno.ELOOP);

        // Read symlink target
        var target = task != null && symlink.Inode is IContextualSymlinkInode contextualSymlink
            ? contextualSymlink.Readlink(task)
            : symlink.Inode?.Readlink();
        if (string.IsNullOrEmpty(target))
            return nd.SetError(-(int)Errno.ENOENT);

        // Save current state for potential backtracking
        var savedPath = nd.PathString;
        var savedPos = nd.PathPosition;
        var savedLocation = nd.Path;

        // Push to symlink stack
        nd.SymlinkStack.Push(new SymlinkStackEntry
        {
            LinkPath = nd.Path,
            TargetPath = target,
            Position = 0,
            SymlinkDentry = symlink
        });

        nd.Depth++;
        nd.TotalLinkCount++;

        // Determine starting point for target resolution
        PathLocation startLocation;
        if (target[0] == '/')
        {
            // Absolute symlink - start from root
            startLocation = _sm.ProcessRoot;
        }
        else
        {
            // Relative symlink - start from symlink's parent directory
            // We use the 'currentMount' we were in when we found the symlink.
            var parentDentry = symlink.Parent ?? nd.Dentry!;
            startLocation = new PathLocation(parentDentry, currentMount ?? nd.Mount!);
        }

        // Recursively walk the target
        nd.PathString = target;
        nd.PathPosition = target[0] == '/' ? 1 : 0;
        nd.Path = startLocation;
        var savedFlags = nd.Flags;
        if (forceFollowFinal)
            nd.Flags = (nd.Flags | LookupFlags.FollowSymlink) & ~LookupFlags.NoFollow;

        var result = LinkPathWalk(nd);

        // Restore path string state (but keep resolved location)
        nd.PathString = savedPath;
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
    ///     Returns the parent directory and the name to create.
    /// </summary>
    public (PathLocation parent, string name, int error) PrepareCreate(string path, PathLocation? startAt = null)
    {
        if (string.IsNullOrEmpty(path))
            return (PathLocation.None, "", -(int)Errno.ENOENT);

        var normalizedPath = path;
        while (normalizedPath.Length > 1 && normalizedPath.EndsWith("/", StringComparison.Ordinal))
            normalizedPath = normalizedPath[..^1];

        // Extract parent path and name
        var lastSlash = normalizedPath.LastIndexOf('/');
        var parentPath = lastSlash <= 0 ? "" : normalizedPath[..lastSlash];
        var name = lastSlash == -1 ? normalizedPath : normalizedPath[(lastSlash + 1)..];

        if (string.IsNullOrEmpty(name))
            return (PathLocation.None, "", -(int)Errno.ENOENT);

        // Resolve parent directory
        var parentLookup = PathWalkWithData(
            string.IsNullOrEmpty(parentPath) ? "." : parentPath,
            startAt,
            LookupFlags.FollowSymlink | LookupFlags.Directory);

        if (parentLookup.HasError)
            return (PathLocation.None, name, parentLookup.ErrorCode);

        var parentLoc = parentLookup.Path;

        if (!parentLoc.IsValid)
            return (PathLocation.None, name, -(int)Errno.ENOENT);

        if (parentLoc.Dentry?.Inode?.Type != InodeType.Directory)
            return (PathLocation.None, name, -(int)Errno.ENOTDIR);

        return (parentLoc, name, 0);
    }
}
