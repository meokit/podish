using System.Text;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
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
            var lowerSb = layerType.CreateAnonymousFileSystem().ReadSuper(layerType, 0, "test-lower",
                new LayerMountOptions { Index = index, ContentProvider = new InMemoryLayerContentProvider() });
            sm.MountRootOverlayWithLower(lowerSb, "silkfs", silkRoot);

            var osRelease = sm.PathWalkWithFlags("/etc/os-release", LookupFlags.FollowSymlink);
            Assert.True(osRelease.IsValid);
            var rf = new LinuxFile(osRelease.Dentry!, FileFlags.O_RDONLY, osRelease.Mount!);
            var buf = new byte[64];
            var n = osRelease.Dentry!.Inode!.ReadToHost(null, rf, buf, 0);
            rf.Close();
            Assert.Equal(payload.Length, n);
            Assert.Equal("ID=layer\n", Encoding.UTF8.GetString(buf, 0, n));

            var etc = sm.PathWalkWithFlags("/etc", LookupFlags.FollowSymlink);
            Assert.True(etc.IsValid);
            var fiber = new Dentry("fiber.txt", null, etc.Dentry, etc.Dentry!.SuperBlock);
            etc.Dentry.Inode!.Create(fiber, 0x1A4, 0, 0);
            var wf = new LinuxFile(fiber, FileFlags.O_WRONLY, etc.Mount!);
            var wrote = fiber.Inode!.WriteFromHost(null, wf, "hello"u8.ToArray(), 0);
            wf.Close();
            Assert.Equal(5, wrote);

            var upperRepo = new SilkRepository(SilkFsOptions.FromSource(silkRoot));
            upperRepo.Initialize();
            using var session = upperRepo.OpenMetadataSession();
            var etcIno = session.LookupDentry(SilkMetadataStore.RootInode, "etc");
            Assert.NotNull(etcIno);
            var fiberIno = session.LookupDentry(etcIno!.Value, "fiber.txt");
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

    [Fact]
    public void Overlay_LayerfsLower_ReadPage_TriggersLowerReadAhead()
    {
        var silkRoot = Path.Combine(Path.GetTempPath(), $"layerfs-pagecache-readahead-{Guid.NewGuid():N}");
        try
        {
            var payload = new byte[LinuxConstants.PageSize * 32];
            for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 256);
            var index = new LayerIndex();
            index.AddEntry(new LayerIndexEntry("/etc", InodeType.Directory, 0x1ED));
            index.AddEntry(new LayerIndexEntry("/etc/os-release", InodeType.File, 0x1A4, Size: (ulong)payload.Length,
                InlineData: payload));

            using var engine = new Engine();
            var sm = new SyscallManager(engine, new VMAManager(), 0);

            var layerType = FileSystemRegistry.Get("layerfs")!;
            var lowerSb = layerType.CreateAnonymousFileSystem().ReadSuper(layerType, 0, "test-lower",
                new LayerMountOptions
                {
                    Index = index,
                    ContentProvider = new InMemoryLayerContentProvider(),
                    MinimumReadAheadBytes = 128 * 1024
                });
            sm.MountRootOverlayWithLower(lowerSb, "silkfs", silkRoot);

            var osRelease = sm.PathWalkWithFlags("/etc/os-release", LookupFlags.FollowSymlink);
            Assert.True(osRelease.IsValid);
            var overlayInode = Assert.IsType<OverlayInode>(osRelease.Dentry!.Inode);
            Assert.NotNull(overlayInode.LowerInode);
            var mappingRef = ((MappingBackedInode)overlayInode.LowerInode!).AcquireMappingRef();
            try
            {
                var file = new LinuxFile(osRelease.Dentry!, FileFlags.O_RDONLY, osRelease.Mount!);

                var pageBuffer = new byte[LinuxConstants.PageSize];
                var rc = osRelease.Dentry.Inode.ReadPage(file, new PageIoRequest(0, 0, LinuxConstants.PageSize),
                    pageBuffer);

                Assert.Equal(0, rc);
                Assert.NotNull(overlayInode.LowerInode!.Mapping);
                Assert.Equal(32, overlayInode.LowerInode.Mapping.PageCount);
                Assert.Equal(payload.AsSpan(0, LinuxConstants.PageSize).ToArray(), pageBuffer);

                file.Close();
            }
            finally
            {
                mappingRef.Release();
            }
            sm.Close();
        }
        finally
        {
            if (Directory.Exists(silkRoot)) Directory.Delete(silkRoot, true);
        }
    }

    [Fact]
    public void Overlay_SymlinkCopyUp_ToSilkfsUpper_PreservesSymlinkAcrossRemount()
    {
        var silkRoot = Path.Combine(Path.GetTempPath(), $"silkfs-symlink-copyup-{Guid.NewGuid():N}");
        try
        {
            var busyboxPayload = Encoding.UTF8.GetBytes("busybox");
            var index = new LayerIndex();
            index.AddEntry(new LayerIndexEntry("/bin", InodeType.Directory, 0x1ED));
            index.AddEntry(new LayerIndexEntry(
                "/bin/busybox",
                InodeType.File,
                0x1ED,
                Size: (ulong)busyboxPayload.Length,
                InlineData: busyboxPayload));
            index.AddEntry(new LayerIndexEntry("/bin/ash", InodeType.Symlink, 0x1FF, SymlinkTarget: "/bin/busybox"));

            using (var engine = new Engine())
            {
                var sm = new SyscallManager(engine, new VMAManager(), 0);
                var layerType = FileSystemRegistry.Get("layerfs")!;
                var lowerSb = layerType.CreateAnonymousFileSystem().ReadSuper(
                    layerType,
                    0,
                    "test-lower",
                    new LayerMountOptions { Index = index, ContentProvider = new InMemoryLayerContentProvider() });
                sm.MountRootOverlayWithLower(lowerSb, "silkfs", silkRoot);

                var binLoc = sm.PathWalkWithFlags("/bin", LookupFlags.FollowSymlink);
                Assert.True(binLoc.IsValid);

                var ash = binLoc.Dentry!.Inode!.Lookup("ash");
                Assert.NotNull(ash);

                var ashOverlay = Assert.IsType<OverlayInode>(ash!.Inode);
                Assert.Equal(0, ashOverlay.Readlink(out var ashTarget));
                Assert.Equal("/bin/busybox", ashTarget);

                var copyRc = ashOverlay.CopyUp(null);
                Assert.Equal(0, copyRc);

                Assert.Equal(InodeType.Symlink, ashOverlay.Type);
                Assert.Equal(0, ashOverlay.Readlink(out var copiedAshTarget));
                Assert.Equal("/bin/busybox", copiedAshTarget);

                sm.Close();
            }

            using (var engine = new Engine())
            {
                var sm = new SyscallManager(engine, new VMAManager(), 0);
                var layerType = FileSystemRegistry.Get("layerfs")!;
                var lowerSb = layerType.CreateAnonymousFileSystem().ReadSuper(
                    layerType,
                    0,
                    "test-lower",
                    new LayerMountOptions { Index = index, ContentProvider = new InMemoryLayerContentProvider() });
                sm.MountRootOverlayWithLower(lowerSb, "silkfs", silkRoot);

                var ashLoc = sm.PathWalkWithFlags("/bin/ash", LookupFlags.None);
                Assert.True(ashLoc.IsValid);
                Assert.Equal(InodeType.Symlink, ashLoc.Dentry!.Inode!.Type);
                Assert.Equal(0, ashLoc.Dentry.Inode.Readlink(out var ashLocTarget));
                Assert.Equal("/bin/busybox", ashLocTarget);

                sm.Close();
            }
        }
        finally
        {
            if (Directory.Exists(silkRoot)) Directory.Delete(silkRoot, true);
        }
    }

    [Fact]
    public void Overlay_UnlinkLowerFile_InSilkfsUpper_PersistsWhiteoutAcrossRemount()
    {
        var silkRoot = Path.Combine(Path.GetTempPath(), $"silkfs-whiteout-unlink-{Guid.NewGuid():N}");
        try
        {
            var index = new LayerIndex();
            index.AddEntry(new LayerIndexEntry("/etc", InodeType.Directory, 0x1ED));
            index.AddEntry(new LayerIndexEntry("/etc/apk", InodeType.Directory, 0x1ED));
            index.AddEntry(new LayerIndexEntry(
                "/etc/apk/world",
                InodeType.File,
                0x1A4,
                Size: 5,
                InlineData: Encoding.UTF8.GetBytes("world")));

            using (var engine = new Engine())
            {
                var sm = new SyscallManager(engine, new VMAManager(), 0);
                var layerType = FileSystemRegistry.Get("layerfs")!;
                var lowerSb = layerType.CreateAnonymousFileSystem().ReadSuper(
                    layerType,
                    0,
                    "test-lower",
                    new LayerMountOptions { Index = index, ContentProvider = new InMemoryLayerContentProvider() });
                sm.MountRootOverlayWithLower(lowerSb, "silkfs", silkRoot);

                var apkLoc = sm.PathWalkWithFlags("/etc/apk", LookupFlags.FollowSymlink);
                Assert.True(apkLoc.IsValid);
                apkLoc.Dentry!.Inode!.Unlink("world");
                Assert.Null(apkLoc.Dentry.Inode.Lookup("world"));

                sm.Close();
            }

            using (var engine = new Engine())
            {
                var sm = new SyscallManager(engine, new VMAManager(), 0);
                var layerType = FileSystemRegistry.Get("layerfs")!;
                var lowerSb = layerType.CreateAnonymousFileSystem().ReadSuper(
                    layerType,
                    0,
                    "test-lower",
                    new LayerMountOptions { Index = index, ContentProvider = new InMemoryLayerContentProvider() });
                sm.MountRootOverlayWithLower(lowerSb, "silkfs", silkRoot);

                var apkLoc = sm.PathWalkWithFlags("/etc/apk", LookupFlags.FollowSymlink);
                Assert.True(apkLoc.IsValid);
                Assert.Null(apkLoc.Dentry!.Inode!.Lookup("world"));

                sm.Close();
            }
        }
        finally
        {
            if (Directory.Exists(silkRoot)) Directory.Delete(silkRoot, true);
        }
    }

    [Fact]
    public void Overlay_RenameLowerFile_InSilkfsUpper_PersistsRenameAcrossRemount()
    {
        var silkRoot = Path.Combine(Path.GetTempPath(), $"silkfs-rename-lower-{Guid.NewGuid():N}");
        try
        {
            var index = new LayerIndex();
            index.AddEntry(new LayerIndexEntry("/lib", InodeType.Directory, 0x1ED));
            index.AddEntry(new LayerIndexEntry(
                "/lib/libfoo.so.1",
                InodeType.File,
                0x1A4,
                Size: 3,
                InlineData: Encoding.UTF8.GetBytes("foo")));

            using (var engine = new Engine())
            {
                var sm = new SyscallManager(engine, new VMAManager(), 0);
                var layerType = FileSystemRegistry.Get("layerfs")!;
                var lowerSb = layerType.CreateAnonymousFileSystem().ReadSuper(
                    layerType,
                    0,
                    "test-lower",
                    new LayerMountOptions { Index = index, ContentProvider = new InMemoryLayerContentProvider() });
                sm.MountRootOverlayWithLower(lowerSb, "silkfs", silkRoot);

                var libLoc = sm.PathWalkWithFlags("/lib", LookupFlags.FollowSymlink);
                Assert.True(libLoc.IsValid);
                libLoc.Dentry!.Inode!.Rename("libfoo.so.1", libLoc.Dentry.Inode!, "libbar.so.1");
                Assert.Null(libLoc.Dentry.Inode.Lookup("libfoo.so.1"));
                Assert.NotNull(libLoc.Dentry.Inode.Lookup("libbar.so.1"));

                sm.Close();
            }

            using (var engine = new Engine())
            {
                var sm = new SyscallManager(engine, new VMAManager(), 0);
                var layerType = FileSystemRegistry.Get("layerfs")!;
                var lowerSb = layerType.CreateAnonymousFileSystem().ReadSuper(
                    layerType,
                    0,
                    "test-lower",
                    new LayerMountOptions { Index = index, ContentProvider = new InMemoryLayerContentProvider() });
                sm.MountRootOverlayWithLower(lowerSb, "silkfs", silkRoot);

                var libLoc = sm.PathWalkWithFlags("/lib", LookupFlags.FollowSymlink);
                Assert.True(libLoc.IsValid);
                Assert.Null(libLoc.Dentry!.Inode!.Lookup("libfoo.so.1"));
                Assert.NotNull(libLoc.Dentry.Inode.Lookup("libbar.so.1"));

                sm.Close();
            }
        }
        finally
        {
            if (Directory.Exists(silkRoot)) Directory.Delete(silkRoot, true);
        }
    }

    [Fact]
    public void Overlay_ModifyLowerRegularFile_InSilkfsUpper_PersistsAcrossRemount()
    {
        var silkRoot = Path.Combine(Path.GetTempPath(), $"silkfs-copyup-write-{Guid.NewGuid():N}");
        try
        {
            var payload = Encoding.UTF8.GetBytes("abcdef");
            var index = new LayerIndex();
            index.AddEntry(new LayerIndexEntry("/etc", InodeType.Directory, 0x1ED));
            index.AddEntry(new LayerIndexEntry(
                "/etc/config.txt",
                InodeType.File,
                0x1A4,
                Size: (ulong)payload.Length,
                InlineData: payload));

            using (var engine = new Engine())
            {
                var sm = new SyscallManager(engine, new VMAManager(), 0);
                var layerType = FileSystemRegistry.Get("layerfs")!;
                var lowerSb = layerType.CreateAnonymousFileSystem().ReadSuper(
                    layerType,
                    0,
                    "test-lower",
                    new LayerMountOptions { Index = index, ContentProvider = new InMemoryLayerContentProvider() });
                sm.MountRootOverlayWithLower(lowerSb, "silkfs", silkRoot);

                var fileLoc = sm.PathWalkWithFlags("/etc/config.txt", LookupFlags.FollowSymlink);
                Assert.True(fileLoc.IsValid);
                var wf = new LinuxFile(fileLoc.Dentry!, FileFlags.O_RDWR, fileLoc.Mount!);
                Assert.Equal(2, fileLoc.Dentry!.Inode!.WriteFromHost(null, wf, "ZZ"u8.ToArray(), 2));
                wf.Close();

                sm.Close();
            }

            using (var engine = new Engine())
            {
                var sm = new SyscallManager(engine, new VMAManager(), 0);
                var layerType = FileSystemRegistry.Get("layerfs")!;
                var lowerSb = layerType.CreateAnonymousFileSystem().ReadSuper(
                    layerType,
                    0,
                    "test-lower",
                    new LayerMountOptions { Index = index, ContentProvider = new InMemoryLayerContentProvider() });
                sm.MountRootOverlayWithLower(lowerSb, "silkfs", silkRoot);

                var fileLoc = sm.PathWalkWithFlags("/etc/config.txt", LookupFlags.FollowSymlink);
                Assert.True(fileLoc.IsValid);
                var rf = new LinuxFile(fileLoc.Dentry!, FileFlags.O_RDONLY, fileLoc.Mount!);
                var buf = new byte[16];
                var n = fileLoc.Dentry!.Inode!.ReadToHost(null, rf, buf, 0);
                rf.Close();
                Assert.Equal(6, n);
                Assert.Equal("abZZef", Encoding.UTF8.GetString(buf, 0, n));

                sm.Close();
            }
        }
        finally
        {
            if (Directory.Exists(silkRoot)) Directory.Delete(silkRoot, true);
        }
    }

    [Fact]
    public void Overlay_MknodCharDevice_InSilkfsUpper_PersistsAcrossRemount()
    {
        var silkRoot = Path.Combine(Path.GetTempPath(), $"silkfs-mknod-char-{Guid.NewGuid():N}");
        try
        {
            var index = new LayerIndex();
            index.AddEntry(new LayerIndexEntry("/dev", InodeType.Directory, 0x1ED));
            const uint rdev = 0x0103;

            using (var engine = new Engine())
            {
                var sm = new SyscallManager(engine, new VMAManager(), 0);
                var layerType = FileSystemRegistry.Get("layerfs")!;
                var lowerSb = layerType.CreateAnonymousFileSystem().ReadSuper(
                    layerType,
                    0,
                    "test-lower",
                    new LayerMountOptions { Index = index, ContentProvider = new InMemoryLayerContentProvider() });
                sm.MountRootOverlayWithLower(lowerSb, "silkfs", silkRoot);

                var devLoc = sm.PathWalkWithFlags("/dev", LookupFlags.FollowSymlink);
                Assert.True(devLoc.IsValid);
                var nullNode = new Dentry("null", null, devLoc.Dentry, devLoc.Dentry!.SuperBlock);
                devLoc.Dentry.Inode!.Mknod(nullNode, 0x1B6, 0, 0, InodeType.CharDev, rdev);

                var created = devLoc.Dentry.Inode.Lookup("null");
                Assert.NotNull(created);
                Assert.Equal(InodeType.CharDev, created!.Inode!.Type);
                Assert.Equal(rdev, created.Inode.Rdev);

                sm.Close();
            }

            using (var engine = new Engine())
            {
                var sm = new SyscallManager(engine, new VMAManager(), 0);
                var layerType = FileSystemRegistry.Get("layerfs")!;
                var lowerSb = layerType.CreateAnonymousFileSystem().ReadSuper(
                    layerType,
                    0,
                    "test-lower",
                    new LayerMountOptions { Index = index, ContentProvider = new InMemoryLayerContentProvider() });
                sm.MountRootOverlayWithLower(lowerSb, "silkfs", silkRoot);

                var nullLoc = sm.PathWalk("/dev/null");
                Assert.True(nullLoc.IsValid);
                Assert.Equal(InodeType.CharDev, nullLoc.Dentry!.Inode!.Type);
                Assert.Equal(rdev, nullLoc.Dentry.Inode.Rdev);

                sm.Close();
            }
        }
        finally
        {
            if (Directory.Exists(silkRoot)) Directory.Delete(silkRoot, true);
        }
    }

    [Fact]
    public void Overlay_MknodFifo_InSilkfsUpper_PersistsAcrossRemount()
    {
        var silkRoot = Path.Combine(Path.GetTempPath(), $"silkfs-mknod-fifo-{Guid.NewGuid():N}");
        try
        {
            var index = new LayerIndex();
            index.AddEntry(new LayerIndexEntry("/run", InodeType.Directory, 0x1ED));

            using (var engine = new Engine())
            {
                var sm = new SyscallManager(engine, new VMAManager(), 0);
                var layerType = FileSystemRegistry.Get("layerfs")!;
                var lowerSb = layerType.CreateAnonymousFileSystem().ReadSuper(
                    layerType,
                    0,
                    "test-lower",
                    new LayerMountOptions { Index = index, ContentProvider = new InMemoryLayerContentProvider() });
                sm.MountRootOverlayWithLower(lowerSb, "silkfs", silkRoot);

                var runLoc = sm.PathWalkWithFlags("/run", LookupFlags.FollowSymlink);
                Assert.True(runLoc.IsValid);
                var fifoNode = new Dentry("apk.pipe", null, runLoc.Dentry, runLoc.Dentry!.SuperBlock);
                runLoc.Dentry.Inode!.Mknod(fifoNode, 0x1B6, 0, 0, InodeType.Fifo, 0);

                var created = runLoc.Dentry.Inode.Lookup("apk.pipe");
                Assert.NotNull(created);
                Assert.Equal(InodeType.Fifo, created!.Inode!.Type);

                sm.Close();
            }

            using (var engine = new Engine())
            {
                var sm = new SyscallManager(engine, new VMAManager(), 0);
                var layerType = FileSystemRegistry.Get("layerfs")!;
                var lowerSb = layerType.CreateAnonymousFileSystem().ReadSuper(
                    layerType,
                    0,
                    "test-lower",
                    new LayerMountOptions { Index = index, ContentProvider = new InMemoryLayerContentProvider() });
                sm.MountRootOverlayWithLower(lowerSb, "silkfs", silkRoot);

                var fifoLoc = sm.PathWalk("/run/apk.pipe");
                Assert.True(fifoLoc.IsValid);
                Assert.Equal(InodeType.Fifo, fifoLoc.Dentry!.Inode!.Type);

                sm.Close();
            }
        }
        finally
        {
            if (Directory.Exists(silkRoot)) Directory.Delete(silkRoot, true);
        }
    }

    [Fact]
    public void Overlay_CreateSymlink_InSilkfsUpper_PersistsAcrossRemount()
    {
        var silkRoot = Path.Combine(Path.GetTempPath(), $"silkfs-create-symlink-{Guid.NewGuid():N}");
        try
        {
            var index = new LayerIndex();
            index.AddEntry(new LayerIndexEntry("/bin", InodeType.Directory, 0x1ED));
            index.AddEntry(new LayerIndexEntry(
                "/bin/busybox",
                InodeType.File,
                0x1ED,
                Size: 7,
                InlineData: Encoding.UTF8.GetBytes("busybox")));

            using (var engine = new Engine())
            {
                var sm = new SyscallManager(engine, new VMAManager(), 0);
                var layerType = FileSystemRegistry.Get("layerfs")!;
                var lowerSb = layerType.CreateAnonymousFileSystem().ReadSuper(
                    layerType,
                    0,
                    "test-lower",
                    new LayerMountOptions { Index = index, ContentProvider = new InMemoryLayerContentProvider() });
                sm.MountRootOverlayWithLower(lowerSb, "silkfs", silkRoot);

                var binLoc = sm.PathWalkWithFlags("/bin", LookupFlags.FollowSymlink);
                Assert.True(binLoc.IsValid);
                var shNode = new Dentry("sh", null, binLoc.Dentry, binLoc.Dentry!.SuperBlock);
                binLoc.Dentry.Inode!.Symlink(shNode, "/bin/busybox", 0, 0);

                var created = binLoc.Dentry.Inode.Lookup("sh");
                Assert.NotNull(created);
                Assert.Equal(InodeType.Symlink, created!.Inode!.Type);
                Assert.Equal(0, created.Inode.Readlink(out var createdShTarget));
                Assert.Equal("/bin/busybox", createdShTarget);

                sm.Close();
            }

            using (var engine = new Engine())
            {
                var sm = new SyscallManager(engine, new VMAManager(), 0);
                var layerType = FileSystemRegistry.Get("layerfs")!;
                var lowerSb = layerType.CreateAnonymousFileSystem().ReadSuper(
                    layerType,
                    0,
                    "test-lower",
                    new LayerMountOptions { Index = index, ContentProvider = new InMemoryLayerContentProvider() });
                sm.MountRootOverlayWithLower(lowerSb, "silkfs", silkRoot);

                var shLoc = sm.PathWalkWithFlags("/bin/sh", LookupFlags.None);
                Assert.True(shLoc.IsValid);
                Assert.Equal(InodeType.Symlink, shLoc.Dentry!.Inode!.Type);
                Assert.Equal(0, shLoc.Dentry.Inode.Readlink(out var shTarget));
                Assert.Equal("/bin/busybox", shTarget);

                sm.Close();
            }
        }
        finally
        {
            if (Directory.Exists(silkRoot)) Directory.Delete(silkRoot, true);
        }
    }

    [Fact]
    public void Overlay_OpaqueDirectoryMarker_InSilkfsUpper_HidesLowerChildrenAcrossRemount()
    {
        var silkRoot = Path.Combine(Path.GetTempPath(), $"silkfs-opaque-dir-{Guid.NewGuid():N}");
        try
        {
            var index = new LayerIndex();
            index.AddEntry(new LayerIndexEntry("/etc", InodeType.Directory, 0x1ED));
            index.AddEntry(new LayerIndexEntry("/etc/apk", InodeType.Directory, 0x1ED));
            index.AddEntry(new LayerIndexEntry(
                "/etc/apk/world",
                InodeType.File,
                0x1A4,
                Size: 5,
                InlineData: Encoding.UTF8.GetBytes("world")));
            index.AddEntry(new LayerIndexEntry(
                "/etc/apk/repositories",
                InodeType.File,
                0x1A4,
                Size: 4,
                InlineData: Encoding.UTF8.GetBytes("repo")));

            using (var engine = new Engine())
            {
                var sm = new SyscallManager(engine, new VMAManager(), 0);
                var layerType = FileSystemRegistry.Get("layerfs")!;
                var lowerSb = layerType.CreateAnonymousFileSystem().ReadSuper(
                    layerType,
                    0,
                    "test-lower",
                    new LayerMountOptions { Index = index, ContentProvider = new InMemoryLayerContentProvider() });
                sm.MountRootOverlayWithLower(lowerSb, "silkfs", silkRoot);

                var apkLoc = sm.PathWalkWithFlags("/etc/apk", LookupFlags.FollowSymlink);
                Assert.True(apkLoc.IsValid);

                var opaque = new Dentry(".wh..wh..opq", null, apkLoc.Dentry, apkLoc.Dentry!.SuperBlock);
                apkLoc.Dentry.Inode!.Mknod(opaque, 0x1B6, 0, 0, InodeType.CharDev, 0);

                Assert.Null(apkLoc.Dentry.Inode.Lookup("world"));
                Assert.Null(apkLoc.Dentry.Inode.Lookup("repositories"));

                sm.Close();
            }

            using (var engine = new Engine())
            {
                var sm = new SyscallManager(engine, new VMAManager(), 0);
                var layerType = FileSystemRegistry.Get("layerfs")!;
                var lowerSb = layerType.CreateAnonymousFileSystem().ReadSuper(
                    layerType,
                    0,
                    "test-lower",
                    new LayerMountOptions { Index = index, ContentProvider = new InMemoryLayerContentProvider() });
                sm.MountRootOverlayWithLower(lowerSb, "silkfs", silkRoot);

                var apkLoc = sm.PathWalkWithFlags("/etc/apk", LookupFlags.FollowSymlink);
                Assert.True(apkLoc.IsValid);
                Assert.Null(apkLoc.Dentry!.Inode!.Lookup("world"));
                Assert.Null(apkLoc.Dentry.Inode.Lookup("repositories"));

                sm.Close();
            }
        }
        finally
        {
            if (Directory.Exists(silkRoot)) Directory.Delete(silkRoot, true);
        }
    }
}