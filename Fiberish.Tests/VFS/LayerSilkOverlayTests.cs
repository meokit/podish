using System.Text;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.SilkFS;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.VFS;

public class LayerSilkOverlayTests
{
    [Fact]
    public void Overlay_LayerfsLower_And_SilkfsUpper_Works()
    {
        var silkRoot = Path.Combine(Path.GetTempPath(), $"silkfs-layer-overlay-{Guid.NewGuid():N}");
        try
        {
            var payload = Encoding.UTF8.GetBytes("ID=layer\n");
            var index = new LayerIndex();
            index.AddEntry(new LayerIndexEntry("/etc", InodeType.Directory, 0x1ED));
            index.AddEntry(new LayerIndexEntry("/etc/os-release", InodeType.File, 0x1A4, Size: (ulong)payload.Length,
                InlineData: payload));

            using var engine = new Engine();
            var sm = new SyscallManager(engine, new VMAManager(), 0);

            var layerType = FileSystemRegistry.Get("layerfs")!;
            var lowerSb = layerType.CreateFileSystem().ReadSuper(layerType, 0, "test-lower",
                new LayerMountOptions { Index = index, ContentProvider = new InMemoryLayerContentProvider() });
            sm.MountRootOverlayWithLower(lowerSb, "silkfs", silkRoot);

            var osRelease = sm.PathWalkWithFlags("/etc/os-release", LookupFlags.FollowSymlink);
            Assert.True(osRelease.IsValid);
            var rf = new LinuxFile(osRelease.Dentry!, FileFlags.O_RDONLY, osRelease.Mount!);
            var buf = new byte[64];
            var n = osRelease.Dentry!.Inode!.Read(rf, buf, 0);
            rf.Close();
            Assert.Equal(payload.Length, n);
            Assert.Equal("ID=layer\n", Encoding.UTF8.GetString(buf, 0, n));

            var etc = sm.PathWalkWithFlags("/etc", LookupFlags.FollowSymlink);
            Assert.True(etc.IsValid);
            var fiber = new Dentry("fiber.txt", null, etc.Dentry, etc.Dentry!.SuperBlock);
            etc.Dentry.Inode!.Create(fiber, 0x1A4, 0, 0);
            var wf = new LinuxFile(fiber, FileFlags.O_WRONLY, etc.Mount!);
            var wrote = fiber.Inode!.Write(wf, "hello"u8.ToArray(), 0);
            wf.Close();
            Assert.Equal(5, wrote);

            var upperRepo = new SilkRepository(SilkFsOptions.FromSource(silkRoot));
            upperRepo.Initialize();
            var etcIno = upperRepo.Metadata.LookupDentry(SilkMetadataStore.RootInode, "etc");
            Assert.NotNull(etcIno);
            var fiberIno = upperRepo.Metadata.LookupDentry(etcIno!.Value, "fiber.txt");
            Assert.NotNull(fiberIno);
            var livePath = upperRepo.GetLiveInodePath(fiberIno!.Value);
            Assert.True(File.Exists(livePath));
            Assert.Equal("hello", File.ReadAllText(livePath));

            var lowerEtc = lowerSb.Root.Inode!.Lookup("etc");
            Assert.NotNull(lowerEtc);
            Assert.Null(lowerEtc!.Inode!.Lookup("fiber.txt"));

            sm.Close();
        }
        finally
        {
            if (Directory.Exists(silkRoot)) Directory.Delete(silkRoot, true);
        }
    }
}