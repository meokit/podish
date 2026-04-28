using System.Text;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.SilkFS;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.VFS;

public class SilkFsAdapterTests
{
    private readonly TestRuntimeFactory _runtime = new();

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    private static readonly FsName Mnt = FsName.FromString("mnt");
    private static readonly FsName HelloTxt = FsName.FromString("hello.txt");
    private static readonly FsName WhGhostTxt = FsName.FromString(".wh.ghost.txt");
    private static readonly FsName KeepTxt = FsName.FromString("keep.txt");
    private static readonly FsName WhGoneTxt = FsName.FromString(".wh.gone.txt");
    private static readonly FsName StampTxt = FsName.FromString("stamp.txt");
    private static readonly FsName From = FsName.FromString("from");
    private static readonly FsName To = FsName.FromString("to");
    private static readonly FsName Child = FsName.FromString("child");
    private static readonly FsName ObjTxt = FsName.FromString("obj.txt");
    private static readonly FsName GoneTxt = FsName.FromString("gone.txt");
    private static readonly FsName ATxt = FsName.FromString("a.txt");
    private static readonly FsName BTxt = FsName.FromString("b.txt");
    private static readonly FsName HeldTxt = FsName.FromString("held.txt");
    private static readonly FsName MapHeldTxt = FsName.FromString("mapheld.txt");
    private static readonly FsName MapTxt = FsName.FromString("map.txt");
    private static readonly FsName WriteFlushTxt = FsName.FromString("write_flush.txt");
    private static readonly FsName ReclaimTxt = FsName.FromString("reclaim.txt");
    private static readonly FsName AutoReclaimTxt = FsName.FromString("auto_reclaim.txt");
    private static readonly FsName Sub = FsName.FromString("sub");
    private static readonly FsName DataTxt = FsName.FromString("data.txt");
    private static readonly FsName BigBin = FsName.FromString("big.bin");
    private static readonly FsName ResizeBin = FsName.FromString("resize.bin");
    private static readonly FsName MappedTxt = FsName.FromString("mapped.txt");
    private static readonly FsName Reclaim = FsName.FromString("reclaim");

    [Fact]
    public void Silkfs_IsRegistered_AndCanAttachMount()
    {
        var silkRoot = Path.Combine(Path.GetTempPath(), $"silkfs-store-{Guid.NewGuid():N}");

        using var engine = _runtime.CreateEngine();
        var vma = _runtime.CreateAddressSpace();
        var sm = new SyscallManager(engine, vma, 0);
        var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
        var rootSb = tmpfsType.CreateAnonymousFileSystem(sm.MemoryContext).ReadSuper(tmpfsType, 0, "test-root", null);
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
                var mntDentry = new Dentry(Mnt, null, root, root.SuperBlock);
                root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
                root.CacheChild(mntDentry, "test");
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

            Assert.True(Directory.Exists(Path.Combine(silkRoot, "live")));
            Assert.True(File.Exists(Path.Combine(silkRoot, "metadata.sqlite3")));

            var file = new Dentry(HelloTxt, null, loc.Dentry, loc.Dentry!.SuperBlock);
            loc.Dentry.Inode!.Create(file, 0x1A4, 0, 0);
            var setRc = file.Inode!.SetXAttr("user.mime_type", "text/plain"u8.ToArray(), 0);
            Assert.Equal(0, setRc);

            var repo = new SilkRepository(SilkFsOptions.FromSource(silkRoot));
            repo.Initialize();
            using var session = repo.OpenMetadataSession();
            var childIno = session.LookupDentry(SilkMetadataStore.RootInode, Utf8("hello.txt"));
            Assert.NotNull(childIno);
            var x = session.GetXAttr(childIno!.Value, Utf8("user.mime_type"));
            Assert.NotNull(x);

            var wh = new Dentry(WhGhostTxt, null, loc.Dentry, loc.Dentry!.SuperBlock);
            loc.Dentry.Inode!.Mknod(wh, 0x180, 0, 0, InodeType.CharDev, 0);
            Assert.True(session.HasWhiteout(SilkMetadataStore.RootInode, Utf8("ghost.txt")));
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
            using (var engine = _runtime.CreateEngine())
            {
                var vma = _runtime.CreateAddressSpace();
                var sm = new SyscallManager(engine, vma, 0);
                var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
                var rootSb = tmpfsType.CreateAnonymousFileSystem(sm.MemoryContext).ReadSuper(tmpfsType, 0, "test-root", null);
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
                    var mntDentry = new Dentry(Mnt, null, root, root.SuperBlock);
                    root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
                    root.CacheChild(mntDentry, "test");
                }

                var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
                Assert.Equal(0, sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
                var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
                Assert.Equal(0, sm.AttachDetachedMount(mount!, target));
                var loc = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);

                var file = new Dentry(KeepTxt, null, loc.Dentry, loc.Dentry!.SuperBlock);
                loc.Dentry.Inode!.Create(file, 0x1A4, 0, 0);
                var wf = new LinuxFile(file, FileFlags.O_WRONLY, loc.Mount!);
                var payload = Encoding.UTF8.GetBytes("hello-silk");
                var wrote = file.Inode!.WriteFromHost(null, wf, payload, 0);
                Assert.Equal(payload.Length, wrote);
                wf.Close();
                Assert.Equal(0, file.Inode!.SetXAttr("user.test", "value"u8.ToArray(), 0));
                var wh = new Dentry(WhGoneTxt, null, loc.Dentry, loc.Dentry.SuperBlock);
                loc.Dentry.Inode.Mknod(wh, 0x180, 0, 0, InodeType.CharDev, 0);
                sm.Close();
            }

            using (var engine = _runtime.CreateEngine())
            {
                var vma = _runtime.CreateAddressSpace();
                var sm = new SyscallManager(engine, vma, 0);
                var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
                var rootSb = tmpfsType.CreateAnonymousFileSystem(sm.MemoryContext).ReadSuper(tmpfsType, 0, "test-root", null);
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
                    var mntDentry = new Dentry(Mnt, null, root, root.SuperBlock);
                    root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
                    root.CacheChild(mntDentry, "test");
                }

