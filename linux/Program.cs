using Bifrost.Core;
using Bifrost.Memory;
using Bifrost.Loader;
using Bifrost.Syscalls;
using Bifrost.Native;
using Task = Bifrost.Core.Task;

namespace Bifrost;

class Program
{
    static async System.Threading.Tasks.Task Main(string[] args)
    {
        string rootfs = "/";
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

        if (argIdx >= args.Length)
        {
            Console.WriteLine("Usage: Bifrost [--rootfs <path>] [--trace] <native_binary> [args...]");
            return;
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

        // 4. Load ELF
        var res = ElfLoader.Load(exe, mm, exeArgs, envs);

        // 5. Setup Stack
        engine.MemWrite(res.SP, res.InitialStack);

        // 6. Setup CPU State
        engine.Eip = res.Entry;
        engine.RegWrite(Reg.ESP, res.SP);
        engine.Eflags = 0x202;

        // 7. Setup Syscalls
        var sys = new SyscallManager(engine, mm, res.BrkAddr);
        sys.Strace = trace;
        sys.RootFS = rootfs;

        // 8. Create Main Task
        var proc = new Process(Task.NextPID(), mm, sys);
        var mainTask = new Task(proc.TGID, proc, engine);
        Scheduler.Add(mainTask);

        // 9. Setup Callbacks
        sys.ExitHandler = (eng, code, group) =>
        {
            var t = Scheduler.CurrentTask; 
            // Fallback if not set (should be set in RunLoop)
            if (t == null) return; 

            Console.WriteLine($"[Task {t.TID}] Exit Code: {code} Group: {group}");
            
            if (group)
            {
                 // Exit all tasks in process
                 // For now, just exit app
                 Environment.Exit(code);
            }
            else
            {
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
                Console.WriteLine($"Clone failed: {ex}");
                return (-1, ex);
            }
        };

        Console.WriteLine($"Starting execution at 0x{res.Entry:x}, SP=0x{res.SP:x}");
        
        // 10. Run
        await mainTask.RunLoopAsync();
    }
    
    private static void GlobalFaultHandler(Engine eng, uint addr, bool isWrite)
    {
        var t = Scheduler.CurrentTask ?? Scheduler.GetByEngine(eng.State);
        if (t != null)
        {
            if (!t.Process.Mem.HandleFault(addr, isWrite, eng))
            {
                Console.WriteLine($"[Task {t.TID}] SegFault at 0x{addr:x} EIP=0x{eng.Eip:x}");
                eng.SetStatusFault();
            }
        }
        else
        {
            Console.WriteLine($"[Unknown Task - Eng: 0x{eng.State:x}] SegFault at 0x{addr:x} EIP=0x{eng.Eip:x}");
            eng.SetStatusFault();
        }
    }
}
