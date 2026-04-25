using System.Buffers.Binary;
using System.Reflection;
using Fiberish.Auth.Cred;
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
    public async Task Fork_DontForkMapping_IsOmittedFromChildAndNativeMmu()
    {
        using var env = new TestEnv();
        const uint baseAddr = 0x00518000;
        var len = (uint)(LinuxConstants.PageSize * 2);

        Assert.Equal(baseAddr, env.Vma.Mmap(baseAddr, len, Protection.Read | Protection.Write,
            MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[dontfork]", env.Engine));
        Assert.True(env.Vma.HandleFault(baseAddr, true, env.Engine));
        Assert.True(env.Vma.HandleFault(baseAddr + LinuxConstants.PageSize, true, env.Engine));
        Assert.True(env.Engine.CopyToUser(baseAddr, new byte[] { 0xA1 }));
        Assert.True(env.Engine.CopyToUser(baseAddr + LinuxConstants.PageSize, new byte[] { 0xB2 }));

        Assert.Equal(0, await env.Call("SysMadvise", baseAddr, LinuxConstants.PageSize, 10));

        var parentVmas = env.Vma.VMAs
            .Where(v => v.Start >= baseAddr && v.End <= baseAddr + len)
            .OrderBy(v => v.Start)
            .ToArray();
        Assert.Equal(2, parentVmas.Length);
        Assert.True(parentVmas[0].DontFork);
        Assert.False(parentVmas[1].DontFork);

        var child = await env.Parent.Clone(0, 0, 0, 0, 0);

        Assert.Null(child.Process.Mem.FindVmArea(baseAddr));
        Assert.False(child.CPU.HasMappedPage(baseAddr, LinuxConstants.PageSize));

        var probe = new byte[1];
        Assert.False(child.CPU.CopyFromUser(baseAddr, probe));
        Assert.True(child.CPU.CopyFromUser(baseAddr + LinuxConstants.PageSize, probe));
        Assert.Equal((byte)0xB2, probe[0]);
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

    [Fact]
    public async Task Fork_CopiesCredentialsAndClearsRootCapabilitiesAfterSetUid()
    {
        using var env = new TestEnv();
        Assert.Equal(0, CredentialService.SetUid(env.Process, 1000));

        var child = await env.Parent.Clone(0, 0, 0, 0, 0);

        Assert.Equal(1000, child.Process.UID);
        Assert.Equal(1000, child.Process.EUID);
        Assert.Equal(1000, child.Process.FSUID);
        Assert.False(child.Process.HasEffectiveCapability(Process.CapabilitySysAdmin));
    }

    [Fact]
    public async Task Fork_PreservesSupplementaryGroups()
    {
        using var env = new TestEnv();
        Assert.Equal(0, CredentialService.SetGroups(env.Process, [10, 20, 30]));

        var child = await env.Parent.Clone(0, 0, 0, 0, 0);

        Assert.Equal([10, 20, 30], child.Process.SupplementaryGroups);
    }

    private sealed class TestEnv : IDisposable
    {
        private readonly List<LinuxFile> _files = [];
        private readonly TestRuntimeFactory _runtime = new();

        public TestEnv()
        {
            Engine = _runtime.CreateEngine();
            Vma = _runtime.CreateAddressSpace();
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

        public async ValueTask<int> Call(string methodName, uint a1 = 0, uint a2 = 0, uint a3 = 0, uint a4 = 0,
            uint a5 = 0, uint a6 = 0)
        {
            var method = typeof(SyscallManager).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            var task = (ValueTask<int>)method!.Invoke(SyscallManager, [Engine, a1, a2, a3, a4, a5, a6])!;
            return await task;
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
