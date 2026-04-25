using Fiberish.Memory;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Memory;

public class InodeMappingTests
{
    [Fact]
    public void InodePageCache_DoesNotCollide_ForDifferentInodesWithSameIno()
    {
        var sb = new TestSuperBlock();
        var inodeA = new TestInode(sb, 42);
        var inodeB = new TestInode(sb, 42);

        var cacheA = inodeA.AcquireMappingRef();
        var cacheB = inodeB.AcquireMappingRef();

        try
        {
            Assert.NotSame(cacheA, cacheB);
        }
        finally
        {
            cacheA.Release();
            cacheB.Release();
        }
    }

    [Fact]
    public void InodePageCache_ReusesSameMappingAcrossAcquireCalls()
    {
        var sb = new TestSuperBlock();
        var inode = new TestInode(sb, 99);

        var cacheA = inode.AcquireMappingRef();
        var cacheB = inode.AcquireMappingRef();

        try
        {
            Assert.Same(cacheA, cacheB);
        }
        finally
        {
            cacheA.Release();
            cacheB.Release();
        }
    }

    private sealed class TestSuperBlock : SuperBlock
    {
        public TestSuperBlock() : base(null, new MemoryRuntimeContext())
        {
        }

        public override Inode AllocInode()
        {
            throw new NotSupportedException();
        }
    }

    private sealed class TestInode : MappingBackedInode
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
