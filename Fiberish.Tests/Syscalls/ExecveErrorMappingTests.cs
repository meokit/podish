using System.Reflection;
using System.Text;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Xunit;

namespace Fiberish.Tests.Syscalls;

[Collection("ExternalPageManagerSerial")]
public class ExecveErrorMappingTests
{
    [Fact]
    public async Task SysExecve_WhenExecPathHitsOom_ReturnsEnomem()
    {
        using var pageScope = ExternalPageManager.BeginIsolatedScope();
        using var cacheScope = GlobalPageCacheManager.BeginIsolatedScope();
        var oldQuota = ExternalPageManager.MemoryQuotaBytes;

        using var engine = new Engine();
        var mm = new VMAManager();
        var sm = new SyscallManager(engine, mm, 0);
        var guestRoot = ResolveGuestRootForHelloStatic();
        sm.MountRootHostfs(guestRoot);

        var scheduler = new KernelScheduler();
        var process = new Process(9201, mm, sm);
        scheduler.RegisterProcess(process);
        var task = new FiberTask(process.TGID, process, engine, scheduler);
        engine.Owner = task;

        try
        {
            const uint filenameAddr = 0x61000000;
            MapUserPage(mm, engine, filenameAddr);
            WriteCString(engine, filenameAddr, "/hello_static");

            // Force subsequent strict anonymous page allocation to fail inside exec path.
            ExternalPageManager.MemoryQuotaBytes = ExternalPageManager.GetAllocatedBytes();

            var rc = await Call(sm, "SysExecve", filenameAddr);
            Assert.Equal(-(int)Errno.ENOMEM, rc);
        }
        finally
        {
            ExternalPageManager.MemoryQuotaBytes = oldQuota;
            sm.Close();
        }
    }

    private static void MapUserPage(VMAManager mm, Engine engine, uint addr)
    {
        mm.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
            MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, LinuxConstants.PageSize, "[test]",
            engine);
        Assert.True(mm.HandleFault(addr, true, engine));
    }

    private static void WriteCString(Engine engine, uint addr, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value + "\0");
        Assert.True(engine.CopyToUser(addr, bytes));
    }

    private static async ValueTask<int> Call(SyscallManager sm, string methodName, uint a1 = 0, uint a2 = 0,
        uint a3 = 0, uint a4 = 0, uint a5 = 0, uint a6 = 0)
    {
        var method = typeof(SyscallManager).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var task = (ValueTask<int>)method!.Invoke(null, [sm.Engine.State, a1, a2, a3, a4, a5, a6])!;
        return await task;
    }

    private static string ResolveGuestRootForHelloStatic()
    {
        const string rel = "tests/linux/hello_static";
        var cwd = Directory.GetCurrentDirectory();
        var current = new DirectoryInfo(cwd);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, rel);
            if (File.Exists(candidate))
                return Path.Combine(current.FullName, "tests/linux");
            current = current.Parent;
        }

        throw new FileNotFoundException("Could not locate tests/linux/hello_static from test working directory.");
    }
}