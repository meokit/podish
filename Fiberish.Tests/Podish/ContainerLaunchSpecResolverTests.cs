using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;
using Fiberish.VFS;
using Microsoft.Extensions.Logging.Abstractions;
using Podish.Core;
using Podish.Core.Native;
using Xunit;

namespace Fiberish.Tests.Podish;

public sealed class ContainerLaunchSpecResolverTests
{
    [Fact]
    public void ResolveEffectiveSpec_UsesOciEntrypointCmdEnvAndWorkingDir()
    {
        var root = TestWorkspace.CreateUniqueDirectory("podish-launch-resolve-");
        try
        {
            WriteStoredImageMetadata(
                root,
                configUser: "1000:1001",
                configEntrypoint: ["/bin/entry", "-f"],
                configCmd: ["echo", "hello"],
                configEnv: ["PATH=/custom/bin", "A=1", "B=base"],
                configWorkingDir: "work/./app/../run");

            var resolved = ContainerLaunchSpecResolver.ResolveEffectiveSpec(
                new PodishRunSpec
                {
                    Image = "test:image",
                    Env = ["B=override", "C=3"]
                },
                root,
                rootfsMode: false);

            Assert.Equal("/bin/entry", resolved.Exe);
            Assert.Equal<string[]>(["-f", "echo", "hello"], resolved.ExeArgs);
            Assert.Equal("1000:1001", resolved.User);
            Assert.Equal<string[]>(["PATH=/custom/bin", "A=1", "B=override", "C=3"], resolved.Env);
            Assert.Equal("/work/run", resolved.WorkingDir);
        }
        finally
        {
            TestWorkspace.DeleteDirectory(root);
        }
    }

