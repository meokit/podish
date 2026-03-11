using Fiberish.Memory;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Memory;

public class MemoryObjectManagerTests
{
    [Fact]
    public void InodePageCache_DoesNotCollide_ForDifferentInodesWithSameIno()
    {
        var manager = new MemoryObjectManager();
        var sb = new TestSuperBlock();
        var inodeA = new TestInode(sb, 42);
        var inodeB = new TestInode(sb, 42);

        var cacheA = manager.GetOrCreateInodePageCache(inodeA);
        var cacheB = manager.GetOrCreateInodePageCache(inodeB);

        try
        {
            Assert.NotSame(cacheA, cacheB);
        }
        finally
        {
            manager.ReleaseInodePageCache(inodeA);
            manager.ReleaseInodePageCache(inodeB);
            cacheA.Release();
            cacheB.Release();
        }
    }

    [Fact]
    public void InodePageCache_IsIsolatedAcrossManagers()
    {
        var managerA = new MemoryObjectManager();
        var managerB = new MemoryObjectManager();
        var sb = new TestSuperBlock();
        var inode = new TestInode(sb, 99);

        var cacheA = managerA.GetOrCreateInodePageCache(inode);
        managerA.ReleaseInodePageCache(inode);
        cacheA.Release();

        var cacheB = managerB.GetOrCreateInodePageCache(inode);

        try
        {
            Assert.NotSame(cacheA, cacheB);
        }
        finally
        {
            managerB.ReleaseInodePageCache(inode);
            cacheB.Release();
        }
    }

    private sealed class TestSuperBlock : SuperBlock
    {
        public override Inode AllocInode()
        {
            throw new NotSupportedException();
        }
    }

    private sealed class TestInode : Inode
    {
        public TestInode(SuperBlock sb, ulong ino)
        {
            SuperBlock = sb;
            Ino = ino;
            Type = InodeType.File;
            Mode = 0x1A4; // 0644
        }
    }
}