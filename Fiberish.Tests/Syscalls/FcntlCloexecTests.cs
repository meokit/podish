using System.Reflection;
using System.Text;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public class FcntlCloexecTests
{
    private const uint F_DUPFD = 0;
    private const uint F_GETFD = 1;
    private const uint F_SETFD = 2;
    private const uint FD_CLOEXEC = 1;
    private const uint F_ADD_SEALS = 1033;
    private const uint F_GET_SEALS = 1034;
    private const uint MFD_ALLOW_SEALING = 0x0002;
    private const uint MFD_NOEXEC_SEAL = 0x0008;
    private const uint MFD_EXEC = 0x0010;
    private const uint F_SEAL_SEAL = 0x0001;
    private const uint F_SEAL_SHRINK = 0x0002;
    private const uint F_SEAL_GROW = 0x0004;
    private const uint F_SEAL_WRITE = 0x0008;

    [Fact]
    public async Task FcntlSetFdCloexec_ShouldNotLeakToSiblingFd_AndDup2ShouldClearIt()
    {
        using var env = new TestEnv();
        var testFile = $".cloexec-{Guid.NewGuid():N}.txt";
        File.WriteAllText(testFile, "x");
        try
        {
            const uint pathAddr = 0x20000;
            env.MapUserPage(pathAddr);
            env.WriteCString(pathAddr, testFile);

            Assert.Equal(0, await env.Call("SysOpen", pathAddr));
            Assert.Equal(1, await env.Call("SysOpen", pathAddr));
            Assert.Equal(2, await env.Call("SysOpen", pathAddr));

            var savedStdout = await env.Call("SysFcntl64", 1, F_DUPFD, 10);
            Assert.Equal(10, savedStdout);

            Assert.Equal(0, await env.Call("SysFcntl64", 1, F_GETFD));
            Assert.Equal(0, await env.Call("SysFcntl64", (uint)savedStdout, F_SETFD, FD_CLOEXEC));
            Assert.Equal((int)FD_CLOEXEC, await env.Call("SysFcntl64", (uint)savedStdout, F_GETFD));
            Assert.Equal(0, await env.Call("SysFcntl64", 1, F_GETFD));

            Assert.Equal(1, await env.Call("SysDup2", (uint)savedStdout, 1));
            Assert.Equal(0, await env.Call("SysFcntl64", 1, F_GETFD));
            Assert.Equal(0, await env.Call("SysClose", (uint)savedStdout));

            var openedFd = await env.Call("SysOpen", pathAddr);
            Assert.Equal(3, openedFd);
        }
        finally
        {
            File.Delete(testFile);
        }
    }

    [Fact]
    public async Task Memfd_AddSeals_AllowedMemfd_ReturnsConfiguredSeals()
    {
        using var env = new TestEnv();
        const uint nameAddr = 0x22000;
        env.MapUserPage(nameAddr);
        env.WriteCString(nameAddr, "foot-shm");

        var fd = await env.Call("SysMemfdCreate", nameAddr, MFD_ALLOW_SEALING);
        Assert.True(fd >= 0);

        var seals = F_SEAL_SHRINK | F_SEAL_GROW | F_SEAL_WRITE;
        Assert.Equal(0, await env.Call("SysFcntl64", (uint)fd, F_ADD_SEALS, seals));
        Assert.Equal((int)seals, await env.Call("SysFcntl64", (uint)fd, F_GET_SEALS));
    }

    [Fact]
    public async Task Memfd_AddSeals_WithoutAllowSealing_ReturnsEperm()
    {
        using var env = new TestEnv();
        const uint nameAddr = 0x23000;
        env.MapUserPage(nameAddr);
        env.WriteCString(nameAddr, "plain-memfd");

        var fd = await env.Call("SysMemfdCreate", nameAddr, 0);
        Assert.True(fd >= 0);

        Assert.Equal(-(int)Errno.EPERM, await env.Call("SysFcntl64", (uint)fd, F_ADD_SEALS, F_SEAL_WRITE));
        Assert.Equal((int)F_SEAL_SEAL, await env.Call("SysFcntl64", (uint)fd, F_GET_SEALS));
    }

    [Fact]
    public async Task Memfd_Create_WithExecFlag_SucceedsAndMarksFileExecutable()
    {
        using var env = new TestEnv();
        const uint nameAddr = 0x24000;
        env.MapUserPage(nameAddr);
        env.WriteCString(nameAddr, "exec-memfd");

        var fd = await env.Call("SysMemfdCreate", nameAddr, MFD_ALLOW_SEALING | MFD_EXEC);
        Assert.True(fd >= 0);

        var file = env.SyscallManager.GetFD(fd);
        var inode = Assert.IsType<TmpfsInode>(file!.OpenedInode);
        Assert.True((inode.Mode & 0x40) != 0);
        Assert.True(inode.IsMemfdExecutable);
        Assert.False(inode.IsMemfdNoExecSealed);
    }

    [Fact]
    public async Task Memfd_Create_WithNoexecSeal_SucceedsAndRemainsNonExecutable()
    {
        using var env = new TestEnv();
        const uint nameAddr = 0x25000;
        env.MapUserPage(nameAddr);
        env.WriteCString(nameAddr, "noexec-memfd");

        var fd = await env.Call("SysMemfdCreate", nameAddr, MFD_ALLOW_SEALING | MFD_NOEXEC_SEAL);
        Assert.True(fd >= 0);

        var file = env.SyscallManager.GetFD(fd);
        var inode = Assert.IsType<TmpfsInode>(file!.OpenedInode);
        Assert.True((inode.Mode & 0x49) == 0);
        Assert.False(inode.IsMemfdExecutable);
        Assert.True(inode.IsMemfdNoExecSealed);
        Assert.Equal(0, await env.Call("SysFcntl64", (uint)fd, F_ADD_SEALS, F_SEAL_WRITE));
        Assert.Equal((int)F_SEAL_WRITE, await env.Call("SysFcntl64", (uint)fd, F_GET_SEALS));
    }

    [Fact]
    public async Task Memfd_Create_WithExecAndNoexecSeal_ReturnsEinval()
    {
        using var env = new TestEnv();
        const uint nameAddr = 0x26000;
        env.MapUserPage(nameAddr);
        env.WriteCString(nameAddr, "bad-memfd");

        var rc = await env.Call("SysMemfdCreate", nameAddr, MFD_EXEC | MFD_NOEXEC_SEAL);
        Assert.Equal(-(int)Errno.EINVAL, rc);
    }

    private sealed class TestEnv : IDisposable
    {
        public TestEnv()
        {
            Engine = new Engine();
            Vma = new VMAManager();
            SyscallManager = new SyscallManager(Engine, Vma, 0);
            SyscallManager.MountRootHostfs(".");
        }

        public Engine Engine { get; }
        public VMAManager Vma { get; }
        public SyscallManager SyscallManager { get; }

        public void Dispose()
        {
            SyscallManager.Close();
        }

        public void MapUserPage(uint addr)
        {
            Vma.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[test]",
                Engine);
            Assert.True(Vma.HandleFault(addr, true, Engine));
        }

        public void WriteCString(uint addr, string value)
        {
            Assert.True(Engine.CopyToUser(addr, Encoding.UTF8.GetBytes(value + "\0")));
        }

        public ValueTask<int> Call(string name, uint a1 = 0, uint a2 = 0, uint a3 = 0, uint a4 = 0, uint a5 = 0,
            uint a6 = 0)
        {
            var method = typeof(SyscallManager).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            return (ValueTask<int>)method!.Invoke(SyscallManager, [Engine, a1, a2, a3, a4, a5, a6])!;
        }
    }
}
