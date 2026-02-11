using Bifrost.Core;
using Bifrost.Memory;
using Bifrost.Loader;
using Bifrost.Syscalls;
using Bifrost.Native;
using Microsoft.Extensions.Logging;
using Bifrost.Diagnostics;
using System.CommandLine;
using Task = Bifrost.Core.Task;

namespace Bifrost;

class Program
{
    private static ILogger Logger = null!;
    public static bool ShowStats { get; private set; } = false;
    public static bool Verbose { get; private set; } = false;
    public static readonly DateTime StartTime = DateTime.UtcNow;

    static async Task<int> Main(string[] args)
    {
        var rootOption = new Option<string>(
            aliases: new[] { "--rootfs", "-r" },
            getDefaultValue: () => Directory.GetCurrentDirectory(),
            description: "Set root filesystem path");

        var verboseOption = new Option<bool>(
            aliases: new[] { "--verbose", "-v" },
            description: "Show verbose output (scheduler, loader info)");

        var traceOption = new Option<bool>(
            aliases: new[] { "--trace", "-t" },
            description: "Enable syscall tracing and detailed logging");
        
        var traceInstructionOption = new Option<bool>(
            aliases: new[] { "--trace-instruction" },
            description: "Enable instruction tracing");

        var statsOption = new Option<bool>(
            name: "--stats",
            description: "Show TLB statistics on exit");

        var dumpBlocksOption = new Option<string>(
            name: "--dump-blocks",
            description: "Dump Basic Blocks to file on exit");

        var exeArgument = new Argument<string>(
            name: "executable",
            description: "Path to the Linux executable to run");

        var argsArgument = new Argument<string[]>(
            name: "args",
            getDefaultValue: () => Array.Empty<string>(),
            description: "Arguments to pass to the executable");

        var rootCommand = new RootCommand("x86emu - Linux x86 emulator")
        {
            rootOption,
            verboseOption,
            traceOption,
            traceInstructionOption,
            statsOption,
            dumpBlocksOption,
            exeArgument,
            argsArgument
        };

        rootCommand.SetHandler(async (rootfs, verbose, trace, traceInstruction, stats, dumpBlocks, exe, exeArgs) =>
        {
            Verbose = verbose;
            ShowStats = stats;
            int exitCode = await RunEmulator(rootfs, verbose, trace, traceInstruction, stats, dumpBlocks, exe, exeArgs);
            if (!string.IsNullOrEmpty(dumpBlocks))
            {
                 // Assuming single engine/process for now, dumping from the last used engine would be ideal
                 // But RunEmulator creates Engine locally.
                 // We need to capture the Engine instance or pass the dump path to RunEmulator.
                 // Let's modify RunEmulator to take the dump path.
            }
            Environment.ExitCode = exitCode;
        }, rootOption, verboseOption, traceOption, traceInstructionOption, statsOption, dumpBlocksOption, exeArgument, argsArgument);

        return await rootCommand.InvokeAsync(args);
    }

    static async Task<int> RunEmulator(string rootfs, bool verbose, bool trace, bool traceInstruction, bool stats, string dumpBlocksDir, string exe, string[] exeArgs)
    {
        // Initialize Logging
        Logging.LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
        {
            if (verbose || trace)
            {
                builder.AddConsole(options =>
                {
                    options.LogToStandardErrorThreshold = LogLevel.Trace;
                });
            }
            builder.SetMinimumLevel(trace ? LogLevel.Trace : (verbose ? LogLevel.Information : LogLevel.Warning));
        });
        Logger = Logging.CreateLogger<Program>();

        // Register Native Logger
        RegisterNativeLogger();

        // Combine exe and additional args
        string[] fullArgs = new[] { exe }.Concat(exeArgs).ToArray();
        string[] envs = new string[] 
        { 
            "PATH=/bin:/usr/bin:/sbin:/usr/sbin", 
            "HOME=/", 
            "TERM=xterm",
            "USER=root" 
        };

        // 1. Init Emulator
        var engine = new Engine();

        // 2. Init VMA Manager
        var mm = new VMAManager();

        // 3. Setup Fault Handler Wrapper
        engine.FaultHandler = GlobalFaultHandler;

