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
    public AddressSpace GetOrCreateMapping(Inode inode)
    {
        if (inode.Mapping != null)
        {
            inode.Mapping.AddRef();
            return inode.Mapping;
        }

        var key = $"pagecache:inode:{RuntimeHelpers.GetHashCode(inode)}";
        lock (_lock)
        {
            if (_namedObjects.TryGetValue(key, out var existing))
            {
                existing.AddRef(); // caller mapping reference
                inode.Mapping ??= (AddressSpace)existing;
                inode.PageCacheManager ??= this;
                return inode.Mapping;
            }

            var isShmem = IsShmemInode(inode);
            var obj = new AddressSpace(
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
            inode.Mapping = obj;
            inode.PageCacheManager = this;
            return obj;
        }
    }

    public void ReleaseMapping(Inode inode)
    {
        if (inode.Mapping == null) return;
        var key = $"pagecache:inode:{RuntimeHelpers.GetHashCode(inode)}";
        CloseNamed(key); // decrements ref; freed when count hits 0
        inode.Mapping = null;
        inode.PageCacheManager = null;
    }

    public AnonVma CreateAnonymousSharedSource()
    {
        var obj = new AnonVma(MemoryObjectKind.Anonymous, null, 0, 0, false,
            MemoryObjectRole.AnonSharedSourceZeroFill);
        GlobalPageCacheManager.TrackPageCache(obj, GlobalPageCacheManager.PageCacheClass.AnonSharedSource);
        return obj;
    }

    public AddressSpace CreateSharedAnonymous()
    {
        var obj = new AddressSpace(MemoryObjectKind.Anonymous, null, 0, 0, true,
            MemoryObjectRole.ShmemSharedSource);
        GlobalPageCacheManager.TrackPageCache(obj, GlobalPageCacheManager.PageCacheClass.Shmem);
        return obj;
    }

    public AnonVma CreatePrivateOverlay()
    {
        return new AnonVma(MemoryObjectKind.Anonymous, null, 0, 0, false,
            MemoryObjectRole.PrivateOverlay);
    }

    public MemoryObject CreateFile(LinuxFile fileHandle, long fileBaseOffset, long fileSize, bool shared)
    {
        return shared
            ? new AddressSpace(MemoryObjectKind.File, fileHandle, fileBaseOffset, fileSize, true)
            : new AnonVma(MemoryObjectKind.File, fileHandle, fileBaseOffset, fileSize, false,
                MemoryObjectRole.PrivateOverlay);
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