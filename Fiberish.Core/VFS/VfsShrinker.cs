using Fiberish.Memory;
using Fiberish.Syscalls;

namespace Fiberish.VFS;

/// <summary>
///     Filesystems can implement this to provide best-effort dentry cache reclaim.
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
    long GuestPageCacheBytesReclaimed,
    long HostMappedCacheBytesTrimmed,
    long DentriesDropped,
    long InodesEvicted,
    long SuperblocksScanned);

public static class VfsShrinker
{
    public static VfsShrinkStats Shrink(SyscallManager? syscallManager, VfsShrinkMode mode)
    {
        if (syscallManager == null || mode == VfsShrinkMode.None)
            return new VfsShrinkStats(0, 0, 0, 0, 0);

        var superblocks = new HashSet<SuperBlock>();
        foreach (var mount in syscallManager.Mounts)
            if (mount?.SB != null)
                superblocks.Add(mount.SB);

        long guestPageCacheBytesReclaimed = 0;
        long hostMappedCacheBytesTrimmed = 0;
        if ((mode & VfsShrinkMode.PageCache) != 0)
        {
            guestPageCacheBytesReclaimed = AddressSpacePolicy.TryReclaimBytes(long.MaxValue);
            hostMappedCacheBytesTrimmed = TrimHostMappedCaches(superblocks, false);
        }

        long dentriesDropped = 0;
        if ((mode & VfsShrinkMode.DentryCache) != 0)
            foreach (var sb in superblocks)
            {
                dentriesDropped += DropDentryCache(sb);
                if (sb is IDentryCacheDropper dropper)
                    dentriesDropped += dropper.DropDentryCache();
            }

        long inodesEvicted = 0;
        if ((mode & VfsShrinkMode.InodeCache) != 0)
            foreach (var sb in superblocks)
                inodesEvicted += EvictUnusedInodes(sb);

        return new VfsShrinkStats(
            guestPageCacheBytesReclaimed,
            hostMappedCacheBytesTrimmed,
            dentriesDropped,
            inodesEvicted,
            superblocks.Count);
    }

    internal static long TrimHostMappedCaches(IEnumerable<SuperBlock> superblocks, bool aggressive)
    {
        var seen = new HashSet<Inode>();
        long trimmed = 0;
        foreach (var sb in superblocks)
        {
            List<Inode> inodes;
            lock (sb.Lock)
            {
                inodes = sb.Inodes.ToList();
            }

            foreach (var inode in inodes)
            {
                if (!seen.Add(inode))
                    continue;
                if (inode is not IHostMappedCacheDropper dropper)
                    continue;
                trimmed += dropper.TrimMappedCache(aggressive);
            }
        }

        return trimmed;
    }

    internal static long DropDentryCache(SuperBlock sb)
    {
        var candidates = sb.SnapshotDentryLru();
        if (candidates.Count == 0) return 0;

        long dropped = 0;
        foreach (var candidate in candidates)
        {
            if (!candidate.IsTrackedBySuperBlock) continue;
            if (ReferenceEquals(candidate, sb.Root)) continue;
            if (candidate.DentryRefCount > 0) continue;
            dropped += DetachCachedSubtree(candidate);
        }

        return dropped;
    }

    public static long DetachCachedSubtree(Dentry root)
    {
        if (root.IsMounted) return 0;

        // Walk first, then reclaim in reverse (children before parents).
        // Reclaim only dentry objects that are fully reclaimable.
        var visited = new HashSet<Dentry>();
        var stack = new Stack<Dentry>();
        var traversal = new List<Dentry>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!visited.Add(current)) continue;
            if (current.IsMounted) continue;
            if (ReferenceEquals(current, current.SuperBlock.Root)) continue;
            traversal.Add(current);

            foreach (var child in current.Children.Values.ToList())
                stack.Push(child);
        }

        var mountProtected = new HashSet<Dentry>();
        long dropped = 0;
        for (var i = traversal.Count - 1; i >= 0; i--)
        {
            var current = traversal[i];
            if (!current.IsTrackedBySuperBlock) continue;
            _ = current.PruneCachedChildren(
                child => !child.IsMounted && child.DentryRefCount == 0 && child.Children.Count == 0,
                "VfsShrinker.DetachCachedSubtree.prune-children");

            var protectsMountPath = false;
            foreach (var child in current.Children.Values)
                if (child.IsMounted || mountProtected.Contains(child))
                {
                    protectsMountPath = true;
                    break;
                }

            if (protectsMountPath)
            {
                mountProtected.Add(current);
                continue;
            }

            if (current.DentryRefCount > 0) continue;

            var detachedFromParent = true;
            if (current.Parent != null && !ReferenceEquals(current.Parent, current))
                if (current.Parent.TryGetCachedChild(current.Name, out var cachedByParent) &&
                    ReferenceEquals(cachedByParent, current))
                    detachedFromParent = current.Parent.TryUncacheChild(
                        current.Name,
                        "VfsShrinker.DetachCachedSubtree",
                        out _);

            if (!detachedFromParent) continue;

            var inode = current.Inode;
            if (inode != null) current.UnbindInode("VfsShrinker.DetachCachedSubtree");

            current.UntrackFromSuperBlock("VfsShrinker.DetachCachedSubtree");
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
            if (inode.TryEvictCache("VfsShrinker.EvictUnusedInodes"))
                evicted++;

        return evicted;
    }
}