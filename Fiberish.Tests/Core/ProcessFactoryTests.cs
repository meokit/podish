using Fiberish.Core;
using Fiberish.Memory;
using Xunit;

namespace Fiberish.Tests.Core;

[Collection("ExternalPageManagerSerial")]
public sealed class ProcessFactoryTests
{
    [Fact]
    public void CreateInitProcess_WhenLoadHitsOom_RollsBackSchedulerState()
    {
        using var pageScope = PageManager.BeginIsolatedScope();
        using var cacheScope = AddressSpacePolicy.BeginIsolatedScope();

        var runtime = KernelRuntime.BootstrapBare(false);
        try
        {
            runtime.Syscalls.MountRootHostfs(ResolveGuestRootForHelloStatic());
            var scheduler = new KernelScheduler();
            var (loc, guestPath) = runtime.Syscalls.ResolvePath("/hello_static", true);
            Assert.True(loc.IsValid);
            Assert.NotNull(loc.Dentry);
            Assert.NotNull(loc.Mount);

            PageManager.MemoryQuotaBytes = 1;

            Assert.Throws<OutOfMemoryException>(() => ProcessFactory.CreateInitProcess(
                runtime,
                loc.Dentry!,
                guestPath,
                ["/hello_static"],
                Array.Empty<string>(),
                scheduler,
                null,
                loc.Mount));

            Assert.Empty(scheduler.GetProcessesSnapshot());
            Assert.Null(runtime.Engine.Owner);
        }
        finally
        {
            runtime.Syscalls.Close();
            runtime.Engine.Dispose();
        }
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