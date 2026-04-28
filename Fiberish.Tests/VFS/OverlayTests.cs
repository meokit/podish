using System.Runtime.InteropServices;
using System.Text;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.VFS;

public class OverlayTests
{
    private static readonly Lazy<bool> HostFileSymlinkCreationSupported = new(ProbeHostFileSymlinkCreationSupport);

    [Fact]
    public void OverlayRoot_ShrinkMode2_DoesNotFallbackToOverlayMountPointAfterDrop()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var mountSource = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(rootDir);
        Directory.CreateDirectory(mountSource);
        File.WriteAllText(Path.Combine(mountSource, "hello.txt"), "hello");

        try
        {
            var runtime = KernelRuntime.Bootstrap(rootDir, false, true);
            var sm = runtime.Syscalls;

            var root = sm.Root.Dentry!;
            var hold = root.Inode!.Lookup("hold");
            if (hold == null)
            {
                var holdDentry = new Dentry(FsName.FromString("hold"), null, root, root.SuperBlock);
                Assert.Equal(0, root.Inode.Mkdir(holdDentry, 0x1FF, 0, 0));
                root.CacheChild(holdDentry, "OverlayTests.setup-hold");
                hold = holdDentry;
            }

            var mnt = hold.Inode!.Lookup("mnt");
            if (mnt == null)
            {
                var mntDentry = new Dentry(FsName.FromString("mnt"), null, hold, hold.SuperBlock);
                Assert.Equal(0, hold.Inode.Mkdir(mntDentry, 0x1FF, 0, 0));
                hold.CacheChild(mntDentry, "OverlayTests.setup-mnt");
            }

            sm.MountHostfs(mountSource, "/hold/mnt");

            var before = sm.PathWalk("/hold/mnt/hello.txt");
            Assert.True(before.IsValid);
            Assert.Equal("hostfs", before.Mount!.FsType);
            Assert.Equal("hello", ReadAll(before));

            _ = VfsShrinker.Shrink(sm, VfsShrinkMode.DentryCache | VfsShrinkMode.InodeCache);

            var after = sm.PathWalk("/hold/mnt/hello.txt");
            Assert.True(after.IsValid);
            Assert.Equal("hostfs", after.Mount!.FsType);
            Assert.Equal("hello", ReadAll(after));
        }
        finally
        {
            if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true);
            if (Directory.Exists(mountSource)) Directory.Delete(mountSource, true);
        }
    }

    [Fact]
    public void OverlayFlock_LowerOnlyLayerFile_ShouldNotReturnEnosys()
    {
        var lowerFs = new LayerFileSystem();
        var lowerRoot = LayerNode.Directory("/")
            .AddChild(LayerNode.Directory("lib")
                .AddChild(LayerNode.Directory("apk")
                    .AddChild(LayerNode.Directory("db")
                        .AddChild(LayerNode.File("lock", [])))));
        var lowerSb = lowerFs.ReadSuper(
            new FileSystemType { Name = "layerfs" },
            0,
            "layer-lower",
            new LayerMountOptions { Root = lowerRoot });

        var upperFsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var upperSb = upperFsType.CreateAnonymousFileSystem().ReadSuper(upperFsType, 0, "ovl-upper-flock", null);

        var overlayFs = new OverlayFileSystem();
        var overlaySb = (OverlaySuperBlock)overlayFs.ReadSuper(
            new FileSystemType { Name = "overlay" },
            0,
            "overlay",
            new OverlayMountOptions { Lower = lowerSb, Upper = upperSb });

        var lib = overlaySb.Root.Inode!.Lookup("lib");
        Assert.NotNull(lib);
        var apk = lib!.Inode!.Lookup("apk");
        Assert.NotNull(apk);
        var db = apk!.Inode!.Lookup("db");
        Assert.NotNull(db);
        var lockFile = db!.Inode!.Lookup("lock");
        Assert.NotNull(lockFile);

        var file = new LinuxFile(lockFile!, FileFlags.O_RDWR, null!);
        try
        {
            var inode = Assert.IsType<OverlayInode>(lockFile!.Inode);
            var ex = inode.Flock(file, LinuxConstants.LOCK_EX | LinuxConstants.LOCK_NB);
            Assert.Equal(0, ex);

            var un = inode.Flock(file, LinuxConstants.LOCK_UN);
            Assert.Equal(0, un);
        }
        finally
        {
            file.Close();
        }
    }

    [Fact]
    public void TestRecursiveCopyUpWithHostfs()
    {
        var tempLower = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var tempUpper = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var tempWork = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        Directory.CreateDirectory(tempLower);
        Directory.CreateDirectory(tempUpper);
        Directory.CreateDirectory(tempWork);

        try
        {
            var nestedDir = Path.Combine(tempLower, "a/b/c");
            Directory.CreateDirectory(nestedDir);
            var filePath = Path.Combine(nestedDir, "file");
            File.WriteAllText(filePath, "hello");

            var fsType = new FileSystemType { Name = "hostfs" };
            var opts = HostfsMountOptions.Parse("rw");
            var lowerSb = new HostSuperBlock(fsType, tempLower, opts);
            lowerSb.Root = lowerSb.GetDentry(tempLower, FsName.Empty, null)!;

            var upperSb = new HostSuperBlock(fsType, tempUpper, opts);
            upperSb.Root = upperSb.GetDentry(tempUpper, FsName.Empty, null)!;

            var overlayFs = new OverlayFileSystem();
            var options = new OverlayMountOptions { Lower = lowerSb, Upper = upperSb };
            var overlaySb =
                (OverlaySuperBlock)overlayFs.ReadSuper(new FileSystemType { Name = "overlay" }, 0, "overlay", options);

            // Lookup the file in overlay
            var root = overlaySb.Root;
            var a_ov = root.Inode!.Lookup("a");
            var b_ov = a_ov!.Inode!.Lookup("b");
            var c_ov = b_ov!.Inode!.Lookup("c");
            var file_ov = c_ov!.Inode!.Lookup("file")!;

            var overlayInode = file_ov.Inode as OverlayInode;
            Assert.NotNull(overlayInode);
            Assert.Null(overlayInode.UpperDentry);

            // Open the file as O_WRONLY
            var linuxFile = new LinuxFile(file_ov, FileFlags.O_WRONLY, null!);
            overlayInode.Open(linuxFile);
            var initialHandle = linuxFile.PrivateData;
            Assert.NotNull(initialHandle);

            // Trigger CopyUp via Write
            overlayInode.WriteFromHost(null, linuxFile, "world"u8.ToArray(), 5);

            Assert.NotNull(overlayInode.UpperDentry);
            Assert.NotEqual(initialHandle, linuxFile.PrivateData); // Handle should have been redirected

            // Check if parents were created in upper FS host path
            Assert.True(Directory.Exists(Path.Combine(tempUpper, "a/b/c")));
            Assert.True(File.Exists(Path.Combine(tempUpper, "a/b/c/file")));

            // Verify content in upper
            Assert.Equal("helloworld",
                ReadAllTextWithUnixCompatibleSharing(Path.Combine(tempUpper, "a/b/c/file")));
        }
        finally
        {
            if (Directory.Exists(tempLower)) Directory.Delete(tempLower, true);
            if (Directory.Exists(tempUpper)) Directory.Delete(tempUpper, true);
            if (Directory.Exists(tempWork)) Directory.Delete(tempWork, true);
        }
    }

    [Fact]
    public void OverlayCopyUp_PageCacheMustNotAlias_WhenLowerAndUpperInoMatch()
    {
        var tempLower = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var tempUpper = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempLower);
        Directory.CreateDirectory(tempUpper);

        try
        {
            var filePath = Path.Combine(tempLower, "file");
            File.WriteAllText(filePath, "lower");

            var fsType = new FileSystemType { Name = "hostfs" };
            var opts = HostfsMountOptions.Parse("rw");
            var lowerSb = new HostSuperBlock(fsType, tempLower, opts);
            lowerSb.Root = lowerSb.GetDentry(tempLower, FsName.Empty, null)!;

            var upperSb = new HostSuperBlock(fsType, tempUpper, opts);
            upperSb.Root = upperSb.GetDentry(tempUpper, FsName.Empty, null)!;

            var overlayFs = new OverlayFileSystem();
            var overlaySb = (OverlaySuperBlock)overlayFs.ReadSuper(
                new FileSystemType { Name = "overlay" },
                0,
                "overlay",
                new OverlayMountOptions { Lower = lowerSb, Upper = upperSb });

            var fileOv = overlaySb.Root.Inode!.Lookup("file")!;
            var overlayInode = Assert.IsType<OverlayInode>(fileOv.Inode);
            Assert.NotNull(overlayInode.LowerInode);
            Assert.Null(overlayInode.UpperInode);

            // Trigger copy-up, so the same overlay inode now points to upper.
            var linuxFile = new LinuxFile(fileOv, FileFlags.O_WRONLY, null!);
            var rc = overlayInode.WriteFromHost(null, linuxFile, "x"u8.ToArray(), 0);
            Assert.True(rc >= 0);
            Assert.NotNull(overlayInode.UpperInode);

            var lowerInode = overlayInode.LowerInode!;
            var upperInode = overlayInode.UpperInode!;

            // Force a worst-case ino collision that used to alias page cache keys.
            const ulong forcedIno = 12345;
            lowerInode.Ino = forcedIno;
            upperInode.Ino = forcedIno;

            var lowerCache = ((MappingBackedInode)lowerInode).AcquireMappingRef();
            var upperCache = ((MappingBackedInode)upperInode).AcquireMappingRef();

            try
            {
                Assert.NotSame(lowerCache, upperCache);

                lowerCache.GetOrCreatePage(0, ptr =>
                {
                    Marshal.Copy("LOWER"u8.ToArray(), 0, ptr, 5);
                    return true;
                }, out var lowerIsNew);
                if (!lowerIsNew)
                    Marshal.Copy("LOWER"u8.ToArray(), 0, lowerCache.GetPage(0), 5);

                upperCache.GetOrCreatePage(0, ptr =>
                {
                    Marshal.Copy("UPPER"u8.ToArray(), 0, ptr, 5);
                    return true;
                }, out var upperIsNew);
                if (!upperIsNew)
                    Marshal.Copy("UPPER"u8.ToArray(), 0, upperCache.GetPage(0), 5);

                var lowerBuf = new byte[5];
                var upperBuf = new byte[5];
                Marshal.Copy(lowerCache.GetPage(0), lowerBuf, 0, 5);
                Marshal.Copy(upperCache.GetPage(0), upperBuf, 0, 5);

                Assert.Equal("LOWER", Encoding.ASCII.GetString(lowerBuf));
                Assert.Equal("UPPER", Encoding.ASCII.GetString(upperBuf));
            }
            finally
            {
                lowerCache.Release();
                upperCache.Release();
            }
        }
        finally
        {
            if (Directory.Exists(tempLower)) Directory.Delete(tempLower, true);
            if (Directory.Exists(tempUpper)) Directory.Delete(tempUpper, true);
        }
    }

    [Fact]
    public void OverlaySymlink_InLowerOnlyDirectory_ShouldCreateInUpper()
    {
        if (!SupportsHostfsSymlinkCreation())
            return;

        var tempLower = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var tempUpper = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempLower);
        Directory.CreateDirectory(tempUpper);

        try
        {
            Directory.CreateDirectory(Path.Combine(tempLower, "usr/lib"));

            var fsType = new FileSystemType { Name = "hostfs" };
            var opts = HostfsMountOptions.Parse("rw");
            var lowerSb = new HostSuperBlock(fsType, tempLower, opts);
            lowerSb.Root = lowerSb.GetDentry(tempLower, FsName.Empty, null)!;

            var upperSb = new HostSuperBlock(fsType, tempUpper, opts);
            upperSb.Root = upperSb.GetDentry(tempUpper, FsName.Empty, null)!;

            var overlayFs = new OverlayFileSystem();
            var overlaySb = (OverlaySuperBlock)overlayFs.ReadSuper(
                new FileSystemType { Name = "overlay" },
                0,
                "overlay",
                new OverlayMountOptions { Lower = lowerSb, Upper = upperSb });

            var usrOv = overlaySb.Root.Inode!.Lookup("usr")!;
            var libOv = usrOv.Inode!.Lookup("lib")!;
            var libInode = Assert.IsType<OverlayInode>(libOv.Inode);
            Assert.Null(libInode.UpperDentry);

            var linkDentry = new Dentry(FsName.FromString("libbz2.so.1"), null, libOv, overlaySb);
            Assert.Equal(0, libInode.Symlink(linkDentry, "libbz2.so.1.0.8", 0, 0));

            var created = libOv.Inode!.Lookup("libbz2.so.1");
            Assert.NotNull(created);
            Assert.Equal(InodeType.Symlink, created!.Inode!.Type);
            Assert.Equal(0, created.Inode.Readlink(out byte[]? createdTarget));
            Assert.Equal("libbz2.so.1.0.8"u8.ToArray(), createdTarget);
            Assert.NotNull(libInode.UpperDentry);
            Assert.Equal(0, libInode.UpperInode!.Lookup("libbz2.so.1")!.Inode!.Readlink(out byte[]? upperTarget));
            Assert.Equal("libbz2.so.1.0.8"u8.ToArray(), upperTarget);
        }
        finally
        {
            if (Directory.Exists(tempLower)) Directory.Delete(tempLower, true);
            if (Directory.Exists(tempUpper)) Directory.Delete(tempUpper, true);
        }
    }

    [Fact]
    public void OverlayMknod_InLowerOnlyDirectory_ShouldCreateInUpper()
    {
        var tempLower = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempLower);

        try
        {
            Directory.CreateDirectory(Path.Combine(tempLower, "tmp"));

            var hostType = new FileSystemType { Name = "hostfs" };
            var hostOpts = HostfsMountOptions.Parse("rw");
            var lowerSb = new HostSuperBlock(hostType, tempLower, hostOpts);
            lowerSb.Root = lowerSb.GetDentry(tempLower, FsName.Empty, null)!;

            var tmpType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
            var upperSb = tmpType.CreateAnonymousFileSystem().ReadSuper(tmpType, 0, "ovl-upper-mknod", null);

            var overlayFs = new OverlayFileSystem();
            var overlaySb = (OverlaySuperBlock)overlayFs.ReadSuper(
                new FileSystemType { Name = "overlay" },
                0,
                "overlay",
                new OverlayMountOptions { Lower = lowerSb, Upper = upperSb });

            var tmpOv = overlaySb.Root.Inode!.Lookup("tmp");
            Assert.NotNull(tmpOv);
            var tmpInode = Assert.IsType<OverlayInode>(tmpOv!.Inode);
            Assert.Null(tmpInode.UpperDentry);

            var node = new Dentry(FsName.FromString("devnode"), null, tmpOv, overlaySb);
            Assert.Equal(0, tmpInode.Mknod(node, 0x180, 0, 0, InodeType.CharDev, (1u << 8) | 3u));

            var created = tmpOv.Inode!.Lookup("devnode");
            Assert.NotNull(created);
            Assert.Equal(InodeType.CharDev, created!.Inode!.Type);
            Assert.Equal((1u << 8) | 3u, created.Inode.Rdev);
            Assert.NotNull(tmpInode.UpperDentry);
            Assert.NotNull(tmpInode.UpperInode!.Lookup("devnode"));
        }
        finally
        {
            if (Directory.Exists(tempLower)) Directory.Delete(tempLower, true);
        }
    }

    [Fact]
    public void OverlayLookup_MultipleLowers_PicksTopmostLower()
    {
        var tempLowerTop = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var tempLowerBottom = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var tempUpper = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempLowerTop);
        Directory.CreateDirectory(tempLowerBottom);
        Directory.CreateDirectory(tempUpper);

        try
        {
            File.WriteAllText(Path.Combine(tempLowerTop, "same.txt"), "top");
            File.WriteAllText(Path.Combine(tempLowerBottom, "same.txt"), "bottom");

            var fsType = new FileSystemType { Name = "hostfs" };
            var opts = HostfsMountOptions.Parse("rw");
            var lowerTopSb = new HostSuperBlock(fsType, tempLowerTop, opts);
            lowerTopSb.Root = lowerTopSb.GetDentry(tempLowerTop, FsName.Empty, null)!;
            var lowerBottomSb = new HostSuperBlock(fsType, tempLowerBottom, opts);
            lowerBottomSb.Root = lowerBottomSb.GetDentry(tempLowerBottom, FsName.Empty, null)!;
            var upperSb = new HostSuperBlock(fsType, tempUpper, opts);
            upperSb.Root = upperSb.GetDentry(tempUpper, FsName.Empty, null)!;

            var overlayFs = new OverlayFileSystem();
            var overlaySb = (OverlaySuperBlock)overlayFs.ReadSuper(
                new FileSystemType { Name = "overlay" },
                0,
                "overlay",
                new OverlayMountOptions
                {
                    Lowers = [lowerTopSb, lowerBottomSb],
                    Upper = upperSb
                });

            var d = overlaySb.Root.Inode!.Lookup("same.txt");
            Assert.NotNull(d);
            var f = new LinuxFile(d!, FileFlags.O_RDONLY, null!);
            var buf = new byte[16];
            var n = d!.Inode!.ReadToHost(null, f, buf, 0);
            Assert.True(n > 0);
            Assert.Equal("top", Encoding.UTF8.GetString(buf, 0, n));
            f.Close();
        }
        finally
        {
            if (Directory.Exists(tempLowerTop)) Directory.Delete(tempLowerTop, true);
            if (Directory.Exists(tempLowerBottom)) Directory.Delete(tempLowerBottom, true);
            if (Directory.Exists(tempUpper)) Directory.Delete(tempUpper, true);
        }
    }

    [Fact]
    public void OverlayGetEntries_MultipleLowers_MergesAndOverridesByHigherLower()
    {
        var tempLowerTop = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var tempLowerBottom = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var tempUpper = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempLowerTop);
        Directory.CreateDirectory(tempLowerBottom);
        Directory.CreateDirectory(tempUpper);

        try
        {
            File.WriteAllText(Path.Combine(tempLowerTop, "same.txt"), "top");
            File.WriteAllText(Path.Combine(tempLowerTop, "top-only.txt"), "t");
            File.WriteAllText(Path.Combine(tempLowerBottom, "same.txt"), "bottom");
            File.WriteAllText(Path.Combine(tempLowerBottom, "bottom-only.txt"), "b");

            var fsType = new FileSystemType { Name = "hostfs" };
            var opts = HostfsMountOptions.Parse("rw");
            var lowerTopSb = new HostSuperBlock(fsType, tempLowerTop, opts);
            lowerTopSb.Root = lowerTopSb.GetDentry(tempLowerTop, FsName.Empty, null)!;
            var lowerBottomSb = new HostSuperBlock(fsType, tempLowerBottom, opts);
            lowerBottomSb.Root = lowerBottomSb.GetDentry(tempLowerBottom, FsName.Empty, null)!;
            var upperSb = new HostSuperBlock(fsType, tempUpper, opts);
            upperSb.Root = upperSb.GetDentry(tempUpper, FsName.Empty, null)!;

            var overlayFs = new OverlayFileSystem();
            var overlaySb = (OverlaySuperBlock)overlayFs.ReadSuper(
                new FileSystemType { Name = "overlay" },
                0,
                "overlay",
                new OverlayMountOptions
                {
                    Lowers = [lowerTopSb, lowerBottomSb],
                    Upper = upperSb
                });

            var names = overlaySb.Root.Inode!.GetEntries().Select(e => e.Name).ToHashSet();
            Assert.Contains("same.txt", names);
            Assert.Contains("top-only.txt", names);
            Assert.Contains("bottom-only.txt", names);
        }
        finally
        {
            if (Directory.Exists(tempLowerTop)) Directory.Delete(tempLowerTop, true);
            if (Directory.Exists(tempLowerBottom)) Directory.Delete(tempLowerBottom, true);
            if (Directory.Exists(tempUpper)) Directory.Delete(tempUpper, true);
        }
    }

    [Fact]
    public void OverlayUnlink_LowerOnlyEntry_CreatesLogicalWhiteout()
    {
        var tempLower = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var tempUpper = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempLower);
        Directory.CreateDirectory(tempUpper);

        try
        {
            File.WriteAllText(Path.Combine(tempLower, "gone.txt"), "x");

            var fsType = new FileSystemType { Name = "hostfs" };
            var opts = HostfsMountOptions.Parse("rw");
            var lowerSb = new HostSuperBlock(fsType, tempLower, opts);
            lowerSb.Root = lowerSb.GetDentry(tempLower, FsName.Empty, null)!;
            var upperSb = new HostSuperBlock(fsType, tempUpper, opts);
            upperSb.Root = upperSb.GetDentry(tempUpper, FsName.Empty, null)!;

            var overlayFs = new OverlayFileSystem();
            var overlaySb = (OverlaySuperBlock)overlayFs.ReadSuper(
                new FileSystemType { Name = "overlay" },
                0,
                "overlay",
                new OverlayMountOptions { Lower = lowerSb, Upper = upperSb });

            var rootInode = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);
            Assert.NotNull(rootInode.Lookup("gone.txt"));

            Assert.Equal(0, rootInode.Unlink("gone.txt"));

            Assert.Null(rootInode.Lookup("gone.txt"));
            var names = rootInode.GetEntries().Select(e => e.Name).ToHashSet();
            Assert.DoesNotContain("gone.txt", names);
        }
        finally
        {
            if (Directory.Exists(tempLower)) Directory.Delete(tempLower, true);
            if (Directory.Exists(tempUpper)) Directory.Delete(tempUpper, true);
        }
    }

    [Fact]
    public void OverlayGetEntries_EncodedOpaqueMarker_HidesLowerEntries()
    {
        var tempLower = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var tempUpper = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempLower);
        Directory.CreateDirectory(tempUpper);

        try
        {
            File.WriteAllText(Path.Combine(tempLower, "lower.txt"), "l");
            File.WriteAllText(Path.Combine(tempUpper, ".wh..wh..opq"), "");
            File.WriteAllText(Path.Combine(tempUpper, "upper.txt"), "u");

            var fsType = new FileSystemType { Name = "hostfs" };
            var opts = HostfsMountOptions.Parse("rw");
            var lowerSb = new HostSuperBlock(fsType, tempLower, opts);
            lowerSb.Root = lowerSb.GetDentry(tempLower, FsName.Empty, null)!;
            var upperSb = new HostSuperBlock(fsType, tempUpper, opts);
            upperSb.Root = upperSb.GetDentry(tempUpper, FsName.Empty, null)!;

            var overlayFs = new OverlayFileSystem();
            var overlaySb = (OverlaySuperBlock)overlayFs.ReadSuper(
                new FileSystemType { Name = "overlay" },
                0,
                "overlay",
                new OverlayMountOptions
                {
                    Lowers = [lowerSb],
                    Upper = upperSb
                });

            var rootInode = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);
            var names = rootInode.GetEntries().Select(e => e.Name).ToHashSet();
            Assert.Contains("upper.txt", names);
            Assert.DoesNotContain("lower.txt", names);
        }
        finally
        {
            if (Directory.Exists(tempLower)) Directory.Delete(tempLower, true);
            if (Directory.Exists(tempUpper)) Directory.Delete(tempUpper, true);
        }
    }

    [Fact]
    public void OverlayUnlink_LowerOnlyEntry_WithTmpfsUpper_UsesCharDeviceWhiteout()
    {
        var tempLower = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempLower);

        try
        {
            File.WriteAllText(Path.Combine(tempLower, "gone.txt"), "x");

            var hostType = new FileSystemType { Name = "hostfs" };
            var opts = HostfsMountOptions.Parse("rw");
            var lowerSb = new HostSuperBlock(hostType, tempLower, opts);
            lowerSb.Root = lowerSb.GetDentry(tempLower, FsName.Empty, null)!;

            var tmpType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
            var upperSb = new Tmpfs().ReadSuper(tmpType, 0, "upper", null);

            var overlayFs = new OverlayFileSystem();
            var overlaySb = (OverlaySuperBlock)overlayFs.ReadSuper(
                new FileSystemType { Name = "overlay" },
                0,
                "overlay",
                new OverlayMountOptions { Lower = lowerSb, Upper = upperSb });

            var rootInode = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);
            Assert.Equal(0, rootInode.Unlink("gone.txt"));

            Assert.Null(rootInode.Lookup("gone.txt"));
            Assert.NotNull(rootInode.UpperInode);
            var marker = rootInode.UpperInode!.Lookup("gone.txt");
            Assert.NotNull(marker);
            Assert.Equal(InodeType.CharDev, marker!.Inode!.Type);
            Assert.Equal(0u, marker.Inode.Rdev);
        }
        finally
        {
            if (Directory.Exists(tempLower)) Directory.Delete(tempLower, true);
        }
    }

    [Fact]
    public void OverlayRmdir_LowerOnlyDirectory_UpdatesParentNlinkAndWhiteouts()
    {
        var tempLower = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var tempUpper = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempLower);
        Directory.CreateDirectory(tempUpper);

        try
        {
            Directory.CreateDirectory(Path.Combine(tempLower, "ghost"));

            var fsType = new FileSystemType { Name = "hostfs" };
            var opts = HostfsMountOptions.Parse("rw");
            var lowerSb = new HostSuperBlock(fsType, tempLower, opts);
            lowerSb.Root = lowerSb.GetDentry(tempLower, FsName.Empty, null)!;
            var upperSb = new HostSuperBlock(fsType, tempUpper, opts);
            upperSb.Root = upperSb.GetDentry(tempUpper, FsName.Empty, null)!;

            var overlayFs = new OverlayFileSystem();
            var overlaySb = (OverlaySuperBlock)overlayFs.ReadSuper(
                new FileSystemType { Name = "overlay" },
                0,
                "overlay",
                new OverlayMountOptions { Lower = lowerSb, Upper = upperSb });

            var rootInode = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);
            var before = rootInode.GetLinkCountForStat();
            Assert.NotNull(rootInode.Lookup("ghost"));

            Assert.Equal(0, rootInode.Rmdir("ghost"));

            Assert.Null(rootInode.Lookup("ghost"));
            Assert.Equal(before - 1, rootInode.GetLinkCountForStat());
            Assert.Contains("ghost", overlaySb.GetWhiteouts(new InodeKey(rootInode.Dev, rootInode.Ino)));
        }
        finally
        {
            if (Directory.Exists(tempLower)) Directory.Delete(tempLower, true);
            if (Directory.Exists(tempUpper)) Directory.Delete(tempUpper, true);
        }
    }

    [Fact]
    public void OverlayRename_LowerOnlySourceOverLowerOnlyTarget_HidesOldNameAndOverwrites()
    {
        var tempLower = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var tempUpper = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempLower);
        Directory.CreateDirectory(tempUpper);

        try
        {
            File.WriteAllText(Path.Combine(tempLower, "src.txt"), "src");
            File.WriteAllText(Path.Combine(tempLower, "dst.txt"), "dst");

            var fsType = new FileSystemType { Name = "hostfs" };
            var opts = HostfsMountOptions.Parse("rw");
            var lowerSb = new HostSuperBlock(fsType, tempLower, opts);
            lowerSb.Root = lowerSb.GetDentry(tempLower, FsName.Empty, null)!;
            var upperSb = new HostSuperBlock(fsType, tempUpper, opts);
            upperSb.Root = upperSb.GetDentry(tempUpper, FsName.Empty, null)!;

            var overlayFs = new OverlayFileSystem();
            var overlaySb = (OverlaySuperBlock)overlayFs.ReadSuper(
                new FileSystemType { Name = "overlay" },
                0,
                "overlay",
                new OverlayMountOptions { Lower = lowerSb, Upper = upperSb });

            var rootInode = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);
            Assert.Equal(0, rootInode.Rename("src.txt", rootInode, "dst.txt"));

            Assert.Null(rootInode.Lookup("src.txt"));
            Assert.Contains("src.txt", overlaySb.GetWhiteouts(new InodeKey(rootInode.Dev, rootInode.Ino)));

            var dst = rootInode.Lookup("dst.txt");
            Assert.NotNull(dst);
            var file = new LinuxFile(dst!, FileFlags.O_RDONLY, null!);
            var buf = new byte[8];
            var n = dst!.Inode!.ReadToHost(null, file, buf, 0);
            Assert.Equal("src", Encoding.UTF8.GetString(buf, 0, n));
            file.Close();
        }
        finally
        {
            if (Directory.Exists(tempLower)) Directory.Delete(tempLower, true);
            if (Directory.Exists(tempUpper)) Directory.Delete(tempUpper, true);
        }
    }

    [Fact]
    public void OverlayCopyUp_LowerOnlyDirectoryMutation_DoesNotChangeAncestorNlink()
    {
        if (!SupportsHostfsSymlinkCreation())
            return;

        var tempLower = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var tempUpper = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempLower);
        Directory.CreateDirectory(tempUpper);

        try
        {
            Directory.CreateDirectory(Path.Combine(tempLower, "usr/lib"));

            var fsType = new FileSystemType { Name = "hostfs" };
            var opts = HostfsMountOptions.Parse("rw");
            var lowerSb = new HostSuperBlock(fsType, tempLower, opts);
            lowerSb.Root = lowerSb.GetDentry(tempLower, FsName.Empty, null)!;
            var upperSb = new HostSuperBlock(fsType, tempUpper, opts);
            upperSb.Root = upperSb.GetDentry(tempUpper, FsName.Empty, null)!;

            var overlayFs = new OverlayFileSystem();
            var overlaySb = (OverlaySuperBlock)overlayFs.ReadSuper(
                new FileSystemType { Name = "overlay" },
                0,
                "overlay",
                new OverlayMountOptions { Lower = lowerSb, Upper = upperSb });

            var rootInode = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);
            var usr = rootInode.Lookup("usr");
            Assert.NotNull(usr);
            var usrInode = Assert.IsType<OverlayInode>(usr!.Inode);
            var lib = usrInode.Lookup("lib");
            Assert.NotNull(lib);
            var libInode = Assert.IsType<OverlayInode>(lib!.Inode);

            var rootBefore = rootInode.GetLinkCountForStat();
            var usrBefore = usrInode.GetLinkCountForStat();

            var link = new Dentry(FsName.FromString("libfoo.so.1"), null, lib, overlaySb);
            Assert.Equal(0, libInode.Symlink(link, "libfoo.so.1.0.0", 0, 0));

            Assert.Equal(rootBefore, rootInode.GetLinkCountForStat());
            Assert.Equal(usrBefore, usrInode.GetLinkCountForStat());
            Assert.NotNull(libInode.Lookup("libfoo.so.1"));
        }
        finally
        {
            if (Directory.Exists(tempLower)) Directory.Delete(tempLower, true);
            if (Directory.Exists(tempUpper)) Directory.Delete(tempUpper, true);
        }
    }

    [Fact]
    public void OverlayUpperOnly_UnlinkedOpenFile_TruncateStillWorks()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "ovl-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "ovl-upper", null);

        var overlayFs = new OverlayFileSystem();
        var overlaySb = (OverlaySuperBlock)overlayFs.ReadSuper(
            new FileSystemType { Name = "overlay" },
            0,
            "overlay",
            new OverlayMountOptions { Lower = lowerSb, Upper = upperSb });

        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);
        var fileDentry = new Dentry(FsName.FromString("open-unlink-truncate"), null, overlaySb.Root, overlaySb);
        Assert.Equal(0, root.Create(fileDentry, 0x1A4, 0, 0));
        overlaySb.Root.CacheChild(fileDentry, "OverlayTests.open-unlink-truncate");

        var file = new LinuxFile(fileDentry, FileFlags.O_RDWR, null!);
        try
        {
            Assert.Equal(0, root.Unlink(fileDentry.Name));

            var truncateRc = file.OpenedInode!.Truncate(4096);
            Assert.Equal(0, truncateRc);

            var writeRc = file.OpenedInode.WriteFromHost(null, file, "Z"u8.ToArray(), 0);
            Assert.Equal(1, writeRc);

            var readBuf = new byte[1];
            var readRc = file.OpenedInode.ReadToHost(null, file, readBuf, 0);
            Assert.Equal(1, readRc);
            Assert.Equal((byte)'Z', readBuf[0]);
        }
        finally
        {
            file.Close();
        }
    }

    [Fact]
    public void OverlayUpperOnly_RecreateSameNameAfterUnlink_MustUseNewUpperBacking()
    {
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "ovl-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "ovl-upper", null);

        var overlayFs = new OverlayFileSystem();
        var overlaySb = (OverlaySuperBlock)overlayFs.ReadSuper(
            new FileSystemType { Name = "overlay" },
            0,
            "overlay",
            new OverlayMountOptions { Lower = lowerSb, Upper = upperSb });

        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        var first = new Dentry(FsName.FromString("config.lock"), null, overlaySb.Root, overlaySb);
        Assert.Equal(0, root.Create(first, 0x1A4, 0, 0));
        overlaySb.Root.CacheChild(first, "OverlayTests.recreate-same-name.first");
        Assert.NotNull(first.Inode);

        var firstFile = new LinuxFile(first, FileFlags.O_RDWR, null!);
        try
        {
            Assert.Equal(1, first.Inode!.WriteFromHost(null, firstFile, "A"u8.ToArray(), 0));
        }
        finally
        {
            firstFile.Close();
        }

        Assert.Equal(0, root.Unlink("config.lock"));
        Assert.Null(root.Lookup("config.lock"));

        var second = new Dentry(FsName.FromString("config.lock"), null, overlaySb.Root, overlaySb);
        Assert.Equal(0, root.Create(second, 0x1A4, 0, 0));
        overlaySb.Root.CacheChild(second, "OverlayTests.recreate-same-name.second");
        Assert.NotNull(second.Inode);

        var secondFile = new LinuxFile(second, FileFlags.O_RDWR, null!);
        try
        {
            var writeRc = second.Inode!.WriteFromHost(null, secondFile, "B"u8.ToArray(), 0);
            Assert.Equal(1, writeRc);
        }
        finally
        {
            secondFile.Close();
        }

        var reopened = root.Lookup("config.lock");
        Assert.NotNull(reopened);
        var reader = new LinuxFile(reopened!, FileFlags.O_RDONLY, null!);
        try
        {
            var buf = new byte[1];
            var readRc = reopened!.Inode!.ReadToHost(null, reader, buf, 0);
            Assert.Equal(1, readRc);
            Assert.Equal("B", Encoding.UTF8.GetString(buf, 0, 1));
        }
        finally
        {
            reader.Close();
        }
    }

    [Fact]
    public void OverlayCopyUp_StateIsSharedAcrossMultipleLookups()
    {
        var lowerFs = new LayerFileSystem();
        var lowerRoot = LayerNode.Directory("/")
            .AddChild(LayerNode.File("shared.txt", Encoding.UTF8.GetBytes("lower")));
        var lowerSb = lowerFs.ReadSuper(
            new FileSystemType { Name = "layerfs" },
            0,
            "layer-lower",
            new LayerMountOptions { Root = lowerRoot });

        var upperType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var upperSb = upperType.CreateAnonymousFileSystem().ReadSuper(upperType, 0, "ovl-upper", null);

        var overlayFs = new OverlayFileSystem();
        var overlaySb = (OverlaySuperBlock)overlayFs.ReadSuper(
            new FileSystemType { Name = "overlay" },
            0,
            "overlay",
            new OverlayMountOptions { Lower = lowerSb, Upper = upperSb });

        var first = overlaySb.Root.Inode!.Lookup("shared.txt")!;
        var second = overlaySb.Root.Inode!.Lookup("shared.txt")!;
        var firstInode = Assert.IsType<OverlayInode>(first.Inode);
        var secondInode = Assert.IsType<OverlayInode>(second.Inode);
        Assert.NotSame(firstInode, secondInode);
        Assert.Null(firstInode.UpperDentry);
        Assert.Null(secondInode.UpperDentry);

        var writer = new LinuxFile(first, FileFlags.O_WRONLY, null!);
        try
        {
            var rc = firstInode.WriteFromHost(null, writer, "!"u8.ToArray(), 5);
            Assert.Equal(1, rc);
        }
        finally
        {
            writer.Close();
        }

        Assert.NotNull(firstInode.UpperDentry);
        Assert.NotNull(secondInode.UpperDentry);

        var reader = new LinuxFile(second, FileFlags.O_RDONLY, null!);
        try
        {
            var buf = new byte[6];
            var rc = secondInode.ReadToHost(null, reader, buf, 0);
            Assert.Equal(6, rc);
            Assert.Equal("lower!", Encoding.UTF8.GetString(buf));
        }
        finally
        {
            reader.Close();
        }
    }

    [Fact]
    public void OverlayLowerOnly_RepeatedWrites_ReuseCopiedUpUpperBacking()
    {
        var lowerFs = new LayerFileSystem();
        var lowerRoot = LayerNode.Directory("/")
            .AddChild(LayerNode.File("shared.txt", Encoding.UTF8.GetBytes("lower")));
        var lowerSb = lowerFs.ReadSuper(
            new FileSystemType { Name = "layerfs" },
            0,
            "layer-lower",
            new LayerMountOptions { Root = lowerRoot });

        var upperType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var upperSb = upperType.CreateAnonymousFileSystem().ReadSuper(upperType, 0, "ovl-upper", null);

        var overlayFs = new OverlayFileSystem();
        var overlaySb = (OverlaySuperBlock)overlayFs.ReadSuper(
            new FileSystemType { Name = "overlay" },
            0,
            "overlay",
            new OverlayMountOptions { Lower = lowerSb, Upper = upperSb });

        var firstLookup = overlaySb.Root.Inode!.Lookup("shared.txt")!;
        var secondLookup = overlaySb.Root.Inode!.Lookup("shared.txt")!;
        var firstInode = Assert.IsType<OverlayInode>(firstLookup.Inode);
        var secondInode = Assert.IsType<OverlayInode>(secondLookup.Inode);
        Assert.Null(firstInode.UpperInode);
        Assert.Null(secondInode.UpperInode);

        using var firstFile = new LinuxFile(firstLookup, FileFlags.O_RDWR, null!);
        Assert.Equal(1, firstInode.WriteFromHost(null, firstFile, "!"u8.ToArray(), 5));
        var copiedUpBacking = Assert.IsAssignableFrom<Inode>(firstInode.UpperInode);
        Assert.Same(copiedUpBacking, secondInode.UpperInode);

        using var secondFile = new LinuxFile(secondLookup, FileFlags.O_RDWR, null!);
        Assert.Equal(1, secondInode.WriteFromHost(null, secondFile, "?"u8.ToArray(), 6));
        Assert.Same(copiedUpBacking, firstInode.UpperInode);
        Assert.Same(copiedUpBacking, secondInode.UpperInode);

        var buf = new byte[7];
        Assert.Equal(7, secondInode.ReadToHost(null, secondFile, buf, 0));
        Assert.Equal("lower!?", Encoding.UTF8.GetString(buf));

        var lowerReader = new LinuxFile(lowerSb.Root.Inode!.Lookup("shared.txt")!, FileFlags.O_RDONLY, null!);
        try
        {
            var lowerBuf = new byte[5];
            Assert.Equal(5, lowerReader.Dentry.Inode!.ReadToHost(null, lowerReader, lowerBuf, 0));
            Assert.Equal("lower", Encoding.UTF8.GetString(lowerBuf));
        }
        finally
        {
            lowerReader.Close();
        }
    }

    [Fact]
    public void OverlayTruncate_LowerOnlyFile_ShrinksPrefixLikeUnionmountSuite()
    {
        const string initial = ":xxx:yyy:zzz";
        var lowerFs = new LayerFileSystem();
        var lowerRoot = LayerNode.Directory("/")
            .AddChild(LayerNode.File("trunc.txt", Encoding.UTF8.GetBytes(initial)));
        var lowerSb = lowerFs.ReadSuper(
            new FileSystemType { Name = "layerfs" },
            0,
            "layer-lower",
            new LayerMountOptions { Root = lowerRoot });

        var upperType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var upperSb = upperType.CreateAnonymousFileSystem().ReadSuper(upperType, 0, "ovl-upper", null);

        var overlayFs = new OverlayFileSystem();
        var overlaySb = (OverlaySuperBlock)overlayFs.ReadSuper(
            new FileSystemType { Name = "overlay" },
            0,
            "overlay",
            new OverlayMountOptions { Lower = lowerSb, Upper = upperSb });

        for (var length = 0; length <= initial.Length; length++)
        {
            var dentry = overlaySb.Root.Inode!.Lookup("trunc.txt")!;
            var inode = Assert.IsType<OverlayInode>(dentry.Inode);
            Assert.Equal(0, inode.Truncate(length));
            Assert.Equal((ulong)length, inode.Size);

            var file = new LinuxFile(dentry, FileFlags.O_RDONLY, null!);
            try
            {
                var buf = new byte[initial.Length];
                var read = inode.ReadToHost(null, file, buf, 0);
                Assert.Equal(length, read);
                Assert.Equal(initial[..length], Encoding.UTF8.GetString(buf, 0, read));
            }
            finally
            {
                file.Close();
            }

            var writer = new LinuxFile(dentry, FileFlags.O_WRONLY, null!);
            try
            {
                Assert.Equal(initial.Length, inode.WriteFromHost(null, writer, Encoding.UTF8.GetBytes(initial), 0));
            }
            finally
            {
                writer.Close();
            }
        }
    }

    [Fact]
    public void OverlayUnlink_LowerSymlink_RemovesOnlyLinkLikeUnionmountSuite()
    {
        var lowerFs = new LayerFileSystem();
        var lowerRoot = LayerNode.Directory("/")
            .AddChild(LayerNode.File("target.txt", Encoding.UTF8.GetBytes(":xxx:yyy:zzz")))
            .AddChild(LayerNode.Symlink("link.txt", "target.txt"));
        var lowerSb = lowerFs.ReadSuper(
            new FileSystemType { Name = "layerfs" },
            0,
            "layer-lower",
            new LayerMountOptions { Root = lowerRoot });

        var upperType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var upperSb = upperType.CreateAnonymousFileSystem().ReadSuper(upperType, 0, "ovl-upper", null);

        var overlayFs = new OverlayFileSystem();
        var overlaySb = (OverlaySuperBlock)overlayFs.ReadSuper(
            new FileSystemType { Name = "overlay" },
            0,
            "overlay",
            new OverlayMountOptions { Lower = lowerSb, Upper = upperSb });

        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);
        Assert.Equal(0, root.Lookup("link.txt")!.Inode!.Readlink(out byte[]? linkTarget));
        Assert.Equal("target.txt"u8.ToArray(), linkTarget);

        Assert.Equal(0, root.Unlink("link.txt"));
        Assert.Null(root.Lookup("link.txt"));

        var target = root.Lookup("target.txt");
        Assert.NotNull(target);
        var file = new LinuxFile(target!, FileFlags.O_RDONLY, null!);
        try
        {
            var buf = new byte[12];
            var read = target!.Inode!.ReadToHost(null, file, buf, 0);
            Assert.Equal(12, read);
            Assert.Equal(":xxx:yyy:zzz", Encoding.UTF8.GetString(buf, 0, read));
        }
        finally
        {
            file.Close();
        }

        Assert.Equal(-(int)Errno.ENOENT, root.Unlink("link.txt"));
    }

    [Fact]
    public void OverlayUnlink_LowerFile_RepeatedUnlinkLikeUnionmountSuite()
    {
        var lowerFs = new LayerFileSystem();
        var lowerRoot = LayerNode.Directory("/")
            .AddChild(LayerNode.File("gone.txt", Encoding.UTF8.GetBytes("x")));
        var lowerSb = lowerFs.ReadSuper(
            new FileSystemType { Name = "layerfs" },
            0,
            "layer-lower",
            new LayerMountOptions { Root = lowerRoot });

        var upperType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var upperSb = upperType.CreateAnonymousFileSystem().ReadSuper(upperType, 0, "ovl-upper", null);

        var overlayFs = new OverlayFileSystem();
        var overlaySb = (OverlaySuperBlock)overlayFs.ReadSuper(
            new FileSystemType { Name = "overlay" },
            0,
            "overlay",
            new OverlayMountOptions { Lower = lowerSb, Upper = upperSb });

        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);
        Assert.NotNull(root.Lookup("gone.txt"));
        Assert.Equal(0, root.Unlink("gone.txt"));
        Assert.Null(root.Lookup("gone.txt"));
        Assert.Equal(-(int)Errno.ENOENT, root.Unlink("gone.txt"));
    }

    private static string ReadAll(PathLocation loc)
    {
        Assert.True(loc.IsValid);
        var file = new LinuxFile(loc.Dentry!, FileFlags.O_RDONLY, loc.Mount!);
        try
        {
            var sb = new StringBuilder();
            var buffer = new byte[256];
            long offset = 0;
            while (true)
            {
                var n = loc.Dentry!.Inode!.ReadToHost(null, file, buffer, offset);
                Assert.True(n >= 0);
                if (n == 0) break;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, n));
                offset += n;
            }

            return sb.ToString();
        }
        finally
        {
            file.Close();
        }
    }

    private static string ReadAllTextWithUnixCompatibleSharing(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static bool SupportsHostfsSymlinkCreation()
    {
        return !OperatingSystem.IsWindows() || HostFileSymlinkCreationSupported.Value;
    }

    private static bool ProbeHostFileSymlinkCreationSupport()
    {
        if (!OperatingSystem.IsWindows())
            return true;

        var root = Path.Combine(Path.GetTempPath(), $"podish-overlay-symlink-cap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var linkPath = Path.Combine(root, "link");
        try
        {
            File.CreateSymbolicLink(linkPath, "missing-target");
            return File.Exists(linkPath) || Directory.Exists(linkPath);
        }
        catch
        {
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(linkPath) || Directory.Exists(linkPath))
                    File.Delete(linkPath);
            }
            catch
            {
            }

            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
            catch
            {
            }
        }
    }
}
