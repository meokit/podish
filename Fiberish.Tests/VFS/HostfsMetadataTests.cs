using System.Text;
using Fiberish.Native;
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
}