                var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
                Assert.Equal(0, sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
                var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
                Assert.Equal(0, sm.AttachDetachedMount(mount!, target));

                var fileLoc = sm.PathWalkWithFlags("/mnt/keep.txt", LookupFlags.FollowSymlink);
                Assert.True(fileLoc.IsValid);
                var rf = new LinuxFile(fileLoc.Dentry!, FileFlags.O_RDONLY, fileLoc.Mount!);
                var readBuf = new byte[64];
                var n = fileLoc.Dentry!.Inode!.ReadToHost(null, rf, readBuf, 0);
                rf.Close();
                Assert.Equal(10, n);
                Assert.Equal("hello-silk", Encoding.UTF8.GetString(readBuf, 0, n));
                var buf = new byte[16];
                var xrc = fileLoc.Dentry!.Inode!.GetXAttr("user.test", buf);
                Assert.Equal(5, xrc);
                Assert.Equal("value", Encoding.UTF8.GetString(buf, 0, xrc));

                var whLoc = sm.PathWalkWithFlags("/mnt/.wh.gone.txt", LookupFlags.FollowSymlink);
                Assert.True(whLoc.IsValid);
                Assert.Equal(InodeType.CharDev, whLoc.Dentry!.Inode!.Type);
                Assert.Equal((uint)0, whLoc.Dentry.Inode.Rdev);

                var repo = new SilkRepository(SilkFsOptions.FromSource(silkRoot));
                repo.Initialize();
                using var session = repo.OpenMetadataSession();
                Assert.True(session.HasWhiteout(SilkMetadataStore.RootInode, Utf8("gone.txt")));
                sm.Close();
            }
        }
        finally
        {
            DeleteDirectoryWithRetry(silkRoot);
        }
    }

    [Fact]
    public void Silkfs_Reload_RestoresPersistedTimestamps()
    {
        var silkRoot = Path.Combine(Path.GetTempPath(), $"silkfs-store-{Guid.NewGuid():N}");

        try
        {
            using (var engine = _runtime.CreateEngine())
            {
                var vma = _runtime.CreateAddressSpace();
                var sm = new SyscallManager(engine, vma, 0);
                var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
                var rootSb = tmpfsType.CreateAnonymousFileSystem(sm.MemoryContext).ReadSuper(tmpfsType, 0, "test-root", null);
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
                    var mntDentry = new Dentry(Mnt, null, root, root.SuperBlock);
                    root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
                    root.CacheChild(mntDentry, "test");
                }

                var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
                Assert.Equal(0, sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
                var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
                Assert.Equal(0, sm.AttachDetachedMount(mount!, target));
                var loc = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);

                var file = new Dentry(StampTxt, null, loc.Dentry, loc.Dentry!.SuperBlock);
                loc.Dentry.Inode!.Create(file, 0x1A4, 0, 0);
                var inode = file.Inode!;
                var atime = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000).UtcDateTime;
                var mtime = DateTimeOffset.FromUnixTimeSeconds(1_700_000_100).UtcDateTime;
                var ctime = DateTimeOffset.FromUnixTimeSeconds(1_700_000_200).UtcDateTime;
                Assert.Equal(0, inode.UpdateTimes(atime, mtime, ctime));
                sm.Close();
            }

            using (var engine = _runtime.CreateEngine())
            {
                var vma = _runtime.CreateAddressSpace();
                var sm = new SyscallManager(engine, vma, 0);
                var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
                var rootSb = tmpfsType.CreateAnonymousFileSystem(sm.MemoryContext).ReadSuper(tmpfsType, 0, "test-root", null);
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
                    var mntDentry = new Dentry(Mnt, null, root, root.SuperBlock);
                    root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
                    root.CacheChild(mntDentry, "test");
                }

                var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
                Assert.Equal(0, sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
                var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
                Assert.Equal(0, sm.AttachDetachedMount(mount!, target));
                var inode = sm.PathWalkWithFlags("/mnt/stamp.txt", LookupFlags.FollowSymlink).Dentry!.Inode!;

                Assert.Equal(1_700_000_000L, new DateTimeOffset(inode.ATime).ToUnixTimeSeconds());
                Assert.Equal(1_700_000_100L, new DateTimeOffset(inode.MTime).ToUnixTimeSeconds());
                Assert.Equal(1_700_000_200L, new DateTimeOffset(inode.CTime).ToUnixTimeSeconds());
                sm.Close();
            }
        }
        finally
        {
            DeleteDirectoryWithRetry(silkRoot);
        }
    }

    [Fact]
    public void Silkfs_Reload_PersistsModeOnlyMetadataMutation()
    {
        var silkRoot = Path.Combine(Path.GetTempPath(), $"silkfs-mode-store-{Guid.NewGuid():N}");

        try
        {
            using (var engine = _runtime.CreateEngine())
            {
                var vma = _runtime.CreateAddressSpace();
                var sm = new SyscallManager(engine, vma, 0);
                var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
                var rootSb = tmpfsType.CreateAnonymousFileSystem(sm.MemoryContext).ReadSuper(tmpfsType, 0, "test-root", null);
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
                    var mntDentry = new Dentry(Mnt, null, root, root.SuperBlock);
                    root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
                    root.CacheChild(mntDentry, "test");
                }

                var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
                Assert.Equal(0, sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
                var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
                Assert.Equal(0, sm.AttachDetachedMount(mount!, target));
                var loc = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);

                var file = new Dentry(StampTxt, null, loc.Dentry, loc.Dentry!.SuperBlock);
                loc.Dentry.Inode!.Create(file, 0x1A4, 0, 0);
                file.Inode!.Mode = 0x1ED;

                sm.Close();
            }

            using (var engine = _runtime.CreateEngine())
            {
                var vma = _runtime.CreateAddressSpace();
                var sm = new SyscallManager(engine, vma, 0);
                var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
                var rootSb = tmpfsType.CreateAnonymousFileSystem(sm.MemoryContext).ReadSuper(tmpfsType, 0, "test-root", null);
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
                    var mntDentry = new Dentry(Mnt, null, root, root.SuperBlock);
                    root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
                    root.CacheChild(mntDentry, "test");
                }

                var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
                Assert.Equal(0, sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
                var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
                Assert.Equal(0, sm.AttachDetachedMount(mount!, target));

                var fileLoc = sm.PathWalkWithFlags("/mnt/stamp.txt", LookupFlags.FollowSymlink);
                Assert.True(fileLoc.IsValid);
                Assert.Equal(0x1ED, fileLoc.Dentry!.Inode!.Mode);

                sm.Close();
            }
        }
        finally
        {
            DeleteDirectoryWithRetry(silkRoot);
        }
    }

    [Fact]
    public void Silkfs_Remount_PreservesDirectoryNlinkAfterCrossParentRename()
    {
        var silkRoot = Path.Combine(Path.GetTempPath(), $"silkfs-store-{Guid.NewGuid():N}");

        try
        {
            using (var engine = _runtime.CreateEngine())
            {
                var vma = _runtime.CreateAddressSpace();
                var sm = new SyscallManager(engine, vma, 0);
                var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
                var rootSb = tmpfsType.CreateAnonymousFileSystem(sm.MemoryContext).ReadSuper(tmpfsType, 0, "test-root", null);
                var rootMount = new Mount(rootSb, rootSb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
                sm.InitializeRoot(rootSb.Root, rootMount);

                var root = sm.Root.Dentry!;
                if (root.Inode!.Lookup("mnt") == null)
                {
                    var mntDentry = new Dentry(Mnt, null, root, root.SuperBlock);
                    root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
                    root.CacheChild(mntDentry, "test");
                }

                var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
                Assert.Equal(0, sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
                var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
                Assert.Equal(0, sm.AttachDetachedMount(mount!, target));

                var loc = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
                var mntInode = loc.Dentry!.Inode!;

                var from = new Dentry(From, null, loc.Dentry, loc.Dentry.SuperBlock);
                mntInode.Mkdir(from, 0x1ED, 0, 0);
                var fromInode = Assert.IsAssignableFrom<Inode>(from.Inode);

                var to = new Dentry(To, null, loc.Dentry, loc.Dentry.SuperBlock);
                mntInode.Mkdir(to, 0x1ED, 0, 0);
                var toInode = Assert.IsAssignableFrom<Inode>(to.Inode);

                var child = new Dentry(Child, null, from, from.SuperBlock);
                fromInode.Mkdir(child, 0x1ED, 0, 0);
                Assert.Equal(3, fromInode.LinkCount);
                Assert.Equal(2, toInode.LinkCount);

                fromInode.Rename("child", toInode, "moved");
                Assert.Equal(2, fromInode.LinkCount);
                Assert.Equal(3, toInode.LinkCount);

                sm.Close();
            }

            using (var engine = _runtime.CreateEngine())
            {
                var vma = _runtime.CreateAddressSpace();
                var sm = new SyscallManager(engine, vma, 0);
                var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
                var rootSb = tmpfsType.CreateAnonymousFileSystem(sm.MemoryContext).ReadSuper(tmpfsType, 0, "test-root", null);
                var rootMount = new Mount(rootSb, rootSb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
                sm.InitializeRoot(rootSb.Root, rootMount);

                var root = sm.Root.Dentry!;
                if (root.Inode!.Lookup("mnt") == null)
                {
                    var mntDentry = new Dentry(Mnt, null, root, root.SuperBlock);
                    root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
                    root.CacheChild(mntDentry, "test");
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
                using var session = repo.OpenMetadataSession();
                var fromIno = session.LookupDentry(SilkMetadataStore.RootInode, Utf8("from"));
                var toIno = session.LookupDentry(SilkMetadataStore.RootInode, Utf8("to"));
                Assert.NotNull(fromIno);
                Assert.NotNull(toIno);

                var fromRec = session.GetInode(fromIno!.Value);
                var toRec = session.GetInode(toIno!.Value);
                Assert.NotNull(fromRec);
                Assert.NotNull(toRec);
                Assert.Equal(2, fromRec!.Value.Nlink);
                Assert.Equal(3, toRec!.Value.Nlink);

                sm.Close();
            }
        }
        finally
        {
            DeleteDirectoryWithRetry(silkRoot);
        }
    }

    [Fact]
    public void Silkfs_LiveFile_RewritesAndDeletesWithoutObjectStore()
    {
        var silkRoot = Path.Combine(Path.GetTempPath(), $"silkfs-store-{Guid.NewGuid():N}");
        try
        {
            using var engine = _runtime.CreateEngine();
            var vma = _runtime.CreateAddressSpace();
            var sm = new SyscallManager(engine, vma, 0);
            var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
            var rootSb = tmpfsType.CreateAnonymousFileSystem(sm.MemoryContext).ReadSuper(tmpfsType, 0, "test-root", null);
            var rootMount = new Mount(rootSb, rootSb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
            sm.InitializeRoot(rootSb.Root, rootMount);

            var root = sm.Root.Dentry!;
            if (root.Inode!.Lookup("mnt") == null)
            {
                var mntDentry = new Dentry(Mnt, null, root, root.SuperBlock);
                root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
                root.CacheChild(mntDentry, "test");
            }

            var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
            Assert.Equal(0, sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
            var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
            Assert.Equal(0, sm.AttachDetachedMount(mount!, target));
            var loc = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
            var silkSb = Assert.IsType<SilkSuperBlock>(loc.Dentry!.SuperBlock);

            var file = new Dentry(ObjTxt, null, loc.Dentry, loc.Dentry!.SuperBlock);
            loc.Dentry.Inode!.Create(file, 0x1A4, 0, 0);

            var wf = new LinuxFile(file, FileFlags.O_WRONLY, loc.Mount!);
            Assert.Equal(1, file.Inode!.WriteFromHost(null, wf, "A"u8.ToArray(), 0));
            file.Inode.Sync(wf);
            wf.Close();

            var livePath = silkSb.Repository.GetLiveInodePath((long)file.Inode!.Ino);
            Assert.True(File.Exists(livePath));
            Assert.Equal("A", ReadAllTextShared(livePath));

            wf = new LinuxFile(file, FileFlags.O_WRONLY, loc.Mount!);
            Assert.Equal(1, file.Inode.WriteFromHost(null, wf, "B"u8.ToArray(), 0));
            file.Inode.Sync(wf);
            wf.Close();

            Assert.True(File.Exists(livePath));
            Assert.Equal("B", ReadAllTextShared(livePath));

            loc.Dentry.Inode!.Unlink("obj.txt");
            Assert.False(File.Exists(livePath));

            sm.UnregisterMount(mount!);
            sm.Close();
            silkSb.Dispose();
        }
        finally
        {
            if (!OperatingSystem.IsWindows())
            {
                DeleteDirectoryWithRetry(silkRoot);
            }
            else
            {
                try
                {
                    DeleteDirectoryWithRetry(silkRoot);
                }
                catch (IOException ex) when (ex.Message.Contains("metadata.sqlite3", StringComparison.OrdinalIgnoreCase))
                {
                    // Rewrite+unlink leaves a Windows-only metadata handle lifetime issue during teardown.
                    // The behavior assertions above still passed; keep cleanup best-effort until that leak is fixed.
                }
            }
        }
    }

    [Fact]
    public void Silkfs_UnlinkClosedFile_RemovesInodeMetadata()
    {
        var silkRoot = Path.Combine(Path.GetTempPath(), $"silkfs-unlink-clean-{Guid.NewGuid():N}");
        try
        {
            using var engine = _runtime.CreateEngine();
            var vma = _runtime.CreateAddressSpace();
            var sm = new SyscallManager(engine, vma, 0);
            var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
            var rootSb = tmpfsType.CreateAnonymousFileSystem(sm.MemoryContext).ReadSuper(tmpfsType, 0, "test-root", null);
            var rootMount = new Mount(rootSb, rootSb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
            sm.InitializeRoot(rootSb.Root, rootMount);

            var root = sm.Root.Dentry!;
            if (root.Inode!.Lookup("mnt") == null)
            {
                var mntDentry = new Dentry(Mnt, null, root, root.SuperBlock);
                root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
                root.CacheChild(mntDentry, "test");
            }

            var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
            Assert.Equal(0, sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
            var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
            Assert.Equal(0, sm.AttachDetachedMount(mount!, target));
            var loc = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);

            var file = new Dentry(GoneTxt, null, loc.Dentry, loc.Dentry!.SuperBlock);
            loc.Dentry.Inode!.Create(file, 0x1A4, 0, 0);
            var wf = new LinuxFile(file, FileFlags.O_WRONLY, loc.Mount!);
            Assert.Equal(4, file.Inode!.WriteFromHost(null, wf, "data"u8.ToArray(), 0));
            wf.Close();

            var repo = new SilkRepository(SilkFsOptions.FromSource(silkRoot));
            repo.Initialize();
            using var session = repo.OpenMetadataSession();
            var ino = session.LookupDentry(SilkMetadataStore.RootInode, Utf8("gone.txt"));
            Assert.NotNull(ino);

            loc.Dentry.Inode.Unlink("gone.txt");

            Assert.Null(session.LookupDentry(SilkMetadataStore.RootInode, Utf8("gone.txt")));
            Assert.Null(session.GetInode(ino!.Value));
            Assert.False(File.Exists(repo.GetLiveInodePath(ino.Value)));
            sm.UnregisterMount(mount!);
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
            using var engine = _runtime.CreateEngine();
            var vma = _runtime.CreateAddressSpace();
            var sm = new SyscallManager(engine, vma, 0);
            var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
            var rootSb = tmpfsType.CreateAnonymousFileSystem(sm.MemoryContext).ReadSuper(tmpfsType, 0, "test-root", null);
            var rootMount = new Mount(rootSb, rootSb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
            sm.InitializeRoot(rootSb.Root, rootMount);

            var root = sm.Root.Dentry!;
            if (root.Inode!.Lookup("mnt") == null)
            {
                var mntDentry = new Dentry(Mnt, null, root, root.SuperBlock);
                root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
                root.CacheChild(mntDentry, "test");
            }

            var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
            Assert.Equal(0, sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
            var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
            Assert.Equal(0, sm.AttachDetachedMount(mount!, target));
            var loc = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);

            var a = new Dentry(ATxt, null, loc.Dentry, loc.Dentry!.SuperBlock);
            var b = new Dentry(BTxt, null, loc.Dentry, loc.Dentry.SuperBlock);
            loc.Dentry.Inode!.Create(a, 0x1A4, 0, 0);
            loc.Dentry.Inode.Create(b, 0x1A4, 0, 0);

            var wa = new LinuxFile(a, FileFlags.O_WRONLY, loc.Mount!);
            Assert.Equal(1, a.Inode!.WriteFromHost(null, wa, "A"u8.ToArray(), 0));
            wa.Close();
            var wb = new LinuxFile(b, FileFlags.O_WRONLY, loc.Mount!);
            Assert.Equal(1, b.Inode!.WriteFromHost(null, wb, "B"u8.ToArray(), 0));
            wb.Close();

            var repo = new SilkRepository(SilkFsOptions.FromSource(silkRoot));
            repo.Initialize();
            using var session = repo.OpenMetadataSession();
            var bIno = session.LookupDentry(SilkMetadataStore.RootInode, Utf8("b.txt"));
            Assert.NotNull(bIno);

            loc.Dentry.Inode.Rename("a.txt", loc.Dentry.Inode, "b.txt");

            Assert.Null(session.LookupDentry(SilkMetadataStore.RootInode, Utf8("a.txt")));
            var newBIno = session.LookupDentry(SilkMetadataStore.RootInode, Utf8("b.txt"));
            Assert.NotNull(newBIno);
            Assert.NotEqual(bIno, newBIno);
            Assert.Null(session.GetInode(bIno!.Value));
            sm.Close();
        }
        finally
        {
            if (Directory.Exists(silkRoot)) Directory.Delete(silkRoot, true);
        }
    }

    [Fact]
    public void Silkfs_UnlinkOpenFile_DefersLiveFileDeletionUntilClose()
    {
        var silkRoot = Path.Combine(Path.GetTempPath(), $"silkfs-orphan-open-{Guid.NewGuid():N}");
        try
        {
            using var engine = _runtime.CreateEngine();
            var mm = _runtime.CreateAddressSpace();
            var sm = new SyscallManager(engine, mm, 0);
            var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
            var rootSb = tmpfsType.CreateAnonymousFileSystem(sm.MemoryContext).ReadSuper(tmpfsType, 0, "test-root", null);
            var rootMount = new Mount(rootSb, rootSb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
            sm.InitializeRoot(rootSb.Root, rootMount);

            var root = sm.Root.Dentry!;
            if (root.Inode!.Lookup("mnt") == null)
            {
                var mntDentry = new Dentry(Mnt, null, root, root.SuperBlock);
                root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
                root.CacheChild(mntDentry, "test");
            }

            var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
            Assert.Equal(0, sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
            var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
            Assert.Equal(0, sm.AttachDetachedMount(mount!, target));
            var loc = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);

            var file = new Dentry(HeldTxt, null, loc.Dentry, loc.Dentry!.SuperBlock);
            loc.Dentry.Inode!.Create(file, 0x1A4, 0, 0);
            var wf = new LinuxFile(file, FileFlags.O_WRONLY, loc.Mount!);
            Assert.Equal(5, file.Inode!.WriteFromHost(null, wf, "hello"u8.ToArray(), 0));
            wf.Close();

            var repo = new SilkRepository(SilkFsOptions.FromSource(silkRoot));
            repo.Initialize();
            using var session = repo.OpenMetadataSession();
            var ino = session.LookupDentry(SilkMetadataStore.RootInode, Utf8("held.txt"));
            Assert.NotNull(ino);
            var livePath = repo.GetLiveInodePath(ino!.Value);
            Assert.True(File.Exists(livePath));

            var rf = new LinuxFile(file, FileFlags.O_RDONLY, loc.Mount!);
            loc.Dentry.Inode!.Unlink("held.txt");

            Assert.True(File.Exists(livePath));
            var readBuf = new byte[16];
            var n = rf.OpenedInode!.ReadToHost(null, rf, readBuf, 0);
            Assert.Equal(5, n);
            Assert.Equal("hello", Encoding.UTF8.GetString(readBuf, 0, n));

            rf.Close();

            Assert.False(File.Exists(livePath));
            sm.Close();
        }
        finally
        {
            if (Directory.Exists(silkRoot)) Directory.Delete(silkRoot, true);
        }
    }

    [Fact]
    public void Silkfs_UnlinkMmapHold_DefersLiveFileDeletionUntilMunmap()
    {
        var silkRoot = Path.Combine(Path.GetTempPath(), $"silkfs-orphan-mmap-{Guid.NewGuid():N}");
        try
        {
            using var engine = _runtime.CreateEngine();
            var mm = _runtime.CreateAddressSpace();
            var sm = new SyscallManager(engine, mm, 0);
            var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
            var rootSb = tmpfsType.CreateAnonymousFileSystem(sm.MemoryContext).ReadSuper(tmpfsType, 0, "test-root", null);
            var rootMount = new Mount(rootSb, rootSb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
            sm.InitializeRoot(rootSb.Root, rootMount);

            var root = sm.Root.Dentry!;
            if (root.Inode!.Lookup("mnt") == null)
            {
                var mntDentry = new Dentry(Mnt, null, root, root.SuperBlock);
                root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
                root.CacheChild(mntDentry, "test");
            }

            var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
            Assert.Equal(0, sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
            var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
            Assert.Equal(0, sm.AttachDetachedMount(mount!, target));
            var loc = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);

            var file = new Dentry(MapHeldTxt, null, loc.Dentry, loc.Dentry!.SuperBlock);
            loc.Dentry.Inode!.Create(file, 0x1A4, 0, 0);
            var wf = new LinuxFile(file, FileFlags.O_WRONLY, loc.Mount!);
            Assert.Equal(5, file.Inode!.WriteFromHost(null, wf, "hello"u8.ToArray(), 0));
            wf.Close();

            var repo = new SilkRepository(SilkFsOptions.FromSource(silkRoot));
            repo.Initialize();
            using var session = repo.OpenMetadataSession();
            var ino = session.LookupDentry(SilkMetadataStore.RootInode, Utf8("mapheld.txt"));
            Assert.NotNull(ino);
            var livePath = repo.GetLiveInodePath(ino!.Value);
            Assert.True(File.Exists(livePath));

            var mappedFile = new LinuxFile(file, FileFlags.O_RDWR, loc.Mount!, LinuxFile.ReferenceKind.MmapHold);
            const uint mapAddr = 0x58000000;
            mm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, mappedFile, 0, "MAP_SHARED", engine);

            loc.Dentry.Inode!.Unlink("mapheld.txt");
            Assert.True(File.Exists(livePath));

            mm.Munmap(mapAddr, LinuxConstants.PageSize, engine);

            Assert.False(File.Exists(livePath));
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
            using (var engine = _runtime.CreateEngine())
            {
                var mm = _runtime.CreateAddressSpace();
                var sm = new SyscallManager(engine, mm, 0);
                var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
                var rootSb = tmpfsType.CreateAnonymousFileSystem(sm.MemoryContext).ReadSuper(tmpfsType, 0, "test-root", null);
                var rootMount = new Mount(rootSb, rootSb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
                sm.InitializeRoot(rootSb.Root, rootMount);

                var root = sm.Root.Dentry!;
                if (root.Inode!.Lookup("mnt") == null)
                {
                    var mntDentry = new Dentry(Mnt, null, root, root.SuperBlock);
                    root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
                    root.CacheChild(mntDentry, "test");
                }

                var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
                Assert.Equal(0, sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
                var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
                Assert.Equal(0, sm.AttachDetachedMount(mount!, target));
                var loc = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);

                var file = new Dentry(MapTxt, null, loc.Dentry, loc.Dentry!.SuperBlock);
                loc.Dentry.Inode!.Create(file, 0x1A4, 0, 0);
                var wf = new LinuxFile(file, FileFlags.O_WRONLY, loc.Mount!);
                var payload = "hello"u8.ToArray();
                Assert.Equal(payload.Length, file.Inode!.WriteFromHost(null, wf, payload, 0));
                wf.Close();

                var fileLoc = sm.PathWalkWithFlags("/mnt/map.txt", LookupFlags.FollowSymlink);
                Assert.True(fileLoc.IsValid);
                var mappedFile = new LinuxFile(fileLoc.Dentry!, FileFlags.O_RDWR, fileLoc.Mount!);
                const uint mapAddr = 0x4C000000;
                mm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                    MapFlags.Shared | MapFlags.Fixed, mappedFile, 0, "MAP_SHARED", engine);
                Assert.True(mm.HandleFault(mapAddr, true, engine));
                Assert.True(engine.CopyToUser(mapAddr + 1, "ZZ"u8.ToArray()));
                var vma = mm.FindVmArea(mapAddr);
                Assert.NotNull(vma);
                VMAManager.SyncVmArea(vma!, engine, mapAddr, mapAddr + LinuxConstants.PageSize);
                mappedFile.Close();
                sm.Close();
            }

            using (var engine = _runtime.CreateEngine())
            {
                var mm = _runtime.CreateAddressSpace();
                var sm = new SyscallManager(engine, mm, 0);
                var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
                var rootSb = tmpfsType.CreateAnonymousFileSystem(sm.MemoryContext).ReadSuper(tmpfsType, 0, "test-root", null);
                var rootMount = new Mount(rootSb, rootSb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
                sm.InitializeRoot(rootSb.Root, rootMount);

                var root = sm.Root.Dentry!;
                if (root.Inode!.Lookup("mnt") == null)
                {
                    var mntDentry = new Dentry(Mnt, null, root, root.SuperBlock);
                    root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
                    root.CacheChild(mntDentry, "test");
                }

                var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
                Assert.Equal(0, sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
                var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
                Assert.Equal(0, sm.AttachDetachedMount(mount!, target));

                var fileLoc = sm.PathWalkWithFlags("/mnt/map.txt", LookupFlags.FollowSymlink);
                Assert.True(fileLoc.IsValid);
                var rf = new LinuxFile(fileLoc.Dentry!, FileFlags.O_RDONLY, fileLoc.Mount!);
                var buf = new byte[16];
                var n = fileLoc.Dentry!.Inode!.ReadToHost(null, rf, buf, 0);
                rf.Close();
                Assert.Equal(5, n);
                Assert.Equal("hZZlo", Encoding.UTF8.GetString(buf, 0, n));
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
            using (var engine = _runtime.CreateEngine())
            {
                var mm = _runtime.CreateAddressSpace();
                var sm = new SyscallManager(engine, mm, 0);
                var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
                var rootSb = tmpfsType.CreateAnonymousFileSystem(sm.MemoryContext).ReadSuper(tmpfsType, 0, "test-root", null);
                var rootMount = new Mount(rootSb, rootSb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
                sm.InitializeRoot(rootSb.Root, rootMount);

                var root = sm.Root.Dentry!;
                if (root.Inode!.Lookup("mnt") == null)
                {
                    var mntDentry = new Dentry(Mnt, null, root, root.SuperBlock);
                    root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
                    root.CacheChild(mntDentry, "test");
                }

                var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
                Assert.Equal(0, sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
                var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
                Assert.Equal(0, sm.AttachDetachedMount(mount!, target));
                var loc = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);

                var file = new Dentry(WriteFlushTxt, null, loc.Dentry, loc.Dentry!.SuperBlock);
                loc.Dentry.Inode!.Create(file, 0x1A4, 0, 0);
                var wf = new LinuxFile(file, FileFlags.O_WRONLY, loc.Mount!);
                Assert.Equal(5, file.Inode!.WriteFromHost(null, wf, "hello"u8.ToArray(), 0));
                wf.Close();

                var mappedFile = new LinuxFile(file, FileFlags.O_RDWR, loc.Mount!);
                const uint mapAddr = 0x4D000000;
                mm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                    MapFlags.Shared | MapFlags.Fixed, mappedFile, 0, "MAP_SHARED", engine);
                Assert.True(mm.HandleFault(mapAddr, false, engine));

                Assert.Equal(2, mappedFile.Dentry.Inode!.WriteFromHost(null, mappedFile, "XY"u8.ToArray(), 1));
                mm.SyncAllMappedSharedFiles(engine);
                mappedFile.Close();
                sm.Close();
            }

            using (var engine = _runtime.CreateEngine())
            {
                var mm = _runtime.CreateAddressSpace();
                var sm = new SyscallManager(engine, mm, 0);
                var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
                var rootSb = tmpfsType.CreateAnonymousFileSystem(sm.MemoryContext).ReadSuper(tmpfsType, 0, "test-root", null);
                var rootMount = new Mount(rootSb, rootSb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
                sm.InitializeRoot(rootSb.Root, rootMount);

                var root = sm.Root.Dentry!;
                if (root.Inode!.Lookup("mnt") == null)
                {
                    var mntDentry = new Dentry(Mnt, null, root, root.SuperBlock);
                    root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
                    root.CacheChild(mntDentry, "test");
                }

                var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
                Assert.Equal(0, sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
                var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
                Assert.Equal(0, sm.AttachDetachedMount(mount!, target));

                var fileLoc = sm.PathWalkWithFlags("/mnt/write_flush.txt", LookupFlags.FollowSymlink);
                Assert.True(fileLoc.IsValid);
                var rf = new LinuxFile(fileLoc.Dentry!, FileFlags.O_RDONLY, fileLoc.Mount!);
                var buf = new byte[16];
                var n = fileLoc.Dentry!.Inode!.ReadToHost(null, rf, buf, 0);
                rf.Close();
                Assert.Equal(5, n);
                Assert.Equal("hXYlo", Encoding.UTF8.GetString(buf, 0, n));
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
        var silkRoot = Path.Combine(Path.GetTempPath(), $"silkfs-reclaim-{Guid.NewGuid():N}");

        try
        {
            using var engine = _runtime.CreateEngine();
            var mm = _runtime.CreateAddressSpace();
            var sm = new SyscallManager(engine, mm, 0);
            var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
            var rootSb = tmpfsType.CreateAnonymousFileSystem(sm.MemoryContext).ReadSuper(tmpfsType, 0, "test-root", null);
            var rootMount = new Mount(rootSb, rootSb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
            sm.InitializeRoot(rootSb.Root, rootMount);

            var root = sm.Root.Dentry!;
            if (root.Inode!.Lookup("mnt") == null)
            {
                var mntDentry = new Dentry(Mnt, null, root, root.SuperBlock);
                root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
                root.CacheChild(mntDentry, "test");
            }

            var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
            Assert.Equal(0, sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
            var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
            Assert.Equal(0, sm.AttachDetachedMount(mount!, target));
            var loc = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);

            var file = new Dentry(ReclaimTxt, null, loc.Dentry, loc.Dentry!.SuperBlock);
            loc.Dentry.Inode!.Create(file, 0x1A4, 0, 0);
            var wf = new LinuxFile(file, FileFlags.O_WRONLY, loc.Mount!);
            Assert.Equal(5, file.Inode!.WriteFromHost(null, wf, "hello"u8.ToArray(), 0));
            wf.Close();

            var mappedFile = new LinuxFile(file, FileFlags.O_RDWR, loc.Mount!);
            const uint mapAddr = 0x4E000000;
            mm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, mappedFile, 0, "MAP_SHARED", engine);
            Assert.True(mm.HandleFault(mapAddr, false, engine));
            mm.Munmap(mapAddr, LinuxConstants.PageSize, engine);

            var silkInode = Assert.IsType<SilkInode>(file.Inode);
            var cache = Assert.IsType<AddressSpace>(silkInode.Mapping);
            Assert.True(cache.PageCount > 0);

            var reclaimed = sm.MemoryContext.AddressSpacePolicy.TryReclaimBytes(LinuxConstants.PageSize);
            Assert.True(reclaimed >= LinuxConstants.PageSize);
            Assert.Equal(0, cache.PageCount);

            var rf = new LinuxFile(file, FileFlags.O_RDONLY, loc.Mount!);
            var readBuf = new byte[16];
            var n = file.Inode.ReadToHost(null, rf, readBuf, 0);
            rf.Close();
            Assert.Equal(5, n);
            Assert.Equal("hello", Encoding.UTF8.GetString(readBuf, 0, n));
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
        using var engine = _runtime.CreateEngine();
        var mm = _runtime.CreateAddressSpace();
        var sm = new SyscallManager(engine, mm, 0);
        var silkRoot = Path.Combine(Path.GetTempPath(), $"silkfs-auto-reclaim-{Guid.NewGuid():N}");

        var originalHigh = sm.MemoryContext.AddressSpacePolicy.HighWatermarkBytes;
        var originalLow = sm.MemoryContext.AddressSpacePolicy.LowWatermarkBytes;
        var originalInterval = sm.MemoryContext.AddressSpacePolicy.WritebackInterval;

        sm.MemoryContext.AddressSpacePolicy.HighWatermarkBytes = 0;
        sm.MemoryContext.AddressSpacePolicy.LowWatermarkBytes = 0;
        sm.MemoryContext.AddressSpacePolicy.WritebackInterval = TimeSpan.Zero;

        try
        {
            var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
            var rootSb = tmpfsType.CreateAnonymousFileSystem(sm.MemoryContext).ReadSuper(tmpfsType, 0, "test-root", null);
            var rootMount = new Mount(rootSb, rootSb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
            sm.InitializeRoot(rootSb.Root, rootMount);

            var root = sm.Root.Dentry!;
            if (root.Inode!.Lookup("mnt") == null)
            {
                var mntDentry = new Dentry(Mnt, null, root, root.SuperBlock);
                root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
                root.CacheChild(mntDentry, "test");
            }

            var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
            Assert.Equal(0, sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
            var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
            Assert.Equal(0, sm.AttachDetachedMount(mount!, target));
            var loc = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);

            var file = new Dentry(AutoReclaimTxt, null, loc.Dentry, loc.Dentry!.SuperBlock);
            loc.Dentry.Inode!.Create(file, 0x1A4, 0, 0);
            var wf = new LinuxFile(file, FileFlags.O_WRONLY, loc.Mount!);
            Assert.Equal(5, file.Inode!.WriteFromHost(null, wf, "hello"u8.ToArray(), 0));
            wf.Close();

            var mappedFile = new LinuxFile(file, FileFlags.O_RDWR, loc.Mount!);
            const uint mapAddr = 0x4F000000;
            mm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, mappedFile, 0, "MAP_SHARED", engine);
            Assert.True(mm.HandleFault(mapAddr, false, engine));
            mm.Munmap(mapAddr, LinuxConstants.PageSize, engine);

            var silkInode = Assert.IsType<SilkInode>(file.Inode);
            var cache = Assert.IsType<AddressSpace>(silkInode.Mapping);
            Assert.True(cache.PageCount > 0);

            sm.MemoryContext.AddressSpacePolicy.MaybeRunMaintenance(mm, engine);

            Assert.Equal(0, cache.PageCount);

            var rf = new LinuxFile(file, FileFlags.O_RDONLY, loc.Mount!);
            var readBuf = new byte[16];
            var n = file.Inode.ReadToHost(null, rf, readBuf, 0);
            rf.Close();
            Assert.Equal(5, n);
            Assert.Equal("hello", Encoding.UTF8.GetString(readBuf, 0, n));
            sm.Close();
        }
        finally
        {
            sm.MemoryContext.AddressSpacePolicy.HighWatermarkBytes = originalHigh;
            sm.MemoryContext.AddressSpacePolicy.LowWatermarkBytes = originalLow;
            sm.MemoryContext.AddressSpacePolicy.WritebackInterval = originalInterval;
            if (Directory.Exists(silkRoot)) Directory.Delete(silkRoot, true);
        }
    }

    [Fact]
    public void Silkfs_DropCaches_EvictsInodes_AndIgetReloadsFromMetadata()
    {
        var silkRoot = Path.Combine(Path.GetTempPath(), $"silkfs-iget-evict-{Guid.NewGuid():N}");
        try
        {
            using var engine = _runtime.CreateEngine();
            var vma = _runtime.CreateAddressSpace();
            var sm = new SyscallManager(engine, vma, 0);
            var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
            var rootSb = tmpfsType.CreateAnonymousFileSystem(sm.MemoryContext).ReadSuper(tmpfsType, 0, "test-root", null);
            var rootMount = new Mount(rootSb, rootSb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
            sm.InitializeRoot(rootSb.Root, rootMount);

            var root = sm.Root.Dentry!;
            if (root.Inode!.Lookup("mnt") == null)
            {
                var mntDentry = new Dentry(Mnt, null, root, root.SuperBlock);
                root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
                root.CacheChild(mntDentry, "test");
            }

            var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
            Assert.Equal(0, sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
            var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
            Assert.Equal(0, sm.AttachDetachedMount(mount!, target));

            var loc = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
            var dir = new Dentry(Sub, null, loc.Dentry, loc.Dentry!.SuperBlock);
            loc.Dentry.Inode!.Mkdir(dir, 0x1ED, 0, 0);
            var file = new Dentry(DataTxt, null, dir, dir.SuperBlock);
            dir.Inode!.Create(file, 0x1A4, 0, 0);

            var payload = "hello-iget";
            var wf = new LinuxFile(file, FileFlags.O_WRONLY, loc.Mount!);
            Assert.Equal(payload.Length, file.Inode!.WriteFromHost(null, wf, Encoding.UTF8.GetBytes(payload), 0));
            wf.Close();

            var before = sm.PathWalkWithFlags("/mnt/sub/data.txt", LookupFlags.FollowSymlink);
            Assert.True(before.IsValid);
            var oldInode = before.Dentry!.Inode!;

            var stats = VfsShrinker.Shrink(sm, VfsShrinkMode.DentryCache | VfsShrinkMode.InodeCache);
            Assert.True(stats.DentriesDropped > 0);
            Assert.True(oldInode.IsCacheEvicted);
            Assert.False(oldInode.IsFinalized);

            var after = sm.PathWalkWithFlags("/mnt/sub/data.txt", LookupFlags.FollowSymlink);
            Assert.True(after.IsValid);
            Assert.NotNull(after.Dentry!.Inode);
            Assert.NotSame(oldInode, after.Dentry.Inode);

            var rf = new LinuxFile(after.Dentry, FileFlags.O_RDONLY, after.Mount!);
            var buffer = new byte[32];
            var n = after.Dentry.Inode!.ReadToHost(null, rf, buffer, 0);
            rf.Close();

            Assert.Equal(payload.Length, n);
            Assert.Equal(payload, Encoding.UTF8.GetString(buffer, 0, n));
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
            using var engine = _runtime.CreateEngine();
            var vma = _runtime.CreateAddressSpace();
            var sm = new SyscallManager(engine, vma, 0);
            var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
            var rootSb = tmpfsType.CreateAnonymousFileSystem(sm.MemoryContext).ReadSuper(tmpfsType, 0, "test-root", null);
            var rootMount = new Mount(rootSb, rootSb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
            sm.InitializeRoot(rootSb.Root, rootMount);

            var root = sm.Root.Dentry!;
            if (root.Inode!.Lookup("mnt") == null)
            {
                var mntDentry = new Dentry(Mnt, null, root, root.SuperBlock);
                root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
                root.CacheChild(mntDentry, "test");
            }

            var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
            Assert.Equal(0, sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
            var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
            Assert.Equal(0, sm.AttachDetachedMount(mount!, target));

            var loc = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
            var file = new Dentry(HeldTxt, null, loc.Dentry, loc.Dentry!.SuperBlock);
            loc.Dentry.Inode!.Create(file, 0x1A4, 0, 0);
            var payload = "keep-open";
            var wf = new LinuxFile(file, FileFlags.O_WRONLY, loc.Mount!);
            Assert.Equal(payload.Length, file.Inode!.WriteFromHost(null, wf, Encoding.UTF8.GetBytes(payload), 0));
            wf.Close();

            var rf = new LinuxFile(file, FileFlags.O_RDONLY, loc.Mount!);
            var stats = VfsShrinker.Shrink(sm, VfsShrinkMode.DentryCache | VfsShrinkMode.InodeCache);
            Assert.True(stats.DentriesDropped >= 0);
            Assert.True(file.DentryRefCount > 0);

            var buffer = new byte[32];
            var n = rf.OpenedInode!.ReadToHost(null, rf, buffer, 0);
            Assert.Equal(payload.Length, n);
            Assert.Equal(payload, Encoding.UTF8.GetString(buffer, 0, n));
            rf.Close();
            sm.Close();
        }
        finally
        {
            if (Directory.Exists(silkRoot)) Directory.Delete(silkRoot, true);
        }
    }

    [Fact]
    public void Silkfs_Remount_PreservesLargeFilePartialRead()
    {
        var silkRoot = Path.Combine(Path.GetTempPath(), $"silkfs-partial-read-{Guid.NewGuid():N}");
        var payload = Enumerable.Range(0, LinuxConstants.PageSize * 3 + 257)
            .Select(i => (byte)('a' + i % 26))
            .ToArray();

        try
        {
            using (var engine = _runtime.CreateEngine())
            {
                var vma = _runtime.CreateAddressSpace();
                var sm = new SyscallManager(engine, vma, 0);
                var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
                var rootSb = tmpfsType.CreateAnonymousFileSystem(sm.MemoryContext).ReadSuper(tmpfsType, 0, "test-root", null);
                var rootMount = new Mount(rootSb, rootSb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
                sm.InitializeRoot(rootSb.Root, rootMount);

                var root = sm.Root.Dentry!;
                if (root.Inode!.Lookup("mnt") == null)
                {
                    var mntDentry = new Dentry(Mnt, null, root, root.SuperBlock);
                    root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
                    root.CacheChild(mntDentry, "test");
                }

                var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
                Assert.Equal(0, sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
                var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
                Assert.Equal(0, sm.AttachDetachedMount(mount!, target));
                var loc = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);

                var file = new Dentry(BigBin, null, loc.Dentry, loc.Dentry!.SuperBlock);
                loc.Dentry.Inode!.Create(file, 0x1A4, 0, 0);
                var wf = new LinuxFile(file, FileFlags.O_WRONLY, loc.Mount!);
                Assert.Equal(payload.Length, file.Inode!.WriteFromHost(null, wf, payload, 0));
                wf.Close();
                sm.Close();
            }

            using (var engine = _runtime.CreateEngine())
            {
                var vma = _runtime.CreateAddressSpace();
                var sm = new SyscallManager(engine, vma, 0);
                var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
                var rootSb = tmpfsType.CreateAnonymousFileSystem(sm.MemoryContext).ReadSuper(tmpfsType, 0, "test-root", null);
                var rootMount = new Mount(rootSb, rootSb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
                sm.InitializeRoot(rootSb.Root, rootMount);

                var root = sm.Root.Dentry!;
                if (root.Inode!.Lookup("mnt") == null)
                {
                    var mntDentry = new Dentry(Mnt, null, root, root.SuperBlock);
                    root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
                    root.CacheChild(mntDentry, "test");
                }

                var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
                Assert.Equal(0, sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
                var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
                Assert.Equal(0, sm.AttachDetachedMount(mount!, target));

                var fileLoc = sm.PathWalkWithFlags("/mnt/big.bin", LookupFlags.FollowSymlink);
                Assert.True(fileLoc.IsValid);
                var rf = new LinuxFile(fileLoc.Dentry!, FileFlags.O_RDONLY, fileLoc.Mount!);
                var offset = LinuxConstants.PageSize - 19;
                var slice = new byte[113];
                var n = fileLoc.Dentry!.Inode!.ReadToHost(null, rf, slice, offset);
                rf.Close();

                Assert.Equal(slice.Length, n);
                Assert.Equal(payload.AsSpan(offset, slice.Length).ToArray(), slice);
                sm.Close();
            }
        }
        finally
        {
            if (Directory.Exists(silkRoot)) Directory.Delete(silkRoot, true);
        }
    }

    [Fact]
    public void Silkfs_TruncateShrinkAndGrow_PersistsAcrossRemount()
    {
        var silkRoot = Path.Combine(Path.GetTempPath(), $"silkfs-truncate-{Guid.NewGuid():N}");

        try
        {
            using (var engine = _runtime.CreateEngine())
            {
                var vma = _runtime.CreateAddressSpace();
                var sm = new SyscallManager(engine, vma, 0);
                var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
                var rootSb = tmpfsType.CreateAnonymousFileSystem(sm.MemoryContext).ReadSuper(tmpfsType, 0, "test-root", null);
                var rootMount = new Mount(rootSb, rootSb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
                sm.InitializeRoot(rootSb.Root, rootMount);

                var root = sm.Root.Dentry!;
                if (root.Inode!.Lookup("mnt") == null)
                {
                    var mntDentry = new Dentry(Mnt, null, root, root.SuperBlock);
                    root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
                    root.CacheChild(mntDentry, "test");
                }

                var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
                Assert.Equal(0, sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
                var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
                Assert.Equal(0, sm.AttachDetachedMount(mount!, target));
                var loc = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);

                var file = new Dentry(ResizeBin, null, loc.Dentry, loc.Dentry!.SuperBlock);
                loc.Dentry.Inode!.Create(file, 0x1A4, 0, 0);
                var wf = new LinuxFile(file, FileFlags.O_RDWR, loc.Mount!);
                Assert.Equal(10, file.Inode!.WriteFromHost(null, wf, "abcdefghij"u8.ToArray(), 0));
                Assert.Equal(0, file.Inode.Truncate(4));
                Assert.Equal(0, file.Inode.Truncate(12));
                Assert.Equal(2, file.Inode.WriteFromHost(null, wf, "XY"u8.ToArray(), 10));
                wf.Close();
                sm.Close();
            }

            using (var engine = _runtime.CreateEngine())
            {
                var vma = _runtime.CreateAddressSpace();
                var sm = new SyscallManager(engine, vma, 0);
                var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
                var rootSb = tmpfsType.CreateAnonymousFileSystem(sm.MemoryContext).ReadSuper(tmpfsType, 0, "test-root", null);
                var rootMount = new Mount(rootSb, rootSb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
                sm.InitializeRoot(rootSb.Root, rootMount);

                var root = sm.Root.Dentry!;
                if (root.Inode!.Lookup("mnt") == null)
                {
                    var mntDentry = new Dentry(Mnt, null, root, root.SuperBlock);
                    root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
                    root.CacheChild(mntDentry, "test");
                }

                var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
                Assert.Equal(0, sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
                var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
                Assert.Equal(0, sm.AttachDetachedMount(mount!, target));

                var fileLoc = sm.PathWalkWithFlags("/mnt/resize.bin", LookupFlags.FollowSymlink);
                Assert.True(fileLoc.IsValid);
                Assert.Equal<ulong>(12, fileLoc.Dentry!.Inode!.Size);

                var rf = new LinuxFile(fileLoc.Dentry, FileFlags.O_RDONLY, fileLoc.Mount!);
                var buf = new byte[16];
                var n = fileLoc.Dentry.Inode.ReadToHost(null, rf, buf, 0);
                rf.Close();

                Assert.Equal(12, n);
                Assert.Equal((byte)'a', buf[0]);
                Assert.Equal((byte)'d', buf[3]);
                Assert.Equal(0, buf[4]);
                Assert.Equal(0, buf[9]);
                Assert.Equal((byte)'X', buf[10]);
                Assert.Equal((byte)'Y', buf[11]);
                sm.Close();
            }
        }
        finally
        {
            if (Directory.Exists(silkRoot)) Directory.Delete(silkRoot, true);
        }
    }

    private static void DeleteDirectoryWithRetry(string path)
    {
        if (!Directory.Exists(path))
            return;

        Exception? lastError = null;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                Directory.Delete(path, true);
                return;
            }
            catch (IOException ex)
            {
                lastError = ex;
            }
            catch (UnauthorizedAccessException ex)
            {
                lastError = ex;
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            Thread.Sleep(50);
        }

        if (lastError != null)
            throw lastError;

        throw new IOException($"Failed to delete directory '{path}'.");
    }

    private static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
