using System.CommandLine;
using System.Text.Json;
using Fiberish.Diagnostics;
using Microsoft.Extensions.Logging;
using Podish.Core;

namespace Podish.Cli;

internal class Program
{
    private static ILogger Logger = null!;

    private static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("Podish.Cli - Podman-like CLI for x86emu");

        // Global Options
        var logLevelOption = new Option<string>(
            new[] { "--log-level", "-l" },
            () => "warn",
            "Log messages above specified level: debug, info, warn, error, fatal, panic");
        var logFileOption = new Option<string?>(
            new[] { "--log-file" },
            () => null,
            "Path to a file where Podish.Cli engine logs will be written");

        rootCommand.AddGlobalOption(logLevelOption);
        rootCommand.AddGlobalOption(logFileOption);

        // --- Run Command ---
        var runCommand = new Command("run", "Run a command in a new container");

        var volumeOption = new Option<string[]>(
            new[] { "--volume", "-v" },
            "Bind mount a volume (e.g. /host/path:/guest/path)")
        {
            AllowMultipleArgumentsPerToken = false
        };
        var interactiveOption = new Option<bool>(
            new[] { "--interactive", "-i" },
            "Keep STDIN open even if not attached");
        var ttyOption = new Option<bool>(
            new[] { "--tty", "-t" },
            "Allocate a pseudo-TTY");
        var straceOption = new Option<bool>(
            new[] { "--strace", "-s" },
            "Enable syscall tracing (strace-like logs)");
        var rootfsOption = new Option<string?>(
            new[] { "--rootfs" },
            "Use a local root filesystem path (Podman-compatible rootfs mode)");
        var envOption = new Option<string[]>(
            new[] { "--env", "-e" },
            "Set environment variables (e.g. -e KEY=VALUE)")
        {
            AllowMultipleArgumentsPerToken = false
        };
        var dnsOption = new Option<string[]>(
            new[] { "--dns" },
            "Set custom DNS servers")
        {
            AllowMultipleArgumentsPerToken = false
        };
        var containerLogDriverOption = new Option<string>(
            new[] { "--log-driver" },
            () => "json-file",
            "Container log driver (json-file|none)");
        var runArgsArgument = new Argument<string[]>(
            "run-args",
            () => Array.Empty<string>(),
            "IMAGE [COMMAND [ARG...]] (or COMMAND [ARG...] with --rootfs)")
        {
            Arity = ArgumentArity.ZeroOrMore
        };

        runCommand.AddOption(volumeOption);
        runCommand.AddOption(interactiveOption);
        runCommand.AddOption(ttyOption);
        runCommand.AddOption(straceOption);
        runCommand.AddOption(rootfsOption);
        runCommand.AddOption(envOption);
        runCommand.AddOption(dnsOption);
        runCommand.AddOption(containerLogDriverOption);
        runCommand.AddArgument(runArgsArgument);

