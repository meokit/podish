using System.Runtime.CompilerServices;
using Fiberish.VFS;

namespace Fiberish.Memory;

public sealed class MemoryObjectManager
{
    private readonly Dictionary<string, MemoryObject> _namedObjects = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public static MemoryObjectManager Instance { get; } = new();

    /// <summary>
    ///     Get or create the inode's global page cache MemoryObject.
    ///     Reuses CreateOrOpenNamed (same mechanism as SysV shm).
    /// </summary>
    public MemoryObject GetOrCreateInodePageCache(Inode inode)
    {
        var key = $"pagecache:inode:{RuntimeHelpers.GetHashCode(inode)}";
        lock (_lock)
        {
            if (_namedObjects.TryGetValue(key, out var existing))
            {
                existing.AddRef(); // caller mapping reference
                inode.PageCache ??= existing;
                return existing;
            }

            var obj = new MemoryObject(MemoryObjectKind.File, null, 0, 0, true);
            _namedObjects[key] = obj; // manager-owned reference (initial ref=1)
            obj.AddRef(); // caller mapping reference
            inode.PageCache = obj;
            return obj;
        }
    }

    public void ReleaseInodePageCache(Inode inode)
    {
        if (inode.PageCache == null) return;
        var key = $"pagecache:inode:{RuntimeHelpers.GetHashCode(inode)}";
        CloseNamed(key); // decrements ref; freed when count hits 0
        inode.PageCache = null;
    }

    public MemoryObject CreateAnonymous(bool shared)
    {
        return new MemoryObject(MemoryObjectKind.Anonymous, null, 0, 0, shared);
    }

    public MemoryObject CreateFile(Fiberish.VFS.LinuxFile fileHandle, long fileBaseOffset, long fileSize, bool shared)
    {
        return new MemoryObject(MemoryObjectKind.File, fileHandle, fileBaseOffset, fileSize, shared);
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
}
