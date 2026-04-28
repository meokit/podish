using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;
using Fiberish.VFS;
using Podish.Core;
using Podish.Core.Native;
using Xunit;

namespace Fiberish.Tests.Podish;

public sealed class ContainerRestartTests
{
    [Fact]
    public async Task NativeContainer_ImageBacked_StartWaitStartWait_PreservesExecutable()
    {
        var root = TestWorkspace.CreateUniqueDirectory("podish-native-restart-");

        try
        {
            var imageStoreDir = Path.Combine(root, "image-store");
            CreateMinimalOciStore(imageStoreDir, "/bin/ash", TestWorkspace.ResolveHelloStaticPath());

            using var ctx = new NativeContext
            {
                Context = new PodishContext(new PodishContextOptions
                {
                    WorkDir = root,
                    LogFile = Path.Combine(root, "podish.log"),
                    LogLevel = "error"
                })
            };

            var created = ctx.CreateContainer(new PodishRunSpec
            {
                Name = "restartable",
                Image = imageStoreDir,
                Exe = "/bin/ash",
                ExeArgs = Array.Empty<string>(),
                Interactive = false,
                Tty = false,
                AutoRemove = false
            });

            Assert.NotNull(created.Container);
            Assert.Equal(PodishNativeApi.PodOk, created.Code);

            await created.Container!.StartAsync();
            var firstExit = await created.Container.WaitAsync();

            await created.Container.StartAsync();
            var secondExit = await created.Container.WaitAsync();

            Assert.Equal(0, firstExit);
            Assert.Equal(0, secondExit);
        }
        finally
        {
            TestWorkspace.DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task NativeContainer_WaitUntilStartedOrExitedAsync_TreatsQuickExitAfterBindAsStarted()
    {
        var controller = new ContainerProcessController();
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new PodishContainerSession("quick-exit", "image", exited.Task, null, controller);

        var waitTask = NativeContainer.WaitUntilStartedOrExitedAsync(session, 100);

        controller.BindInitProcess(123, static _ => { });
        controller.Unbind();
        exited.SetResult(0);

        Assert.True(await waitTask);
        Assert.True(session.HasStarted);
        Assert.True(session.IsCompleted);
        Assert.Null(session.InitPid);
    }

    [Fact(Skip = "暂时跳过：test_futex guest 资产当前未纳入此测试流程，待单独补齐。")]
    public async Task NativeContainer_ImageBacked_StartStopStart_DoesNotLoseExecutable()
    {
        var root = TestWorkspace.CreateUniqueDirectory("podish-native-stop-restart-");

        try
        {
            var imageStoreDir = Path.Combine(root, "image-store");
            CreateMinimalOciStore(imageStoreDir, "/bin/ash", TestWorkspace.ResolveGuestAssetPath("test_futex"));

            using var ctx = new NativeContext
            {
                Context = new PodishContext(new PodishContextOptions
                {
                    WorkDir = root,
                    LogFile = Path.Combine(root, "podish.log"),
                    LogLevel = "error"
                })
            };

            var created = ctx.CreateContainer(new PodishRunSpec
            {
                Name = "restart-after-stop",
                Image = imageStoreDir,
                Exe = "/bin/ash",
                ExeArgs = Array.Empty<string>(),
                Interactive = false,
                Tty = false,
                AutoRemove = false
            });

            Assert.NotNull(created.Container);
            Assert.Equal(PodishNativeApi.PodOk, created.Code);

            await created.Container!.StartAsync();
            await WaitUntilAsync(static c => c.IsRunning, created.Container, TimeSpan.FromSeconds(1));

            Assert.True(created.Container.Stop(15, 500));

            await created.Container.StartAsync();
            var exitCode = await created.Container.WaitAsync();
            Assert.Equal(0, exitCode);
        }
        finally
        {
            TestWorkspace.DeleteDirectory(root);
        }
    }

    private static void CreateMinimalOciStore(string storeDir, string guestExePath, string hostExePath)
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
        CreateSingleFileTar(tarPath, guestExePath, exeBytes, 0x1ED);

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
        File.WriteAllText(
            indexPath,
            JsonSerializer.Serialize(entries));

        var image = new OciStoredImage(
            ImageReference: storeDir,
            Registry: "local",
            Repository: "restart-test",
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
            ]);

        File.WriteAllText(
            Path.Combine(storeDir, "image.json"),
            JsonSerializer.Serialize(image));
    }

    private static void CreateSingleFileTar(string tarPath, string guestPath, byte[] content, int mode)
    {
        using var stream = File.Create(tarPath);
        using var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: false);

        AddDirectoryEntries(writer, guestPath);

        var entry = new PaxTarEntry(TarEntryType.RegularFile, guestPath.TrimStart('/'))
        {
            DataStream = new MemoryStream(content, writable: false),
            Mode = (UnixFileMode)mode
        };
        writer.WriteEntry(entry);
    }

    private static void AddDirectoryEntries(TarWriter writer, string guestPath)
    {
        var parts = guestPath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 1)
            return;

        var current = string.Empty;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            current = string.IsNullOrEmpty(current) ? parts[i] : $"{current}/{parts[i]}";
            var entry = new PaxTarEntry(TarEntryType.Directory, current)
            {
                Mode = (UnixFileMode)0x1ED
            };
            writer.WriteEntry(entry);
        }
    }

    private static async Task WaitUntilAsync(Func<NativeContainer, bool> predicate, NativeContainer container,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate(container))
                return;
            await Task.Delay(10);
        }

        Assert.True(predicate(container), "Condition was not met before timeout.");
    }
}
