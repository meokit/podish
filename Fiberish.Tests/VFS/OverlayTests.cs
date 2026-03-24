using System.Runtime.InteropServices;
using System.Text;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.VFS;

public class OverlayTests
{
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
                var holdDentry = new Dentry("hold", null, root, root.SuperBlock);
                root.Inode.Mkdir(holdDentry, 0x1FF, 0, 0);
                root.CacheChild(holdDentry, "OverlayTests.setup-hold");
                hold = holdDentry;
            }

            var mnt = hold.Inode!.Lookup("mnt");
            if (mnt == null)
            {
                var mntDentry = new Dentry("mnt", null, hold, hold.SuperBlock);
                hold.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
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
            lowerSb.Root = lowerSb.GetDentry(tempLower, "/", null)!;

            var upperSb = new HostSuperBlock(fsType, tempUpper, opts);
            upperSb.Root = upperSb.GetDentry(tempUpper, "/", null)!;

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
            overlayInode.Write(linuxFile, "world"u8.ToArray(), 5);

            Assert.NotNull(overlayInode.UpperDentry);
            Assert.NotEqual(initialHandle, linuxFile.PrivateData); // Handle should have been redirected

            // Check if parents were created in upper FS host path
            Assert.True(Directory.Exists(Path.Combine(tempUpper, "a/b/c")));
            Assert.True(File.Exists(Path.Combine(tempUpper, "a/b/c/file")));

            // Verify content in upper
            Assert.Equal("helloworld", File.ReadAllText(Path.Combine(tempUpper, "a/b/c/file")));
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
            lowerSb.Root = lowerSb.GetDentry(tempLower, "/", null)!;

            var upperSb = new HostSuperBlock(fsType, tempUpper, opts);
            upperSb.Root = upperSb.GetDentry(tempUpper, "/", null)!;

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
            var rc = overlayInode.Write(linuxFile, "x"u8.ToArray(), 0);
            Assert.True(rc >= 0);
            Assert.NotNull(overlayInode.UpperInode);

            var lowerInode = overlayInode.LowerInode!;
            var upperInode = overlayInode.UpperInode!;

            // Force a worst-case ino collision that used to alias page cache keys.
            const ulong forcedIno = 12345;
            lowerInode.Ino = forcedIno;
            upperInode.Ino = forcedIno;

            var manager = new VmBackingManager();
            var lowerCache = manager.GetOrCreateMapping(lowerInode);
            var upperCache = manager.GetOrCreateMapping(upperInode);

            try
            {
                Assert.NotSame(lowerCache, upperCache);

                lowerCache.GetOrCreatePage(0, ptr =>
                {
                    Marshal.Copy("LOWER"u8.ToArray(), 0, ptr, 5);
                    return true;
                }, out var lowerIsNew);
                Assert.True(lowerIsNew);

                upperCache.GetOrCreatePage(0, ptr =>
                {
                    Marshal.Copy("UPPER"u8.ToArray(), 0, ptr, 5);
                    return true;
                }, out var upperIsNew);
                Assert.True(upperIsNew);

                var lowerBuf = new byte[5];
                var upperBuf = new byte[5];
                Marshal.Copy(lowerCache.GetPage(0), lowerBuf, 0, 5);
                Marshal.Copy(upperCache.GetPage(0), upperBuf, 0, 5);

                Assert.Equal("LOWER", Encoding.ASCII.GetString(lowerBuf));
                Assert.Equal("UPPER", Encoding.ASCII.GetString(upperBuf));
            }
            finally
            {
                manager.ReleaseMapping(lowerInode);
                manager.ReleaseMapping(upperInode);
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
            lowerSb.Root = lowerSb.GetDentry(tempLower, "/", null)!;

            var upperSb = new HostSuperBlock(fsType, tempUpper, opts);
            upperSb.Root = upperSb.GetDentry(tempUpper, "/", null)!;

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

            var linkDentry = new Dentry("libbz2.so.1", null, libOv, overlaySb);
            libInode.Symlink(linkDentry, "libbz2.so.1.0.8", 0, 0);

            var created = libOv.Inode!.Lookup("libbz2.so.1");
            Assert.NotNull(created);
            Assert.Equal(InodeType.Symlink, created!.Inode!.Type);
            Assert.Equal("libbz2.so.1.0.8", created.Inode.Readlink());
            Assert.NotNull(libInode.UpperDentry);
            Assert.Equal("libbz2.so.1.0.8", libInode.UpperInode!.Lookup("libbz2.so.1")!.Inode!.Readlink());
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
            lowerSb.Root = lowerSb.GetDentry(tempLower, "/", null)!;

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

            var node = new Dentry("devnode", null, tmpOv, overlaySb);
            tmpInode.Mknod(node, 0x180, 0, 0, InodeType.CharDev, (1u << 8) | 3u);

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
            lowerTopSb.Root = lowerTopSb.GetDentry(tempLowerTop, "/", null)!;
            var lowerBottomSb = new HostSuperBlock(fsType, tempLowerBottom, opts);
            lowerBottomSb.Root = lowerBottomSb.GetDentry(tempLowerBottom, "/", null)!;
            var upperSb = new HostSuperBlock(fsType, tempUpper, opts);
            upperSb.Root = upperSb.GetDentry(tempUpper, "/", null)!;

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
            var n = d!.Inode!.Read(f, buf, 0);
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
            lowerTopSb.Root = lowerTopSb.GetDentry(tempLowerTop, "/", null)!;
            var lowerBottomSb = new HostSuperBlock(fsType, tempLowerBottom, opts);
            lowerBottomSb.Root = lowerBottomSb.GetDentry(tempLowerBottom, "/", null)!;
            var upperSb = new HostSuperBlock(fsType, tempUpper, opts);
            upperSb.Root = upperSb.GetDentry(tempUpper, "/", null)!;

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
            lowerSb.Root = lowerSb.GetDentry(tempLower, "/", null)!;
            var upperSb = new HostSuperBlock(fsType, tempUpper, opts);
            upperSb.Root = upperSb.GetDentry(tempUpper, "/", null)!;

            var overlayFs = new OverlayFileSystem();
            var overlaySb = (OverlaySuperBlock)overlayFs.ReadSuper(
                new FileSystemType { Name = "overlay" },
                0,
                "overlay",
                new OverlayMountOptions { Lower = lowerSb, Upper = upperSb });

            var rootInode = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);
            Assert.NotNull(rootInode.Lookup("gone.txt"));

            rootInode.Unlink("gone.txt");

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
            lowerSb.Root = lowerSb.GetDentry(tempLower, "/", null)!;
            var upperSb = new HostSuperBlock(fsType, tempUpper, opts);
            upperSb.Root = upperSb.GetDentry(tempUpper, "/", null)!;

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
            lowerSb.Root = lowerSb.GetDentry(tempLower, "/", null)!;

            var tmpType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
            var upperSb = new Tmpfs().ReadSuper(tmpType, 0, "upper", null);

            var overlayFs = new OverlayFileSystem();
            var overlaySb = (OverlaySuperBlock)overlayFs.ReadSuper(
                new FileSystemType { Name = "overlay" },
                0,
                "overlay",
                new OverlayMountOptions { Lower = lowerSb, Upper = upperSb });

            var rootInode = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);
            rootInode.Unlink("gone.txt");

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
            lowerSb.Root = lowerSb.GetDentry(tempLower, "/", null)!;
            var upperSb = new HostSuperBlock(fsType, tempUpper, opts);
            upperSb.Root = upperSb.GetDentry(tempUpper, "/", null)!;

            var overlayFs = new OverlayFileSystem();
            var overlaySb = (OverlaySuperBlock)overlayFs.ReadSuper(
                new FileSystemType { Name = "overlay" },
                0,
                "overlay",
                new OverlayMountOptions { Lower = lowerSb, Upper = upperSb });

            var rootInode = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);
            var before = rootInode.GetLinkCountForStat();
            Assert.NotNull(rootInode.Lookup("ghost"));

            rootInode.Rmdir("ghost");

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
            lowerSb.Root = lowerSb.GetDentry(tempLower, "/", null)!;
            var upperSb = new HostSuperBlock(fsType, tempUpper, opts);
            upperSb.Root = upperSb.GetDentry(tempUpper, "/", null)!;

            var overlayFs = new OverlayFileSystem();
            var overlaySb = (OverlaySuperBlock)overlayFs.ReadSuper(
                new FileSystemType { Name = "overlay" },
                0,
                "overlay",
                new OverlayMountOptions { Lower = lowerSb, Upper = upperSb });

            var rootInode = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);
            rootInode.Rename("src.txt", rootInode, "dst.txt");

            Assert.Null(rootInode.Lookup("src.txt"));
            Assert.Contains("src.txt", overlaySb.GetWhiteouts(new InodeKey(rootInode.Dev, rootInode.Ino)));

            var dst = rootInode.Lookup("dst.txt");
            Assert.NotNull(dst);
            var file = new LinuxFile(dst!, FileFlags.O_RDONLY, null!);
            var buf = new byte[8];
            var n = dst!.Inode!.Read(file, buf, 0);
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
            lowerSb.Root = lowerSb.GetDentry(tempLower, "/", null)!;
            var upperSb = new HostSuperBlock(fsType, tempUpper, opts);
            upperSb.Root = upperSb.GetDentry(tempUpper, "/", null)!;

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

            var link = new Dentry("libfoo.so.1", null, lib, overlaySb);
            libInode.Symlink(link, "libfoo.so.1.0.0", 0, 0);

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
        var fileDentry = new Dentry("open-unlink-truncate", null, overlaySb.Root, overlaySb);
        root.Create(fileDentry, 0x1A4, 0, 0);
        overlaySb.Root.CacheChild(fileDentry, "OverlayTests.open-unlink-truncate");

        var file = new LinuxFile(fileDentry, FileFlags.O_RDWR, null!);
        try
        {
            root.Unlink(fileDentry.Name);

            var truncateRc = file.OpenedInode!.Truncate(4096);
            Assert.Equal(0, truncateRc);

            var writeRc = file.OpenedInode.Write(file, "Z"u8.ToArray(), 0);
            Assert.Equal(1, writeRc);

            var readBuf = new byte[1];
            var readRc = file.OpenedInode.Read(file, readBuf, 0);
            Assert.Equal(1, readRc);
            Assert.Equal((byte)'Z', readBuf[0]);
        }
        finally
        {
            file.Close();
        }
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
                var n = loc.Dentry!.Inode!.Read(file, buffer, offset);
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
}