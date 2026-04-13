using Fiberish.Auth.Cred;
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
        var runtime = KernelRuntime.BootstrapBare(false, memoryContext: new MemoryRuntimeContext());
        runtime.MemoryContext.MemoryQuotaBytes = 1;
        try
        {
            runtime.Syscalls.MountRootHostfs(ResolveGuestRootForHelloStatic());
            var scheduler = new KernelScheduler();
            var (loc, guestPath) = runtime.Syscalls.ResolvePath("/hello_static", true);
            Assert.True(loc.IsValid);
            Assert.NotNull(loc.Dentry);
            Assert.NotNull(loc.Mount);

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
            runtime.Dispose();
        }
    }

    [Fact]
    public void CreateInitProcess_AppliesConfiguredCredentialsBeforeLoadingExecutable()
    {
        var runtime = KernelRuntime.BootstrapBare(false, memoryContext: new MemoryRuntimeContext());
        try
        {
            runtime.Syscalls.MountRootHostfs(ResolveGuestRootForHelloStatic());
            var scheduler = new KernelScheduler();
            var (loc, guestPath) = runtime.Syscalls.ResolvePath("/hello_static", true);
            Assert.True(loc.IsValid);
            Assert.NotNull(loc.Dentry);
            Assert.NotNull(loc.Mount);

            var task = ProcessFactory.CreateInitProcess(
                runtime,
                loc.Dentry!,
                guestPath,
                ["/hello_static"],
                Array.Empty<string>(),
                scheduler,
                null,
                loc.Mount,
                null,
                0,
                proc => CredentialService.InitializeCredentials(proc, 1000, 1001, [2000, 2001]));

            Assert.Equal(1000, task.Process.UID);
            Assert.Equal(1000, task.Process.EUID);
            Assert.Equal(1000, task.Process.FSUID);
            Assert.Equal(1001, task.Process.GID);
            Assert.Equal(1001, task.Process.EGID);
            Assert.Equal(1001, task.Process.FSGID);
            Assert.Equal([2000, 2001], task.Process.SupplementaryGroups);
            Assert.False(task.Process.HasEffectiveCapability(Process.CapabilitySysAdmin));
        }
        finally
        {
            runtime.Dispose();
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
