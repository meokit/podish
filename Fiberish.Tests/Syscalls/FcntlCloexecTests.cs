using System.Reflection;
using System.Text;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public class FcntlCloexecTests
{
    private const uint F_DUPFD = 0;
    private const uint F_GETFD = 1;
    private const uint F_SETFD = 2;
    private const uint FD_CLOEXEC = 1;

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