using Fiberish.Core.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Podish.Core;
using Xunit;

namespace Fiberish.Tests.Podish;

public sealed class ContainerMemoryLimitsTests
{
    private static readonly SemaphoreSlim ConsoleErrorGate = new(1, 1);

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
        using var pageScope = Fiberish.Memory.ExternalPageManager.BeginIsolatedScope();
        using var cacheScope = Fiberish.Memory.GlobalAddressSpaceCacheManager.BeginIsolatedScope();

        var root = Path.Combine(Path.GetTempPath(), "podish-memory-limit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
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
            Directory.Delete(root, true);
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
