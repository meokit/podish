using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.VFS;

public class FileSystemBehaviorTests
{
    public static TheoryData<string> MutableFileSystems => new()
    {
        "hostfs",
        "tmpfs",
        "overlayfs"
    };

    [Theory]
    [MemberData(nameof(MutableFileSystems))]
    public void Unlink_RemovesRegularAndSymlinkEntries(string fsName)
    {
        using var rig = FileSystemTestRigFactory.Create(fsName);
        var root = rig.Root;
        var rootInode = rig.RootInode;

        var regular = new Dentry("file.txt", null, root, rig.SuperBlock);
        rootInode.Create(regular, 0x1A4, 0, 0);
        Assert.NotNull(rootInode.Lookup("file.txt"));

        rootInode.Unlink("file.txt");
        Assert.Null(rootInode.Lookup("file.txt"));
        Assert.DoesNotContain(rootInode.GetEntries(), e => e.Name == "file.txt");

        var symlink = new Dentry("link.txt", null, root, rig.SuperBlock);
        rootInode.Symlink(symlink, "target.txt", 0, 0);
        Assert.NotNull(rootInode.Lookup("link.txt"));

        rootInode.Unlink("link.txt");
        Assert.Null(rootInode.Lookup("link.txt"));
        Assert.DoesNotContain(rootInode.GetEntries(), e => e.Name == "link.txt");
    }

    [Theory]
    [MemberData(nameof(MutableFileSystems))]
    public void Rmdir_RemovesDirectoryAndUpdatesParentEntries(string fsName)
    {
        using var rig = FileSystemTestRigFactory.Create(fsName);
        var root = rig.Root;
        var rootInode = rig.RootInode;

        var dir = new Dentry("dir", null, root, rig.SuperBlock);
        rootInode.Mkdir(dir, 0x1ED, 0, 0);
        Assert.NotNull(rootInode.Lookup("dir"));
        Assert.Contains(rootInode.GetEntries(), e => e.Name == "dir");

        var dirInode = Assert.IsAssignableFrom<Inode>(dir.Inode);
        var nested = new Dentry("nested", null, dir, rig.SuperBlock);
        dirInode.Mkdir(nested, 0x1ED, 0, 0);
        Assert.NotNull(dirInode.Lookup("nested"));

        dirInode.Rmdir("nested");
        Assert.Null(dirInode.Lookup("nested"));
        Assert.DoesNotContain(dirInode.GetEntries(), e => e.Name == "nested");

        rootInode.Rmdir("dir");
        Assert.Null(rootInode.Lookup("dir"));
        Assert.DoesNotContain(rootInode.GetEntries(), e => e.Name == "dir");
    }

    [Theory]
    [MemberData(nameof(MutableFileSystems))]
    public void DirectoryNlink_MkdirRmdirAndNestedMove_TracksExpectedDelta(string fsName)
    {
        using var rig = FileSystemTestRigFactory.Create(fsName);
        var root = rig.Root;
        var rootInode = rig.RootInode;
        var rootStart = rootInode.GetLinkCountForStat();

        var dir = new Dentry("dir", null, root, rig.SuperBlock);
        rootInode.Mkdir(dir, 0x1ED, 0, 0);
        var dirInode = Assert.IsAssignableFrom<Inode>(dir.Inode);

        Assert.Equal(rootStart + 1, rootInode.GetLinkCountForStat());
        Assert.Equal(2u, dirInode.GetLinkCountForStat());

        var nested = new Dentry("nested", null, dir, rig.SuperBlock);
        dirInode.Mkdir(nested, 0x1ED, 0, 0);
        var nestedInode = Assert.IsAssignableFrom<Inode>(nested.Inode);

        Assert.Equal(3u, dirInode.GetLinkCountForStat());
        Assert.Equal(2u, nestedInode.GetLinkCountForStat());

        dirInode.Rmdir("nested");
        Assert.Equal(2u, dirInode.GetLinkCountForStat());

        rootInode.Rmdir("dir");
        Assert.Equal(rootStart, rootInode.GetLinkCountForStat());
    }

    [Theory]
    [MemberData(nameof(MutableFileSystems))]
    public void Rename_FilePreservesInodeAndUpdatesEntries(string fsName)
    {
        using var rig = FileSystemTestRigFactory.Create(fsName);
        var root = rig.Root;
        var rootInode = rig.RootInode;

        var file = new Dentry("old.txt", null, root, rig.SuperBlock);
        rootInode.Create(file, 0x1A4, 0, 0);
        var oldIno = file.Inode!.Ino;

        rootInode.Rename("old.txt", rootInode, "new.txt");

        Assert.Null(rootInode.Lookup("old.txt"));
        var renamed = rootInode.Lookup("new.txt");
        Assert.NotNull(renamed);
        Assert.Equal(oldIno, renamed!.Inode!.Ino);
        Assert.DoesNotContain(rootInode.GetEntries(), e => e.Name == "old.txt");
        Assert.Contains(rootInode.GetEntries(), e => e.Name == "new.txt");
    }

