using System.Reflection;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.VFS;

public class FileLockReviewRegressionTests
{
    // Review #5: data copy-up must not change the identity used by any lock flavor.
    [Theory]
    [InlineData("flock")]
    [InlineData("posix")]
    [InlineData("ofd")]
    public async Task Overlay_CopyUp_PreservesLockConflictsAndUnlocks(string kind)
    {
        var lower = CreateLower();
        var sb = CreateOverlay(lower);
        var dentry = sb.Root.Inode!.Lookup("file")!;
        var inode = Assert.IsType<OverlayInode>(dentry.Inode);
        using var first = new LinuxFile(dentry, FileFlags.O_RDWR, null);
        using var second = new LinuxFile(dentry, FileFlags.O_RDWR, null);
        var owner = new object();
        var before = inode.GetOrCreateFileLockState();
        Assert.Equal(0, await Acquire(kind, inode, first, owner));
        Assert.Null(inode.UpperDentry);
        Assert.Equal(1, inode.WriteFromHost(null, first, new byte[] { 42 }, 0));
        Assert.NotNull(inode.UpperDentry);
        Assert.Same(before, inode.GetOrCreateFileLockState());
        Assert.Equal(-(int)Errno.EAGAIN, await Acquire(kind, inode, second, new object()));
        if (kind == "flock")
            Assert.Equal(0, inode.Flock(first, LinuxConstants.LOCK_UN));
        else
            Assert.Equal(0, await before.SetLockAsync(kind == "ofd" ? first : owner, 100,
                FileLockState.F_UNLCK, 0, 9, false, null));
        Assert.Equal(0, await Acquire(kind, inode, second, new object()));
    }

    [Fact]
    public void Overlay_IndependentOverlaysDoNotShareLocksThroughTheLowerLayer()
    {
        var lower = CreateLower();
        var firstSb = CreateOverlay(lower);
        var secondSb = CreateOverlay(lower);
        var firstDentry = firstSb.Root.Inode!.Lookup("file")!;
        var secondDentry = secondSb.Root.Inode!.Lookup("file")!;
        using var first = new LinuxFile(firstDentry, FileFlags.O_RDWR, null);
        using var second = new LinuxFile(secondDentry, FileFlags.O_RDWR, null);
        Assert.Equal(0, firstDentry.Inode!.Flock(first, LinuxConstants.LOCK_EX | LinuxConstants.LOCK_NB));
        Assert.Equal(0, secondDentry.Inode!.Flock(second, LinuxConstants.LOCK_EX | LinuxConstants.LOCK_NB));
    }

    [Fact]
    public void Overlay_CopyUp_NewUpperOnlyAliasFindsTheOriginalLocks()
    {
        var sb = CreateOverlay(CreateLower());
        var original = sb.Root.Inode!.Lookup("file")!;
        var inode = Assert.IsType<OverlayInode>(original.Inode);
        using var first = new LinuxFile(original, FileFlags.O_RDWR, null);
        Assert.Equal(0, inode.Flock(first, LinuxConstants.LOCK_EX | LinuxConstants.LOCK_NB));
        Assert.Equal(1, inode.WriteFromHost(null, first, new byte[] { 42 }, 0));
        var aliasInode = new OverlayInode(sb, (Dentry?)null, inode.UpperDentry);
        var alias = new Dentry(FsName.FromString("alias"), aliasInode, sb.Root, sb);
        using var second = new LinuxFile(alias, FileFlags.O_RDWR, null);
        Assert.Same(inode.GetOrCreateFileLockState(), aliasInode.GetOrCreateFileLockState());
        Assert.Equal(-(int)Errno.EAGAIN, aliasInode.Flock(second, LinuxConstants.LOCK_EX | LinuxConstants.LOCK_NB));
        Assert.Equal(0, inode.Flock(first, LinuxConstants.LOCK_UN));
        Assert.Equal(0, aliasInode.Flock(second, LinuxConstants.LOCK_EX | LinuxConstants.LOCK_NB));
    }