        // 4. Setup Syscalls (Init VFS) & 5. Create Main Task & 6. Load ELF
        SyscallManager sys;
        Task mainTask;
        try
        {
            sys = new SyscallManager(engine, mm, 0, rootfs);
            sys.Strace = trace;
            ProcFsManager.Init(sys);

            var proc = new Process(Task.NextPID(), mm, sys);
            proc.traceInstruction = traceInstruction;
            
            mainTask = new Task(proc.TGID, proc, engine);
            Scheduler.Add(mainTask);
            ProcFsManager.OnProcessStart(sys, proc.TGID);
            Logger.LogInformation("Created Main Task {TID}, Engine 0x{Engine:x}", mainTask.TID, engine.State);

            var res = ElfLoader.Load(exe, sys, fullArgs, envs);
            sys.BrkAddr = res.BrkAddr;

            // 7. Setup CPU State
            engine.Eip = res.Entry;
            engine.RegWrite(Reg.ESP, res.SP);
            engine.Eflags = 0x202;

            // 8. Setup Stack
            uint spBase = res.SP;
            byte[] stackData = res.InitialStack;
            for (uint addr = spBase & LinuxConstants.PageMask; 
                 addr < ((spBase + (uint)stackData.Length + (uint)LinuxConstants.PageSize - 1) & LinuxConstants.PageMask); 
                 addr += (uint)LinuxConstants.PageSize)
            {
                if (engine.AllocatePage(addr, (byte)(Protection.Read | Protection.Write)) == IntPtr.Zero)
                    throw new InvalidOperationException($"Failed to allocate stack page at 0x{addr:x}");
            }

            if (!engine.CopyToUser(spBase, stackData))
                throw new InvalidOperationException("Failed to write initial stack content to guest memory");
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            Logger.LogCritical("Error: {Message}", ex.Message);
            return 127;
        }
        catch (DirectoryNotFoundException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            Logger.LogCritical("Error: {Message}", ex.Message);
            return 127;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Critical Error: {ex.Message}");
            Logger.LogCritical("Critical Error: {Message}", ex.Message);
            if (verbose) Logger.LogCritical(ex.StackTrace);
            return 1;
        }

        // 9. Setup Callbacks
        sys.ExitHandler = (eng, code, group) =>
        {
            var t = Scheduler.CurrentTask ?? Scheduler.GetByEngine(eng.State);
            if (t == null) return;

            // Dump blocks on exit if requested
            if (!string.IsNullOrEmpty(dumpBlocksDir))
            {
                DumpBlocks(eng, dumpBlocksDir, Path.GetFileName(exe), t.TID);
            }

            if (group)
            {
                lock (t.Process)
                {
                    if (t.Process.State != ProcessState.Zombie)
                    {
                        t.Process.State = ProcessState.Zombie;
                        t.Process.ExitStatus = code;
                        t.Process.ZombieEvent.Set();
                    }
                }

                t.Exited = true;
                t.ExitCode = code;
                t.CPU.Stop();
            }
            else
            {
                if (t.TID == t.Process.TGID)
                {
                    lock (t.Process)
                    {
                        t.Process.State = ProcessState.Zombie;
                        t.Process.ExitStatus = code;
                        t.Process.ZombieEvent.Set();
                    }
                }
                t.Exited = true;
                t.ExitCode = code;
                t.CPU.Stop();
            }
        };

        engine.InterruptHandler = (eng, vec) =>
        {
            var t = Scheduler.CurrentTask;
            if (t != null)
            {
                return t.Process.Syscalls.Handle(eng, vec);
            }
            return sys.Handle(eng, vec);
        };

        // Setup Dependency Injection
        sys.GetTID = (eng) => Scheduler.CurrentTask?.TID ?? 0;
        sys.GetTGID = (eng) => Scheduler.CurrentTask?.Process?.TGID ?? 0;

        sys.CloneHandler = (flags, stack, ptid, tls, ctid) =>
        {
            var parent = Scheduler.CurrentTask;
            if (parent == null) return (-1, new Exception("No parent task"));

            try
            {
                var child = parent.Clone(flags, stack, ptid, tls, ctid);
                ProcFsManager.OnProcessStart(child.Process.Syscalls, child.TID);

                _ = child.RunLoopAsync();

                return (child.TID, null);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Clone failed");
                return (-1, ex);
            }
        };

