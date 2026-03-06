using System.Text;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.VFS;

public class HostfsMetadataTests
{
    [Fact]
    public void Hostfs_Mknod_UsesSidecarMetadataAndRoundtrips()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);

        try
        {
            var fsType = new FileSystemType { Name = "hostfs" };
            var opts = HostfsMountOptions.Parse("rw");
            var sb = new HostSuperBlock(fsType, tempRoot, opts);
            sb.Root = sb.GetDentry(tempRoot, "/", null)!;
            var rootInode = Assert.IsType<HostInode>(sb.Root.Inode);

            var nodeDentry = new Dentry("wh", null, sb.Root, sb);
            rootInode.Mknod(nodeDentry, 0x1B6, 0, 0, InodeType.CharDev, 0);

            var looked = rootInode.Lookup("wh");
            Assert.NotNull(looked);
            Assert.Equal(InodeType.CharDev, looked!.Inode!.Type);
            Assert.Equal(0u, looked.Inode.Rdev);

            var metaDir = Path.Combine(tempRoot, ".fiberish_meta");
            Assert.True(Directory.Exists(metaDir));
            Assert.True(Directory.GetFiles(metaDir, "*.json").Length > 0);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void Hostfs_XAttr_PersistsViaSidecar()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);

        try
        {
            File.WriteAllText(Path.Combine(tempRoot, "f"), "x");

            var fsType = new FileSystemType { Name = "hostfs" };
            var opts = HostfsMountOptions.Parse("rw");
            var sb = new HostSuperBlock(fsType, tempRoot, opts);
            sb.Root = sb.GetDentry(tempRoot, "/", null)!;
            var inode = sb.Root.Inode!.Lookup("f")!.Inode!;

            var setRc = inode.SetXAttr("user.test", Encoding.UTF8.GetBytes("abc"), 0);
            Assert.Equal(0, setRc);

            var readBuf = new byte[8];
            var getRc = inode.GetXAttr("user.test", readBuf);
            Assert.Equal(3, getRc);
            Assert.Equal("abc", Encoding.UTF8.GetString(readBuf, 0, getRc));

            var listBuf = new byte[64];
            var listRc = inode.ListXAttr(listBuf);
            Assert.True(listRc > 0);
            var listed = Encoding.UTF8.GetString(listBuf, 0, listRc);
            Assert.Contains("user.test", listed);

            var sb2 = new HostSuperBlock(fsType, tempRoot, opts);
            sb2.Root = sb2.GetDentry(tempRoot, "/", null)!;
            var inode2 = sb2.Root.Inode!.Lookup("f")!.Inode!;
            var getRc2 = inode2.GetXAttr("user.test", readBuf);
            Assert.Equal(3, getRc2);
            Assert.Equal("abc", Encoding.UTF8.GetString(readBuf, 0, getRc2));

            var rmRc = inode2.RemoveXAttr("user.test");
            Assert.Equal(0, rmRc);
            Assert.Equal(-(int)Errno.ENODATA, inode2.GetXAttr("user.test", readBuf));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void Hostfs_Hides_FiberishMetaDirectory()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);
        try
        {
            var fsType = new FileSystemType { Name = "hostfs" };
            var opts = HostfsMountOptions.Parse("rw");
            var sb = new HostSuperBlock(fsType, tempRoot, opts);
            sb.Root = sb.GetDentry(tempRoot, "/", null)!;
            var rootInode = sb.Root.Inode!;

            var names = rootInode.GetEntries().Select(e => e.Name).ToHashSet();
            Assert.DoesNotContain(".fiberish_meta", names);
            Assert.Null(rootInode.Lookup(".fiberish_meta"));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void Hostfs_MetadataLess_DoesNotCreateSidecarDirectory()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);
        try
        {
            File.WriteAllText(Path.Combine(tempRoot, "f"), "x");

            var fsType = new FileSystemType { Name = "hostfs" };
            var opts = HostfsMountOptions.Parse("ro,metadataless");
            var sb = new HostSuperBlock(fsType, tempRoot, opts);
            sb.Root = sb.GetDentry(tempRoot, "/", null)!;

            var inode = sb.Root.Inode!.Lookup("f")!.Inode!;
            var buf = new byte[8];
            Assert.Equal(-(int)Errno.ENODATA, inode.GetXAttr("user.none", buf));

            var metaDir = Path.Combine(tempRoot, ".fiberish_meta");
            Assert.False(Directory.Exists(metaDir));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void MountHostfs_ReadOnly_UsesDetachedMount_AndDoesNotCreateMetaDir()
    {
        var hostDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(hostDir);
        File.WriteAllText(Path.Combine(hostDir, "data.txt"), "hello");

        using var engine = new Engine();
        var vma = new VMAManager();
        var sm = new SyscallManager(engine, vma, 0);
        var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
        var rootSb = tmpfsType.CreateFileSystem().ReadSuper(tmpfsType, 0, "test-root", null);
        var rootMount = new Mount(rootSb, rootSb.Root)
        {
            Source = "tmpfs",
            FsType = "tmpfs",
            Options = "rw"
        };
        sm.InitializeRoot(rootSb.Root, rootMount);

        try
        {
            sm.MountHostfs(hostDir, "/mnt", readOnly: true);

            var loc = sm.PathWalkWithFlags("/mnt/data.txt", LookupFlags.FollowSymlink);
            Assert.True(loc.IsValid);
            Assert.Equal("hostfs", loc.Mount!.FsType);
            Assert.True(loc.Mount.IsReadOnly);

            var file = new LinuxFile(loc.Dentry!, FileFlags.O_RDONLY, loc.Mount);
            var buf = new byte[16];
            var n = loc.Dentry!.Inode!.Read(file, buf, 0);
            Assert.Equal("hello", Encoding.UTF8.GetString(buf, 0, n));

            Assert.False(Directory.Exists(Path.Combine(hostDir, ".fiberish_meta")));
        }
        finally
        {
            sm.Close();
            if (Directory.Exists(hostDir)) Directory.Delete(hostDir, true);
        }
    }

    [Fact]
    public void MountRootHostfs_ReadOnlyMetadataless_DoesNotCreateMetaDir()
    {
        var hostDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(hostDir);
        File.WriteAllText(Path.Combine(hostDir, "root-data.txt"), "hello-root");

        using var engine = new Engine();
        var vma = new VMAManager();
        var sm = new SyscallManager(engine, vma, 0);

        try
        {
            sm.MountRootHostfs(hostDir, "ro,metadataless");

            var loc = sm.PathWalkWithFlags("/root-data.txt", LookupFlags.FollowSymlink);
            Assert.True(loc.IsValid);
            Assert.Equal("hostfs", loc.Mount!.FsType);
            Assert.True(loc.Mount.IsReadOnly);

            var file = new LinuxFile(loc.Dentry!, FileFlags.O_RDONLY, loc.Mount);
            var buf = new byte[32];
            var n = loc.Dentry!.Inode!.Read(file, buf, 0);
            Assert.Equal("hello-root", Encoding.UTF8.GetString(buf, 0, n));

            Assert.False(Directory.Exists(Path.Combine(hostDir, ".fiberish_meta")));
        }
        finally
        {
            sm.Close();
            if (Directory.Exists(hostDir)) Directory.Delete(hostDir, true);
        }
    }

    [Fact]
    public void Hostfs_Lookup_DropsStaleCachedEntry_AfterHostDelete()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);
        var hostFile = Path.Combine(tempRoot, "ghost.txt");
        File.WriteAllText(hostFile, "ghost");

        try
        {
            var fsType = new FileSystemType { Name = "hostfs" };
            var opts = HostfsMountOptions.Parse("rw");
            var sb = new HostSuperBlock(fsType, tempRoot, opts);
            sb.Root = sb.GetDentry(tempRoot, "/", null)!;
            var rootInode = Assert.IsType<HostInode>(sb.Root.Inode);

            var first = rootInode.Lookup("ghost.txt");
            Assert.NotNull(first);

            File.Delete(hostFile);

            var second = rootInode.Lookup("ghost.txt");
            Assert.Null(second);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void Hostfs_RenameDirectory_UpdatesCachedDescendantPaths()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var oldDir = Path.Combine(tempRoot, "dir");
        var oldSubDir = Path.Combine(oldDir, "sub");
        Directory.CreateDirectory(oldSubDir);
        File.WriteAllText(Path.Combine(oldSubDir, "file.txt"), "data");

        try
        {
            var fsType = new FileSystemType { Name = "hostfs" };
            var opts = HostfsMountOptions.Parse("rw");
            var sb = new HostSuperBlock(fsType, tempRoot, opts);
            sb.Root = sb.GetDentry(tempRoot, "/", null)!;
            var rootInode = Assert.IsType<HostInode>(sb.Root.Inode);

            var dir = rootInode.Lookup("dir");
            Assert.NotNull(dir);
            var sub = Assert.IsType<HostInode>(dir!.Inode).Lookup("sub");
            Assert.NotNull(sub);
            var file = Assert.IsType<HostInode>(sub!.Inode).Lookup("file.txt");
            Assert.NotNull(file);

            rootInode.Rename("dir", rootInode, "dir2");

            var renamedDir = rootInode.Lookup("dir2");
            Assert.NotNull(renamedDir);
            Assert.Equal(Path.Combine(tempRoot, "dir2"), Assert.IsType<HostInode>(renamedDir!.Inode).HostPath);
            Assert.Equal(Path.Combine(tempRoot, "dir2", "sub"), Assert.IsType<HostInode>(sub.Inode).HostPath);
            Assert.Equal(Path.Combine(tempRoot, "dir2", "sub", "file.txt"), Assert.IsType<HostInode>(file!.Inode).HostPath);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }
}
