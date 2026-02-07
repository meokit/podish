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

        var statsOption = new Option<bool>(
            name: "--stats",
            description: "Show TLB statistics on exit");

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
            statsOption,
            exeArgument,
            argsArgument
        };

        rootCommand.SetHandler(async (rootfs, verbose, trace, stats, exe, exeArgs) =>
        {
            Verbose = verbose;
            ShowStats = stats;
            Environment.ExitCode = await RunEmulator(rootfs, verbose, trace, stats, exe, exeArgs);
        }, rootOption, verboseOption, traceOption, statsOption, exeArgument, argsArgument);

        return await rootCommand.InvokeAsync(args);
    }

    static async Task<int> RunEmulator(string rootfs, bool verbose, bool trace, bool stats, string exe, string[] exeArgs)
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

    private static void GlobalFaultHandler(Engine eng, uint addr, bool isWrite)
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
            }
        }
        else
        {
            Logger.LogError("[Unknown Task - Eng: 0x{Eng:x}] SegFault at 0x{Addr:x} EIP=0x{Eip:x} - {Registers}", eng.State, addr, eng.Eip, eng.ToString());
            eng.SetStatusFault();
        }
    }
}
