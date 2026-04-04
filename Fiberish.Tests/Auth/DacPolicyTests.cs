using Fiberish.Auth.Permission;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Auth;

public class DacPolicyTests
{
    [Theory]
    [InlineData(InodeType.File)]
    [InlineData(InodeType.Directory)]
    [InlineData(InodeType.Fifo)]
    [InlineData(InodeType.CharDev)]
    [InlineData(InodeType.BlockDev)]
    [InlineData(InodeType.Socket)]
    public void ApplySetIdClearOnChown_ClearsBits_ForNonSymlinkInodes(InodeType type)
    {
        var inode = new TestInode(type, 0x1ED | 0xC00);

        var mode = DacPolicy.ApplySetIdClearOnChown(inode, oldUid: 1, oldGid: 2, newUid: 3, newGid: 2);

        Assert.Equal(0x1ED, mode);
    }

    [Fact]
    public void ApplySetIdClearOnChown_PreservesBits_ForSymlink()
    {
        var inode = new TestInode(InodeType.Symlink, 0x1ED | 0xC00);

        var mode = DacPolicy.ApplySetIdClearOnChown(inode, oldUid: 1, oldGid: 2, newUid: 3, newGid: 2);

        Assert.Equal(0xDED, mode);
    }

    private sealed class TestInode : Inode
    {
        public TestInode(InodeType type, int mode)
        {
            Type = type;
            Mode = mode;
        }

        public override Dentry? Lookup(string name)
        {
            return null;
        }
    }
}
