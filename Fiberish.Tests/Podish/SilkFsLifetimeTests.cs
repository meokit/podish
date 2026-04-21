using Xunit;
using System.Reflection;
using System.Diagnostics;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.SilkFS;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Podish.Core;
using Xunit.Sdk;
using DiagnosticsProcess = System.Diagnostics.Process;

namespace Fiberish.Tests.Podish;

public sealed class SilkFsLifetimeTests
{
    [Fact]
    public void KernelRuntimeDispose_WithoutSyscallsClose_LeavesSilkMetadataSessionAlive()
    {
        var root = Path.Combine(Path.GetTempPath(), "silkfs-lifetime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var runtime = KernelRuntime.BootstrapBare(false, memoryContext: new MemoryRuntimeContext());
            var sb = MountSilkRoot(runtime, root);

            Assert.False(GetMetadataSessionDisposed(sb));
            Assert.True(sb.RefCount > 0);

            runtime.Dispose();

            Assert.False(GetMetadataSessionDisposed(sb));
            Assert.True(sb.RefCount > 0);

            runtime.Syscalls.Close();

            Assert.True(GetMetadataSessionDisposed(sb));
            Assert.Equal(0, sb.RefCount);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public void SyscallsClose_ReleasesSilkSuperBlockAndMetadataSession()
    {
        var root = Path.Combine(Path.GetTempPath(), "silkfs-close-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            using var runtime = KernelRuntime.BootstrapBare(false, memoryContext: new MemoryRuntimeContext());
            var sb = MountSilkRoot(runtime, root);

            Assert.False(GetMetadataSessionDisposed(sb));
            Assert.True(sb.RefCount > 0);

            runtime.Syscalls.Close();

            Assert.True(GetMetadataSessionDisposed(sb));
            Assert.Equal(0, sb.RefCount);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task PodishSession_ForceStop_ReleasesSilkMetadataSession()
    {
        EnsureZigAvailable();

        var root = Path.Combine(Path.GetTempPath(), "podish-silkfs-force-stop-" + Guid.NewGuid().ToString("N"));
        var guestAssetDir = Path.Combine(root, "guest-assets");
        var imageStoreDir = Path.Combine(root, "image-store");
        var sleepyBinary = Path.Combine(guestAssetDir, "sleepy");
        Directory.CreateDirectory(guestAssetDir);

        try
        {
            BuildGuestSleepBinary(sleepyBinary);
            CreateMinimalOciStore(imageStoreDir, "/sleepy", sleepyBinary);

            var superBlockReady = new TaskCompletionSource<SilkSuperBlock>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var ctx = new PodishContext(new PodishContextOptions
            {
                WorkDir = root,
                LogFile = Path.Combine(root, "podish.log"),
                LogLevel = "error"
            });

            var session = await ctx.StartAsync(new PodishRunSpec
            {
                Name = "force-stop-silkfs",
                Image = imageStoreDir,
                Exe = "/sleepy",
                ExeArgs = Array.Empty<string>(),
                Interactive = false,
                Tty = false,
                AutoRemove = false,
                TestSuperBlockObserver = sb =>
                {
                    if (sb is SilkSuperBlock silk)
                        superBlockReady.TrySetResult(silk);
                }
            });

            var capturedSuperBlock = await WaitForTaskAsync(superBlockReady.Task, TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => GetMetadataSession(capturedSuperBlock) != null, TimeSpan.FromSeconds(5));
            Assert.False(GetMetadataSessionDisposed(capturedSuperBlock));

            await WaitUntilAsync(() => session.HasStarted || session.IsCompleted, TimeSpan.FromSeconds(5));
            Assert.False(session.IsCompleted);
            Assert.True(await WaitUntilConditionReturnsTrueAsync(session.ForceStop, TimeSpan.FromSeconds(5)));
            _ = await session.WaitAsync();

            Assert.True(GetMetadataSessionDisposed(capturedSuperBlock));
            Assert.Equal(0, capturedSuperBlock.RefCount);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    private static SilkSuperBlock MountSilkRoot(KernelRuntime runtime, string root)
    {
        var fsType = new FileSystemType
        {
            Name = "silkfs",
            FactoryWithContext = (devManager, memoryContext) => new SilkFileSystem(devManager, memoryContext)
        };

        var sb = (SilkSuperBlock)fsType.CreateFileSystem(runtime.DeviceNumbers, runtime.MemoryContext)
            .ReadSuper(fsType, 0, root, null);
        runtime.Syscalls.MountRoot(sb, new SyscallManager.RootMountOptions
        {
            Source = root,
            FsType = "silkfs",
            Options = "rw"
        });
        return sb;
    }

    private static bool GetMetadataSessionDisposed(SilkSuperBlock sb)
    {
        var field = typeof(SilkSuperBlock).GetField("_metadataSessionDisposed",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<bool>(field!.GetValue(sb));
    }

    private static object? GetMetadataSession(SilkSuperBlock sb)
    {
        var field = typeof(SilkSuperBlock).GetField("_metadataSession",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field!.GetValue(sb);
    }

    private static void EnsureZigAvailable()
    {
        if (!CommandSucceeds("zig", "version"))
            throw new SkipException("zig is required to build the guest sleep binary for this test.");
    }

    private static void BuildGuestSleepBinary(string outputPath)
    {
        var sourcePath = Path.Combine(Path.GetDirectoryName(outputPath)!, "sleepy.c");
        File.WriteAllText(sourcePath, """
                                    #include <fcntl.h>
                                    #include <unistd.h>
                                    #include <time.h>

                                    int main(void) {
                                        int fd = open("/probe", O_CREAT | O_WRONLY | O_TRUNC, 0644);
                                        if (fd >= 0) {
                                            write(fd, "probe", 5);
                                            close(fd);
                                        }
                                        struct timespec req;
                                        req.tv_sec = 0;
                                        req.tv_nsec = 100000000;
                                        for (;;) nanosleep(&req, 0);
                                    }
                                    """);

        RunChecked("zig",
            $"cc -target x86-linux-musl -static -O2 -o \"{outputPath}\" \"{sourcePath}\"");
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
        File.WriteAllText(indexPath, JsonSerializer.Serialize(entries));

        var image = new OciStoredImage(
            ImageReference: storeDir,
            Registry: "local",
            Repository: "silkfs-force-stop-test",
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
            JsonSerializer.Serialize(image, PodishJsonContext.Default.OciStoredImage));
    }

    private static void CreateSingleFileTar(string tarPath, string guestPath, byte[] content, int mode)
    {
        using var fs = File.Create(tarPath);
        using var writer = new TarWriter(fs, leaveOpen: false);
        var entry = new PaxTarEntry(TarEntryType.RegularFile, guestPath.TrimStart('/'))
        {
            DataStream = new MemoryStream(content, writable: false),
            Mode = (UnixFileMode)mode
        };
        writer.WriteEntry(entry);
    }


    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;

            await Task.Delay(20);
        }

        Assert.True(condition(), "Condition was not satisfied before timeout.");
    }

    private static async Task<T> WaitForTaskAsync<T>(Task<T> task, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout));
        Assert.True(ReferenceEquals(task, completed), "Task did not complete before timeout.");
        return await task;
    }

    private static async Task<bool> WaitUntilConditionReturnsTrueAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;

            await Task.Delay(20);
        }

        return condition();
    }

    private static bool CommandSucceeds(string fileName, string arguments)
    {
        try
        {
            using var process = DiagnosticsProcess.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            });
            if (process == null)
                return false;

            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void RunChecked(string fileName, string arguments)
    {
        using var process = DiagnosticsProcess.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        });
        Assert.NotNull(process);
        var stdout = process!.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0,
            $"Command failed: {fileName} {arguments}\nstdout:\n{stdout}\nstderr:\n{stderr}");
    }
}