    [Theory]
    [MemberData(nameof(MutableFileSystems))]
    public void Rename_DirectoryPreservesInodeAndUpdatesDescendantLookup(string fsName)
    {
        using var rig = FileSystemTestRigFactory.Create(fsName);
        var root = rig.Root;
        var rootInode = rig.RootInode;

        var dir = new Dentry("olddir", null, root, rig.SuperBlock);
        rootInode.Mkdir(dir, 0x1ED, 0, 0);
        var dirIno = dir.Inode!.Ino;

        var dirInode = Assert.IsAssignableFrom<Inode>(dir.Inode);
        var child = new Dentry("child.txt", null, dir, rig.SuperBlock);
        dirInode.Create(child, 0x1A4, 0, 0);

        rootInode.Rename("olddir", rootInode, "newdir");

        Assert.Null(rootInode.Lookup("olddir"));
        var renamedDir = rootInode.Lookup("newdir");
        Assert.NotNull(renamedDir);
        Assert.Equal(dirIno, renamedDir!.Inode!.Ino);

        var renamedDirInode = Assert.IsAssignableFrom<Inode>(renamedDir.Inode);
        Assert.NotNull(renamedDirInode.Lookup("child.txt"));
        Assert.DoesNotContain(rootInode.GetEntries(), e => e.Name == "olddir");
        Assert.Contains(rootInode.GetEntries(), e => e.Name == "newdir");
    }

    [Theory]
    [MemberData(nameof(MutableFileSystems))]
    public void Unlink_DirectoryThrows_AndPreservesDirectory(string fsName)
    {
        using var rig = FileSystemTestRigFactory.Create(fsName);
        var root = rig.Root;
        var rootInode = rig.RootInode;

        var dir = new Dentry("dir", null, root, rig.SuperBlock);
        rootInode.Mkdir(dir, 0x1ED, 0, 0);

        Assert.ThrowsAny<Exception>(() => rootInode.Unlink("dir"));
        Assert.NotNull(rootInode.Lookup("dir"));
        Assert.Contains(rootInode.GetEntries(), e => e.Name == "dir");
    }

    [Theory]
    [MemberData(nameof(MutableFileSystems))]
    public void Rmdir_NonEmptyDirectoryThrows_AndPreservesDirectory(string fsName)
    {
        using var rig = FileSystemTestRigFactory.Create(fsName);
        var root = rig.Root;
        var rootInode = rig.RootInode;

        var dir = new Dentry("dir", null, root, rig.SuperBlock);
        rootInode.Mkdir(dir, 0x1ED, 0, 0);
        var dirInode = Assert.IsAssignableFrom<Inode>(dir.Inode);
        var child = new Dentry("child.txt", null, dir, rig.SuperBlock);
        dirInode.Create(child, 0x1A4, 0, 0);

        Assert.ThrowsAny<Exception>(() => rootInode.Rmdir("dir"));
        Assert.NotNull(rootInode.Lookup("dir"));
        Assert.NotNull(dirInode.Lookup("child.txt"));
    }

    [Theory]
    [MemberData(nameof(MutableFileSystems))]
    public void Rename_OverwritesExistingFileAndPreservesSourceIdentity(string fsName)
    {
        using var rig = FileSystemTestRigFactory.Create(fsName);
        var root = rig.Root;
        var rootInode = rig.RootInode;

        var source = new Dentry("source.txt", null, root, rig.SuperBlock);
        rootInode.Create(source, 0x1A4, 0, 0);
        var sourceIno = source.Inode!.Ino;

        var target = new Dentry("target.txt", null, root, rig.SuperBlock);
        rootInode.Create(target, 0x1A4, 0, 0);
        Assert.NotEqual(sourceIno, target.Inode!.Ino);

        rootInode.Rename("source.txt", rootInode, "target.txt");

        Assert.Null(rootInode.Lookup("source.txt"));
        var renamed = rootInode.Lookup("target.txt");
        Assert.NotNull(renamed);
        Assert.Equal(sourceIno, renamed!.Inode!.Ino);
    }

