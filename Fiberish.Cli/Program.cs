using System.CommandLine;
using Fiberish.Core;
using Fiberish.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Fiberish.Cli;

internal class Program
{
    private static ILogger Logger = null!;
    public static readonly DateTime StartTime = DateTime.UtcNow;

    private static async Task<int> Main(string[] args)
    {
        var rootOption = new Option<string>(
            new[] { "--rootfs", "-r" },
            () => Directory.GetCurrentDirectory(),
            "Set root filesystem path");

        var verboseOption = new Option<bool>(
            new[] { "--verbose", "-v" },
            "Show verbose output (scheduler, loader info)");

        var traceOption = new Option<bool>(
            new[] { "--trace", "-t" },
            "Enable syscall tracing and detailed logging");

        var traceInstructionOption = new Option<bool>(
            new[] { "--trace-instruction" },
            "Enable instruction tracing");

        var statsOption = new Option<bool>(
            "--stats",
            "Show TLB statistics on exit");

        var dumpBlocksOption = new Option<string>(
            "--dump-blocks",
            "Dump Basic Blocks to file on exit");

        var exeArgument = new Argument<string>(
            "executable",
            "Path to the Linux executable to run");

        var argsArgument = new Argument<string[]>(
            "args",
            () => Array.Empty<string>(),
            "Arguments to pass to the executable");

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
                var exitCode = await RunEmulator(rootfs, verbose, trace, traceInstruction, stats, dumpBlocks, exe,
                    exeArgs);
                if (!string.IsNullOrEmpty(dumpBlocks))
                {
                    // Assuming single engine/process for now, dumping from the last used engine would be ideal
                    // But RunEmulator creates Engine locally.
                    // We need to capture the Engine instance or pass the dump path to RunEmulator.
                    // Let's modify RunEmulator to take the dump path.
                }

                Environment.ExitCode = exitCode;
            }, rootOption, verboseOption, traceOption, traceInstructionOption, statsOption, dumpBlocksOption,
            exeArgument,
            argsArgument);

        return await rootCommand.InvokeAsync(args);
    }

#pragma warning disable CS1998 // Async method lacks await operators
    private static async Task<int> RunEmulator(string rootfs, bool verbose, bool trace, bool traceInstruction,
        bool stats, string dumpBlocksDir, string exe, string[] exeArgs)
    {
        // Initialize Logging
        Logging.LoggerFactory = LoggerFactory.Create(builder =>
        {
            if (verbose || trace)
                builder.AddConsole(options => { options.LogToStandardErrorThreshold = LogLevel.Trace; });
            builder.SetMinimumLevel(trace ? LogLevel.Trace : verbose ? LogLevel.Information : LogLevel.Warning);
        });
        Logger = Logging.CreateLogger<Program>();


        // Combine exe and additional args
        var fullArgs = new[] { exe }.Concat(exeArgs).ToArray();
        var envs = new[]
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