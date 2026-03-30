using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Fiberish.Core;
using Fiberish.Core.Net;
using Fiberish.Core.VFS.TTY;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;
using Podish.Core.Networking;

namespace Podish.Core;

public sealed class ContainerRunRequest
{
    public const string PulseServerSocketPath = "/run/pulse/native";
    public const string PulseServerEnvVar = "PULSE_SERVER";
    public const string PulseRuntimePathEnvVar = "PULSE_RUNTIME_PATH";
    public const string WaylandDisplaySocketPath = "/run/wayland-0";
    public const string WaylandDisplayEnvVar = "WAYLAND_DISPLAY";
    public const string XdgRuntimeDirEnvVar = "XDG_RUNTIME_DIR";

    public required string RootfsPath { get; init; }
    public Func<DeviceNumberManager, SuperBlock>? RootFileSystemFactory { get; init; }
    public string Hostname { get; init; } = string.Empty;
    public string? ContainerName { get; init; }
    public NetworkMode NetworkMode { get; init; } = NetworkMode.Host;
    public string Exe { get; init; } = string.Empty;
    public string[] ExeArgs { get; init; } = Array.Empty<string>();
    public string[] Volumes { get; init; } = Array.Empty<string>();
    public string[] GuestEnvs { get; init; } = Array.Empty<string>();
    public string[] DnsServers { get; init; } = Array.Empty<string>();
    public bool UseTty { get; init; }
    public bool Strace { get; init; }
    public bool UseOverlay { get; init; }
    public required string ContainerId { get; init; }
    public required string Image { get; init; }
    public required string ContainerDir { get; init; }
    public ContainerLogDriver LogDriver { get; init; } = ContainerLogDriver.JsonFile;
    public required ContainerEventStore EventStore { get; init; }
    public PodishTerminalBridge? TerminalBridge { get; init; }
    public ContainerProcessController? ProcessController { get; init; }
    public bool EnableHostConsoleInput { get; init; } = true;
    public IReadOnlyList<PublishedPortSpec> PublishedPorts { get; init; } = Array.Empty<PublishedPortSpec>();
    public bool UseEngineInit { get; init; }
    public long? MemoryQuotaBytes { get; init; }
    public string? GuestStatsExportDir { get; init; }
    public bool EnablePulseServer { get; init; }
    public bool EnableWaylandServer { get; init; }
    public int WaylandDesktopWidth { get; init; } = 1024;
    public int WaylandDesktopHeight { get; init; } = 768;
    public Action<KernelRuntime, KernelScheduler, UTSNamespace?, int>? ConfigureVirtualDaemons { get; init; }
}

public sealed class ContainerRuntimeService
{
    private readonly ILogger _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Func<IPortForwardManager> _portForwardManagerFactory;
    private IPortForwardManager? _portForwardManager;

    public ContainerRuntimeService(ILogger logger, ILoggerFactory loggerFactory)
        : this(logger, loggerFactory, () => new PortForwardManager(loggerFactory))
    {
    }

    internal ContainerRuntimeService(ILogger logger, ILoggerFactory loggerFactory,
        Func<IPortForwardManager> portForwardManagerFactory)
    {
        _logger = logger ?? NullLogger.Instance;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _portForwardManagerFactory = portForwardManagerFactory
                                     ?? throw new ArgumentNullException(nameof(portForwardManagerFactory));
    }

    public async Task<int> RunAsync(ContainerRunRequest request)
    {
        await Task.CompletedTask;
        using var _externalPageScope = ExternalPageManager.BeginIsolatedScope();
        using var _globalPageCacheScope = GlobalAddressSpaceCacheManager.BeginIsolatedScope();

        var scheduler = new KernelScheduler();
        scheduler.LoggerFactory = _loggerFactory;

        TtyDiscipline? ttyDiag = null;
        KernelRuntime? runtime = null;
        FileStream? stdinStream = null;
        CancellationTokenSource? inputCts = null;
        Task? inputTask = null;
        PosixSignalRegistration? sigwinch = null;
        ITtyDriver? driver = null;
        EventHandler? processExitHandler = null;
        ConsoleCancelEventHandler? cancelKeyPressHandler = null;
        var rawModeEnabled = false;
        var isInteractive = request.UseTty && request.EnableHostConsoleInput && !Console.IsInputRedirected;

        using var logSink = CreateContainerLogSink(request.LogDriver, request.ContainerDir, _loggerFactory);
        var publishedPorts = request.PublishedPorts ?? Array.Empty<PublishedPortSpec>();

        // Always create a driver + discipline so all I/O routes through ITtyDriver.
        // In TTY mode this provides line discipline, echo, signals, etc.
        // In non-TTY mode it acts as a passthrough to the driver's Write().
        driver = request.TerminalBridge != null
            ? new BridgeTtyDriver(request.TerminalBridge, logSink, scheduler)
            : new ConsoleTtyDriver(logSink);
        var broadcaster = new SchedulerSignalBroadcaster(scheduler);
        ttyDiag = new TtyDiscipline(driver, broadcaster, _loggerFactory.CreateLogger<TtyDiscipline>(), scheduler);
        if (driver is ConsoleTtyDriver consoleDriver)
            consoleDriver.BindTty(ttyDiag);
        if (request.TerminalBridge != null)
            request.TerminalBridge.BindTty(ttyDiag);

        if (isInteractive)
        {
            var tty = ttyDiag!;
            stdinStream = new FileStream(new SafeFileHandle(0, false), FileAccess.Read);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) || RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var res = HostTermios.EnableRawMode(0);
                if (res != 0) Console.Error.WriteLine($"Warning: Failed to enable raw mode: {res}");
                rawModeEnabled = res == 0;

                void RawModeCleanup()
                {
                    if (!rawModeEnabled)
                        return;

                    try
                    {
                        HostTermios.DisableRawMode(0);
                    }
                    catch
                    {
                    }
                }

                // Keep a last-resort restore path for abrupt process termination.
                processExitHandler = (_, _) => RawModeCleanup();
                cancelKeyPressHandler = (_, _) =>
                {
                    RawModeCleanup();
                    // Don't cancel — let the default SIGINT handling terminate the process.
                };
                AppDomain.CurrentDomain.ProcessExit += processExitHandler;
                Console.CancelKeyPress += cancelKeyPressHandler;
            }

