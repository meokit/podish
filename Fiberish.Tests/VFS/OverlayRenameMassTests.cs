using Fiberish.Native;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.VFS;

public class OverlayRenameMassTests
{
    private const int RenameMassRingSize = 7;
    private static readonly int RenameMassIterCount = (int)(RenameMassRingSize * 4.5);

    [Fact]
    public void Unionmount_RenameMassSequentialFiles_MatchesUpstreamSemantics()
    {
        const int fileCount = 104;
        const int iterCount = 3;

        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        for (var i = 100; i < fileCount; i++)
            CreateFile(lowerSb.Root, lowerSb, $"file{i}", $"v{i}");

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        for (var j = 0; j < iterCount; j++)
        for (var i = fileCount - 1; i >= 100; i--)
            root.Rename($"file{i + j}", root, $"file{i + j + 1}");

        for (var i = 100; i < fileCount; i++)
        {
            var path = $"file{i + iterCount}";
            var dentry = root.Lookup(path);
            Assert.NotNull(dentry);
            Assert.Equal($"v{i}", ReadAll(dentry!));
        }

        for (var i = 100; i < fileCount; i++)
            root.Unlink($"file{i + iterCount}");
    }

    [Fact]
    public void Unionmount_RenameMassSequentialFilesWithDeletePhase_MatchesUpstreamSemantics()
    {
        const int fileCount = 104;
        const int iterCount = 3;

        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        for (var i = 100; i < fileCount; i++)
            CreateFile(lowerSb.Root, lowerSb, $"file{i}", $"v{i}");

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        for (var i = 100; i < fileCount; i++)
            root.Rename($"file{i}", root, $"file{i}_0");

        for (var j = 0; j < iterCount; j++)
        for (var i = fileCount - 1; i >= 100; i--)
            root.Rename($"file{i}_{j}", root, $"file{i}_{j + 1}");

        for (var i = 100; i < fileCount; i++)
            Assert.Equal($"v{i}", ReadAll(root.Lookup($"file{i}_{iterCount}")!));

        for (var i = 100; i < fileCount; i++)
            root.Unlink($"file{i}_{iterCount}");

        for (var i = 100; i < fileCount; i++)
            Assert.Null(root.Lookup($"file{i}_{iterCount}"));
    }

    [Fact]
    public void Unionmount_RenameMassExistingSequentialFilesCircularly_MatchesUpstreamSemantics()
    {
        // Equivalent to rename-mass-3.py
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        for (var i = 0; i < RenameMassRingSize - 1; i++)
            CreateFile(lowerSb.Root, lowerSb, $"file{100 + i}", $"v{i}");

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        var gap = RenameMassRingSize - 1;
        for (var i = 0; i < RenameMassIterCount; i++)
        {
            var nextGap = (gap - 1 + RenameMassRingSize) % RenameMassRingSize;
            root.Rename($"file{100 + nextGap}", root, $"file{100 + gap}");
            gap = nextGap;
        }

        var finalGap = PositiveMod(-(RenameMassIterCount + 1), RenameMassRingSize);
        for (var i = 0; i < RenameMassRingSize; i++)
        {
            var path = $"file{100 + i}";
            if (i == finalGap)
            {
                Assert.Null(root.Lookup(path));
            }
            else
            {
                Assert.NotNull(root.Lookup(path));
                root.Unlink(path);
            }
        }
    }

    [Fact]
    public void Unionmount_RenameMassNewSequentialFilesCircularly_MatchesUpstreamSemantics()
    {
        // Equivalent to rename-mass-4.py
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        for (var i = 0; i < RenameMassRingSize; i++)
            CreateFile(overlaySb.Root, overlaySb, $"newfile{100 + i}", $"abcd{i}");

        var gap = RenameMassRingSize - 1;
        for (var i = 0; i < RenameMassIterCount; i++)
        {
            var nextGap = (gap - 1 + RenameMassRingSize) % RenameMassRingSize;
            root.Rename($"newfile{100 + nextGap}", root, $"newfile{100 + gap}");
            gap = nextGap;
        }

        var finalGap = PositiveMod(-(RenameMassIterCount + 1), RenameMassRingSize);
        for (var i = 0; i < RenameMassRingSize; i++)
        {
            var path = $"newfile{100 + i}";
            if (i == finalGap)
            {
                Assert.Null(root.Lookup(path));
            }
            else
            {
                Assert.NotNull(root.Lookup(path));
                root.Unlink(path);
            }
        }
    }

