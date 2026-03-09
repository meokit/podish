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
        // For inodes that have not switched to explicit link tracking yet
        // (e.g. HostFS projected inodes), link() call sites invoke this after
        // the alias dentry is already bound. In that case, the current alias
        // count already reflects the new namespace entry, so initialize from
        // aliases directly instead of adding one more.
        if (!inode.HasExplicitLinkCount)
        {
            inode.SetInitialLinkCount(Math.Max(1, inode.Dentries.Count), reason);
            return;
        }

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
