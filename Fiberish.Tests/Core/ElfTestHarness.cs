using Fiberish.Core;
using Fiberish.Memory;

namespace Fiberish.Tests.Core;

internal sealed class ElfTestHarness : IDisposable
{
    private ElfTestHarness(KernelRuntime runtime, KernelScheduler scheduler, FiberTask task, string guestPath)
    {
        Runtime = runtime;
        Scheduler = scheduler;
        Task = task;
        GuestPath = guestPath;
    }

    public KernelRuntime Runtime { get; }
    public KernelScheduler Scheduler { get; }
    public FiberTask Task { get; }
    public string GuestPath { get; }
    public Process Process => Task.Process;

    public static ElfTestHarness LoadLinuxTestAsset(string assetName, bool strace = false, string[]? args = null,
        string[]? envs = null)
    {
        var guestRoot = ResolveLinuxGuestRoot();
        var runtime = KernelRuntime.BootstrapBare(strace);
        var scheduler = new KernelScheduler();

        try
        {
            runtime.Syscalls.MountRootHostfs(guestRoot);
            var guestPath = "/" + assetName.TrimStart('/');
            var (loc, resolvedGuestPath) = runtime.Syscalls.ResolvePath(guestPath, true);
            if (!loc.IsValid || loc.Dentry == null || loc.Mount == null)
                throw new FileNotFoundException($"Could not resolve guest ELF '{guestPath}' under '{guestRoot}'.");

            var task = ProcessFactory.CreateInitProcess(
                runtime,
                loc.Dentry,
                resolvedGuestPath,
                args ?? [guestPath],
                envs ?? Array.Empty<string>(),
                scheduler,
                null,
                loc.Mount);

            return new ElfTestHarness(runtime, scheduler, task, resolvedGuestPath);
        }
        catch
        {
            runtime.Syscalls.Close();
            runtime.Engine.Dispose();
            throw;
        }
    }

    public static string ResolveLinuxGuestRoot()
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

        throw new FileNotFoundException("Could not locate tests/linux assets from test working directory.");
    }

    public void Dispose()
    {
        Runtime.Syscalls.Close();
        Runtime.Engine.Dispose();
    }
}
