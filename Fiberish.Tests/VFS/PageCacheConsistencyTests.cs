using System.Text;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.VFS;

public class PageCacheConsistencyTests
{
    [Fact]
    public void Hostfs_MapSharedDirtyPage_IsVisibleToRead_BeforeWriteback()
    {
        var root = Path.Combine(Path.GetTempPath(), "hostfs-pagecache-consistency-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        File.WriteAllText(hostFile, "hello");

        try
        {
            using var engine = new Engine();
            var mm = new VMAManager();
            var file = OpenHostFile(root, "data.bin");

            const uint mapAddr = 0x45000000;
            mm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, file, 0, (long)file.Dentry.Inode!.Size, "MAP_SHARED", engine);
            Assert.True(mm.HandleFault(mapAddr, true, engine));
            Assert.True(engine.CopyToUser(mapAddr + 1, "ZZ"u8.ToArray()));

            var buf = new byte[5];
            var n = file.Dentry.Inode!.Read(file, buf, 0);
            Assert.Equal(5, n);
            Assert.Equal("hZZlo", Encoding.ASCII.GetString(buf));
            Assert.Equal("hello", File.ReadAllText(hostFile));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Tmpfs_MapSharedDirtyPage_IsVisibleToRead_BeforeWriteback()
    {
        using var engine = new Engine();
        var mm = new VMAManager();
        var fsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var sb = fsType.CreateFileSystem().ReadSuper(fsType, 0, "tmp", null);
        var root = sb.Root;
        var dentry = new Dentry("data.bin", null, root, sb);
        root.Inode!.Create(dentry, 0x1B6, 0, 0);

        var file = new LinuxFile(dentry, FileFlags.O_RDWR, null!);
        Assert.Equal(5, dentry.Inode!.Write(file, "hello"u8.ToArray(), 0));

        const uint mapAddr = 0x46000000;
        mm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
            MapFlags.Shared | MapFlags.Fixed, file, 0, (long)dentry.Inode.Size, "MAP_SHARED", engine);
        Assert.True(mm.HandleFault(mapAddr, true, engine));
        Assert.True(engine.CopyToUser(mapAddr + 2, "XY"u8.ToArray()));

        var buf = new byte[5];
        var n = dentry.Inode.Read(file, buf, 0);
        Assert.Equal(5, n);
        Assert.Equal("heXYo", Encoding.ASCII.GetString(buf));
    }

    [Fact]
    public void Overlay_MapSharedDirtyPage_IsVisibleToRead_BeforeWriteback()
    {
        var tempLower = Path.Combine(Path.GetTempPath(), "overlay-pc-lower-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempLower);
        var lowerFile = Path.Combine(tempLower, "data.bin");
        File.WriteAllText(lowerFile, "hello");

        try
        {
            var hostType = new FileSystemType { Name = "hostfs" };
            var hostOpts = HostfsMountOptions.Parse("rw");
            var lowerSb = new HostSuperBlock(hostType, tempLower, hostOpts);
            lowerSb.Root = lowerSb.GetDentry(tempLower, "/", null)!;

            var tmpType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
            var upperSb = tmpType.CreateFileSystem().ReadSuper(tmpType, 0, "ovl-upper", null);

            var overlayFs = new OverlayFileSystem();
            var overlaySb = (OverlaySuperBlock)overlayFs.ReadSuper(
                new FileSystemType { Name = "overlay" },
                0,
                "overlay",
                new OverlayMountOptions { Lower = lowerSb, Upper = upperSb });

            var fileDentry = overlaySb.Root.Inode!.Lookup("data.bin");
            Assert.NotNull(fileDentry);
            var file = new LinuxFile(fileDentry!, FileFlags.O_RDWR, null!);

            using var engine = new Engine();
            var mm = new VMAManager();
            const uint mapAddr = 0x47000000;
            mm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, file, 0, (long)file.Dentry.Inode!.Size, "MAP_SHARED", engine);
            Assert.True(mm.HandleFault(mapAddr, true, engine));
            Assert.True(engine.CopyToUser(mapAddr + 3, "PQ"u8.ToArray()));

            var buf = new byte[5];
            var n = file.Dentry.Inode!.Read(file, buf, 0);
            Assert.Equal(5, n);
            Assert.Equal("helPQ", Encoding.ASCII.GetString(buf));
            Assert.Equal("hello", File.ReadAllText(lowerFile));
        }
        finally
        {
            Directory.Delete(tempLower, true);
        }
    }

    [Fact]
    public void Hostfs_Write_PersistsAfterFlush()
    {
        var root = Path.Combine(Path.GetTempPath(), "hostfs-pagecache-reverse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        File.WriteAllText(hostFile, "hello");

        try
        {
            using var engine = new Engine();
            var mm = new VMAManager();
            var file = OpenHostFile(root, "data.bin");

            const uint mapAddr = 0x48000000;
            mm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, file, 0, (long)file.Dentry.Inode!.Size, "MAP_SHARED", engine);
            Assert.True(mm.HandleFault(mapAddr, false, engine));

            var rc = file.Dentry.Inode!.Write(file, "XY"u8.ToArray(), 1);
            Assert.Equal(2, rc);

            Assert.Equal("hello", File.ReadAllText(hostFile));

            mm.SyncAllMappedSharedFiles(engine);
            Assert.Equal("hXYlo", File.ReadAllText(hostFile));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Tmpfs_Write_IsVisibleToMappedPage_Immediately()
    {
        using var engine = new Engine();
        var mm = new VMAManager();
        var fsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var sb = fsType.CreateFileSystem().ReadSuper(fsType, 0, "tmp", null);
        var root = sb.Root;
        var dentry = new Dentry("data.bin", null, root, sb);
        root.Inode!.Create(dentry, 0x1B6, 0, 0);
        var file = new LinuxFile(dentry, FileFlags.O_RDWR, null!);
        Assert.Equal(5, dentry.Inode!.Write(file, "hello"u8.ToArray(), 0));

        const uint mapAddr = 0x49000000;
        mm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
            MapFlags.Shared | MapFlags.Fixed, file, 0, (long)dentry.Inode.Size, "MAP_SHARED", engine);
        Assert.True(mm.HandleFault(mapAddr, false, engine));

        var rc = dentry.Inode.Write(file, "MN"u8.ToArray(), 2);
        Assert.Equal(2, rc);

        var mapped = new byte[5];
        Assert.True(engine.CopyFromUser(mapAddr, mapped));
        Assert.Equal("heMNo", Encoding.ASCII.GetString(mapped));
    }

    [Fact]
    public void Tmpfs_WriteBeforeMmap_UsesSamePageCacheObject()
    {
        using var engine = new Engine();
        var mm = new VMAManager();
        var fsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var sb = fsType.CreateFileSystem().ReadSuper(fsType, 0, "tmp", null);
        var root = sb.Root;
        var dentry = new Dentry("data.bin", null, root, sb);
        root.Inode!.Create(dentry, 0x1B6, 0, 0);
        var file = new LinuxFile(dentry, FileFlags.O_RDWR, null!);

        Assert.Equal(5, dentry.Inode!.Write(file, "hello"u8.ToArray(), 0));
        var beforeMapCache = dentry.Inode.PageCache;
        Assert.NotNull(beforeMapCache);

        const uint mapAddr = 0x4B000000;
        mm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
            MapFlags.Shared | MapFlags.Fixed, file, 0, (long)dentry.Inode.Size, "MAP_SHARED", engine);
        Assert.True(mm.HandleFault(mapAddr, false, engine));
        Assert.Same(beforeMapCache, dentry.Inode.PageCache);

        var mapped = new byte[5];
        Assert.True(engine.CopyFromUser(mapAddr, mapped));
        Assert.Equal("hello", Encoding.ASCII.GetString(mapped));
    }

    [Fact]
    public void Overlay_Write_IsVisibleToMappedPage_Immediately()
    {
        var tempLower = Path.Combine(Path.GetTempPath(), "overlay-pc-reverse-lower-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempLower);
        var lowerFile = Path.Combine(tempLower, "data.bin");
        File.WriteAllText(lowerFile, "hello");

        try
        {
            var hostType = new FileSystemType { Name = "hostfs" };
            var hostOpts = HostfsMountOptions.Parse("rw");
            var lowerSb = new HostSuperBlock(hostType, tempLower, hostOpts);
            lowerSb.Root = lowerSb.GetDentry(tempLower, "/", null)!;

            var tmpType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
            var upperSb = tmpType.CreateFileSystem().ReadSuper(tmpType, 0, "ovl-upper-reverse", null);

            var overlayFs = new OverlayFileSystem();
            var overlaySb = (OverlaySuperBlock)overlayFs.ReadSuper(
                new FileSystemType { Name = "overlay" },
                0,
                "overlay",
                new OverlayMountOptions { Lower = lowerSb, Upper = upperSb });

            var fileDentry = overlaySb.Root.Inode!.Lookup("data.bin");
            Assert.NotNull(fileDentry);
            var file = new LinuxFile(fileDentry!, FileFlags.O_RDWR, null!);

            using var engine = new Engine();
            var mm = new VMAManager();
            const uint mapAddr = 0x4A000000;
            mm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, file, 0, (long)file.Dentry.Inode!.Size, "MAP_SHARED", engine);
            Assert.True(mm.HandleFault(mapAddr, false, engine));

            var rc = file.Dentry.Inode!.Write(file, "UV"u8.ToArray(), 3);
            Assert.Equal(2, rc);

            var mapped = new byte[5];
            Assert.True(engine.CopyFromUser(mapAddr, mapped));
            Assert.Equal("helUV", Encoding.ASCII.GetString(mapped));
            Assert.Equal("hello", File.ReadAllText(lowerFile));
        }
        finally
        {
            Directory.Delete(tempLower, true);
        }
    }

    private static LinuxFile OpenHostFile(string rootDir, string relativePath)
    {
        var fsType = new FileSystemType { Name = "hostfs" };
        var opts = HostfsMountOptions.Parse("rw");
        var sb = new HostSuperBlock(fsType, rootDir, opts);
        sb.Root = sb.GetDentry(rootDir, "/", null)!;
        var dentry = sb.Root.Inode!.Lookup(relativePath);
        Assert.NotNull(dentry);
        var file = new LinuxFile(dentry!, FileFlags.O_RDWR, null!);
        dentry!.Inode!.Open(file);
        return file;
    }
}
