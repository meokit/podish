using System.CommandLine;
using Fiberish.Core;
using Fiberish.Diagnostics;
using Microsoft.Extensions.Logging;

using System.Runtime.InteropServices;
using Fiberish.Core.VFS.TTY;

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

        // 2. Setup TTY
        // Only enable raw mode if input is not redirected
        bool isInteractive = !Console.IsInputRedirected;
        TtyDiscipline? tty = null;
        CancellationTokenSource? inputCts = null;
        Task? inputTask = null;

        if (isInteractive)
        {
            // Enable Raw Mode
            // TODO: Detect OS and use appropriate Termios (Only MacOS for now)
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                MacOSTermios.EnableRawMode(0); // Stdin FD
            }

            var driver = new ConsoleTtyDriver();
            var broadcaster = new SchedulerSignalBroadcaster(scheduler);
            tty = new TtyDiscipline(driver, broadcaster, Logging.CreateLogger<TtyDiscipline>());

            // Start Input Loop
            inputCts = new CancellationTokenSource();
            inputTask = Task.Run(() => InputLoop(tty, inputCts.Token));
        }

        try
        {
            // 3. Spawn Process
            var mainTask = Process.Spawn(exe, fullArgs, envs, rootfs, traceInstruction, trace, scheduler, tty);

            Logger.LogInformation("Spawned Main Task {TID}", mainTask.TID);

            // 4. Run Scheduler
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
        finally
        {
            if (inputCts != null)
            {
                inputCts.Cancel();
                try { await inputTask!; } catch { }
            }

            if (isInteractive && RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                MacOSTermios.DisableRawMode(0);
            }
        }
    }

    private static async Task InputLoop(TtyDiscipline tty, CancellationToken token)
    {
        var buffer = new byte[256];
        var stdin = Console.OpenStandardInput();
        try
        {
            while (!token.IsCancellationRequested)
            {
                // We use ReadAsync but Console Stream on some platforms might block even with cancellation?
                // On .NET 8 it should be better.
                int read = await stdin.ReadAsync(buffer, token);
                if (read == 0) break; // EOF
                
                tty.Input(buffer.AsSpan(0, read).ToArray());
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // Logger might be disposed if we are shutting down?
            // Use Console.Error just in case
            Console.Error.WriteLine($"[InputLoop] Error: {ex.Message}");
        }
    }

    private class ConsoleTtyDriver : ITtyDriver
    {
        private readonly Stream _stdout = Console.OpenStandardOutput();
        private readonly Stream _stderr = Console.OpenStandardError();

        public int Write(TtyEndpointKind kind, ReadOnlySpan<byte> buffer)
        {
            var stream = kind == TtyEndpointKind.Stderr ? _stderr : _stdout;
            stream.Write(buffer);
            stream.Flush(); // Flush immediately for TTY behavior
            return buffer.Length;
        }

        public void Flush()
        {
            _stdout.Flush();
            _stderr.Flush();
        }
    }

    private class SchedulerSignalBroadcaster : ISignalBroadcaster
    {
        private readonly KernelScheduler _scheduler;

        public SchedulerSignalBroadcaster(KernelScheduler scheduler)
        {
            _scheduler = scheduler;
        }

        public void SignalProcessGroup(int pgid, int signal)
        {
            _scheduler.SignalProcessGroup(pgid, signal);
        }

        public void SignalForegroundTask(int signal)
        {
             // If we don't track foreground task here, 
             // we assume TtyDiscipline calls SignalProcessGroup with its ForegroundPgrp.
             // But TtyLogic: "if (ForegroundPgrp > 0) broadcaster.SignalProcessGroup..."
             // "else broadcaster.SignalForegroundTask(sig)" -> Logic fallback?
             
             // In single-process emulator, maybe signal current task?
             // But TtyDiscipline runs in InputLoop (thread pool or async task).
             // KernelScheduler.Current might not be set or not relevant.
             
             // Let's assume SignalProcessGroup is the primary mechanism used by TTY.
             // SignalForegroundTask might be used if no pgrp set?
             
             // For now, log warning or ignore.
             // Or maybe we can expose "MainTask" from program?
             // But TTY shouldn't know about Program static state.
        }
    }
}