    [Fact]
    public void ResolveEffectiveSpec_ExplicitCommandOverridesOciCommandButKeepsImageEnvAndWorkingDir()
    {
        var root = TestWorkspace.CreateUniqueDirectory("podish-launch-explicit-");
        try
        {
            WriteStoredImageMetadata(
                root,
                configUser: "1234",
                configEntrypoint: ["/bin/image-entry"],
                configCmd: ["image-arg"],
                configEnv: ["A=1", "B=base"],
                configWorkingDir: "/workspace");

            var resolved = ContainerLaunchSpecResolver.ResolveEffectiveSpec(
                new PodishRunSpec
                {
                    Image = "test:image",
                    Exe = "/bin/custom",
                    ExeArgs = ["--flag"],
                    User = "5678",
                    Env = ["B=override"]
                },
                root,
                rootfsMode: false);

            Assert.Equal("/bin/custom", resolved.Exe);
            Assert.Equal<string[]>(["--flag"], resolved.ExeArgs);
            Assert.Equal("5678", resolved.User);
            Assert.Equal<string[]>(["A=1", "B=override"], resolved.Env);
            Assert.Equal("/workspace", resolved.WorkingDir);
        }
        finally
        {
            TestWorkspace.DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task RunAsync_ImageCmdUsesPathSearchAndReturnsZero()
    {
        var root = TestWorkspace.CreateUniqueDirectory("podish-launch-run-");
        var imageStoreDir = Path.Combine(root, "image-store");

        try
        {
            CreateExecutableImageStore(
                imageStoreDir,
                "/bin/hello_static",
                TestWorkspace.ResolveHelloStaticPath(),
                configCmd: ["hello_static"]);

            using var context = new PodishContext(new PodishContextOptions
            {
                WorkDir = root,
                LogLevel = "error",
                LogFile = Path.Combine(root, "podish.log")
            });

            var result = await context.RunAsync(new PodishRunSpec
            {
                Image = imageStoreDir,
                Interactive = false,
                Tty = false,
                LogDriver = "none"
            });

            Assert.Equal(0, result.ExitCode);
        }
        finally
        {
            TestWorkspace.DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task RunAsync_MissingWorkingDirectoryReturns125()
    {
        var root = TestWorkspace.CreateUniqueDirectory("podish-launch-workdir-");
        var imageStoreDir = Path.Combine(root, "image-store");

        try
        {
            CreateExecutableImageStore(
                imageStoreDir,
                "/bin/hello_static",
                TestWorkspace.ResolveHelloStaticPath(),
                configCmd: ["hello_static"],
                configWorkingDir: "/missing",
                createWorkingDirectory: false);

            using var context = new PodishContext(new PodishContextOptions
            {
                WorkDir = root,
                LogLevel = "error",
                LogFile = Path.Combine(root, "podish.log")
            });

            var result = await context.RunAsync(new PodishRunSpec
            {
                Image = imageStoreDir,
                Interactive = false,
                Tty = false,
                LogDriver = "none"
            });

            Assert.Equal(125, result.ExitCode);
        }
        finally
        {
            TestWorkspace.DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task NativeContainer_StartAsync_NormalizesLegacyImageBackedSpecAndPersistsIt()
    {
        var root = TestWorkspace.CreateUniqueDirectory("podish-launch-legacy-");
        var imageStoreDir = Path.Combine(root, "image-store");

        try
        {
            CreateExecutableImageStore(
                imageStoreDir,
                "/bin/hello_static",
                TestWorkspace.ResolveHelloStaticPath(),
                configUser: "1000",
                configCmd: ["hello_static"],
                configEnv: ["A=1"],
                configWorkingDir: "/workspace");

            using var ctx = new NativeContext
            {
                Context = new PodishContext(new PodishContextOptions
                {
                    WorkDir = root,
                    LogLevel = "error",
                    LogFile = Path.Combine(root, "podish.log")
                })
            };

            var containerId = "legacy-" + Guid.NewGuid().ToString("N")[..12];
            PodishContainerMetadataStore.Write(ctx.Context.ContainersDir, new PodishContainerMetadata
            {
                ContainerId = containerId,
                Name = "legacy",
                Image = imageStoreDir,
                State = "created",
                Running = false,
                HasTerminal = false,
                Spec = new PodishRunSpec
                {
                    Name = "legacy",
                    Image = imageStoreDir,
                    Exe = null,
                    ExeArgs = Array.Empty<string>(),
                    Env = Array.Empty<string>(),
                    Interactive = false,
                    Tty = false,
                    LogDriver = "none"
                }
            });

            var container = ctx.OpenContainerByIdOrName(containerId);
            Assert.NotNull(container);

            await container!.StartAsync();
            var exitCode = await container.WaitAsync();
            Assert.Equal(0, exitCode);

            var metadata = PodishContainerMetadataStore.Resolve(ctx.Context.ContainersDir, containerId);
            Assert.NotNull(metadata);
            Assert.Equal("hello_static", metadata!.Spec.Exe);
            Assert.Equal<string[]>(Array.Empty<string>(), metadata.Spec.ExeArgs);
            Assert.Equal("1000", metadata.Spec.User);
            Assert.Equal("/workspace", metadata.Spec.WorkingDir);
            Assert.Equal<string[]>(["A=1"], metadata.Spec.Env);
        }
        finally
        {
            TestWorkspace.DeleteDirectory(root);
        }
    }

    [Fact]
    public void ImageArchiveService_SaveLoad_PreservesRuntimeConfigFields()
    {
        var sourceRoot = TestWorkspace.CreateUniqueDirectory("podish-archive-source-");
        var destinationRoot = TestWorkspace.CreateUniqueDirectory("podish-archive-dest-");
        var archivePath = Path.Combine(sourceRoot, "image.oci.tar");
        const string imageReference = "example.com/demo/app:1.0";

        try
        {
            var safeName = ContainerLaunchSpecResolver.ToSafeImageName(imageReference);
            var sourceStoreDir = Path.Combine(sourceRoot, ".fiberpod", "oci", "images", safeName);
            CreateExecutableImageStore(
                sourceStoreDir,
                "/bin/hello_static",
                TestWorkspace.ResolveHelloStaticPath(),
                configUser: "1000:1001",
                configCmd: ["hello_static", "--flag"],
                configEntrypoint: ["/bin/sh", "-lc"],
                configEnv: ["A=1", "B=2"],
                configWorkingDir: "/workspace");

            var sourceArchiveService = new ImageArchiveService(sourceRoot);
            sourceArchiveService.Save(archivePath, [imageReference]);

            var destinationArchiveService = new ImageArchiveService(destinationRoot);
            var loaded = destinationArchiveService.Load(archivePath);

            Assert.Equal<string[]>([imageReference], loaded.ToArray());

            var destinationStoreDir = Path.Combine(destinationRoot, ".fiberpod", "oci", "images", safeName);
            var stored = JsonSerializer.Deserialize(
                File.ReadAllText(Path.Combine(destinationStoreDir, "image.json")),
                PodishJsonContext.Default.OciStoredImage);

            Assert.NotNull(stored);
            Assert.Equal("1000:1001", stored!.ConfigUser);
            Assert.Equal<string[]>(["/bin/sh", "-lc"], stored.ConfigEntrypoint!);
            Assert.Equal<string[]>(["hello_static", "--flag"], stored.ConfigCmd!);
            Assert.Equal<string[]>(["A=1", "B=2"], stored.ConfigEnv!);
            Assert.Equal("/workspace", stored.ConfigWorkingDir);
        }
        finally
        {
            TestWorkspace.DeleteDirectory(sourceRoot);
            TestWorkspace.DeleteDirectory(destinationRoot);
        }
    }

    private static void WriteStoredImageMetadata(
        string storeDir,
        string? configUser = null,
        string[]? configEntrypoint = null,
        string[]? configCmd = null,
        string[]? configEnv = null,
        string? configWorkingDir = null)
    {
        Directory.CreateDirectory(storeDir);
        var image = new OciStoredImage(
            ImageReference: "test:image",
            Registry: "local",
            Repository: "test",
            Tag: "latest",
            ManifestDigest: "sha256:deadbeef",
            StoreDirectory: ".",
            Layers: Array.Empty<OciStoredLayer>(),
            ConfigUser: configUser,
            ConfigEntrypoint: configEntrypoint,
            ConfigCmd: configCmd,
            ConfigEnv: configEnv,
            ConfigWorkingDir: configWorkingDir);
        File.WriteAllText(
            Path.Combine(storeDir, "image.json"),
            JsonSerializer.Serialize(image));
    }

    private static void CreateExecutableImageStore(
        string storeDir,
        string guestExePath,
        string hostExePath,
        string? configUser = null,
        string[]? configCmd = null,
        string[]? configEntrypoint = null,
        string[]? configEnv = null,
        string? configWorkingDir = null,
        bool createWorkingDirectory = true)
    {
        Directory.CreateDirectory(storeDir);
        var blobsDir = Path.Combine(storeDir, "blobs", "sha256");
        var indexesDir = Path.Combine(storeDir, "indexes");
        Directory.CreateDirectory(blobsDir);
        Directory.CreateDirectory(indexesDir);

        var exeBytes = File.ReadAllBytes(hostExePath);
        using var sha = SHA256.Create();
        var digestHex = Convert.ToHexString(sha.ComputeHash(exeBytes)).ToLowerInvariant();

        var tarPath = Path.Combine(blobsDir, $"{digestHex}.tar");
        using (var stream = File.Create(tarPath))
        using (var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: false))
        {
            AddDirectoryEntry(writer, "/bin");
            if (!string.IsNullOrWhiteSpace(configWorkingDir) && createWorkingDirectory)
                AddDirectoryEntry(writer, configWorkingDir!);

            var entry = new PaxTarEntry(TarEntryType.RegularFile, guestExePath.TrimStart('/'))
            {
                DataStream = new MemoryStream(exeBytes, writable: false),
                Mode = (UnixFileMode)0x1ED
            };
            writer.WriteEntry(entry);
        }

        List<LayerIndexEntry> entries;
        using (var tarStream = File.OpenRead(tarPath))
        {
            entries = OciLayerIndexBuilder.BuildFromTar(tarStream, $"sha256:{digestHex}")
                .Entries
                .Values
                .Select(static entry => entry with { InlineData = null })
                .ToList();
        }

        var indexPath = Path.Combine(indexesDir, $"{digestHex}.json");
        File.WriteAllText(indexPath, JsonSerializer.Serialize(entries));

        var image = new OciStoredImage(
            ImageReference: storeDir,
            Registry: "local",
            Repository: "test",
            Tag: "latest",
            ManifestDigest: $"sha256:{digestHex}",
            StoreDirectory: ".",
            Layers:
            [
                new OciStoredLayer(
                    Digest: $"sha256:{digestHex}",
                    MediaType: "application/vnd.oci.image.layer.v1.tar",
                    Size: new FileInfo(tarPath).Length,
                    BlobPath: $"blobs/sha256/{digestHex}.tar",
                    IndexPath: $"indexes/{digestHex}.json")
            ],
            ConfigUser: configUser,
            ConfigEntrypoint: configEntrypoint,
            ConfigCmd: configCmd,
            ConfigEnv: configEnv,
            ConfigWorkingDir: configWorkingDir);

        File.WriteAllText(
            Path.Combine(storeDir, "image.json"),
            JsonSerializer.Serialize(image));
    }

    private static void AddDirectoryEntry(TarWriter writer, string guestDirectory)
    {
        var parts = guestDirectory.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return;

        var current = string.Empty;
        for (var i = 0; i < parts.Length; i++)
        {
            current = string.IsNullOrEmpty(current) ? parts[i] : $"{current}/{parts[i]}";
            var entry = new PaxTarEntry(TarEntryType.Directory, current)
            {
                Mode = (UnixFileMode)0x1ED
            };
            writer.WriteEntry(entry);
        }
    }

}
