using Fiberish.Core;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;

namespace Podish.Benchmarks;

internal static class InotifyBenchmarks
{
    public static IEnumerable<IThroughputBenchmark> Create()
    {
        yield return new ThroughputBenchmark<RaiseScope>
        {
            Name = "inotify.raise.no-watch",
            Category = "inotify",
            CreateScope = () => new RaiseScope(watch: false),
            Run = static (scope, iterations) =>
            {
                for (var i = 0; i < iterations; i++)
                    InotifyEvents.Raise(scope.Inode, LinuxConstants.IN_MODIFY);
                return iterations;
            }
        };

        yield return new ThroughputBenchmark<RaiseScope>
        {
            Name = "inotify.raise.one-watch",
            Category = "inotify",
            CreateScope = () => new RaiseScope(watch: true),
            Run = static (scope, iterations) =>
            {
                Span<byte> buffer = stackalloc byte[256];
                for (var i = 0; i < iterations; i++)
                {
                    InotifyEvents.Raise(scope.Inode, LinuxConstants.IN_MODIFY);
                    _ = GuestBenchmarkScope.Drain(scope.Watch!, buffer);
                }
                return iterations;
            }
        };

        yield return new ThroughputBenchmark<RaiseScope>
        {
            Name = "inotify.raise.one-watch.coalesced",
            Category = "inotify",
            CreateScope = () => new RaiseScope(watch: true),
            Run = static (scope, iterations) =>
            {
                for (var i = 0; i < iterations; i++)
                    InotifyEvents.Raise(scope.Inode, LinuxConstants.IN_MODIFY);
                return iterations;
            }
        };

        yield return new ThroughputBenchmark<FanoutScope>
        {
            Name = "inotify.raise.fanout-16",
            Category = "inotify",
            CreateScope = () => new FanoutScope(16),
            Run = static (scope, iterations) =>
            {
                Span<byte> buffer = stackalloc byte[256];
                for (var i = 0; i < iterations; i++)
                {
                    InotifyEvents.Raise(scope.Inode, LinuxConstants.IN_MODIFY);
                    foreach (var watch in scope.Watches)
                        _ = GuestBenchmarkScope.Drain(watch, buffer);
                }
                return iterations;
            }
        };

        yield return new ThroughputBenchmark<WatchMutationScope>
        {
            Name = "inotify.watch.add-remove",
            Category = "inotify",
            CreateScope = () => new WatchMutationScope(),
            Run = static (scope, iterations) =>
            {
                Span<byte> buffer = stackalloc byte[256];
                for (var i = 0; i < iterations; i++)
                {
                    var wd = scope.Instance.AddWatch(LinuxConstants.IN_MODIFY, false,
                        scope.Inode.SuperBlock, scope.Inode.Ino, scope.Inode);
                    if (wd <= 0) throw new InvalidOperationException($"AddWatch failed: {wd}");
                    if (scope.Instance.RemoveWatch(wd) != 0)
                        throw new InvalidOperationException("RemoveWatch failed.");
                    _ = GuestBenchmarkScope.Drain(scope.Instance, buffer);
                }
                return iterations;
            }
        };

        yield return new ThroughputBenchmark<WatchMutationScope>
        {
            Name = "inotify.watch.update-existing",
            Category = "inotify",
            CreateScope = () => new WatchMutationScope(),
            Run = static (scope, iterations) =>
            {
                for (var i = 0; i < iterations; i++)
                {
                    var wd = scope.Instance.AddWatch(LinuxConstants.IN_MODIFY, false,
                        scope.Inode.SuperBlock, scope.Inode.Ino, scope.Inode);
                    if (wd <= 0) throw new InvalidOperationException($"AddWatch failed: {wd}");
                }
                return iterations;
            }
        };

        yield return new ThroughputBenchmark<RaiseScope>
        {
            Name = "inotify.finalization-scope.no-watch",
            Category = "inotify",
            CreateScope = () => new RaiseScope(watch: false),
            Run = static (scope, iterations) =>
            {
                var runtime = scope.Env.Sb.MemoryContext.Inotify;
                for (var i = 0; i < iterations; i++)
                {
                    using (runtime.SuspendFinalization(scope.Inode))
                    {
                    }
                }
                return iterations;
            }
        };

        yield return new ThroughputBenchmark<RaiseChildScope>
        {
            Name = "inotify.raise-child.parent-and-inode",
            Category = "inotify",
            CreateScope = () => new RaiseChildScope(),
            Run = static (scope, iterations) =>
            {
                Span<byte> buffer = stackalloc byte[256];
                for (var i = 0; i < iterations; i++)
                {
                    InotifyEvents.RaiseChild(scope.Dentry, scope.Inode, LinuxConstants.IN_MODIFY);
                    _ = GuestBenchmarkScope.Drain(scope.ParentWatch, buffer);
                    _ = GuestBenchmarkScope.Drain(scope.InodeWatch, buffer);
                }
                return iterations;
            }
        };

        yield return new ThroughputBenchmark<OpenCloseScope>
        {
            Name = "vfs.open-close.no-watch",
            Category = "vfs",
            CreateScope = () => new OpenCloseScope(watch: false),
            Run = static (scope, iterations) =>
            {
                for (var i = 0; i < iterations; i++)
                {
                    var fd = scope.Env.OpenPrepared();
                    if (fd < 0) throw new InvalidOperationException($"open failed: {fd}");
                    scope.Env.Syscalls.FreeFD(fd);
                }
                return iterations;
            }
        };

        yield return new ThroughputBenchmark<OpenCloseScope>
        {
            Name = "vfs.open-close.watched",
            Category = "vfs",
            CreateScope = () => new OpenCloseScope(watch: true),
            Run = static (scope, iterations) =>
            {
                Span<byte> buffer = stackalloc byte[256];
                for (var i = 0; i < iterations; i++)
                {
                    var fd = scope.Env.OpenPrepared();
                    if (fd < 0) throw new InvalidOperationException($"open failed: {fd}");
                    scope.Env.Syscalls.FreeFD(fd);
                    _ = GuestBenchmarkScope.Drain(scope.Watch!, buffer);
                }
                return iterations;
            }
        };

        yield return new ThroughputBenchmark<RaiseScope>
        {
            Name = "vfs.pathwalk.file",
            Category = "vfs",
            CreateScope = () => new RaiseScope(watch: false),
            Run = static (scope, iterations) =>
            {
                for (var i = 0; i < iterations; i++)
                {
                    var loc = scope.Env.Syscalls.PathWalk("/bench/file");
                    if (!loc.IsValid) throw new InvalidOperationException("path walk failed.");
                }
                return iterations;
            }
        };
    }

