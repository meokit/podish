using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.SilkFS;
using Fiberish.Syscalls;
using Fiberish.VFS;
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
}
