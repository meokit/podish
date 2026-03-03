using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.VFS;

public class StandardMountsMigrationTests
{
    [Fact]
    public void StandardMounts_AttachThroughDetachedFlow_AndRemainAccessible()
    {
        using var engine = new Engine();
        var vma = new VMAManager();
        var sm = new SyscallManager(engine, vma, 0);
        var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
        var rootSb = tmpfsType.FileSystem.ReadSuper(tmpfsType, 0, "test-root", null);
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
        }
        finally
        {
            sm.Close();
        }
    }
}
