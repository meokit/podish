namespace Fiberish.VFS;

public enum NamespaceLinkDeltaKind
{
    LinkAdded,
    DirectoryCreated,
    EntryRemoved,
    DirectoryRemoved,
    DirectoryMovedAcrossParents,
    RenameOverwrite
}

/// <summary>
/// Centralized namespace link-count transitions.
/// Filesystems should route link/unlink/rmdir/rename-overwrite through this helper
/// so link semantics stay consistent across implementations.
/// </summary>
public static class NamespaceOps
{
    public static void ApplyLinkDelta(
        NamespaceLinkDeltaKind kind,
        string reason,
        Inode? inode = null,
        Inode? parentDirectory = null,
        Inode? secondaryInode = null)
    {
        switch (kind)
        {
            case NamespaceLinkDeltaKind.LinkAdded:
                inode?.IncLink(reason);
                return;
            case NamespaceLinkDeltaKind.DirectoryCreated:
                if (parentDirectory == null || inode == null)
                {
                    VfsDebugTrace.FailInvariant($"NamespaceOps.DirectoryCreated missing inode(s) reason={reason}");
                    return;
                }

                inode.SetInitialLinkCount(2, $"{reason}.child-init");
                parentDirectory.IncLink($"{reason}.parent-inc");
                return;
            case NamespaceLinkDeltaKind.EntryRemoved:
                inode?.DecLink(reason);
                return;
            case NamespaceLinkDeltaKind.DirectoryRemoved:
                if (parentDirectory == null || inode == null)
                {
                    VfsDebugTrace.FailInvariant($"NamespaceOps.DirectoryRemoved missing inode(s) reason={reason}");
                    return;
                }

                parentDirectory.DecLink($"{reason}.parent-dec");
                inode.DecLink($"{reason}.child-dec-parent");
                inode.DecLink($"{reason}.child-dec-dot");
                return;
            case NamespaceLinkDeltaKind.DirectoryMovedAcrossParents:
                if (parentDirectory == null || secondaryInode == null)
                {
                    VfsDebugTrace.FailInvariant(
                        $"NamespaceOps.DirectoryMovedAcrossParents missing inode(s) reason={reason}");
                    return;
                }

                if (ReferenceEquals(parentDirectory, secondaryInode))
                    return;

                parentDirectory.DecLink($"{reason}.old-parent-dec");
                secondaryInode.IncLink($"{reason}.new-parent-inc");
                return;
            case NamespaceLinkDeltaKind.RenameOverwrite:
                if (secondaryInode == null)
                    return;
                if (inode != null && ReferenceEquals(inode, secondaryInode))
                    return;
                secondaryInode.DecLink(reason);
                return;
            default:
                VfsDebugTrace.FailInvariant($"NamespaceOps.ApplyLinkDelta unknown kind={kind} reason={reason}");
                return;
        }
    }

    public static void OnLinkAdded(Inode inode, string reason)
    {
        ApplyLinkDelta(NamespaceLinkDeltaKind.LinkAdded, reason, inode: inode);
    }

    public static void OnDirectoryCreated(Inode parentDirectory, Inode childDirectory, string reason)
    {
        ApplyLinkDelta(
            NamespaceLinkDeltaKind.DirectoryCreated,
            reason,
            inode: childDirectory,
            parentDirectory: parentDirectory);
    }

    public static void OnEntryRemoved(Inode? inode, string reason)
    {
        ApplyLinkDelta(NamespaceLinkDeltaKind.EntryRemoved, reason, inode: inode);
    }

    public static void OnDirectoryRemoved(Inode parentDirectory, Inode removedDirectory, string reason)
    {
        ApplyLinkDelta(
            NamespaceLinkDeltaKind.DirectoryRemoved,
            reason,
            inode: removedDirectory,
            parentDirectory: parentDirectory);
    }

    public static void OnDirectoryMovedAcrossParents(Inode oldParentDirectory, Inode newParentDirectory, string reason)
    {
        ApplyLinkDelta(
            NamespaceLinkDeltaKind.DirectoryMovedAcrossParents,
            reason,
            parentDirectory: oldParentDirectory,
            secondaryInode: newParentDirectory);
    }

    public static void OnRenameOverwrite(Inode? sourceInode, Inode? replacedInode, string reason)
    {
        ApplyLinkDelta(
            NamespaceLinkDeltaKind.RenameOverwrite,
            reason,
            inode: sourceInode,
            secondaryInode: replacedInode);
    }
}
