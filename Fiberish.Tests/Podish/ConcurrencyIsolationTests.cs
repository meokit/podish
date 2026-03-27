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
        var gate = new Lock();

        try
        {
            var t1 = Task.Run(() =>
            {
                using var _ = CooperativeFileLock.Acquire(lockPath);
                lock (gate)
                {
                    trace.Add("t1-enter");
                }

                Thread.Sleep(200);
                lock (gate)
                {
                    trace.Add("t1-exit");
                }
            });

            Thread.Sleep(20);
            var t2 = Task.Run(() =>
            {
                using var _ = CooperativeFileLock.Acquire(lockPath);
                lock (gate)
                {
                    trace.Add("t2-enter");
                }

                Thread.Sleep(20);
                lock (gate)
                {
                    trace.Add("t2-exit");
                }
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

    [Fact]
    public void NativeContext_CreateContainer_IsAtomic_ForDuplicateNames()
    {
        var root = Path.Combine(Path.GetTempPath(), "podish-native-create-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            using var ctx = new NativeContext
            {
                Context = new PodishContext(new PodishContextOptions
                {
                    WorkDir = root,
                    LogFile = Path.Combine(root, "podish.log"),
                    LogLevel = "error"
                })
            };

            var spec = new PodishRunSpec { Name = "dup-name" };
            var results = new (NativeContainer? Container, string? Error, int Code)[8];

            Parallel.For(0, results.Length, i => { results[i] = ctx.CreateContainer(spec); });

            var succeeded = results.Count(r => r.Container != null && r.Code == PodishNativeApi.PodOk);
            var busy = results.Count(r => r.Container == null && r.Code == PodishNativeApi.PodEbusy);

            Assert.Equal(1, succeeded);
            Assert.Equal(results.Length - 1, busy);
            Assert.Single(ctx.ContainersSnapshot());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void NativeContext_OpenContainer_DeduplicatesLiveInstance()
    {
        var root = Path.Combine(Path.GetTempPath(), "podish-native-open-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        string containerId;
        try
        {
            using (var writerCtx = new NativeContext
                   {
                       Context = new PodishContext(new PodishContextOptions
                       {
                           WorkDir = root,
                           LogFile = Path.Combine(root, "writer.log"),
                           LogLevel = "error"
                       })
                   })
            {
                var created = writerCtx.CreateContainer(new PodishRunSpec { Name = "to-open" });
                Assert.NotNull(created.Container);
                Assert.Equal(PodishNativeApi.PodOk, created.Code);
                containerId = created.Container!.ContainerId;
            }

            using var readerCtx = new NativeContext
            {
                Context = new PodishContext(new PodishContextOptions
                {
                    WorkDir = root,
                    LogFile = Path.Combine(root, "reader.log"),
                    LogLevel = "error"
                })
            };

            NativeContainer? first = null;
            Parallel.For(0, 16, _ =>
            {
                var opened = readerCtx.OpenContainerByIdOrName(containerId);
                Assert.NotNull(opened);
                Interlocked.CompareExchange(ref first, opened, null);
                Assert.Same(first, opened);
            });

            Assert.Single(readerCtx.ContainersSnapshot());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task NativeContainer_Remove_PreventsFutureStart()
    {
        var root = Path.Combine(Path.GetTempPath(), "podish-native-remove-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            using var ctx = new NativeContext
            {
                Context = new PodishContext(new PodishContextOptions
                {
                    WorkDir = root,
                    LogFile = Path.Combine(root, "podish.log"),
                    LogLevel = "error"
                })
            };

            var created = ctx.CreateContainer(new PodishRunSpec { Name = "to-remove" });
            Assert.NotNull(created.Container);
            Assert.Equal(PodishNativeApi.PodOk, created.Code);

            Assert.True(created.Container!.Remove(true, 0));
            await Assert.ThrowsAsync<InvalidOperationException>(() => created.Container.StartAsync());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}