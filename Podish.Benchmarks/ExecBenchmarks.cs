using System.Buffers.Binary;
using System.Reflection;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Syscalls;

namespace Podish.Benchmarks;

internal static class ExecBenchmarks
{
    public static IEnumerable<IThroughputBenchmark> Create()
    {
        foreach (var count in new[] { 1, 128 })
        {
            var entries = count;
            yield return new ThroughputBenchmark<ExecScope>
            {
                Name = entries == 1 ? "execve.static.small" : "execve.static.argv-env-128",
                Category = "exec",
                CreateScope = () => new ExecScope(entries),
                Run = static (scope, iterations) =>
                {
                    for (long i = 0; i < iterations; i++) scope.Exec();
                    return iterations;
                }
            };
        }

        yield return new ThroughputBenchmark<GuestBenchmarkScope>
        {
            Name = "exec.fd-unshare.exclusive",
            Category = "exec",
            CreateScope = () => new GuestBenchmarkScope(),
            Run = static (scope, iterations) =>
            {
                for (long i = 0; i < iterations; i++) scope.Syscalls.UnshareFdTable(scope.Task);
                return iterations;
            }
        };
        yield return new ThroughputBenchmark<GuestBenchmarkScope>
        {
            Name = "exec.read-empty-vector",
            Category = "exec",
            CreateScope = () => new GuestBenchmarkScope(),
            Run = static (scope, iterations) =>
            {
                for (long i = 0; i < iterations; i++)
                {
                    var result = scope.Syscalls.ReadUserCStringVectorBytes(GuestBenchmarkScope.PathAddress);
                    if (result.Error != 0 || result.Values.Length != 0)
                        throw new InvalidOperationException("Empty vector benchmark failed.");
                }
                return iterations;
            }
        };
        yield return new ThroughputBenchmark<VectorScope>
        {
            Name = "exec.read-vector-128",
            Category = "exec",
            CreateScope = () => new VectorScope(),
            Run = static (scope, iterations) =>
            {
                for (long i = 0; i < iterations; i++)
                {
                    var result = scope.Env.Syscalls.ReadUserCStringVectorBytes(GuestBenchmarkScope.PathAddress);
                    if (result.Error != 0 || result.Values.Length != 128)
                        throw new InvalidOperationException("Vector read benchmark failed.");
                }
                return iterations;
            }
        };
    }

    private sealed class VectorScope : IDisposable
    {
        public GuestBenchmarkScope Env { get; } = new();

        public VectorScope()
        {
            Env.PutString(GuestBenchmarkScope.PathAddress + 4096, "value");
            Span<byte> vector = stackalloc byte[129 * 4];
            for (var i = 0; i < 128; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(vector.Slice(i * 4, 4), GuestBenchmarkScope.PathAddress + 4096);
            vector[^4..].Clear();
            if (!Env.Engine.CopyToUser(GuestBenchmarkScope.PathAddress, vector))
                throw new InvalidOperationException("Vector fixture write failed.");
        }

        public void Dispose() => Env.Dispose();
    }

    private sealed class ExecScope : IDisposable
    {
        private const uint Base = 0x08048000;
        private readonly GuestBenchmarkScope _env = new();
        private readonly Func<Engine, uint, uint, uint, uint, uint, uint, ValueTask<int>> _exec;

        public ExecScope(int entries)
        {
            var image = new byte[4096];
            image[0] = 0x7f; image[1] = (byte)'E'; image[2] = (byte)'L'; image[3] = (byte)'F';
            image[4] = 1; image[5] = 1; image[6] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(16), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(18), 3);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(24), Base + 512);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(28), 52);
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(40), 52);
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(42), 32);
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(44), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(52), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(60), Base);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(68), 4096);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(72), 4096);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(76), 5);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(80), 4096);
            image[512] = 0xeb; image[513] = 0xfe; // guest code is never run by this host microbenchmark
            "/image\0"u8.CopyTo(image.AsSpan(0x300));
            "X=1\0"u8.CopyTo(image.AsSpan(0x320));
            for (var i = 0; i < entries; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x400 + i * 4), Base + 0x300);
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x700 + i * 4), Base + 0x320);
            }
            _env.Syscalls.WriteGuestFile("/image", image, 0x1ed);
            _env.Engine.CurrentSyscallManager = _env.Syscalls;
            var method = typeof(SyscallManager).GetMethod("SysExecve", BindingFlags.Instance | BindingFlags.NonPublic)!;
            _exec = method.CreateDelegate<Func<Engine, uint, uint, uint, uint, uint, uint, ValueTask<int>>>(_env.Syscalls);
            _env.PutString(GuestBenchmarkScope.PathAddress, "/image");
            Check(_exec(_env.Engine, GuestBenchmarkScope.PathAddress, 0, 0, 0, 0, 0).GetAwaiter().GetResult());
        }

        // Path and vectors live in the repeatedly mapped ELF page, so neither
        // rebuilding inputs nor reflection Invoke/object[] enters measurements.
        public void Exec() => Check(_exec(_env.Engine, Base + 0x300, Base + 0x400, Base + 0x700, 0, 0, 0)
            .GetAwaiter().GetResult());

        private void Check(int result)
        {
            if (result != 0 || _env.Task.Exited)
                throw new InvalidOperationException($"exec benchmark failed: rc={result}");
        }

        public void Dispose() => _env.Dispose();
    }
}
