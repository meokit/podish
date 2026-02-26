using System.CommandLine;
using System.Runtime.InteropServices;
using System.Text;
using Fiberish.Core;
using Fiberish.Core.VFS.TTY;
using Fiberish.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

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

        var overlayOption = new Option<bool>(
            new[] { "--overlay", "-o" },
            "Enable OverlayFS for the root filesystem");

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
            overlayOption,
            exeArgument,
            argsArgument
        };

        rootCommand.SetHandler(async context =>
        {
            var rootfs = context.ParseResult.GetValueForOption(rootOption)!;
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var trace = context.ParseResult.GetValueForOption(traceOption);
            var traceInstruction = context.ParseResult.GetValueForOption(traceInstructionOption);
            var stats = context.ParseResult.GetValueForOption(statsOption);
            var dumpBlocks = context.ParseResult.GetValueForOption(dumpBlocksOption);
            var overlay = context.ParseResult.GetValueForOption(overlayOption);
            var exe = context.ParseResult.GetValueForArgument(exeArgument);
            var exeArgs = context.ParseResult.GetValueForArgument(argsArgument) ?? Array.Empty<string>();

            Configuration.Verbose = verbose;
            Configuration.ShowStats = stats;
            var exitCode = await RunEmulator(rootfs, verbose, trace, overlay, exe,
                exeArgs);

            context.ExitCode = exitCode;
        });

        return await rootCommand.InvokeAsync(args);
    }

