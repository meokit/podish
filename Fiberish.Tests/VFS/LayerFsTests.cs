using System.Text;
using System.Reflection;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.VFS;

public class LayerFsTests
{
    private sealed class OffsetContentProvider(byte[] blob) : ILayerContentProvider
    {
        public bool TryRead(LayerIndexEntry entry, long offset, Span<byte> buffer, out int bytesRead)
        {
            bytesRead = 0;
            if (entry.Type != InodeType.File) return true;
            if (entry.DataOffset < 0) return false;

            var start = (int)(entry.DataOffset + offset);
            if (start >= blob.Length) return true;

            var maxByBlob = blob.Length - start;
            var maxBySize = (int)Math.Max(0, (long)entry.Size - offset);
            var toCopy = Math.Min(buffer.Length, Math.Min(maxByBlob, maxBySize));
            if (toCopy <= 0) return true;

            blob.AsSpan(start, toCopy).CopyTo(buffer);
            bytesRead = toCopy;
            return true;
        }
    }

    [Fact]
    public void Lookup_IsCaseSensitive()
    {
        var rootNode = LayerNode.Directory("/")
            .AddChild(LayerNode.File("Readme", "hello"u8.ToArray()));
        var fs = new LayerFileSystem();
        var sb = fs.ReadSuper(
            new FileSystemType { Name = "layerfs" },
            0,
            "layer",
            new LayerMountOptions { Root = rootNode });

        var hit = sb.Root.Inode!.Lookup("Readme");
        var miss = sb.Root.Inode!.Lookup("readme");

        Assert.NotNull(hit);
        Assert.Null(miss);
    }

    [Fact]
    public void Read_FileContent_Works()
    {
        var rootNode = LayerNode.Directory("/")
            .AddChild(LayerNode.File("x.txt", Encoding.UTF8.GetBytes("abc")));
        var fs = new LayerFileSystem();
        var sb = fs.ReadSuper(
            new FileSystemType { Name = "layerfs" },
            0,
            "layer",
            new LayerMountOptions { Root = rootNode });

        var d = sb.Root.Inode!.Lookup("x.txt");
        Assert.NotNull(d);
        var lf = new LinuxFile(d!, FileFlags.O_RDONLY, null!);
        var buf = new byte[8];
        var n = d!.Inode!.Read(lf, buf, 0);

        Assert.Equal(3, n);
        Assert.Equal("abc", Encoding.UTF8.GetString(buf, 0, n));
    }

    [Fact]
    public void Readlink_Symlink_Works()
    {
        var rootNode = LayerNode.Directory("/")
            .AddChild(LayerNode.Symlink("sh", "/bin/busybox"));
        var fs = new LayerFileSystem();
        var sb = fs.ReadSuper(
            new FileSystemType { Name = "layerfs" },
            0,
            "layer",
            new LayerMountOptions { Root = rootNode });

        var d = sb.Root.Inode!.Lookup("sh");
        Assert.NotNull(d);
        Assert.Equal("/bin/busybox", d!.Inode!.Readlink());
    }

    [Fact]
    public void Write_ReturnsErofs()
    {
        var rootNode = LayerNode.Directory("/")
            .AddChild(LayerNode.File("x.txt", Encoding.UTF8.GetBytes("abc")));
        var fs = new LayerFileSystem();
        var sb = fs.ReadSuper(
            new FileSystemType { Name = "layerfs" },
            0,
            "layer",
            new LayerMountOptions { Root = rootNode });

        var d = sb.Root.Inode!.Lookup("x.txt");
        Assert.NotNull(d);
        var lf = new LinuxFile(d!, FileFlags.O_WRONLY, null!);
        var rc = d!.Inode!.Write(lf, "z"u8.ToArray(), 0);

        Assert.Equal(-(int)Errno.EROFS, rc);
    }

    [Fact]
    public void MountFromIndex_PreservesMetadata()
    {
        var index = new LayerIndex();
        index.AddEntry(new LayerIndexEntry(
            "/bin",
            InodeType.Directory,
            Mode: 0x1ED,
            Uid: 1000,
            Gid: 1001));
        index.AddEntry(new LayerIndexEntry(
            "/bin/app",
            InodeType.File,
            Mode: 0x1ED,
            Uid: 2000,
            Gid: 2001,
            Size: 3,
            InlineData: "abc"u8.ToArray()));

        var fs = new LayerFileSystem();
        var sb = fs.ReadSuper(
            new FileSystemType { Name = "layerfs" },
            0,
            "layer",
            new LayerMountOptions { Index = index });

        var bin = sb.Root.Inode!.Lookup("bin");
        Assert.NotNull(bin);
        Assert.Equal(0x1ED, bin!.Inode!.Mode);
        Assert.Equal(1000, bin.Inode.Uid);
        Assert.Equal(1001, bin.Inode.Gid);

        var app = bin.Inode.Lookup("app");
        Assert.NotNull(app);
        Assert.Equal(0x1ED, app!.Inode!.Mode);
        Assert.Equal(2000, app.Inode.Uid);
        Assert.Equal(2001, app.Inode.Gid);
    }

