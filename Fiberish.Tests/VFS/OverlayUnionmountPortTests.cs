using System.Text;
using Fiberish.Native;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.VFS;

public class OverlayUnionmountPortTests
{
    [Fact]
    public void Unionmount_RenameNewEmptyDirOverEmptyLowerDir_MatchesUpstreamSemantics()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateDirectory(lowerSb.Root, lowerSb, "empty");
        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        var created = new Dentry("empty-new", null, overlaySb.Root, overlaySb);
        root.Mkdir(created, 0x1ED, 0, 0);

        root.Rename("empty-new", root, "empty");

        Assert.Null(root.Lookup("empty-new"));
        var replaced = root.Lookup("empty");
        Assert.NotNull(replaced);
        Assert.Equal(InodeType.Directory, replaced!.Inode!.Type);
        Assert.Empty(replaced.Inode.GetEntries().Where(e => e.Name is not "." and not ".."));
    }

    [Fact]
    public void Unionmount_MoveNewDirBranchIntoLowerAncestor_MatchesUpstreamSemantics()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateDirectory(lowerSb.Root, lowerSb, "empty");
        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        var empty = root.Lookup("empty");
        Assert.NotNull(empty);
        var emptyInode = Assert.IsType<OverlayInode>(empty!.Inode);

        var newParent = new Dentry("newp", null, empty, overlaySb);
        emptyInode.Mkdir(newParent, 0x1ED, 0, 0);
        var newParentInode = Assert.IsType<OverlayInode>(newParent.Inode);

        var child = new Dentry("new", null, newParent, overlaySb);
        newParentInode.Mkdir(child, 0x1ED, 0, 0);

        emptyInode.Rename("newp", root, "moved");

        Assert.Null(emptyInode.Lookup("newp"));
        Assert.NotNull(root.Lookup("moved"));
        var moved = root.Lookup("moved")!;
        var movedInode = Assert.IsType<OverlayInode>(moved.Inode);
        Assert.NotNull(movedInode.Lookup("new"));
    }

    [Fact]
    public void Unionmount_RenameChildThenParentThenMoveDirectory_MatchesUpstreamSemantics()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateDirectory(lowerSb.Root, lowerSb, "src");
        CreateDirectory(lowerSb.Root, lowerSb, "dst");
        var src = lowerSb.Root.Inode!.Lookup("src")!;
        CreateFile(src, lowerSb, "a", "A");
        CreateDirectory(src, lowerSb, "pop");
        var pop = src.Inode!.Lookup("pop")!;
        CreateFile(pop, lowerSb, "b", "B");

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        var srcOverlay = root.Lookup("src");
        Assert.NotNull(srcOverlay);
        var srcInode = Assert.IsType<OverlayInode>(srcOverlay!.Inode);
        srcInode.Rename("pop", srcInode, "popx");
        root.Rename("src", root, "srcx");

        var dstOverlay = root.Lookup("dst");
        Assert.NotNull(dstOverlay);
        var dstInode = Assert.IsType<OverlayInode>(dstOverlay!.Inode);
        root.Rename("srcx", dstInode, "src");

        Assert.Null(root.Lookup("src"));
        Assert.Null(root.Lookup("srcx"));

        var moved = dstInode.Lookup("src");
        Assert.NotNull(moved);
        var movedInode = Assert.IsType<OverlayInode>(moved!.Inode);
        Assert.NotNull(movedInode.Lookup("a"));
        Assert.Null(movedInode.Lookup("pop"));
        var popx = movedInode.Lookup("popx");
        Assert.NotNull(popx);
        Assert.Equal("B", ReadAll(popx!.Inode!.Lookup("b")!));
    }

    [Fact]
    public void Unionmount_RenameNewEmptyDirOverPopulatedLowerDir_FailsAndPreservesBoth()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateDirectory(lowerSb.Root, lowerSb, "empty");
        CreateDirectory(lowerSb.Root, lowerSb, "populated");
        var populated = lowerSb.Root.Inode!.Lookup("populated")!;
        CreateFile(populated, lowerSb, "a", "A");

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        var created = new Dentry("empty-new", null, overlaySb.Root, overlaySb);
        root.Mkdir(created, 0x1ED, 0, 0);

        Assert.True(root.Rename("empty-new", root, "populated") < 0);
        Assert.NotNull(root.Lookup("empty-new"));
        var stillPopulated = root.Lookup("populated");
        Assert.NotNull(stillPopulated);
        Assert.NotNull(stillPopulated!.Inode!.Lookup("a"));
    }

    [Fact]
    public void Unionmount_RenamePopulatedDirOverEmptyDir_ReplacesDestination()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateDirectory(lowerSb.Root, lowerSb, "src");
        CreateDirectory(lowerSb.Root, lowerSb, "empty");
        var src = lowerSb.Root.Inode!.Lookup("src")!;
        CreateFile(src, lowerSb, "a", "A");

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        root.Rename("src", root, "empty");

        Assert.Null(root.Lookup("src"));
        var replaced = root.Lookup("empty");
        Assert.NotNull(replaced);
        Assert.NotNull(replaced!.Inode!.Lookup("a"));
    }

    [Fact]
    public void Unionmount_RenameDirOverOwnChildFile_FailsAndPreservesTree()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateDirectory(lowerSb.Root, lowerSb, "src");
        var src = lowerSb.Root.Inode!.Lookup("src")!;
        CreateFile(src, lowerSb, "a", "A");
        CreateDirectory(src, lowerSb, "pop");
        var pop = src.Inode!.Lookup("pop")!;
        CreateFile(pop, lowerSb, "b", "B");

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        var srcOverlay = Assert.IsType<OverlayInode>(root.Lookup("src")!.Inode);
        Assert.True(root.Rename("src", srcOverlay, "a") < 0);
        var srcAfter = root.Lookup("src");
        Assert.NotNull(srcAfter);
        Assert.NotNull(srcAfter!.Inode!.Lookup("a"));
        Assert.NotNull(srcAfter.Inode.Lookup("pop"));
    }

    [Fact]
    public void Unionmount_RenameDirIntoOwnSubdirectory_FailsAndPreservesTree()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateDirectory(lowerSb.Root, lowerSb, "src");
        var src = lowerSb.Root.Inode!.Lookup("src")!;
        CreateDirectory(src, lowerSb, "pop");
        var pop = src.Inode!.Lookup("pop")!;
        CreateFile(pop, lowerSb, "b", "B");

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);
        var srcOverlay = Assert.IsType<OverlayInode>(root.Lookup("src")!.Inode);
        var popOverlay = Assert.IsType<OverlayInode>(srcOverlay.Lookup("pop")!.Inode);

        Assert.True(root.Rename("src", popOverlay, "src") < 0);
        Assert.NotNull(root.Lookup("src"));
        Assert.NotNull(srcOverlay.Lookup("pop"));
        Assert.NotNull(popOverlay.Lookup("b"));
    }

    [Fact]
    public void Unionmount_RenameEmptyDir_RemoveOldNameAfterRoundTrip_MatchesUpstreamSemantics()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateDirectory(lowerSb.Root, lowerSb, "empty");

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        root.Rename("empty", root, "empty2");
        Assert.Null(root.Lookup("empty"));

        root.Rename("empty2", root, "empty");
        Assert.True(root.Rmdir("empty2") < 0);

        var empty = root.Lookup("empty");
        Assert.NotNull(empty);
        root.Rmdir("empty");

        Assert.Null(root.Lookup("empty"));
        Assert.Null(root.Lookup("empty2"));
    }

    [Fact]
    public void Unionmount_RenameEmptyDir_UnlinkOldNameFailsAndDoesNotResurrectOldPath()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateDirectory(lowerSb.Root, lowerSb, "empty");

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        root.Rename("empty", root, "empty2");
        Assert.True(root.Unlink("empty") < 0);

        root.Rename("empty2", root, "empty");
        Assert.True(root.Unlink("empty") < 0);

        Assert.NotNull(root.Lookup("empty"));
        Assert.Null(root.Lookup("empty2"));
    }

    [Fact]
    public void Unionmount_RenameEmptyDir_Twice_MatchesUpstreamSemantics()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateDirectory(lowerSb.Root, lowerSb, "empty");

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        root.Rename("empty", root, "empty2");
        Assert.True(root.Rename("empty", root, "empty2") < 0);

        root.Rename("empty2", root, "empty3");
        Assert.True(root.Rename("empty2", root, "empty3") < 0);

        Assert.Null(root.Lookup("empty"));
        Assert.Null(root.Lookup("empty2"));
        Assert.NotNull(root.Lookup("empty3"));
    }

    [Fact]
    public void Unionmount_RenameEmptyDir_OverSelf_IsNoOp()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateDirectory(lowerSb.Root, lowerSb, "empty");

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        root.Rename("empty", root, "empty");

        var empty = root.Lookup("empty");
        Assert.NotNull(empty);
        Assert.Equal(InodeType.Directory, empty!.Inode!.Type);
    }

    [Fact]
    public void Unionmount_RenameEmptyDir_OverParentDir_FailsAndPreservesDirectory()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateDirectory(lowerSb.Root, lowerSb, "empty");

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        Assert.True(root.Rename("empty", root, ".") < 0);

        var empty = root.Lookup("empty");
        Assert.NotNull(empty);
        Assert.Equal(InodeType.Directory, empty!.Inode!.Type);
    }

    [Fact]
    public void Unionmount_RenamePopulatedDir_RemoveOldNameAfterRoundTrip_MatchesUpstreamSemantics()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateDirectory(lowerSb.Root, lowerSb, "src");
        var src = lowerSb.Root.Inode!.Lookup("src")!;
        CreateFile(src, lowerSb, "a", "A");

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        root.Rename("src", root, "dst");
        Assert.True(root.Rmdir("src") < 0);

        root.Rename("dst", root, "src");
        Assert.True(root.Rmdir("src") < 0);

        var restored = root.Lookup("src");
        Assert.NotNull(restored);
        Assert.Equal("A", ReadAll(restored!.Inode!.Lookup("a")!));
        Assert.Null(root.Lookup("dst"));
    }

    [Fact]
    public void Unionmount_RenamePopulatedDir_UnlinkOldNameFailsAndPreservesTree()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateDirectory(lowerSb.Root, lowerSb, "src");
        var src = lowerSb.Root.Inode!.Lookup("src")!;
        CreateFile(src, lowerSb, "a", "A");

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        root.Rename("src", root, "dst");
        Assert.True(root.Unlink("src") < 0);

        root.Rename("dst", root, "src");
        Assert.True(root.Unlink("src") < 0);

        var restored = root.Lookup("src");
        Assert.NotNull(restored);
        Assert.Equal("A", ReadAll(restored!.Inode!.Lookup("a")!));
        Assert.Null(root.Lookup("dst"));
    }

    [Fact]
    public void Unionmount_RenamePopulatedDir_Twice_MatchesUpstreamSemantics()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateDirectory(lowerSb.Root, lowerSb, "src");
        var src = lowerSb.Root.Inode!.Lookup("src")!;
        CreateFile(src, lowerSb, "a", "A");

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        root.Rename("src", root, "mid");
        Assert.True(root.Rename("src", root, "mid") < 0);

        root.Rename("mid", root, "dst");
        Assert.True(root.Rename("mid", root, "dst") < 0);

        Assert.Null(root.Lookup("src"));
        Assert.Null(root.Lookup("mid"));
        Assert.Equal("A", ReadAll(root.Lookup("dst")!.Inode!.Lookup("a")!));
    }

    [Fact]
    public void Unionmount_RenamePopulatedDir_OverSelf_IsNoOp()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateDirectory(lowerSb.Root, lowerSb, "src");
        var src = lowerSb.Root.Inode!.Lookup("src")!;
        CreateFile(src, lowerSb, "a", "A");

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        root.Rename("src", root, "src");

        var unchanged = root.Lookup("src");
        Assert.NotNull(unchanged);
        Assert.Equal("A", ReadAll(unchanged!.Inode!.Lookup("a")!));
    }

    [Fact]
    public void Unionmount_MovePopulatedDir_ToSibling_MatchesUpstreamSemantics()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateDirectory(lowerSb.Root, lowerSb, "src");
        var src = lowerSb.Root.Inode!.Lookup("src")!;
        CreateFile(src, lowerSb, "a", "A");
        CreateDirectory(lowerSb.Root, lowerSb, "empty");

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);
        var empty = Assert.IsType<OverlayInode>(root.Lookup("empty")!.Inode);

        root.Rename("src", empty, "x");
        Assert.True(root.Rename("src", empty, "x") < 0);

        Assert.Null(root.Lookup("src"));
        var moved = empty.Lookup("x");
        Assert.NotNull(moved);
        Assert.Equal("A", ReadAll(moved!.Inode!.Lookup("a")!));
    }

    [Fact]
    public void Unionmount_MovePopulatedSubdir_ToSiblingParent_MatchesUpstreamSemantics()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateDirectory(lowerSb.Root, lowerSb, "src");
        var src = lowerSb.Root.Inode!.Lookup("src")!;
        CreateDirectory(src, lowerSb, "pop");
        CreateFile(src.Inode!.Lookup("pop")!, lowerSb, "b", "B");
        CreateDirectory(lowerSb.Root, lowerSb, "empty");

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);
        var srcOverlay = Assert.IsType<OverlayInode>(root.Lookup("src")!.Inode);
        var empty = Assert.IsType<OverlayInode>(root.Lookup("empty")!.Inode);

        srcOverlay.Rename("pop", empty, "pop");
        Assert.True(srcOverlay.Rename("pop", empty, "pop") < 0);

        Assert.Null(srcOverlay.Lookup("pop"));
        var moved = empty.Lookup("pop");
        Assert.NotNull(moved);
        Assert.Equal("B", ReadAll(moved!.Inode!.Lookup("b")!));
    }

    [Fact]
    public void Unionmount_MovePopulatedDir_IntoAnotherThenMoveSubdir_AcrossParents_MatchesUpstreamSemantics()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateDirectory(lowerSb.Root, lowerSb, "src");
        var src = lowerSb.Root.Inode!.Lookup("src")!;
        CreateFile(src, lowerSb, "a", "A");
        CreateDirectory(src, lowerSb, "pop");
        CreateFile(src.Inode!.Lookup("pop")!, lowerSb, "b", "B");
        CreateDirectory(lowerSb.Root, lowerSb, "empty");

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);
        var empty = Assert.IsType<OverlayInode>(root.Lookup("empty")!.Inode);

        root.Rename("src", empty, "src");
        var moved = Assert.IsType<OverlayInode>(empty.Lookup("src")!.Inode);
        moved.Rename("pop", root, "pop");

        Assert.Null(root.Lookup("src"));
        Assert.NotNull(empty.Lookup("src"));
        Assert.Null(moved.Lookup("pop"));
        Assert.Equal("A", ReadAll(moved.Lookup("a")!));
        Assert.Equal("B", ReadAll(root.Lookup("pop")!.Inode!.Lookup("b")!));
    }

    [Fact]
    public void Unionmount_RenameNewPopulatedDirOverRemovedUnionedDir_SameFiles_MatchesUpstreamSemantics()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateDirectory(lowerSb.Root, lowerSb, "pop");
        var lowerPop = lowerSb.Root.Inode!.Lookup("pop")!;
        CreateFile(lowerPop, lowerSb, "b", "B");

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        var created = new Dentry("pop-new", null, overlaySb.Root, overlaySb);
        Assert.Equal(0, root.Mkdir(created, 0x1ED, 0, 0));
        _ = Assert.IsType<OverlayInode>(created.Inode);
        CreateFile(created, overlaySb, "b", "aaaa");

        var pop = Assert.IsType<OverlayInode>(root.Lookup("pop")!.Inode);
        pop.Unlink("b");
        root.Rmdir("pop");

        root.Rename("pop-new", root, "pop");

        Assert.Null(root.Lookup("pop-new"));
        var replaced = Assert.IsType<OverlayInode>(root.Lookup("pop")!.Inode);
        Assert.Equal("aaaa", ReadAll(replaced.Lookup("b")!));
    }

    [Fact]
    public void Unionmount_Mkdir_InEmptyLowerDir_CreatesSubdirWithoutHidingExistingState()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateDirectory(lowerSb.Root, lowerSb, "empty");
        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);
        var empty = Assert.IsType<OverlayInode>(root.Lookup("empty")!.Inode);

        var sub = new Dentry("sub", null, root.Lookup("empty"), overlaySb);
        empty.Mkdir(sub, 0x1ED, 0, 0);

        Assert.NotNull(empty.Lookup("sub"));
        Assert.True(empty.Mkdir(new Dentry("sub", null, root.Lookup("empty"), overlaySb), 0x1ED, 0, 0) < 0);
    }

    [Fact]
    public void Unionmount_Mkdir_OverExistingLowerFile_FailsWithExists()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateFile(lowerSb.Root, lowerSb, "file", ":xxx:yyy:zzz");
        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        Assert.True(root.Mkdir(new Dentry("file", null, overlaySb.Root, overlaySb), 0x1ED, 0, 0) < 0);
        Assert.Equal(":xxx:yyy:zzz", ReadAll(root.Lookup("file")!));
    }

    [Fact]
    public void Unionmount_Rmdir_EmptyLowerDir_RemovesAndStaysGone()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateDirectory(lowerSb.Root, lowerSb, "empty");
        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        root.Rmdir("empty");
        Assert.Null(root.Lookup("empty"));
        Assert.True(root.Rmdir("empty") < 0);
    }

    [Fact]
    public void Unionmount_Rmdir_PopulatedLowerDir_FailsUntilEntriesRemoved()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateDirectory(lowerSb.Root, lowerSb, "populated");
        var populated = lowerSb.Root.Inode!.Lookup("populated")!;
        CreateFile(populated, lowerSb, "a", "");

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);
        Assert.True(root.Rmdir("populated") < 0);

        var populatedOverlay = Assert.IsType<OverlayInode>(root.Lookup("populated")!.Inode);
        populatedOverlay.Unlink("a");
        root.Rmdir("populated");
        Assert.Null(root.Lookup("populated"));
    }

    [Fact]
    public void Unionmount_HardLink_File_CreatesSecondNameWithSameContent()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateFile(lowerSb.Root, lowerSb, "file", ":xxx:yyy:zzz");
        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        var source = Assert.IsType<OverlayInode>(root.Lookup("file")!.Inode);
        var link = new Dentry("file2", null, overlaySb.Root, overlaySb);
        root.Link(link, source);

        Assert.Equal(":xxx:yyy:zzz", ReadAll(root.Lookup("file")!));
        Assert.Equal(":xxx:yyy:zzz", ReadAll(root.Lookup("file2")!));
    }

    [Fact]
    public void Unionmount_HardLink_FileOverExistingFile_FailsAndPreservesBoth()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateFile(lowerSb.Root, lowerSb, "file", ":xxx:yyy:zzz");
        CreateFile(lowerSb.Root, lowerSb, "target", "");
        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        var source = Assert.IsType<OverlayInode>(root.Lookup("file")!.Inode);
        Assert.True(root.Link(new Dentry("target", null, overlaySb.Root, overlaySb), source) < 0);
        Assert.Equal(":xxx:yyy:zzz", ReadAll(root.Lookup("file")!));
        Assert.Equal("", ReadAll(root.Lookup("target")!));
    }

    [Fact]
    public void Unionmount_RenameFile_OverExistingLowerFile_ReplacesDestinationContent()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateFile(lowerSb.Root, lowerSb, "file", ":xxx:yyy:zzz");
        CreateFile(lowerSb.Root, lowerSb, "target", "");

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        root.Rename("file", root, "target");

        Assert.Null(root.Lookup("file"));
        Assert.Equal(":xxx:yyy:zzz", ReadAll(root.Lookup("target")!));
    }

    [Fact]
    public void Unionmount_RenameFile_BackAndForth_PreservesContentAndOldNameDisappears()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateFile(lowerSb.Root, lowerSb, "file", ":xxx:yyy:zzz");

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        root.Rename("file", root, "file2");
        Assert.Null(root.Lookup("file"));
        Assert.Equal(":xxx:yyy:zzz", ReadAll(root.Lookup("file2")!));

        root.Rename("file2", root, "file");
        Assert.Null(root.Lookup("file2"));
        Assert.Equal(":xxx:yyy:zzz", ReadAll(root.Lookup("file")!));
    }

    [Fact]
    public void Unionmount_RenameFile_ThenUnlink_RemovesRenamedEntry()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateFile(lowerSb.Root, lowerSb, "file", ":xxx:yyy:zzz");

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        root.Rename("file", root, "file2");
        root.Unlink("file2");

        Assert.Null(root.Lookup("file"));
        Assert.Null(root.Lookup("file2"));
    }

    [Fact]
    public void Unionmount_RenameFile_OverDirectory_FailsAndPreservesFile()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateFile(lowerSb.Root, lowerSb, "file", ":xxx:yyy:zzz");
        CreateDirectory(lowerSb.Root, lowerSb, "empty");
        CreateDirectory(lowerSb.Root, lowerSb, "populated");
        CreateFile(lowerSb.Root.Inode!.Lookup("populated")!, lowerSb, "a", "A");

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        Assert.True(root.Rename("file", root, "empty") < 0);
        Assert.True(root.Rename("file", root, "populated") < 0);

        Assert.Equal(":xxx:yyy:zzz", ReadAll(root.Lookup("file")!));
        Assert.NotNull(root.Lookup("empty"));
        Assert.NotNull(root.Lookup("populated"));
    }

    [Fact]
    public void Unionmount_RenameHardLinkedFile_PreservesRemainingNames()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateFile(lowerSb.Root, lowerSb, "file", ":xxx:yyy:zzz");
        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        var source = Assert.IsType<OverlayInode>(root.Lookup("file")!.Inode);
        root.Link(new Dentry("file2", null, overlaySb.Root, overlaySb), source);
        root.Rename("file2", root, "file3");
        root.Rename("file", root, "file4");

        Assert.Null(root.Lookup("file"));
        Assert.Null(root.Lookup("file2"));
        Assert.Equal(":xxx:yyy:zzz", ReadAll(root.Lookup("file3")!));
        Assert.Equal(":xxx:yyy:zzz", ReadAll(root.Lookup("file4")!));
    }

    [Fact]
    public void Unionmount_HardLink_Symlink_CreatesSecondSymlinkEntry()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateFile(lowerSb.Root, lowerSb, "target", ":xxx:yyy:zzz");
        CreateSymlink(lowerSb.Root, lowerSb, "link", "target");

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        var source = Assert.IsType<OverlayInode>(root.Lookup("link")!.Inode);
        root.Link(new Dentry("link2", null, overlaySb.Root, overlaySb), source);

        var link = root.Lookup("link");
        var link2 = root.Lookup("link2");
        Assert.NotNull(link);
        Assert.NotNull(link2);
        Assert.Equal(InodeType.Symlink, link!.Inode!.Type);
        Assert.Equal(InodeType.Symlink, link2!.Inode!.Type);
        Assert.Equal(0, link.Inode.Readlink(out var linkTarget));
        Assert.Equal("target", linkTarget);
        Assert.Equal(0, link2.Inode.Readlink(out var link2Target));
        Assert.Equal("target", link2Target);
    }

    [Fact]
    public void Unionmount_RenameEmptyDir_BackAndForth_ThenRemove_Works()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateDirectory(lowerSb.Root, lowerSb, "empty");

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        root.Rename("empty", root, "empty2");
        Assert.Null(root.Lookup("empty"));
        Assert.NotNull(root.Lookup("empty2"));

        root.Rename("empty2", root, "empty");
        Assert.Null(root.Lookup("empty2"));
        Assert.NotNull(root.Lookup("empty"));

        root.Rmdir("empty");
        Assert.Null(root.Lookup("empty"));
    }

    [Fact]
    public void Unionmount_RenameEmptyDir_OverFile_FailsAndPreservesDirectory()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateDirectory(lowerSb.Root, lowerSb, "empty");
        CreateFile(lowerSb.Root, lowerSb, "file", ":xxx:yyy:zzz");
        CreateDirectory(lowerSb.Root, lowerSb, "populated");
        CreateFile(lowerSb.Root.Inode!.Lookup("populated")!, lowerSb, "a", "A");

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        Assert.True(root.Rename("empty", root, "file") < 0);
        Assert.True(root.Rename("empty", Assert.IsType<OverlayInode>(root.Lookup("populated")!.Inode), "a") < 0);

        Assert.NotNull(root.Lookup("empty"));
        Assert.Equal(":xxx:yyy:zzz", ReadAll(root.Lookup("file")!));
        Assert.Equal("A", ReadAll(root.Lookup("populated")!.Inode!.Lookup("a")!));
    }

    [Fact]
    public void Unionmount_RenameNewDirOverRemovedLowerDir_ReplacesWhiteoutedTree()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateDirectory(lowerSb.Root, lowerSb, "pop");
        var lowerPop = lowerSb.Root.Inode!.Lookup("pop")!;
        CreateFile(lowerPop, lowerSb, "b", "B");

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        var created = new Dentry("newdir", null, overlaySb.Root, overlaySb);
        Assert.Equal(0, root.Mkdir(created, 0x1ED, 0, 0));
        _ = Assert.IsType<OverlayInode>(created.Inode);
        CreateFile(created, overlaySb, "a", "A");

        var pop = Assert.IsType<OverlayInode>(root.Lookup("pop")!.Inode);
        pop.Unlink("b");
        root.Rmdir("pop");

        root.Rename("newdir", root, "pop");

        Assert.Null(root.Lookup("newdir"));
        var replaced = Assert.IsType<OverlayInode>(root.Lookup("pop")!.Inode);
        Assert.Equal("A", ReadAll(replaced.Lookup("a")!));
        Assert.Null(replaced.Lookup("b"));
    }

    [Fact]
    public void Unionmount_Readlink_RegularFile_FailsLikeUpstream()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateFile(lowerSb.Root, lowerSb, "file", ":xxx:yyy:zzz");

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        Assert.Equal(-(int)Errno.EINVAL, root.Lookup("file")!.Inode!.Readlink(out _));
    }

    [Fact]
    public void Unionmount_Readlink_BrokenSymlink_ReturnsTargetText()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateSymlink(lowerSb.Root, lowerSb, "dangling", "missing-target");

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        Assert.Equal(0, root.Lookup("dangling")!.Inode!.Readlink(out var danglingTarget));
        Assert.Equal("missing-target", danglingTarget);
    }

    [Fact]
    public void Unionmount_Readlink_DirectorySymlink_ReturnsTargetText()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateDirectory(lowerSb.Root, lowerSb, "dir");
        CreateSymlink(lowerSb.Root, lowerSb, "dir-link", "dir");

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        Assert.Equal(0, root.Lookup("dir-link")!.Inode!.Readlink(out var dirLinkTarget));
        Assert.Equal("dir", dirLinkTarget);
    }

    [Fact]
    public void Unionmount_Readlink_DirectSymlinkToFile_ReturnsTargetText()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateFile(lowerSb.Root, lowerSb, "target", ":xxx:yyy:zzz");
        CreateSymlink(lowerSb.Root, lowerSb, "link", "target");

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        Assert.Equal(0, root.Lookup("link")!.Inode!.Readlink(out var directLinkTarget));
        Assert.Equal("target", directLinkTarget);
    }

    [Fact]
    public void Unionmount_Readlink_IndirectSymlinkToFile_ReturnsIntermediate()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateFile(lowerSb.Root, lowerSb, "target", ":xxx:yyy:zzz");
        CreateSymlink(lowerSb.Root, lowerSb, "link", "target");
        CreateSymlink(lowerSb.Root, lowerSb, "chain", "link");

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        Assert.Equal(0, root.Lookup("chain")!.Inode!.Readlink(out var chainTarget));
        Assert.Equal("link", chainTarget);
    }

    [Fact]
    public void Unionmount_Readlink_IndirectSymlinkToDir_ReturnsIntermediate()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateDirectory(lowerSb.Root, lowerSb, "dir");
        CreateSymlink(lowerSb.Root, lowerSb, "dir-link", "dir");
        CreateSymlink(lowerSb.Root, lowerSb, "dir-chain", "dir-link");

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        Assert.Equal(0, root.Lookup("dir-chain")!.Inode!.Readlink(out var dirChainTarget));
        Assert.Equal("dir-link", dirChainTarget);
    }

    [Fact]
    public void Unionmount_Readlink_MissingFile_Unresolved()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        Assert.Null(root.Lookup("absent"));
    }

    [Fact]
    public void Unionmount_Rmtree_PopulatedLowerDir_RemovesWholeTree()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateDirectory(lowerSb.Root, lowerSb, "tree");
        var lowerTree = lowerSb.Root.Inode!.Lookup("tree")!;
        CreateFile(lowerTree, lowerSb, "a", "A");
        CreateDirectory(lowerTree, lowerSb, "sub");
        var lowerSub = lowerTree.Inode!.Lookup("sub")!;
        CreateFile(lowerSub, lowerSb, "b", "B");

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        RemoveTree(root, "tree");

        Assert.Null(root.Lookup("tree"));
    }

    [Fact]
    public void Unionmount_Rmtree_LowerDirWithNewSubdir_RemovesWholeTree()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateDirectory(lowerSb.Root, lowerSb, "tree");
        var lowerTree = lowerSb.Root.Inode!.Lookup("tree")!;
        CreateFile(lowerTree, lowerSb, "a", "A");

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);
        var tree = Assert.IsType<OverlayInode>(root.Lookup("tree")!.Inode);
        var newSubDentry = new Dentry("newsub", null, root.Lookup("tree"), overlaySb);
        Assert.Equal(0, tree.Mkdir(newSubDentry, 0x1ED, 0, 0));
        _ = Assert.IsType<OverlayInode>(newSubDentry.Inode);
        CreateFile(newSubDentry, overlaySb, "b", "B");

        RemoveTree(root, "tree");

        Assert.Null(root.Lookup("tree"));
    }

    [Fact]
    public void Unionmount_RenameNewEmptyDir_OverFile_FailsAndPreservesDirectory()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateFile(lowerSb.Root, lowerSb, "file", "target");

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        CreateDirectory(overlaySb.Root, overlaySb, "newdir");
        var newdir = root.Lookup("newdir")!;

        // Rename directory over file should fail
        Assert.True(root.Rename("newdir", root, "file") < 0);

        Assert.NotNull(root.Lookup("newdir"));
        Assert.Equal("target", ReadAll(root.Lookup("file")!));
    }

    [Fact]
    public void Unionmount_RenameNewPopulatedDir_OverOwnChildFile_FailsAndPreservesTree()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        CreateDirectory(overlaySb.Root, overlaySb, "newdir");
        var newdirDentry = root.Lookup("newdir")!;
        var newdirInode = Assert.IsType<OverlayInode>(newdirDentry.Inode);
        CreateFile(newdirDentry, overlaySb, "a", "AAAA");

        // Rename directory over its own child should fail
        Assert.True(root.Rename("newdir", newdirInode, "a") < 0);

        Assert.NotNull(root.Lookup("newdir"));
        Assert.Equal("AAAA", ReadAll(newdirInode.Lookup("a")!));
    }

    [Fact]
    public void Unionmount_RenameNewPopulatedDir_OverEmptiedLowerDir_MatchesUpstreamSemantics()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateDirectory(lowerSb.Root, lowerSb, "pop");
        var lowerPop = lowerSb.Root.Inode!.Lookup("pop")!;
        CreateFile(lowerPop, lowerSb, "b", ":aaa:bbb:ccc");

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);
        var pop = Assert.IsType<OverlayInode>(root.Lookup("pop")!.Inode);

        // Empty the lower dir on upper
        pop.Unlink("b");

        CreateDirectory(overlaySb.Root, overlaySb, "newdir");
        var newdirDentry = root.Lookup("newdir")!;
        CreateFile(newdirDentry, overlaySb, "a", "AAAA");

        // Rename new populated dir over emptied lower dir
        root.Rename("newdir", root, "pop");

        Assert.Null(root.Lookup("newdir"));
        var finalPop = Assert.IsType<OverlayInode>(root.Lookup("pop")!.Inode);
        Assert.Equal("AAAA", ReadAll(finalPop.Lookup("a")!));
        Assert.Null(finalPop.Lookup("b"));
    }

    [Fact]
    public void Unionmount_RenameMovePopulatedDir_IntoAnotherDir_MatchesUpstreamSemantics()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateDirectory(lowerSb.Root, lowerSb, "pop");
        var lowerPop = lowerSb.Root.Inode!.Lookup("pop")!;
        CreateFile(lowerPop, lowerSb, "a", "AAAA");
        CreateDirectory(lowerSb.Root, lowerSb, "empty");

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);
        var empty = Assert.IsType<OverlayInode>(root.Lookup("empty")!.Inode);

        // Move /pop into /empty/pop
        root.Rename("pop", empty, "pop");

        Assert.Null(root.Lookup("pop"));
        var finalPop2 = Assert.IsType<OverlayInode>(empty.Lookup("pop")!.Inode);
        Assert.Equal("AAAA", ReadAll(finalPop2.Lookup("a")!));
    }

    [Fact]
    public void Unionmount_RenameMoveDir_AndThenMoveItsSubdir_MatchesUpstreamSemantics()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateDirectory(lowerSb.Root, lowerSb, "tree");
        var lowerTree = lowerSb.Root.Inode!.Lookup("tree")!;
        CreateFile(lowerTree, lowerSb, "a", "AAAA");
        CreateDirectory(lowerTree, lowerSb, "pop");
        var lowerPop = lowerTree.Inode!.Lookup("pop")!;
        CreateFile(lowerPop, lowerSb, "b", "BBBB");

        CreateDirectory(lowerSb.Root, lowerSb, "target");

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);
        var target = Assert.IsType<OverlayInode>(root.Lookup("target")!.Inode);

        // Move /tree into /target/tree
        root.Rename("tree", target, "tree");
        var treeInTarget = Assert.IsType<OverlayInode>(target.Lookup("tree")!.Inode);

        // Move /target/tree/pop into /pop (back to root)
        treeInTarget.Rename("pop", root, "pop");

        Assert.Null(treeInTarget.Lookup("pop"));
        var popAtRoot = Assert.IsType<OverlayInode>(root.Lookup("pop")!.Inode);
        Assert.Equal("BBBB", ReadAll(popAtRoot.Lookup("b")!));
        Assert.Equal("AAAA", ReadAll(treeInTarget.Lookup("a")!));
    }

    [Fact]
    public void Unionmount_RenameMoveNewDirBranch_IntoLowerAncestor_MatchesUpstreamSemantics()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        CreateDirectory(lowerSb.Root, lowerSb, "parent");

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);
        var parent = Assert.IsType<OverlayInode>(root.Lookup("parent")!.Inode);

        // Create new branch: /parent/newp/new
        CreateDirectory(root.Lookup("parent")!, overlaySb, "newp");
        var newp = Assert.IsType<OverlayInode>(parent.Lookup("newp")!.Inode);
        CreateDirectory(parent.Lookup("newp")!, overlaySb, "new");

        // Move /parent/newp to /newp (root)
        parent.Rename("newp", root, "newp");

        Assert.Null(parent.Lookup("newp"));
        var finalNewp = Assert.IsType<OverlayInode>(root.Lookup("newp")!.Inode);
        Assert.NotNull(finalNewp.Lookup("new"));
    }

    private static OverlaySuperBlock CreateOverlay(SuperBlock lowerSb, SuperBlock upperSb)
    {
        var overlayFs = new OverlayFileSystem();
        return (OverlaySuperBlock)overlayFs.ReadSuper(
            new FileSystemType { Name = "overlay" },
            0,
            "overlay",
            new OverlayMountOptions { Lower = lowerSb, Upper = upperSb });
    }

    private static void CreateDirectory(Dentry parent, SuperBlock sb, string name)
    {
        var dentry = new Dentry(name, null, parent, sb);
        parent.Inode!.Mkdir(dentry, 0x1ED, 0, 0);
    }

    private static void CreateFile(Dentry parent, SuperBlock sb, string name, string content)
    {
        var dentry = new Dentry(name, null, parent, sb);
        parent.Inode!.Create(dentry, 0x1A4, 0, 0);
        var file = new LinuxFile(dentry, FileFlags.O_WRONLY, null!);
        try
        {
            var payload = Encoding.UTF8.GetBytes(content);
            Assert.Equal(payload.Length, dentry.Inode!.WriteFromHost(null, file, payload, 0));
        }
        finally
        {
            file.Close();
        }
    }

    private static void CreateSymlink(Dentry parent, SuperBlock sb, string name, string target)
    {
        var dentry = new Dentry(name, null, parent, sb);
        parent.Inode!.Symlink(dentry, target, 0, 0);
    }

    private static void RemoveTree(OverlayInode parent, string name)
    {
        var entry = parent.Lookup(name) ?? throw new InvalidOperationException($"Missing entry {name}");
        if (entry.Inode?.Type != InodeType.Directory)
        {
            parent.Unlink(name);
            return;
        }

        var dir = Assert.IsType<OverlayInode>(entry.Inode);
        foreach (var child in dir.GetEntries().Where(e => e.Name is not "." and not "..").ToList())
            RemoveTree(dir, child.Name);

        parent.Rmdir(name);
    }

    private static string ReadAll(Dentry dentry)
    {
        var file = new LinuxFile(dentry, FileFlags.O_RDONLY, null!);
        try
        {
            var buf = new byte[64];
            var read = dentry.Inode!.ReadToHost(null, file, buf, 0);
            return Encoding.UTF8.GetString(buf, 0, read);
        }
        finally
        {
            file.Close();
        }
    }

    private static int PositiveMod(int value, int modulus)
    {
        var result = value % modulus;
        return result < 0 ? result + modulus : result;
    }
}