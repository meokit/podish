using System.Buffers.Binary;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Xunit;

namespace Fiberish.Tests.Core;

public class FiberTaskCloneTests
{
    [Fact]
    public async Task Clone_WithCloneParentSetTid_WritesChildTidToParentAddress()
    {
        using var env = new TestEnv();
        const uint ptidPtr = 0x00400000;
        env.MapUserPage(ptidPtr);

        var flags = (int)(LinuxConstants.CLONE_VM | LinuxConstants.CLONE_PARENT_SETTID);
        var child = await env.Parent.Clone(flags, 0, ptidPtr, 0, 0);

        var tidBuf = new byte[4];
        Assert.True(env.Engine.CopyFromUser(ptidPtr, tidBuf));
        Assert.Equal(child.TID, BinaryPrimitives.ReadInt32LittleEndian(tidBuf));
    }

    [Fact]
    public async Task Fork_ChildPrivateMapping_RefaultsIntoChildExternalPages()
    {
        using var env = new TestEnv();
        const uint addr = 0x00410000;
        env.MapUserPage(addr);
        Assert.True(env.Engine.CopyToUser(addr, new byte[] { 0x5A }));

        var pageAddr = addr & LinuxConstants.PageMask;
        Assert.True(env.Vma.ExternalPages.TryGet(pageAddr, out _));

        var child = await env.Parent.Clone(0, 0, 0, 0, 0); // fork
        var childMm = child.Process.Mem;
        Assert.False(childMm.ExternalPages.TryGet(pageAddr, out _));

        var childRead = new byte[1];
        Assert.True(child.CPU.CopyFromUser(addr, childRead));
        Assert.Equal((byte)0x5A, childRead[0]);
        Assert.True(childMm.ExternalPages.TryGet(pageAddr, out _));
    }

    private sealed class TestEnv : IDisposable
    {
        public TestEnv()
        {
            Engine = new Engine();
            Vma = new VMAManager();
            SyscallManager = new SyscallManager(Engine, Vma, 0);
            SyscallManager.MountRootHostfs(".");
            Process = new Process(100, Vma, SyscallManager);
            Scheduler = new KernelScheduler();
            Parent = new FiberTask(100, Process, Engine, Scheduler);
            Engine.Owner = Parent;
        }

        public Engine Engine { get; }
        public VMAManager Vma { get; }
        public SyscallManager SyscallManager { get; }
        public Process Process { get; }
        public KernelScheduler Scheduler { get; }
        public FiberTask Parent { get; }

        public void MapUserPage(uint addr)
        {
            Vma.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, LinuxConstants.PageSize, "[test]",
                Engine);
            Assert.True(Vma.HandleFault(addr, true, Engine));
        }

        public void Dispose()
        {
            GC.KeepAlive(Parent);
        }
    }
}
