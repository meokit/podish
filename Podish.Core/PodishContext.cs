using Fiberish.Core.Net;
using Fiberish.Core.VFS.TTY;
using Fiberish.Diagnostics;
using Fiberish.VFS;
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
    public string? Name { get; init; }
    public string? Hostname { get; init; }
    public bool AutoRemove { get; init; }
    public NetworkMode NetworkMode { get; init; } = NetworkMode.Host;
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
    public bool Init { get; init; }
    public bool PulseServer { get; init; }
    public bool WaylandServer { get; init; }
    public int WaylandDesktopWidth { get; init; } = 1024;
    public int WaylandDesktopHeight { get; init; } = 768;
    public long? MemoryQuotaBytes { get; init; }
    public string LogDriver { get; init; } = "json-file";
    public ushort? TerminalRows { get; init; }
    public ushort? TerminalCols { get; init; }
    public IReadOnlyList<PublishedPortSpec> PublishedPorts { get; init; } = Array.Empty<PublishedPortSpec>();
}

public sealed class PodishRunResult
{
    public required string ContainerId { get; init; }
    public required string ImageRef { get; init; }
    public required int ExitCode { get; init; }
}

public sealed class PodishContainerSession
{
    private readonly Lock _lock = new();
    private readonly ContainerProcessController _processController;
    private Task<int>? _runTask;
    private readonly Func<Task<int>>? _startRun;
    private readonly PodishTerminalBridge? _terminalBridge;

    internal PodishContainerSession(string containerId, string imageRef, Task<int> runTask,
        PodishTerminalBridge? terminalBridge, ContainerProcessController processController)
        : this(containerId, imageRef, terminalBridge, processController)
    {
        _runTask = runTask;
    }

    internal PodishContainerSession(string containerId, string imageRef, Func<Task<int>> startRun,
        PodishTerminalBridge? terminalBridge, ContainerProcessController processController)
        : this(containerId, imageRef, terminalBridge, processController)
    {
        _startRun = startRun;
    }

    private PodishContainerSession(string containerId, string imageRef, PodishTerminalBridge? terminalBridge,
        ContainerProcessController processController)
    {
        ContainerId = containerId;
        ImageRef = imageRef;
        _terminalBridge = terminalBridge;
        _processController = processController;
    }

    public string ContainerId { get; }
    public string ImageRef { get; }
    public bool HasTerminal => _terminalBridge != null;
    public bool IsCompleted => _runTask?.IsCompleted ?? false;
    public int? InitPid => _processController.InitPid;
    public bool HasStarted => _processController.HasStarted;

    public void SetOutputHandler(Action<TtyEndpointKind, ReadOnlySpan<byte>>? handler)
    {
        _terminalBridge?.SetOutputHandler(handler);
    }

    public int WriteInput(ReadOnlySpan<byte> data)
    {
        if (_terminalBridge == null)
            return 0;
        return _terminalBridge.WriteInput(data);
    }

    public bool Resize(ushort rows, ushort cols)
    {
        if (_terminalBridge == null)
            return false;
        _terminalBridge.Resize(rows, cols);
        return true;
    }

    public int ReadOutput(Span<byte> buffer, int timeoutMs)
    {
        if (_terminalBridge == null)
            return 0;
        return _terminalBridge.ReadOutput(buffer, timeoutMs);
    }

    public Task<int> WaitAsync()
    {
        lock (_lock)
        {
            if (_runTask != null)
                return _runTask;

            if (_startRun == null)
                throw new InvalidOperationException("Session run task is not available.");

            _runTask = _startRun();
            return _runTask;
        }
    }

    public bool SignalInitProcess(int signal)
    {
        return _processController.TrySignalInitProcess(signal);
    }

    public bool ForceStop()
    {
        return _processController.TryForceStop();
    }
}

public sealed class ContainerProcessController
{
    private readonly Lock _lock = new();
    private readonly Queue<int> _pendingSignals = [];
    private Action? _forceStop;
    private Action<int>? _signalInit;

    public int? InitPid { get; private set; }
    public bool HasStarted { get; private set; }

    public void BindInitProcess(int pid, Action<int> signalInit)
    {
        Queue<int>? pending = null;
        lock (_lock)
        {
            InitPid = pid;
            HasStarted = true;
            _signalInit = signalInit;
            if (_pendingSignals.Count > 0)
            {
                pending = new Queue<int>(_pendingSignals);
                _pendingSignals.Clear();
            }
        }

        if (pending == null) return;
        while (pending.TryDequeue(out var sig))
            signalInit(sig);
    }

    public void Unbind()
    {
        lock (_lock)
        {
            _signalInit = null;
            _forceStop = null;
            InitPid = null;
            _pendingSignals.Clear();
        }
    }

    public void BindRuntimeControl(Action forceStop)
    {
        lock (_lock)
        {
            _forceStop = forceStop;
        }
    }

