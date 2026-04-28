using System.Text;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.VFS;

public class HostfsWindowsCompatibilityTests
{
    [Fact]
    public void Hostfs_Windows_UnlinkOpenFile_ReleasesVisibleName_AndPreservesOldFd()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = CreateTempRoot();
        File.WriteAllText(Path.Combine(root, "data.bin"), "old");
        HostSuperBlock? sb = null;

        try
        {
            (sb, var rootInode) = MountHostfs(root);
            var original = rootInode.Lookup("data.bin");
            Assert.NotNull(original);

            var openFile = new LinuxFile(original!, FileFlags.O_RDWR, null!, LinuxFile.ReferenceKind.Normal);
            try
            {
                Assert.Equal(0, rootInode.Unlink("data.bin"));
                Assert.Null(rootInode.Lookup("data.bin"));

                var hiddenPaths = GetHiddenDeletedPaths(root);
                Assert.Single(hiddenPaths);
                Assert.Null(rootInode.Lookup(Path.GetFileName(hiddenPaths[0])));
                Assert.DoesNotContain(rootInode.GetEntries(), entry =>
                    entry.Name.ToString()!.StartsWith(HostSuperBlock.WindowsDeletedNamePrefix,
                        StringComparison.OrdinalIgnoreCase));

                File.WriteAllText(Path.Combine(root, "data.bin"), "new");
                var recreated = rootInode.Lookup("data.bin");
                Assert.NotNull(recreated);
                Assert.Equal("new", ReadAll(recreated!.Inode!, 3));
                Assert.Equal("old", ReadAll(openFile.OpenedInode!, openFile, 3));
            }
            finally
            {
                openFile.Close();
            }

            Assert.Empty(GetHiddenDeletedPaths(root));
        }
        finally
        {
            CleanupMountAndRoot(sb, root);
        }
    }

    [Fact]
    public void Hostfs_Windows_RenameOverwriteOpenFile_PreservesOldTargetFd()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = CreateTempRoot();
        var sourcePath = Path.Combine(root, "from.txt");
        var targetPath = Path.Combine(root, "to.txt");
        File.WriteAllText(sourcePath, "src");
        File.WriteAllText(targetPath, "dst");
        HostSuperBlock? sb = null;

        try
        {
            (sb, var rootInode) = MountHostfs(root);
            var target = rootInode.Lookup("to.txt");
            Assert.NotNull(target);

            var targetFile = new LinuxFile(target!, FileFlags.O_RDWR, null!, LinuxFile.ReferenceKind.Normal);
            try
            {
                Assert.Equal(0, rootInode.Rename("from.txt"u8, rootInode, "to.txt"u8));
                Assert.Null(rootInode.Lookup("from.txt"));
                Assert.Equal("src", File.ReadAllText(targetPath));
                Assert.Equal("dst", ReadAll(targetFile.OpenedInode!, targetFile, 3));

                var hiddenPaths = GetHiddenDeletedPaths(root);
                Assert.Single(hiddenPaths);
                Assert.Null(rootInode.Lookup(Path.GetFileName(hiddenPaths[0])));
            }
            finally
            {
                targetFile.Close();
            }

            Assert.Empty(GetHiddenDeletedPaths(root));
        }
        finally
        {
            CleanupMountAndRoot(sb, root);
        }
    }

    [Fact]
    public void Hostfs_Windows_HardlinkNlink_ExcludesHiddenDeletedAlias_AfterRemount()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = CreateTempRoot();
        var aPath = Path.Combine(root, "a.txt");
        File.WriteAllText(aPath, "payload");
        HostSuperBlock? sb = null;
        HostSuperBlock? remount = null;

        try
        {
            (sb, var rootInode) = MountHostfs(root);
            var a = rootInode.Lookup("a.txt");
            Assert.NotNull(a);
            var aInode = a!.Inode!;
            var alias = new Dentry(FsName.FromString("b.txt"), null, sb.Root, sb);
            Assert.Equal(0, rootInode.Link(alias, aInode));

            var b = rootInode.Lookup("b.txt");
            Assert.NotNull(b);
            Assert.Equal(2u, aInode.GetLinkCountForStat());
            Assert.Equal(2u, b!.Inode!.GetLinkCountForStat());

            var openFile = new LinuxFile(a, FileFlags.O_RDWR, null!, LinuxFile.ReferenceKind.Normal);
            try
            {
                Assert.Equal(0, rootInode.Unlink("a.txt"));

                var visible = rootInode.Lookup("b.txt");
                Assert.NotNull(visible);
                Assert.Equal(1u, visible!.Inode!.GetLinkCountForStat());
                Assert.Empty(GetHiddenDeletedPaths(root));

                (remount, var remountRoot) = MountHostfs(root);
                var remountedVisible = remountRoot.Lookup("b.txt");
                Assert.NotNull(remountedVisible);
                Assert.Equal(1u, remountedVisible!.Inode!.GetLinkCountForStat());
            }
            finally
            {
                openFile.Close();
            }

            Assert.Empty(GetHiddenDeletedPaths(root));
        }
        finally
        {
            CleanupMountAndRoot(remount, null);
            CleanupMountAndRoot(sb, root);
        }
    }

    [Fact]
    public void Hostfs_Windows_StartupSweep_RemovesLeftoverHiddenDeletedEntries()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = CreateTempRoot();
        var hiddenPath = Path.Combine(root, $"{HostSuperBlock.WindowsDeletedNamePrefix}deadbeef.1234");
        File.WriteAllText(hiddenPath, "orphan");
        HostSuperBlock? sb = null;

        try
        {
            (sb, _) = MountHostfs(root);
            Assert.False(File.Exists(hiddenPath));
        }
        finally
        {
            CleanupMountAndRoot(sb, root);
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "hostfs-win-compat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static (HostSuperBlock Sb, HostInode Root) MountHostfs(string root)
    {
        var fsType = new FileSystemType { Name = "hostfs" };
        var opts = HostfsMountOptions.Parse("rw,metadata=1");
        var sb = new HostSuperBlock(fsType, root, opts);
        sb.Root = sb.GetDentry(root, FsName.Empty, null)!;
        sb.Root.Parent = sb.Root;
        return (sb, Assert.IsType<HostInode>(sb.Root.Inode));
    }

    private static string[] GetHiddenDeletedPaths(string root)
    {
        return Directory.GetFiles(root, $"{HostSuperBlock.WindowsDeletedNamePrefix}*");
    }

    private static void CleanupMountAndRoot(HostSuperBlock? sb, string? root)
    {
        if (sb != null)
            ShutdownSuperBlock(sb);

        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            return;

        Directory.Delete(root, true);
    }

    private static void ShutdownSuperBlock(HostSuperBlock sb)
    {
        var shutdown = typeof(HostSuperBlock).GetMethod("Shutdown",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(shutdown);
        shutdown!.Invoke(sb, null);
    }

    private static string ReadAll(Inode inode, int length)
    {
        using var file = new LinuxFile(inode.Dentries[0], FileFlags.O_RDONLY, null!, LinuxFile.ReferenceKind.Normal);
        return ReadAll(inode, file, length);
    }

    private static string ReadAll(Inode inode, LinuxFile file, int length)
    {
        var buffer = new byte[length];
        var rc = inode.ReadToHost(null, file, buffer, 0);
        Assert.Equal(length, rc);
        return Encoding.UTF8.GetString(buffer);
    }
}
