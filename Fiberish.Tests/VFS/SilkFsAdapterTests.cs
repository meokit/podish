using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.SilkFS;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Fiberish.Native;
using System.Linq;
using Xunit;

namespace Fiberish.Tests.VFS;

public class SilkFsAdapterTests
{
    [Fact]
    public void Silkfs_IsRegistered_AndCanAttachMount()
    {
        var silkRoot = Path.Combine(Path.GetTempPath(), $"silkfs-store-{Guid.NewGuid():N}");

        using var engine = new Engine();
        var vma = new VMAManager();
        var sm = new SyscallManager(engine, vma, 0);
        var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
        var rootSb = tmpfsType.CreateFileSystem().ReadSuper(tmpfsType, 0, "test-root", null);
        var rootMount = new Mount(rootSb, rootSb.Root)
        {
            Source = "tmpfs",
            FsType = "tmpfs",
            Options = "rw"
        };
        sm.InitializeRoot(rootSb.Root, rootMount);

        try
        {
            Assert.NotNull(FileSystemRegistry.Get("silkfs"));

            var root = sm.Root.Dentry!;
            if (root.Inode!.Lookup("mnt") == null)
            {
                var mntDentry = new Dentry("mnt", null, root, root.SuperBlock);
                root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
                root.Children["mnt"] = mntDentry;
            }

            var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
            var rc = sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount);
            Assert.Equal(0, rc);
            Assert.NotNull(mount);

            var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
            Assert.True(target.IsValid);
            Assert.Equal(0, sm.AttachDetachedMount(mount!, target));

            var loc = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
            Assert.True(loc.IsValid);
            Assert.Equal("silkfs", loc.Mount!.FsType);

            Assert.True(Directory.Exists(Path.Combine(silkRoot, "objects")));
            Assert.True(File.Exists(Path.Combine(silkRoot, "metadata.sqlite3")));

            var file = new Dentry("hello.txt", null, loc.Dentry, loc.Dentry!.SuperBlock);
            loc.Dentry.Inode!.Create(file, 0x1A4, 0, 0);
            var setRc = file.Inode!.SetXAttr("user.mime_type", "text/plain"u8.ToArray(), 0);
            Assert.Equal(0, setRc);

            var repo = new SilkRepository(SilkFsOptions.FromSource(silkRoot));
            repo.Initialize();
            var childIno = repo.Metadata.LookupDentry(SilkMetadataStore.RootInode, "hello.txt");
            Assert.NotNull(childIno);
            var x = repo.Metadata.GetXAttr(childIno!.Value, "user.mime_type");
            Assert.NotNull(x);

            var wh = new Dentry(".wh.ghost.txt", null, loc.Dentry, loc.Dentry!.SuperBlock);
            loc.Dentry.Inode!.Mknod(wh, 0x180, 0, 0, InodeType.CharDev, 0);
            Assert.True(repo.Metadata.HasWhiteout(SilkMetadataStore.RootInode, "ghost.txt"));
        }
        finally
        {
            sm.Close();
            if (Directory.Exists(silkRoot)) Directory.Delete(silkRoot, true);
        }
    }

    [Fact]
    public void Silkfs_Remount_RestoresFileXattrAndWhiteoutFromMetadata()
    {
        var silkRoot = Path.Combine(Path.GetTempPath(), $"silkfs-store-{Guid.NewGuid():N}");

        try
        {
            using (var engine = new Engine())
            {
                var vma = new VMAManager();
                var sm = new SyscallManager(engine, vma, 0);
                var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
                var rootSb = tmpfsType.CreateFileSystem().ReadSuper(tmpfsType, 0, "test-root", null);
                var rootMount = new Mount(rootSb, rootSb.Root)
                {
                    Source = "tmpfs",
                    FsType = "tmpfs",
                    Options = "rw"
                };
                sm.InitializeRoot(rootSb.Root, rootMount);

                var root = sm.Root.Dentry!;
                if (root.Inode!.Lookup("mnt") == null)
                {
                    var mntDentry = new Dentry("mnt", null, root, root.SuperBlock);
                    root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
                    root.Children["mnt"] = mntDentry;
                }

                var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
                Assert.Equal(0, sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
                var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
                Assert.Equal(0, sm.AttachDetachedMount(mount!, target));
                var loc = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);

                var file = new Dentry("keep.txt", null, loc.Dentry, loc.Dentry!.SuperBlock);
                loc.Dentry.Inode!.Create(file, 0x1A4, 0, 0);
                var wf = new LinuxFile(file, FileFlags.O_WRONLY, loc.Mount!);
                var payload = System.Text.Encoding.UTF8.GetBytes("hello-silk");
                var wrote = file.Inode!.Write(wf, payload, 0);
                Assert.Equal(payload.Length, wrote);
                wf.Close();
                Assert.Equal(0, file.Inode!.SetXAttr("user.test", "value"u8.ToArray(), 0));
                var wh = new Dentry(".wh.gone.txt", null, loc.Dentry, loc.Dentry.SuperBlock);
                loc.Dentry.Inode.Mknod(wh, 0x180, 0, 0, InodeType.CharDev, 0);
                sm.Close();
            }

            using (var engine = new Engine())
            {
                var vma = new VMAManager();
                var sm = new SyscallManager(engine, vma, 0);
                var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
                var rootSb = tmpfsType.CreateFileSystem().ReadSuper(tmpfsType, 0, "test-root", null);
                var rootMount = new Mount(rootSb, rootSb.Root)
                {
                    Source = "tmpfs",
                    FsType = "tmpfs",
                    Options = "rw"
                };
                sm.InitializeRoot(rootSb.Root, rootMount);

                var root = sm.Root.Dentry!;
                if (root.Inode!.Lookup("mnt") == null)
                {
                    var mntDentry = new Dentry("mnt", null, root, root.SuperBlock);
                    root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
                    root.Children["mnt"] = mntDentry;
                }

                var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
                Assert.Equal(0, sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
                var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
                Assert.Equal(0, sm.AttachDetachedMount(mount!, target));

                var fileLoc = sm.PathWalkWithFlags("/mnt/keep.txt", LookupFlags.FollowSymlink);
                Assert.True(fileLoc.IsValid);
                var rf = new LinuxFile(fileLoc.Dentry!, FileFlags.O_RDONLY, fileLoc.Mount!);
                var readBuf = new byte[64];
                var n = fileLoc.Dentry!.Inode!.Read(rf, readBuf, 0);
                rf.Close();
                Assert.Equal(10, n);
                Assert.Equal("hello-silk", System.Text.Encoding.UTF8.GetString(readBuf, 0, n));
                var buf = new byte[16];
                var xrc = fileLoc.Dentry!.Inode!.GetXAttr("user.test", buf);
                Assert.Equal(5, xrc);
                Assert.Equal("value", System.Text.Encoding.UTF8.GetString(buf, 0, xrc));

                var whLoc = sm.PathWalkWithFlags("/mnt/.wh.gone.txt", LookupFlags.FollowSymlink);
                Assert.True(whLoc.IsValid);
                Assert.Equal(InodeType.CharDev, whLoc.Dentry!.Inode!.Type);
                Assert.Equal((uint)0, whLoc.Dentry.Inode.Rdev);

                var repo = new SilkRepository(SilkFsOptions.FromSource(silkRoot));
                repo.Initialize();
                Assert.True(repo.Metadata.HasWhiteout(SilkMetadataStore.RootInode, "gone.txt"));
                sm.Close();
            }
        }
        finally
        {
            if (Directory.Exists(silkRoot)) Directory.Delete(silkRoot, true);
        }
    }

    [Fact]
    public void Silkfs_Remount_PreservesDirectoryNlinkAfterCrossParentRename()
    {
        var silkRoot = Path.Combine(Path.GetTempPath(), $"silkfs-store-{Guid.NewGuid():N}");

        try
        {
            using (var engine = new Engine())
            {
                var vma = new VMAManager();
                var sm = new SyscallManager(engine, vma, 0);
                var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
                var rootSb = tmpfsType.CreateFileSystem().ReadSuper(tmpfsType, 0, "test-root", null);
                var rootMount = new Mount(rootSb, rootSb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
                sm.InitializeRoot(rootSb.Root, rootMount);

                var root = sm.Root.Dentry!;
                if (root.Inode!.Lookup("mnt") == null)
                {
                    var mntDentry = new Dentry("mnt", null, root, root.SuperBlock);
                    root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
                    root.Children["mnt"] = mntDentry;
                }

                var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
                Assert.Equal(0, sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
                var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
                Assert.Equal(0, sm.AttachDetachedMount(mount!, target));

                var loc = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
                var mntInode = loc.Dentry!.Inode!;

                var from = new Dentry("from", null, loc.Dentry, loc.Dentry.SuperBlock);
                mntInode.Mkdir(from, 0x1ED, 0, 0);
                var fromInode = Assert.IsAssignableFrom<Inode>(from.Inode);

                var to = new Dentry("to", null, loc.Dentry, loc.Dentry.SuperBlock);
                mntInode.Mkdir(to, 0x1ED, 0, 0);
                var toInode = Assert.IsAssignableFrom<Inode>(to.Inode);

                var child = new Dentry("child", null, from, from.SuperBlock);
                fromInode.Mkdir(child, 0x1ED, 0, 0);
                Assert.Equal(3, fromInode.LinkCount);
                Assert.Equal(2, toInode.LinkCount);

                fromInode.Rename("child", toInode, "moved");
                Assert.Equal(2, fromInode.LinkCount);
                Assert.Equal(3, toInode.LinkCount);

                sm.Close();
            }

            using (var engine = new Engine())
            {
                var vma = new VMAManager();
                var sm = new SyscallManager(engine, vma, 0);
                var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
                var rootSb = tmpfsType.CreateFileSystem().ReadSuper(tmpfsType, 0, "test-root", null);
                var rootMount = new Mount(rootSb, rootSb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
                sm.InitializeRoot(rootSb.Root, rootMount);

                var root = sm.Root.Dentry!;
                if (root.Inode!.Lookup("mnt") == null)
                {
                    var mntDentry = new Dentry("mnt", null, root, root.SuperBlock);
                    root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
                    root.Children["mnt"] = mntDentry;
                }

                var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
                Assert.Equal(0, sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
                var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
                Assert.Equal(0, sm.AttachDetachedMount(mount!, target));

                var fromLoc = sm.PathWalkWithFlags("/mnt/from", LookupFlags.FollowSymlink);
                var toLoc = sm.PathWalkWithFlags("/mnt/to", LookupFlags.FollowSymlink);
                Assert.True(fromLoc.IsValid);
                Assert.True(toLoc.IsValid);
                Assert.Equal(2, fromLoc.Dentry!.Inode!.LinkCount);
                Assert.Equal(3, toLoc.Dentry!.Inode!.LinkCount);

                var repo = new SilkRepository(SilkFsOptions.FromSource(silkRoot));
                repo.Initialize();
                var fromIno = repo.Metadata.LookupDentry(SilkMetadataStore.RootInode, "from");
                var toIno = repo.Metadata.LookupDentry(SilkMetadataStore.RootInode, "to");
                Assert.NotNull(fromIno);
                Assert.NotNull(toIno);

                var fromRec = repo.Metadata.GetInode(fromIno!.Value);
                var toRec = repo.Metadata.GetInode(toIno!.Value);
                Assert.NotNull(fromRec);
                Assert.NotNull(toRec);
                Assert.Equal(2, fromRec!.Value.Nlink);
                Assert.Equal(3, toRec!.Value.Nlink);

                sm.Close();
            }
        }
        finally
        {
            if (Directory.Exists(silkRoot)) Directory.Delete(silkRoot, true);
        }
    }

    [Fact]
    public void Silkfs_ObjectRefCount_CleansUnusedObjectsOnRewriteAndUnlink()
    {
        var silkRoot = Path.Combine(Path.GetTempPath(), $"silkfs-store-{Guid.NewGuid():N}");
        try
        {
            using var engine = new Engine();
            var vma = new VMAManager();
            var sm = new SyscallManager(engine, vma, 0);
            var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
            var rootSb = tmpfsType.CreateFileSystem().ReadSuper(tmpfsType, 0, "test-root", null);
            var rootMount = new Mount(rootSb, rootSb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
            sm.InitializeRoot(rootSb.Root, rootMount);

            var root = sm.Root.Dentry!;
            if (root.Inode!.Lookup("mnt") == null)
            {
                var mntDentry = new Dentry("mnt", null, root, root.SuperBlock);
                root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
                root.Children["mnt"] = mntDentry;
            }

            var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
            Assert.Equal(0, sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
            var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
            Assert.Equal(0, sm.AttachDetachedMount(mount!, target));
            var loc = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);

            var file = new Dentry("obj.txt", null, loc.Dentry, loc.Dentry!.SuperBlock);
            loc.Dentry.Inode!.Create(file, 0x1A4, 0, 0);

            var wf = new LinuxFile(file, FileFlags.O_WRONLY, loc.Mount!);
            Assert.Equal(1, file.Inode!.Write(wf, "A"u8.ToArray(), 0));
            wf.Close();

            var repo = new SilkRepository(SilkFsOptions.FromSource(silkRoot));
            repo.Initialize();
            var ino = repo.Metadata.LookupDentry(SilkMetadataStore.RootInode, "obj.txt");
            Assert.NotNull(ino);
            var obj1 = repo.Metadata.GetInodeObject(ino!.Value);
            Assert.NotNull(obj1);
            Assert.Equal(1, repo.Metadata.GetObjectRefCount(obj1!));

            wf = new LinuxFile(file, FileFlags.O_WRONLY, loc.Mount!);
            Assert.Equal(1, file.Inode.Write(wf, "B"u8.ToArray(), 0));
            wf.Close();

            var obj2 = repo.Metadata.GetInodeObject(ino.Value);
            Assert.NotNull(obj2);
            Assert.Equal(1, repo.Metadata.GetObjectRefCount(obj2!));
            Assert.Equal(0, repo.Metadata.GetObjectRefCount(obj1!));

            loc.Dentry.Inode!.Unlink("obj.txt");
            Assert.Equal(0, repo.Metadata.GetObjectRefCount(obj2));

            var objectFiles = Directory.EnumerateFiles(Path.Combine(silkRoot, "objects"), "*",
                SearchOption.AllDirectories).ToArray();
            Assert.Empty(objectFiles);

            sm.Close();
        }
        finally
        {
            if (Directory.Exists(silkRoot)) Directory.Delete(silkRoot, true);
        }
    }

    [Fact]
    public void Silkfs_UnlinkClosedFile_RemovesInodeMetadata()
    {
        var silkRoot = Path.Combine(Path.GetTempPath(), $"silkfs-unlink-clean-{Guid.NewGuid():N}");
        try
        {
            using var engine = new Engine();
            var vma = new VMAManager();
            var sm = new SyscallManager(engine, vma, 0);
            var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
            var rootSb = tmpfsType.CreateFileSystem().ReadSuper(tmpfsType, 0, "test-root", null);
            var rootMount = new Mount(rootSb, rootSb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
            sm.InitializeRoot(rootSb.Root, rootMount);

            var root = sm.Root.Dentry!;
            if (root.Inode!.Lookup("mnt") == null)
            {
                var mntDentry = new Dentry("mnt", null, root, root.SuperBlock);
                root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
                root.Children["mnt"] = mntDentry;
            }

            var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
            Assert.Equal(0, sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
            var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
            Assert.Equal(0, sm.AttachDetachedMount(mount!, target));
            var loc = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);

            var file = new Dentry("gone.txt", null, loc.Dentry, loc.Dentry!.SuperBlock);
            loc.Dentry.Inode!.Create(file, 0x1A4, 0, 0);
            var wf = new LinuxFile(file, FileFlags.O_WRONLY, loc.Mount!);
            Assert.Equal(4, file.Inode!.Write(wf, "data"u8.ToArray(), 0));
            wf.Close();

            var repo = new SilkRepository(SilkFsOptions.FromSource(silkRoot));
            repo.Initialize();
            var ino = repo.Metadata.LookupDentry(SilkMetadataStore.RootInode, "gone.txt");
            Assert.NotNull(ino);

            loc.Dentry.Inode.Unlink("gone.txt");

            Assert.Null(repo.Metadata.LookupDentry(SilkMetadataStore.RootInode, "gone.txt"));
            Assert.Null(repo.Metadata.GetInode(ino!.Value));
            Assert.Null(repo.Metadata.GetInodeObject(ino.Value));
            sm.Close();
        }
        finally
        {
            if (Directory.Exists(silkRoot)) Directory.Delete(silkRoot, true);
        }
    }

    [Fact]
    public void Silkfs_RenameOverwrite_RemovesOverwrittenInodeMetadata()
    {
        var silkRoot = Path.Combine(Path.GetTempPath(), $"silkfs-rename-overwrite-{Guid.NewGuid():N}");
        try
        {
            using var engine = new Engine();
            var vma = new VMAManager();
            var sm = new SyscallManager(engine, vma, 0);
            var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
            var rootSb = tmpfsType.CreateFileSystem().ReadSuper(tmpfsType, 0, "test-root", null);
            var rootMount = new Mount(rootSb, rootSb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
            sm.InitializeRoot(rootSb.Root, rootMount);

            var root = sm.Root.Dentry!;
            if (root.Inode!.Lookup("mnt") == null)
            {
                var mntDentry = new Dentry("mnt", null, root, root.SuperBlock);
                root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
                root.Children["mnt"] = mntDentry;
            }

            var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
            Assert.Equal(0, sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
            var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
            Assert.Equal(0, sm.AttachDetachedMount(mount!, target));
            var loc = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);

            var a = new Dentry("a.txt", null, loc.Dentry, loc.Dentry!.SuperBlock);
            var b = new Dentry("b.txt", null, loc.Dentry, loc.Dentry.SuperBlock);
            loc.Dentry.Inode!.Create(a, 0x1A4, 0, 0);
            loc.Dentry.Inode.Create(b, 0x1A4, 0, 0);

            var wa = new LinuxFile(a, FileFlags.O_WRONLY, loc.Mount!);
            Assert.Equal(1, a.Inode!.Write(wa, "A"u8.ToArray(), 0));
            wa.Close();
            var wb = new LinuxFile(b, FileFlags.O_WRONLY, loc.Mount!);
            Assert.Equal(1, b.Inode!.Write(wb, "B"u8.ToArray(), 0));
            wb.Close();

            var repo = new SilkRepository(SilkFsOptions.FromSource(silkRoot));
            repo.Initialize();
            var bIno = repo.Metadata.LookupDentry(SilkMetadataStore.RootInode, "b.txt");
            Assert.NotNull(bIno);

            loc.Dentry.Inode.Rename("a.txt", loc.Dentry.Inode, "b.txt");

            Assert.Null(repo.Metadata.LookupDentry(SilkMetadataStore.RootInode, "a.txt"));
            var newBIno = repo.Metadata.LookupDentry(SilkMetadataStore.RootInode, "b.txt");
            Assert.NotNull(newBIno);
            Assert.NotEqual(bIno, newBIno);
            Assert.Null(repo.Metadata.GetInode(bIno!.Value));
            sm.Close();
        }
        finally
        {
            if (Directory.Exists(silkRoot)) Directory.Delete(silkRoot, true);
        }
    }

    [Fact]
    public void Silkfs_UnlinkOpenFile_DefersObjectDeletionUntilClose()
    {
        var silkRoot = Path.Combine(Path.GetTempPath(), $"silkfs-orphan-open-{Guid.NewGuid():N}");
        try
        {
            using var engine = new Engine();
            var mm = new VMAManager();
            var sm = new SyscallManager(engine, mm, 0);
            var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
            var rootSb = tmpfsType.CreateFileSystem().ReadSuper(tmpfsType, 0, "test-root", null);
            var rootMount = new Mount(rootSb, rootSb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
            sm.InitializeRoot(rootSb.Root, rootMount);

            var root = sm.Root.Dentry!;
            if (root.Inode!.Lookup("mnt") == null)
            {
                var mntDentry = new Dentry("mnt", null, root, root.SuperBlock);
                root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
                root.Children["mnt"] = mntDentry;
            }

            var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
            Assert.Equal(0, sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
            var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
            Assert.Equal(0, sm.AttachDetachedMount(mount!, target));
            var loc = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);

            var file = new Dentry("held.txt", null, loc.Dentry, loc.Dentry!.SuperBlock);
            loc.Dentry.Inode!.Create(file, 0x1A4, 0, 0);
            var wf = new LinuxFile(file, FileFlags.O_WRONLY, loc.Mount!);
            Assert.Equal(5, file.Inode!.Write(wf, "hello"u8.ToArray(), 0));
            wf.Close();

            var repo = new SilkRepository(SilkFsOptions.FromSource(silkRoot));
            repo.Initialize();
            var ino = repo.Metadata.LookupDentry(SilkMetadataStore.RootInode, "held.txt");
            Assert.NotNull(ino);
            var obj = repo.Metadata.GetInodeObject(ino!.Value);
            Assert.False(string.IsNullOrEmpty(obj));
            Assert.Equal(1, repo.Metadata.GetObjectRefCount(obj!));

            var rf = new LinuxFile(file, FileFlags.O_RDONLY, loc.Mount!);
            loc.Dentry.Inode!.Unlink("held.txt");

            Assert.Equal(1, repo.Metadata.GetObjectRefCount(obj!));
            Assert.NotNull(repo.ReadObject(obj));
            var readBuf = new byte[16];
            var n = rf.OpenedInode!.Read(rf, readBuf, 0);
            Assert.Equal(5, n);
            Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(readBuf, 0, n));

            rf.Close();

            Assert.Equal(0, repo.Metadata.GetObjectRefCount(obj));
            Assert.Null(repo.ReadObject(obj));
            sm.Close();
        }
        finally
        {
            if (Directory.Exists(silkRoot)) Directory.Delete(silkRoot, true);
        }
    }

    [Fact]
    public void Silkfs_UnlinkMmapHold_DefersObjectDeletionUntilMunmap()
    {
        var silkRoot = Path.Combine(Path.GetTempPath(), $"silkfs-orphan-mmap-{Guid.NewGuid():N}");
        try
        {
            using var engine = new Engine();
            var mm = new VMAManager();
            var sm = new SyscallManager(engine, mm, 0);
            var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
            var rootSb = tmpfsType.CreateFileSystem().ReadSuper(tmpfsType, 0, "test-root", null);
            var rootMount = new Mount(rootSb, rootSb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
            sm.InitializeRoot(rootSb.Root, rootMount);

            var root = sm.Root.Dentry!;
            if (root.Inode!.Lookup("mnt") == null)
            {
                var mntDentry = new Dentry("mnt", null, root, root.SuperBlock);
                root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
                root.Children["mnt"] = mntDentry;
            }

            var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
            Assert.Equal(0, sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
            var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
            Assert.Equal(0, sm.AttachDetachedMount(mount!, target));
            var loc = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);

            var file = new Dentry("mapheld.txt", null, loc.Dentry, loc.Dentry!.SuperBlock);
            loc.Dentry.Inode!.Create(file, 0x1A4, 0, 0);
            var wf = new LinuxFile(file, FileFlags.O_WRONLY, loc.Mount!);
            Assert.Equal(5, file.Inode!.Write(wf, "hello"u8.ToArray(), 0));
            wf.Close();

            var repo = new SilkRepository(SilkFsOptions.FromSource(silkRoot));
            repo.Initialize();
            var ino = repo.Metadata.LookupDentry(SilkMetadataStore.RootInode, "mapheld.txt");
            Assert.NotNull(ino);
            var obj = repo.Metadata.GetInodeObject(ino!.Value);
            Assert.False(string.IsNullOrEmpty(obj));
            Assert.Equal(1, repo.Metadata.GetObjectRefCount(obj!));

            var mappedFile = new LinuxFile(file, FileFlags.O_RDWR, loc.Mount!, LinuxFile.ReferenceKind.MmapHold);
            const uint mapAddr = 0x58000000;
            mm.Mmap(
                mapAddr,
                LinuxConstants.PageSize,
                Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed,
                mappedFile,
                0,
                (long)file.Inode!.Size,
                "MAP_SHARED",
                engine);

            loc.Dentry.Inode!.Unlink("mapheld.txt");
            Assert.Equal(1, repo.Metadata.GetObjectRefCount(obj));
            Assert.NotNull(repo.ReadObject(obj));

            mm.Munmap(mapAddr, LinuxConstants.PageSize, engine);

            Assert.Equal(0, repo.Metadata.GetObjectRefCount(obj));
            Assert.Null(repo.ReadObject(obj));
            sm.Close();
        }
        finally
        {
            if (Directory.Exists(silkRoot)) Directory.Delete(silkRoot, true);
        }
    }

    [Fact]
    public void Silkfs_MapSharedWriteback_PersistsAcrossRemount()
    {
        var silkRoot = Path.Combine(Path.GetTempPath(), $"silkfs-mmap-writeback-{Guid.NewGuid():N}");

        try
        {
            using (var engine = new Engine())
            {
                var mm = new VMAManager();
                var sm = new SyscallManager(engine, mm, 0);
                var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
                var rootSb = tmpfsType.CreateFileSystem().ReadSuper(tmpfsType, 0, "test-root", null);
                var rootMount = new Mount(rootSb, rootSb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
                sm.InitializeRoot(rootSb.Root, rootMount);

                var root = sm.Root.Dentry!;
                if (root.Inode!.Lookup("mnt") == null)
                {
                    var mntDentry = new Dentry("mnt", null, root, root.SuperBlock);
                    root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
                    root.Children["mnt"] = mntDentry;
                }

                var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
                Assert.Equal(0, sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
                var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
                Assert.Equal(0, sm.AttachDetachedMount(mount!, target));
                var loc = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);

                var file = new Dentry("map.txt", null, loc.Dentry, loc.Dentry!.SuperBlock);
                loc.Dentry.Inode!.Create(file, 0x1A4, 0, 0);
                var wf = new LinuxFile(file, FileFlags.O_WRONLY, loc.Mount!);
                var payload = "hello"u8.ToArray();
                Assert.Equal(payload.Length, file.Inode!.Write(wf, payload, 0));
                wf.Close();

                var fileLoc = sm.PathWalkWithFlags("/mnt/map.txt", LookupFlags.FollowSymlink);
                Assert.True(fileLoc.IsValid);
                var mappedFile = new LinuxFile(fileLoc.Dentry!, FileFlags.O_RDWR, fileLoc.Mount!);
                const uint mapAddr = 0x4C000000;
                mm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                    MapFlags.Shared | MapFlags.Fixed, mappedFile, 0, (long)mappedFile.Dentry.Inode!.Size, "MAP_SHARED", engine);
                Assert.True(mm.HandleFault(mapAddr, true, engine));
                Assert.True(engine.CopyToUser(mapAddr + 1, "ZZ"u8.ToArray()));
                var vma = mm.FindVMA(mapAddr);
                Assert.NotNull(vma);
                VMAManager.SyncVMA(vma!, engine, mapAddr, mapAddr + LinuxConstants.PageSize);
                mappedFile.Close();
                sm.Close();
            }

            using (var engine = new Engine())
            {
                var mm = new VMAManager();
                var sm = new SyscallManager(engine, mm, 0);
                var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
                var rootSb = tmpfsType.CreateFileSystem().ReadSuper(tmpfsType, 0, "test-root", null);
                var rootMount = new Mount(rootSb, rootSb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
                sm.InitializeRoot(rootSb.Root, rootMount);

                var root = sm.Root.Dentry!;
                if (root.Inode!.Lookup("mnt") == null)
                {
                    var mntDentry = new Dentry("mnt", null, root, root.SuperBlock);
                    root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
                    root.Children["mnt"] = mntDentry;
                }

                var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
                Assert.Equal(0, sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
                var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
                Assert.Equal(0, sm.AttachDetachedMount(mount!, target));

                var fileLoc = sm.PathWalkWithFlags("/mnt/map.txt", LookupFlags.FollowSymlink);
                Assert.True(fileLoc.IsValid);
                var rf = new LinuxFile(fileLoc.Dentry!, FileFlags.O_RDONLY, fileLoc.Mount!);
                var buf = new byte[16];
                var n = fileLoc.Dentry!.Inode!.Read(rf, buf, 0);
                rf.Close();
                Assert.Equal(5, n);
                Assert.Equal("hZZlo", System.Text.Encoding.UTF8.GetString(buf, 0, n));
                sm.Close();
            }
        }
        finally
        {
            if (Directory.Exists(silkRoot)) Directory.Delete(silkRoot, true);
        }
    }

    [Fact]
    public void Silkfs_WriteAfterMmap_Flush_PersistsAcrossRemount()
    {
        var silkRoot = Path.Combine(Path.GetTempPath(), $"silkfs-write-flush-{Guid.NewGuid():N}");

        try
        {
            using (var engine = new Engine())
            {
                var mm = new VMAManager();
                var sm = new SyscallManager(engine, mm, 0);
                var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
                var rootSb = tmpfsType.CreateFileSystem().ReadSuper(tmpfsType, 0, "test-root", null);
                var rootMount = new Mount(rootSb, rootSb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
                sm.InitializeRoot(rootSb.Root, rootMount);

                var root = sm.Root.Dentry!;
                if (root.Inode!.Lookup("mnt") == null)
                {
                    var mntDentry = new Dentry("mnt", null, root, root.SuperBlock);
                    root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
                    root.Children["mnt"] = mntDentry;
                }

                var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
                Assert.Equal(0, sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
                var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
                Assert.Equal(0, sm.AttachDetachedMount(mount!, target));
                var loc = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);

                var file = new Dentry("write_flush.txt", null, loc.Dentry, loc.Dentry!.SuperBlock);
                loc.Dentry.Inode!.Create(file, 0x1A4, 0, 0);
                var wf = new LinuxFile(file, FileFlags.O_WRONLY, loc.Mount!);
                Assert.Equal(5, file.Inode!.Write(wf, "hello"u8.ToArray(), 0));
                wf.Close();

                var mappedFile = new LinuxFile(file, FileFlags.O_RDWR, loc.Mount!);
                const uint mapAddr = 0x4D000000;
                mm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                    MapFlags.Shared | MapFlags.Fixed, mappedFile, 0, (long)mappedFile.Dentry.Inode!.Size, "MAP_SHARED", engine);
                Assert.True(mm.HandleFault(mapAddr, false, engine));

                Assert.Equal(2, mappedFile.Dentry.Inode!.Write(mappedFile, "XY"u8.ToArray(), 1));
                mm.SyncAllMappedSharedFiles(engine);
                mappedFile.Close();
                sm.Close();
            }

            using (var engine = new Engine())
            {
                var mm = new VMAManager();
                var sm = new SyscallManager(engine, mm, 0);
                var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
                var rootSb = tmpfsType.CreateFileSystem().ReadSuper(tmpfsType, 0, "test-root", null);
                var rootMount = new Mount(rootSb, rootSb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
                sm.InitializeRoot(rootSb.Root, rootMount);

                var root = sm.Root.Dentry!;
                if (root.Inode!.Lookup("mnt") == null)
                {
                    var mntDentry = new Dentry("mnt", null, root, root.SuperBlock);
                    root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
                    root.Children["mnt"] = mntDentry;
                }

                var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
                Assert.Equal(0, sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
                var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
                Assert.Equal(0, sm.AttachDetachedMount(mount!, target));

                var fileLoc = sm.PathWalkWithFlags("/mnt/write_flush.txt", LookupFlags.FollowSymlink);
                Assert.True(fileLoc.IsValid);
                var rf = new LinuxFile(fileLoc.Dentry!, FileFlags.O_RDONLY, fileLoc.Mount!);
                var buf = new byte[16];
                var n = fileLoc.Dentry!.Inode!.Read(rf, buf, 0);
                rf.Close();
                Assert.Equal(5, n);
                Assert.Equal("hXYlo", System.Text.Encoding.UTF8.GetString(buf, 0, n));
                sm.Close();
            }
        }
        finally
        {
            if (Directory.Exists(silkRoot)) Directory.Delete(silkRoot, true);
        }
    }

    [Fact]
    public void Silkfs_PageCache_CanBeReclaimedUnderPressure_AndReadStillCorrect()
    {
        using var cacheScope = GlobalPageCacheManager.BeginIsolatedScope();
        var silkRoot = Path.Combine(Path.GetTempPath(), $"silkfs-reclaim-{Guid.NewGuid():N}");

        try
        {
            using var engine = new Engine();
            var mm = new VMAManager();
            var sm = new SyscallManager(engine, mm, 0);
            var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
            var rootSb = tmpfsType.CreateFileSystem().ReadSuper(tmpfsType, 0, "test-root", null);
            var rootMount = new Mount(rootSb, rootSb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
            sm.InitializeRoot(rootSb.Root, rootMount);

            var root = sm.Root.Dentry!;
            if (root.Inode!.Lookup("mnt") == null)
            {
                var mntDentry = new Dentry("mnt", null, root, root.SuperBlock);
                root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
                root.Children["mnt"] = mntDentry;
            }

            var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
            Assert.Equal(0, sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
            var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
            Assert.Equal(0, sm.AttachDetachedMount(mount!, target));
            var loc = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);

            var file = new Dentry("reclaim.txt", null, loc.Dentry, loc.Dentry!.SuperBlock);
            loc.Dentry.Inode!.Create(file, 0x1A4, 0, 0);
            var wf = new LinuxFile(file, FileFlags.O_WRONLY, loc.Mount!);
            Assert.Equal(5, file.Inode!.Write(wf, "hello"u8.ToArray(), 0));
            wf.Close();

            var silkInode = Assert.IsType<SilkInode>(file.Inode);
            var cache = Assert.IsType<MemoryObject>(silkInode.PageCache);
            Assert.True(cache.PageCount > 0);

            var reclaimed = GlobalPageCacheManager.TryReclaimBytes(LinuxConstants.PageSize);
            Assert.True(reclaimed >= LinuxConstants.PageSize);
            Assert.Equal(0, cache.PageCount);

            var rf = new LinuxFile(file, FileFlags.O_RDONLY, loc.Mount!);
            var readBuf = new byte[16];
            var n = file.Inode.Read(rf, readBuf, 0);
            rf.Close();
            Assert.Equal(5, n);
            Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(readBuf, 0, n));
            sm.Close();
        }
        finally
        {
            if (Directory.Exists(silkRoot)) Directory.Delete(silkRoot, true);
        }
    }

    [Fact]
    public void Silkfs_PageCache_AutoReclaim_OnMaintenancePressure()
    {
        using var cacheScope = GlobalPageCacheManager.BeginIsolatedScope();
        var originalHigh = GlobalPageCacheManager.HighWatermarkBytes;
        var originalLow = GlobalPageCacheManager.LowWatermarkBytes;
        var originalInterval = GlobalPageCacheManager.WritebackInterval;
        var silkRoot = Path.Combine(Path.GetTempPath(), $"silkfs-auto-reclaim-{Guid.NewGuid():N}");

        GlobalPageCacheManager.HighWatermarkBytes = 0;
        GlobalPageCacheManager.LowWatermarkBytes = 0;
        GlobalPageCacheManager.WritebackInterval = TimeSpan.Zero;

        try
        {
            using var engine = new Engine();
            var mm = new VMAManager();
            var sm = new SyscallManager(engine, mm, 0);
            var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
            var rootSb = tmpfsType.CreateFileSystem().ReadSuper(tmpfsType, 0, "test-root", null);
            var rootMount = new Mount(rootSb, rootSb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
            sm.InitializeRoot(rootSb.Root, rootMount);

            var root = sm.Root.Dentry!;
            if (root.Inode!.Lookup("mnt") == null)
            {
                var mntDentry = new Dentry("mnt", null, root, root.SuperBlock);
                root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
                root.Children["mnt"] = mntDentry;
            }

            var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
            Assert.Equal(0, sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
            var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
            Assert.Equal(0, sm.AttachDetachedMount(mount!, target));
            var loc = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);

            var file = new Dentry("auto_reclaim.txt", null, loc.Dentry, loc.Dentry!.SuperBlock);
            loc.Dentry.Inode!.Create(file, 0x1A4, 0, 0);
            var wf = new LinuxFile(file, FileFlags.O_WRONLY, loc.Mount!);
            Assert.Equal(5, file.Inode!.Write(wf, "hello"u8.ToArray(), 0));
            wf.Close();

            var silkInode = Assert.IsType<SilkInode>(file.Inode);
            var cache = Assert.IsType<MemoryObject>(silkInode.PageCache);
            Assert.True(cache.PageCount > 0);

            GlobalPageCacheManager.MaybeRunMaintenance(mm, engine);

            Assert.Equal(0, cache.PageCount);

            var rf = new LinuxFile(file, FileFlags.O_RDONLY, loc.Mount!);
            var readBuf = new byte[16];
            var n = file.Inode.Read(rf, readBuf, 0);
            rf.Close();
            Assert.Equal(5, n);
            Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(readBuf, 0, n));
            sm.Close();
        }
        finally
        {
            GlobalPageCacheManager.HighWatermarkBytes = originalHigh;
            GlobalPageCacheManager.LowWatermarkBytes = originalLow;
            GlobalPageCacheManager.WritebackInterval = originalInterval;
            if (Directory.Exists(silkRoot)) Directory.Delete(silkRoot, true);
        }
    }

    [Fact]
    public void Silkfs_DropCaches_EvictsInodes_AndIgetReloadsFromMetadata()
    {
        var silkRoot = Path.Combine(Path.GetTempPath(), $"silkfs-iget-evict-{Guid.NewGuid():N}");
        try
        {
            using var engine = new Engine();
            var vma = new VMAManager();
            var sm = new SyscallManager(engine, vma, 0);
            var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
            var rootSb = tmpfsType.CreateFileSystem().ReadSuper(tmpfsType, 0, "test-root", null);
            var rootMount = new Mount(rootSb, rootSb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
            sm.InitializeRoot(rootSb.Root, rootMount);

            var root = sm.Root.Dentry!;
            if (root.Inode!.Lookup("mnt") == null)
            {
                var mntDentry = new Dentry("mnt", null, root, root.SuperBlock);
                root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
                root.Children["mnt"] = mntDentry;
            }

            var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
            Assert.Equal(0, sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
            var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
            Assert.Equal(0, sm.AttachDetachedMount(mount!, target));

            var loc = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
            var dir = new Dentry("sub", null, loc.Dentry, loc.Dentry!.SuperBlock);
            loc.Dentry.Inode!.Mkdir(dir, 0x1ED, 0, 0);
            var file = new Dentry("data.txt", null, dir, dir.SuperBlock);
            dir.Inode!.Create(file, 0x1A4, 0, 0);

            var payload = "hello-iget";
            var wf = new LinuxFile(file, FileFlags.O_WRONLY, loc.Mount!);
            Assert.Equal(payload.Length, file.Inode!.Write(wf, System.Text.Encoding.UTF8.GetBytes(payload), 0));
            wf.Close();

            var before = sm.PathWalkWithFlags("/mnt/sub/data.txt", LookupFlags.FollowSymlink);
            Assert.True(before.IsValid);
            var oldInode = before.Dentry!.Inode!;

            var stats = VfsCacheReclaimer.DropDentryAndInodeCaches(sm);
            Assert.True(stats.DentriesDropped > 0);
            Assert.True(stats.InodesDropped > 0);

            var after = sm.PathWalkWithFlags("/mnt/sub/data.txt", LookupFlags.FollowSymlink);
            Assert.True(after.IsValid);
            Assert.NotNull(after.Dentry!.Inode);
            Assert.NotSame(oldInode, after.Dentry.Inode);

            var rf = new LinuxFile(after.Dentry, FileFlags.O_RDONLY, after.Mount!);
            var buffer = new byte[32];
            var n = after.Dentry.Inode!.Read(rf, buffer, 0);
            rf.Close();

            Assert.Equal(payload.Length, n);
            Assert.Equal(payload, System.Text.Encoding.UTF8.GetString(buffer, 0, n));
            sm.Close();
        }
        finally
        {
            if (Directory.Exists(silkRoot)) Directory.Delete(silkRoot, true);
        }
    }

    [Fact]
    public void Silkfs_DropCaches_DoesNotBreakOpenFileRefs()
    {
        var silkRoot = Path.Combine(Path.GetTempPath(), $"silkfs-drop-open-{Guid.NewGuid():N}");
        try
        {
            using var engine = new Engine();
            var vma = new VMAManager();
            var sm = new SyscallManager(engine, vma, 0);
            var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
            var rootSb = tmpfsType.CreateFileSystem().ReadSuper(tmpfsType, 0, "test-root", null);
            var rootMount = new Mount(rootSb, rootSb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
            sm.InitializeRoot(rootSb.Root, rootMount);

            var root = sm.Root.Dentry!;
            if (root.Inode!.Lookup("mnt") == null)
            {
                var mntDentry = new Dentry("mnt", null, root, root.SuperBlock);
                root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
                root.Children["mnt"] = mntDentry;
            }

            var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
            Assert.Equal(0, sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
            var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
            Assert.Equal(0, sm.AttachDetachedMount(mount!, target));

            var loc = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
            var file = new Dentry("held.txt", null, loc.Dentry, loc.Dentry!.SuperBlock);
            loc.Dentry.Inode!.Create(file, 0x1A4, 0, 0);
            var payload = "keep-open";
            var wf = new LinuxFile(file, FileFlags.O_WRONLY, loc.Mount!);
            Assert.Equal(payload.Length, file.Inode!.Write(wf, System.Text.Encoding.UTF8.GetBytes(payload), 0));
            wf.Close();

            var rf = new LinuxFile(file, FileFlags.O_RDONLY, loc.Mount!);
            var stats = VfsCacheReclaimer.DropDentryAndInodeCaches(sm);
            Assert.True(stats.DentriesDropped > 0);

            var buffer = new byte[32];
            var n = rf.OpenedInode!.Read(rf, buffer, 0);
            Assert.Equal(payload.Length, n);
            Assert.Equal(payload, System.Text.Encoding.UTF8.GetString(buffer, 0, n));
            rf.Close();
            sm.Close();
        }
        finally
        {
            if (Directory.Exists(silkRoot)) Directory.Delete(silkRoot, true);
        }
    }
}
