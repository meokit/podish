using Fiberish.Auth.Cred;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.VFS;
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

    [Fact]
    public void CreateInitProcess_InvalidUtf8ExeAndArgv_PreservesRawBytes()
    {
        var runtime = KernelRuntime.BootstrapBare(false, memoryContext: new MemoryRuntimeContext());
        try
        {
            var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
            var rootSb = tmpfsType.CreateAnonymousFileSystem(runtime.MemoryContext)
                .ReadSuper(tmpfsType, 0, "factory-raw-test", null);
            var rootMount = new Mount(rootSb, rootSb.Root)
            {
                Source = "tmpfs",
                FsType = "tmpfs",
                Options = "rw"
            };
            runtime.Syscalls.InitializeRoot(rootSb.Root, rootMount);

            var helloBytes = File.ReadAllBytes(Path.Combine(ResolveGuestRootForHelloStatic(), "hello_static"));

            var invalidUtf8Name = new byte[] { 0xC0, 0x80, (byte)'x' };
            var fileDentry = new Dentry(FsName.FromBytes(invalidUtf8Name), null, rootSb.Root, rootSb);
            rootSb.Root.Inode!.Create(fileDentry, 0x1ED, 0, 0);
            var wf = new LinuxFile(fileDentry, FileFlags.O_WRONLY, rootMount);
            fileDentry.Inode!.WriteFromHost(null, wf, helloBytes, 0);
            wf.Close();

            var scheduler = new KernelScheduler();
            var pathBytes = new[] { (byte)'/' }.Concat(invalidUtf8Name).ToArray();
            var (loc, _, _) = runtime.Syscalls.ResolvePathBytes(pathBytes);
            Assert.True(loc.IsValid);
            Assert.NotNull(loc.Dentry);
            Assert.NotNull(loc.Mount);

            var invalidUtf8Arg0 = new byte[] { 0xC0, 0x80, (byte)'a' };
            var invalidUtf8Env = new byte[] { 0xC0, 0x80, (byte)'e', (byte)'=', (byte)'1' };

            var task = ProcessFactory.CreateInitProcess(
                runtime,
                loc.Dentry!,
                pathBytes,
                [invalidUtf8Arg0],
                [invalidUtf8Env],
                scheduler,
                null,
                loc.Mount);

            Assert.Equal(pathBytes, task.Process.ExecutablePathRaw);
            Assert.Equal(invalidUtf8Arg0, task.Process.CommandLineArgumentBytes[0]);

            var expectedCmdline = new byte[invalidUtf8Arg0.Length + 1];
            invalidUtf8Arg0.CopyTo(expectedCmdline, 0);
            expectedCmdline[invalidUtf8Arg0.Length] = 0;
            Assert.Equal(expectedCmdline, task.Process.CommandLineRaw);
        }
        finally
        {
            runtime.Dispose();
        }
    }

    [Fact]
    public void CreateInitProcess_RawInputsAreCopiedBeforeProcessImageIsStored()
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

            var guestPathRaw = FsEncoding.EncodeUtf8(guestPath);
            var arg0Raw = FsEncoding.EncodeUtf8("/hello_static");
            var envRaw = FsEncoding.EncodeUtf8("A=1");

            var task = ProcessFactory.CreateInitProcess(
                runtime,
                loc.Dentry!,
                guestPathRaw,
                [arg0Raw],
                [envRaw],
                scheduler,
                null,
                loc.Mount);

            guestPathRaw[0] = (byte)'!';
            arg0Raw[0] = (byte)'!';
            envRaw[0] = (byte)'!';

            Assert.Equal(FsEncoding.EncodeUtf8("/hello_static"), task.Process.ExecutablePathRaw);
            Assert.Equal(FsEncoding.EncodeUtf8("/hello_static"), task.Process.CommandLineArgumentBytes[0]);
            Assert.Equal(FsEncoding.EncodeUtf8("/hello_static\0"), task.Process.CommandLineRaw);
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
