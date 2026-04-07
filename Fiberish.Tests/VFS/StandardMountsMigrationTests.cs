using Fiberish.Core;
using Fiberish.Core.VFS.TTY;
using Fiberish.Memory;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.VFS;

public class StandardMountsMigrationTests
{
    [Fact]
    public void Close_ReleasesDevStdioBeforeUnmountingNamespace()
    {
        var strictBefore = VfsDebugTrace.StrictInvariants;
        var enabledBefore = VfsDebugTrace.Enabled;
        using var engine = new Engine();
        var vma = new VMAManager();
        var sm = new SyscallManager(engine, vma, 0);
        var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
        var rootSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "test-root", null);
        var rootMount = new Mount(rootSb, rootSb.Root)
        {
            Source = "tmpfs",
            FsType = "tmpfs",
            Options = "rw"
        };
        sm.InitializeRoot(rootSb.Root, rootMount);

        try
        {
            VfsDebugTrace.StrictInvariants = true;
            VfsDebugTrace.Enabled = false;

            sm.MountStandardDev();

            var stdin = sm.FDs[0];
            var ex = Record.Exception(() => sm.Close());

            Assert.Null(ex);
            Assert.Equal(0, stdin.Dentry.DentryRefCount);
        }
        finally
        {
            VfsDebugTrace.StrictInvariants = strictBefore;
            VfsDebugTrace.Enabled = enabledBefore;
            if (!sm.IsClosed)
                sm.Close();
        }
    }

    [Fact]
    public void StandardMounts_AttachThroughDetachedFlow_AndRemainAccessible()
    {
        using var engine = new Engine();
        var vma = new VMAManager();
        var sm = new SyscallManager(engine, vma, 0);
        var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
        var rootSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "test-root", null);
        var rootMount = new Mount(rootSb, rootSb.Root)
        {
            Source = "tmpfs",
            FsType = "tmpfs",
            Options = "rw"
        };
        sm.InitializeRoot(rootSb.Root, rootMount);

        try
        {
            sm.MountStandardDev();
            sm.MountStandardProc();
            sm.MountStandardShm();

            var devLoc = sm.PathWalkWithFlags("/dev", LookupFlags.FollowSymlink);
            Assert.True(devLoc.IsValid);
            Assert.Equal("devtmpfs", devLoc.Mount!.FsType);

            var procLoc = sm.PathWalkWithFlags("/proc", LookupFlags.FollowSymlink);
            Assert.True(procLoc.IsValid);
            Assert.Equal("proc", procLoc.Mount!.FsType);

            var ptsLoc = sm.PathWalkWithFlags("/dev/pts", LookupFlags.FollowSymlink);
            Assert.True(ptsLoc.IsValid);
            Assert.Equal("devpts", ptsLoc.Mount!.FsType);
            Assert.Contains("gid=5,mode=620", ptsLoc.Mount.Options);

            var shmLoc = sm.PathWalkWithFlags("/dev/shm", LookupFlags.FollowSymlink);
            Assert.True(shmLoc.IsValid);
            Assert.Equal("tmpfs", shmLoc.Mount!.FsType);
            Assert.Contains("nosuid", shmLoc.Mount.Options);
            Assert.Contains("nodev", shmLoc.Mount.Options);
            Assert.NotSame(sm.MemfdSuperBlock, shmLoc.Mount.SB);
            Assert.Same(sm.DevShmRoot, shmLoc.Dentry);
        }
        finally
        {
            sm.Close();
        }
    }

    [Fact]
    public void DevPts_IsolatedPerSyscallManagerInstance()
    {
        using var engine1 = new Engine();
        using var engine2 = new Engine();
        var sm1 = new SyscallManager(engine1, new VMAManager(), 0);
        sm1.PtyManager.BindScheduler(new KernelScheduler());
        var sm2 = new SyscallManager(engine2, new VMAManager(), 0);
        sm2.PtyManager.BindScheduler(new KernelScheduler());
        var tmpfsType = FileSystemRegistry.Get("tmpfs")!;

        var rootSb1 = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "test-root-1", null);
        var rootMount1 = new Mount(rootSb1, rootSb1.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
        sm1.InitializeRoot(rootSb1.Root, rootMount1);

        var rootSb2 = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "test-root-2", null);
        var rootMount2 = new Mount(rootSb2, rootSb2.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
        sm2.InitializeRoot(rootSb2.Root, rootMount2);

        try
        {
            sm1.MountStandardDev();
            sm2.MountStandardDev();

            var ptmx1 = sm1.PathWalkWithFlags("/dev/ptmx", LookupFlags.FollowSymlink);
            var ptmx2 = sm2.PathWalkWithFlags("/dev/ptmx", LookupFlags.FollowSymlink);
            Assert.True(ptmx1.IsValid);
            Assert.True(ptmx2.IsValid);

            var file1 = new LinuxFile(ptmx1.Dentry!, FileFlags.O_RDWR, ptmx1.Mount!);
            var file2 = new LinuxFile(ptmx2.Dentry!, FileFlags.O_RDWR, ptmx2.Mount!);

            try
            {
                var pair1 = Assert.IsType<PtyPair>(file1.PrivateData);
                var pair2 = Assert.IsType<PtyPair>(file2.PrivateData);

                Assert.Equal(0, pair1.Index);
                Assert.Equal(0, pair2.Index);
            }
            finally
            {
                file1.Close();
                file2.Close();
            }
        }
        finally
        {
            sm1.Close();
            sm2.Close();
        }
    }
}