        // 10. Run
        await mainTask.RunLoopAsync();

        return mainTask.ExitCode;
    }

    private unsafe static void DumpBlocks(Engine engine, string dir, string exeName, int tid)
    {
        string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string filename = $"{exeName}-{tid}-{timestamp}.bin";
        string path = Path.Combine(dir, filename);
        
        Logger.LogInformation("Dumping Basic Blocks for Task {TID} to {Path}...", tid, path);
        try
        {
            Directory.CreateDirectory(dir);
            using var fs = File.OpenWrite(path);
            using var writer = new BinaryWriter(fs);

            // 1. Header: Base Address
            IntPtr baseAddr = X86Native.GetLibAddress();
            writer.Write((long)baseAddr);

            // 2. Block Count
            int count = X86Native.GetBlockCount(engine.State);
            writer.Write(count);
            
            Logger.LogInformation("[Task {TID}] Base Address: 0x{Base:x}, Block Count: {Count}", tid, baseAddr, count);

            if (count > 0)
            {
                IntPtr* blocks = (IntPtr*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)count * (nuint)sizeof(IntPtr));
                try
                {
                    int fetched = X86Native.GetBlockList(engine.State, blocks, count);
                    for (int i = 0; i < fetched; i++)
                    {
                        X86Native.BasicBlock* bb = (X86Native.BasicBlock*)blocks[i];
                        
                        // Block Header
                        writer.Write(bb->start_eip);
                        writer.Write(bb->end_eip);
                        writer.Write(bb->inst_count);
                        writer.Write(bb->exec_count);
                        
                        // Ops
                        X86Native.DecodedOp* ops = (X86Native.DecodedOp*)((byte*)bb + 32); // Offset 32
                        for (int j = 0; j < bb->inst_count; j++)
                        {
                            // Write raw DecodedOp (32 bytes)
                            byte* ptr = (byte*)&ops[j];
                            var span = new ReadOnlySpan<byte>(ptr, 32);
                            writer.Write(span);
                        }
                    }
                }
                finally
                {
                    System.Runtime.InteropServices.NativeMemory.Free(blocks);
                }
            }
            Logger.LogInformation("[Task {TID}] Dump complete.", tid);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[Task {TID}] Failed to dump blocks", tid);
        }
    }

    private static bool GlobalFaultHandler(Engine eng, uint addr, bool isWrite)
    {
        var t = Scheduler.CurrentTask ?? Scheduler.GetByEngine(eng.State);
        if (t != null)
        {
            if (!t.Process.Mem.HandleFault(addr, isWrite, eng))
            {
                Logger.LogError("[Task {TID}] SegFault at 0x{Addr:x} (Vector: {Vector}) EIP=0x{Eip:x} - {Registers}", t.TID, addr, eng.FaultVector, eng.Eip, eng.ToString());
                try {
                    // Use CopyFromUser instead of MemRead to avoid recursive fault
                    byte[] code = new byte[16];
                    if (eng.CopyFromUser(eng.Eip, code))
                    {
                        Logger.LogError("Code at EIP: {Code}", BitConverter.ToString(code).Replace("-", " "));
                    }
                } catch { }

                t.DumpTrace();
                t.Process.Mem.LogVMAs();
                eng.SetStatusFault();
                return false;
            }
            return true;
        }
        else
        {
            Logger.LogError("[Unknown Task - Eng: 0x{Eng:x}] SegFault at 0x{Addr:x} EIP=0x{Eip:x} - {Registers}", eng.State, addr, eng.Eip, eng.ToString());
            eng.SetStatusFault();
        }
        return false;
    }

    private static unsafe void RegisterNativeLogger()
    {
        X86Native.SetLogCallback(&LogCallback);
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static void LogCallback(int level, IntPtr messagePtr)
    {
        if (Logger == null) return;
        
        string? message = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(messagePtr);
        if (message == null) return;

        LogLevel logLevel = (LogLevel)level;
        Logger.Log(logLevel, "[Native] {Message}", message);
    }
}