    [Fact]
    public void Read_FromIndexOffset_UsesContentProvider()
    {
        var blob = Encoding.UTF8.GetBytes("xxpayloadyy");
        var index = new LayerIndex();
        index.AddEntry(new LayerIndexEntry(
            "/f",
            InodeType.File,
            Mode: 0x1A4,
            Size: 7,
            DataOffset: 2));

        var fs = new LayerFileSystem();
        var sb = fs.ReadSuper(
            new FileSystemType { Name = "layerfs" },
            0,
            "layer",
            new LayerMountOptions
            {
                Index = index,
                ContentProvider = new OffsetContentProvider(blob)
            });

        var d = sb.Root.Inode!.Lookup("f");
        Assert.NotNull(d);
        var lf = new LinuxFile(d!, FileFlags.O_RDONLY, null!);
        var buf = new byte[16];
        var n = d!.Inode!.Read(lf, buf, 0);

        Assert.Equal(7, n);
        Assert.Equal("payload", Encoding.UTF8.GetString(buf, 0, n));
    }

    [Fact]
    public void PageCacheOps_ReadPageWorks_AndWritePageReturnsErofs()
    {
        var rootNode = LayerNode.Directory("/")
            .AddChild(LayerNode.File("x.txt", Encoding.UTF8.GetBytes("abc")));
        var fs = new LayerFileSystem();
        var sb = fs.ReadSuper(
            new FileSystemType { Name = "layerfs" },
            0,
            "layer",
            new LayerMountOptions { Root = rootNode });

        var d = sb.Root.Inode!.Lookup("x.txt");
        Assert.NotNull(d);
        var lf = new LinuxFile(d!, FileFlags.O_RDONLY, null!);

        var page = new byte[LinuxConstants.PageSize];
        var readRc = d!.Inode!.ReadPage(lf, new PageIoRequest(0, 0, 3), page);
        Assert.Equal(0, readRc);
        Assert.Equal("abc", Encoding.UTF8.GetString(page, 0, 3));

        var writeRc = d.Inode.WritePage(lf, new PageIoRequest(0, 0, 1), "z"u8.ToArray(), true);
        Assert.Equal(-(int)Errno.EROFS, writeRc);
    }

    [Fact]
    public void ShrinkAfterLookup_ReLookupMustUseFreshInodeObject()
    {
        var rootNode = LayerNode.Directory("/")
            .AddChild(LayerNode.File("x.txt", Encoding.UTF8.GetBytes("abc")));
        var fs = new LayerFileSystem();
        var sb = fs.ReadSuper(
            new FileSystemType { Name = "layerfs" },
            0,
            "layer",
            new LayerMountOptions { Root = rootNode });

        var first = sb.Root.Inode!.Lookup("x.txt");
        Assert.NotNull(first);
        sb.Root.CacheChild(first!, "LayerFsTests.ShrinkAfterLookup.cache");
        var oldInode = Assert.IsType<LayerInode>(first!.Inode);

        _ = VfsShrinker.DropDentryCache(sb);
        _ = VfsShrinker.EvictUnusedInodes(sb);
        Assert.True(oldInode.IsCacheEvicted);

        var second = sb.Root.Inode!.Lookup("x.txt");
        Assert.NotNull(second);
        Assert.NotSame(oldInode, second!.Inode);
        Assert.False(second.Inode!.IsCacheEvicted);

        var file = new LinuxFile(second, FileFlags.O_RDONLY, null!);
        try
        {
            var buffer = new byte[8];
            var n = second.Inode.Read(file, buffer, 0);
            Assert.Equal(3, n);
            Assert.Equal("abc", Encoding.UTF8.GetString(buffer, 0, n));
        }
        finally
        {
            file.Close();
        }
    }

    [Fact]
    public void GetEntries_MustNotInstantiateAllChildInodes()
    {
        var rootNode = LayerNode.Directory("/")
            .AddChild(LayerNode.File("a.txt", Encoding.UTF8.GetBytes("a")))
            .AddChild(LayerNode.File("b.txt", Encoding.UTF8.GetBytes("b")));
        var fs = new LayerFileSystem();
        var sb = fs.ReadSuper(
            new FileSystemType { Name = "layerfs" },
            0,
            "layer",
            new LayerMountOptions { Root = rootNode });

        var layerSb = Assert.IsType<LayerSuperBlock>(sb);
        Assert.Equal(1, GetCachedInodeObjectCount(layerSb));

        var entries = sb.Root.Inode!.GetEntries();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, static e => Assert.True(e.Ino > 0));
        Assert.Equal(1, GetCachedInodeObjectCount(layerSb));

