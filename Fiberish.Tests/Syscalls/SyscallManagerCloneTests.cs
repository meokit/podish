using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public class SyscallManagerCloneTests
{
    [Fact]
    public void Clone_ShouldPreserveAnonMount()
    {
        var runtime = new TestRuntimeFactory();
        using var engine = runtime.CreateEngine();
        var vma = runtime.CreateAddressSpace();
        var sm = new SyscallManager(engine, vma, 0);
        sm.MountRootHostfs(".");

        var cloned = sm.Clone(vma, false, false);

        Assert.NotNull(cloned.AnonMount);
        Assert.Same(sm.AnonMount, cloned.AnonMount);
    }

    [Fact]
    public void ClosingClonedSyscallManager_MustNotDetachParentProcMount()
    {
        var runtime = new TestRuntimeFactory();
        using var engine = runtime.CreateEngine();
        var vma = runtime.CreateAddressSpace();
        var sm = new SyscallManager(engine, vma, 0);
        sm.MountRootHostfs(".");
        sm.MountStandardProc();

        var procBefore = sm.PathWalkWithFlags("/proc", LookupFlags.FollowSymlink);
        Assert.True(procBefore.IsValid);
        Assert.Equal("proc", procBefore.Mount!.FsType);

        var cloned = sm.Clone(vma, false, false);
        cloned.Close();

        var procAfter = sm.PathWalkWithFlags("/proc", LookupFlags.FollowSymlink);
        Assert.True(procAfter.IsValid);
        Assert.Equal("proc", procAfter.Mount!.FsType);

        sm.Close();
    }

    [Fact]
    public void Clone_WithShareFiles_CloseOneOwner_MustKeepFdTableAlive()
    {
        var runtime = new TestRuntimeFactory();
        using var engine = runtime.CreateEngine();
        var vma = runtime.CreateAddressSpace();
        var sm = new SyscallManager(engine, vma, 0);
        sm.MountRootHostfs(".");

        var root = sm.PathWalk("/");
        Assert.True(root.IsValid);
        var file = new LinuxFile(root.Dentry!, FileFlags.O_RDONLY, root.Mount!);
        var fd = sm.AllocFD(file);

        var shared = sm.Clone(vma, true, false);
        Assert.NotNull(shared.GetFD(fd));

        shared.Close();

        Assert.NotNull(sm.GetFD(fd));

        sm.Close();
        Assert.Null(sm.GetFD(fd));
    }

    [Fact]
    public void Clone_MustShareDeviceNumberAllocator()
    {
        var runtime = new TestRuntimeFactory();
        using var engine = runtime.CreateEngine();
        var vma = runtime.CreateAddressSpace();
        var sm = new SyscallManager(engine, vma, 0);

        var cloned = sm.Clone(vma, false, false);

        var tmpFsType = FileSystemRegistry.Get("tmpfs")!;
        var sb1 = tmpFsType.CreateFileSystem(sm.DeviceNumbers).ReadSuper(tmpFsType, 0, "tmp-a", null);
        var sb2 = tmpFsType.CreateFileSystem(cloned.DeviceNumbers).ReadSuper(tmpFsType, 0, "tmp-b", null);

        Assert.NotEqual(0u, sb1.Dev);
        Assert.NotEqual(0u, sb2.Dev);
        Assert.NotEqual(sb1.Dev, sb2.Dev);
        Assert.Same(sm.DeviceNumbers, cloned.DeviceNumbers);
    }
}
