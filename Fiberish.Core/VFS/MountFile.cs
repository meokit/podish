namespace Fiberish.VFS;

/// <summary>
///     A specialized LinuxFile returned by open_tree(OPEN_TREE_CLONE).
///     This file handle represents a detached mount and is used exclusively
///     as a handle for move_mount.
/// </summary>
public class MountFile : LinuxFile
{
    public MountFile(Mount mount, FileFlags flags = FileFlags.O_RDONLY) : base(mount.Root, flags, mount)
    {
        // Mount is set via base constructor
        mount.Get(); // Extra reference for the DetachedMount property
    }

    public override void Close()
    {
        Mount?.Put();
        Mount = null!;
        base.Close();
    }
}