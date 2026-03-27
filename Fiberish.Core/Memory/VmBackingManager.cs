using System.Runtime.CompilerServices;
using Fiberish.VFS;

namespace Fiberish.Memory;

public sealed class VmBackingManager
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, AddressSpace> _namedObjects = new(StringComparer.Ordinal);

    /// <summary>
    ///     Get or create the inode's global address_space.
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
                inode.Mapping ??= existing;
                inode.MappingManager ??= this;
                return inode.Mapping;
            }

            var isShmem = IsShmemInode(inode);
            var obj = new AddressSpace(isShmem ? AddressSpaceKind.Shmem : AddressSpaceKind.File);
            GlobalAddressSpaceCacheManager.TrackAddressSpace(obj, isShmem
                ? GlobalAddressSpaceCacheManager.AddressSpaceCacheClass.Shmem
                : GlobalAddressSpaceCacheManager.AddressSpaceCacheClass.File);
            _namedObjects[key] = obj; // manager-owned reference (initial ref=1)
            obj.AddRef(); // caller mapping reference
            inode.Mapping = obj;
            inode.MappingManager = this;
            return obj;
        }
    }

    public void ReleaseMapping(Inode inode)
    {
        if (inode.Mapping == null) return;
        var key = $"pagecache:inode:{RuntimeHelpers.GetHashCode(inode)}";
        CloseNamed(key); // decrements ref; freed when count hits 0
        inode.Mapping = null;
        inode.MappingManager = null;
    }

    public AddressSpace CreateSharedAnonymous()
    {
        var obj = new AddressSpace(AddressSpaceKind.Shmem);
        GlobalAddressSpaceCacheManager.TrackAddressSpace(obj,
            GlobalAddressSpaceCacheManager.AddressSpaceCacheClass.Shmem);
        return obj;
    }

    public AnonVma CreatePrivateOverlay()
    {
        return new AnonVma();
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