    [Fact]
    public void Unionmount_RenameMassHardlinkedSequentialFilesCircularly_MatchesUpstreamSemantics()
    {
        // Equivalent to rename-mass-5.py
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        for (var i = 0; i <= RenameMassRingSize; i++)
            CreateFile(lowerSb.Root, lowerSb, $"srcfile{100 + i}", $"v{i}");

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        for (var i = 0; i <= RenameMassRingSize; i++)
        {
            var source = Assert.IsType<OverlayInode>(root.Lookup($"srcfile{100 + i}")!.Inode);
            root.Link(new Dentry($"lnfile{100 + i}", null, overlaySb.Root, overlaySb), source);
        }

        var gap = RenameMassRingSize - 1;
        for (var i = 0; i < RenameMassIterCount; i++)
        {
            var nextGap = (gap - 1 + RenameMassRingSize) % RenameMassRingSize;
            root.Rename($"lnfile{100 + nextGap}", root, $"lnfile{100 + gap}");
            gap = nextGap;
        }

        gap = RenameMassRingSize - 1;
        for (var i = 0; i < RenameMassIterCount; i++)
        {
            var nextGap = (gap + 1) % RenameMassRingSize;
            root.Rename($"srcfile{100 + nextGap}", root, $"srcfile{100 + gap}");
            gap = nextGap;
        }

        var linkGap = PositiveMod(-(RenameMassIterCount + 1), RenameMassRingSize);
        for (var i = 0; i <= RenameMassRingSize; i++)
        {
            var path = $"lnfile{100 + i}";
            if (i == linkGap)
                Assert.Null(root.Lookup(path));
            else
                root.Unlink(path);
        }

        var srcGap = PositiveMod(RenameMassIterCount - 1, RenameMassRingSize);
        for (var i = 0; i <= RenameMassRingSize; i++)
        {
            var path = $"srcfile{100 + i}";
            if (i == srcGap)
                Assert.Null(root.Lookup(path));
            else
                root.Unlink(path);
        }
    }

    [Fact]
    public void Unionmount_RenameMassDirectoriesCircularly_MatchesUpstreamSemantics()
    {
        // Equivalent to rename-mass-dir.py subtest_1
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        for (var i = 0; i < RenameMassRingSize - 1; i++)
            root.Mkdir(new Dentry($"dir{100 + i}", null, overlaySb.Root, overlaySb), 0x1ED, 0, 0);

        var gap = RenameMassRingSize - 1;
        for (var i = 0; i < RenameMassIterCount; i++)
        {
            var nextGap = (gap - 1 + RenameMassRingSize) % RenameMassRingSize;
            root.Rename($"dir{100 + nextGap}", root, $"dir{100 + gap}");
            gap = nextGap;
        }

        var finalGap = PositiveMod(-(RenameMassIterCount + 1), RenameMassRingSize);
        for (var i = 0; i < RenameMassRingSize; i++)
        {
            var path = $"dir{100 + i}";
            if (i == finalGap)
            {
                Assert.Null(root.Lookup(path));
            }
            else
            {
                Assert.Equal(InodeType.Directory, root.Lookup(path)!.Inode!.Type);
                root.Rmdir(path);
            }
        }
    }

