using System.Collections;
using System.Reflection;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Syscalls;
using Xunit;

namespace Fiberish.Tests.Memory;

public class ProcessAddressSpaceRegistryTests
{
    [Fact]
    public void SyscallManagerClose_UnregistersEngineFromAddressSpaceRegistry()
    {
        using var engine = new Engine();
        var mm = new VMAManager();
        var sm = new SyscallManager(engine, mm, 0);
        var state = engine.State;

        Assert.True(ContainsEngineState(state));

        sm.Close();

        Assert.False(ContainsEngineState(state));
    }

    [Fact]
    public void DetachTask_UnregistersEngineFromAddressSpaceRegistry()
    {
        var oldCurrent = KernelScheduler.Current;
        using var engine = new Engine();
        var mm = new VMAManager();
        var sm = new SyscallManager(engine, mm, 0);
        var state = engine.State;

        try
        {
            var scheduler = new KernelScheduler();
            KernelScheduler.Current = scheduler;
            var process = new Process(7001, mm, sm);
            scheduler.RegisterProcess(process);
            var task = new FiberTask(7001, process, engine, scheduler);
            engine.Owner = task;

            Assert.True(ContainsEngineState(state));

            _ = scheduler.DetachTask(task);

            Assert.False(ContainsEngineState(state));
        }
        finally
        {
            KernelScheduler.Current = oldCurrent;
            sm.Close();
        }
    }

    private static bool ContainsEngineState(IntPtr state)
    {
        var syncType = typeof(VMAManager).Assembly.GetType("Fiberish.Memory.ProcessAddressSpaceSync");
        Assert.NotNull(syncType);
        var field = syncType!.GetField("AddressSpaceByEngineState",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var map = field!.GetValue(null) as IDictionary;
        Assert.NotNull(map);
        return map!.Contains(state);
    }
}
