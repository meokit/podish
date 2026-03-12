using Fiberish.Core.Net;
using Fiberish.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Podish.Core;
using Xunit;

namespace Fiberish.Tests.Podish;

[Collection("ExternalPageManagerSerial")]
public sealed class ContainerRuntimeOomTests
{
    private static readonly SemaphoreSlim ConsoleErrorGate = new(1, 1);

    [Fact]
    public async Task RunAsync_StartupOom_EmitsSpecificUserMessageAndEvent()
    {
        using var pageScope = ExternalPageManager.BeginIsolatedScope();
        using var cacheScope = GlobalAddressSpaceCacheManager.BeginIsolatedScope();

        var root = Path.Combine(Path.GetTempPath(), "podish-oom-" + Guid.NewGuid().ToString("N"));
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
                ContainerId = "oom-test-container",
                Hostname = "oom-test",
                NetworkMode = NetworkMode.Host,
                Image = "oom:test",
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
            Assert.Contains("[Podish OOM]", stderr);
            Assert.Contains("ENOMEM", stderr);

            var exit = eventStore.ReadAll().LastOrDefault(e => e.Type == "container-exit");
            Assert.NotNull(exit);
            Assert.NotNull(exit!.Message);
            Assert.Contains("ENOMEM", exit.Message!);
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