    [Theory]
    [MemberData(nameof(MutableFileSystems))]
    public void GetEntries_ReflectsCreateRenameAndDelete(string fsName)
    {
        using var rig = FileSystemTestRigFactory.Create(fsName);
        var root = rig.Root;
        var rootInode = rig.RootInode;

        var file = new Dentry("alpha.txt", null, root, rig.SuperBlock);
        rootInode.Create(file, 0x1A4, 0, 0);
        Assert.Contains(rootInode.GetEntries(), e => e.Name == "alpha.txt");

        rootInode.Rename("alpha.txt", rootInode, "beta.txt");
        Assert.DoesNotContain(rootInode.GetEntries(), e => e.Name == "alpha.txt");
        Assert.Contains(rootInode.GetEntries(), e => e.Name == "beta.txt");

        rootInode.Unlink("beta.txt");
        Assert.DoesNotContain(rootInode.GetEntries(), e => e.Name == "beta.txt");
    }

    [Theory]
    [MemberData(nameof(MutableFileSystems))]
    public void Symlink_CreatesLinkAndReadlinkRoundTrips(string fsName)
    {
        using var rig = FileSystemTestRigFactory.Create(fsName);
        var root = rig.Root;
        var rootInode = rig.RootInode;

        var file = new Dentry("target.txt", null, root, rig.SuperBlock);
        rootInode.Create(file, 0x1A4, 0, 0);

        var link = new Dentry("link.txt", null, root, rig.SuperBlock);
        rootInode.Symlink(link, "target.txt", 0, 0);

        var looked = rootInode.Lookup("link.txt");
        Assert.NotNull(looked);
        Assert.Equal(InodeType.Symlink, looked!.Inode!.Type);
        Assert.Equal("target.txt", looked.Inode.Readlink());
        Assert.Contains(rootInode.GetEntries(), e => e.Name == "link.txt");
    }

    [Theory]
    [MemberData(nameof(MutableFileSystems))]
    public void Rename_SymlinkPreservesLinkIdentityAndTarget(string fsName)
    {
        using var rig = FileSystemTestRigFactory.Create(fsName);
        var root = rig.Root;
        var rootInode = rig.RootInode;

        var file = new Dentry("target.txt", null, root, rig.SuperBlock);
        rootInode.Create(file, 0x1A4, 0, 0);

        var link = new Dentry("link.txt", null, root, rig.SuperBlock);
        rootInode.Symlink(link, "target.txt", 0, 0);
        var linkIno = link.Inode!.Ino;

        rootInode.Rename("link.txt", rootInode, "renamed-link.txt");

        Assert.Null(rootInode.Lookup("link.txt"));
        var renamed = rootInode.Lookup("renamed-link.txt");
        Assert.NotNull(renamed);
        Assert.Equal(InodeType.Symlink, renamed!.Inode!.Type);
        Assert.Equal(linkIno, renamed.Inode.Ino);
        Assert.Equal("target.txt", renamed.Inode.Readlink());
    }

    [Theory]
    [MemberData(nameof(MutableFileSystems))]
    public void Rename_DirectoryIntoOwnDescendantThrows(string fsName)
    {
        using var rig = FileSystemTestRigFactory.Create(fsName);
        var root = rig.Root;
        var rootInode = rig.RootInode;

        var parent = new Dentry("parent", null, root, rig.SuperBlock);
        rootInode.Mkdir(parent, 0x1ED, 0, 0);
        var parentInode = Assert.IsAssignableFrom<Inode>(parent.Inode);

        var child = new Dentry("child", null, parent, rig.SuperBlock);
        parentInode.Mkdir(child, 0x1ED, 0, 0);
        var parentIno = parent.Inode!.Ino;

        Assert.ThrowsAny<Exception>(() => rootInode.Rename("parent", parentInode, "moved"));

        var parentAfter = rootInode.Lookup("parent");
        Assert.NotNull(parentAfter);
        Assert.Equal(parentIno, parentAfter!.Inode!.Ino);
        Assert.NotNull(parentAfter.Inode.Lookup("child"));
        Assert.Null(rootInode.Lookup("moved"));
    }

    [Theory]
    [MemberData(nameof(MutableFileSystems))]
    public void Rename_DirectoryOverEmptyDirectory_ReplacesTarget(string fsName)
    {
        using var rig = FileSystemTestRigFactory.Create(fsName);
        var root = rig.Root;
        var rootInode = rig.RootInode;

        var source = new Dentry("source", null, root, rig.SuperBlock);
        rootInode.Mkdir(source, 0x1ED, 0, 0);
        var sourceInode = Assert.IsAssignableFrom<Inode>(source.Inode);
        var sourceChild = new Dentry("child.txt", null, source, rig.SuperBlock);
        sourceInode.Create(sourceChild, 0x1A4, 0, 0);
        var sourceIno = source.Inode!.Ino;

        var target = new Dentry("target", null, root, rig.SuperBlock);
        rootInode.Mkdir(target, 0x1ED, 0, 0);
        Assert.NotNull(rootInode.Lookup("target"));

        rootInode.Rename("source", rootInode, "target");

        Assert.Null(rootInode.Lookup("source"));
        var replaced = rootInode.Lookup("target");
        Assert.NotNull(replaced);
        Assert.Equal(sourceIno, replaced!.Inode!.Ino);
        Assert.NotNull(replaced.Inode.Lookup("child.txt"));
    }