    [Fact]
    public void Unionmount_RenameMassPopulatedDirectoriesCircularly_MatchesUpstreamSemantics()
    {
        // Equivalent to rename-mass-dir.py subtest_3
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        for (var i = 0; i < RenameMassRingSize - 1; i++)
        {
            var dirDentry = new Dentry($"pdir{100 + i}", null, overlaySb.Root, overlaySb);
            var dir = Assert.IsType<OverlayInode>(root.Mkdir(dirDentry, 0x1ED, 0, 0).Inode);
            CreateFile(dirDentry, overlaySb, "a", $"abcd{i}");
        }

        var gap = RenameMassRingSize - 1;
        for (var i = 0; i < RenameMassIterCount; i++)
        {
            var nextGap = (gap - 1 + RenameMassRingSize) % RenameMassRingSize;
            root.Rename($"pdir{100 + nextGap}", root, $"pdir{100 + gap}");
            gap = nextGap;
        }

        var n = RenameMassIterCount % (RenameMassRingSize * (RenameMassRingSize - 1));
        var cycle = n / RenameMassRingSize;
        n = (RenameMassRingSize - 1) - cycle;

        var finalGap = PositiveMod(-(RenameMassIterCount + 1), RenameMassRingSize);
        for (var i = 0; i < RenameMassRingSize; i++)
        {
            var path = $"pdir{100 + i}";
            if (i == finalGap)
            {
                Assert.Null(root.Lookup(path));
            }
            else
            {
                var dir = root.Lookup(path)!;
                Assert.Equal($"abcd{n}", ReadAll(dir.Inode!.Lookup("a")!));
                n = (n + 1) % (RenameMassRingSize - 1);
            }
        }

        for (var i = 0; i < RenameMassRingSize; i++)
        {
            var path = $"pdir{100 + i}";
            if (i == finalGap)
            {
                Assert.Null(root.Lookup(path));
            }
            else
            {
                var dir = Assert.IsType<OverlayInode>(root.Lookup(path)!.Inode);
                dir.Unlink("a");
                root.Rmdir(path);
            }
        }
    }

    [Fact]
    public void Unionmount_RenameMassSymlinksCircularly_MatchesUpstreamSemantics()
    {
        // Equivalent to rename-mass-sym.py subtest_1
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        for (var i = 0; i < RenameMassRingSize - 1; i++)
        {
            CreateFile(lowerSb.Root, lowerSb, $"target{100 + i}", ":xxx:yyy:zzz");
            CreateSymlink(lowerSb.Root, lowerSb, $"sym{100 + i}", $"target{100 + i}");
        }

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        var gap = RenameMassRingSize - 1;
        for (var i = 0; i < RenameMassIterCount; i++)
        {
            var nextGap = (gap - 1 + RenameMassRingSize) % RenameMassRingSize;
            root.Rename($"sym{100 + nextGap}", root, $"sym{100 + gap}");
            gap = nextGap;
        }

        var n = RenameMassIterCount % (RenameMassRingSize * (RenameMassRingSize - 1));
        var cycle = n / RenameMassRingSize;
        n = (RenameMassRingSize - 1) - cycle;
        var finalGap = PositiveMod(-(RenameMassIterCount + 1), RenameMassRingSize);

        for (var i = 0; i < RenameMassRingSize; i++)
        {
            var path = $"sym{100 + i}";
            if (i == finalGap)
            {
                Assert.Null(root.Lookup(path));
            }
            else
            {
                var entry = root.Lookup(path)!;
                var target = $"target{100 + n}";
                Assert.Equal(target, entry.Inode!.Readlink());
                Assert.Equal(":xxx:yyy:zzz", ReadAll(root.Lookup(target)!));
                n = (n + 1) % (RenameMassRingSize - 1);
            }
        }

        for (var i = 0; i <= RenameMassRingSize; i++)
        {
            var path = $"sym{100 + i}";
            if (i == finalGap)
                Assert.Null(root.Lookup(path));
            else if (root.Lookup(path) != null)
                root.Unlink(path);
        }
    }

    [Fact]
    public void Unionmount_RenameMassDirectorySymlinksCircularly_MatchesUpstreamSemantics()
    {
        // Equivalent to rename-mass-sym.py subtest_4
        var tmpfsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-lower", null);
        var upperSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "um-upper", null);

