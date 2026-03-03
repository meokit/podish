using System.CommandLine;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Fiberish.Core;
using Fiberish.Core.VFS;
using Fiberish.Core.VFS.TTY;
using Fiberish.Diagnostics;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace FiberPod;

internal class Program
{
    private static ILogger Logger = null!;

    private static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("FiberPod - Podman-like CLI for x86emu");

        // Global Options
        var logLevelOption = new Option<LogLevel>(
            new[] { "--log-level", "-l" },
            () => LogLevel.Warning,
            "Set the logging level (Trace, Debug, Information, Warning, Error, Critical)");
        var logFileOption = new Option<string?>(
            new[] { "--log-file" },
            () => null,
            "Path to a file where logs will be written (default to stderr if not set)");

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
        var imageArgument = new Argument<string?>("image", () => null, "Image name");
        var exeArgument =
            new Argument<string?>("command", () => null, "Command to execute (optional if image has entrypoint)");
        var exeArgsArgument = new Argument<string[]>("args", () => Array.Empty<string>(), "Command arguments");

        runCommand.AddOption(volumeOption);
        runCommand.AddOption(interactiveOption);
        runCommand.AddOption(ttyOption);
        runCommand.AddOption(straceOption);
        runCommand.AddOption(rootfsOption);
        runCommand.AddOption(envOption);
        runCommand.AddOption(dnsOption);
        runCommand.AddOption(containerLogDriverOption);
        runCommand.AddArgument(imageArgument);
        runCommand.AddArgument(exeArgument);
        runCommand.AddArgument(exeArgsArgument);

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
            var image = context.ParseResult.GetValueForArgument(imageArgument);
            var exe = context.ParseResult.GetValueForArgument(exeArgument);
            var exeArgs = context.ParseResult.GetValueForArgument(exeArgsArgument) ?? Array.Empty<string>();
            var useRootfs = !string.IsNullOrWhiteSpace(rootfs);

            if (useRootfs)
            {
                // Podman-compatible: with --rootfs, positional arguments are command + args.
                if (!string.IsNullOrWhiteSpace(image))
                {
                    if (!string.IsNullOrWhiteSpace(exe))
                        exeArgs = new[] { exe! }.Concat(exeArgs).ToArray();
                    exe = image;
                    image = null;
                }
            }

            var logLevel = context.ParseResult.GetValueForOption(logLevelOption);
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

            if (!ContainerLogDriverParser.TryParse(containerLogDriverRaw, out var containerLogDriver))
            {
                Console.Error.WriteLine($"[FiberPod] invalid --log-driver value: {containerLogDriverRaw}. Use json-file|none");
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
                    Console.Error.WriteLine("[FiberPod] image is required unless --rootfs is set");
                    context.ExitCode = 125;
                    return;
                }