    private sealed class RaiseScope : IDisposable
    {
        public RaiseScope(bool watch)
        {
            Env = new GuestBenchmarkScope();
            Env.Syscalls.WriteGuestFile("/bench/file", new byte[4096]);
            Inode = Env.InodeOf("/bench/file");
            if (watch) Watch = Env.Watch(Inode, LinuxConstants.IN_MODIFY);
        }

        public GuestBenchmarkScope Env { get; }
        public Inode Inode { get; }
        public InotifyInode? Watch { get; }

        public void Dispose() => Env.Dispose();
    }

    private sealed class FanoutScope : IDisposable
    {
        public FanoutScope(int count)
        {
            Env = new GuestBenchmarkScope();
            Env.Syscalls.WriteGuestFile("/bench/file", new byte[4096]);
            Inode = Env.InodeOf("/bench/file");
            for (var i = 0; i < count; i++)
                Watches.Add(Env.Watch(Inode, LinuxConstants.IN_MODIFY, 7300 + i));
        }

        public GuestBenchmarkScope Env { get; }
        public Inode Inode { get; }
        public List<InotifyInode> Watches { get; } = [];

        public void Dispose() => Env.Dispose();
    }

    private sealed class WatchMutationScope : IDisposable
    {
        public WatchMutationScope()
        {
            Env = new GuestBenchmarkScope();
            Env.Syscalls.WriteGuestFile("/bench/file", new byte[4096]);
            Inode = Env.InodeOf("/bench/file");
            Instance = new InotifyInode(0, Env.Sb, 7400);
        }

        public GuestBenchmarkScope Env { get; }
        public Inode Inode { get; }
        public InotifyInode Instance { get; }

        public void Dispose()
        {
            Instance.Release(null!);
            Env.Dispose();
        }
    }

    private sealed class RaiseChildScope : IDisposable
    {
        public RaiseChildScope()
        {
            Env = new GuestBenchmarkScope();
            Env.Syscalls.WriteGuestFile("/bench/file", new byte[4096]);
            var loc = Env.Syscalls.PathWalk("/bench/file");
            Dentry = loc.Dentry!;
            Inode = Dentry.Inode!;
            ParentWatch = Env.Watch(Env.Sb.Root.Inode!, LinuxConstants.IN_MODIFY, 7500);
            InodeWatch = Env.Watch(Inode, LinuxConstants.IN_MODIFY, 7501);
        }

        public GuestBenchmarkScope Env { get; }
        public Dentry Dentry { get; }
        public Inode Inode { get; }
        public InotifyInode ParentWatch { get; }
        public InotifyInode InodeWatch { get; }

        public void Dispose() => Env.Dispose();
    }

    private sealed class OpenCloseScope : IDisposable
    {
        public OpenCloseScope(bool watch)
        {
            Env = new GuestBenchmarkScope();
            Env.Syscalls.WriteGuestFile("/bench/file", new byte[4096]);
            Env.PutString(GuestBenchmarkScope.PathAddress, "/bench/file");
            if (watch) Watch = Env.Watch(Env.InodeOf("/bench/file"), LinuxConstants.IN_OPEN | LinuxConstants.IN_CLOSE_NOWRITE);
        }

        public GuestBenchmarkScope Env { get; }
        public InotifyInode? Watch { get; }

        public void Dispose() => Env.Dispose();
    }
}
