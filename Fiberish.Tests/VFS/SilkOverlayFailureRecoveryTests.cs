using System.Text;
using Fiberish.Core;
using Fiberish.SilkFS;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.VFS;

public class SilkOverlayFailureRecoveryTests
{
    private readonly TestRuntimeFactory _runtime = new();

    [Fact]
    public void Overlay_SilkUpper_FailedCreate_DoesNotPersistPhantomRegularFile()
    {
        var silkRoot = Path.Combine(Path.GetTempPath(), $"silk-overlay-create-rollback-{Guid.NewGuid():N}");
        try
        {
            using (var engine = _runtime.CreateEngine())
            {
                var sm = new SyscallManager(engine, _runtime.CreateAddressSpace(), 0);
                var lowerSb = CreateEmptyTmpfsLower();
                sm.MountRootOverlayWithLower(lowerSb, "silkfs", silkRoot);

                var livePath = Path.Combine(silkRoot, "live");
                Directory.Delete(livePath, true);
                File.WriteAllText(livePath, "block-live-dir");

                var root = Assert.IsType<OverlayInode>(sm.RootMount!.Root.Inode);
                var ghost = new Dentry(FsName.FromString("ghost"), null, sm.RootMount.Root, sm.RootMount.SB);

                Assert.ThrowsAny<IOException>(() => root.Create(ghost, 0x1A4, 0, 0));
                Assert.Null(root.Lookup("ghost"));

                var opts = SilkFsOptions.FromSource(silkRoot);
                var meta = new SilkMetadataStore(opts.MetadataPath);
                using var session = meta.OpenSession();
                Assert.Null(session.LookupDentry(SilkMetadataStore.RootInode, Encoding.UTF8.GetBytes("ghost")));

                sm.Close();
            }

            DeletePathIfExists(Path.Combine(silkRoot, "live"));
            Directory.CreateDirectory(Path.Combine(silkRoot, "live"));

            using (var engine = _runtime.CreateEngine())
            {
                var sm = new SyscallManager(engine, _runtime.CreateAddressSpace(), 0);
                var lowerSb = CreateEmptyTmpfsLower();
                sm.MountRootOverlayWithLower(lowerSb, "silkfs", silkRoot);

                var root = Assert.IsType<OverlayInode>(sm.RootMount!.Root.Inode);
                Assert.Null(root.Lookup("ghost"));

                sm.Close();
            }
        }
        finally
        {
            DeletePathIfExists(Path.Combine(silkRoot, "live"));
            DeletePathIfExists(silkRoot);
        }
    }

    [Fact]
    public void Overlay_SilkUpper_RepeatedMissingBackingOpenFailures_DoNotLeakRefs()
    {
        var silkRoot = Path.Combine(Path.GetTempPath(), $"silk-overlay-open-leak-{Guid.NewGuid():N}");
        try
        {
            using var engine = _runtime.CreateEngine();
            var sm = new SyscallManager(engine, _runtime.CreateAddressSpace(), 0);
            var lowerSb = CreateEmptyTmpfsLower();
            sm.MountRootOverlayWithLower(lowerSb, "silkfs", silkRoot);

            var root = Assert.IsType<OverlayInode>(sm.RootMount!.Root.Inode);
            var ghost = new Dentry(FsName.FromString("ghost"), null, sm.RootMount.Root, sm.RootMount.SB);
            Assert.Equal(0, root.Create(ghost, 0x1A4, 0, 0));

            var fileDentry = root.Lookup("ghost");
            Assert.NotNull(fileDentry);
            var overlayInode = Assert.IsType<OverlayInode>(fileDentry!.Inode);
            var upperInode = Assert.IsType<SilkInode>(overlayInode.UpperInode);
            var repo = new SilkRepository(SilkFsOptions.FromSource(silkRoot));
            File.Delete(repo.GetLiveInodePath((long)upperInode.Ino));

            var overlayBaseRefCount = overlayInode.RefCount;
            var overlayBaseOpenRefs = overlayInode.FileOpenRefCount;
            var overlayBaseMmapRefs = overlayInode.FileMmapRefCount;
            var upperBaseRefCount = upperInode.RefCount;
            var upperBaseOpenRefs = upperInode.FileOpenRefCount;
            var upperBaseMmapRefs = upperInode.FileMmapRefCount;

            for (var i = 0; i < 32; i++)
            {
                Assert.Throws<FileNotFoundException>(() => new LinuxFile(fileDentry, FileFlags.O_RDONLY, sm.RootMount!));
                Assert.Throws<FileNotFoundException>(() =>
                    new LinuxFile(fileDentry, FileFlags.O_RDONLY, sm.RootMount!, LinuxFile.ReferenceKind.MmapHold));
            }

            Assert.Equal(overlayBaseRefCount, overlayInode.RefCount);
            Assert.Equal(overlayBaseOpenRefs, overlayInode.FileOpenRefCount);
            Assert.Equal(overlayBaseMmapRefs, overlayInode.FileMmapRefCount);
            Assert.Equal(upperBaseRefCount, upperInode.RefCount);
            Assert.Equal(upperBaseOpenRefs, upperInode.FileOpenRefCount);
            Assert.Equal(upperBaseMmapRefs, upperInode.FileMmapRefCount);

            sm.Close();
        }
        finally
        {
            DeletePathIfExists(silkRoot);
        }
    }

    private SuperBlock CreateEmptyTmpfsLower()
    {
        var tmpfsType = new FileSystemType
        {
            Name = "tmpfs",
            Factory = static _ => new Tmpfs(),
            FactoryWithContext = static (_, memoryContext) => new Tmpfs(memoryContext: memoryContext)
        };

        return tmpfsType.CreateAnonymousFileSystem(_runtime.MemoryContext)
            .ReadSuper(tmpfsType, 0, $"tmpfs-lower-{Guid.NewGuid():N}", null);
    }

    private static void DeletePathIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
        else if (Directory.Exists(path))
            Directory.Delete(path, true);
    }
}
