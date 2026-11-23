using System.Collections.Concurrent;
using Fiberish.Core.VFS.TTY;
using Fiberish.Memory;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Fiberish.X86.Native;

namespace Fiberish.Core;

/// <summary>
///     Global runtime objects that should be initialized once for the first process.
/// </summary>
public sealed class KernelRuntime : IDisposable
{
    private KernelRuntime(MemoryRuntimeContext memoryContext, Engine engine, VMAManager memory, SyscallManager syscalls,
        Configuration configuration, DeviceNumberManager deviceNumbers)
    {
        MemoryContext = memoryContext;
        Engine = engine;
        Memory = memory;
        Syscalls = syscalls;
        Configuration = configuration;
        DeviceNumbers = deviceNumbers;
    }

    public MemoryRuntimeContext MemoryContext { get; }
    public Engine Engine { get; }
    public VMAManager Memory { get; }
    public SyscallManager Syscalls { get; }
    public Configuration Configuration { get; }
    public DeviceNumberManager DeviceNumbers { get; }

    public bool EnableGuestStatsCollection { get; set; }
    public ConcurrentQueue<Engine> RetiredEngines { get; } = new();

    public void Dispose()
    {
        // Host presentation can still be reading guest pages while the runtime exits.
        Syscalls.Framebuffer?.StopPresentation();
        while (RetiredEngines.TryDequeue(out var engine)) engine.Dispose();
        Engine.Dispose();
        MemoryContext.Dispose();
    }

