using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Syscalls;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public class SyscallManagerCloneTests
{
    [Fact]
    public void Clone_ShouldPreserveAnonMount()
    {
        var engine = new Engine();
        var vma = new VMAManager();
        var sm = new SyscallManager(engine, vma, 0);
        sm.MountRootHostfs(".");

        var cloned = sm.Clone(vma, shareFiles: false);

        Assert.NotNull(cloned.AnonMount);
        Assert.Same(sm.AnonMount, cloned.AnonMount);
    }

    [Fact]
    public void ClosingClonedSyscallManager_MustNotDetachParentProcMount()
    {
        var engine = new Engine();
        var vma = new VMAManager();
        var sm = new SyscallManager(engine, vma, 0);
        sm.MountRootHostfs(".");
        sm.MountStandardProc();

        var procBefore = sm.PathWalkWithFlags("/proc", LookupFlags.FollowSymlink);
        Assert.True(procBefore.IsValid);
        Assert.Equal("proc", procBefore.Mount!.FsType);

        var cloned = sm.Clone(vma, shareFiles: false);
        cloned.Close();

        var procAfter = sm.PathWalkWithFlags("/proc", LookupFlags.FollowSymlink);
        Assert.True(procAfter.IsValid);
        Assert.Equal("proc", procAfter.Mount!.FsType);

        sm.Close();
    }
}
