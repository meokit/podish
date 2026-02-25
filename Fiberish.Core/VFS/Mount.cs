using Fiberish.Core;
using Fiberish.Native;

namespace Fiberish.VFS;

/// <summary>
/// Represents an instance of a filesystem attached at a specific location
/// or stored as a detached tree (for open_tree/move_mount support).
/// </summary>
public class Mount
{
    private static long _nextId;
    private int _refCount = 1;

    public Mount(SuperBlock sb, Dentry root, Dentry? mountPoint = null, Mount? parent = null)
    {
        Id = Interlocked.Increment(ref _nextId);
        SB = sb;
        Root = root;
        MountPoint = mountPoint;
        Parent = parent;
        sb.Get();
        root.Get();
    }

    /// <summary>
    /// Unique identifier for this mount instance.
    /// </summary>
    public long Id { get; }

    /// <summary>
    /// The mounted filesystem's superblock.
    /// </summary>
    public SuperBlock SB { get; }

    /// <summary>
    /// The root dentry of this mount (usually SB.Root, but can be a subtree for bind mounts).
    /// </summary>
    public Dentry Root { get; }

    /// <summary>
    /// The dentry in the parent filesystem where this mount is attached.
    /// Null if this is a detached mount (created by open_tree with OPEN_TREE_CLONE).
    /// </summary>
    public Dentry? MountPoint { get; private set; }

    /// <summary>
    /// The parent mount (for path resolution traversal).
    /// Null for the root mount.
    /// </summary>
    public Mount? Parent { get; private set; }

    /// <summary>
    /// Mount-specific flags (e.g., MS_RDONLY).
    /// </summary>
    public uint Flags { get; set; }

    /// <summary>
    /// Source path for this mount (e.g., device name or "none").
    /// </summary>
    public string Source { get; set; } = "none";

    /// <summary>
    /// Filesystem type name.
    /// </summary>
    public string FsType { get; set; } = "none";

    /// <summary>
    /// Mount options string.
    /// </summary>
    public string Options { get; set; } = "";

    /// <summary>
    /// Whether this mount is currently attached to the directory tree.
    /// </summary>
    public bool IsAttached => MountPoint != null;

    /// <summary>
    /// Whether this mount is read-only.
    /// </summary>
    public bool IsReadOnly => (Flags & LinuxConstants.MS_RDONLY) != 0;

    /// <summary>
    /// Attach this mount to a dentry in the parent filesystem.
    /// </summary>
    public void Attach(Dentry mountPoint, Mount? parent)
    {
        if (IsAttached)
            throw new InvalidOperationException("Mount is already attached");

        MountPoint = mountPoint;
        Parent = parent;

        // Update dentry mount info
        mountPoint.IsMounted = true;
        mountPoint.MountRoot = Root;
        Root.MountedAt = mountPoint;

        // Set the mount reference on the dentry
        mountPoint.Mount = this;
    }

    /// <summary>
    /// Detach this mount from the directory tree.
    /// </summary>
    public void Detach()
    {
        if (!IsAttached)
            return;

        if (MountPoint != null)
        {
            MountPoint.IsMounted = false;
            MountPoint.MountRoot = null;
            MountPoint.Mount = null;
        }

        Root.MountedAt = null;
        MountPoint = null;
        Parent = null;
    }

    /// <summary>
    /// Increment reference count.
    /// </summary>
    public void Get()
    {
        Interlocked.Increment(ref _refCount);
    }

    /// <summary>
    /// Decrement reference count and release resources if zero.
    /// </summary>
    public void Put()
    {
        if (Interlocked.Decrement(ref _refCount) > 0)
            return;

        // Release resources
        Detach();
        SB.Put();
        Root.Put();
    }

    /// <summary>
    /// Create a clone of this mount for OPEN_TREE_CLONE.
    /// </summary>
    public Mount Clone()
    {
        var clone = new Mount(SB, Root, null, null)
        {
            Flags = Flags,
            Source = Source,
            FsType = FsType,
            Options = Options
        };
        SB.Get();
        Root.Get();
        return clone;
    }

    public override string ToString()
    {
        var target = MountPoint != null ? $" at {MountPoint.Name}" : " (detached)";
        return $"Mount[{Id}]: {Source} on {FsType}{target}";
    }
}
