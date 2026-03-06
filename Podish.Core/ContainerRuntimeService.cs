using System.Runtime.InteropServices;
using System.Text;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.Core.VFS.TTY;
using Fiberish.Diagnostics;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Podish.Core;

public sealed class ContainerRunRequest
{
    public required string RootfsPath { get; init; }
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
}

public sealed class ContainerRuntimeService
{
    private readonly ILogger _logger;
    private readonly ILoggerFactory _loggerFactory;

    public ContainerRuntimeService(ILogger logger, ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    public async Task<int> RunAsync(ContainerRunRequest request)
    {
        await Task.CompletedTask;

        var scheduler = new KernelScheduler();
        scheduler.LoggerFactory = _loggerFactory;

        TtyDiscipline? ttyDiag = null;
        KernelRuntime? runtime = null;
        FileStream? stdinStream = null;
        CancellationTokenSource? inputCts = null;
        Task? inputTask = null;
        PosixSignalRegistration? sigwinch = null;
        var isInteractive = request.UseTty && request.EnableHostConsoleInput && !Console.IsInputRedirected;

        using var logSink = CreateContainerLogSink(request.LogDriver, request.ContainerDir, _loggerFactory);
        ITtyDriver driver = request.TerminalBridge != null
            ? new BridgeTtyDriver(request.TerminalBridge, logSink)
            : new ConsoleTtyDriver(logSink);
        var broadcaster = new SchedulerSignalBroadcaster(scheduler);
        ttyDiag = new TtyDiscipline(driver, broadcaster, _loggerFactory.CreateLogger<TtyDiscipline>());
        if (driver is ConsoleTtyDriver consoleDriver)
            consoleDriver.BindTty(ttyDiag);
        if (request.TerminalBridge != null)
            request.TerminalBridge.BindTty(ttyDiag);
        scheduler.Tty = ttyDiag;

        if (isInteractive)
        {
            stdinStream = new FileStream(new SafeFileHandle(0, true), FileAccess.Read);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var res = Fiberish.Core.VFS.TTY.MacOSTermios.EnableRawMode(0);
                if (res != 0) Console.Error.WriteLine($"Warning: Failed to enable raw mode: {res}");

                // Last-resort cleanup: disable raw mode on any exit path
                // (Ctrl-C, unhandled exception, Environment.Exit, etc.)
                void RawModeCleanup()
                {
                    try { Fiberish.Core.VFS.TTY.MacOSTermios.DisableRawMode(0); } catch { }
                }
                AppDomain.CurrentDomain.ProcessExit += (_, _) => RawModeCleanup();
                Console.CancelKeyPress += (_, e) =>
                {
                    RawModeCleanup();
                    // Don't cancel — let the default SIGINT handling terminate the process.
                };
            }

            inputCts = new CancellationTokenSource();
            inputTask = Task.Run(() => InputLoop(ttyDiag, stdinStream, inputCts.Token));

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                try
                {
                    if (!Console.IsOutputRedirected)
                        ttyDiag.Device.EnqueueResize(Console.WindowHeight, Console.WindowWidth);

                    sigwinch = PosixSignalRegistration.Create(PosixSignal.SIGWINCH, context =>
                    {
                        context.Cancel = true;
                        try
                        {
                            if (!Console.IsOutputRedirected)
                                ttyDiag.Device.EnqueueResize(Console.WindowHeight, Console.WindowWidth);
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
        }

        try
        {
            if (!Directory.Exists(request.RootfsPath))
            {
                Console.Error.WriteLine($"[Podish Error] RootFS path not found: {request.RootfsPath}");
                request.EventStore.Append(new ContainerEvent(DateTimeOffset.UtcNow, "container-exit",
                    request.ContainerId,
                    request.Image, 1,
                    "rootfs not found"));
                return 1;
            }

            runtime = KernelRuntime.BootstrapBare(request.Strace, ttyDiag);
            if (request.UseOverlay)
            {
                if (!TryCreateLayerLower(request.RootfsPath, out var layerLowerSb, out var layerProvider,
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

                var upperSb = silkType.CreateFileSystem().ReadSuper(silkType, 0, silkUpperStore, null);
                var overlaySb = overlayType.CreateFileSystem().ReadSuper(overlayType, 0, "root_overlay",
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
                var hostType = FileSystemRegistry.Get("hostfs")
                               ?? throw new InvalidOperationException("hostfs is not registered");
                var hostSb = hostType.CreateFileSystem().ReadSuper(hostType, 0, request.RootfsPath, null);
                runtime.Syscalls.MountRoot(hostSb, new SyscallManager.RootMountOptions
                {
                    Source = request.RootfsPath,
                    FsType = "hostfs",
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
                var resolvConf = BuildResolvConfContent(request.DnsServers);
                _logger.LogInformation("Mounting generated DNS configuration at /etc/resolv.conf via detached tmpfs");
                runtime.Syscalls.MountDetachedTmpfsFile(
                    "/etc/resolv.conf",
                    "resolv.conf",
                    Encoding.UTF8.GetBytes(resolvConf),
                    readOnly: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to mount DNS configuration. Network resolution may not work.");
            }

            var actualExe = string.IsNullOrEmpty(request.Exe) ? "/bin/sh" : request.Exe;
            var fullArgs = new[] { actualExe }.Concat(request.ExeArgs).ToArray();

            var finalEnvs = new List<string>
            {
                "PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
                "HOME=/root",
                "TERM=xterm",
                "USER=root"
            };
            foreach (var env in request.GuestEnvs)
                finalEnvs.Add(env);

            var (loc, guestPathResolved) = runtime.Syscalls.ResolvePath(actualExe, true);
            if (!loc.IsValid) throw new FileNotFoundException($"Could not find executable in VFS: {actualExe}");

            var mainTask = ProcessFactory.CreateInitProcess(runtime, loc.Dentry!, guestPathResolved, fullArgs,
                finalEnvs.ToArray(),
                scheduler, ttyDiag, loc.Mount!);
            request.ProcessController?.BindRuntimeControl(() =>
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
            request.ProcessController?.BindInitProcess(mainTask.Process.TGID, sig => mainTask.PostSignal(sig));
            request.EventStore.Append(new ContainerEvent(DateTimeOffset.UtcNow, "container-start", request.ContainerId,
                request.Image));

            _logger.LogDebug(
                "Starting scheduler run containerId={ContainerId} exe={Exe} args={Args} tty={UseTty} volumes={VolumeCount} logDriver={LogDriver}",
                request.ContainerId, request.Exe, string.Join(" ", request.ExeArgs), request.UseTty, request.Volumes.Length,
                request.LogDriver);
            scheduler.Run();
            _logger.LogDebug(
                "Scheduler run returned containerId={ContainerId} mainExited={MainExited}",
                request.ContainerId, mainTask.Exited);

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
            return mainTask.ExitStatus;
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
                Task.Delay(50).Wait();
            }
            catch
            {
            }

            _logger.LogTrace("Container teardown flushing stdio containerId={ContainerId}", request.ContainerId);
            Console.Out.Flush();
            Console.Error.Flush();

            _logger.LogTrace("Container teardown disposing stdin stream containerId={ContainerId}", request.ContainerId);
            stdinStream?.Dispose();

            if (inputCts != null)
            {
                _logger.LogTrace("Container teardown cancelling input loop containerId={ContainerId}", request.ContainerId);
                inputCts.Cancel();
                if (inputTask != null)
                {
                    try
                    {
                        _logger.LogTrace("Container teardown waiting for input loop containerId={ContainerId}", request.ContainerId);
                        Task.WhenAny(inputTask, Task.Delay(100)).Wait();
                    }
                    catch
                    {
                    }
                }

                inputCts.Dispose();
            }

            _logger.LogTrace("Container teardown disposing SIGWINCH registration containerId={ContainerId}", request.ContainerId);
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

            if (isInteractive && RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                _logger.LogTrace("Container teardown disabling raw mode containerId={ContainerId}", request.ContainerId);
                Fiberish.Core.VFS.TTY.MacOSTermios.DisableRawMode(0);
            }

            _logger.LogTrace("Container teardown unbinding controller containerId={ContainerId}", request.ContainerId);
            request.ProcessController?.Unbind();
            _logger.LogDebug("Container teardown finished containerId={ContainerId}", request.ContainerId);
        }
    }

    private bool TryCreateLayerLower(string ociStoreDir, out SuperBlock? lowerSb, out IDisposable? provider,
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
            storedImage = System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(imagePath),
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
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(indexPath) ?? ociStoreDir);
                    using var tarStream = File.OpenRead(blobPath);
                    var rebuilt = OciLayerIndexBuilder.BuildFromTar(tarStream, layer.Digest);
                    var persistedEntries = rebuilt.Entries.Values
                        .Select(e => e with { InlineData = null })
                        .ToList();
                    File.WriteAllText(indexPath,
                        System.Text.Json.JsonSerializer.Serialize(persistedEntries,
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
            }

            if (!File.Exists(indexPath))
            {
                error = $"missing layer index file: stored='{layer.IndexPath}', resolved='{indexPath}'";
                return false;
            }

            try
            {
                var entries =
                    System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(indexPath),
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
        {
            try
            {
                var repaired = storedImage with
                {
                    StoreDirectory = OciStorePath.RelativeStoreDirectory, Layers = normalizedLayers
                };
                File.WriteAllText(imagePath,
                    System.Text.Json.JsonSerializer.Serialize(repaired, PodishJsonContext.Default.OciStoredImage));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist repaired OCI image metadata at {ImagePath}", imagePath);
            }
        }

        var merged = MergeLayerIndexes(layerIndexes);
        var layerType = FileSystemRegistry.Get("layerfs");
        if (layerType == null)
        {
            error = "layerfs is not registered";
            return false;
        }

        provider = new TarBlobLayerContentProvider(digestToBlobPath);
        lowerSb = layerType.CreateFileSystem().ReadSuper(layerType, 0, "layer-lower",
            new LayerMountOptions { Index = merged, ContentProvider = (ILayerContentProvider)provider });
        return true;
    }

    private static LayerIndex MergeLayerIndexes(IReadOnlyList<IReadOnlyList<LayerIndexEntry>> layers)
    {
        var merged = new Dictionary<string, LayerIndexEntry>(StringComparer.Ordinal)
        {
            ["/"] = new LayerIndexEntry("/", InodeType.Directory, 0x1ED)
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
            {
                try
                {
                    var lines = File.ReadAllLines(hostResolvConf);
                    foreach (var line in lines)
                    {
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
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read host /etc/resolv.conf");
                }
            }

            if (definedNameservers == 0)
                sb.AppendLine("nameserver 8.8.8.8");
        }

        return sb.ToString();
    }

    private sealed class TarBlobLayerContentProvider : ILayerContentProvider, IDisposable
    {
        private readonly Dictionary<string, string> _digestToBlobPath;
        private readonly Dictionary<string, FileStream> _streams = new(StringComparer.Ordinal);
        private readonly object _lock = new();

        public TarBlobLayerContentProvider(Dictionary<string, string> digestToBlobPath)
        {
            _digestToBlobPath = digestToBlobPath;
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

        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var s in _streams.Values)
                    s.Dispose();
                _streams.Clear();
            }
        }
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

    private sealed class ConsoleTtyDriver : ITtyDriver
    {
        private readonly Stream _stderr = Console.OpenStandardError();
        private readonly Stream _stdout = Console.OpenStandardOutput();
        private readonly IContainerLogSink _containerLogSink;
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

        public bool RegisterWriteWait(Action callback)
        {
            return false;
        }

        public void BindTty(TtyDiscipline tty)
        {
            _tty = tty;
        }
    }

    private sealed class BridgeTtyDriver : ITtyDriver, IDisposable
    {
        private readonly object _lock = new();
        private readonly Queue<(TtyEndpointKind Kind, byte[] Data)> _queue = new();
        private readonly AsyncWaitQueue _writeReady = new();
        private readonly AutoResetEvent _hasData = new(false);
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _pumpTask;
        private readonly PodishTerminalBridge _bridge;
        private readonly IContainerLogSink _containerLogSink;
        private int _queuedBytes;
        private const int OutputQueueCapacityBytes = 64 * 1024;

        public BridgeTtyDriver(PodishTerminalBridge bridge, IContainerLogSink containerLogSink)
        {
            _bridge = bridge;
            _containerLogSink = containerLogSink;
            _pumpTask = Task.Run(PumpLoop);
        }

        public int Write(TtyEndpointKind kind, ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length == 0)
                return 0;

            int written;
            var payload = Array.Empty<byte>();
            lock (_lock)
            {
                var space = OutputQueueCapacityBytes - _queuedBytes;
                if (space <= 0)
                {
                    _writeReady.Reset();
                    return -(int)Errno.EAGAIN;
                }

                written = Math.Min(space, buffer.Length);
                payload = buffer[..written].ToArray();
                _queue.Enqueue((kind, payload));
                _queuedBytes += written;
                if (_queuedBytes >= OutputQueueCapacityBytes)
                    _writeReady.Reset();
            }

            _containerLogSink.Write(kind, payload);
            _hasData.Set();
            return written;
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

        public bool RegisterWriteWait(Action callback)
        {
            if (CanWrite)
                return false;
            _writeReady.Register(callback);
            return true;
        }

        private void PumpLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                _hasData.WaitOne(50);
                while (true)
                {
                    (TtyEndpointKind Kind, byte[] Data) item;
                    var becameWritable = false;
                    lock (_lock)
                    {
                        if (_queue.Count == 0)
                            break;

                        var wasFull = _queuedBytes >= OutputQueueCapacityBytes;
                        item = _queue.Dequeue();
                        _queuedBytes -= item.Data.Length;
                        becameWritable = wasFull && _queuedBytes < OutputQueueCapacityBytes;
                    }

                    if (becameWritable)
                        _writeReady.Signal();

                    _bridge.EmitOutput(item.Kind, item.Data);
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _hasData.Set();
            try
            {
                _pumpTask.Wait(200);
            }
            catch
            {
            }

            _cts.Dispose();
            _hasData.Dispose();
        }
    }

    private sealed class SchedulerSignalBroadcaster : ISignalBroadcaster
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
        }
    }
}
