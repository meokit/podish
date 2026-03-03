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
}
