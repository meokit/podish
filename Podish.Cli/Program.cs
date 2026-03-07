using System.CommandLine;
using System.Text.Json;
using Fiberish.Core.Net;
using Fiberish.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Podish.Core;

namespace Podish.Cli;

internal class Program
{
    private static ILogger Logger = null!;
    private static ILoggerFactory ProgramLoggerFactory = NullLoggerFactory.Instance;

    private static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("Podish.Cli - Podman-like CLI for x86emu");

        // Global Options
        var logLevelOption = new Option<string>(
            new[] { "--log-level", "-l" },
            () => "warn",
            "Log messages above specified level: trace, debug, info, warn, error, fatal, panic");
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
        var containerNameOption = new Option<string?>(
            new[] { "--name" },
            "Assign a name to the container");
        var autoRemoveOption = new Option<bool>(
            new[] { "--rm" },
            "Automatically remove the container when it exits");
        var hostnameOption = new Option<string?>(
            new[] { "--hostname", "-h" },
            "Set the container hostname (defaults to --name, then short container id)");
        var networkOption = new Option<string>(
            new[] { "--network" },
            () => "host",
            "Network mode (host|private)");
        var publishOption = new Option<string[]>(
            new[] { "--publish", "-p" },
            "Publish a container's port(s) to the host (e.g. hostPort:containerPort)")
        {
            AllowMultipleArgumentsPerToken = false
        };
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
        runCommand.AddOption(containerNameOption);
        runCommand.AddOption(autoRemoveOption);
        runCommand.AddOption(hostnameOption);
        runCommand.AddOption(networkOption);
        runCommand.AddOption(publishOption);
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
            var containerName = context.ParseResult.GetValueForOption(containerNameOption);
            var autoRemove = context.ParseResult.GetValueForOption(autoRemoveOption);
            var explicitHostname = context.ParseResult.GetValueForOption(hostnameOption);
            var networkRaw = context.ParseResult.GetValueForOption(networkOption) ?? "host";
            var publishRaw = context.ParseResult.GetValueForOption(publishOption) ?? Array.Empty<string>();
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

            if (!TryParsePodmanLogLevel(logLevelRaw, out var logLevel))
            {
                Console.Error.WriteLine(
                    $"[Podish.Cli] invalid --log-level value: {logLevelRaw}. Use trace|debug|info|warn|error|fatal|panic");
                context.ExitCode = 125;
                return;
            }

            logFile = ResolveEngineLogFile(logLevel, logFile, logsDir);

            if (!ContainerLogDriverParser.TryParse(containerLogDriverRaw, out var containerLogDriver))
            {
                Console.Error.WriteLine(
                    $"[Podish.Cli] invalid --log-driver value: {containerLogDriverRaw}. Use json-file|none");
                context.ExitCode = 125;
                return;
            }

            if (!TryParseNetworkMode(networkRaw, out var networkMode))
            {
                Console.Error.WriteLine($"[Podish.Cli] invalid --network value: {networkRaw}. Use host|private");
                context.ExitCode = 125;
                return;
            }

            if (networkMode == NetworkMode.Host && publishRaw.Length > 0)
            {
                Console.Error.WriteLine("[Podish.Cli] Error: --publish/-p is not supported in host network mode.");
                context.ExitCode = 125;
                return;
            }

            var publishedPorts = new List<PublishedPortSpec>();
            foreach (var p in publishRaw)
            {
                if (!TryParsePublishedPort(p, out var pSpec))
                {
                    Console.Error.WriteLine($"[Podish.Cli] invalid published port format: {p}. Expected hostPort:containerPort");
                    context.ExitCode = 125;
                    return;
                }
                publishedPorts.Add(pSpec);
            }

            SetupLogging(logLevel, logFile);
            using var _logScope = Logging.BeginScope(ProgramLoggerFactory);

            if (!PodishContainerMetadataStore.IsValidName(containerName))
            {
                Console.Error.WriteLine("[Podish.Cli] invalid --name. Allowed: [a-zA-Z0-9][a-zA-Z0-9_.-]*");
                context.ExitCode = 125;
                return;
            }

