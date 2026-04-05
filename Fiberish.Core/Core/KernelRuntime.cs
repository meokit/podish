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
    private KernelRuntime(Engine engine, VMAManager memory, SyscallManager syscalls, Configuration configuration,
        DeviceNumberManager deviceNumbers)
    {
        Engine = engine;
        Memory = memory;
        Syscalls = syscalls;
        Configuration = configuration;
        DeviceNumbers = deviceNumbers;
    }

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

    public static KernelRuntime BootstrapBare(bool strace, TtyDiscipline? tty = null)
    {
        var configuration = new Configuration();
        var engine = new Engine();
        var mm = new VMAManager();
        var deviceNumbers = new DeviceNumberManager();

        var sys = new SyscallManager(engine, mm, 0, tty, deviceNumbers)
        {
            Strace = strace
        };

        return new KernelRuntime(engine, mm, sys, configuration, deviceNumbers);
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