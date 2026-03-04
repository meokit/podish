using System.Text.Json;
using Podish.Core;
using Podish.Core.Native;
using Xunit;

namespace Fiberish.Tests.Podish;

public class ConcurrencyIsolationTests
{
    [Fact]
    public void EventStore_Append_IsSerializedAcrossInstances()
    {
        var root = Path.Combine(Path.GetTempPath(), "podish-events-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "events.jsonl");

        try
        {
            var storeA = new ContainerEventStore(path);
            var storeB = new ContainerEventStore(path);

            const int perWriter = 100;
            var t1 = Task.Run(() =>
            {
                for (var i = 0; i < perWriter; i++)
                    storeA.Append(new ContainerEvent(DateTimeOffset.UtcNow, "test-a", $"a-{i}"));
            });
            var t2 = Task.Run(() =>
            {
                for (var i = 0; i < perWriter; i++)
                    storeB.Append(new ContainerEvent(DateTimeOffset.UtcNow, "test-b", $"b-{i}"));
            });

            Task.WaitAll(t1, t2);

            var lines = File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            Assert.Equal(perWriter * 2, lines.Length);
            foreach (var line in lines)
            {
                var evt = JsonSerializer.Deserialize<ContainerEvent>(line);
                Assert.NotNull(evt);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void CooperativeFileLock_SerializesCriticalSection_ForSamePath()
    {
        var root = Path.Combine(Path.GetTempPath(), "podish-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var lockPath = Path.Combine(root, "resource.lock");
        var trace = new List<string>();
        var gate = new object();

        try
        {
            var t1 = Task.Run(() =>
            {
                using var _ = CooperativeFileLock.Acquire(lockPath);
                lock (gate) trace.Add("t1-enter");
                Thread.Sleep(200);
                lock (gate) trace.Add("t1-exit");
            });

            Thread.Sleep(20);
            var t2 = Task.Run(() =>
            {
                using var _ = CooperativeFileLock.Acquire(lockPath);
                lock (gate) trace.Add("t2-enter");
                Thread.Sleep(20);
                lock (gate) trace.Add("t2-exit");
            });

            Task.WaitAll(t1, t2);

            Assert.Equal(["t1-enter", "t1-exit", "t2-enter", "t2-exit"], trace);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void NativeContext_LastError_IsThreadIsolated()
    {
        var root = Path.Combine(Path.GetTempPath(), "podish-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var ctx = new NativeContext
            {
                Context = new PodishContext(new PodishContextOptions
                {
                    WorkDir = root,
                    LogFile = Path.Combine(root, "podish.log"),
                    LogLevel = "error"
                })
            };

            string? got1 = null;
            string? got2 = null;
            var barrier = new Barrier(2);

            var th1 = new Thread(() =>
            {
                ctx.SetLastErrorForCurrentThread("error-from-t1");
                barrier.SignalAndWait();
                got1 = ctx.GetLastErrorForCurrentThread();
            });
            var th2 = new Thread(() =>
            {
                ctx.SetLastErrorForCurrentThread("error-from-t2");
                barrier.SignalAndWait();
                got2 = ctx.GetLastErrorForCurrentThread();
            });

            th1.Start();
            th2.Start();
            th1.Join();
            th2.Join();

            Assert.Equal("error-from-t1", got1);
            Assert.Equal("error-from-t2", got2);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
