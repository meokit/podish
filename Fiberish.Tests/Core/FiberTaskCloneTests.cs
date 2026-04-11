using System.Buffers.Binary;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
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
    public async Task Fork_ChildPrivateMapping_PreservesMappedExternalPages()
    {
        using var env = new TestEnv();
        const uint addr = 0x00410000;
        env.MapUserPage(addr);
        Assert.True(env.Engine.CopyToUser(addr, new byte[] { 0x5A }));

        var pageAddr = addr & LinuxConstants.PageMask;
        Assert.True(env.Vma.PageMapping.TryGet(pageAddr, out _));

        var child = await env.Parent.Clone(0, 0, 0, 0, 0); // fork
        var childMm = child.Process.Mem;
        Assert.True(child.CPU.HasMappedPage(addr, LinuxConstants.PageSize));
        Assert.True(childMm.PageMapping.TryGet(pageAddr, out _));

        var childRead = new byte[1];
        Assert.True(child.CPU.CopyFromUser(addr, childRead));
        Assert.Equal((byte)0x5A, childRead[0]);
    }

    [Fact]
    public async Task Fork_PrivateFileDirtyPage_PreservesNativeMappings_AndBytes()
    {
        using var env = new TestEnv();
        const uint addr = 0x00500000;
        env.MapPrivateFilePage(addr, (byte)'A');

        var read = new byte[1];
        Assert.True(env.Engine.CopyFromUser(addr, read));
        Assert.Equal((byte)'A', read[0]);

        Assert.True(env.Engine.CopyToUser(addr, new[] { (byte)'Z' }));
        Assert.True(env.Engine.HasMappedPage(addr, LinuxConstants.PageSize));

        var child = await env.Parent.Clone(0, 0, 0, 0, 0); // fork
        var childMm = child.Process.Mem;
        var pageAddr = addr & LinuxConstants.PageMask;

        Assert.True(child.CPU.HasMappedPage(addr, LinuxConstants.PageSize));
        Assert.True(childMm.PageMapping.TryGet(pageAddr, out _));

        var childRead = new byte[1];
        Assert.True(child.CPU.CopyFromUser(addr, childRead));
        Assert.Equal((byte)'Z', childRead[0]);
    }

    [Fact]
    public async Task Fork_PrivateAnonymousPage_PreservesMappings_ThenSplitsOnWrite()
    {
        using var env = new TestEnv();
        const uint addr = 0x00510000;
        env.MapUserPage(addr);
        Assert.True(env.Engine.CopyToUser(addr, new byte[] { 0x5A }));

        Assert.True(env.Engine.HasMappedPage(addr, LinuxConstants.PageSize));

        var child = await env.Parent.Clone(0, 0, 0, 0, 0); // fork
        var childMm = child.Process.Mem;
        var pageAddr = addr & LinuxConstants.PageMask;

        Assert.True(env.Engine.HasMappedPage(addr, LinuxConstants.PageSize));
        Assert.True(child.CPU.HasMappedPage(addr, LinuxConstants.PageSize));
        Assert.True(env.Vma.PageMapping.TryGet(pageAddr, out _));
        Assert.True(childMm.PageMapping.TryGet(pageAddr, out _));

        var parentRead = new byte[1];
        var childRead = new byte[1];
        Assert.True(env.Engine.CopyFromUser(addr, parentRead));
        Assert.True(child.CPU.CopyFromUser(addr, childRead));
        Assert.Equal((byte)0x5A, parentRead[0]);
        Assert.Equal((byte)0x5A, childRead[0]);

        Assert.True(child.CPU.CopyToUser(addr, new byte[] { 0x43 }));

        Assert.True(env.Engine.CopyFromUser(addr, parentRead));
        Assert.True(child.CPU.CopyFromUser(addr, childRead));
        Assert.Equal((byte)0x5A, parentRead[0]);
        Assert.Equal((byte)0x43, childRead[0]);
    }

    [Fact]
    public async Task Fork_SequentialChildren_WritingPrivateAnonymousPage_DoesNotCorruptParent()
    {
        using var env = new TestEnv();
        const uint addr = 0x00520000;
        env.MapUserPage(addr);
        Assert.True(env.Engine.CopyToUser(addr, new byte[] { 0x11 }));

        var probe = new byte[1];

        var child1 = await env.Parent.Clone(0, 0, 0, 0, 0);
        Assert.True(child1.CPU.CopyToUser(addr, new byte[] { 0x22 }));
        Assert.True(env.Engine.CopyFromUser(addr, probe));
        Assert.Equal((byte)0x11, probe[0]);

        var child2 = await env.Parent.Clone(0, 0, 0, 0, 0);
        Assert.True(child2.CPU.CopyToUser(addr, new byte[] { 0x33 }));
        Assert.True(env.Engine.CopyFromUser(addr, probe));
        Assert.Equal((byte)0x11, probe[0]);

        Assert.True(child1.CPU.CopyFromUser(addr, probe));
        Assert.Equal((byte)0x22, probe[0]);
        Assert.True(child2.CPU.CopyFromUser(addr, probe));
        Assert.Equal((byte)0x33, probe[0]);
    }

    private sealed class TestEnv : IDisposable
    {
        private readonly List<LinuxFile> _files = [];

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

        public void Dispose()
        {
            foreach (var file in _files) file.Close();
            GC.KeepAlive(Parent);
        }

        public void MapUserPage(uint addr)
        {
            Vma.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[test]", Engine);
            Assert.True(Vma.HandleFault(addr, true, Engine));
        }

        public void MapPrivateFilePage(uint addr, byte initialByte)
        {
            var fsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
            var sb = fsType.CreateAnonymousFileSystem().ReadSuper(fsType, 0, "clone-private-file", null);
            var mount = new Mount(sb, sb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
            var dentry = new Dentry(FsName.FromString("clone.bin"), null, sb.Root, sb);
            sb.Root.Inode!.Create(dentry, 0x1A4, 0, 0);
            var file = new LinuxFile(dentry, FileFlags.O_RDWR, mount);
            _files.Add(file);

            Assert.Equal(1, dentry.Inode!.WriteFromHost(null, file, new[] { initialByte }, 0));
            Vma.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed, file, 0, "[private-file]", Engine);
        }
    }
}