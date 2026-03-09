namespace Fiberish.VFS;

/// <summary>
/// Centralized namespace link-count transitions.
/// Filesystems should route link/unlink/rmdir/rename-overwrite through this helper
/// so link semantics stay consistent across implementations.
/// </summary>
public static class NamespaceOps
{
    public static void OnLinkAdded(Inode inode, string reason)
    {
        inode.IncLink(reason);
    }

    public static void OnEntryRemoved(Inode? inode, string reason)
    {
        inode?.DecLink(reason);
    }

    public static void OnRenameOverwrite(Inode? sourceInode, Inode? replacedInode, string reason)
    {
        if (replacedInode == null) return;
        if (sourceInode != null && ReferenceEquals(sourceInode, replacedInode)) return;
        replacedInode.DecLink(reason);
    }
}

