using Fiberish.Core;
using Xunit;

namespace Fiberish.Tests.Core;

public sealed class ElfTestHarnessTests
{
    [Fact]
    public void LoadLinuxTestAsset_LoadsHelloStaticIntoInitTask()
    {
        using var harness = ElfTestHarness.LoadLinuxTestAsset("hello_static");

        Assert.Equal("/hello_static", harness.GuestPath);
        Assert.Equal(ProcessKind.Normal, harness.Process.Kind);
        Assert.Equal(TaskExecutionMode.RunningGuest, harness.Task.ExecutionMode);
        Assert.NotEqual(0u, harness.Task.CPU.Eip);
    }
}
