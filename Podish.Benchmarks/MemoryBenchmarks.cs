using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.VFS;

namespace Podish.Benchmarks;

internal static class MemoryBenchmarks
{
    private const uint MapAddress = 0x600000;
    private const uint MapLength = 0x10000;

    public static IEnumerable<IThroughputBenchmark> Create()
    {
        yield return new ThroughputBenchmark<MappingScope>
        {
            Name = "mmap.anon.private.fixed",
            Category = "mmap",
            CreateScope = () => new MappingScope(fileBacked: false, MapFlags.Private | MapFlags.Fixed,
                MapAddress),
            Run = static (scope, iterations) => scope.Run(iterations)
        };

        yield return new ThroughputBenchmark<MappingScope>
        {
            Name = "mmap.file.private.fixed",
            Category = "mmap",
            CreateScope = () => new MappingScope(fileBacked: true, MapFlags.Private | MapFlags.Fixed,
                MapAddress),
            Run = static (scope, iterations) => scope.Run(iterations)
        };

        yield return new ThroughputBenchmark<MappingScope>
        {
            Name = "mmap.file.shared.fixed",
            Category = "mmap",
            CreateScope = () => new MappingScope(fileBacked: true, MapFlags.Shared | MapFlags.Fixed,
                MapAddress),
            Run = static (scope, iterations) => scope.Run(iterations)
        };

        yield return new ThroughputBenchmark<MappingScope>
        {
            Name = "mmap.file.private.allocate",
            Category = "mmap",
            CreateScope = () => new MappingScope(fileBacked: true, MapFlags.Private, 0),
            Run = static (scope, iterations) => scope.Run(iterations)
        };

        yield return new ThroughputBenchmark<MappingScope>
        {
            Name = "mmap.file.private.allocate.vma-4096",
            Category = "mmap",
            CreateScope = () => new MappingScope(fileBacked: true, MapFlags.Private, 0, prepopulateVmas: 4096),
            Run = static (scope, iterations) => scope.Run(iterations)
        };
    }

    private sealed class MappingScope : IDisposable
    {
        private readonly GuestBenchmarkScope _env;
        private readonly MapFlags _flags;
        private readonly uint _address;
        private readonly LinuxFile? _file;

        public MappingScope(bool fileBacked, MapFlags flags, uint address, int prepopulateVmas = 0)
        {
            _env = new GuestBenchmarkScope();
            _env.Syscalls.WriteGuestFile("/bench/mapped", new byte[MapLength]);
            _flags = flags;
            _address = address;
            if (fileBacked)
            {
                var loc = _env.Syscalls.PathWalk("/bench/mapped");
                _file = new LinuxFile(loc, FileFlags.O_RDWR);
            }

            // A V8-like process can accumulate thousands of small mappings. Fill the
            // top of the address space so the measured auto-selected mmap must scan the
            // whole VMA list to find the next free gap. Mapping in ascending order keeps
            // setup appends O(1).
            // SetupVDSO occupies the last page, so leave it free.
            var top = _env.Mem.Layout?.TaskSize ?? LinuxConstants.TaskSize32;
            var first = top - ((uint)prepopulateVmas + 1) * LinuxConstants.PageSize;
            for (var i = 0; i < prepopulateVmas; i++)
            {
                _env.Mem.Mmap(first + (uint)i * LinuxConstants.PageSize, LinuxConstants.PageSize,
                    Protection.Read, MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0,
                    "prepopulated", _env.Engine);
            }
        }

        public long Run(long iterations)
        {
            long completed = 0;
            for (var i = 0; i < iterations; i++)
            {
                _file?.Get();
                var transferred = false;
                try
                {
                    var address = _env.Mem.Mmap(_address, MapLength, Protection.Read | Protection.Write, _flags,
                        _file, 0, "bench-mmap", _env.Engine);
                    transferred = true;
                    if (_env.Mem.Munmap(address, MapLength, _env.Engine) != 0)
                        throw new InvalidOperationException("munmap failed");
                    completed++;
                }
                finally
                {
                    if (_file != null && !transferred)
                        _file.Close();
                }
            }

            return completed;
        }

        public void Dispose()
        {
            _file?.Close();
            _env.Dispose();
        }
    }
}