    public bool TrySignalInitProcess(int signal)
    {
        Action<int>? target;
        lock (_lock)
        {
            target = _signalInit;
            if (target == null)
            {
                _pendingSignals.Enqueue(signal);
                return false;
            }
        }

        target(signal);
        return true;
    }

    public bool TryForceStop()
    {
        Action? stop;
        lock (_lock)
        {
            stop = _forceStop;
        }

        if (stop == null)
            return false;

        stop();
        return true;
    }
}

public sealed class PodishTerminalBridge
{
    private const int MaxBufferedBytes = 64 * 1024;
    private readonly Lock _lock = new();
    private readonly Queue<byte> _outputBuffer = [];
    private readonly AutoResetEvent _outputEvent = new(false);
    private Action<TtyEndpointKind, ReadOnlySpan<byte>>? _outputHandler;
    private ushort? _pendingCols;
    private ushort? _pendingRows;
    private TtyDiscipline? _tty;

    public void BindTty(TtyDiscipline tty)
    {
        ushort? r, c;
        lock (_lock)
        {
            _tty = tty;
            r = _pendingRows;
            c = _pendingCols;
        }

        if (r.HasValue && c.HasValue) tty.InitializeWindowSize(r.Value, c.Value);

    }

    public void SetOutputHandler(Action<TtyEndpointKind, ReadOnlySpan<byte>>? handler)
    {
        byte[]? bufferedOutput = null;
        lock (_lock)
        {
            _outputHandler = handler;
            if (handler != null && _outputBuffer.Count > 0)
            {
                bufferedOutput = _outputBuffer.ToArray();
                _outputBuffer.Clear();
            }
        }

        if (bufferedOutput != null)
            handler!(TtyEndpointKind.Stdout, bufferedOutput);
    }

    public int WriteInput(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
            return 0;

        TtyDiscipline? tty;
        lock (_lock)
        {
            tty = _tty;
        }

        if (tty == null)
            return 0;

        tty.Input(data.ToArray());
        return data.Length;
    }

    public void Resize(ushort rows, ushort cols)
    {
        TtyDiscipline? tty;
        lock (_lock)
        {
            _pendingRows = rows;
            _pendingCols = cols;
            tty = _tty;
        }

        tty?.Device.EnqueueResize(rows, cols);
    }

    public void EmitOutput(TtyEndpointKind kind, ReadOnlySpan<byte> data)
    {
        Action<TtyEndpointKind, ReadOnlySpan<byte>>? handler;
        lock (_lock)
        {
            EnqueueOutputLocked(data);
            handler = _outputHandler;
        }

        handler?.Invoke(kind, data);
    }

    public int ReadOutput(Span<byte> buffer, int timeoutMs)
    {
        if (buffer.Length == 0)
            return 0;

        var copied = TryDrain(buffer);
        if (copied > 0 || timeoutMs == 0)
            return copied;

        _outputEvent.WaitOne(timeoutMs < 0 ? Timeout.Infinite : timeoutMs);
        return TryDrain(buffer);
    }

    private int TryDrain(Span<byte> buffer)
    {
        lock (_lock)
        {
            if (_outputBuffer.Count == 0)
                return 0;
            var n = Math.Min(buffer.Length, _outputBuffer.Count);
            for (var i = 0; i < n; i++)
                buffer[i] = _outputBuffer.Dequeue();
            return n;
        }
    }

    private void EnqueueOutputLocked(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
            return;

        var needed = data.Length;
        while (_outputBuffer.Count + needed > MaxBufferedBytes && _outputBuffer.Count > 0)
            _outputBuffer.Dequeue();

        for (var i = 0; i < data.Length; i++)
            _outputBuffer.Enqueue(data[i]);

        _outputEvent.Set();
    }
}

public sealed class PodishContext : IDisposable
{
    private readonly PodishFileLoggerProvider _fileLoggerProvider;
    private readonly ILogger _logger;

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

