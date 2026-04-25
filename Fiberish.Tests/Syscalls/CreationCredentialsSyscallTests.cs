using System.Reflection;
using System.Text;
using Fiberish.Auth.Cred;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public sealed class CreationCredentialsSyscallTests
{
    [Fact]
    public async Task OpenCreate_UsesFsUidAndFsGidForNewInodeOwnership()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x10000u);
        env.WriteCString(0x10000u, "/owned");

        Assert.Equal(0, CredentialService.SetFsUid(env.Process, 1234));
        Assert.Equal(0, CredentialService.SetFsGid(env.Process, 2345));

        var fd = await env.Call("SysOpen", 0x10000u, (uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT), 0x1A4u);
        Assert.True(fd >= 0);

        var inode = env.ResolveInode("/owned");
        Assert.Equal(1234, inode.Uid);
        Assert.Equal(2345, inode.Gid);
    }

    [Fact]
    public async Task CreateInsideSetgidDirectory_InheritsDirectoryGroupAndBit()
    {
        using var env = new TestEnv();
        env.MapUserPage(0x11000u);
        env.MapUserPage(0x12000u);
        env.MapUserPage(0x13000u);
        env.WriteCString(0x11000u, "/parent");
        env.WriteCString(0x12000u, "/parent/file");
        env.WriteCString(0x13000u, "/parent/child");

        Assert.Equal(0, CredentialService.SetFsUid(env.Process, 1000));
        Assert.Equal(0, CredentialService.SetFsGid(env.Process, 4321));
        Assert.Equal(0, await env.Call("SysMkdir", 0x11000u, 0x5FDu)); // 02775

        Assert.Equal(4321, CredentialService.SetFsGid(env.Process, 9999));

        var fileFd = await env.Call("SysOpen", 0x12000u, (uint)(FileFlags.O_WRONLY | FileFlags.O_CREAT), 0x1B6u);
        Assert.True(fileFd >= 0);
        Assert.Equal(0, await env.Call("SysMkdir", 0x13000u, 0x1FDu));

        var fileInode = env.ResolveInode("/parent/file");
        var childDir = env.ResolveInode("/parent/child");

        Assert.Equal(1000, fileInode.Uid);
        Assert.Equal(4321, fileInode.Gid);
        Assert.Equal(1000, childDir.Uid);
        Assert.Equal(4321, childDir.Gid);
        Assert.True((childDir.Mode & 0x400) != 0);
    }

    private sealed class TestEnv : IDisposable
    {
        private readonly TestRuntimeFactory _runtime = new();

        public TestEnv()
        {
            Engine = _runtime.CreateEngine();
            Vma = _runtime.CreateAddressSpace();
            SyscallManager = new SyscallManager(Engine, Vma, 0);
            var tmpfsType = new FileSystemType
            {
                Name = "tmpfs",
                Factory = static _ => new Tmpfs(),
                FactoryWithContext = static (_, memoryContext) => new Tmpfs(memoryContext: memoryContext)
            };
            var rootSb = tmpfsType.CreateAnonymousFileSystem(_runtime.MemoryContext).ReadSuper(tmpfsType, 0,
                "creation-creds-root", null);
            SyscallManager.MountRoot(rootSb, new SyscallManager.RootMountOptions
            {
                Source = "tmpfs",
                FsType = "tmpfs",
                Options = "rw"
            });
            Process = new Process(100, Vma, SyscallManager);
            Scheduler = new KernelScheduler();
            Scheduler.RegisterProcess(Process);
            Task = new FiberTask(Process.TGID, Process, Engine, Scheduler);
            Engine.Owner = Task;
        }

        public Engine Engine { get; }
        public VMAManager Vma { get; }
        public SyscallManager SyscallManager { get; }
        public Process Process { get; }
        public KernelScheduler Scheduler { get; }
        public FiberTask Task { get; }

        public void Dispose()
        {
            SyscallManager.Close();
            Engine.Dispose();
        }

        public void MapUserPage(uint addr)
        {
            Vma.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[test]", Engine);
            Assert.True(Vma.HandleFault(addr, true, Engine));
        }

        public void WriteCString(uint addr, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value + "\0");
            Assert.True(Engine.CopyToUser(addr, bytes));
        }

        public Inode ResolveInode(string path)
        {
            var (loc, _) = SyscallManager.ResolvePath(path);
            Assert.True(loc.IsValid);
            Assert.NotNull(loc.Dentry?.Inode);
            return loc.Dentry!.Inode!;
        }

        public async ValueTask<int> Call(string methodName, uint a1 = 0, uint a2 = 0, uint a3 = 0, uint a4 = 0,
            uint a5 = 0, uint a6 = 0)
        {
            var method = typeof(SyscallManager).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            var previous = SyscallManager.CurrentSyscallEngine.CurrentSyscallManager;
            SyscallManager.CurrentSyscallEngine.CurrentSyscallManager = SyscallManager;
            try
            {
                var task = (ValueTask<int>)method!.Invoke(SyscallManager, [Engine, a1, a2, a3, a4, a5, a6])!;
                return await task;
            }
            finally
            {
                SyscallManager.CurrentSyscallEngine.CurrentSyscallManager = previous;
            }
        }
    }
}
