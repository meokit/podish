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

    [Fact]
    public void Tmpfs_RenameOverwrite_DropsReplacedInodeLinkCount()
    {
        using var rig = FileSystemTestRigFactory.Create("tmpfs");
        var root = rig.Root;
        var rootInode = rig.RootInode;

        var src = new Dentry("src.txt", null, root, rig.SuperBlock);
        rootInode.Create(src, 0x1A4, 0, 0);
        var srcInode = src.Inode!;

        var dst = new Dentry("dst.txt", null, root, rig.SuperBlock);
        rootInode.Create(dst, 0x1A4, 0, 0);
        var dstInode = dst.Inode!;

        Assert.Equal(1, srcInode.LinkCount);
        Assert.Equal(1, dstInode.LinkCount);

        rootInode.Rename("src.txt", rootInode, "dst.txt");

        var renamed = rootInode.Lookup("dst.txt");
        Assert.NotNull(renamed);
        Assert.Same(srcInode, renamed!.Inode);
        Assert.Equal(1, srcInode.LinkCount);
        Assert.Equal(0, dstInode.LinkCount);
        Assert.True(dstInode.IsEvicted);
    }

    [Fact]
    public void Tmpfs_Rename_WhenOldAndNewAreSameInode_IsNoOp()
    {
        using var rig = FileSystemTestRigFactory.Create("tmpfs");
        var root = rig.Root;
        var rootInode = rig.RootInode;

        var src = new Dentry("src.txt", null, root, rig.SuperBlock);
        rootInode.Create(src, 0x1A4, 0, 0);
        var inode = src.Inode!;

        var alias = new Dentry("alias.txt", null, root, rig.SuperBlock);
        rootInode.Link(alias, inode);
        Assert.Equal(2, inode.LinkCount);

        rootInode.Rename("src.txt", rootInode, "alias.txt");

        var srcAfter = rootInode.Lookup("src.txt");
        var aliasAfter = rootInode.Lookup("alias.txt");
        Assert.NotNull(srcAfter);
        Assert.NotNull(aliasAfter);
        Assert.Same(inode, srcAfter!.Inode);
        Assert.Same(inode, aliasAfter!.Inode);
        Assert.Equal(2, inode.LinkCount);
    }
}
