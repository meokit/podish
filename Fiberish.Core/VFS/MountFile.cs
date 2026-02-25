using Fiberish.Core;

namespace Fiberish.VFS;

/// <summary>
/// A specialized LinuxFile returned by open_tree(OPEN_TREE_CLONE).
/// This file handle represents a detached mount and is used exclusively
/// as a handle for move_mount.
/// </summary>
public class MountFile : LinuxFile
{
    public MountFile(Mount mount) : base(mount.Root, FileFlags.O_RDONLY)
    {
        Mount = mount;
        mount.Get();
    }

    /// <summary>
    /// The detached mount object.
    /// </summary>
    public Mount Mount { get; private set; }

    public override void Close()
    {
        Mount?.Put();
        Mount = null!;
        base.Close();
    }
}
