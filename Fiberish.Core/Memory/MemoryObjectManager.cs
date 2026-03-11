using System.Runtime.CompilerServices;
using Fiberish.VFS;

namespace Fiberish.Memory;

public sealed class MemoryObjectManager
{
    private readonly object _lock = new();
    private readonly Dictionary<string, MemoryObject> _namedObjects = new(StringComparer.Ordinal);

    /// <summary>
    ///     Get or create the inode's global page cache MemoryObject.
    ///     Reuses CreateOrOpenNamed (same mechanism as SysV shm).
    /// </summary>
    public MemoryObject GetOrCreateInodePageCache(Inode inode)
    {
        if (inode.PageCache != null)
        {
            inode.PageCache.AddRef();
            return inode.PageCache;
        }

        var key = $"pagecache:inode:{RuntimeHelpers.GetHashCode(inode)}";
        lock (_lock)
        {
            if (_namedObjects.TryGetValue(key, out var existing))
            {
                existing.AddRef(); // caller mapping reference
                inode.PageCache ??= existing;
                inode.PageCacheManager ??= this;
                return existing;
            }

            var isShmem = IsShmemInode(inode);
            var obj = new MemoryObject(
                MemoryObjectKind.File,
                null,
                0,
                0,
                true,
                isShmem ? MemoryObjectRole.ShmemSharedSource : MemoryObjectRole.FileSharedSource);
            GlobalPageCacheManager.TrackPageCache(obj, isShmem
                ? GlobalPageCacheManager.PageCacheClass.Shmem
                : GlobalPageCacheManager.PageCacheClass.File);
            _namedObjects[key] = obj; // manager-owned reference (initial ref=1)
            obj.AddRef(); // caller mapping reference
            inode.PageCache = obj;
            inode.PageCacheManager = this;
            return obj;
        }
    }

    public void ReleaseInodePageCache(Inode inode)
    {
        if (inode.PageCache == null) return;
        var key = $"pagecache:inode:{RuntimeHelpers.GetHashCode(inode)}";
        CloseNamed(key); // decrements ref; freed when count hits 0
        inode.PageCache = null;
        inode.PageCacheManager = null;
    }

    public MemoryObject CreateAnonymousSharedSource()
    {
        var obj = new MemoryObject(MemoryObjectKind.Anonymous, null, 0, 0, false,
            MemoryObjectRole.AnonSharedSourceZeroFill);
        GlobalPageCacheManager.TrackPageCache(obj, GlobalPageCacheManager.PageCacheClass.AnonSharedSource);
        return obj;
    }

    public MemoryObject CreateSharedAnonymous()
    {
        var obj = new MemoryObject(MemoryObjectKind.Anonymous, null, 0, 0, true,
            MemoryObjectRole.ShmemSharedSource);
        GlobalPageCacheManager.TrackPageCache(obj, GlobalPageCacheManager.PageCacheClass.Shmem);
        return obj;
    }

    public MemoryObject CreatePrivateOverlay()
    {
        return new MemoryObject(MemoryObjectKind.Anonymous, null, 0, 0, false,
            MemoryObjectRole.PrivateOverlay);
    }

    public MemoryObject CreateFile(LinuxFile fileHandle, long fileBaseOffset, long fileSize, bool shared)
    {
        return new MemoryObject(MemoryObjectKind.File, fileHandle, fileBaseOffset, fileSize, shared,
            shared ? MemoryObjectRole.FileSharedSource : MemoryObjectRole.PrivateOverlay);
    }

    public MemoryObject CreateOrOpenNamed(string name, Func<MemoryObject> factory, out bool created)
    {
        lock (_lock)
        {
            if (_namedObjects.TryGetValue(name, out var existing))
            {
                existing.AddRef();
                created = false;
                return existing;
            }

            var obj = factory();
            _namedObjects[name] = obj;
            created = true;
            return obj;
        }
    }

    public void CloseNamed(string name)
    {
        lock (_lock)
        {
            if (!_namedObjects.TryGetValue(name, out var obj)) return;
            _namedObjects.Remove(name);
            obj.Release();
        }
    }

    private static bool IsShmemInode(Inode inode)
    {
        var fsName = inode.SuperBlock?.Type?.Name;
        return string.Equals(fsName, "tmpfs", StringComparison.Ordinal);
    }
}