    [Theory]
    [MemberData(nameof(MutableFileSystems))]
    public void Rename_DirectoryOverNonEmptyDirectory_ThrowsAndPreservesBoth(string fsName)
    {
        using var rig = FileSystemTestRigFactory.Create(fsName);
        var root = rig.Root;
        var rootInode = rig.RootInode;

        var source = new Dentry("source", null, root, rig.SuperBlock);
        rootInode.Mkdir(source, 0x1ED, 0, 0);
        var sourceInode = Assert.IsAssignableFrom<Inode>(source.Inode);
        var sourceChild = new Dentry("source-child.txt", null, source, rig.SuperBlock);
        sourceInode.Create(sourceChild, 0x1A4, 0, 0);
        var sourceIno = source.Inode!.Ino;

        var target = new Dentry("target", null, root, rig.SuperBlock);
        rootInode.Mkdir(target, 0x1ED, 0, 0);
        var targetInode = Assert.IsAssignableFrom<Inode>(target.Inode);
        var targetChild = new Dentry("target-child.txt", null, target, rig.SuperBlock);
        targetInode.Create(targetChild, 0x1A4, 0, 0);

        Assert.ThrowsAny<Exception>(() => rootInode.Rename("source", rootInode, "target"));

        var sourceAfter = rootInode.Lookup("source");
        var targetAfter = rootInode.Lookup("target");
        Assert.NotNull(sourceAfter);
        Assert.NotNull(targetAfter);
        Assert.Equal(sourceIno, sourceAfter!.Inode!.Ino);
        Assert.NotNull(sourceAfter.Inode.Lookup("source-child.txt"));
        Assert.NotNull(targetAfter!.Inode!.Lookup("target-child.txt"));
    }

    [Theory]
    [MemberData(nameof(MutableFileSystems))]
    public void Link_CreatesSecondNameSharingInode_AndUnlinkPreservesPeer(string fsName)
    {
        using var rig = FileSystemTestRigFactory.Create(fsName);
        var root = rig.Root;
        var rootInode = rig.RootInode;

        var source = new Dentry("source.txt", null, root, rig.SuperBlock);
        rootInode.Create(source, 0x1A4, 0, 0);
        var sourceIno = source.Inode!.Ino;

        var linked = new Dentry("linked.txt", null, root, rig.SuperBlock);
        rootInode.Link(linked, source.Inode);

        var sourceLookup = rootInode.Lookup("source.txt");
        var linkedLookup = rootInode.Lookup("linked.txt");
        Assert.NotNull(sourceLookup);
        Assert.NotNull(linkedLookup);
        Assert.Equal(sourceIno, sourceLookup!.Inode!.Ino);
        Assert.Equal(sourceIno, linkedLookup!.Inode!.Ino);

        rootInode.Unlink("source.txt");

        Assert.Null(rootInode.Lookup("source.txt"));
        var linkedAfter = rootInode.Lookup("linked.txt");
        Assert.NotNull(linkedAfter);
        Assert.Equal(sourceIno, linkedAfter!.Inode!.Ino);
    }

    [Theory]
    [MemberData(nameof(MutableFileSystems))]
    public void Rename_FileAcrossParentsPreservesInodeAndMovesEntry(string fsName)
    {
        using var rig = FileSystemTestRigFactory.Create(fsName);
        var root = rig.Root;
        var rootInode = rig.RootInode;

        var fromDir = new Dentry("from", null, root, rig.SuperBlock);
        rootInode.Mkdir(fromDir, 0x1ED, 0, 0);
        var toDir = new Dentry("to", null, root, rig.SuperBlock);
        rootInode.Mkdir(toDir, 0x1ED, 0, 0);

        var fromInode = Assert.IsAssignableFrom<Inode>(fromDir.Inode);
        var toInode = Assert.IsAssignableFrom<Inode>(toDir.Inode);

        var file = new Dentry("payload.txt", null, fromDir, rig.SuperBlock);
        fromInode.Create(file, 0x1A4, 0, 0);
        var fileIno = file.Inode!.Ino;

        fromInode.Rename("payload.txt", toInode, "moved.txt");

        Assert.Null(fromInode.Lookup("payload.txt"));
        var moved = toInode.Lookup("moved.txt");
        Assert.NotNull(moved);
        Assert.Equal(fileIno, moved!.Inode!.Ino);
        Assert.DoesNotContain(fromInode.GetEntries(), e => e.Name == "payload.txt");
        Assert.Contains(toInode.GetEntries(), e => e.Name == "moved.txt");
    }
}
