using System.Reflection;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Xunit;

namespace Fiberish.Tests.Memory;

public class PageFaultOomTests
{
    [Fact]
    public void AnonymousFault_WhenQuotaExhausted_ReturnsOom()
    {
        using var pageScope = ExternalPageManager.BeginIsolatedScope();
        using var cacheScope = GlobalAddressSpaceCacheManager.BeginIsolatedScope();
        using var engine = new Engine();
        var mm = new VMAManager();
        var oldQuota = ExternalPageManager.MemoryQuotaBytes;
        ExternalPageManager.MemoryQuotaBytes = LinuxConstants.PageSize - 1;
        try
        {
            var mapped = mm.Mmap(0x72000000, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Anonymous, null, 0, "oom-anon", engine);
            Assert.Equal((uint)0x72000000, mapped);

            Assert.Equal(FaultResult.Oom, mm.HandleFaultDetailed(mapped, true, engine));
        }
        finally
        {
            ExternalPageManager.MemoryQuotaBytes = oldQuota;
        }
    }

    [Fact]
    public void TaskPageFault_WhenOom_KillsProcessWithSigkill()
    {
        using var pageScope = ExternalPageManager.BeginIsolatedScope();
        using var cacheScope = GlobalAddressSpaceCacheManager.BeginIsolatedScope();
        var oldQuota = ExternalPageManager.MemoryQuotaBytes;

        using var engine = new Engine();
        var mm = new VMAManager();
        var sm = new SyscallManager(engine, mm, 0);
        try
        {
            var scheduler = new KernelScheduler();


            var process = new Process(7101, mm, sm);
            scheduler.RegisterProcess(process);
            var task = new FiberTask(7101, process, engine, scheduler);
            engine.Owner = task;

            var mapped = mm.Mmap(0x73000000, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Anonymous, null, 0, "oom-task", engine);
            Assert.Equal((uint)0x73000000, mapped);
            ExternalPageManager.MemoryQuotaBytes = LinuxConstants.PageSize - 1;

            var method = typeof(FiberTask).GetMethod("HandlePageFault", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            var handled = (bool)method!.Invoke(task, [mapped, true])!;
            Assert.True(handled);

            Assert.Equal(ProcessState.Zombie, process.State);
            Assert.True(process.ExitedBySignal);
            Assert.Equal((int)Signal.SIGKILL, process.TermSignal);
        }
        finally
        {
            ExternalPageManager.MemoryQuotaBytes = oldQuota;
            sm.Close();
        }
    }
}