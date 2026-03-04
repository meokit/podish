using Fiberish.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Podish.Core;

public sealed class PodishContextOptions
{
    public string WorkDir { get; init; } = Directory.GetCurrentDirectory();
    public string LogLevel { get; init; } = "warn";
    public string? LogFile { get; init; }
}

public sealed class PodishRunSpec
{
    public string? Image { get; init; }
    public string? Rootfs { get; init; }
    public string? Exe { get; init; }
    public string[] ExeArgs { get; init; } = Array.Empty<string>();
    public string[] Volumes { get; init; } = Array.Empty<string>();
    public string[] Env { get; init; } = Array.Empty<string>();
    public string[] Dns { get; init; } = Array.Empty<string>();
    public bool Interactive { get; init; }
    public bool Tty { get; init; }
    public bool Strace { get; init; }
    public string LogDriver { get; init; } = "json-file";
}

public sealed class PodishRunResult
{
    public required string ContainerId { get; init; }
    public required string ImageRef { get; init; }
    public required int ExitCode { get; init; }
}

public sealed class PodishContext
{
    private readonly ILogger _logger;
    private readonly ILoggerFactory _loggerFactory;

    public PodishContext(PodishContextOptions options)
    {
        WorkDir = options.WorkDir;
        FiberpodDir = Path.Combine(WorkDir, ".fiberpod");
        ImagesDir = Path.Combine(FiberpodDir, "images");
        OciStoreImagesDir = Path.Combine(FiberpodDir, "oci", "images");
        LogsDir = Path.Combine(FiberpodDir, "logs");
        ContainersDir = Path.Combine(FiberpodDir, "containers");

        Directory.CreateDirectory(ImagesDir);
        Directory.CreateDirectory(OciStoreImagesDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(ContainersDir);

        var logFile = options.LogFile;
        if (string.IsNullOrWhiteSpace(logFile))
            logFile = Path.Combine(LogsDir, $"podish_{DateTime.Now:yyyyMMdd_HHmmss}.log");

        if (!TryParsePodmanLogLevel(options.LogLevel, out var level))
            throw new ArgumentException($"invalid log level: {options.LogLevel}", nameof(options));

        _loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            builder.SetMinimumLevel(level);
            if (!string.IsNullOrEmpty(logFile))
                builder.AddProvider(new PodishFileLoggerProvider(logFile));
        });
        _logger = _loggerFactory.CreateLogger<PodishContext>();
    }

    public string WorkDir { get; }
    public string FiberpodDir { get; }
    public string ImagesDir { get; }
    public string OciStoreImagesDir { get; }
    public string LogsDir { get; }
    public string ContainersDir { get; }
    public ILoggerFactory LoggerFactory => _loggerFactory;

    public async Task<OciStoredImage> PullImageAsync(string image)
    {
        using var _ = Logging.BeginScope(_loggerFactory);
        var pullService = new OciPullService(_logger);
        var safeImageName = image.Replace("/", "_").Replace(":", "_");
        var storeDir = Path.Combine(OciStoreImagesDir, safeImageName);
        return await pullService.PullAndStoreImageAsync(image, storeDir);
    }

    public async Task<PodishRunResult> RunAsync(PodishRunSpec spec)
    {
        using var _ = Logging.BeginScope(_loggerFactory);
        var useRootfs = !string.IsNullOrWhiteSpace(spec.Rootfs);
        if (!ContainerLogDriverParser.TryParse(spec.LogDriver, out var containerLogDriver))
            throw new InvalidOperationException($"invalid log driver: {spec.LogDriver}");

        string? image = null;
        var rootfsPath = spec.Rootfs ?? string.Empty;
        if (!useRootfs)
        {
            if (string.IsNullOrWhiteSpace(spec.Image))
                throw new InvalidOperationException("image is required unless rootfs is set");

            image = spec.Image;
            rootfsPath = image;
            var safeImageName = image.Replace("/", "_").Replace(":", "_");
            var ociStoreDir = Path.Combine(OciStoreImagesDir, safeImageName);
            if (!Directory.Exists(rootfsPath) && Directory.Exists(ociStoreDir))
            {
                rootfsPath = ociStoreDir;
            }
            else if (!Directory.Exists(rootfsPath))
            {
                await PullImageAsync(image);
                rootfsPath = ociStoreDir;
            }
        }
        else
        {
            if (!Directory.Exists(rootfsPath))
                throw new DirectoryNotFoundException($"rootfs path not found: {rootfsPath}");
            image = rootfsPath;
        }

        var containerId = Guid.NewGuid().ToString("N")[..12];
        var containerDir = Path.Combine(ContainersDir, containerId);
        Directory.CreateDirectory(containerDir);

        var imageRef = image ?? rootfsPath;
        var eventStore = new ContainerEventStore(Path.Combine(FiberpodDir, "events.jsonl"));
        eventStore.Append(new ContainerEvent(DateTimeOffset.UtcNow, "container-create", containerId, imageRef));

        var service = new ContainerRuntimeService(_logger, _loggerFactory);
        var exitCode = await service.RunAsync(new ContainerRunRequest
        {
            RootfsPath = rootfsPath,
            Exe = spec.Exe ?? string.Empty,
            ExeArgs = spec.ExeArgs,
            Volumes = spec.Volumes,
            GuestEnvs = spec.Env,
            DnsServers = spec.Dns,
            UseTty = spec.Interactive && spec.Tty,
            Strace = spec.Strace,
            UseOverlay = !useRootfs,
            ContainerId = containerId,
            Image = imageRef,
            ContainerDir = containerDir,
            LogDriver = containerLogDriver,
            EventStore = eventStore
        });

        return new PodishRunResult
        {
            ContainerId = containerId,
            ImageRef = imageRef,
            ExitCode = exitCode
        };
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
}

file sealed class PodishFileLoggerProvider : ILoggerProvider
{
    private readonly object _lock = new();
    private readonly StreamWriter _writer;

    public PodishFileLoggerProvider(string filePath)
    {
        if (File.Exists(filePath))
            File.Delete(filePath);
        _writer = new StreamWriter(File.Open(filePath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true
        };
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new PodishFileLogger(categoryName, this);
    }

    public void Dispose()
    {
        _writer.Dispose();
    }

    public void WriteLog(string message)
    {
        lock (_lock)
        {
            _writer.WriteLine(message);
        }
    }
}

file sealed class PodishFileLogger : ILogger
{
    private readonly string _categoryName;
    private readonly PodishFileLoggerProvider _provider;

    public PodishFileLogger(string categoryName, PodishFileLoggerProvider provider)
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
            _ => logLevel.ToString().ToUpperInvariant()[..4]
        };
        var message = $"[{timestamp}] [{level}] {_categoryName}: {formatter(state, exception)}";
        if (exception != null)
            message += $"\n{exception}";
        _provider.WriteLog(message);
    }
}
