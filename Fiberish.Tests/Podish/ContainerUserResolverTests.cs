using System.Text.Json;
using Fiberish.Core.Net;
using Fiberish.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Podish.Core;
using Xunit;

namespace Fiberish.Tests.Podish;

public sealed class ContainerUserResolverTests
{
    private static readonly SemaphoreSlim ConsoleErrorGate = new(1, 1);

    [Fact]
    public void Resolve_NamedUserWithExplicitGroup_UsesGuestPasswdAndSupplementaryGroups()
    {
        var root = CreateTempRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "etc"));
            File.WriteAllText(Path.Combine(root, "etc", "passwd"),
                "root:x:0:0:root:/root:/bin/sh\napp:x:1000:1001:app:/home/app:/bin/sh\n");
            File.WriteAllText(Path.Combine(root, "etc", "group"),
                "root:x:0:\nappgrp:x:1001:app\nextra:x:2000:app\nstaff:x:3000:app\n");

            using var runtime = Fiberish.Core.KernelRuntime.BootstrapBare(false, memoryContext: new MemoryRuntimeContext());
            runtime.Syscalls.MountRootHostfs(root);

            var resolved = ContainerUserResolver.Resolve(runtime.Syscalls, "app:staff");

            Assert.Equal(1000, resolved.Uid);
            Assert.Equal(3000, resolved.Gid);
            Assert.Equal([1001, 2000], resolved.SupplementaryGroups.OrderBy(static gid => gid).ToArray());
            Assert.Equal("app", resolved.UserName);
            Assert.Equal("/home/app", resolved.HomeDirectory);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Resolve_NumericUidWithoutGroup_DefaultsGidToUid()
    {
        var root = CreateTempRoot();
        try
        {
            using var runtime = Fiberish.Core.KernelRuntime.BootstrapBare(false, memoryContext: new MemoryRuntimeContext());
            runtime.Syscalls.MountRootHostfs(root);

            var resolved = ContainerUserResolver.Resolve(runtime.Syscalls, "1234");

            Assert.Equal(1234, resolved.Uid);
            Assert.Equal(1234, resolved.Gid);
            Assert.Empty(resolved.SupplementaryGroups);
            Assert.Equal("1234", resolved.UserName);
            Assert.Equal("/", resolved.HomeDirectory);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Resolve_NamedUserWithoutPasswd_ThrowsConfigurationError()
    {
        var root = CreateTempRoot();
        try
        {
            using var runtime = Fiberish.Core.KernelRuntime.BootstrapBare(false, memoryContext: new MemoryRuntimeContext());
            runtime.Syscalls.MountRootHostfs(root);

            var ex = Assert.Throws<ContainerConfigurationException>(() => ContainerUserResolver.Resolve(runtime.Syscalls, "app"));
            Assert.Contains("/etc/passwd", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RunAsync_InvalidNamedUserOnRootfs_Returns125()
    {
        var root = CreateTempRootWithHelloStatic();
        var runtimeRoot = Path.Combine(Path.GetTempPath(), "podish-user-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runtimeRoot);
        Directory.CreateDirectory(Path.Combine(runtimeRoot, "ctr"));
        var capture = new StringWriter();

        try
        {
            var service = new ContainerRuntimeService(NullLogger.Instance, NullLoggerFactory.Instance);
            var request = CreateHelloStaticRequest(root, runtimeRoot, true, "app");

            int rc;
            await ConsoleErrorGate.WaitAsync();
            try
            {
                var previous = Console.Error;
                try
                {
                    Console.SetError(capture);
                    rc = await service.RunAsync(request);
                }
                finally
                {
                    Console.SetError(previous);
                }
            }
            finally
            {
                ConsoleErrorGate.Release();
            }

            Assert.Equal(125, rc);
            Assert.Contains("[Podish Config]", capture.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(runtimeRoot, true);
        }
    }

    [Fact]
    public async Task RunAsync_InvalidImageConfigUser_Returns125()
    {
        var root = CreateTempRootWithHelloStatic();
        var runtimeRoot = Path.Combine(Path.GetTempPath(), "podish-image-user-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runtimeRoot);
        Directory.CreateDirectory(Path.Combine(runtimeRoot, "ctr"));
        File.WriteAllText(Path.Combine(root, "image.json"), JsonSerializer.Serialize(new OciStoredImage(
            ImageReference: "test:image",
            Registry: "local",
            Repository: "test",
            Tag: "latest",
            ManifestDigest: "sha256:deadbeef",
            StoreDirectory: ".",
            Layers: Array.Empty<OciStoredLayer>(),
            ConfigUser: "app")));
        var capture = new StringWriter();

        try
        {
            var service = new ContainerRuntimeService(NullLogger.Instance, NullLoggerFactory.Instance);
            var request = CreateHelloStaticRequest(root, runtimeRoot, false, null);

            int rc;
            await ConsoleErrorGate.WaitAsync();
            try
            {
                var previous = Console.Error;
                try
                {
                    Console.SetError(capture);
                    rc = await service.RunAsync(request);
                }
                finally
                {
                    Console.SetError(previous);
                }
            }
            finally
            {
                ConsoleErrorGate.Release();
            }

            Assert.Equal(125, rc);
            Assert.Contains("[Podish Config]", capture.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(runtimeRoot, true);
        }
    }

    private static ContainerRunRequest CreateHelloStaticRequest(string rootfsPath, string runtimeRoot, bool rootfsMode,
        string? user)
    {
        return new ContainerRunRequest
        {
            RootfsPath = rootfsPath,
            RootfsMode = rootfsMode,
            Exe = "/hello_static",
            ExeArgs = Array.Empty<string>(),
            Volumes = Array.Empty<string>(),
            GuestEnvs = Array.Empty<string>(),
            DnsServers = Array.Empty<string>(),
            User = user,
            UseTty = false,
            Strace = false,
            UseOverlay = false,
            ContainerId = "user-config-test-" + Guid.NewGuid().ToString("N")[..12],
            Hostname = "user-config-test",
            NetworkMode = NetworkMode.Host,
            Image = "test:image",
            ContainerDir = Path.Combine(runtimeRoot, "ctr"),
            LogDriver = ContainerLogDriver.None,
            EventStore = new ContainerEventStore(Path.Combine(runtimeRoot, "events.jsonl")),
            PublishedPorts = Array.Empty<PublishedPortSpec>()
        };
    }

    private static string CreateTempRootWithHelloStatic()
    {
        var root = CreateTempRoot();
        File.Copy(Path.Combine(ResolveGuestRootForHelloStatic(), "hello_static"), Path.Combine(root, "hello_static"), true);
        return root;
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "podish-user-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
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