        var dentry = sb.Root.Inode.Lookup("a.txt");
        Assert.NotNull(dentry);
        Assert.Equal(2, GetCachedInodeObjectCount(layerSb));
    }

    private static int GetCachedInodeObjectCount(LayerSuperBlock sb)
    {
        var field = typeof(LayerSuperBlock).GetField("_inodeByPath", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var map = Assert.IsAssignableFrom<System.Collections.IDictionary>(field!.GetValue(sb));
        return map.Count;
    }

    [Fact]
    public void MmapHold_ShouldBlockInodeEviction_UntilMunmap()
    {
        var rootNode = LayerNode.Directory("/")
            .AddChild(LayerNode.File("mapped.txt", Encoding.UTF8.GetBytes("abc")));
        var fs = new LayerFileSystem();
        var sb = fs.ReadSuper(
            new FileSystemType { Name = "layerfs" },
            0,
            "layer",
            new LayerMountOptions { Root = rootNode });

        using var engine = new Engine();
        var mm = new VMAManager();
        var dentry = sb.Root.Inode!.Lookup("mapped.txt");
        Assert.NotNull(dentry);
        sb.Root.CacheChild(dentry!, "LayerFsTests.MmapHold.cache");
        var inode = Assert.IsType<LayerInode>(dentry!.Inode);

        const uint mapAddr = 0x52000000;
        var mmapFile = new LinuxFile(dentry, FileFlags.O_RDONLY, null!, LinuxFile.ReferenceKind.MmapHold);
        mm.Mmap(
            mapAddr,
            LinuxConstants.PageSize,
            Protection.Read,
            MapFlags.Shared | MapFlags.Fixed,
            mmapFile,
            0,
            (long)inode.Size,
            "MAP_SHARED",
            engine);
        Assert.True(mm.HandleFault(mapAddr, false, engine));

        _ = VfsShrinker.DropDentryCache(sb);
        _ = VfsShrinker.EvictUnusedInodes(sb);
        Assert.False(inode.IsCacheEvicted);

        mm.Munmap(mapAddr, LinuxConstants.PageSize, engine);

        _ = VfsShrinker.DropDentryCache(sb);
        _ = VfsShrinker.EvictUnusedInodes(sb);
        Assert.True(inode.IsCacheEvicted);
    }

    [Fact]
    public void PageCacheReclaim_ShouldSkipMappedPage_AndReadStillCorrectAfterUnmap()
    {
        using var cacheScope = GlobalPageCacheManager.BeginIsolatedScope();
        var rootNode = LayerNode.Directory("/")
            .AddChild(LayerNode.File("reclaim.txt", Encoding.UTF8.GetBytes("hello")));
        var fs = new LayerFileSystem();
        var sb = fs.ReadSuper(
            new FileSystemType { Name = "layerfs" },
            0,
            "layer",
            new LayerMountOptions { Root = rootNode });

        using var engine = new Engine();
        var mm = new VMAManager();
        var dentry = sb.Root.Inode!.Lookup("reclaim.txt");
        Assert.NotNull(dentry);
        var inode = Assert.IsType<LayerInode>(dentry!.Inode);

        const uint mapAddr = 0x53000000;
        var mmapFile = new LinuxFile(dentry, FileFlags.O_RDONLY, null!, LinuxFile.ReferenceKind.MmapHold);
        mm.Mmap(
            mapAddr,
            LinuxConstants.PageSize,
            Protection.Read,
            MapFlags.Shared | MapFlags.Fixed,
            mmapFile,
            0,
            (long)inode.Size,
            "MAP_SHARED",
            engine);
        Assert.True(mm.HandleFault(mapAddr, false, engine));

        var cache = Assert.IsType<MemoryObject>(inode.PageCache);
        Assert.True(cache.PageCount > 0);

        var reclaimedWhileMapped = GlobalPageCacheManager.TryReclaimBytes(LinuxConstants.PageSize);
        Assert.True(reclaimedWhileMapped < LinuxConstants.PageSize);
        Assert.True(cache.PageCount > 0);

        mm.Munmap(mapAddr, LinuxConstants.PageSize, engine);

        var reclaimedAfterUnmap = GlobalPageCacheManager.TryReclaimBytes(LinuxConstants.PageSize);
        Assert.True(reclaimedAfterUnmap >= LinuxConstants.PageSize);
        Assert.Equal(0, cache.PageCount);

        var rf = new LinuxFile(dentry, FileFlags.O_RDONLY, null!);
        try
        {
            var buf = new byte[16];
            var n = inode.Read(rf, buf, 0);
            Assert.Equal(5, n);
            Assert.Equal("hello", Encoding.UTF8.GetString(buf, 0, n));
        }
        finally
        {
            rf.Close();
        }
    }

    [Fact]
    public void EndToEnd_ShrinkAll_PathWalkAndMmapCanRebuild()
    {
        using var cacheScope = GlobalPageCacheManager.BeginIsolatedScope();
        using var engine = new Engine();
        var mm = new VMAManager();
        var sm = new SyscallManager(engine, mm, 0);

        var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
        var rootSb = tmpfsType.CreateFileSystem().ReadSuper(tmpfsType, 0, "test-root", null);
        var rootMount = new Mount(rootSb, rootSb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
        sm.InitializeRoot(rootSb.Root, rootMount);

        var root = sm.Root.Dentry!;
        var mountPoint = root.Inode!.Lookup("mnt");
        if (mountPoint == null)
        {
            mountPoint = new Dentry("mnt", null, root, root.SuperBlock);
            root.Inode.Mkdir(mountPoint, 0x1ED, 0, 0);
            root.CacheChild(mountPoint, "LayerFsTests.EndToEnd.mountpoint");
        }

        var layerFs = new LayerFileSystem();
        var layerSb = layerFs.ReadSuper(
            new FileSystemType { Name = "layerfs" },
            0,
            "layer",
            new LayerMountOptions
            {
                Root = LayerNode.Directory("/")
                    .AddChild(LayerNode.File("e2e.txt", Encoding.UTF8.GetBytes("hello-layer")))
            });

        var detached = sm.CreateDetachedMount(layerSb, "layer", "layerfs", 0);
        var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
        Assert.Equal(0, sm.AttachDetachedMount(detached, target));

        var firstLoc = sm.PathWalkWithFlags("/mnt/e2e.txt", LookupFlags.FollowSymlink);
        Assert.True(firstLoc.IsValid);
        var firstDentry = firstLoc.Dentry!;
        var firstInode = Assert.IsType<LayerInode>(firstDentry.Inode);

        const uint mapAddr = 0x54000000;
        mm.Mmap(
            mapAddr,
            LinuxConstants.PageSize,
            Protection.Read,
            MapFlags.Shared | MapFlags.Fixed,
            new LinuxFile(firstDentry, FileFlags.O_RDONLY, firstLoc.Mount!, LinuxFile.ReferenceKind.MmapHold),
            0,
            (long)firstInode.Size,
            "MAP_SHARED",
            engine);
        Assert.True(mm.HandleFault(mapAddr, false, engine));

        var firstCache = Assert.IsType<MemoryObject>(firstInode.PageCache);
        Assert.True(firstCache.PageCount > 0);

        var firstShrink = VfsShrinker.Shrink(sm, VfsShrinkMode.PageCache | VfsShrinkMode.DentryCache | VfsShrinkMode.InodeCache);
        Assert.True(firstShrink.DentriesDropped >= 0);
        Assert.False(firstInode.IsCacheEvicted);
        Assert.True(firstCache.PageCount > 0);

        mm.Munmap(mapAddr, LinuxConstants.PageSize, engine);

        var secondShrink = VfsShrinker.Shrink(sm, VfsShrinkMode.PageCache | VfsShrinkMode.DentryCache | VfsShrinkMode.InodeCache);
        Assert.True(secondShrink.DentriesDropped > 0);
        Assert.True(secondShrink.InodesEvicted > 0);
        Assert.True(secondShrink.PageCacheBytesReclaimed >= LinuxConstants.PageSize);
        Assert.True(firstInode.IsCacheEvicted);

        var secondLoc = sm.PathWalkWithFlags("/mnt/e2e.txt", LookupFlags.FollowSymlink);
        Assert.True(secondLoc.IsValid);
        var secondInode = Assert.IsType<LayerInode>(secondLoc.Dentry!.Inode);
        Assert.NotSame(firstInode, secondInode);

        var rf = new LinuxFile(secondLoc.Dentry!, FileFlags.O_RDONLY, secondLoc.Mount!);
        try
        {
            var buffer = new byte[32];
            var n = secondInode.Read(rf, buffer, 0);
            Assert.Equal(11, n);
            Assert.Equal("hello-layer", Encoding.UTF8.GetString(buffer, 0, n));
        }
        finally
        {
            rf.Close();
        }

        mm.Mmap(
            mapAddr,
            LinuxConstants.PageSize,
            Protection.Read,
            MapFlags.Shared | MapFlags.Fixed,
            new LinuxFile(secondLoc.Dentry!, FileFlags.O_RDONLY, secondLoc.Mount!, LinuxFile.ReferenceKind.MmapHold),
            0,
            (long)secondInode.Size,
            "MAP_SHARED",
            engine);
        Assert.True(mm.HandleFault(mapAddr, false, engine));
        mm.Munmap(mapAddr, LinuxConstants.PageSize, engine);
    }
}
