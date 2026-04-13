using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.VFS;

public class SparseWriteBackfillTests
{
    [Fact]
    public void Silkfs_SparseBackfillWrite_DoesNotSurfaceAsEnomem()
    {
        var silkRoot = Path.Combine(Path.GetTempPath(), $"silkfs-sparse-backfill-{Guid.NewGuid():N}");

        try
        {
            var runtime = new TestRuntimeFactory();
            using var engine = runtime.CreateEngine();
            var mm = runtime.CreateAddressSpace();
            var sm = CreateTmpfsRoot(engine, mm);
            var mountLoc = MountSilkfs(sm, silkRoot);

            var fileDentry = new Dentry(FsName.FromString("data.bin"), null, mountLoc.Dentry, mountLoc.Dentry!.SuperBlock);
            Assert.Equal(0, mountLoc.Dentry.Inode!.Create(fileDentry, 0x1A4, 0, 0));

            var file = new LinuxFile(fileDentry, FileFlags.O_RDWR, mountLoc.Mount!);
            fileDentry.Inode!.Open(file);

            try
            {
                Assert.Equal(2, fileDentry.Inode.WriteFromHost(null, file, "p3"u8.ToArray(),
                    3L * LinuxConstants.PageSize));

                // This page was never materialized and still reads as a hole until writeback runs.
                Assert.Equal(2, fileDentry.Inode.WriteFromHost(null, file, "p2"u8.ToArray(),
                    2L * LinuxConstants.PageSize));
            }
            finally
            {
                file.Close();
            }

            var readFile = new LinuxFile(fileDentry, FileFlags.O_RDONLY, mountLoc.Mount!);
            fileDentry.Inode.Open(readFile);
            try
            {
                var page2 = new byte[2];
                var page3 = new byte[2];
                Assert.Equal(2, fileDentry.Inode.ReadToHost(null, readFile, page2, 2L * LinuxConstants.PageSize));
                Assert.Equal(2, fileDentry.Inode.ReadToHost(null, readFile, page3, 3L * LinuxConstants.PageSize));
                Assert.Equal("p2"u8.ToArray(), page2);
                Assert.Equal("p3"u8.ToArray(), page3);
            }
            finally
            {
                readFile.Close();
                sm.Close();
            }
        }
        finally
        {
            if (Directory.Exists(silkRoot))
                Directory.Delete(silkRoot, true);
        }
    }

    private static SyscallManager CreateTmpfsRoot(Engine engine, VMAManager mm)
    {
        var sm = new SyscallManager(engine, mm, 0);
        var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
        var rootSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "test-root", null);
        var rootMount = new Mount(rootSb, rootSb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
        sm.InitializeRoot(rootSb.Root, rootMount);

        var root = sm.Root.Dentry!;
        if (root.Inode!.Lookup("mnt") == null)
        {
            var mntDentry = new Dentry(FsName.FromString("mnt"), null, root, root.SuperBlock);
            root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
            root.CacheChild(mntDentry, "test");
        }

        return sm;
    }

    private static PathLocation MountSilkfs(SyscallManager sm, string silkRoot)
    {
        var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
        Assert.Equal(0, sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
        var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
        Assert.Equal(0, sm.AttachDetachedMount(mount!, target));
        return sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
    }
}