#pragma warning disable CS1998 // Async method lacks await operators
    private static async Task<int> RunEmulator(string rootfs, bool verbose, bool trace, bool overlay, string exe,
        string[] exeArgs)
    {
        // Initialize Logging
        Logging.LoggerFactory = LoggerFactory.Create(builder =>
        {
            if (verbose || trace) builder.AddSimpleFile("emulator.log");
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
        // Only enable raw mode if input is not redirected;
        var isInteractive = !Console.IsInputRedirected;
        TtyDiscipline? tty = null;
        CancellationTokenSource? inputCts = null;
        Task? inputTask = null;
        FileStream? stdinStream = null;
        PosixSignalRegistration? sigwinch = null;

        if (isInteractive)
        {
            var driver = new ConsoleTtyDriver();
            var broadcaster = new SchedulerSignalBroadcaster(scheduler);
            tty = new TtyDiscipline(driver, broadcaster, Logging.CreateLogger<TtyDiscipline>());
            driver.BindTty(tty);

            // Set TTY on scheduler so it can check for pending input
            scheduler.Tty = tty;

            // Open stdin stream BEFORE enabling raw mode, in case opening it resets terminal attributes
            // Bypass Console.OpenStandardInput() to avoid potential TTY state reset
            // OwnsHandle=true to ensure closing the stream closes the FD, unblocking ReadAsync
            stdinStream = new FileStream(new SafeFileHandle(0, true), FileAccess.Read);

            // Enable Raw Mode
            // TODO: Detect OS and use appropriate Termios (Only MacOS for now)
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var res = MacOSTermios.EnableRawMode(0); // Stdin FD
                if (res != 0) Console.Error.WriteLine($"Warning: Failed to enable raw mode: {res}");
            }

            // Start Input Loop
            inputCts = new CancellationTokenSource();
            inputTask = Task.Run(() => InputLoop(tty, stdinStream, inputCts.Token));

            // Register SIGWINCH handler (Unix only)
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                try
                {
                    // Initial size update
                    try
                    {
                        if (!Console.IsOutputRedirected)
                            tty.Device.EnqueueResize(Console.WindowHeight, Console.WindowWidth);
                    }
                    catch
                    {
                        // Ignore
                    }

                    sigwinch = PosixSignalRegistration.Create(PosixSignal.SIGWINCH, context =>
                    {
                        context.Cancel = true;
                        try
                        {
                            if (!Console.IsOutputRedirected)
                                tty.Device.EnqueueResize(Console.WindowHeight, Console.WindowWidth);
                        }
                        catch
                        {
                            // Ignore
                        }
                    });
                }
                catch
                {
                    // Ignore platform not supported etc.
                }
        }

        try
        {
            // 3. Bootstrap runtime and first process
            var runtime = KernelRuntime.Bootstrap(rootfs, trace, overlay, tty);

            // Resolve exe path for the initial process
            var (loc, guestPath) = runtime.Syscalls.ResolvePath(exe, true);

            if (!loc.IsValid) throw new FileNotFoundException($"Could not find executable in VFS: {exe}");

            var mainTask = ProcessFactory.CreateInitProcess(runtime, loc.Dentry!, guestPath, fullArgs, envs, scheduler,
                tty, loc.Mount!);

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
            // Close stdin to unblock InputLoop (ReadAsync should throw or return 0)
            stdinStream?.Dispose();

            if (inputCts != null)
            {
                inputCts.Cancel();
                // Wait briefly for the task to finish, but don't block forever if read is stuck
                if (inputTask != null)
                    try
                    {
                        await Task.WhenAny(inputTask, Task.Delay(100));
                    }
                    catch
                    {
                    }
            }

            if (isInteractive && RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                // Use stdout (fd 1) to restore terminal settings because fd 0 is closed
                MacOSTermios.DisableRawMode(1);
        }
    }

    private static async Task InputLoop(TtyDiscipline tty, Stream stdin, CancellationToken token)
    {
        var buffer = new byte[256];
        // Stream passed from main thread
        try
        {
            while (!token.IsCancellationRequested)
            {
                // We use ReadAsync but Console Stream on some platforms might block even with cancellation?
                // On .NET 8 it should be better.
                var read = await stdin.ReadAsync(buffer, token);
                if (read == 0) break; // EOF

                Logger.LogDebug("[InputLoop] Received {Count} bytes: {Bytes}", read,
                    BitConverter.ToString(buffer, 0, read));
                tty.Input(buffer.AsSpan(0, read).ToArray());
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Logger might be disposed if we are shutting down?
            // Use Console.Error just in case
            Console.Error.WriteLine($"[InputLoop] Error: {ex.Message}");
        }
    }

    private class ConsoleTtyDriver : ITtyDriver
    {
        private static readonly ILogger TtyWriteLogger = Logging.CreateLogger<ConsoleTtyDriver>();
        private readonly Stream _stderr = Console.OpenStandardError();
        private readonly Stream _stdout = Console.OpenStandardOutput();
        private TtyDiscipline? _tty;

        public int Write(TtyEndpointKind kind, ReadOnlySpan<byte> buffer)
        {
            // Diagnose terminal query stalls: many shells issue CSI 6n and wait for a response.
            if (buffer.Length >= 4 && buffer[0] == 0x1B && buffer[1] == (byte)'[')
            {
                var isDsrCursorQuery = buffer.Length == 4 && buffer[2] == (byte)'6' && buffer[3] == (byte)'n';
                if (isDsrCursorQuery)
                {
                    TtyWriteLogger.LogInformation("[ConsoleTtyDriver] OUT CSI 6n (cursor position query)");
                    RespondCursorPosition();
                }
                else
                {
                    var seq = Encoding.ASCII.GetString(buffer.ToArray());
                    TtyWriteLogger.LogDebug("[ConsoleTtyDriver] OUT ESC sequence: {Seq}",
                        seq.Replace("\u001b", "<ESC>"));
                }
            }

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

        public void BindTty(TtyDiscipline tty)
        {
            _tty = tty;
        }

        private void RespondCursorPosition()
        {
            var tty = _tty;
            if (tty == null) return;

            var (row, col, isReal) = TryGetCursorPosition();
            var response = Encoding.ASCII.GetBytes($"\u001b[{row};{col}R");
            tty.InjectTerminalResponse(response);

            if (isReal)
                TtyWriteLogger.LogInformation("[ConsoleTtyDriver] IN CSI {Row};{Col}R (real cursor position)", row,
                    col);
            else
                TtyWriteLogger.LogInformation("[ConsoleTtyDriver] IN CSI {Row};{Col}R (fallback cursor position)", row,
                    col);
        }

        private static (int Row, int Col, bool IsReal) TryGetCursorPosition()
        {
            try
            {
                if (Console.IsOutputRedirected) return (1, 1, false);
                var pos = Console.GetCursorPosition();
                return (Math.Max(1, pos.Top + 1), Math.Max(1, pos.Left + 1), true);
            }
            catch
            {
                return (1, 1, false);
            }
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

/// <summary>
///     Simple file logger provider that writes logs to a file.
/// </summary>
file class FileLoggerProvider : ILoggerProvider
{
    private readonly string _filePath;
    private readonly object _lock = new();
    private StreamWriter? _writer;

    public FileLoggerProvider(string filePath)
    {
        _filePath = filePath;
        // Delete old log file
        if (File.Exists(filePath)) File.Delete(filePath);
        _writer = new StreamWriter(File.Open(filePath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true
        };
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new FileLogger(categoryName, this);
    }

    public void Dispose()
    {
        _writer?.Dispose();
        _writer = null;
    }

    public void WriteLog(string message)
    {
        lock (_lock)
        {
            _writer?.WriteLine(message);
        }
    }
}

file class FileLogger : ILogger
{
    private readonly string _categoryName;
    private readonly FileLoggerProvider _provider;

    public FileLogger(string categoryName, FileLoggerProvider provider)
    {
        _categoryName = categoryName;
        _provider = provider;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var timestamp = DateTime.UtcNow.ToString("HH:mm:ss.fff");
        var level = logLevel switch
        {
            LogLevel.Trace => "TRCE",
            LogLevel.Debug => "DBUG",
            LogLevel.Information => "INFO",
            LogLevel.Warning => "WARN",
            LogLevel.Error => "FAIL",
            LogLevel.Critical => "CRIT",
            _ => logLevel.ToString().ToUpper()[..4]
        };
        var message = $"[{timestamp}] [{level}] {_categoryName}: {formatter(state, exception)}";
        if (exception != null) message += $"\n{exception}";
        _provider.WriteLog(message);
    }
}