        for (var i = 0; i < RenameMassRingSize - 1; i++)
        {
            CreateDirectory(lowerSb.Root, lowerSb, $"dirtarget{100 + i}");
            CreateSymlink(lowerSb.Root, lowerSb, $"dirsym{100 + i}", $"dirtarget{100 + i}");
        }

        var overlaySb = CreateOverlay(lowerSb, upperSb);
        var root = Assert.IsType<OverlayInode>(overlaySb.Root.Inode);

        var gap = RenameMassRingSize - 1;
        for (var i = 0; i < RenameMassIterCount; i++)
        {
            var nextGap = (gap - 1 + RenameMassRingSize) % RenameMassRingSize;
            root.Rename($"dirsym{100 + nextGap}", root, $"dirsym{100 + gap}");
            gap = nextGap;
        }

        var n = RenameMassIterCount % (RenameMassRingSize * (RenameMassRingSize - 1));
        var cycle = n / RenameMassRingSize;
        n = (RenameMassRingSize - 1) - cycle;
        var finalGap = PositiveMod(-(RenameMassIterCount + 1), RenameMassRingSize);

        for (var i = 0; i < RenameMassRingSize; i++)
        {
            var path = $"dirsym{100 + i}";
            if (i == finalGap)
            {
                Assert.Null(root.Lookup(path));
            }
            else
            {
                var entry = root.Lookup(path)!;
                var target = $"dirtarget{100 + n}";
                Assert.Equal(target, entry.Inode!.Readlink());
                Assert.Equal(InodeType.Directory, root.Lookup(target)!.Inode!.Type);
                n = (n + 1) % (RenameMassRingSize - 1);
            }
        }

        for (var i = 0; i <= RenameMassRingSize; i++)
        {
            var path = $"dirsym{100 + i}";
            if (i == finalGap)
                Assert.Null(root.Lookup(path));
            else if (root.Lookup(path) != null)
                root.Unlink(path);
        }
    }

    private static OverlaySuperBlock CreateOverlay(SuperBlock lowerSb, SuperBlock upperSb)
    {
        var overlayFs = new OverlayFileSystem();
        return (OverlaySuperBlock)overlayFs.ReadSuper(
            new FileSystemType { Name = "overlay" },
            0,
            "overlay",
            new OverlayMountOptions { Lower = lowerSb, Upper = upperSb });
    }

    private static void CreateDirectory(Dentry parent, SuperBlock sb, string name)
    {
        var dentry = new Dentry(name, null, parent, sb);
        parent.Inode!.Mkdir(dentry, 0x1ED, 0, 0);
    }

    private static void CreateFile(Dentry parent, SuperBlock sb, string name, string content)
    {
        var dentry = new Dentry(name, null, parent, sb);
        parent.Inode!.Create(dentry, 0x1A4, 0, 0);
        var file = new LinuxFile(dentry, FileFlags.O_WRONLY, null!);
        try
        {
            var payload = System.Text.Encoding.UTF8.GetBytes(content);
            Assert.Equal(payload.Length, dentry.Inode!.WriteFromHost(null, file, payload, 0));
        }
        finally
        {
            file.Close();
        }
    }

    private static void CreateSymlink(Dentry parent, SuperBlock sb, string name, string target)
    {
        var dentry = new Dentry(name, null, parent, sb);
        parent.Inode!.Symlink(dentry, target, 0, 0);
    }

    private static string ReadAll(Dentry dentry)
    {
        var file = new LinuxFile(dentry, FileFlags.O_RDONLY, null!);
        try
        {
            var buf = new byte[64];
            var read = dentry.Inode!.ReadToHost(null, file, buf, 0);
            return System.Text.Encoding.UTF8.GetString(buf, 0, read);
        }
        finally
        {
            file.Close();
        }
    }

    private static int PositiveMod(int value, int modulus)
    {
        var result = value % modulus;
        return result < 0 ? result + modulus : result;
    }
}
