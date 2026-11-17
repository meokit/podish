using System.Collections.Concurrent;
using System.Text;
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
        while (RetiredEngines.TryDequeue(out var engine)) engine.Dispose();
        Engine.Dispose();
        MemoryContext.Dispose();
    }

    /// <summary>Export quiescent engines' cumulative Continue edges, without ASLR addresses.</summary>
    public unsafe void DumpSequenceProfile(Stream output)
    {
        var pairs = new Dictionary<(string First, string Second), ulong>();
        var ngrams = new Dictionary<(string First, string Second, string Third, string? Fourth), ulong>();
        var symbols = new Dictionary<IntPtr, string>();
        string Symbol(IntPtr handler)
        {
            if (!symbols.TryGetValue(handler, out var symbol))
            {
                var id = X86Native.GetHandlerId(handler);
                symbol = id < 0 ? "" : X86Native.GetHandlerSymbolById(id) ?? "";
                if (symbol.Length == 0) throw new InvalidDataException("Unresolved training handler");
                var names = System.Text.RegularExpressions.Regex.Matches(symbol, @"(?:fiberish::)?op::(Op[A-Za-z0-9_]+)");
                if (names.Count != 1) throw new InvalidDataException("Ambiguous training handler: " + symbol);
                symbol = "op::" + names[0].Groups[1].Value;
                symbols.Add(handler, symbol);
            }
            return symbol;
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
                var key = (Symbol(entry.First), Symbol(entry.Second));
                pairs[key] = checked(pairs.GetValueOrDefault(key) + entry.Count);
            }
            var ngramCount = checked((int)X86Native.GetNGramProfile(engine.State, null, 0));
            var ngramEntries = new X86Native.NGramProfileEntry[ngramCount];
            fixed (X86Native.NGramProfileEntry* pointer = ngramEntries)
                if (X86Native.GetNGramProfile(engine.State, pointer, (nuint)ngramCount) != (nuint)ngramCount)
                    throw new InvalidOperationException("NGram profile changed during quiescent export");
            foreach (var entry in ngramEntries)
            {
                var key = (Symbol(entry.First), Symbol(entry.Second), Symbol(entry.Third),
                    entry.Fourth == IntPtr.Zero ? null : Symbol(entry.Fourth));
                ngrams[key] = checked(ngrams.GetValueOrDefault(key) + entry.Count);
            }
        }
        using var writer = new System.Text.Json.Utf8JsonWriter(output, new() { Indented = true });
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", 2);
        writer.WriteString("counterSemantics", "within-block-continue-edges-v1");
        writer.WriteNumber("buildFeatures", X86Native.GetBuildFeatures());
        writer.WriteString("superOpcodeSetHash", X86Native.GetSuperOpcodeSetHash());
        writer.WriteStartArray("pairs");
        foreach (var (pair, count) in pairs.OrderBy(entry => entry.Key.First, StringComparer.Ordinal)
                     .ThenBy(entry => entry.Key.Second, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("first", pair.First);
            writer.WriteString("second", pair.Second);
            writer.WriteNumber("count", count);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteString("ngramSemantics", "consecutive-within-block-dispatch-entries-v1");
        writer.WriteStartArray("ngrams");
        foreach (var (sequence, count) in ngrams.OrderBy(entry => entry.Key.First, StringComparer.Ordinal)
                     .ThenBy(entry => entry.Key.Second, StringComparer.Ordinal).ThenBy(entry => entry.Key.Third, StringComparer.Ordinal)
                     .ThenBy(entry => entry.Key.Fourth, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("ops");
            writer.WriteStringValue(sequence.First); writer.WriteStringValue(sequence.Second); writer.WriteStringValue(sequence.Third);
            if (sequence.Fourth is not null) writer.WriteStringValue(sequence.Fourth);
            writer.WriteEndArray();
            writer.WriteNumber("count", count);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    public unsafe void DumpAllBlocks(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);

        using var writer = new BinaryWriter(output, Encoding.UTF8, true);
        var imageBase = Engine.GetNativeImageBase().ToInt64();

        // Deduplicate blocks by ptr
        var allBlocks = new HashSet<IntPtr>();
        foreach (var ptr in Engine.GetBlockPointers())
            allBlocks.Add(ptr);

        foreach (var engine in RetiredEngines)
        foreach (var ptr in engine.GetBlockPointers())
            allBlocks.Add(ptr);

        var blocks = allBlocks.ToArray();

        // Handlers are identical across engines since they map to native library functions
        var handlerTable = Engine.BuildBlockDumpHandlerTable();

        writer.Write(Engine.BlockDumpMagic);
        writer.Write(Engine.BlockDumpFormatVersion);
        writer.Write((ulong)imageBase);
        writer.Write(blocks.Length);
        writer.Write(handlerTable.Count);
        foreach (var handler in handlerTable)
        {
            writer.Write(handler.HandlerId);
            writer.Write(handler.OpId);
            writer.Write(unchecked((ulong)handler.HandlerPtr.ToInt64()));
            Engine.WriteLengthPrefixedUtf8(writer, handler.Symbol);
        }

        var handlerIdsByPtr = new Dictionary<nint, int>(handlerTable.Count);
        foreach (var handler in handlerTable)
            if (handler.HandlerPtr != IntPtr.Zero)
                handlerIdsByPtr[handler.HandlerPtr] = handler.HandlerId;

        foreach (var blockPtr in blocks)
        {
            if (blockPtr == IntPtr.Zero) continue;

            var nativeBlock = (X86Native.BasicBlock*)blockPtr;
            var startEip = nativeBlock->start_eip;
            var instCount = (uint)nativeBlock->inst_count;
            writer.Write(startEip);
            writer.Write(nativeBlock->end_eip);
            writer.Write(instCount);
            writer.Write(nativeBlock->exec_count);

            var ops = (X86Native.DecodedOp*)((byte*)nativeBlock +
                                             sizeof(X86Native.BasicBlock));
            for (var i = 0; i < instCount; i++)
            {
                var op = ops[i];
                var memPacked = Engine.PackDumpMem(op);
                writer.Write(memPacked);
                writer.Write(op.next_eip);
                writer.Write(op.len);
                writer.Write(op.modrm);
                writer.Write(op.prefixes);
                writer.Write(op.meta);
                writer.Write(Engine.ExtractDumpImm(op));
                writer.Write(0u);
                writer.Write(op.handler.ToInt64());
                var handlerId = handlerIdsByPtr.TryGetValue(op.handler, out var knownHandlerId)
                    ? knownHandlerId
                    : X86Native.GetHandlerId(op.handler);
                writer.Write(handlerId);
            }
        }
    }

    public static KernelRuntime BootstrapBare(bool strace, TtyDiscipline? tty = null,
        MemoryRuntimeContext? memoryContext = null)
    {
        var configuration = new Configuration();
        memoryContext ??= new MemoryRuntimeContext();
        var engine = new Engine(memoryContext);
        var mm = new VMAManager(memoryContext);
        var deviceNumbers = new DeviceNumberManager();

        var sys = new SyscallManager(engine, mm, 0, tty, deviceNumbers)
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
