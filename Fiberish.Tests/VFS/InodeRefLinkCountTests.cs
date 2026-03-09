using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.VFS;

public class InodeRefLinkCountTests
{
    [Fact]
    public void Tmpfs_CreateLinkUnlink_OpenClose_TracksRefAndLinkCount()
    {
        using var rig = FileSystemTestRigFactory.Create("tmpfs");
        var root = rig.Root;
        var rootInode = rig.RootInode;

        var original = new Dentry("original.txt", null, root, rig.SuperBlock);
        rootInode.Create(original, 0x1A4, 0, 0);
        var inode = original.Inode!;

        Assert.True(inode.HasExplicitLinkCount);
        Assert.Equal(1, inode.LinkCount);
        Assert.False(inode.HasActiveRuntimeRefs);
        Assert.False(rig.SuperBlock.HasActiveInodes());
        var refAfterCreate = inode.RefCount;

        var linked = new Dentry("linked.txt", null, root, rig.SuperBlock);
        rootInode.Link(linked, inode);
        Assert.Equal(2, inode.LinkCount);
        Assert.Equal(refAfterCreate + 1, inode.RefCount);

        rootInode.Unlink("original.txt");
        Assert.Equal(1, inode.LinkCount);
        Assert.Equal(refAfterCreate, inode.RefCount);
        Assert.Same(inode, linked.Inode);
        Assert.NotNull(rootInode.Lookup("linked.txt"));

        var mount = new Mount(rig.SuperBlock, rig.Root);
        var refBeforeOpen = inode.RefCount;
        var file = new LinuxFile(linked, FileFlags.O_RDONLY, mount);
        Assert.Equal(refBeforeOpen + 1, inode.RefCount);
        Assert.Equal(1, inode.LinkCount);
        Assert.Equal(1, inode.FileOpenRefCount);
        Assert.True(inode.HasActiveRuntimeRefs);
        Assert.True(rig.SuperBlock.HasActiveInodes());

        file.Close();
        Assert.Equal(refBeforeOpen, inode.RefCount);
        Assert.Equal(1, inode.LinkCount);
        Assert.Equal(0, inode.FileOpenRefCount);
        Assert.False(inode.HasActiveRuntimeRefs);
        Assert.False(rig.SuperBlock.HasActiveInodes());

        rootInode.Unlink("linked.txt");
        Assert.Equal(0, inode.LinkCount);
        Assert.Equal(0, inode.RefCount);
        Assert.True(inode.IsEvicted);
    }

    [Fact]
    public void Tmpfs_MmapHold_TracksFileMmapRefKind()
    {
        using var rig = FileSystemTestRigFactory.Create("tmpfs");
        var root = rig.Root;
        var rootInode = rig.RootInode;

        var mapped = new Dentry("mapped.txt", null, root, rig.SuperBlock);
        rootInode.Create(mapped, 0x1A4, 0, 0);
        var inode = mapped.Inode!;
        var mount = new Mount(rig.SuperBlock, rig.Root);

        var hold = new LinuxFile(mapped, FileFlags.O_RDONLY, mount, LinuxFile.ReferenceKind.MmapHold);
        Assert.Equal(1, inode.FileMmapRefCount);
        Assert.Equal(0, inode.FileOpenRefCount);
        Assert.True(inode.HasActiveRuntimeRefs);
        Assert.True(rig.SuperBlock.HasActiveInodes());

        hold.Close();
        Assert.Equal(0, inode.FileMmapRefCount);
        Assert.False(inode.HasActiveRuntimeRefs);
        Assert.False(rig.SuperBlock.HasActiveInodes());
    }
}
