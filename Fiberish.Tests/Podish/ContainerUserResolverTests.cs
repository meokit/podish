using System.Text.Json;
using Fiberish.Core.Net;
using Fiberish.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Podish.Core;
using Xunit;

namespace Fiberish.Tests.Podish;

public sealed class ContainerUserResolverTests
{
    [Fact]
    public void Resolve_NamedUserWithExplicitGroup_UsesGuestPasswdAndSupplementaryGroups()
    {
        var root = TestWorkspace.CreateUniqueDirectory("podish-user-root-");
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
            Assert.Equal<int[]>([1001, 2000], resolved.SupplementaryGroups.OrderBy(static gid => gid).ToArray());
            Assert.Equal("app", resolved.UserName);
            Assert.Equal("/home/app", resolved.HomeDirectory);
        }
        finally
        {
            TestWorkspace.DeleteDirectory(root);
        }
    }

    [Fact]
    public void Resolve_NumericUidWithoutGroup_DefaultsGidToUid()
    {
        var root = TestWorkspace.CreateUniqueDirectory("podish-user-root-");
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
            TestWorkspace.DeleteDirectory(root);
        }
    }

    [Fact]
    public void Resolve_NamedUserWithoutPasswd_ThrowsConfigurationError()
    {
        var root = TestWorkspace.CreateUniqueDirectory("podish-user-root-");
        try
        {
            using var runtime = Fiberish.Core.KernelRuntime.BootstrapBare(false, memoryContext: new MemoryRuntimeContext());
            runtime.Syscalls.MountRootHostfs(root);

            var ex = Assert.Throws<ContainerConfigurationException>(() => ContainerUserResolver.Resolve(runtime.Syscalls, "app"));
            Assert.Contains("/etc/passwd", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            TestWorkspace.DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task RunAsync_InvalidNamedUserOnRootfs_Returns125()
    {
        var root = CreateTempRootWithHelloStatic();
        var runtimeRoot = TestWorkspace.CreateUniqueDirectory("podish-user-config-");
        Directory.CreateDirectory(Path.Combine(runtimeRoot, "ctr"));
        var capture = new StringWriter();

        try
        {
            var service = new ContainerRuntimeService(NullLogger.Instance, NullLoggerFactory.Instance);
            var request = CreateHelloStaticRequest(root, runtimeRoot, true, "app");

            var rc = await TestWorkspace.RedirectConsoleErrorAsync(
                async writer =>
                {
                    var runRc = await service.RunAsync(request);
                    capture.Write(writer.ToString());
                    return runRc;
                });

            Assert.Equal(125, rc);
            Assert.Contains("[Podish Config]", capture.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            TestWorkspace.DeleteDirectory(root);
            TestWorkspace.DeleteDirectory(runtimeRoot);
        }
    }

    [Fact]
    public async Task RunAsync_InvalidImageConfigUser_Returns125()
    {
        var root = CreateTempRootWithHelloStatic();
        var runtimeRoot = TestWorkspace.CreateUniqueDirectory("podish-image-user-");
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

            var rc = await TestWorkspace.RedirectConsoleErrorAsync(
                async writer =>
                {
                    var runRc = await service.RunAsync(request);
                    capture.Write(writer.ToString());
                    return runRc;
                });

            Assert.Equal(125, rc);
            Assert.Contains("[Podish Config]", capture.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            TestWorkspace.DeleteDirectory(root);
            TestWorkspace.DeleteDirectory(runtimeRoot);
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
        File.Copy(Path.Combine(TestWorkspace.ResolveLinuxGuestRoot(), "hello_static"), Path.Combine(root, "hello_static"),
            true);
        return root;
    }

    private static string CreateTempRoot()
    {
        return TestWorkspace.CreateUniqueDirectory("podish-user-root-");
    }
}