            var existing = PodishContainerMetadataStore.ReadAll(containersDir);
            if (!string.IsNullOrWhiteSpace(containerName) &&
                existing.Any(x => string.Equals(x.Name, containerName, StringComparison.Ordinal)))
            {
                Console.Error.WriteLine($"[Podish.Cli] container name already exists: {containerName}");
                context.ExitCode = 125;
                return;
            }

            var containerId = Guid.NewGuid().ToString("N")[..12];
            var hostname = ResolveContainerHostname(explicitHostname, containerName, containerId);
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

            var now = DateTimeOffset.UtcNow;
            var spec = new PodishRunSpec
            {
                Name = containerName,
                Hostname = hostname,
                AutoRemove = autoRemove,
                NetworkMode = networkMode,
                Image = image,
                Rootfs = rootfs,
                Exe = exe,
                ExeArgs = exeArgs,
                Volumes = volumes,
                Env = guestEnvs,
                Dns = dnsServers,
                Interactive = interactive,
                Tty = tty,
                Strace = strace,
                LogDriver = containerLogDriver.ToCliValue(),
                PublishedPorts = publishedPorts
            };
            var metadata = new PodishContainerMetadata
            {
                ContainerId = containerId,
                Name = containerName,
                Image = imageRef,
                State = "created",
                ExitCode = null,
                HasTerminal = interactive && tty,
                Running = false,
                CreatedAt = now,
                UpdatedAt = now,
                Spec = spec
            };
            PodishContainerMetadataStore.Write(containersDir, metadata);
            eventStore.Append(new ContainerEvent(DateTimeOffset.UtcNow, "container-create", containerId, imageRef));
            metadata.State = "running";
            metadata.Running = true;
            metadata.UpdatedAt = DateTimeOffset.UtcNow;
            PodishContainerMetadataStore.Write(containersDir, metadata);

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
                containerName,
                hostname,
                networkMode,
                imageRef,
                containerDir,
                containerLogDriver,
                eventStore,
                publishedPorts);
            metadata.State = "exited";
            metadata.Running = false;
            metadata.ExitCode = exitCode;
            metadata.UpdatedAt = DateTimeOffset.UtcNow;
            if (autoRemove)
            {
                eventStore.Append(new ContainerEvent(DateTimeOffset.UtcNow, "container-remove", containerId, imageRef));
                PodishContainerMetadataStore.Delete(containersDir, containerId);
            }
            else
            {
                PodishContainerMetadataStore.Write(containersDir, metadata);
            }
            context.ExitCode = exitCode;
        });

        // --- Start Command ---
        var startCommand = new Command("start", "Start an existing container by name or ID");
        var startContainerArgument = new Argument<string>("container", "Container name or ID");
        startCommand.AddArgument(startContainerArgument);
        startCommand.SetHandler(async (context) =>
        {
            var containerId = context.ParseResult.GetValueForArgument(startContainerArgument);
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

            if (!TryParsePodmanLogLevel(logLevelRaw, out var logLevel))
            {
                Console.Error.WriteLine(
                    $"[Podish.Cli] invalid --log-level value: {logLevelRaw}. Use debug|info|warn|error|fatal|panic");
                context.ExitCode = 125;
                return;
            }

            logFile = ResolveEngineLogFile(logLevel, logFile, logsDir);

            SetupLogging(logLevel, logFile);
            using var _logScope = Logging.BeginScope(ProgramLoggerFactory);

            var metadata = PodishContainerMetadataStore.Resolve(containersDir, containerId);
            if (metadata == null)
            {
                Console.Error.WriteLine($"[Podish.Cli] container not found: {containerId}");
                context.ExitCode = 125;
                return;
            }
            containerId = metadata.ContainerId;
            var containerDir = Path.Combine(containersDir, containerId);

            if (metadata?.Spec == null)
            {
                Console.Error.WriteLine("[Podish.Cli] container metadata is missing run spec");
                context.ExitCode = 125;
                return;
            }

            if (!ContainerLogDriverParser.TryParse(metadata.Spec.LogDriver, out var containerLogDriver))
            {
                Console.Error.WriteLine(
                    $"[Podish.Cli] invalid log driver in container metadata: {metadata.Spec.LogDriver}");
                context.ExitCode = 125;
                return;
            }

            var spec = metadata.Spec;
            var useRootfs = !string.IsNullOrWhiteSpace(spec.Rootfs);
            var imageRef = metadata.Image ?? spec.Image ?? spec.Rootfs ?? "<unknown>";
            var rootfsPath = spec.Rootfs ?? spec.Image ?? string.Empty;
            var safeImageName = imageRef.Replace("/", "_").Replace(":", "_");
            var ociStoreDir = Path.Combine(ociStoreImagesDir, safeImageName);
            var eventStore = new ContainerEventStore(Path.Combine(fiberpodDir, "events.jsonl"));

            if (!useRootfs)
            {
                if (string.IsNullOrWhiteSpace(spec.Image))
                {
                    Console.Error.WriteLine("[Podish.Cli] invalid container metadata: image is required");
                    context.ExitCode = 125;
                    return;
                }

                if (!Directory.Exists(rootfsPath) && Directory.Exists(ociStoreDir))
                {
                    rootfsPath = ociStoreDir;
                }
                else if (!Directory.Exists(rootfsPath))
                {
                    Console.Error.WriteLine($"[Podish.Cli] Unable to find image '{spec.Image}' locally");
                    Console.Error.WriteLine($"[Podish.Cli] Trying to pull {spec.Image}...");
                    var pullService = new OciPullService(Logger);
                    try
                    {
                        await pullService.PullAndStoreImageAsync(spec.Image, ociStoreDir);
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
            else if (!Directory.Exists(rootfsPath))
            {
                Console.Error.WriteLine($"[Podish.Cli Error] --rootfs path not found: {rootfsPath}");
                context.ExitCode = 1;
                return;
            }

            Logger.LogInformation("Starting existing container {ContainerId}", containerId);
            metadata.State = "running";
            metadata.Running = true;
            metadata.ExitCode = null;
            metadata.UpdatedAt = DateTimeOffset.UtcNow;
            PodishContainerMetadataStore.Write(containersDir, metadata);
            var exitCode = await RunContainer(
                rootfsPath,
                spec.Exe ?? string.Empty,
                spec.ExeArgs,
                spec.Volumes,
                spec.Env,
                spec.Dns,
                spec.Interactive && spec.Tty,
                spec.Strace,
                !useRootfs,
                containersDir,
                containerId,
                metadata.Name,
                spec.Hostname ?? ResolveContainerHostname(null, metadata.Name, containerId),
                spec.NetworkMode,
                imageRef,
                containerDir,
                containerLogDriver,
                eventStore,
                spec.PublishedPorts);
            metadata.State = "exited";
            metadata.Running = false;
            metadata.ExitCode = exitCode;
            metadata.UpdatedAt = DateTimeOffset.UtcNow;
            PodishContainerMetadataStore.Write(containersDir, metadata);
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

            if (!TryParsePodmanLogLevel(logLevelRaw, out var logLevel))
            {
                Console.Error.WriteLine(
                    $"[Podish.Cli] invalid --log-level value: {logLevelRaw}. Use debug|info|warn|error|fatal|panic");
                context.ExitCode = 125;
                return;
            }

            logFile = ResolveEngineLogFile(logLevel, logFile, logsDir);

            SetupLogging(logLevel, logFile);
            using var _logScope = Logging.BeginScope(ProgramLoggerFactory);

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
            if (!TryParsePodmanLogLevel(logLevelRaw, out var logLevel))
            {
                Console.Error.WriteLine(
                    $"[Podish.Cli] invalid --log-level value: {logLevelRaw}. Use debug|info|warn|error|fatal|panic");
                context.ExitCode = 125;
                return;
            }

            logFile = ResolveEngineLogFile(logLevel, logFile, logsDir);

            SetupLogging(logLevel, logFile);
            using var _logScope = Logging.BeginScope(ProgramLoggerFactory);
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
            if (!TryParsePodmanLogLevel(logLevelRaw, out var logLevel))
            {
                Console.Error.WriteLine(
                    $"[Podish.Cli] invalid --log-level value: {logLevelRaw}. Use debug|info|warn|error|fatal|panic");
                context.ExitCode = 125;
                return;
            }

            logFile = ResolveEngineLogFile(logLevel, logFile, logsDir);

            SetupLogging(logLevel, logFile);
            using var _logScope = Logging.BeginScope(ProgramLoggerFactory);
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
            if (!TryParsePodmanLogLevel(logLevelRaw, out var logLevel))
            {
                Console.Error.WriteLine(
                    $"[Podish.Cli] invalid --log-level value: {logLevelRaw}. Use debug|info|warn|error|fatal|panic");
                context.ExitCode = 125;
                return;
            }

            logFile = ResolveEngineLogFile(logLevel, logFile, logsDir);

            SetupLogging(logLevel, logFile);
            using var _logScope = Logging.BeginScope(ProgramLoggerFactory);
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
            if (!TryParsePodmanLogLevel(logLevelRaw, out var logLevel))
            {
                Console.Error.WriteLine(
                    $"[Podish.Cli] invalid --log-level value: {logLevelRaw}. Use debug|info|warn|error|fatal|panic");
                context.ExitCode = 125;
                return;
            }

            logFile = ResolveEngineLogFile(logLevel, logFile, logsDir);

            SetupLogging(logLevel, logFile);
            using var _logScope = Logging.BeginScope(ProgramLoggerFactory);
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

        // --- PS Command ---
        var psCommand = new Command("ps", "List containers");
        var psAllOption = new Option<bool>(new[] { "--all", "-a" }, "Show all containers (default: only running)");
        var psFormatOption = new Option<string>(
            new[] { "--format" },
            () => "table",
            "Output format (table|json)");
        psCommand.AddOption(psAllOption);
        psCommand.AddOption(psFormatOption);
        psCommand.SetHandler((context) =>
        {
            var showAll = context.ParseResult.GetValueForOption(psAllOption);
            var format = context.ParseResult.GetValueForOption(psFormatOption) ?? "table";
            var fiberpodDir = Path.Combine(Directory.GetCurrentDirectory(), ".fiberpod");
            var containersDir = Path.Combine(fiberpodDir, "containers");
            var rows = PodishContainerMetadataStore.ReadAll(containersDir)
                .Where(x => showAll || string.Equals(x.State, "running", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.UpdatedAt)
                .ToList();

            if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(JsonSerializer.Serialize(rows));
                return;
            }

            if (!string.Equals(format, "table", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"[Podish.Cli] invalid --format value: {format}. Use table|json");
                context.ExitCode = 125;
                return;
            }

            Console.WriteLine("CONTAINER ID   NAME                 IMAGE                           STATE     EXIT");
            foreach (var c in rows)
            {
                var id = (c.ContainerId ?? string.Empty).PadRight(14).Substring(0, 14);
                var name = (c.Name ?? string.Empty).PadRight(20).Substring(0, 20);
                var image = (c.Image ?? string.Empty).PadRight(32).Substring(0, 32);
                var state = (c.State ?? "unknown").PadRight(9).Substring(0, 9);
                var exit = c.ExitCode?.ToString() ?? "";
                Console.WriteLine($"{id} {name} {image} {state} {exit}");
            }
        });

        // --- Rename Command ---
        var renameCommand = new Command("rename", "Rename a container");
        var renameOldArgument = new Argument<string>("container", "Container name or ID");
        var renameNewArgument = new Argument<string>("new-name", "New container name");
        renameCommand.AddArgument(renameOldArgument);
        renameCommand.AddArgument(renameNewArgument);
        renameCommand.SetHandler((context) =>
        {
            var oldQuery = context.ParseResult.GetValueForArgument(renameOldArgument);
            var newName = context.ParseResult.GetValueForArgument(renameNewArgument);
            var fiberpodDir = Path.Combine(Directory.GetCurrentDirectory(), ".fiberpod");
            var containersDir = Path.Combine(fiberpodDir, "containers");

            if (!PodishContainerMetadataStore.IsValidName(newName))
            {
                Console.Error.WriteLine("[Podish.Cli] invalid new name. Allowed: [a-zA-Z0-9][a-zA-Z0-9_.-]*");
                context.ExitCode = 125;
                return;
            }

            var all = PodishContainerMetadataStore.ReadAll(containersDir);
            var existing = all.FirstOrDefault(x =>
                string.Equals(x.Name, newName, StringComparison.Ordinal) ||
                string.Equals(x.ContainerId, newName, StringComparison.Ordinal));
            if (existing != null)
            {
                Console.Error.WriteLine($"[Podish.Cli] name already exists: {newName}");
                context.ExitCode = 125;
                return;
            }

            var target = PodishContainerMetadataStore.Resolve(containersDir, oldQuery);
            if (target == null)
            {
                Console.Error.WriteLine($"[Podish.Cli] container not found: {oldQuery}");
                context.ExitCode = 125;
                return;
            }

            target.Name = newName;
            target.UpdatedAt = DateTimeOffset.UtcNow;
            PodishContainerMetadataStore.Write(containersDir, target);
            Console.WriteLine($"{target.ContainerId} {target.Name}");
        });

        // --- RM Command ---
        var rmCommand = new Command("rm", "Remove container(s) by name or ID");
        var rmContainersArgument = new Argument<string[]>("containers", "Container names or IDs")
        {
            Arity = ArgumentArity.OneOrMore
        };
        var rmForceOption = new Option<bool>(new[] { "--force", "-f" }, "Force remove running container metadata");
        rmCommand.AddArgument(rmContainersArgument);
        rmCommand.AddOption(rmForceOption);
        rmCommand.SetHandler((context) =>
        {
            var targets = context.ParseResult.GetValueForArgument(rmContainersArgument) ?? Array.Empty<string>();
            var force = context.ParseResult.GetValueForOption(rmForceOption);
            var fiberpodDir = Path.Combine(Directory.GetCurrentDirectory(), ".fiberpod");
            var containersDir = Path.Combine(fiberpodDir, "containers");
            var eventStore = new ContainerEventStore(Path.Combine(fiberpodDir, "events.jsonl"));

            foreach (var query in targets)
            {
                var metadata = PodishContainerMetadataStore.Resolve(containersDir, query);
                if (metadata == null)
                {
                    Console.Error.WriteLine($"[Podish.Cli] container not found: {query}");
                    context.ExitCode = 1;
                    continue;
                }

                if (!force && string.Equals(metadata.State, "running", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine(
                        $"[Podish.Cli] refusing to remove running container {query}; use rm -f");
                    context.ExitCode = 1;
                    continue;
                }

                PodishContainerMetadataStore.Delete(containersDir, metadata.ContainerId);

                eventStore.Append(new ContainerEvent(
                    DateTimeOffset.UtcNow,
                    "container-remove",
                    metadata.ContainerId,
                    metadata.Image ?? string.Empty));
                Console.WriteLine(metadata.ContainerId);
            }
        });

        // --- Images Command ---
        var imagesCommand = new Command("images", "List local images");
        imagesCommand.SetHandler((context) =>
        {
            var fiberpodDir = Path.Combine(Directory.GetCurrentDirectory(), ".fiberpod");
            var ociStoreImagesDir = Path.Combine(fiberpodDir, "oci", "images");
            if (!Directory.Exists(ociStoreImagesDir))
                return;

            Console.WriteLine("REPOSITORY                    TAG                 DIGEST                             LAYERS");
            foreach (var dir in Directory.GetDirectories(ociStoreImagesDir).OrderBy(x => x, StringComparer.Ordinal))
            {
                var imagePath = Path.Combine(dir, "image.json");
                if (!File.Exists(imagePath))
                    continue;

                try
                {
                    var image = JsonSerializer.Deserialize<OciStoredImage>(File.ReadAllText(imagePath),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (image == null)
                        continue;

                    var (repo, tag) = SplitImageReference(image.ImageReference);
                    var digest = image.ManifestDigest ?? string.Empty;
                    Console.WriteLine(
                        $"{repo.PadRight(29).Substring(0, 29)} {tag.PadRight(19).Substring(0, 19)} {digest.PadRight(34).Substring(0, 34)} {image.Layers.Count}");
                }
                catch
                {
                    // ignore malformed metadata
                }
            }
        });

        // --- Image Command ---
        var imageCommand = new Command("image", "Manage images");
        var imageRmCommand = new Command("rm", "Remove image(s)");
        var imageRmTargets = new Argument<string[]>("images", "Image references")
        {
            Arity = ArgumentArity.OneOrMore
        };
        imageRmCommand.AddArgument(imageRmTargets);
        imageRmCommand.SetHandler((context) =>
        {
            var targets = context.ParseResult.GetValueForArgument(imageRmTargets) ?? Array.Empty<string>();
            var fiberpodDir = Path.Combine(Directory.GetCurrentDirectory(), ".fiberpod");
            var ociStoreImagesDir = Path.Combine(fiberpodDir, "oci", "images");
            var imagesDir = Path.Combine(fiberpodDir, "images");
            foreach (var target in targets)
            {
                var safe = target.Replace("/", "_").Replace(":", "_");
                var direct = Path.Combine(ociStoreImagesDir, safe);
                var matched = Directory.Exists(direct)
                    ? direct
                    : FindImageDirectoryByReference(ociStoreImagesDir, target);
                if (matched == null)
                {
                    Console.Error.WriteLine($"[Podish.Cli] image not found: {target}");
                    context.ExitCode = 1;
                    continue;
                }

                Directory.Delete(matched, true);
                var extractedDir = Path.Combine(imagesDir, Path.GetFileName(matched));
                if (Directory.Exists(extractedDir))
                    Directory.Delete(extractedDir, true);
                Console.WriteLine(target);
            }
        });
        imageCommand.AddCommand(imageRmCommand);

        // --- Logs Command ---
        var logsCommand = new Command("logs", "Fetch container logs");
        var logsContainerArgument = new Argument<string>("container", "Container name or ID");
        var logsTimestampsOption = new Option<bool>(new[] { "--timestamps" }, "Show timestamps");
        logsCommand.AddArgument(logsContainerArgument);
        logsCommand.AddOption(logsTimestampsOption);
        logsCommand.SetHandler((context) =>
        {
            var containerQuery = context.ParseResult.GetValueForArgument(logsContainerArgument);
            var showTimestamps = context.ParseResult.GetValueForOption(logsTimestampsOption);

            var fiberpodDir = Path.Combine(Directory.GetCurrentDirectory(), ".fiberpod");
            var containersDir = Path.Combine(fiberpodDir, "containers");
            var container = PodishContainerMetadataStore.Resolve(containersDir, containerQuery);
            var containerId = container?.ContainerId ?? containerQuery;
            var logPath = Path.Combine(fiberpodDir, "containers", containerId, "ctr.log");
            if (!File.Exists(logPath))
            {
                Console.Error.WriteLine($"[Podish.Cli] log file not found for container {containerQuery}");
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
        rootCommand.AddCommand(startCommand);
        rootCommand.AddCommand(psCommand);
        rootCommand.AddCommand(rmCommand);
        rootCommand.AddCommand(renameCommand);
        rootCommand.AddCommand(imagesCommand);
        rootCommand.AddCommand(imageCommand);
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
        ProgramLoggerFactory = LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            builder.SetMinimumLevel(logLevel);
            if (!string.IsNullOrEmpty(logFile))
            {
                // Write only to file; never emit engine logs to console.
                builder.AddProvider(new FileLoggerProvider(logFile));
            }
        });
        Logger = ProgramLoggerFactory.CreateLogger<Program>();
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
            "--rootfs",
            "--name",
            "--hostname",
            "--network"
        };
        var optionsNoValue = new HashSet<string>(StringComparer.Ordinal)
        {
            "-i",
            "--interactive",
            "-t",
            "--tty",
            "-s",
            "--strace",
            "--rm"
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

    private static (string Repository, string Tag) SplitImageReference(string imageRef)
    {
        if (string.IsNullOrWhiteSpace(imageRef))
            return ("<none>", "<none>");
        var idx = imageRef.LastIndexOf(':');
        if (idx <= 0 || idx == imageRef.Length - 1 || imageRef[(idx - 1)..].Contains('/'))
            return (imageRef, "latest");
        return (imageRef[..idx], imageRef[(idx + 1)..]);
    }

    private static string? FindImageDirectoryByReference(string ociStoreImagesDir, string imageRef)
    {
        if (!Directory.Exists(ociStoreImagesDir))
            return null;

        foreach (var dir in Directory.GetDirectories(ociStoreImagesDir))
        {
            var imagePath = Path.Combine(dir, "image.json");
            if (!File.Exists(imagePath))
                continue;

            try
            {
                var image = JsonSerializer.Deserialize<OciStoredImage>(File.ReadAllText(imagePath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (image != null && string.Equals(image.ImageReference, imageRef, StringComparison.Ordinal))
                    return dir;
            }
            catch
            {
                // ignore malformed metadata
            }
        }

        return null;
    }

    private static bool TryParsePodmanLogLevel(string raw, out LogLevel level)
    {
        return PodishContext.TryParsePodmanLogLevel(raw, out level);
    }

    private static bool TryParseNetworkMode(string raw, out NetworkMode mode)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "host":
                mode = NetworkMode.Host;
                return true;
            case "private":
                mode = NetworkMode.Private;
                return true;
            default:
                mode = default;
                return false;
        }
    }

    private static string? ResolveEngineLogFile(LogLevel logLevel, string? explicitLogFile, string logsDir)
    {
        if (!string.IsNullOrWhiteSpace(explicitLogFile))
            return explicitLogFile;
        if (logLevel > LogLevel.Debug)
            return null;

        Directory.CreateDirectory(logsDir);
        CleanupOldAutoEngineLogs(logsDir, keep: 20);
        return Path.Combine(logsDir, $"engine_{DateTime.Now:yyyyMMdd_HHmmss}_{Environment.ProcessId}.log");
    }

    private static bool TryParsePublishedPort(string raw, out PublishedPortSpec spec)
    {
        spec = null!;
        var parts = raw.Split(':');
        if (parts.Length != 2) return false;

        if (!int.TryParse(parts[0], out var hostPort) || hostPort < 1 || hostPort > 65535) return false;
        if (!int.TryParse(parts[1], out var containerPort) || containerPort < 1 || containerPort > 65535) return false;

        spec = new PublishedPortSpec
        {
            HostPort = hostPort,
            ContainerPort = containerPort,
            Protocol = TransportProtocol.Tcp
        };
        return true;
    }

    private static void CleanupOldAutoEngineLogs(string logsDir, int keep)
    {
        try
        {
            var oldLogs = new DirectoryInfo(logsDir)
                .GetFiles("engine_*.log", SearchOption.TopDirectoryOnly)
                .OrderByDescending(x => x.CreationTimeUtc)
                .Skip(keep);

            foreach (var file in oldLogs)
                file.Delete();
        }
        catch
        {
        }
    }

    private static string ResolveContainerHostname(string? explicitHostname, string? containerName, string containerId)
    {
        if (!string.IsNullOrWhiteSpace(explicitHostname))
            return explicitHostname.Trim();
        if (!string.IsNullOrWhiteSpace(containerName))
            return containerName.Trim();
        return containerId;
    }

    private static async Task<int> RunContainer(string rootfsPath, string exe, string[] exeArgs, string[] volumes,
        string[] guestEnvs, string[] dnsServers, bool useTty, bool strace, bool useOverlay, string containersDir,
        string containerId, string? containerName, string hostname, NetworkMode networkMode, string image, string containerDir, ContainerLogDriver logDriver,
        ContainerEventStore eventStore, IReadOnlyList<PublishedPortSpec> publishedPorts)
    {
        using var _logScope = Logging.BeginScope(ProgramLoggerFactory);
        var service = new ContainerRuntimeService(Logger, ProgramLoggerFactory);
        return await service.RunAsync(new ContainerRunRequest
        {
            RootfsPath = rootfsPath,
            ContainerName = containerName,
            Exe = exe,
            ExeArgs = exeArgs,
            Volumes = volumes,
            GuestEnvs = guestEnvs,
            DnsServers = dnsServers,
            UseTty = useTty,
            Strace = strace,
            UseOverlay = useOverlay,
            ContainerId = containerId,
            Hostname = hostname,
            NetworkMode = networkMode,
            Image = image,
            ContainerDir = containerDir,
            LogDriver = logDriver,
            EventStore = eventStore,
            PublishedPorts = publishedPorts
        });
    }
}

internal static class ContainerLogDriverExtensions
{
    public static string ToCliValue(this ContainerLogDriver driver)
    {
        return driver == ContainerLogDriver.None ? "none" : "json-file";
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
