using Bifrost.Core;
using Bifrost.Memory;
using Bifrost.Loader;
using Bifrost.Syscalls;
using Bifrost.Native;
using Microsoft.Extensions.Logging;
using Bifrost.Diagnostics;
using Task = Bifrost.Core.Task;

namespace Bifrost;

class Program
{
    private static ILogger Logger = null!;

    static async Task<int> Main(string[] args)
    {
        string rootfs = Directory.GetCurrentDirectory();
        bool trace = false;
        int argIdx = 0;

        while (argIdx < args.Length && args[argIdx].StartsWith("--"))
        {
            if (args[argIdx] == "--trace")
            {
                trace = true;
                argIdx++;
            }
            else if (args[argIdx] == "--rootfs" && argIdx + 1 < args.Length)
            {
                rootfs = args[argIdx + 1];
                argIdx += 2;
            }
            else
            {
                break;
            }
        }

        // Initialize Logging
        Logging.LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
        {
            builder.AddConsole(options =>
            {
                options.LogToStandardErrorThreshold = LogLevel.Trace;
            });
            builder.SetMinimumLevel(trace ? LogLevel.Trace : LogLevel.Information);
        });
        Logger = Logging.CreateLogger<Program>();

        if (argIdx >= args.Length)
        {
            Console.Error.WriteLine("Usage: Bifrost [--rootfs <path>] [--trace] <native_binary> [args...]");
            return 1;
        }

        string exe = args[argIdx];
        string[] exeArgs = args.Skip(argIdx).ToArray();
        string[] envs = Environment.GetEnvironmentVariables()
            .Keys.Cast<string>()
            .Select(k => $"{k}={Environment.GetEnvironmentVariable(k)}")
            .ToArray();

        // 1. Init Emulator
        // Note: Engine is IDisposable. We should manage its lifecycle carefully with Tasks.
        // Initial engine for loading.
        var engine = new Engine();

        // 2. Init VMA Manager
        var mm = new VMAManager();

        // 3. Setup Fault Handler Wrapper
        // This global handler delegates to the specific Task's handler
        engine.FaultHandler = GlobalFaultHandler;

        // 4. Setup Syscalls (Init VFS)
        var sys = new SyscallManager(engine, mm, 0, rootfs);
        sys.Strace = trace;

        // 5. Load ELF (Using VFS)
        var res = ElfLoader.Load(exe, sys, exeArgs, envs);
        sys.BrkAddr = res.BrkAddr;

        // 6. Setup CPU State (before stack write)
        engine.Eip = res.Entry;
        engine.RegWrite(Reg.ESP, res.SP);
        engine.Eflags = 0x202;

        // 7. Create Main Task (BEFORE writing stack to avoid "Unknown Task" faults)
        var proc = new Process(Task.NextPID(), mm, sys);
        var mainTask = new Task(proc.TGID, proc, engine);
        Scheduler.Add(mainTask);

        // 8. Setup Stack (after Task is registered so fault handler can find it)
        engine.MemWrite(res.SP, res.InitialStack);

        // 9. Setup Callbacks
        sys.ExitHandler = (eng, code, group) =>
        {
            var t = Scheduler.CurrentTask ?? Scheduler.GetByEngine(eng.State);
            if (t == null) return; 

            if (group)
            {
                // mark the whole process as exiting
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
                // Single thread exit - if it's the main thread (TID == TGID), mark process as zombie
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
            // Use current task's syscall manager (usually same as sys for main thread)
            var t = Scheduler.CurrentTask;
            if (t != null)
            {
                return t.Process.Syscalls.Handle(eng, vec);
            }
            return sys.Handle(eng, vec); // Fallback
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
                
                // Start child in background without blocking
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
                Logger.LogError("[Task {TID}] SegFault at 0x{Addr:x} EIP=0x{Eip:x}", t.TID, addr, eng.Eip);
                eng.SetStatusFault();
            }
        }
        else
        {
            Logger.LogError("[Unknown Task - Eng: 0x{Eng:x}] SegFault at 0x{Addr:x} EIP=0x{Eip:x}", eng.State, addr, eng.Eip);
            eng.SetStatusFault();
        }
    }
}
