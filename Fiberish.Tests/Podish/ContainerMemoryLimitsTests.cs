using Fiberish.Core.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Podish.Core;
using Podish.Core.Networking;
using Xunit;

namespace Fiberish.Tests.Podish;

public sealed class ContainerMemoryLimitsTests
{
    [Theory]
    [InlineData("32M", 32L * 1024 * 1024)]
    [InlineData("64m", 64L * 1024 * 1024)]
    [InlineData("1G", 1024L * 1024 * 1024)]
    [InlineData("512MiB", 512L * 1024 * 1024)]
    public void TryParseMemoryQuotaBytes_AcceptsSupportedSizes(string raw, long expectedBytes)
    {
        var ok = ContainerMemoryLimits.TryParseMemoryQuotaBytes(raw, out var bytes, out var error);

        Assert.True(ok);
        Assert.Equal(expectedBytes, bytes);
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [InlineData("31M")]
    [InlineData("1")]
    [InlineData("foo")]
    public void TryParseMemoryQuotaBytes_RejectsInvalidValues(string raw)
    {
        var ok = ContainerMemoryLimits.TryParseMemoryQuotaBytes(raw, out var bytes, out var error);

        Assert.False(ok);
        Assert.Equal(0, bytes);
        Assert.NotEmpty(error);
    }

    [Fact]
    public async Task RunAsync_MemoryQuotaBelowMinimum_IsRejected()
    {
        var root = TestWorkspace.CreateUniqueDirectory("podish-memory-limit-");
        var containerDir = Path.Combine(root, "ctr");
        Directory.CreateDirectory(containerDir);
        var eventPath = Path.Combine(root, "events.jsonl");
        var eventStore = new ContainerEventStore(eventPath);

        try
        {
            var service = new ContainerRuntimeService(NullLogger.Instance, NullLoggerFactory.Instance);
            var guestRoot = ResolveGuestRootForHelloStatic();
            var request = new ContainerRunRequest
            {
                RootfsPath = guestRoot,
                Exe = "/hello_static",
                ExeArgs = Array.Empty<string>(),
                Volumes = Array.Empty<string>(),
                GuestEnvs = Array.Empty<string>(),
                DnsServers = Array.Empty<string>(),
                UseTty = false,
                Strace = false,
                UseEngineInit = false,
                UseOverlay = false,
                ContainerId = "memory-limit-test-container",
                Hostname = "memory-limit-test",
                NetworkMode = NetworkMode.Host,
                Image = "memory:test",
                ContainerDir = containerDir,
                LogDriver = ContainerLogDriver.None,
                EventStore = eventStore,
                PublishedPorts = Array.Empty<PublishedPortSpec>(),
                MemoryQuotaBytes = 1
            };

            var capture = new StringWriter();
            var rc = await TestWorkspace.RedirectConsoleErrorAsync(
                async writer =>
                {
                    var runRc = await service.RunAsync(request);
                    capture.Write(writer.ToString());
                    return runRc;
                });

            Assert.Equal(1, rc);
            var stderr = capture.ToString();
            Assert.Contains("[Podish Error]", stderr);
            Assert.Contains("memory quota must be at least 32M", stderr);

            var exit = eventStore.ReadAll().LastOrDefault(e => e.Type == "container-exit");
            Assert.NotNull(exit);
            Assert.NotNull(exit!.Message);
            Assert.Contains("memory quota must be at least 32M", exit.Message!);
        }
        finally
        {
            TestWorkspace.DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task RunAsync_HostModeWithoutPublishedPorts_DoesNotCreatePortForwardManager()
    {
        var root = CreateRuntimeTestRoot();
        var managerCreated = false;

        try
        {
            var service = new ContainerRuntimeService(NullLogger.Instance, NullLoggerFactory.Instance, () =>
            {
                managerCreated = true;
                return new FakePortForwardManager();
            });

            var rc = await service.RunAsync(CreateHelloStaticRequest(root, NetworkMode.Host,
                Array.Empty<PublishedPortSpec>()));
            Assert.Equal(0, rc);
            Assert.False(managerCreated);
        }
        finally
        {
            TestWorkspace.DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task RunAsync_PrivateModeWithoutPublishedPorts_DoesNotCreatePortForwardManager()
    {
        var root = CreateRuntimeTestRoot();
        var managerCreated = false;

        try
        {
            var service = new ContainerRuntimeService(NullLogger.Instance, NullLoggerFactory.Instance, () =>
            {
                managerCreated = true;
                return new FakePortForwardManager();
            });

            var rc = await service.RunAsync(CreateHelloStaticRequest(root, NetworkMode.Private,
                Array.Empty<PublishedPortSpec>()));
            Assert.Equal(0, rc);
            Assert.False(managerCreated);
        }
        finally
        {
            TestWorkspace.DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task RunAsync_PrivateModeWithPublishedPorts_CreatesAndDisposesPortForwardManager()
    {
        var root = CreateRuntimeTestRoot();
        FakePortForwardManager? manager = null;

        try
        {
            var service = new ContainerRuntimeService(NullLogger.Instance, NullLoggerFactory.Instance, () =>
            {
                manager = new FakePortForwardManager();
                return manager;
            });

            var rc = await service.RunAsync(CreateHelloStaticRequest(root, NetworkMode.Private, [
                new PublishedPortSpec
                {
                    HostPort = 0,
                    ContainerPort = 12345,
                    Protocol = TransportProtocol.Tcp
                }
            ]));

            Assert.Equal(0, rc);
            Assert.NotNull(manager);
            Assert.Equal(1, manager!.StartCalls);
            Assert.Equal(1, manager.StopCalls);
            Assert.Equal(1, manager.DisposeCalls);
        }
        finally
        {
            TestWorkspace.DeleteDirectory(root);
        }
    }

    private static string CreateRuntimeTestRoot()
    {
        var root = TestWorkspace.CreateUniqueDirectory("podish-runtime-test-");
        Directory.CreateDirectory(Path.Combine(root, "ctr"));
        return root;
    }

    private static ContainerRunRequest CreateHelloStaticRequest(string root, NetworkMode networkMode,
        IReadOnlyList<PublishedPortSpec> publishedPorts)
    {
        var guestRoot = ResolveGuestRootForHelloStatic();
        return new ContainerRunRequest
        {
            RootfsPath = guestRoot,
            Exe = "/hello_static",
            ExeArgs = Array.Empty<string>(),
            Volumes = Array.Empty<string>(),
            GuestEnvs = Array.Empty<string>(),
            DnsServers = Array.Empty<string>(),
            UseTty = false,
            Strace = false,
            UseEngineInit = false,
            UseOverlay = false,
            ContainerId = "runtime-port-forward-test-" + Guid.NewGuid().ToString("N")[..12],
            Hostname = "runtime-port-forward-test",
            NetworkMode = networkMode,
            Image = "runtime:test",
            ContainerDir = Path.Combine(root, "ctr"),
            LogDriver = ContainerLogDriver.None,
            EventStore = new ContainerEventStore(Path.Combine(root, "events.jsonl")),
            PublishedPorts = publishedPorts
        };
    }

    private static string ResolveGuestRootForHelloStatic()
    {
        return TestWorkspace.ResolveLinuxGuestRoot();
    }

    private sealed class FakePortForwardManager : IPortForwardManager
    {
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public int DisposeCalls { get; private set; }

        public void Dispose()
        {
            DisposeCalls++;
        }

        public void Start(ContainerNetworkContext context, IReadOnlyList<PublishedPortSpec> ports)
        {
            StartCalls++;
        }

        public bool Stop(ContainerNetworkContext context)
        {
            StopCalls++;
            return true;
        }
    }
}