    /// <summary>Export quiescent engines' cumulative Continue edges, without ASLR addresses.</summary>
    public unsafe void DumpSequenceProfile(Stream output)
    {
        var pairs = new Dictionary<(uint First, uint Second), ulong>();
        var ngrams = new Dictionary<(uint First, uint Second, uint Third, uint? Fourth), ulong>();
        var handlerIds = new Dictionary<IntPtr, uint>();
        uint OperationId(IntPtr handler)
        {
            if (handlerIds.TryGetValue(handler, out var id)) return id;
            id = X86Native.GetTrainingHandlerId(handler);
            if (id == 0 || id >> 30 != 0)
                throw new InvalidDataException($"Sequence profile handler has no scalar training ID (0x{id:x8})");
            handlerIds.Add(handler, id);
            return id;
        }
        foreach (var engine in new[] { Engine }.Concat(RetiredEngines).DistinctBy(engine => engine.State))
        {
            var count = checked((int)X86Native.GetSequenceProfile(engine.State, null, 0));
            var entries = new X86Native.SequenceProfileEntry[count];
            fixed (X86Native.SequenceProfileEntry* pointer = entries)
                if (X86Native.GetSequenceProfile(engine.State, pointer, (nuint)count) != (nuint)count)
                    throw new InvalidOperationException("Profile changed during quiescent export");
            foreach (var entry in entries)
            {
                var key = (OperationId(entry.First), OperationId(entry.Second));
                pairs[key] = checked(pairs.GetValueOrDefault(key) + entry.Count);
            }
            var ngramCount = checked((int)X86Native.GetNGramProfile(engine.State, null, 0));
            var ngramEntries = new X86Native.NGramProfileEntry[ngramCount];
            fixed (X86Native.NGramProfileEntry* pointer = ngramEntries)
                if (X86Native.GetNGramProfile(engine.State, pointer, (nuint)ngramCount) != (nuint)ngramCount)
                    throw new InvalidOperationException("NGram profile changed during quiescent export");
            foreach (var entry in ngramEntries)
            {
                var key = (OperationId(entry.First), OperationId(entry.Second), OperationId(entry.Third),
                    entry.Fourth == IntPtr.Zero ? (uint?)null : OperationId(entry.Fourth));
                ngrams[key] = checked(ngrams.GetValueOrDefault(key) + entry.Count);
            }
        }
        using var writer = new System.Text.Json.Utf8JsonWriter(output, new() { Indented = true });
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", 3);
        writer.WriteString("counterSemantics", "within-block-continue-edges-v1");
        writer.WriteNumber("buildFeatures", X86Native.GetBuildFeatures());
        writer.WriteString("superOpcodeSetHash", X86Native.GetSuperOpcodeSetHash());
        writer.WriteStartObject("operations");
        if (pairs.Count != 0)
            foreach (var id in Engine.GetTrainingOperationIds())
                writer.WriteString(id.ToString(System.Globalization.CultureInfo.InvariantCulture), X86Native.GetTrainingOperationName(id));
        writer.WriteEndObject();
        writer.WriteStartArray("pairs");
        foreach (var (pair, count) in pairs.OrderBy(entry => entry.Key.First)
                     .ThenBy(entry => entry.Key.Second))
        {
            writer.WriteStartObject();
            writer.WriteNumber("first", pair.First);
            writer.WriteNumber("second", pair.Second);
            writer.WriteNumber("count", count);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteString("ngramSemantics", "consecutive-within-block-dispatch-entries-v1");
        writer.WriteStartArray("ngrams");
        foreach (var (sequence, count) in ngrams.OrderBy(entry => entry.Key.First)
                     .ThenBy(entry => entry.Key.Second).ThenBy(entry => entry.Key.Third)
                     .ThenBy(entry => entry.Key.Fourth))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("ops");
            writer.WriteNumberValue(sequence.First);
            writer.WriteNumberValue(sequence.Second);
            writer.WriteNumberValue(sequence.Third);
            if (sequence.Fourth is uint fourth) writer.WriteNumberValue(fourth);
            writer.WriteEndArray();
            writer.WriteNumber("count", count);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    public void DumpAllBlocks(Stream output)
    {
        var blocks = new HashSet<IntPtr>(Engine.GetBlockPointers());
        foreach (var engine in RetiredEngines)
            blocks.UnionWith(engine.GetBlockPointers());

        Engine.WriteBlockDump(output, blocks);
    }

    public static KernelRuntime BootstrapBare(bool strace, TtyDiscipline? tty = null,
        MemoryRuntimeContext? memoryContext = null)
    {
        return BootstrapBare(strace, tty, memoryContext, initializeVdso: true);
    }

    /// <summary>Bootstrap an empty runtime, optionally deferring the vDSO until the first exec.</summary>
    public static KernelRuntime BootstrapBare(bool strace, TtyDiscipline? tty, MemoryRuntimeContext? memoryContext,
        bool initializeVdso)
    {
        var configuration = new Configuration();
        memoryContext ??= new MemoryRuntimeContext();
        var engine = new Engine(memoryContext);
        var mm = new VMAManager(memoryContext);
        var deviceNumbers = new DeviceNumberManager();

        var sys = new SyscallManager(engine, mm, 0, tty, deviceNumbers, initializeVdso)
        {
            Strace = strace
        };

        return new KernelRuntime(memoryContext, engine, mm, sys, configuration, deviceNumbers);
    }

    public static KernelRuntime Bootstrap(string rootRes, bool strace, bool useOverlay, TtyDiscipline? tty = null)
    {
        return BootstrapWithRoot(strace, sys =>
        {
            if (useOverlay)
            {
                sys.MountRootOverlay(rootRes);
                sys.MountStandardDev(tty);
                sys.MountStandardProc();
                sys.MountStandardShm();
                sys.CreateStandardTmp();
            }
            else
            {
                sys.MountRootHostfs(rootRes);
                sys.MountStandardDev(tty);
                sys.MountStandardProc();
                sys.MountStandardShm();
            }
        }, tty);
    }

    public static KernelRuntime BootstrapWithRoot(bool strace, Action<SyscallManager> mountRoot,
        TtyDiscipline? tty = null)
    {
        var runtime = BootstrapBare(strace, tty);
        mountRoot(runtime.Syscalls);
        return runtime;
    }
}