        runCommand.SetHandler(async (context) =>
        {
            var volumes = context.ParseResult.GetValueForOption(volumeOption) ?? Array.Empty<string>();
            var interactive = context.ParseResult.GetValueForOption(interactiveOption);
            var tty = context.ParseResult.GetValueForOption(ttyOption);
            var strace = context.ParseResult.GetValueForOption(straceOption);
            var rootfs = context.ParseResult.GetValueForOption(rootfsOption);
            var guestEnvs = context.ParseResult.GetValueForOption(envOption) ?? Array.Empty<string>();
            var dnsServers = context.ParseResult.GetValueForOption(dnsOption) ?? Array.Empty<string>();
            var containerLogDriverRaw = context.ParseResult.GetValueForOption(containerLogDriverOption);
            var runArgs = context.ParseResult.GetValueForArgument(runArgsArgument) ?? Array.Empty<string>();
            var useRootfs = !string.IsNullOrWhiteSpace(rootfs);
            string? image = null;
            string? exe = null;
            string[] exeArgs = Array.Empty<string>();

            if (useRootfs)
            {
                if (runArgs.Length > 0)
                {
                    exe = runArgs[0];
                    exeArgs = runArgs.Skip(1).ToArray();
                }
            }
            else
            {
                if (runArgs.Length > 0)
                {
                    image = runArgs[0];
                    if (runArgs.Length > 1)
                    {
                        exe = runArgs[1];
                        exeArgs = runArgs.Skip(2).ToArray();
                    }
                }
            }

            var logLevelRaw = context.ParseResult.GetValueForOption(logLevelOption) ?? "warn";
            var logFile = context.ParseResult.GetValueForOption(logFileOption);

            var fiberpodDir = Path.Combine(Directory.GetCurrentDirectory(), ".fiberpod");
            var imagesDir = Path.Combine(fiberpodDir, "images");
            var ociStoreImagesDir = Path.Combine(fiberpodDir, "oci", "images");
            var logsDir = Path.Combine(fiberpodDir, "logs");
            var containersDir = Path.Combine(fiberpodDir, "containers");
            Directory.CreateDirectory(imagesDir);
            Directory.CreateDirectory(ociStoreImagesDir);
            Directory.CreateDirectory(logsDir);
            Directory.CreateDirectory(containersDir);

            if (logFile == null)
            {
                logFile = Path.Combine(logsDir, $"fiberpod_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            }

            if (!TryParsePodmanLogLevel(logLevelRaw, out var logLevel))
            {
                Console.Error.WriteLine(
                    $"[Podish.Cli] invalid --log-level value: {logLevelRaw}. Use debug|info|warn|error|fatal|panic");
                context.ExitCode = 125;
                return;
            }

            if (!ContainerLogDriverParser.TryParse(containerLogDriverRaw, out var containerLogDriver))
            {
                Console.Error.WriteLine(
                    $"[Podish.Cli] invalid --log-driver value: {containerLogDriverRaw}. Use json-file|none");
                context.ExitCode = 125;
                return;
            }

            SetupLogging(logLevel, logFile);

            var containerId = Guid.NewGuid().ToString("N")[..12];
            var containerDir = Path.Combine(containersDir, containerId);
            Directory.CreateDirectory(containerDir);
            var eventStore = new ContainerEventStore(Path.Combine(fiberpodDir, "events.jsonl"));

            var imageRef = image ?? rootfs ?? "<unknown>";
            var rootfsPath = rootfs ?? image ?? string.Empty;
            var safeImageName = imageRef.Replace("/", "_").Replace(":", "_");
            var pulledDir = Path.Combine(imagesDir, safeImageName);
            var ociStoreDir = Path.Combine(ociStoreImagesDir, safeImageName);

            if (!useRootfs)
            {
                if (string.IsNullOrWhiteSpace(image))
                {
                    Console.Error.WriteLine("[Podish.Cli] image is required unless --rootfs is set");
                    context.ExitCode = 125;
                    return;
                }

                if (!Directory.Exists(rootfsPath) && Directory.Exists(ociStoreDir))
                {
                    rootfsPath = ociStoreDir;
                }
                else if (!Directory.Exists(rootfsPath))
                {
                    Console.Error.WriteLine($"[Podish.Cli] Unable to find image '{image}' locally");
                    Console.Error.WriteLine($"[Podish.Cli] Trying to pull {image}...");
                    var pullService = new OciPullService(Logger);
                    try
                    {
                        await pullService.PullAndStoreImageAsync(image, ociStoreDir);
                        rootfsPath = ociStoreDir;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[Podish.Cli Error] Failed to pull image: {ex.Message}");
                        context.ExitCode = 1;
                        return;
                    }
                }
            }
            else
            {
                if (!Directory.Exists(rootfsPath))
                {
                    Console.Error.WriteLine($"[Podish.Cli Error] --rootfs path not found: {rootfsPath}");
                    context.ExitCode = 1;
                    return;
                }
            }

            Logger.LogInformation("Running image/rootfs: {Image}", imageRef);
            if (!string.IsNullOrEmpty(exe))
            {
                Logger.LogInformation("Executing: {Exe} {Args}", exe, string.Join(" ", exeArgs));
                Logger.LogInformation("Env: {Envs}", string.Join(", ", guestEnvs));
            }

            eventStore.Append(new ContainerEvent(DateTimeOffset.UtcNow, "container-create", containerId, imageRef));

            var exitCode = await RunContainer(
                rootfsPath,
                exe ?? string.Empty,
                exeArgs,
                volumes,
                guestEnvs,
                dnsServers,
                interactive && tty,
                strace,
                !useRootfs,
                containersDir,
                containerId,
                imageRef,
                containerDir,
                containerLogDriver,
                eventStore);
            context.ExitCode = exitCode;
        });

        // --- Pull Command ---
        var pullCommand = new Command("pull", "Pull an image from a registry");
        var pullImageArgument = new Argument<string>("image", "Image name to pull");
        var pullStoreOciOption = new Option<bool>(
            new[] { "--store-oci" },
            "Store image as OCI blobs + layer indexes (no host filesystem extraction)");
        pullCommand.AddArgument(pullImageArgument);
        pullCommand.AddOption(pullStoreOciOption);
        pullCommand.SetHandler(async (context) =>
        {
            var image = context.ParseResult.GetValueForArgument(pullImageArgument);
            var storeOci = context.ParseResult.GetValueForOption(pullStoreOciOption);
            var logLevelRaw = context.ParseResult.GetValueForOption(logLevelOption) ?? "warn";
            var logFile = context.ParseResult.GetValueForOption(logFileOption);

            var fiberpodDir = Path.Combine(Directory.GetCurrentDirectory(), ".fiberpod");
            var imagesDir = Path.Combine(fiberpodDir, "images");
            var logsDir = Path.Combine(fiberpodDir, "logs");
            var ociStoreImagesDir = Path.Combine(fiberpodDir, "oci", "images");
            Directory.CreateDirectory(imagesDir);
            Directory.CreateDirectory(logsDir);
            Directory.CreateDirectory(ociStoreImagesDir);

            if (logFile == null)
            {
                logFile = Path.Combine(logsDir, $"fiberpod_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            }

            if (!TryParsePodmanLogLevel(logLevelRaw, out var logLevel))
            {
                Console.Error.WriteLine(
                    $"[Podish.Cli] invalid --log-level value: {logLevelRaw}. Use debug|info|warn|error|fatal|panic");
                context.ExitCode = 125;
                return;
            }

            SetupLogging(logLevel, logFile);

            var pullService = new OciPullService(Logger);
            var safeImageName = image.Replace("/", "_").Replace(":", "_");
            var outputDir = Path.Combine(imagesDir, safeImageName);

            try
            {
                if (storeOci)
                {
                    var storeDir = Path.Combine(ociStoreImagesDir, safeImageName);
                    var stored = await pullService.PullAndStoreImageAsync(image, storeDir);
                    Console.WriteLine(
                        $"[Podish.Cli] Image {image} stored as OCI layers at {stored.StoreDirectory} ({stored.Layers.Count} layers)");
                }
                else
                {
                    await pullService.PullAndExtractImageAsync(image, outputDir);
                    Console.WriteLine($"[Podish.Cli] Image {image} pulled successfully to {outputDir}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Podish.Cli] Failed to pull image {image}: {ex.Message}");
                context.ExitCode = 1;
            }
        });

        // --- Save Command ---
        var saveCommand = new Command("save", "Save image(s) to an archive");
        var saveOutputOption = new Option<string>(
                new[] { "--output", "-o" },
                "Write to an archive file")
            { IsRequired = true };
        var saveImagesArgument = new Argument<string[]>("images", "Image references")
        {
            Arity = ArgumentArity.OneOrMore
        };
        saveCommand.AddOption(saveOutputOption);
        saveCommand.AddArgument(saveImagesArgument);
        saveCommand.SetHandler((context) =>
        {
            var logLevelRaw = context.ParseResult.GetValueForOption(logLevelOption) ?? "warn";
            var logFile = context.ParseResult.GetValueForOption(logFileOption);
            var output = context.ParseResult.GetValueForOption(saveOutputOption)!;
            var images = context.ParseResult.GetValueForArgument(saveImagesArgument) ?? Array.Empty<string>();

            var fiberpodDir = Path.Combine(Directory.GetCurrentDirectory(), ".fiberpod");
            var logsDir = Path.Combine(fiberpodDir, "logs");
            Directory.CreateDirectory(logsDir);
            if (logFile == null)
                logFile = Path.Combine(logsDir, $"fiberpod_{DateTime.Now:yyyyMMdd_HHmmss}.log");

            if (!TryParsePodmanLogLevel(logLevelRaw, out var logLevel))
            {
                Console.Error.WriteLine(
                    $"[Podish.Cli] invalid --log-level value: {logLevelRaw}. Use debug|info|warn|error|fatal|panic");
                context.ExitCode = 125;
                return;
            }

            SetupLogging(logLevel, logFile);
            try
            {
                var svc = new ImageArchiveService(Directory.GetCurrentDirectory());
                svc.Save(output, images);
                Console.WriteLine(output);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Podish.Cli] save failed: {ex.Message}");
                context.ExitCode = 1;
            }
        });

        // --- Load Command ---
        var loadCommand = new Command("load", "Load image(s) from an archive");
        var loadInputOption = new Option<string>(
                new[] { "--input", "-i" },
                "Read from archive file")
            { IsRequired = true };
        loadCommand.AddOption(loadInputOption);
        loadCommand.SetHandler((context) =>
        {
            var logLevelRaw = context.ParseResult.GetValueForOption(logLevelOption) ?? "warn";
            var logFile = context.ParseResult.GetValueForOption(logFileOption);
            var input = context.ParseResult.GetValueForOption(loadInputOption)!;

            var fiberpodDir = Path.Combine(Directory.GetCurrentDirectory(), ".fiberpod");
            var logsDir = Path.Combine(fiberpodDir, "logs");
            Directory.CreateDirectory(logsDir);
            if (logFile == null)
                logFile = Path.Combine(logsDir, $"fiberpod_{DateTime.Now:yyyyMMdd_HHmmss}.log");

            if (!TryParsePodmanLogLevel(logLevelRaw, out var logLevel))
            {
                Console.Error.WriteLine(
                    $"[Podish.Cli] invalid --log-level value: {logLevelRaw}. Use debug|info|warn|error|fatal|panic");
                context.ExitCode = 125;
                return;
            }

            SetupLogging(logLevel, logFile);
            try
            {
                var svc = new ImageArchiveService(Directory.GetCurrentDirectory());
                var loaded = svc.Load(input);
                foreach (var image in loaded)
                    Console.WriteLine(image);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Podish.Cli] load failed: {ex.Message}");
                context.ExitCode = 1;
            }
        });

        // --- Import Command ---
        var importCommand = new Command("import", "Import a rootfs tar as an image");
        var importSourceArgument = new Argument<string>("source", "Source rootfs tar file");
        var importReferenceArgument = new Argument<string?>("reference", () => "localhost/fiberpod-import:latest",
            "Target image reference");
        importCommand.AddArgument(importSourceArgument);
        importCommand.AddArgument(importReferenceArgument);
        importCommand.SetHandler((context) =>
        {
            var logLevelRaw = context.ParseResult.GetValueForOption(logLevelOption) ?? "warn";
            var logFile = context.ParseResult.GetValueForOption(logFileOption);
            var source = context.ParseResult.GetValueForArgument(importSourceArgument);
            var reference = context.ParseResult.GetValueForArgument(importReferenceArgument)
                            ?? "localhost/fiberpod-import:latest";

            var fiberpodDir = Path.Combine(Directory.GetCurrentDirectory(), ".fiberpod");
            var logsDir = Path.Combine(fiberpodDir, "logs");
            Directory.CreateDirectory(logsDir);
            if (logFile == null)
                logFile = Path.Combine(logsDir, $"fiberpod_{DateTime.Now:yyyyMMdd_HHmmss}.log");

            if (!TryParsePodmanLogLevel(logLevelRaw, out var logLevel))
            {
                Console.Error.WriteLine(
                    $"[Podish.Cli] invalid --log-level value: {logLevelRaw}. Use debug|info|warn|error|fatal|panic");
                context.ExitCode = 125;
                return;
            }

            SetupLogging(logLevel, logFile);
            try
            {
                var svc = new ImageArchiveService(Directory.GetCurrentDirectory());
                var image = svc.Import(source, reference);
                Console.WriteLine(image);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Podish.Cli] import failed: {ex.Message}");
                context.ExitCode = 1;
            }
        });

        // --- Export Command ---
        var exportCommand = new Command("export", "Export a container filesystem as tar");
        var exportOutputOption = new Option<string?>(
            new[] { "--output", "-o" },
            "Write to an archive file (default: stdout)");
        var exportContainerArgument = new Argument<string>("container", "Container ID");
        exportCommand.AddOption(exportOutputOption);
        exportCommand.AddArgument(exportContainerArgument);
        exportCommand.SetHandler((context) =>
        {
            var logLevelRaw = context.ParseResult.GetValueForOption(logLevelOption) ?? "warn";
            var logFile = context.ParseResult.GetValueForOption(logFileOption);
            var output = context.ParseResult.GetValueForOption(exportOutputOption);
            var container = context.ParseResult.GetValueForArgument(exportContainerArgument);

            var fiberpodDir = Path.Combine(Directory.GetCurrentDirectory(), ".fiberpod");
            var logsDir = Path.Combine(fiberpodDir, "logs");
            Directory.CreateDirectory(logsDir);
            if (logFile == null)
                logFile = Path.Combine(logsDir, $"fiberpod_{DateTime.Now:yyyyMMdd_HHmmss}.log");

            if (!TryParsePodmanLogLevel(logLevelRaw, out var logLevel))
            {
                Console.Error.WriteLine(
                    $"[Podish.Cli] invalid --log-level value: {logLevelRaw}. Use debug|info|warn|error|fatal|panic");
                context.ExitCode = 125;
                return;
            }

            SetupLogging(logLevel, logFile);
            try
            {
                var svc = new ImageArchiveService(Directory.GetCurrentDirectory());
                if (string.IsNullOrWhiteSpace(output))
                {
                    using var stdout = Console.OpenStandardOutput();
                    svc.ExportToStream(container, stdout);
                    stdout.Flush();
                }
                else
                {
                    svc.Export(container, output);
                    Console.WriteLine(output);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Podish.Cli] export failed: {ex.Message}");
                context.ExitCode = 1;
            }
        });

        // --- Logs Command ---
        var logsCommand = new Command("logs", "Fetch container logs");
        var logsContainerArgument = new Argument<string>("container", "Container ID");
        var logsTimestampsOption = new Option<bool>(new[] { "--timestamps" }, "Show timestamps");
        logsCommand.AddArgument(logsContainerArgument);
        logsCommand.AddOption(logsTimestampsOption);
        logsCommand.SetHandler((context) =>
        {
            var containerId = context.ParseResult.GetValueForArgument(logsContainerArgument);
            var showTimestamps = context.ParseResult.GetValueForOption(logsTimestampsOption);

            var fiberpodDir = Path.Combine(Directory.GetCurrentDirectory(), ".fiberpod");
            var logPath = Path.Combine(fiberpodDir, "containers", containerId, "ctr.log");
            if (!File.Exists(logPath))
            {
                Console.Error.WriteLine($"[Podish.Cli] log file not found for container {containerId}");
                context.ExitCode = 1;
                return;
            }

            foreach (var line in File.ReadLines(logPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                ContainerLogEntry? entry;
                try
                {
                    entry = JsonSerializer.Deserialize<ContainerLogEntry>(line);
                }
                catch
                {
                    continue;
                }

                if (entry == null) continue;

                if (showTimestamps)
                    Console.Out.Write($"{entry.Time:O} ");
                Console.Out.Write(entry.Log);
            }
        });

        // --- Events Command ---
        var eventsCommand = new Command("events", "Show container runtime events");
        var eventsFormatOption = new Option<string>(
            new[] { "--format" },
            () => "default",
            "Output format (default|json)");
        eventsCommand.AddOption(eventsFormatOption);
        eventsCommand.SetHandler((context) =>
        {
            var format = context.ParseResult.GetValueForOption(eventsFormatOption) ?? "default";

            var fiberpodDir = Path.Combine(Directory.GetCurrentDirectory(), ".fiberpod");
            var store = new ContainerEventStore(Path.Combine(fiberpodDir, "events.jsonl"));
            foreach (var evt in store.ReadAll())
            {
                if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine(JsonSerializer.Serialize(evt));
                    continue;
                }

                var exitPart = evt.ExitCode.HasValue ? $" exit={evt.ExitCode.Value}" : string.Empty;
                var imagePart = !string.IsNullOrEmpty(evt.Image) ? $" image={evt.Image}" : string.Empty;
                var msgPart = !string.IsNullOrEmpty(evt.Message) ? $" msg={evt.Message}" : string.Empty;
                Console.WriteLine($"{evt.Time:O} {evt.Type} {evt.ContainerId}{imagePart}{exitPart}{msgPart}");
            }
        });

        rootCommand.AddCommand(runCommand);
        rootCommand.AddCommand(pullCommand);
        rootCommand.AddCommand(saveCommand);
        rootCommand.AddCommand(loadCommand);
        rootCommand.AddCommand(importCommand);
        rootCommand.AddCommand(exportCommand);
        rootCommand.AddCommand(logsCommand);
        rootCommand.AddCommand(eventsCommand);

        var normalizedArgs = NormalizeRunArgsForPodman(args);
        return await rootCommand.InvokeAsync(normalizedArgs);
    }

    private static void SetupLogging(LogLevel logLevel, string? logFile)
    {
        Logging.LoggerFactory = LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            builder.SetMinimumLevel(logLevel);
            if (!string.IsNullOrEmpty(logFile))
            {
                // Write only to file; never emit engine logs to console.
                builder.AddProvider(new FileLoggerProvider(logFile));
            }
        });
        Logger = Logging.CreateLogger<Program>();
    }

    private static bool TryParsePodmanLogLevel(string raw, out LogLevel level)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "debug":
                level = LogLevel.Debug;
                return true;
            case "info":
                level = LogLevel.Information;
                return true;
            case "warn":
                level = LogLevel.Warning;
                return true;
            case "error":
                level = LogLevel.Error;
                return true;
            case "fatal":
            case "panic":
                level = LogLevel.Critical;
                return true;
            default:
                level = LogLevel.Warning;
                return false;
        }
    }

    private static string[] NormalizeRunArgsForPodman(string[] args)
    {
        if (args.Length == 0)
            return args;

        var runIdx = Array.FindIndex(args, a => string.Equals(a, "run", StringComparison.Ordinal));
        if (runIdx < 0 || runIdx == args.Length - 1)
            return args;

        // If user already provided `--`, keep raw tokens.
        for (var i = runIdx + 1; i < args.Length; i++)
        {
            if (args[i] == "--")
                return args;
        }

        var optionsWithValue = new HashSet<string>(StringComparer.Ordinal)
        {
            "-l",
            "--log-level",
            "--log-file",
            "-v",
            "--volume",
            "-e",
            "--env",
            "--dns",
            "--log-driver",
            "--rootfs"
        };
        var optionsNoValue = new HashSet<string>(StringComparer.Ordinal)
        {
            "-i",
            "--interactive",
            "-t",
            "--tty",
            "-s",
            "--strace"
        };

        bool hasRootfs = false;
        var firstPositional = -1;
        for (var i = runIdx + 1; i < args.Length; i++)
        {
            var token = args[i];
            if (token.StartsWith("--rootfs=", StringComparison.Ordinal))
            {
                hasRootfs = true;
                continue;
            }

            if (token.StartsWith("-", StringComparison.Ordinal))
            {
                if (optionsNoValue.Contains(token))
                    continue;

                if (optionsWithValue.Contains(token))
                {
                    if (token == "--rootfs")
                        hasRootfs = true;
                    i++;
                    continue;
                }

                // Unknown option before passthrough boundary: keep original to let parser report it.
                return args;
            }

            firstPositional = i;
            break;
        }

        if (firstPositional < 0)
            return args;

        var passThroughStart = hasRootfs ? firstPositional : firstPositional + 1;
        if (passThroughStart >= args.Length)
            return args;

        var rewritten = new List<string>(args.Length + 1);
        rewritten.AddRange(args.Take(passThroughStart));
        rewritten.Add("--");
        rewritten.AddRange(args.Skip(passThroughStart));
        return rewritten.ToArray();
    }

    private static async Task<int> RunContainer(string rootfsPath, string exe, string[] exeArgs, string[] volumes,
        string[] guestEnvs, string[] dnsServers, bool useTty, bool strace, bool useOverlay, string containersDir,
        string containerId, string image, string containerDir, ContainerLogDriver logDriver,
        ContainerEventStore eventStore)
    {
        var service = new ContainerRuntimeService(Logger);
        return await service.RunAsync(new ContainerRunRequest
        {
            RootfsPath = rootfsPath,
            Exe = exe,
            ExeArgs = exeArgs,
            Volumes = volumes,
            GuestEnvs = guestEnvs,
            DnsServers = dnsServers,
            UseTty = useTty,
            Strace = strace,
            UseOverlay = useOverlay,
            ContainerId = containerId,
            Image = image,
            ContainerDir = containerDir,
            LogDriver = logDriver,
            EventStore = eventStore
        });
    }
}

/// <summary>
/// Simple file logger provider that writes logs to a file.
/// </summary>
file class FileLoggerProvider : ILoggerProvider
{
    private readonly string _filePath;
    private readonly object _lock = new();
    private StreamWriter? _writer;

    public FileLoggerProvider(string filePath)
    {
        _filePath = filePath;
        if (File.Exists(filePath)) File.Delete(filePath);
        _writer = new StreamWriter(File.Open(filePath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true
        };
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

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

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

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