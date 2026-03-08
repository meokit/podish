using Fiberish.Syscalls;

namespace Fiberish.VFS;

/// <summary>
/// Filesystems can implement this to provide best-effort dentry/inode cache reclaim.
/// </summary>
public interface IDentryInodeCacheDropper
{
    long DropDentryAndInodeCaches();
}

public readonly record struct VfsCacheReclaimStats(long DentriesDropped, long InodesDropped, long SuperblocksScanned);

public static class VfsCacheReclaimer
{
    public static VfsCacheReclaimStats DropDentryAndInodeCaches(SyscallManager? syscallManager)
    {
        if (syscallManager == null) return new VfsCacheReclaimStats(0, 0, 0);

        var superblocks = new HashSet<SuperBlock>();
        foreach (var mount in syscallManager.Mounts)
        {
            if (mount?.SB != null) superblocks.Add(mount.SB);
        }

        long dentriesDropped = 0;
        long inodesDropped = 0;
        foreach (var sb in superblocks)
        {
            if (sb is IDentryInodeCacheDropper dropper)
                dentriesDropped += dropper.DropDentryAndInodeCaches();

            inodesDropped += SweepUnusedInodes(sb);
        }

        return new VfsCacheReclaimStats(dentriesDropped, inodesDropped, superblocks.Count);
    }

    internal static long DetachCachedSubtree(Dentry root)
    {
        if (root.IsMounted) return 0;

        long dropped = 0;
        var visited = new HashSet<Dentry>();
        var stack = new Stack<Dentry>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!visited.Add(current)) continue;
            if (current.IsMounted) continue;

            foreach (var child in current.Children.Values.ToList())
                stack.Push(child);

            current.Children.Clear();
            if (current.Parent != null && !ReferenceEquals(current.Parent, current))
                current.Parent.Children.Remove(current.Name);

            var inode = current.Inode;
            if (inode != null && inode.Dentries.Remove(current))
                inode.Put();

            dropped++;
        }

        return dropped;
    }

    internal static long SweepUnusedInodes(SuperBlock sb)
    {
        lock (sb.Lock)
        {
            if (sb.Inodes.Count == 0) return 0;

            var rootInode = sb.Root?.Inode;
            var victims = sb.Inodes
                .Where(inode => !ReferenceEquals(inode, rootInode) && inode.RefCount <= 0 && inode.Dentries.Count == 0)
                .ToList();
            if (victims.Count == 0) return 0;

            foreach (var inode in victims)
                sb.RemoveInodeFromTracking(inode);

            return victims.Count;
        }
    }
}