    // Review #7: two hostfs mounts of a host file have distinct guest inode objects.
    [Theory]
    [InlineData("flock")]
    [InlineData("posix")]
    [InlineData("ofd")]
    public async Task Hostfs_SeparateMounts_CoordinateLocksByHostFileIdentity(string kind)
    {
        var root = TestWorkspace.CreateUniqueDirectory("host-lock-review-");
        try
        {
            File.WriteAllText(Path.Combine(root, "file"), "data");
            var type = new FileSystemType { Name = "hostfs" };
            var firstSb = new HostSuperBlock(type, root, HostfsMountOptions.Parse("rw"));
            var secondSb = new HostSuperBlock(type, root, HostfsMountOptions.Parse("rw"));
            firstSb.Root = firstSb.GetDentry(root, FsName.Empty, null)!;
            secondSb.Root = secondSb.GetDentry(root, FsName.Empty, null)!;
            var firstDentry = firstSb.Root.Inode!.Lookup("file")!;
            var secondDentry = secondSb.Root.Inode!.Lookup("file")!;
            Assert.NotSame(firstDentry.Inode, secondDentry.Inode);
            using var first = new LinuxFile(firstDentry, FileFlags.O_RDWR, null);
            using var second = new LinuxFile(secondDentry, FileFlags.O_RDWR, null);
            var owner = new object();
            Assert.Equal(0, await Acquire(kind, firstDentry.Inode!, first, owner));
            Assert.Same(firstDentry.Inode!.GetOrCreateFileLockState(), secondDentry.Inode!.GetOrCreateFileLockState());
            Assert.Equal(-(int)Errno.EAGAIN, await Acquire(kind, secondDentry.Inode, second, new object()));
            if (kind == "posix")
                secondDentry.Inode.GetOrCreateFileLockState().ReleasePosixLocks(owner);
            else
                firstDentry.Inode.ReleaseFileDescriptorLocks(first);
            Assert.Equal(0, await Acquire(kind, secondDentry.Inode, second, new object()));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static ValueTask<int> Acquire(string kind, Inode inode, LinuxFile file, object owner)
    {
        if (kind == "flock")
            return ValueTask.FromResult(inode.Flock(file, LinuxConstants.LOCK_EX | LinuxConstants.LOCK_NB));
        return inode.GetOrCreateFileLockState().SetLockAsync(kind == "ofd" ? file : owner, 100,
            FileLockState.F_WRLCK, 0, 9, false, null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedLockState_WaitersOnDifferentSchedulers_AreBothWoken(bool record)
    {
        var runtime = new TestRuntimeFactory();
        using var firstCpu = runtime.CreateEngine();
        using var secondCpu = runtime.CreateEngine();
        var firstScheduler = new KernelScheduler();
        var secondScheduler = new KernelScheduler();
        var firstTask = new FiberTask(100, runtime.CreateOwnedProcess(100), firstCpu, firstScheduler);
        var secondTask = new FiberTask(200, runtime.CreateOwnedProcess(200), secondCpu, secondScheduler);
        var lower = CreateLower();
        var dentry = lower.Root.Inode!.Lookup("file")!;
        using var holder = new LinuxFile(dentry, FileFlags.O_RDONLY, null);
        using var first = new LinuxFile(dentry, FileFlags.O_RDONLY, null);
        using var second = new LinuxFile(dentry, FileFlags.O_RDONLY, null);
        var state = new FileLockState();
        var owner = new object();
        if (record)
            Assert.Equal(0, await state.SetLockAsync(owner, 1, FileLockState.F_WRLCK, 0, 9, false, null));
        else
            Assert.Equal(0, await state.FlockAsync(holder, null, LinuxConstants.LOCK_EX));
        var firstWait = (record
            ? state.SetLockAsync(firstTask.Process, 100, FileLockState.F_RDLCK, 0, 9, true, firstTask)
            : state.FlockAsync(first, firstTask, LinuxConstants.LOCK_SH)).AsTask();
        var secondWait = (record
            ? state.SetLockAsync(secondTask.Process, 200, FileLockState.F_RDLCK, 0, 9, true, secondTask)
            : state.FlockAsync(second, secondTask, LinuxConstants.LOCK_SH)).AsTask();
        Assert.False(firstWait.IsCompleted);
        Assert.False(secondWait.IsCompleted);
        if (record)
            Assert.Equal(0, await state.SetLockAsync(owner, 1, FileLockState.F_UNLCK, 0, 9, false, null));
        else
            Assert.Equal(0, await state.FlockAsync(holder, null, LinuxConstants.LOCK_UN));
        var drain = typeof(KernelScheduler).GetMethod("DrainEvents", BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (var i = 0; i < 50 && (!firstWait.IsCompleted || !secondWait.IsCompleted); i++)
        {
            drain.Invoke(firstScheduler, null);
            drain.Invoke(secondScheduler, null);
            await Task.Delay(1);
        }
        Assert.True(firstWait.IsCompleted);
        Assert.True(secondWait.IsCompleted);
        Assert.Equal(0, await firstWait);
        Assert.Equal(0, await secondWait);
    }

    private static SuperBlock CreateLower()
    {
        return new LayerFileSystem().ReadSuper(new FileSystemType { Name = "layerfs" }, 0, "review-lower",
            new LayerMountOptions { Root = LayerNode.Directory("/").AddChild(LayerNode.File("file", [])) });
    }

    private static OverlaySuperBlock CreateOverlay(SuperBlock lower)
    {
        var upper = new Tmpfs().ReadSuper(new FileSystemType { Name = "tmpfs" }, 0, "review-upper", null);
        return (OverlaySuperBlock)new OverlayFileSystem().ReadSuper(new FileSystemType { Name = "overlay" },
            0, "review-overlay", new OverlayMountOptions { Lower = lower, Upper = upper });
    }
}
