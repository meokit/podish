using System.Reflection;
using System.Text;
using System.Text.Json;
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
            var opts = HostfsMountOptions.Parse("rw,metadata=1");
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
            var opts = HostfsMountOptions.Parse("rw,metadata=1");
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
    public void Hostfs_Timestamps_PersistViaSidecar()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);

        try
        {
            File.WriteAllText(Path.Combine(tempRoot, "f"), "x");

            var fsType = new FileSystemType { Name = "hostfs" };
            var opts = HostfsMountOptions.Parse("rw,metadata=1");
            var sb = new HostSuperBlock(fsType, tempRoot, opts);
            sb.Root = sb.GetDentry(tempRoot, "/", null)!;
            var inode = Assert.IsType<HostInode>(sb.Root.Inode!.Lookup("f")!.Inode!);

            var atime = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000).UtcDateTime;
            var mtime = DateTimeOffset.FromUnixTimeSeconds(1_700_000_100).UtcDateTime;
            var ctime = DateTimeOffset.FromUnixTimeSeconds(1_700_000_200).UtcDateTime;

            Assert.Equal(0, inode.UpdateTimes(atime, mtime, ctime));

            var sb2 = new HostSuperBlock(fsType, tempRoot, opts);
            sb2.Root = sb2.GetDentry(tempRoot, "/", null)!;
            var inode2 = Assert.IsType<HostInode>(sb2.Root.Inode!.Lookup("f")!.Inode!);

            Assert.Equal(1_700_000_000L, new DateTimeOffset(inode2.ATime).ToUnixTimeSeconds());
            Assert.Equal(1_700_000_100L, new DateTimeOffset(inode2.MTime).ToUnixTimeSeconds());
            Assert.Equal(1_700_000_200L, new DateTimeOffset(inode2.CTime).ToUnixTimeSeconds());

            Assert.Equal(1_700_000_000L,
                new DateTimeOffset(File.GetLastAccessTimeUtc(Path.Combine(tempRoot, "f"))).ToUnixTimeSeconds());
            Assert.Equal(1_700_000_100L,
                new DateTimeOffset(File.GetLastWriteTimeUtc(Path.Combine(tempRoot, "f"))).ToUnixTimeSeconds());
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void Hostfs_MetadataLess_TimestampsUpdateHostFileWithoutSidecar()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);

        try
        {
            var filePath = Path.Combine(tempRoot, "f");
            File.WriteAllText(filePath, "x");

            var fsType = new FileSystemType { Name = "hostfs" };
            var opts = HostfsMountOptions.Parse("rw,metadata=0");
            var sb = new HostSuperBlock(fsType, tempRoot, opts);
            sb.Root = sb.GetDentry(tempRoot, "/", null)!;
            var inode = Assert.IsType<HostInode>(sb.Root.Inode!.Lookup("f")!.Inode!);

            var atime = DateTimeOffset.FromUnixTimeSeconds(1_700_001_000).UtcDateTime;
            var mtime = DateTimeOffset.FromUnixTimeSeconds(1_700_001_100).UtcDateTime;

            Assert.Equal(0, inode.UpdateTimes(atime, mtime, null));

            Assert.Equal(1_700_001_000L, new DateTimeOffset(File.GetLastAccessTimeUtc(filePath)).ToUnixTimeSeconds());
            Assert.Equal(1_700_001_100L, new DateTimeOffset(File.GetLastWriteTimeUtc(filePath)).ToUnixTimeSeconds());
            Assert.False(Directory.Exists(Path.Combine(tempRoot, ".fiberish_meta")));

            var sb2 = new HostSuperBlock(fsType, tempRoot, opts);
            sb2.Root = sb2.GetDentry(tempRoot, "/", null)!;
            var inode2 = Assert.IsType<HostInode>(sb2.Root.Inode!.Lookup("f")!.Inode!);
            Assert.Equal(1_700_001_000L, new DateTimeOffset(inode2.ATime).ToUnixTimeSeconds());
            Assert.Equal(1_700_001_100L, new DateTimeOffset(inode2.MTime).ToUnixTimeSeconds());
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
            var opts = HostfsMountOptions.Parse("rw,metadata=1");
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
            var opts = HostfsMountOptions.Parse("ro");
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
        var rootSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "test-root", null);
        var rootMount = new Mount(rootSb, rootSb.Root)
        {
            Source = "tmpfs",
            FsType = "tmpfs",
            Options = "rw"
        };
        sm.InitializeRoot(rootSb.Root, rootMount);

        try
        {
            sm.MountHostfs(hostDir, "/mnt", true);

            var loc = sm.PathWalkWithFlags("/mnt/data.txt", LookupFlags.FollowSymlink);
            Assert.True(loc.IsValid);
            Assert.Equal("hostfs", loc.Mount!.FsType);
            Assert.True(loc.Mount.IsReadOnly);

            var file = new LinuxFile(loc.Dentry!, FileFlags.O_RDONLY, loc.Mount);
            var buf = new byte[16];
            var n = loc.Dentry!.Inode!.ReadToHost(null, file, buf, 0);
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
            sm.MountRootHostfs(hostDir, "ro");

            var loc = sm.PathWalkWithFlags("/root-data.txt", LookupFlags.FollowSymlink);
            Assert.True(loc.IsValid);
            Assert.Equal("hostfs", loc.Mount!.FsType);
            Assert.True(loc.Mount.IsReadOnly);

            var file = new LinuxFile(loc.Dentry!, FileFlags.O_RDONLY, loc.Mount);
            var buf = new byte[32];
            var n = loc.Dentry!.Inode!.ReadToHost(null, file, buf, 0);
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
            var opts = HostfsMountOptions.Parse("rw,metadata=1");
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
            var opts = HostfsMountOptions.Parse("rw,metadata=1");
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
            Assert.Equal(Path.Combine(tempRoot, "dir2", "sub", "file.txt"),
                Assert.IsType<HostInode>(file!.Inode).HostPath);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void Hostfs_LinkAndUnlink_TracksLogicalLinkCount()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);
        File.WriteAllText(Path.Combine(tempRoot, "a.txt"), "x");

        try
        {
            var fsType = new FileSystemType { Name = "hostfs" };
            var opts = HostfsMountOptions.Parse("rw,metadata=1");
            var sb = new HostSuperBlock(fsType, tempRoot, opts);
            sb.Root = sb.GetDentry(tempRoot, "/", null)!;
            var rootInode = Assert.IsType<HostInode>(sb.Root.Inode);

            var source = rootInode.Lookup("a.txt");
            Assert.NotNull(source);
            var sourceInode = source!.Inode!;
            Assert.Equal(1u, sourceInode.GetLinkCountForStat());

            var alias = new Dentry("b.txt", null, sb.Root, sb);
            rootInode.Link(alias, sourceInode);
            Assert.Equal(2u, sourceInode.GetLinkCountForStat());

            rootInode.Unlink("a.txt");
            Assert.Equal(1u, sourceInode.GetLinkCountForStat());
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void Hostfs_HardlinkAliases_ShareInodeIdentity_AfterDcacheDrop()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);
        File.WriteAllText(Path.Combine(tempRoot, "a.txt"), "x");

        try
        {
            var fsType = new FileSystemType { Name = "hostfs" };
            var opts = HostfsMountOptions.Parse("rw,metadata=1");
            var sb = new HostSuperBlock(fsType, tempRoot, opts);
            sb.Root = sb.GetDentry(tempRoot, "/", null)!;
            var rootInode = Assert.IsType<HostInode>(sb.Root.Inode);

            var source = rootInode.Lookup("a.txt");
            Assert.NotNull(source);
            var sourceInode = source!.Inode!;

            var alias = new Dentry("b.txt", null, sb.Root, sb);
            rootInode.Link(alias, sourceInode);
            Assert.Equal(2u, sourceInode.GetLinkCountForStat());

            _ = sb.DropDentryCache();

            var a = rootInode.Lookup("a.txt");
            var b = rootInode.Lookup("b.txt");
            Assert.NotNull(a);
            Assert.NotNull(b);
            Assert.Same(a!.Inode, b!.Inode);
            Assert.Equal(2u, a.Inode!.GetLinkCountForStat());
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void Hostfs_HardlinkAliases_XAttrPersists_AcrossUnlinkDropCacheAndRemount()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);
        File.WriteAllText(Path.Combine(tempRoot, "a.txt"), "x");

        try
        {
            var fsType = new FileSystemType { Name = "hostfs" };
            var opts = HostfsMountOptions.Parse("rw,metadata=1");
            var sb = new HostSuperBlock(fsType, tempRoot, opts);
            sb.Root = sb.GetDentry(tempRoot, "/", null)!;
            var rootInode = Assert.IsType<HostInode>(sb.Root.Inode);

            var source = rootInode.Lookup("a.txt");
            Assert.NotNull(source);
            var sourceInode = source!.Inode!;
            Assert.Equal(0, sourceInode.SetXAttr("user.tag", Encoding.UTF8.GetBytes("alpha"), 0));

            var alias = new Dentry("b.txt", null, sb.Root, sb);
            rootInode.Link(alias, sourceInode);

            _ = sb.DropDentryCache();
            var b1 = rootInode.Lookup("b.txt");
            Assert.NotNull(b1);
            var buf = new byte[16];
            var rc1 = b1!.Inode!.GetXAttr("user.tag", buf);
            Assert.Equal(5, rc1);
            Assert.Equal("alpha", Encoding.UTF8.GetString(buf, 0, rc1));

            rootInode.Unlink("a.txt");
            _ = sb.DropDentryCache();
            var b2 = rootInode.Lookup("b.txt");
            Assert.NotNull(b2);
            var rc2 = b2!.Inode!.GetXAttr("user.tag", buf);
            Assert.Equal(5, rc2);
            Assert.Equal("alpha", Encoding.UTF8.GetString(buf, 0, rc2));

            var sb2 = new HostSuperBlock(fsType, tempRoot, opts);
            sb2.Root = sb2.GetDentry(tempRoot, "/", null)!;
            var b3 = sb2.Root.Inode!.Lookup("b.txt");
            Assert.NotNull(b3);
            var rc3 = b3!.Inode!.GetXAttr("user.tag", buf);
            Assert.Equal(5, rc3);
            Assert.Equal("alpha", Encoding.UTF8.GetString(buf, 0, rc3));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void Hostfs_DropDcache_PreservesPathBinding_ForOpenDentry()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);
        var hostPath = Path.Combine(tempRoot, "active.txt");
        File.WriteAllText(hostPath, "active");

        try
        {
            var fsType = new FileSystemType { Name = "hostfs" };
            var opts = HostfsMountOptions.Parse("rw,metadata=1");
            var sb = new HostSuperBlock(fsType, tempRoot, opts);
            sb.Root = sb.GetDentry(tempRoot, "/", null)!;
            var rootInode = Assert.IsType<HostInode>(sb.Root.Inode);
            var dentry = rootInode.Lookup("active.txt");
            Assert.NotNull(dentry);

            var file = new LinuxFile(dentry!, FileFlags.O_RDONLY, null!);
            try
            {
                Assert.True(TryGetPathForDentry(sb, dentry!, out var beforePath));
                Assert.Equal(Path.GetFullPath(hostPath), beforePath);

                _ = sb.DropDentryCache();

                Assert.True(TryGetPathForDentry(sb, dentry!, out var afterPath));
                Assert.Equal(Path.GetFullPath(hostPath), afterPath);

                var buffer = new byte[16];
                var read = dentry!.Inode!.ReadToHost(null, file, buffer, 0);
                Assert.Equal("active", Encoding.UTF8.GetString(buffer, 0, read));
            }
            finally
            {
                file.Close();
            }

            _ = sb.DropDentryCache();
            Assert.False(TryGetPathForDentry(sb, dentry!, out _));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void Hostfs_MetadataStore_ResetsLegacyLayoutToV2()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);
        File.WriteAllText(Path.Combine(tempRoot, "f"), "x");
        var metaDir = Path.Combine(tempRoot, ".fiberish_meta");
        Directory.CreateDirectory(metaDir);
        File.WriteAllText(Path.Combine(metaDir, "legacy.json"), "{\"Path\":\"/old\"}");

        try
        {
            var fsType = new FileSystemType { Name = "hostfs" };
            var opts = HostfsMountOptions.Parse("rw,metadata=1");
            var sb = new HostSuperBlock(fsType, tempRoot, opts);
            sb.Root = sb.GetDentry(tempRoot, "/", null)!;
            var inode = sb.Root.Inode!.Lookup("f")!.Inode!;
            Assert.Equal(0, inode.SetXAttr("user.reset", Encoding.UTF8.GetBytes("ok"), 0));

            var manifestPath = Path.Combine(metaDir, "manifest.json");
            Assert.True(File.Exists(manifestPath));
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            Assert.Equal(2, doc.RootElement.GetProperty("SchemaVersion").GetInt32());
            Assert.True(Directory.Exists(Path.Combine(metaDir, "paths")));
            Assert.True(Directory.Exists(Path.Combine(metaDir, "objects")));
            Assert.True(Directory.Exists(Path.Combine(metaDir, "identities")));
            Assert.False(File.Exists(Path.Combine(metaDir, "legacy.json")));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void Hostfs_MkdirAndRmdir_TrackDirectoryLinkCount()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);

        try
        {
            var fsType = new FileSystemType { Name = "hostfs" };
            var opts = HostfsMountOptions.Parse("rw,metadata=1");
            var sb = new HostSuperBlock(fsType, tempRoot, opts);
            sb.Root = sb.GetDentry(tempRoot, "/", null)!;
            var rootInode = Assert.IsType<HostInode>(sb.Root.Inode);

            Assert.Equal(2u, rootInode.GetLinkCountForStat());

            var dir = new Dentry("dir", null, sb.Root, sb);
            rootInode.Mkdir(dir, 0x1ED, 0, 0);
            var dirInode = Assert.IsType<HostInode>(dir.Inode);

            Assert.Equal(3u, rootInode.GetLinkCountForStat());
            Assert.Equal(2u, dirInode.GetLinkCountForStat());

            var nested = new Dentry("nested", null, dir, sb);
            dirInode.Mkdir(nested, 0x1ED, 0, 0);
            var nestedInode = Assert.IsType<HostInode>(nested.Inode);

            Assert.Equal(3u, dirInode.GetLinkCountForStat());
            Assert.Equal(2u, nestedInode.GetLinkCountForStat());

            dirInode.Rmdir("nested");
            Assert.Equal(2u, dirInode.GetLinkCountForStat());
            Assert.Equal(0u, nestedInode.GetLinkCountForStat());
            Assert.True(nestedInode.IsFinalized);

            rootInode.Rmdir("dir");
            Assert.Equal(2u, rootInode.GetLinkCountForStat());
            Assert.Equal(0u, dirInode.GetLinkCountForStat());
            Assert.True(dirInode.IsFinalized);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void Hostfs_RenameDirectoryAcrossParents_AdjustsParentLinkCount()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);

        try
        {
            var fsType = new FileSystemType { Name = "hostfs" };
            var opts = HostfsMountOptions.Parse("rw,metadata=1");
            var sb = new HostSuperBlock(fsType, tempRoot, opts);
            sb.Root = sb.GetDentry(tempRoot, "/", null)!;
            var rootInode = Assert.IsType<HostInode>(sb.Root.Inode);

            var from = new Dentry("from", null, sb.Root, sb);
            rootInode.Mkdir(from, 0x1ED, 0, 0);
            var fromInode = Assert.IsType<HostInode>(from.Inode);

            var to = new Dentry("to", null, sb.Root, sb);
            rootInode.Mkdir(to, 0x1ED, 0, 0);
            var toInode = Assert.IsType<HostInode>(to.Inode);

            var child = new Dentry("child", null, from, sb);
            fromInode.Mkdir(child, 0x1ED, 0, 0);
            var childInode = Assert.IsType<HostInode>(child.Inode);

            Assert.Equal(4u, rootInode.GetLinkCountForStat());
            Assert.Equal(3u, fromInode.GetLinkCountForStat());
            Assert.Equal(2u, toInode.GetLinkCountForStat());
            Assert.Equal(2u, childInode.GetLinkCountForStat());

            fromInode.Rename("child", toInode, "moved");

            Assert.Equal(4u, rootInode.GetLinkCountForStat());
            Assert.Equal(2u, fromInode.GetLinkCountForStat());
            Assert.Equal(3u, toInode.GetLinkCountForStat());
            Assert.Equal(2u, childInode.GetLinkCountForStat());
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void Hostfs_RenameDirectoryOverwrite_DropsVictimAndUpdatesParentNlink()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);
        Directory.CreateDirectory(Path.Combine(tempRoot, "src"));
        Directory.CreateDirectory(Path.Combine(tempRoot, "dst"));

        try
        {
            var fsType = new FileSystemType { Name = "hostfs" };
            var opts = HostfsMountOptions.Parse("rw,metadata=1");
            var sb = new HostSuperBlock(fsType, tempRoot, opts);
            sb.Root = sb.GetDentry(tempRoot, "/", null)!;
            var rootInode = Assert.IsType<HostInode>(sb.Root.Inode);

            var src = rootInode.Lookup("src");
            var dst = rootInode.Lookup("dst");
            Assert.NotNull(src);
            Assert.NotNull(dst);
            var srcInode = src!.Inode!;
            var dstInode = dst!.Inode!;

            Assert.Equal(4u, rootInode.GetLinkCountForStat());
            Assert.Equal(2u, srcInode.GetLinkCountForStat());
            Assert.Equal(2u, dstInode.GetLinkCountForStat());

            rootInode.Rename("src", rootInode, "dst");

            Assert.Equal(3u, rootInode.GetLinkCountForStat());
            Assert.Equal(2u, srcInode.GetLinkCountForStat());
            Assert.Equal(0u, dstInode.GetLinkCountForStat());
            Assert.True(dstInode.IsFinalized);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void Hostfs_RenameOverwrite_DropsReplacedInodeLinkCount()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);
        File.WriteAllText(Path.Combine(tempRoot, "a.txt"), "a");
        File.WriteAllText(Path.Combine(tempRoot, "b.txt"), "b");

        try
        {
            var fsType = new FileSystemType { Name = "hostfs" };
            var opts = HostfsMountOptions.Parse("rw,metadata=1");
            var sb = new HostSuperBlock(fsType, tempRoot, opts);
            sb.Root = sb.GetDentry(tempRoot, "/", null)!;
            var rootInode = Assert.IsType<HostInode>(sb.Root.Inode);

            var source = rootInode.Lookup("a.txt");
            var victim = rootInode.Lookup("b.txt");
            Assert.NotNull(source);
            Assert.NotNull(victim);
            var sourceInode = source!.Inode!;
            var victimInode = victim!.Inode!;
            Assert.Equal(1u, victimInode.GetLinkCountForStat());

            rootInode.Rename("a.txt", rootInode, "b.txt");

            var renamed = rootInode.Lookup("b.txt");
            Assert.NotNull(renamed);
            Assert.Same(sourceInode, renamed!.Inode);
            Assert.Equal(1u, sourceInode.GetLinkCountForStat());
            Assert.Equal(0u, victimInode.GetLinkCountForStat());
            Assert.True(victimInode.IsFinalized);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void Hostfs_Rename_WhenOldAndNewAreSameInode_IsNoOp()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);
        File.WriteAllText(Path.Combine(tempRoot, "src.txt"), "x");

        try
        {
            var fsType = new FileSystemType { Name = "hostfs" };
            var opts = HostfsMountOptions.Parse("rw,metadata=1");
            var sb = new HostSuperBlock(fsType, tempRoot, opts);
            sb.Root = sb.GetDentry(tempRoot, "/", null)!;
            var rootInode = Assert.IsType<HostInode>(sb.Root.Inode);

            var src = rootInode.Lookup("src.txt");
            Assert.NotNull(src);
            var inode = src!.Inode!;
            var alias = new Dentry("alias.txt", null, sb.Root, sb);
            rootInode.Link(alias, inode);
            Assert.Equal(2u, inode.GetLinkCountForStat());

            rootInode.Rename("src.txt", rootInode, "alias.txt");

            var srcAfter = rootInode.Lookup("src.txt");
            var aliasAfter = rootInode.Lookup("alias.txt");
            Assert.NotNull(srcAfter);
            Assert.NotNull(aliasAfter);
            Assert.Same(inode, srcAfter!.Inode);
            Assert.Same(inode, aliasAfter!.Inode);
            Assert.Equal(2u, inode.GetLinkCountForStat());
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    private static bool TryGetPathForDentry(HostSuperBlock sb, Dentry dentry, out string path)
    {
        var method = typeof(HostSuperBlock).GetMethod(
            "TryGetPathForDentry",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        object?[] args = [dentry, null];
        var ok = (bool)method!.Invoke(sb, args)!;
        path = args[1] as string ?? string.Empty;
        return ok;
    }
}