using Fiberish.Syscalls;
using Fiberish.Memory;

namespace Fiberish.VFS;

/// <summary>
/// Filesystems can implement this to provide best-effort dentry cache reclaim.
/// </summary>
public interface IDentryCacheDropper
{
    long DropDentryCache();
}

[Flags]
public enum VfsShrinkMode
{
    None = 0,
    PageCache = 1 << 0,
    DentryCache = 1 << 1,
    InodeCache = 1 << 2
}

public readonly record struct VfsShrinkStats(
    long PageCacheBytesReclaimed,
    long DentriesDropped,
    long InodesEvicted,
    long SuperblocksScanned);

public static class VfsShrinker
{
    public static VfsShrinkStats Shrink(SyscallManager? syscallManager, VfsShrinkMode mode)
    {
        if (syscallManager == null || mode == VfsShrinkMode.None)
            return new VfsShrinkStats(0, 0, 0, 0);

        var superblocks = new HashSet<SuperBlock>();
        foreach (var mount in syscallManager.Mounts)
        {
            if (mount?.SB != null) superblocks.Add(mount.SB);
        }

        long pageCacheBytesReclaimed = 0;
        if ((mode & VfsShrinkMode.PageCache) != 0)
            pageCacheBytesReclaimed = GlobalPageCacheManager.TryReclaimBytes(long.MaxValue);

        long dentriesDropped = 0;
        if ((mode & VfsShrinkMode.DentryCache) != 0)
        {
            foreach (var sb in superblocks)
            {
                if (sb is IDentryCacheDropper dropper)
                    dentriesDropped += dropper.DropDentryCache();
            }
        }

        long inodesEvicted = 0;
        if ((mode & VfsShrinkMode.InodeCache) != 0)
        {
            foreach (var sb in superblocks)
                inodesEvicted += EvictUnusedInodes(sb);
        }

        return new VfsShrinkStats(pageCacheBytesReclaimed, dentriesDropped, inodesEvicted, superblocks.Count);
    }

    public static long DetachCachedSubtree(Dentry root)
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

            if (current.DentryRefCount > 0) continue;

            current.ClearCachedChildren("VfsShrinker.DetachCachedSubtree");
            if (current.Parent != null && !ReferenceEquals(current.Parent, current))
                current.Parent.TryUncacheChild(current.Name, "VfsShrinker.DetachCachedSubtree", out _);

            var inode = current.Inode;
            if (inode != null)
            {
                current.UnbindInode("VfsShrinker.DetachCachedSubtree");
            }

            dropped++;
        }

        return dropped;
    }

    internal static long EvictUnusedInodes(SuperBlock sb)
    {
        List<Inode> candidates;
        lock (sb.Lock)
        {
            if (sb.Inodes.Count == 0) return 0;

            var rootInode = sb.Root?.Inode;
            candidates = sb.Inodes
                .Where(inode => !ReferenceEquals(inode, rootInode) && inode.RefCount == 0 && !inode.IsCacheEvicted)
                .ToList();
        }

        if (candidates.Count == 0) return 0;

        long evicted = 0;
        foreach (var inode in candidates)
        {
            if (inode.TryEvictCache("VfsShrinker.EvictUnusedInodes"))
                evicted++;
        }

        return evicted;
    }
}