            inputCts = new CancellationTokenSource();
            inputTask = Task.Run(() => InputLoop(tty, stdinStream, inputCts.Token));

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                try
                {
                    if (!Console.IsOutputRedirected)
                        tty.Device.EnqueueResize(Console.WindowHeight, Console.WindowWidth);

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
                        }
                    });
                }
                catch
                {
                }
        }

        INetworkBackend? networkBackend = null;
        ContainerNetworkContext? networkContext = null;
        var effectiveNetworkMode = OperatingSystem.IsBrowser() && request.NetworkMode == NetworkMode.Private
            ? NetworkMode.Host
            : request.NetworkMode;
        var actualExe = string.IsNullOrEmpty(request.Exe) ? "/bin/sh" : request.Exe;
        var initProcessStarted = false;
        var startupPhase = "bootstrap";
        try
        {
            if (request.RootFileSystemFactory == null && !Directory.Exists(request.RootfsPath))
            {
                Console.Error.WriteLine($"[Podish Error] RootFS path not found: {request.RootfsPath}");
                request.EventStore.Append(new ContainerEvent(DateTimeOffset.UtcNow, "container-exit",
                    request.ContainerId,
                    request.Image, 1,
                    "rootfs not found"));
                return 1;
            }

            if (request.MemoryQuotaBytes is { } quotaBytes &&
                quotaBytes < ContainerMemoryLimits.MinimumMemoryQuotaBytes)
            {
                var message =
                    $"memory quota must be at least {ContainerMemoryLimits.MinimumMemoryQuotaBytes / (1024 * 1024)}M";
                Console.Error.WriteLine($"[Podish Error] {message}");
                request.EventStore.Append(new ContainerEvent(DateTimeOffset.UtcNow, "container-exit",
                    request.ContainerId,
                    request.Image, 1,
                    message));
                return 1;
            }

            if (request.MemoryQuotaBytes.HasValue)
                ExternalPageManager.MemoryQuotaBytes = request.MemoryQuotaBytes.Value;

            runtime = KernelRuntime.BootstrapBare(request.Strace, ttyDiag);

            if (effectiveNetworkMode == NetworkMode.Private)
                networkBackend = new PrivateNetworkBackend(new DummySwitch());
            else
                networkBackend = new HostNetworkBackend();

            if (effectiveNetworkMode == NetworkMode.Private)
            {
                networkContext = networkBackend.CreateContainerNetwork(new ContainerNetworkSpec
                    { ContainerId = request.ContainerId });
                if (publishedPorts.Count > 0)
                {
                    _portForwardManager ??= _portForwardManagerFactory();
                    _portForwardManager.Start(networkContext, publishedPorts);
                }

                runtime.Syscalls.SetPrivateNetNamespace(networkContext.SharedNamespace);
            }

            runtime.Syscalls.NetworkMode = effectiveNetworkMode;
            if (request.UseOverlay)
            {
                if (!TryCreateLayerLower(runtime.DeviceNumbers, request.RootfsPath, out var layerLowerSb,
                        out var layerProvider,
                        out var layerError))
                {
                    Console.Error.WriteLine($"[Podish Error] {layerError}");
                    request.EventStore.Append(new ContainerEvent(DateTimeOffset.UtcNow, "container-exit",
                        request.ContainerId, request.Image, 1,
                        layerError));
                    return 1;
                }

                using var _ = layerProvider;
                var silkUpperStore = Path.Combine(request.ContainerDir, "silk-upper");
                Directory.CreateDirectory(silkUpperStore);
                var silkType = FileSystemRegistry.Get("silkfs")
                               ?? throw new InvalidOperationException("silkfs is not registered");
                var overlayType = FileSystemRegistry.Get("overlay")
                                  ?? throw new InvalidOperationException("overlay is not registered");

                var upperSb = silkType.CreateFileSystem(runtime.DeviceNumbers)
                    .ReadSuper(silkType, 0, silkUpperStore, null);
                var overlaySb = overlayType.CreateFileSystem(runtime.DeviceNumbers).ReadSuper(overlayType, 0,
                    "root_overlay",
                    new OverlayMountOptions
                    {
                        Lower = layerLowerSb!,
                        Upper = upperSb
                    });
                runtime.Syscalls.MountRoot(overlaySb, new SyscallManager.RootMountOptions
                {
                    Source = "overlay",
                    FsType = "overlay",
                    Options = "rw,relatime,lowerdir=/,upperdir=/silk-upper,workdir=/work"
                });
                runtime.Syscalls.MountStandardDev(ttyDiag);
                runtime.Syscalls.MountStandardProc();
                runtime.Syscalls.MountStandardShm();
                runtime.Syscalls.CreateStandardTmp();
            }
            else
            {
                SuperBlock rootSb;
                string fsType;
                string source;

                if (request.RootFileSystemFactory != null)
                {
                    rootSb = request.RootFileSystemFactory(runtime.DeviceNumbers);
                    fsType = rootSb.Type?.Name ?? "tmpfs";
                    source = request.RootfsPath;
                }
                else
                {
                    var hostType = FileSystemRegistry.Get("hostfs")
                                   ?? throw new InvalidOperationException("hostfs is not registered");
                    rootSb = hostType.CreateFileSystem(runtime.DeviceNumbers)
                        .ReadSuper(hostType, 0, request.RootfsPath, null);
                    fsType = "hostfs";
                    source = request.RootfsPath;
                }

                runtime.Syscalls.MountRoot(rootSb, new SyscallManager.RootMountOptions
                {
                    Source = source,
                    FsType = fsType,
                    Options = "rw,relatime"
                });
                runtime.Syscalls.MountStandardDev(ttyDiag);
                runtime.Syscalls.MountStandardProc();
                runtime.Syscalls.MountStandardShm();
            }

            foreach (var vol in request.Volumes)
            {
                var parts = vol.Split(':');
                if (parts.Length < 2)
                {
                    _logger.LogWarning("Invalid volume format: {Volume}. Expected /host/path:/guest/path[:ro]", vol);
                    continue;
                }

                var hostPath = parts[0];
                var guestPath = parts[1];
                var readOnly = parts.Length > 2 && parts[2] == "ro";

                if (!Directory.Exists(hostPath) && !File.Exists(hostPath))
                {
                    _logger.LogWarning("Host path does not exist, skipping mount: {HostPath}", hostPath);
                    continue;
                }

                var hostInfo = new DirectoryInfo(hostPath);
                if (hostInfo.LinkTarget != null)
                {
                    var resolved = hostInfo.ResolveLinkTarget(true);
                    if (resolved != null)
                        hostPath = resolved.FullName;
                }

                _logger.LogInformation("Mounting {HostPath} at {GuestPath} (ro: {ReadOnly})", hostPath, guestPath,
                    readOnly);
                runtime.Syscalls.MountHostfs(hostPath, guestPath, readOnly);
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(request.Hostname))
                {
                    _logger.LogInformation("Mounting generated hostname at /etc/hostname via detached tmpfs");
                    runtime.Syscalls.MountDetachedTmpfsFile(
                        "/etc/hostname",
                        "hostname",
                        Encoding.UTF8.GetBytes(BuildHostnameFileContent(request.Hostname)));
                }

                if (!string.IsNullOrWhiteSpace(request.Hostname))
                {
                    _logger.LogInformation("Mounting generated hosts file at /etc/hosts via detached tmpfs");
                    runtime.Syscalls.MountDetachedTmpfsFile(
                        "/etc/hosts",
                        "hosts",
                        Encoding.UTF8.GetBytes(BuildHostsFileContent(request.Hostname, request.ContainerName)));
                }

                var resolvConf = BuildResolvConfContent(request.DnsServers);
                _logger.LogInformation("Mounting generated DNS configuration at /etc/resolv.conf via detached tmpfs");
                runtime.Syscalls.MountDetachedTmpfsFile(
                    "/etc/resolv.conf",
                    "resolv.conf",
                    Encoding.UTF8.GetBytes(resolvConf));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to mount DNS configuration. Network resolution may not work.");
            }

            var fullArgs = new[] { actualExe }.Concat(request.ExeArgs).ToArray();

            var finalEnvs = new List<string>
            {
                "PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
                "HOME=/root",
                "USER=root"
            };
            if (request.UseTty)
                finalEnvs.Add("TERM=xterm");
            foreach (var env in request.GuestEnvs)
                finalEnvs.Add(env);
            if (request.EnablePulseServer)
            {
                finalEnvs.Add(
                    $"{ContainerRunRequest.PulseServerEnvVar}=unix:{ContainerRunRequest.PulseServerSocketPath}");
                finalEnvs.Add($"{ContainerRunRequest.PulseRuntimePathEnvVar}=/run/pulse");
            }

            if (request.EnableWaylandServer)
            {
                finalEnvs.Add($"{ContainerRunRequest.WaylandDisplayEnvVar}=wayland-0");
                finalEnvs.Add($"{ContainerRunRequest.XdgRuntimeDirEnvVar}=/run");
            }

            startupPhase = "resolve-init";
            var (loc, guestPathResolved) = runtime.Syscalls.ResolvePath(actualExe, true);
            if (!loc.IsValid) throw new FileNotFoundException($"Could not find executable in VFS: {actualExe}");

            var uts = new UTSNamespace
            {
                NodeName = string.IsNullOrWhiteSpace(request.Hostname) ? request.ContainerId : request.Hostname
            };

            Process? engineInitProc = null;
            if (request.UseEngineInit)
            {
                engineInitProc = ProcessFactory.CreateEngineInitProcess(runtime, scheduler, uts);
                scheduler.SetEngineInitReaperEnabled(true);
                _logger.LogInformation("Engine init reaper enabled. PID 1 is reserved by runtime.");
            }

            startupPhase = "load-init";
            var mainTask = ProcessFactory.CreateInitProcess(runtime, loc.Dentry!, guestPathResolved, fullArgs,
                finalEnvs.ToArray(),
                scheduler, ttyDiag, loc.Mount!, uts, engineInitProc?.TGID ?? 0);
            if (request.EnablePulseServer || request.EnableWaylandServer)
                request.ConfigureVirtualDaemons?.Invoke(runtime, scheduler, uts, mainTask.Process.TGID);
            initProcessStarted = true;
            startupPhase = "running";
            request.ProcessController?.BindRuntimeControl(() =>
            {
                scheduler.ScheduleFromAnyThread(() =>
                {
                    try
                    {
                        // Best-effort container-wide writeback before forced stop.
                        runtime.Syscalls.SyncContainerPageCache();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Force-stop writeback failed");
                    }

                    scheduler.Running = false;
                    scheduler.WakeUp();
                });
            });
            var exposedInitPid = engineInitProc?.TGID ?? mainTask.Process.TGID;
            request.ProcessController?.BindInitProcess(exposedInitPid, sig =>
            {
                scheduler.ScheduleFromAnyThread(() =>
                {
                    if (request.UseEngineInit)
                        _ = scheduler.SignalProcess(exposedInitPid, sig);
                    else
                        mainTask.PostSignal(sig);
                });
            });
            request.EventStore.Append(new ContainerEvent(DateTimeOffset.UtcNow, "container-start", request.ContainerId,
                request.Image));

            _logger.LogDebug(
                "Starting scheduler run containerId={ContainerId} exe={Exe} args={Args} tty={UseTty} volumes={VolumeCount} logDriver={LogDriver} publishedPortCount={PublishedPortCount}",
                request.ContainerId, request.Exe, string.Join(" ", request.ExeArgs), request.UseTty,
                request.Volumes.Length,
                request.LogDriver, publishedPorts.Count);
            if (OperatingSystem.IsBrowser())
            {
                await scheduler.RunAsync();
            }
            else
            {
                scheduler.Run();
            }
            _logger.LogDebug(
                "Scheduler run returned containerId={ContainerId} mainExited={MainExited}",
                request.ContainerId, mainTask.Exited);

            TryExportGuestStats(runtime.Engine, request);

            if (!mainTask.Exited)
            {
                _logger.LogError(
                    "KernelScheduler returned but main task has not exited. status={Status} mode={Mode} pendingSyscall={HasPendingSyscall} continuation={HasContinuation}",
                    mainTask.Status, mainTask.ExecutionMode, mainTask.PendingSyscall != null,
                    mainTask.Continuation != null);
                request.EventStore.Append(new ContainerEvent(DateTimeOffset.UtcNow, "container-exit",
                    request.ContainerId, request.Image, 1,
                    "scheduler returned before main task exited"));
                return 1;
            }

            request.EventStore.Append(new ContainerEvent(DateTimeOffset.UtcNow, "container-exit", request.ContainerId,
                request.Image,
                mainTask.ExitStatus));
            if (ShouldPrintHandlerProfile())
                PrintHandlerProfile(runtime.Engine);
            if (ShouldPrintJccProfile())
                PrintJccProfile(runtime.Engine);
            return mainTask.ExitStatus;
        }
        catch (OutOfMemoryException ex)
        {
            var message = initProcessStarted
                ? $"ENOMEM: container ran out of memory while running '{actualExe}'."
                : $"ENOMEM: container startup failed before init process was ready (phase={startupPhase}, exe='{actualExe}').";
            _logger.LogError(ex,
                "Container OOM. containerId={ContainerId} initStarted={InitStarted} phase={Phase} exe={Exe}",
                request.ContainerId, initProcessStarted, startupPhase, actualExe);
            Console.Error.WriteLine($"[Podish OOM] {message}");
            request.EventStore.Append(new ContainerEvent(DateTimeOffset.UtcNow, "container-exit", request.ContainerId,
                request.Image, 1, message));
            return 1;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Critical Error during container emulation");
            Console.Error.WriteLine($"[Podish] Error: {ex}");
            request.EventStore.Append(new ContainerEvent(DateTimeOffset.UtcNow, "container-exit", request.ContainerId,
                request.Image, 1,
                ex.ToString()));
            return 1;
        }
        finally
        {
            _logger.LogDebug("Container teardown starting containerId={ContainerId}", request.ContainerId);
            try
            {
                if (OperatingSystem.IsBrowser()) await Task.Delay(50);
                else Task.Delay(50).Wait();
            }
            catch
            {
            }

            _logger.LogTrace("Container teardown flushing stdio containerId={ContainerId}", request.ContainerId);
            Console.Out.Flush();
            Console.Error.Flush();

            if (inputCts != null)
            {
                _logger.LogTrace("Container teardown cancelling input loop containerId={ContainerId}",
                    request.ContainerId);
                inputCts.Cancel();
                if (inputTask != null)
                    try
                    {
                        _logger.LogTrace("Container teardown waiting for input loop containerId={ContainerId}",
                            request.ContainerId);
                        if (OperatingSystem.IsBrowser()) await Task.WhenAny(inputTask, Task.Delay(100));
                        else Task.WhenAny(inputTask, Task.Delay(100)).Wait();
                    }
                    catch
                    {
                    }

                inputCts.Dispose();
            }

            _logger.LogTrace("Container teardown disposing SIGWINCH registration containerId={ContainerId}",
                request.ContainerId);
            sigwinch?.Dispose();
            if (driver is IDisposable driverDisposable)
            {
                _logger.LogTrace("Container teardown disposing tty driver type={DriverType} containerId={ContainerId}",
                    driver.GetType().Name, request.ContainerId);
                driverDisposable.Dispose();
            }

            try
            {
                _logger.LogTrace("Container teardown closing guest file descriptors containerId={ContainerId}",
                    request.ContainerId);
                foreach (var process in scheduler.GetProcessesSnapshot())
                    process.Syscalls.CloseAllFileDescriptors();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to close all guest file descriptors during container teardown");
            }

            if (processExitHandler != null)
                AppDomain.CurrentDomain.ProcessExit -= processExitHandler;

            if (cancelKeyPressHandler != null)
                Console.CancelKeyPress -= cancelKeyPressHandler;

            if (rawModeEnabled && (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ||
                                   RuntimeInformation.IsOSPlatform(OSPlatform.Linux)))
            {
                _logger.LogTrace("Container teardown disabling raw mode containerId={ContainerId}",
                    request.ContainerId);
                HostTermios.DisableRawMode(0);
                rawModeEnabled = false;
            }

            _logger.LogTrace("Container teardown disposing stdin stream containerId={ContainerId}",
                request.ContainerId);
            stdinStream?.Dispose();

            _logger.LogTrace("Container teardown unbinding controller containerId={ContainerId}", request.ContainerId);
            request.ProcessController?.Unbind();

            if (networkContext != null)
            {
                var portForwardStopped = _portForwardManager?.Stop(networkContext) ?? true;
                if (portForwardStopped)
                {
                    networkBackend?.DestroyContainerNetwork(networkContext);
                    networkContext.Dispose();
                }
                else
                {
                    _logger.LogCritical(
                        "Port forwarding loop failed to stop gracefully for container {ContainerId}. Leaking network resources to prevent memory corruption.",
                        request.ContainerId);
                }
            }

            networkBackend?.Dispose();
            _portForwardManager?.Dispose();

            _logger.LogDebug("Container teardown finished containerId={ContainerId}", request.ContainerId);
        }
    }

    private bool TryCreateLayerLower(DeviceNumberManager devNumbers, string ociStoreDir, out SuperBlock? lowerSb,
        out IDisposable? provider,
        out string error)
    {
        lowerSb = null;
        provider = null;
        error = string.Empty;

        var imagePath = Path.Combine(ociStoreDir, "image.json");
        if (!File.Exists(imagePath))
        {
            error =
                $"overlay mode expects OCI image store, but '{ociStoreDir}' has no image.json. Pull with `fiberpod pull --store-oci IMAGE`.";
            return false;
        }

        OciStoredImage? storedImage;
        try
        {
            storedImage = JsonSerializer.Deserialize(File.ReadAllText(imagePath),
                PodishJsonContext.Default.OciStoredImage);
        }
        catch (Exception ex)
        {
            error = $"failed to parse OCI image metadata: {ex.Message}";
            return false;
        }

        if (storedImage == null || storedImage.Layers.Count == 0)
        {
            error = "OCI image metadata is empty or invalid.";
            return false;
        }

        var digestToBlobPath = new Dictionary<string, string>(StringComparer.Ordinal);
        var layerIndexes = new List<IReadOnlyList<LayerIndexEntry>>(storedImage.Layers.Count);
        var normalizedLayers = new List<OciStoredLayer>(storedImage.Layers.Count);
        var metadataChanged = !string.Equals(storedImage.StoreDirectory, OciStorePath.RelativeStoreDirectory,
            StringComparison.Ordinal);
        foreach (var layer in storedImage.Layers)
        {
            string blobPath;
            string indexPath;
            try
            {
                blobPath = OciStorePath.Resolve(ociStoreDir, layer.BlobPath);
                indexPath = OciStorePath.Resolve(ociStoreDir, layer.IndexPath);
            }
            catch (Exception ex)
            {
                error =
                    $"invalid layer stored path for digest {layer.Digest}: blob='{layer.BlobPath}', index='{layer.IndexPath}', store='{ociStoreDir}', error='{ex.Message}'";
                return false;
            }

            if (!File.Exists(blobPath))
            {
                error = $"missing layer blob file: stored='{layer.BlobPath}', resolved='{blobPath}'";
                return false;
            }

            if (!File.Exists(indexPath))
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(indexPath) ?? ociStoreDir);
                    using var tarStream = File.OpenRead(blobPath);
                    var rebuilt = OciLayerIndexBuilder.BuildFromTar(tarStream, layer.Digest);
                    var persistedEntries = rebuilt.Entries.Values
                        .Select(e => e with { InlineData = null })
                        .ToList();
                    File.WriteAllText(indexPath,
                        JsonSerializer.Serialize(persistedEntries,
                            PodishJsonContext.Default.ListLayerIndexEntry));
                    _logger.LogWarning(
                        "Rebuilt missing layer index '{IndexPath}' from blob '{BlobPath}' for digest {Digest}",
                        indexPath, blobPath, layer.Digest);
                    metadataChanged = true;
                }
                catch (Exception ex)
                {
                    error = $"missing layer index file: {layer.IndexPath}; rebuild failed: {ex.Message}";
                    return false;
                }

            if (!File.Exists(indexPath))
            {
                error = $"missing layer index file: stored='{layer.IndexPath}', resolved='{indexPath}'";
                return false;
            }

            try
            {
                var entries =
                    JsonSerializer.Deserialize(File.ReadAllText(indexPath),
                        PodishJsonContext.Default.ListLayerIndexEntry);
                if (entries == null)
                {
                    error = $"invalid layer index JSON: {indexPath}";
                    return false;
                }

                layerIndexes.Add(entries);
            }
            catch (Exception ex)
            {
                error = $"failed to parse layer index '{indexPath}': {ex.Message}";
                return false;
            }

            digestToBlobPath[layer.Digest] = blobPath;
            var storedBlobPath = OciStorePath.ToStoredPath(ociStoreDir, blobPath);
            var storedIndexPath = OciStorePath.ToStoredPath(ociStoreDir, indexPath);
            if (!string.Equals(layer.BlobPath, storedBlobPath, StringComparison.Ordinal) ||
                !string.Equals(layer.IndexPath, storedIndexPath, StringComparison.Ordinal))
                metadataChanged = true;
            normalizedLayers.Add(layer with { BlobPath = storedBlobPath, IndexPath = storedIndexPath });
        }

        if (metadataChanged)
            try
            {
                var repaired = storedImage with
                {
                    StoreDirectory = OciStorePath.RelativeStoreDirectory, Layers = normalizedLayers
                };
                File.WriteAllText(imagePath,
                    JsonSerializer.Serialize(repaired, PodishJsonContext.Default.OciStoredImage));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist repaired OCI image metadata at {ImagePath}", imagePath);
            }

        var merged = MergeLayerIndexes(layerIndexes);
        var layerType = FileSystemRegistry.Get("layerfs");
        if (layerType == null)
        {
            error = "layerfs is not registered";
            return false;
        }

        provider = new TarBlobLayerContentProvider(digestToBlobPath);
        lowerSb = layerType.CreateFileSystem(devNumbers).ReadSuper(layerType, 0, "layer-lower",
            new LayerMountOptions { Index = merged, ContentProvider = (ILayerContentProvider)provider });
        return true;
    }

    private static LayerIndex MergeLayerIndexes(IReadOnlyList<IReadOnlyList<LayerIndexEntry>> layers)
    {
        var merged = new Dictionary<string, LayerIndexEntry>(StringComparer.Ordinal)
        {
            ["/"] = new("/", InodeType.Directory, 0x1ED)
        };

        foreach (var layer in layers)
        foreach (var entry in layer)
        {
            var path = NormalizeAbsolutePath(entry.Path);
            if (path == "/") continue;

            var parent = ParentPath(path);
            var name = BaseName(path);
            if (name == ".wh..wh..opq")
            {
                RemoveAllChildren(merged, parent);
                continue;
            }

            if (name.StartsWith(".wh.", StringComparison.Ordinal) && name.Length > 4)
            {
                var hiddenName = name[4..];
                var hiddenPath = parent == "/" ? "/" + hiddenName : parent + "/" + hiddenName;
                RemovePathWithDescendants(merged, hiddenPath);
                continue;
            }

            merged[path] = entry with { Path = path };
        }

        var index = new LayerIndex();
        foreach (var entry in merged.Values
                     .Where(e => e.Path != "/")
                     .OrderBy(e => e.Path.Count(c => c == '/'))
                     .ThenBy(e => e.Path, StringComparer.Ordinal))
            index.AddEntry(entry);
        return index;
    }

    private static void RemoveAllChildren(Dictionary<string, LayerIndexEntry> merged, string parentPath)
    {
        var prefix = parentPath == "/" ? "/" : parentPath + "/";
        var keys = merged.Keys.Where(k => k != "/" && k.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        foreach (var k in keys)
            merged.Remove(k);
    }

    private static void RemovePathWithDescendants(Dictionary<string, LayerIndexEntry> merged, string path)
    {
        var normalized = NormalizeAbsolutePath(path);
        merged.Remove(normalized);
        var prefix = normalized == "/" ? "/" : normalized + "/";
        var keys = merged.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        foreach (var k in keys)
            merged.Remove(k);
    }

    private static string NormalizeAbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";
        var p = path.Replace('\\', '/');
        if (!p.StartsWith('/')) p = "/" + p;
        while (p.Contains("//", StringComparison.Ordinal)) p = p.Replace("//", "/", StringComparison.Ordinal);
        if (p.Length > 1 && p.EndsWith('/')) p = p.TrimEnd('/');
        return p;
    }

    private static string ParentPath(string path)
    {
        var normalized = NormalizeAbsolutePath(path);
        if (normalized == "/") return "/";
        var lastSlash = normalized.LastIndexOf('/');
        return lastSlash <= 0 ? "/" : normalized[..lastSlash];
    }

    private static string BaseName(string path)
    {
        var normalized = NormalizeAbsolutePath(path);
        if (normalized == "/") return "/";
        var lastSlash = normalized.LastIndexOf('/');
        return lastSlash < 0 ? normalized : normalized[(lastSlash + 1)..];
    }

    private string BuildResolvConfContent(string[] dnsServers)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Generated by Podish");

        if (dnsServers.Length > 0)
        {
            foreach (var dns in dnsServers)
                sb.AppendLine($"nameserver {dns}");
        }
        else
        {
            var hostResolvConf = "/etc/resolv.conf";
            var definedNameservers = 0;
            if (File.Exists(hostResolvConf))
                try
                {
                    var lines = File.ReadAllLines(hostResolvConf);
                    foreach (var line in lines)
                        if (line.TrimStart().StartsWith("nameserver "))
                        {
                            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length > 1)
                            {
                                var ip = parts[1];
                                if (ip.StartsWith("127."))
                                {
                                    _logger.LogInformation(
                                        "Host DNS is local loopback ({IP}). Falling back to 8.8.8.8 for guest.", ip);
                                    sb.AppendLine("nameserver 8.8.8.8");
                                }
                                else
                                {
                                    sb.AppendLine($"nameserver {ip}");
                                }

                                definedNameservers++;
                            }
                        }
                        else
                        {
                            sb.AppendLine(line);
                        }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read host /etc/resolv.conf");
                }

            if (definedNameservers == 0)
                sb.AppendLine("nameserver 8.8.8.8");
        }

        return sb.ToString();
    }

    private static string BuildHostnameFileContent(string hostname)
    {
        return hostname.Trim() + "\n";
    }

    private static string BuildHostsFileContent(string hostname, string? containerName)
    {
        var primary = hostname.Trim();
        var aliases = new List<string> { primary };
        if (!string.IsNullOrWhiteSpace(containerName))
        {
            var alias = containerName.Trim();
            if (!string.Equals(alias, primary, StringComparison.Ordinal))
                aliases.Add(alias);
        }

        var sb = new StringBuilder();
        sb.AppendLine("127.0.0.1 localhost");
        sb.Append("127.0.1.1 ");
        sb.AppendLine(string.Join(" ", aliases));
        sb.AppendLine("::1 localhost ip6-localhost ip6-loopback");
        return sb.ToString();
    }

    private static async Task InputLoop(TtyDiscipline tty, Stream stdin, CancellationToken token)
    {
        var buffer = new byte[256];
        try
        {
            while (!token.IsCancellationRequested)
            {
                var read = await stdin.ReadAsync(buffer, token);
                if (read == 0) break;
                tty.Input(buffer.AsSpan(0, read).ToArray());
            }
        }
        catch
        {
        }
    }

    private IContainerLogSink CreateContainerLogSink(ContainerLogDriver driver, string containerDir,
        ILoggerFactory loggerFactory)
    {
        return driver switch
        {
            ContainerLogDriver.JsonFile => new JsonFileContainerLogSink(Path.Combine(containerDir, "ctr.log"),
                loggerFactory.CreateLogger<JsonFileContainerLogSink>()),
            ContainerLogDriver.None => new NoneContainerLogSink(),
            _ => new NoneContainerLogSink()
        };
    }

    private static bool ShouldPrintHandlerProfile()
    {
        var value = Environment.GetEnvironmentVariable("PODISH_HANDLER_PROFILE");
        return !string.IsNullOrWhiteSpace(value) && value != "0";
    }

    private static bool ShouldPrintJccProfile()
    {
        var value = Environment.GetEnvironmentVariable("PODISH_JCC_PROFILE");
        return !string.IsNullOrWhiteSpace(value) && value != "0";
    }

    private static void PrintHandlerProfile(Engine engine)
    {
        var stats = engine.GetHandlerProfileStats()
            .Where(static x => x.ExecCount != 0)
            .OrderByDescending(static x => x.ExecCount)
            .ToArray();
        var imageBase = engine.GetNativeImageBase();

        Console.Error.WriteLine("[Podish.HandlerProfile.Begin]");
        Console.Error.WriteLine($"base\t0x{imageBase.ToInt64():x}");
        foreach (var stat in stats)
            Console.Error.WriteLine($"{stat.ExecCount}\t0x{stat.Handler.ToInt64():x}");
        Console.Error.WriteLine("[Podish.HandlerProfile.End]");
    }

    private static void PrintJccProfile(Engine engine)
    {
        var stats = engine.GetJccProfileStats()
            .Where(static x => x.Taken != 0 || x.NotTaken != 0 || x.CacheHit != 0 || x.CacheMiss != 0)
            .OrderByDescending(static x => x.Taken + x.NotTaken)
            .ToArray();
        var imageBase = engine.GetNativeImageBase();

        Console.Error.WriteLine("[Podish.JccProfile.Begin]");
        Console.Error.WriteLine($"base\t0x{imageBase.ToInt64():x}");
        foreach (var stat in stats)
            Console.Error.WriteLine(
                $"{stat.Taken}\t{stat.NotTaken}\t{stat.CacheHit}\t{stat.CacheMiss}\t0x{stat.Handler.ToInt64():x}");

        Console.Error.WriteLine("[Podish.JccProfile.End]");
    }

    private void TryExportGuestStats(Engine engine, ContainerRunRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.GuestStatsExportDir))
            return;

        try
        {
            var exportDir = Path.GetFullPath(request.GuestStatsExportDir);
            Directory.CreateDirectory(exportDir);

            var blocksPath = Path.Combine(exportDir, "blocks.bin");
            var skipBlocksDump = Environment.GetEnvironmentVariable("PODISH_SKIP_BLOCKS_EXPORT") is
                                     { Length: > 0 } value &&
                                 value != "0";
            if (!skipBlocksDump)
            {
                using var fs = File.Create(blocksPath);
                engine.DumpBlocks(fs);
            }

            var nativeStats = engine.DumpStats();
            var blockStats = engine.GetBlockStats();
            var handlerProfile = engine.GetHandlerProfileStats()
                .Where(static x => x.ExecCount != 0)
                .OrderByDescending(static x => x.ExecCount)
                .Select(static x => new GuestStatsHandlerProfileEntry(
                    $"0x{x.Handler.ToInt64():x}",
                    x.ExecCount))
                .ToArray();
            var jccProfile = engine.GetJccProfileStats()
                .Where(static x => x.Taken != 0 || x.NotTaken != 0 || x.CacheHit != 0 || x.CacheMiss != 0)
                .OrderByDescending(static x => x.Taken + x.NotTaken)
                .Select(static x => new GuestStatsJccProfileEntry(
                    $"0x{x.Handler.ToInt64():x}",
                    x.Taken,
                    x.NotTaken,
                    x.CacheHit,
                    x.CacheMiss))
                .ToArray();

            var summary = new GuestStatsSummary(
                1,
                DateTimeOffset.UtcNow,
                request.ContainerId,
                request.Image,
                $"0x{engine.GetNativeImageBase().ToInt64():x}",
                nativeStats,
                GuestStatsBlockStats.FromSnapshot(blockStats),
                handlerProfile,
                jccProfile,
                new GuestStatsFiles("blocks.bin"));

            var summaryPath = Path.Combine(exportDir, "summary.json");
            using (var stream = File.Create(summaryPath))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                JsonSerializer.Serialize(writer, summary, PodishJsonContext.Default.GuestStatsSummary);
            }

            _logger.LogInformation("Exported guest stats for container {ContainerId} to {ExportDir}",
                request.ContainerId, exportDir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to export guest stats for container {ContainerId}",
                request.ContainerId);
        }
    }

    private sealed class TarBlobLayerContentProvider : ILayerContentProvider, IDisposable
    {
        private readonly Dictionary<string, string> _digestToBlobPath;
        private readonly Lock _lock = new();
        private readonly Dictionary<string, FileStream> _streams = new(StringComparer.Ordinal);

        public TarBlobLayerContentProvider(Dictionary<string, string> digestToBlobPath)
        {
            _digestToBlobPath = digestToBlobPath;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var s in _streams.Values)
                    s.Dispose();
                _streams.Clear();
            }
        }

        public bool TryRead(LayerIndexEntry entry, long offset, Span<byte> buffer, out int bytesRead)
        {
            bytesRead = 0;
            if (entry.Type != InodeType.File) return true;

            var hasBlobBacking = entry.DataOffset >= 0 && !string.IsNullOrWhiteSpace(entry.BlobDigest);
            if (!hasBlobBacking && entry.InlineData != null)
            {
                if (offset >= entry.InlineData.Length) return true;
                var remaining = entry.InlineData.Length - (int)offset;
                var toCopy = Math.Min(buffer.Length, remaining);
                entry.InlineData.AsSpan((int)offset, toCopy).CopyTo(buffer);
                bytesRead = toCopy;
                return true;
            }

            if (entry.DataOffset < 0 || string.IsNullOrWhiteSpace(entry.BlobDigest))
                return false;
            if (!_digestToBlobPath.TryGetValue(entry.BlobDigest, out var blobPath))
                return false;
            if (offset < 0) return false;
            if ((ulong)offset >= entry.Size) return true;

            var remainingInEntry = (long)entry.Size - offset;
            if (remainingInEntry <= 0) return true;
            var maxReadable = (int)Math.Min(buffer.Length, remainingInEntry);
            if (maxReadable <= 0) return true;

            lock (_lock)
            {
                if (!_streams.TryGetValue(entry.BlobDigest, out var stream))
                {
                    stream = new FileStream(blobPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    _streams[entry.BlobDigest] = stream;
                }

                var start = entry.DataOffset + offset;
                if (start < 0 || start >= stream.Length)
                    return true;

                stream.Seek(start, SeekOrigin.Begin);
                bytesRead = stream.Read(buffer[..maxReadable]);
                return true;
            }
        }
    }

    private sealed class ConsoleTtyDriver : ITtyDriver
    {
        private readonly IContainerLogSink _containerLogSink;
        private readonly Stream _stderr = Console.OpenStandardError();
        private readonly Stream _stdout = Console.OpenStandardOutput();
        private TtyDiscipline? _tty;

        public ConsoleTtyDriver(IContainerLogSink containerLogSink)
        {
            _containerLogSink = containerLogSink;
        }

        public int Write(TtyEndpointKind kind, ReadOnlySpan<byte> buffer)
        {
            var stream = kind == TtyEndpointKind.Stderr ? _stderr : _stdout;
            stream.Write(buffer);
            stream.Flush();
            _containerLogSink.Write(kind, buffer);
            return buffer.Length;
        }

        public void Flush()
        {
            _stdout.Flush();
            _stderr.Flush();
        }

        public bool CanWrite => true;

        public bool RegisterWriteWait(Action callback, KernelScheduler scheduler)
        {
            _ = scheduler;
            return false;
        }

        public void BindTty(TtyDiscipline tty)
        {
            _tty = tty;
        }
    }

    private sealed class BridgeTtyDriver : ITtyDriver, IDisposable
    {
        private const int OutputQueueCapacityBytes = 64 * 1024;
        private readonly PodishTerminalBridge _bridge;
        private readonly IContainerLogSink _containerLogSink;
        private readonly Lock _lock = new();
        private readonly AsyncWaitQueue _writeReady;
        private int _queuedBytes;
        private bool _disposed;

        public BridgeTtyDriver(PodishTerminalBridge bridge, IContainerLogSink containerLogSink,
            KernelScheduler scheduler)
        {
            _bridge = bridge;
            _containerLogSink = containerLogSink;
            _writeReady = new AsyncWaitQueue(scheduler);
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _disposed = true;
            }
        }

        public int Write(TtyEndpointKind kind, ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length == 0)
                return 0;

            byte[] payload;
            lock (_lock)
            {
                if (_disposed)
                    return -(int)Errno.EIO;

                var space = OutputQueueCapacityBytes - _queuedBytes;
                if (space <= 0)
                {
                    _writeReady.Reset();
                    return -(int)Errno.EAGAIN;
                }

                var written = Math.Min(space, buffer.Length);
                payload = buffer[..written].ToArray();
                _queuedBytes += written;
            }

            // On Wasm (single-threaded), emit synchronously on scheduler thread.
            // On native, same: the pump is the caller, so no background thread needed.
            _containerLogSink.Write(kind, payload);
            _bridge.EmitOutput(kind, payload);

            lock (_lock)
            {
                _queuedBytes -= payload.Length;
                if (_queuedBytes < OutputQueueCapacityBytes)
                    _writeReady.Signal();
            }

            return payload.Length;
        }

        public void Flush()
        {
        }

        public bool CanWrite
        {
            get
            {
                lock (_lock)
                {
                    return _queuedBytes < OutputQueueCapacityBytes;
                }
            }
        }

        public bool RegisterWriteWait(Action callback, KernelScheduler scheduler)
        {
            if (CanWrite)
                return false;
            _writeReady.Register(callback, scheduler);
            return true;
        }
    }

    private sealed class SchedulerSignalBroadcaster : ISignalBroadcaster
    {
        private readonly KernelScheduler _scheduler;

        public SchedulerSignalBroadcaster(KernelScheduler scheduler)
        {
            _scheduler = scheduler;
        }

        public void SignalProcessGroup(FiberTask? task, int pgid, int signal)
        {
            _scheduler.SignalProcessGroupFromAnyThread(pgid, signal);
        }

        public void SignalForegroundTask(FiberTask? task, int signal)
        {
            if (task != null)
                _scheduler.SignalTaskFromAnyThread(task, signal);
        }
    }
}
