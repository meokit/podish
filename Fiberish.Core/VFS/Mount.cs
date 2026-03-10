using Fiberish.Native;
using System.Threading;

namespace Fiberish.VFS;

/// <summary>
///     Represents an instance of a filesystem attached at a specific location
///     or stored as a detached tree (for open_tree/move_mount support).
/// </summary>
public class Mount
{
    private static long _nextId;
    private int _refCount;
    private bool _mountPointPinned;

    public Mount(SuperBlock sb, Dentry root, Dentry? mountPoint = null, Mount? parent = null)
    {
        Id = Interlocked.Increment(ref _nextId);
        SB = sb;
        Root = root;
        MountPoint = mountPoint;
        Parent = parent;
        sb.Get();
        root.Get("Mount.ctor");
        if (mountPoint != null)
        {
            mountPoint.Get("Mount.ctor.mount-pin");
            _mountPointPinned = true;
            mountPoint.IsMounted = true;
        }
    }

    /// <summary>
    ///     Unique identifier for this mount instance.
    /// </summary>
    public long Id { get; }

    /// <summary>
    ///     The mounted filesystem's superblock.
    /// </summary>
    public SuperBlock SB { get; }

    /// <summary>
    ///     The root dentry of this mount (usually SB.Root, but can be a subtree for bind mounts).
    /// </summary>
    public Dentry Root { get; }

    /// <summary>
    ///     The dentry in the parent filesystem where this mount is attached.
    ///     Null if this is a detached mount (created by open_tree with OPEN_TREE_CLONE).
    /// </summary>
    public Dentry? MountPoint { get; private set; }

    /// <summary>
    ///     The parent mount (for path resolution traversal).
    ///     Null for the root mount.
    /// </summary>
    public Mount? Parent { get; private set; }

    /// <summary>
    ///     Mount-specific flags (e.g., MS_RDONLY).
    /// </summary>
    public uint Flags { get; set; }

    /// <summary>
    ///     Source path for this mount (e.g., device name or "none").
    /// </summary>
    public string Source { get; set; } = "none";

    /// <summary>
    ///     Filesystem type name.
    /// </summary>
    public string FsType { get; set; } = "none";

    /// <summary>
    ///     Mount options string.
    /// </summary>
    public string Options { get; set; } = "";

    /// <summary>
    ///     Whether this mount is currently attached to the directory tree.
    /// </summary>
    public bool IsAttached => MountPoint != null;
    public int RefCount => Volatile.Read(ref _refCount);

    /// <summary>
    ///     Whether this mount is read-only.
    /// </summary>
    public bool IsReadOnly => (Flags & LinuxConstants.MS_RDONLY) != 0;

    /// <summary>
    ///     Check if write operation is allowed on this mount.
    ///     Similar to Linux kernel's mnt_want_write().
    /// </summary>
    /// <returns>0 if allowed, -EROFS if read-only</returns>
    public int WantWrite()
    {
        if (IsReadOnly)
            return -(int)Errno.EROFS;
        return 0;
    }

    /// <summary>
    ///     Attach this mount to a dentry in the parent filesystem.
    /// </summary>
    public void Attach(Dentry mountPoint, Mount? parent)
    {
        if (IsAttached)
            throw new InvalidOperationException("Mount is already attached");

        if (mountPoint.Parent != null && !ReferenceEquals(mountPoint.Parent, mountPoint))
            mountPoint.Parent.CacheChild(mountPoint, "Mount.Attach.ensure-hashed");

        mountPoint.Get("Mount.Attach.mount-pin");
        _mountPointPinned = true;
        MountPoint = mountPoint;
        Parent = parent;

        // Update dentry mount info
        mountPoint.IsMounted = true;
    }

    /// <summary>
    ///     Detach this mount from the directory tree.
    /// </summary>
    public void Detach()
    {
        if (!IsAttached)
            return;

        if (MountPoint != null)
        {
            MountPoint.IsMounted = false;
            if (_mountPointPinned)
            {
                MountPoint.Put("Mount.Detach.mount-unpin");
                _mountPointPinned = false;
            }
        }

        MountPoint = null;
        Parent = null;
    }

    /// <summary>
    ///     Increment reference count.
    /// </summary>
    public void Get()
    {
        Interlocked.Increment(ref _refCount);
    }

    /// <summary>
    ///     Decrement reference count and release resources if zero.
    /// </summary>
    public void Put()
    {
        if (Interlocked.Decrement(ref _refCount) > 0)
            return;

        // Release resources
        Detach();
        SB.Put();
        Root.Put("Mount.Put");
    }

    /// <summary>
    ///     Create a clone of this mount for OPEN_TREE_CLONE.
    ///     If subtree is provided, creates a bind mount with that subtree as root.
    /// </summary>
    public Mount Clone(Dentry? subtree = null)
    {
        var root = subtree ?? Root;
        var clone = new Mount(SB, root)
        {
            Flags = Flags,
            Source = Source,
            FsType = FsType,
            Options = Options
        };
        return clone;
    }

    public override string ToString()
    {
        var target = MountPoint != null ? $" at {MountPoint.Name}" : " (detached)";
        return $"Mount[{Id}]: {Source} on {FsType}{target}";
    }
}