        _fileLoggerProvider = new PodishFileLoggerProvider(logFile);
        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            builder.SetMinimumLevel(level);
            builder.AddProvider(_fileLoggerProvider);
        });
        _logger = LoggerFactory.CreateLogger<PodishContext>();
    }

    public string WorkDir { get; }
    public string FiberpodDir { get; }
    public string ImagesDir { get; }
    public string OciStoreImagesDir { get; }
    public string LogsDir { get; }
    public string ContainersDir { get; }
    public ILoggerFactory LoggerFactory { get; }

    public void Dispose()
    {
        LoggerFactory.Dispose();
    }

    public void SetLogObserver(Action<LogLevel, string>? observer)
    {
        _fileLoggerProvider.SetObserver(observer);
    }

    public async Task<OciStoredImage> PullImageAsync(string image)
    {
        using var _ = Logging.BeginScope(LoggerFactory);
        var pullService = new OciPullService(_logger);
        var safeImageName = image.Replace("/", "_").Replace(":", "_");
        var storeDir = Path.Combine(OciStoreImagesDir, safeImageName);
        return await pullService.PullAndStoreImageAsync(image, storeDir);
    }

    public async Task<PodishRunResult> RunAsync(PodishRunSpec spec)
    {
        using var _ = Logging.BeginScope(LoggerFactory);
        var session = await StartInternalAsync(spec, false);
        var exitCode = await session.WaitAsync();

        return new PodishRunResult
        {
            ContainerId = session.ContainerId,
            ImageRef = session.ImageRef,
            ExitCode = exitCode
        };
    }

    public async Task<PodishContainerSession> StartAsync(PodishRunSpec spec, string? containerIdOverride = null)
    {
        using var _ = Logging.BeginScope(LoggerFactory);
        return await StartInternalAsync(spec, true, containerIdOverride);
    }

    internal async Task<PodishContainerSession> StartWithRootFileSystemAsync(PodishRunSpec spec,
        Func<DeviceNumberManager, SuperBlock> rootFileSystemFactory,
        string? containerIdOverride = null,
        string? rootFileSystemSource = null)
    {
        using var _ = Logging.BeginScope(LoggerFactory);
        ArgumentNullException.ThrowIfNull(rootFileSystemFactory);
        return await StartInternalAsync(spec, true, containerIdOverride, rootFileSystemFactory, rootFileSystemSource);
    }

    public async Task<PodishRunResult> RunRootfsTarAsync(Stream rootfsTarStream, PodishRunSpec spec,
        string rootfsName = "in-memory-rootfs", string? containerIdOverride = null)
    {
        using var _ = Logging.BeginScope(LoggerFactory);
        var session = await StartRootfsTarAsync(rootfsTarStream, spec, rootfsName, containerIdOverride);
        var exitCode = await session.WaitAsync();

        return new PodishRunResult
        {
            ContainerId = session.ContainerId,
            ImageRef = session.ImageRef,
            ExitCode = exitCode
        };
    }

    public async Task<PodishContainerSession> StartRootfsTarAsync(Stream rootfsTarStream, PodishRunSpec spec,
        string rootfsName = "in-memory-rootfs", string? containerIdOverride = null)
    {
        using var _ = Logging.BeginScope(LoggerFactory);
        ArgumentNullException.ThrowIfNull(rootfsTarStream);

        byte[] tarBytes;
        if (rootfsTarStream is MemoryStream ms && ms.TryGetBuffer(out var segment))
        {
            tarBytes = segment.Array!;
            if (segment.Offset != 0 || segment.Count != tarBytes.Length)
            {
                tarBytes = new byte[segment.Count];
                Buffer.BlockCopy(segment.Array!, segment.Offset, tarBytes, 0, segment.Count);
            }
        }
        else
        {
            await using var copy = new MemoryStream();
            await rootfsTarStream.CopyToAsync(copy);
            tarBytes = copy.ToArray();
        }

        var rootfsSpec = new PodishRunSpec
        {
            Name = spec.Name,
            Hostname = spec.Hostname,
            AutoRemove = spec.AutoRemove,
            NetworkMode = spec.NetworkMode,
            Image = spec.Image,
            Rootfs = rootfsName,
            Exe = spec.Exe,
            ExeArgs = spec.ExeArgs,
            Volumes = spec.Volumes,
            Env = spec.Env,
            Dns = spec.Dns,
            Interactive = spec.Interactive,
            Tty = spec.Tty,
            Strace = spec.Strace,
            Init = spec.Init,
            PulseServer = spec.PulseServer,
            WaylandServer = spec.WaylandServer,
            WaylandDesktopWidth = spec.WaylandDesktopWidth,
            WaylandDesktopHeight = spec.WaylandDesktopHeight,
            MemoryQuotaBytes = spec.MemoryQuotaBytes,
            LogDriver = spec.LogDriver,
            PublishedPorts = spec.PublishedPorts
        };

        return await StartInternalAsync(
            rootfsSpec,
            true,
            containerIdOverride,
            devNumbers => RootfsArchiveLoader.LoadTmpfsFromTar(new MemoryStream(tarBytes, false), devNumbers,
                rootfsName),
            rootfsName);
    }


    private async Task<PodishContainerSession> StartInternalAsync(PodishRunSpec spec, bool attachTerminalBridge,
        string? containerIdOverride = null,
        Func<DeviceNumberManager, SuperBlock>? rootFileSystemFactory = null,
        string? rootFileSystemSource = null)
    {
        var useRootfs = !string.IsNullOrWhiteSpace(spec.Rootfs) || rootFileSystemFactory != null;
        if (!ContainerLogDriverParser.TryParse(spec.LogDriver, out var containerLogDriver))
            throw new InvalidOperationException($"invalid log driver: {spec.LogDriver}");

        string? image = null;
        var rootfsPath = spec.Rootfs ?? string.Empty;
        if (rootFileSystemFactory != null)
        {
            rootfsPath = rootFileSystemSource ?? spec.Rootfs ?? "in-memory-rootfs";
            image = rootfsPath;
        }
        else if (!useRootfs)
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

        var containerId = string.IsNullOrWhiteSpace(containerIdOverride)
            ? Guid.NewGuid().ToString("N")[..12]
            : containerIdOverride!;
        var containerDir = Path.Combine(ContainersDir, containerId);
        Directory.CreateDirectory(containerDir);

        var imageRef = image ?? rootfsPath;
        var eventStore = new ContainerEventStore(Path.Combine(FiberpodDir, "events.jsonl"));
        eventStore.Append(new ContainerEvent(DateTimeOffset.UtcNow, "container-create", containerId, imageRef));

        var bridge = attachTerminalBridge && spec.Interactive && spec.Tty ? new PodishTerminalBridge() : null;
        if (bridge != null && spec.TerminalRows.HasValue && spec.TerminalCols.HasValue)
            bridge.Resize(spec.TerminalRows.Value, spec.TerminalCols.Value);
        var processController = new ContainerProcessController();
        var request = new ContainerRunRequest
        {
            RootfsPath = rootfsPath,
            RootFileSystemFactory = rootFileSystemFactory,
            Exe = spec.Exe ?? string.Empty,
            ExeArgs = spec.ExeArgs,
            Volumes = spec.Volumes,
            GuestEnvs = spec.Env,
            DnsServers = spec.Dns,
            UseTty = spec.Interactive && spec.Tty,
            Strace = spec.Strace,
            UseOverlay = !useRootfs && rootFileSystemFactory == null,
            NetworkMode = spec.NetworkMode,
            Hostname = spec.Hostname ?? spec.Name ?? containerId,
            ContainerName = spec.Name,
            ContainerId = containerId,
            Image = imageRef,
            ContainerDir = containerDir,
            LogDriver = containerLogDriver,
            EventStore = eventStore,
            TerminalBridge = bridge,
            ProcessController = processController,
            EnableHostConsoleInput = !attachTerminalBridge,
            PublishedPorts = spec.PublishedPorts,
            UseEngineInit = spec.Init,
            MemoryQuotaBytes = spec.MemoryQuotaBytes,
            EnablePulseServer = spec.PulseServer,
            EnableWaylandServer = spec.WaylandServer,
            WaylandDesktopWidth = spec.WaylandDesktopWidth,
            WaylandDesktopHeight = spec.WaylandDesktopHeight,
            TerminalRows = spec.TerminalRows,
            TerminalCols = spec.TerminalCols
        };
        if (OperatingSystem.IsBrowser())
            return new PodishContainerSession(containerId, imageRef, () => RunInCurrentThreadAsync(request), bridge,
                processController);

        var runTask = RunInDedicatedRuntimeThreadAsync(request);
        return new PodishContainerSession(containerId, imageRef, runTask, bridge, processController);
    }

    private Task<int> RunInCurrentThreadAsync(ContainerRunRequest request)
    {
        var service = new ContainerRuntimeService(_logger, LoggerFactory);
        return service.RunAsync(request);
    }

    private Task<int> RunInDedicatedRuntimeThreadAsync(ContainerRunRequest request)
    {
        var runtimeThread = new ContainerRuntimeThread(_logger, LoggerFactory);
        return RunInDedicatedRuntimeThreadAsync(runtimeThread, request);
    }

    private static async Task<int> RunInDedicatedRuntimeThreadAsync(ContainerRuntimeThread runtimeThread,
        ContainerRunRequest request)
    {
        try
        {
            return await runtimeThread.Start(request).ConfigureAwait(false);
        }
        finally
        {
            runtimeThread.Dispose();
        }
    }

    public static bool TryParsePodmanLogLevel(string raw, out LogLevel level)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "trace":
                level = LogLevel.Trace;
                return true;
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

internal sealed class PodishFileLoggerProvider : ILoggerProvider
{
    private readonly Lock _lock = new();
    private readonly StreamWriter _writer;
    private Action<LogLevel, string>? _observer;

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

    public void SetObserver(Action<LogLevel, string>? observer)
    {
        lock (_lock)
        {
            _observer = observer;
        }
    }

    public void WriteLog(LogLevel level, string message)
    {
        Action<LogLevel, string>? observer;
        lock (_lock)
        {
            _writer.WriteLine(message);
            observer = _observer;
        }

        observer?.Invoke(level, message);
    }
}

internal sealed class PodishFileLogger : ILogger
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
        _provider.WriteLog(logLevel, message);
    }
}