                if (!Directory.Exists(rootfsPath) && Directory.Exists(ociStoreDir))
                {
                    rootfsPath = ociStoreDir;
                }
                else if (!Directory.Exists(rootfsPath))
                {
                    Console.Error.WriteLine($"[FiberPod] Unable to find image '{image}' locally");
                    Console.Error.WriteLine($"[FiberPod] Trying to pull {image}...");
                    var pullService = new OciPullService(Logger);
                    try
                    {
                        await pullService.PullAndStoreImageAsync(image, ociStoreDir);
                        rootfsPath = ociStoreDir;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[FiberPod Error] Failed to pull image: {ex.Message}");
                        context.ExitCode = 1;
                        return;
                    }
                }
            }
            else
            {
                if (!Directory.Exists(rootfsPath))
                {
                    Console.Error.WriteLine($"[FiberPod Error] --rootfs path not found: {rootfsPath}");
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
            var logLevel = context.ParseResult.GetValueForOption(logLevelOption);
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
                        $"[FiberPod] Image {image} stored as OCI layers at {stored.StoreDirectory} ({stored.Layers.Count} layers)");
                }
                else
                {
                    await pullService.PullAndExtractImageAsync(image, outputDir);
                    Console.WriteLine($"[FiberPod] Image {image} pulled successfully to {outputDir}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[FiberPod] Failed to pull image {image}: {ex.Message}");
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
                Console.Error.WriteLine($"[FiberPod] log file not found for container {containerId}");
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
        rootCommand.AddCommand(logsCommand);
        rootCommand.AddCommand(eventsCommand);

        return await rootCommand.InvokeAsync(args);
    }

    private static void SetupLogging(LogLevel logLevel, string? logFile)
    {
        Logging.LoggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(logLevel);
            if (!string.IsNullOrEmpty(logFile))
            {
                // Write to file
                builder.AddProvider(new FileLoggerProvider(logFile));
            }

            // Write to Console Error (stderr) ALWAYS
            builder.AddConsole(options =>
            {
                options.LogToStandardErrorThreshold = LogLevel.Trace; // All logs to stderr
            });
        });
        Logger = Logging.CreateLogger<Program>();
    }

    private static async Task<int> RunContainer(string rootfsPath, string exe, string[] exeArgs, string[] volumes,
        string[] guestEnvs, string[] dnsServers, bool useTty, bool strace, bool useOverlay, string containersDir,
        string containerId, string image, string containerDir, ContainerLogDriver logDriver, ContainerEventStore eventStore)
    {
        await Task.CompletedTask; // TODO: remove async?

        // 1. Create Kernel Scheduler
        var scheduler = new KernelScheduler();
        scheduler.LoggerFactory = Logging.LoggerFactory;

        // 2. Setup TTY
        TtyDiscipline? ttyDiag = null;
        FileStream? stdinStream = null;
        CancellationTokenSource? inputCts = null;
        Task? inputTask = null;
        PosixSignalRegistration? sigwinch = null;
        var isInteractive = useTty && !Console.IsInputRedirected;

        using var logSink = CreateContainerLogSink(logDriver, containerDir);
        var driver = new ConsoleTtyDriver(logSink);
        var broadcaster = new SchedulerSignalBroadcaster(scheduler);
        ttyDiag = new TtyDiscipline(driver, broadcaster, Logging.CreateLogger<TtyDiscipline>());
        driver.BindTty(ttyDiag);
        scheduler.Tty = ttyDiag;

        if (isInteractive)
        {
            stdinStream = new FileStream(new SafeFileHandle(0, true), FileAccess.Read);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var res = Fiberish.Core.VFS.TTY.MacOSTermios.EnableRawMode(0);
                if (res != 0) Console.Error.WriteLine($"Warning: Failed to enable raw mode: {res}");
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
            if (!Directory.Exists(rootfsPath))
            {
                Console.Error.WriteLine($"[FiberPod Error] RootFS path not found: {rootfsPath}");
                eventStore.Append(new ContainerEvent(DateTimeOffset.UtcNow, "container-exit", containerId, image, 1,
                    "rootfs not found"));
                return 1;
            }

            // 3. Bootstrap bare runtime, and let FiberPod assemble the rootfs.
            var runtime = KernelRuntime.BootstrapBare(strace, ttyDiag);
            if (useOverlay)
            {
                if (!TryCreateLayerLower(rootfsPath, out var layerLowerSb, out var layerProvider, out var layerError))
                {
                    Console.Error.WriteLine($"[FiberPod Error] {layerError}");
                    eventStore.Append(new ContainerEvent(DateTimeOffset.UtcNow, "container-exit", containerId, image, 1,
                        layerError));
                    return 1;
                }

                using var _ = layerProvider;
                var silkUpperStore = Path.Combine(containerDir, "silk-upper");
                Directory.CreateDirectory(silkUpperStore);
                var silkType = FileSystemRegistry.Get("silkfs")
                               ?? throw new InvalidOperationException("silkfs is not registered");
                var overlayType = FileSystemRegistry.Get("overlay")
                                  ?? throw new InvalidOperationException("overlay is not registered");

                var upperSb = silkType.FileSystem.ReadSuper(silkType, 0, silkUpperStore, null);
                var overlaySb = overlayType.FileSystem.ReadSuper(overlayType, 0, "root_overlay", new OverlayMountOptions
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
                var hostSb = hostType.FileSystem.ReadSuper(hostType, 0, rootfsPath, null);
                runtime.Syscalls.MountRoot(hostSb, new SyscallManager.RootMountOptions
                {
                    Source = rootfsPath,
                    FsType = "hostfs",
                    Options = "rw,relatime"
                });
                runtime.Syscalls.MountStandardDev(ttyDiag);
                runtime.Syscalls.MountStandardProc();
                runtime.Syscalls.MountStandardShm();
            }

            // 4. Mount Volumes
            foreach (var vol in volumes)
            {
                var parts = vol.Split(':');
                if (parts.Length < 2)
                {
                    Logger.LogWarning("Invalid volume format: {Volume}. Expected /host/path:/guest/path[:ro]", vol);
                    continue;
                }
                var hostPath = parts[0];
                var guestPath = parts[1];
                var readOnly = parts.Length > 2 && parts[2] == "ro";

                if (!Directory.Exists(hostPath) && !File.Exists(hostPath))
                {
                    Logger.LogWarning("Host path does not exist, skipping mount: {HostPath}", hostPath);
                    continue;
                }

                // Follow symlinks on host (e.g. /var -> /private/var on macOS)
                // Otherwise guest processes might fail to resolve host paths correctly.
                var hostInfo = new DirectoryInfo(hostPath);
                if (hostInfo.LinkTarget != null)
                {
                    var resolved = hostInfo.ResolveLinkTarget(true);
                    if (resolved != null)
                    {
                        hostPath = resolved.FullName;
                    }
                }

                Logger.LogInformation("Mounting {HostPath} at {GuestPath} (ro: {ReadOnly})", hostPath, guestPath,
                    readOnly);
                runtime.Syscalls.MountHostfs(hostPath, guestPath, readOnly);
            }

            // 4.5. Mount DNS Configuration (/etc/resolv.conf)
            try
            {
                var resolvConf = BuildResolvConfContent(dnsServers);
                Logger.LogInformation("Mounting generated DNS configuration at /etc/resolv.conf via detached tmpfs");
                runtime.Syscalls.MountDetachedTmpfsFile(
                    "/etc/resolv.conf",
                    "resolv.conf",
                    Encoding.UTF8.GetBytes(resolvConf),
                    readOnly: true);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to mount DNS configuration. Network resolution may not work.");
            }

            var actualExe = string.IsNullOrEmpty(exe) ? "/bin/sh" : exe;
            var fullArgs = new[] { actualExe }.Concat(exeArgs).ToArray();
            
            // Build strictly isolated environment
            var finalEnvs = new List<string>
            {
                "PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
                "HOME=/root",
                "TERM=xterm",
                "USER=root"
            };
            
            // Add user provided envs. This replaces duplicates if any.
            foreach (var env in guestEnvs)
            {
                finalEnvs.Add(env);
            }

            var (loc, guestPathResolved) = runtime.Syscalls.ResolvePath(actualExe, true);
            if (!loc.IsValid) throw new FileNotFoundException($"Could not find executable in VFS: {actualExe}");

            var mainTask = ProcessFactory.CreateInitProcess(runtime, loc.Dentry!, guestPathResolved, fullArgs, finalEnvs.ToArray(),
                scheduler, ttyDiag, loc.Mount!);
            eventStore.Append(new ContainerEvent(DateTimeOffset.UtcNow, "container-start", containerId, image));

            // 5. Run Scheduler
            scheduler.Run();

            // Cleanup temp rootfs happens in the loop above

            eventStore.Append(new ContainerEvent(DateTimeOffset.UtcNow, "container-exit", containerId, image,
                mainTask.ExitStatus));
            return mainTask.ExitStatus;
        }
        catch (Exception ex)
        {
            Logger.LogCritical(ex, "Critical Error during container emulation");
            Console.Error.WriteLine($"[FiberPod] Error: {ex.Message}");
            eventStore.Append(new ContainerEvent(DateTimeOffset.UtcNow, "container-exit", containerId, image, 1,
                ex.Message));
            return 1;
        }
        finally
        {
            // Give pending I/O tasks a little time to flush, especially in non-interactive tests
            try
            {
                Task.Delay(50).Wait();
            }
            catch
            {
            }

            Console.Out.Flush();
            Console.Error.Flush();

            stdinStream?.Dispose();

            if (inputCts != null)
            {
                inputCts.Cancel();
                if (inputTask != null)
                {
                    try
                    {
                        Task.WhenAny(inputTask, Task.Delay(100)).Wait();
                    }
                    catch
                    {
                    }
                }

                inputCts.Dispose();
            }

            sigwinch?.Dispose();

            if (isInteractive && RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Fiberish.Core.VFS.TTY.MacOSTermios.DisableRawMode(1);
            }
        }
    }

    private static bool TryCreateLayerLower(string ociStoreDir, out SuperBlock? lowerSb, out IDisposable? provider,
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
            storedImage = JsonSerializer.Deserialize<OciStoredImage>(File.ReadAllText(imagePath));
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
        foreach (var layer in storedImage.Layers)
        {
            if (!File.Exists(layer.IndexPath))
            {
                error = $"missing layer index file: {layer.IndexPath}";
                return false;
            }

            if (!File.Exists(layer.BlobPath))
            {
                error = $"missing layer blob file: {layer.BlobPath}";
                return false;
            }

            try
            {
                var entries = JsonSerializer.Deserialize<List<LayerIndexEntry>>(File.ReadAllText(layer.IndexPath));
                if (entries == null)
                {
                    error = $"invalid layer index JSON: {layer.IndexPath}";
                    return false;
                }

                layerIndexes.Add(entries);
            }
            catch (Exception ex)
            {
                error = $"failed to parse layer index '{layer.IndexPath}': {ex.Message}";
                return false;
            }

            digestToBlobPath[layer.Digest] = layer.BlobPath;
        }

        var merged = MergeLayerIndexes(layerIndexes);
        var layerType = FileSystemRegistry.Get("layerfs");
        if (layerType == null)
        {
            error = "layerfs is not registered";
            return false;
        }

        provider = new TarBlobLayerContentProvider(digestToBlobPath);
        lowerSb = layerType.FileSystem.ReadSuper(layerType, 0, "layer-lower",
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
        {
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

    private static string BuildResolvConfContent(string[] dnsServers)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Generated by FiberPod");

        if (dnsServers.Length > 0)
        {
            foreach (var dns in dnsServers)
            {
                sb.AppendLine($"nameserver {dns}");
            }
        }
        else
        {
            // Fallback to host DNS. On unix, read /etc/resolv.conf.
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
                                // If the host is using a local loopback resolver (like systemd-resolved),
                                // the guest cannot reach it. Fallback to public DNS.
                                if (ip.StartsWith("127."))
                                {
                                    Logger.LogInformation(
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
                            // Copy search domains, options, etc. verbatim
                            sb.AppendLine(line);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Failed to read host /etc/resolv.conf");
                }
            }

            if (definedNameservers == 0)
            {
                // Absolute fallback if everything else fails
                sb.AppendLine("nameserver 8.8.8.8");
            }
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

            if (entry.InlineData != null)
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
                bytesRead = stream.Read(buffer);
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

    private static IContainerLogSink CreateContainerLogSink(ContainerLogDriver driver, string containerDir)
    {
        return driver switch
        {
            ContainerLogDriver.JsonFile => new JsonFileContainerLogSink(Path.Combine(containerDir, "ctr.log")),
            ContainerLogDriver.None => new NoneContainerLogSink(),
            _ => new NoneContainerLogSink()
        };
    }

    private class ConsoleTtyDriver : ITtyDriver
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

        public void BindTty(TtyDiscipline tty) => _tty = tty;
    }

    private class SchedulerSignalBroadcaster : ISignalBroadcaster
    {
        private readonly KernelScheduler _scheduler;
        public SchedulerSignalBroadcaster(KernelScheduler scheduler) => _scheduler = scheduler;
        public void SignalProcessGroup(int pgid, int signal) => _scheduler.SignalProcessGroup(pgid, signal);

        public void SignalForegroundTask(int signal)
        {
        }
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
