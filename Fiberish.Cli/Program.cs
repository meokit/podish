using Bifrost.Core;
using Bifrost.Memory;
using Bifrost.Loader;
using Bifrost.Syscalls;
using Bifrost.Native;
using Microsoft.Extensions.Logging;
using Bifrost.Diagnostics;
using System.CommandLine;


namespace Bifrost;

class Program
{
    private static ILogger Logger = null!;
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
            Configuration.Verbose = verbose;
            Configuration.ShowStats = stats;
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


        // Combine exe and additional args
        string[] fullArgs = new[] { exe }.Concat(exeArgs).ToArray();
        string[] envs = new string[] 
        { 
            "PATH=/bin:/usr/bin:/sbin:/usr/sbin", 
            "HOME=/", 
            "TERM=xterm",
            "USER=root" 
        };

        // 1. Create Kernel Scheduler
        var scheduler = new KernelScheduler();
        scheduler.LoggerFactory = Logging.LoggerFactory;
        
        // 2. Spawn Process
        try
        {
            var mainTask = Process.Spawn(exe, fullArgs, envs, rootfs, traceInstruction, trace);
            
            Logger.LogInformation("Spawned Main Task {TID}", mainTask.TID);

            // 3. Run Scheduler
            scheduler.Run();

            return mainTask.ExitStatus;
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 127;
        }
        catch (Exception ex)
        {
            Logger.LogCritical(ex, "Critical Error during emulation");
            return 1;
        }
    }

}
