using System.Text;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.SilkFS;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.VFS;

public class PageCacheConsistencyTests
{
    private readonly TestRuntimeFactory _runtime = new();
    private static readonly HostMemoryMapGeometry BufferedOnlyGeometry =
        new(LinuxConstants.PageSize, LinuxConstants.PageSize, LinuxConstants.PageSize, false, false);

    [Fact]
    public void Overlay_LayerfsCopyUpBeforePrivateMmap_UsesUpperTmpfsMappingAndData()
    {
        var overlaySb = CreateLayerLowerTmpfsUpperOverlay("hello");
        var fileDentry = LookupOverlayFile(overlaySb, "/bin/app");
        var overlayInode = Assert.IsType<OverlayInode>(fileDentry.Inode);
        var file = new LinuxFile(fileDentry, FileFlags.O_RDWR, null!);

        try
        {
            Assert.Equal(2, overlayInode.WriteFromHost(null, file, "XY"u8.ToArray(), 1));

            var direct = new byte[5];
            Assert.Equal(5, overlayInode.ReadToHost(null, file, direct, 0));
            Assert.Equal("hXYlo", Encoding.ASCII.GetString(direct));

            using var engine = _runtime.CreateEngine();
            var mm = _runtime.CreateAddressSpace();
            const uint mapAddr = 0x4C000000;

            mm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read,
                MapFlags.Private | MapFlags.Fixed, file, 0, "MAP_PRIVATE", engine);

            var vma = mm.FindVmArea(mapAddr);
            Assert.NotNull(vma);
            Assert.NotNull(overlayInode.UpperInode);
            Assert.NotNull(overlayInode.UpperInode!.Mapping);
            Assert.Same(overlayInode.UpperInode.Mapping, vma!.VmMapping);

            Assert.True(mm.HandleFault(mapAddr, false, engine));

            var mapped = new byte[5];
            Assert.True(engine.CopyFromUser(mapAddr, mapped));
            Assert.Equal("hXYlo", Encoding.ASCII.GetString(mapped));
        }
        finally
        {
            file.Close();
        }
    }

    [Fact]
    public void Overlay_LayerfsSharedMmap_CopyUp_RebindsVmaAndRefaultsFromUpperTmpfs()
    {
        var overlaySb = CreateLayerLowerTmpfsUpperOverlay("hello");
        var fileDentry = LookupOverlayFile(overlaySb, "/bin/app");
        var overlayInode = Assert.IsType<OverlayInode>(fileDentry.Inode);
        var mappedFile = new LinuxFile(fileDentry, FileFlags.O_RDWR, null!);
        var writerFile = new LinuxFile(fileDentry, FileFlags.O_RDWR, null!);
        LinuxFile? upperFile = null;

        try
        {
            using var engine = _runtime.CreateEngine();
            var mm = _runtime.CreateAddressSpace();
            const uint mapAddr = 0x4D000000;

            mm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, mappedFile, 0, "MAP_SHARED", engine);
            Assert.True(mm.HandleFault(mapAddr, false, engine));

            var initial = new byte[5];
            Assert.True(engine.CopyFromUser(mapAddr, initial));
            Assert.Equal("hello", Encoding.ASCII.GetString(initial));

            Assert.Equal(0, overlayInode.CopyUp(writerFile));
            Assert.NotNull(overlayInode.UpperDentry);
            upperFile = new LinuxFile(overlayInode.UpperDentry!, FileFlags.O_RDWR, null!);
            Assert.Equal(2, overlayInode.UpperInode!.WriteFromHost(null, upperFile, "XY"u8.ToArray(), 1));

            var direct = new byte[5];
            Assert.Equal(5, overlayInode.ReadToHost(null, writerFile, direct, 0));
            Assert.Equal("hXYlo", Encoding.ASCII.GetString(direct));

            var vma = mm.FindVmArea(mapAddr);
            Assert.NotNull(vma);
            Assert.NotNull(overlayInode.UpperInode);
            Assert.NotNull(overlayInode.UpperInode!.Mapping);
            Assert.Same(overlayInode.UpperInode.Mapping, vma!.VmMapping);

            mm.TearDownNativeMappings(engine, mapAddr, LinuxConstants.PageSize,
                false,
                false,
                true);

            Assert.True(mm.HandleFault(mapAddr, false, engine));

            var mapped = new byte[5];
            Assert.True(engine.CopyFromUser(mapAddr, mapped));
            Assert.Equal("hXYlo", Encoding.ASCII.GetString(mapped));
        }
        finally
        {
            upperFile?.Close();
            writerFile.Close();
            mappedFile.Close();
        }
    }

    [Fact]
    public void Overlay_LayerfsPrivateMmap_CopyUp_PreservesCowAnonPage()
    {
        var overlaySb = CreateLayerLowerTmpfsUpperOverlay("hello");
        var fileDentry = LookupOverlayFile(overlaySb, "/bin/app");
        var overlayInode = Assert.IsType<OverlayInode>(fileDentry.Inode);
        var mappedFile = new LinuxFile(fileDentry, FileFlags.O_RDWR, null!);
        var writerFile = new LinuxFile(fileDentry, FileFlags.O_RDWR, null!);
        Engine? engine = null;
        VMAManager? mm = null;

        try
        {
            engine = _runtime.CreateEngine();
            mm = _runtime.CreateAddressSpace();
            mm.BindOrAssertAddressSpaceHandle(engine);
            ProcessAddressSpaceSync.RegisterEngineAddressSpace(mm, engine);
            engine.PageFaultResolver = (addr, isWrite) => mm.HandleFault(addr, isWrite, engine);

            const uint mapAddr = 0x4D100000;
            mm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed, mappedFile, 0, "MAP_PRIVATE", engine);

            var initial = new byte[5];
            Assert.True(engine.CopyFromUser(mapAddr, initial));
            Assert.Equal("hello", Encoding.ASCII.GetString(initial));

            Assert.True(engine.CopyToUser(mapAddr + 1, "XY"u8.ToArray()));

            var vma = mm.FindVmArea(mapAddr);
            Assert.NotNull(vma);
            var pageIndex = vma!.GetPageIndex(mapAddr);
            var privatePage = vma.VmAnonVma!.GetPage(pageIndex);
            Assert.NotEqual(IntPtr.Zero, privatePage);
            Assert.True(mm.PageMapping.TryGet(mapAddr, out var mappedPtr));
            Assert.Equal(privatePage, mappedPtr);

            Assert.Equal(0, overlayInode.CopyUp(writerFile));

            Assert.False(mm.PageMapping.TryGet(mapAddr, out _));
            Assert.Equal(privatePage, vma.VmAnonVma!.GetPage(pageIndex));

            var mapped = new byte[5];
            Assert.True(engine.CopyFromUser(mapAddr, mapped));
            Assert.Equal("hXYlo", Encoding.ASCII.GetString(mapped));
            Assert.True(mm.PageMapping.TryGet(mapAddr, out var remappedPtr));
            Assert.Equal(privatePage, remappedPtr);
        }
        finally
        {
            if (mm != null && engine != null)
                ProcessAddressSpaceSync.UnregisterEngineAddressSpace(mm, engine);
            engine?.Dispose();
            writerFile.Close();
            mappedFile.Close();
        }
    }

    [Fact]
    public void Overlay_CopyUp_MetadataOnlyFallback_TearsDownStalePagesOnNextFault()
    {
        var payload = new byte[LinuxConstants.PageSize * 2];
        payload[0] = (byte)'A';
        payload[LinuxConstants.PageSize] = (byte)'B';

        var overlaySb = CreateTmpfsLowerTmpfsUpperOverlay("data.bin", payload);
        var fileDentry = LookupOverlayFile(overlaySb, "/data.bin");
        var overlayInode = Assert.IsType<OverlayInode>(fileDentry.Inode);
        var mappedFile = new LinuxFile(fileDentry, FileFlags.O_RDWR, null!);
        var writerFile = new LinuxFile(fileDentry, FileFlags.O_RDWR, null!);
        LinuxFile? upperFile = null;

        try
        {
            using var engine = _runtime.CreateEngine();
            var mm = _runtime.CreateAddressSpace();
            engine.PageFaultResolver = (addr, isWrite) => mm.HandleFault(addr, isWrite, engine);

            const uint mapAddr = 0x4D200000;
            mm.Mmap(mapAddr, LinuxConstants.PageSize * 2, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, mappedFile, 0, "MAP_SHARED", engine);

            var first = new byte[1];
            Assert.True(engine.CopyFromUser(mapAddr, first));
            Assert.Equal((byte)'A', first[0]);
            Assert.True(mm.PageMapping.TryGet(mapAddr, out var staleFirstPage));

            Assert.Equal(0, overlayInode.CopyUp(writerFile));
            Assert.NotNull(overlayInode.UpperDentry);
            upperFile = new LinuxFile(overlayInode.UpperDentry!, FileFlags.O_RDWR, null!);
            Assert.Equal(1, overlayInode.UpperInode!.WriteFromHost(null, upperFile, "X"u8.ToArray(), 0));

            var vma = mm.FindVmArea(mapAddr);
            Assert.NotNull(vma);
            Assert.Same(overlayInode.UpperInode.Mapping, vma!.VmMapping);
            Assert.True(mm.PageMapping.TryGet(mapAddr, out var stillMappedFirstPage));
            Assert.Equal(staleFirstPage, stillMappedFirstPage);

            var second = new byte[1];
            Assert.True(engine.CopyFromUser(mapAddr + LinuxConstants.PageSize, second));
            Assert.Equal((byte)'B', second[0]);
            Assert.False(mm.PageMapping.TryGet(mapAddr, out _));

            Assert.True(engine.CopyFromUser(mapAddr, first));
            Assert.Equal((byte)'X', first[0]);
        }
        finally
        {
            upperFile?.Close();
            writerFile.Close();
            mappedFile.Close();
        }
    }

    [Fact]
    public void Overlay_CopyUp_MetadataOnlyFallback_TearsDownStalePagesBeforeRun()
    {
        var payload = new byte[LinuxConstants.PageSize];
        payload[0] = (byte)'A';

        var overlaySb = CreateTmpfsLowerTmpfsUpperOverlay("data.bin", payload);
        var fileDentry = LookupOverlayFile(overlaySb, "/data.bin");
        var overlayInode = Assert.IsType<OverlayInode>(fileDentry.Inode);
        var mappedFile = new LinuxFile(fileDentry, FileFlags.O_RDWR, null!);
        var writerFile = new LinuxFile(fileDentry, FileFlags.O_RDWR, null!);
        LinuxFile? upperFile = null;

        try
        {
            using var engine = _runtime.CreateEngine();
            var mm = _runtime.CreateAddressSpace();
            engine.PageFaultResolver = (addr, isWrite) => mm.HandleFault(addr, isWrite, engine);

            const uint mapAddr = 0x4D300000;
            mm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, mappedFile, 0, "MAP_SHARED", engine);

            var first = new byte[1];
            Assert.True(engine.CopyFromUser(mapAddr, first));
            Assert.Equal((byte)'A', first[0]);
            Assert.True(mm.PageMapping.TryGet(mapAddr, out var staleFirstPage));

            Assert.Equal(0, overlayInode.CopyUp(writerFile));
            Assert.NotNull(overlayInode.UpperDentry);
            upperFile = new LinuxFile(overlayInode.UpperDentry!, FileFlags.O_RDWR, null!);
            Assert.Equal(1, overlayInode.UpperInode!.WriteFromHost(null, upperFile, "X"u8.ToArray(), 0));

            Assert.True(mm.PageMapping.TryGet(mapAddr, out var stillMappedFirstPage));
            Assert.Equal(staleFirstPage, stillMappedFirstPage);

            ProcessAddressSpaceSync.SyncEngineBeforeRun(mm, engine);

            Assert.False(mm.PageMapping.TryGet(mapAddr, out _));
            Assert.True(engine.CopyFromUser(mapAddr, first));
            Assert.Equal((byte)'X', first[0]);
        }
        finally
        {
            upperFile?.Close();
            writerFile.Close();
            mappedFile.Close();
        }
    }

    [Fact]
    public void Hostfs_MapSharedDirtyPage_IsVisibleToRead_BeforeWriteback()
    {
        var root = Path.Combine(Path.GetTempPath(), "hostfs-pagecache-consistency-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        File.WriteAllText(hostFile, "hello");

        try
        {
            using var engine = _runtime.CreateEngine();
            var mm = _runtime.CreateAddressSpace();
            var file = OpenHostFile(root, "data.bin");

            const uint mapAddr = 0x45000000;
            mm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, file, 0, "MAP_SHARED", engine);
            Assert.True(mm.HandleFault(mapAddr, true, engine));
            Assert.True(engine.CopyToUser(mapAddr + 1, "ZZ"u8.ToArray()));

            var buf = new byte[5];
            var n = file.Dentry.Inode!.ReadToHost(null, file, buf, 0);
            Assert.Equal(5, n);
            Assert.Equal("hZZlo", Encoding.ASCII.GetString(buf));
            Assert.Equal("hZZlo", File.ReadAllText(hostFile));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Tmpfs_MapSharedDirtyPage_IsVisibleToRead_BeforeWriteback()
    {
        using var engine = _runtime.CreateEngine();
        var mm = _runtime.CreateAddressSpace();
        var fsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var sb = fsType.CreateAnonymousFileSystem(_runtime.MemoryContext).ReadSuper(fsType, 0, "tmp", null);
        var root = sb.Root;
        var dentry = new Dentry(FsName.FromString("data.bin"), null, root, sb);
        root.Inode!.Create(dentry, 0x1B6, 0, 0);

        var file = new LinuxFile(dentry, FileFlags.O_RDWR, null!);
        Assert.Equal(5, dentry.Inode!.WriteFromHost(null, file, "hello"u8.ToArray(), 0));

        const uint mapAddr = 0x46000000;
        mm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write, MapFlags.Shared | MapFlags.Fixed,
            file, 0, "MAP_SHARED", engine);
        Assert.True(mm.HandleFault(mapAddr, true, engine));
        Assert.True(engine.CopyToUser(mapAddr + 2, "XY"u8.ToArray()));

        var buf = new byte[5];
        var n = dentry.Inode.ReadToHost(null, file, buf, 0);
        Assert.Equal(5, n);
        Assert.Equal("heXYo", Encoding.ASCII.GetString(buf));
    }

    [Fact]
    public void SharedAnonymousCloneStyleMappings_UseHiddenInodeBacking_AndShareBytes()
    {
        var runtime = new MemoryRuntimeContext();
        using var writeEngine = new Engine(runtime);
        using var readEngine = new Engine(runtime);
        var writeMm = new VMAManager(runtime);
        writeMm.BindOrAssertAddressSpaceHandle(writeEngine);

        const uint writeAddr = 0x47400000;
        var code1 = new byte[] { 0xB8, 0x78, 0x56, 0x34, 0x12 };
        var code2 = new byte[] { 0xB8, 0xEF, 0xBE, 0xAD, 0xDE };

        writeMm.Mmap(writeAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
            MapFlags.Shared | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "MAP_SHARED_ANON", writeEngine);

        var writeVma = Assert.IsType<VmArea>(writeMm.FindVmArea(writeAddr));
        Assert.True(writeVma.IsFileBacked);
        Assert.NotNull(writeVma.File?.OpenedInode);
        Assert.Same(runtime.GetOrCreateShmSuperBlock(), writeVma.File!.OpenedInode!.SuperBlock);

        var readMm = writeMm.Clone();
        readMm.BindOrAssertAddressSpaceHandle(readEngine);
        Assert.Equal(0, readMm.Mprotect(writeAddr, LinuxConstants.PageSize, Protection.Read, readEngine, out _));

        var readVma = Assert.IsType<VmArea>(readMm.FindVmArea(writeAddr));
        Assert.True(readVma.IsFileBacked);
        Assert.Same(writeVma.File!.OpenedInode, readVma.File!.OpenedInode);

        Assert.True(writeMm.HandleFault(writeAddr, true, writeEngine));
        Assert.True(writeEngine.CopyToUser(writeAddr, code1));
        Assert.True(readMm.HandleFault(writeAddr, false, readEngine));
        var mapped = new byte[code1.Length];
        Assert.True(readEngine.CopyFromUser(writeAddr, mapped));
        Assert.Equal(code1, mapped);

        Assert.True(writeEngine.CopyToUser(writeAddr, code2));
        Assert.True(readEngine.CopyFromUser(writeAddr, mapped));
        Assert.Equal(code2, mapped);
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
            lowerSb.Root = lowerSb.GetDentry(tempLower, FsName.Empty, null)!;

            var tmpType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
            var upperSb = tmpType.CreateAnonymousFileSystem(_runtime.MemoryContext).ReadSuper(tmpType, 0, "ovl-upper", null);

            var overlayFs = new OverlayFileSystem();
            var overlaySb = (OverlaySuperBlock)overlayFs.ReadSuper(
                new FileSystemType { Name = "overlay" },
                0,
                "overlay",
                new OverlayMountOptions { Lower = lowerSb, Upper = upperSb });

            var fileDentry = overlaySb.Root.Inode!.Lookup("data.bin");
            Assert.NotNull(fileDentry);
            var file = new LinuxFile(fileDentry!, FileFlags.O_RDWR, null!);

            using var engine = _runtime.CreateEngine();
            var mm = _runtime.CreateAddressSpace();
            const uint mapAddr = 0x47000000;
            mm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, file, 0, "MAP_SHARED", engine);
            Assert.True(mm.HandleFault(mapAddr, true, engine));
            Assert.True(engine.CopyToUser(mapAddr + 3, "PQ"u8.ToArray()));

            var buf = new byte[5];
            var n = file.Dentry.Inode!.ReadToHost(null, file, buf, 0);
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
            using var engine = _runtime.CreateEngine();
            var mm = _runtime.CreateAddressSpace();
            var file = OpenHostFile(root, "data.bin");

            const uint mapAddr = 0x48000000;
            mm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, file, 0, "MAP_SHARED", engine);
            Assert.True(mm.HandleFault(mapAddr, false, engine));

            var rc = file.Dentry.Inode!.WriteFromHost(null, file, "XY"u8.ToArray(), 1);
            Assert.Equal(2, rc);

            Assert.Equal("hXYlo", File.ReadAllText(hostFile));

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
        using var engine = _runtime.CreateEngine();
        var mm = _runtime.CreateAddressSpace();
        var fsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var sb = fsType.CreateAnonymousFileSystem(_runtime.MemoryContext).ReadSuper(fsType, 0, "tmp", null);
        var root = sb.Root;
        var dentry = new Dentry(FsName.FromString("data.bin"), null, root, sb);
        root.Inode!.Create(dentry, 0x1B6, 0, 0);
        var file = new LinuxFile(dentry, FileFlags.O_RDWR, null!);
        Assert.Equal(5, dentry.Inode!.WriteFromHost(null, file, "hello"u8.ToArray(), 0));

        const uint mapAddr = 0x49000000;
        mm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write, MapFlags.Shared | MapFlags.Fixed,
            file, 0, "MAP_SHARED", engine);
        Assert.True(mm.HandleFault(mapAddr, false, engine));

        var rc = dentry.Inode.WriteFromHost(null, file, "MN"u8.ToArray(), 2);
        Assert.Equal(2, rc);

        var mapped = new byte[5];
        Assert.True(engine.CopyFromUser(mapAddr, mapped));
        Assert.Equal("heMNo", Encoding.ASCII.GetString(mapped));
    }

    [Fact]
    public void Tmpfs_WriteBeforeMmap_UsesSamePageCacheObject()
    {
        using var engine = _runtime.CreateEngine();
        var mm = _runtime.CreateAddressSpace();
        var fsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var sb = fsType.CreateAnonymousFileSystem(_runtime.MemoryContext).ReadSuper(fsType, 0, "tmp", null);
        var root = sb.Root;
        var dentry = new Dentry(FsName.FromString("data.bin"), null, root, sb);
        root.Inode!.Create(dentry, 0x1B6, 0, 0);
        var file = new LinuxFile(dentry, FileFlags.O_RDWR, null!);

        Assert.Equal(5, dentry.Inode!.WriteFromHost(null, file, "hello"u8.ToArray(), 0));
        var beforeMapCache = dentry.Inode.Mapping;
        Assert.NotNull(beforeMapCache);

        const uint mapAddr = 0x4B000000;
        mm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write, MapFlags.Shared | MapFlags.Fixed,
            file, 0, "MAP_SHARED", engine);
        Assert.True(mm.HandleFault(mapAddr, false, engine));
        Assert.Same(beforeMapCache, dentry.Inode.Mapping);

        var mapped = new byte[5];
        Assert.True(engine.CopyFromUser(mapAddr, mapped));
        Assert.Equal("hello", Encoding.ASCII.GetString(mapped));
    }

    [Fact]
    public void Silkfs_WriteBeforeMmap_UsesSamePageCacheObject_AndPreservesPartialWriteData()
    {
        var silkRoot = Path.Combine(Path.GetTempPath(), "silkfs-pagecache-consistency-" + Guid.NewGuid().ToString("N"));

        try
        {
            using var engine = _runtime.CreateEngine();
            var mm = _runtime.CreateAddressSpace();
            var repo = new SilkRepository(SilkFsOptions.FromSource(silkRoot));
            repo.Initialize();

            var sb = new SilkSuperBlock(new FileSystemType { Name = "silkfs" }, repo, new DeviceNumberManager());
            sb.LoadFromMetadata();

            var root = sb.Root;
            var dentry = new Dentry(FsName.FromString("data.bin"), null, root, sb);
            Assert.Equal(0, root.Inode!.Create(dentry, 0x1B6, 0, 0));

            using var file = new LinuxFile(dentry, FileFlags.O_RDWR, null!);
            Assert.Equal(5, dentry.Inode!.WriteFromHost(null, file, "hello"u8.ToArray(), 0));

            var silkInode = Assert.IsType<SilkInode>(dentry.Inode);
            var beforeMapCache = silkInode.Mapping;
            Assert.NotNull(beforeMapCache);

            Assert.Equal(2, silkInode.WriteFromHost(null, file, "XY"u8.ToArray(), 1));
            var direct = new byte[5];
            Assert.Equal(5, silkInode.ReadToHost(null, file, direct, 0));
            Assert.Equal("hXYlo", Encoding.ASCII.GetString(direct));

            const uint mapAddr = 0x4B100000;
            mm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed,
                file, 0, "MAP_SHARED", engine);
            Assert.True(mm.HandleFault(mapAddr, false, engine));
            Assert.Same(beforeMapCache, silkInode.Mapping);

            var mapped = new byte[5];
            Assert.True(engine.CopyFromUser(mapAddr, mapped));
            Assert.Equal("hXYlo", Encoding.ASCII.GetString(mapped));
        }
        finally
        {
            if (Directory.Exists(silkRoot))
                Directory.Delete(silkRoot, true);
        }
    }

    [Fact]
    public void Silkfs_RefaultAfterBackingShrink_MustFailInsteadOfZeroFillingTail()
    {
        var runtime = new TestRuntimeFactory(BufferedOnlyGeometry);
        var silkRoot = Path.Combine(Path.GetTempPath(), "silkfs-refault-short-read-" + Guid.NewGuid().ToString("N"));

        try
        {
            using var engine = runtime.CreateEngine();
            var repo = new SilkRepository(SilkFsOptions.FromSource(silkRoot));
            repo.Initialize();

            var sb = new SilkSuperBlock(new FileSystemType { Name = "silkfs" }, repo, new DeviceNumberManager(),
                runtime.MemoryContext);
            sb.LoadFromMetadata();

            var root = sb.Root;
            var dentry = new Dentry(FsName.FromString("data.bin"), null, root, sb);
            Assert.Equal(0, root.Inode!.Create(dentry, 0x1B6, 0, 0));

            using var file = new LinuxFile(dentry, FileFlags.O_RDWR, null!);
            Assert.Equal(5, dentry.Inode!.WriteFromHost(null, file, "hello"u8.ToArray(), 0));

            var silkInode = Assert.IsType<SilkInode>(dentry.Inode);
            var cache = Assert.IsType<AddressSpace>(silkInode.Mapping);
            silkInode.Sync(file);
            Assert.False(cache.IsDirty(0));

            var warm = new byte[5];
            Assert.Equal(5, silkInode.ReadToHost(null, file, warm, 0));
            Assert.Equal("hello", Encoding.ASCII.GetString(warm));
            Assert.NotEqual(IntPtr.Zero, cache.GetPage(0));

            repo.TruncateLiveInodeData((long)silkInode.Ino, 2);

            var reclaimed = runtime.MemoryContext.AddressSpacePolicy.TryReclaimBytes(LinuxConstants.PageSize);
            Assert.True(reclaimed >= LinuxConstants.PageSize);
            Assert.Equal(0, cache.PageCount);

            var readBack = new byte[5];
            var rc = silkInode.ReadToHost(null, file, readBack, 0);
            Assert.Equal(-(int)Errno.EIO, rc);
        }
        finally
        {
            if (Directory.Exists(silkRoot))
                Directory.Delete(silkRoot, true);
        }
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
            lowerSb.Root = lowerSb.GetDentry(tempLower, FsName.Empty, null)!;

            var tmpType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
            var upperSb = tmpType.CreateAnonymousFileSystem(_runtime.MemoryContext).ReadSuper(tmpType, 0, "ovl-upper-reverse", null);

            var overlayFs = new OverlayFileSystem();
            var overlaySb = (OverlaySuperBlock)overlayFs.ReadSuper(
                new FileSystemType { Name = "overlay" },
                0,
                "overlay",
                new OverlayMountOptions { Lower = lowerSb, Upper = upperSb });

            var fileDentry = overlaySb.Root.Inode!.Lookup("data.bin");
            Assert.NotNull(fileDentry);
            var file = new LinuxFile(fileDentry!, FileFlags.O_RDWR, null!);

            using var engine = _runtime.CreateEngine();
            var mm = _runtime.CreateAddressSpace();
            const uint mapAddr = 0x4A000000;
            mm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, file, 0, "MAP_SHARED", engine);
            Assert.True(mm.HandleFault(mapAddr, false, engine));

            var rc = file.Dentry.Inode!.WriteFromHost(null, file, "UV"u8.ToArray(), 3);
            Assert.Equal(2, rc);

            Assert.True(mm.HandleFault(mapAddr, false, engine));

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

    [Fact]
    public void Hostfs_WritePath_MustMarkMemoryObjectDirty_BeforeWriteback()
    {
        var root = Path.Combine(Path.GetTempPath(), "hostfs-pagecache-dirty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        File.WriteAllText(hostFile, "hello");

        LinuxFile? file = null;
        AddressSpace? cache = null;
        try
        {
            file = OpenHostFile(root, "data.bin");
            var inode = file.Dentry.Inode!;
            cache = ((MappingBackedInode)inode).AcquireMappingRef();

            var rc = inode.WriteFromHost(null, file, "XY"u8.ToArray(), 1);
            Assert.Equal(2, rc);
            Assert.Equal("hXYlo", File.ReadAllText(hostFile));

            // Unflushed cached page must not be reclaimable as "clean".
            Assert.True(cache.IsDirty(0));
            Assert.False(cache.TryEvictCleanPage(0));

            var buf = new byte[5];
            var n = inode.ReadToHost(null, file, buf, 0);
            Assert.Equal(5, n);
            Assert.Equal("hXYlo", Encoding.ASCII.GetString(buf));
        }
        finally
        {
            if (file?.Dentry.Inode != null) file.Dentry.Inode.Release(file);
            cache?.Release();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Tmpfs_WritePath_MustMarkMemoryObjectDirty_BeforeWriteback()
    {
        var fsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var sb = fsType.CreateAnonymousFileSystem(_runtime.MemoryContext).ReadSuper(fsType, 0, "tmp", null);
        var root = sb.Root;
        var dentry = new Dentry(FsName.FromString("data.bin"), null, root, sb);
        root.Inode!.Create(dentry, 0x1B6, 0, 0);
        var file = new LinuxFile(dentry, FileFlags.O_RDWR, null!);

        var inode = dentry.Inode!;
        var rc = inode.WriteFromHost(null, file, "hello"u8.ToArray(), 0);
        Assert.Equal(5, rc);
        var cache = Assert.IsType<AddressSpace>(inode.Mapping);

        // tmpfs data is shmem resident; unflushed page must not be reclaimable as "clean".
        Assert.True(cache.IsDirty(0));
        Assert.False(cache.TryEvictCleanPage(0));

        var buf = new byte[5];
        var n = inode.ReadToHost(null, file, buf, 0);
        Assert.Equal(5, n);
        Assert.Equal("hello", Encoding.ASCII.GetString(buf));
    }

    [Fact]
    public void Hostfs_TruncateShrinkThenGrow_MustNotExposeStalePageCacheData()
    {
        var root = Path.Combine(Path.GetTempPath(), "hostfs-truncate-stale-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        File.WriteAllText(hostFile, "hello");

        LinuxFile? file = null;
        AddressSpace? cache = null;
        try
        {
            file = OpenHostFile(root, "data.bin");
            var inode = file.Dentry.Inode!;
            cache = ((MappingBackedInode)inode).AcquireMappingRef();

            var warm = new byte[5];
            var warmN = inode.ReadToHost(null, file, warm, 0);
            Assert.Equal(5, warmN);
            Assert.Equal("hello", Encoding.ASCII.GetString(warm));
            Assert.NotEqual(IntPtr.Zero, cache.GetPage(0));

            Assert.Equal(0, inode.Truncate(0));
            Assert.Equal(0, inode.Truncate(5));
            Assert.Equal(5, File.ReadAllBytes(hostFile).Length);

            var readBack = new byte[5];
            var n = inode.ReadToHost(null, file, readBack, 0);
            Assert.Equal(5, n);
            Assert.Equal(new byte[5], readBack);
        }
        finally
        {
            if (file?.Dentry.Inode != null) file.Dentry.Inode.Release(file);
            cache?.Release();
            Directory.Delete(root, true);
        }
    }

    private static LinuxFile OpenHostFile(string rootDir, string relativePath)
    {
        var fsType = new FileSystemType { Name = "hostfs" };
        var opts = HostfsMountOptions.Parse("rw");
        var sb = new HostSuperBlock(fsType, rootDir, opts);
        sb.Root = sb.GetDentry(rootDir, FsName.Empty, null)!;
        var dentry = sb.Root.Inode!.Lookup(relativePath);
        Assert.NotNull(dentry);
        var file = new LinuxFile(dentry!, FileFlags.O_RDWR, null!);
        dentry!.Inode!.Open(file);
        return file;
    }

    private OverlaySuperBlock CreateLayerLowerTmpfsUpperOverlay(string fileContents)
    {
        var payload = Encoding.ASCII.GetBytes(fileContents);
        var index = new LayerIndex();
        index.AddEntry(new LayerIndexEntry("/bin", InodeType.Directory, 0x1ED));
        index.AddEntry(new LayerIndexEntry(
            "/bin/app",
            InodeType.File,
            0x1A4,
            Size: (ulong)payload.Length,
            InlineData: payload));

        var layerFs = new LayerFileSystem(memoryContext: _runtime.MemoryContext);
        var lowerSb = layerFs.ReadSuper(
            new FileSystemType { Name = "layerfs" },
            0,
            "test-layer-lower",
            new LayerMountOptions { Index = index, ContentProvider = new InMemoryLayerContentProvider() });

        var tmpType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var upperSb = tmpType.CreateAnonymousFileSystem(_runtime.MemoryContext)
            .ReadSuper(tmpType, 0, "test-tmpfs-upper", null);

        var overlayFs = new OverlayFileSystem();
        return (OverlaySuperBlock)overlayFs.ReadSuper(
            new FileSystemType { Name = "overlay" },
            0,
            "test-overlay",
            new OverlayMountOptions { Lower = lowerSb, Upper = upperSb });
    }

    private OverlaySuperBlock CreateTmpfsLowerTmpfsUpperOverlay(string fileName, byte[] payload)
    {
        var tmpType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
        var lowerSb = tmpType.CreateAnonymousFileSystem(_runtime.MemoryContext)
            .ReadSuper(tmpType, 0, "test-tmpfs-lower", null);
        var lowerFileDentry = new Dentry(FsName.FromString(fileName), null, lowerSb.Root, lowerSb);
        lowerSb.Root.Inode!.Create(lowerFileDentry, 0x1A4, 0, 0);
        var lowerFile = new LinuxFile(lowerFileDentry, FileFlags.O_RDWR, null!);
        try
        {
            Assert.Equal(payload.Length, lowerFileDentry.Inode!.WriteFromHost(null, lowerFile, payload, 0));
        }
        finally
        {
            lowerFile.Close();
        }

        var upperSb = tmpType.CreateAnonymousFileSystem(_runtime.MemoryContext)
            .ReadSuper(tmpType, 0, "test-tmpfs-upper", null);

        var overlayFs = new OverlayFileSystem();
        return (OverlaySuperBlock)overlayFs.ReadSuper(
            new FileSystemType { Name = "overlay" },
            0,
            "test-overlay",
            new OverlayMountOptions { Lower = lowerSb, Upper = upperSb });
    }

    private static Dentry LookupOverlayFile(OverlaySuperBlock overlaySb, string path)
    {
        var current = overlaySb.Root;
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current.Inode!.Lookup(segment);
            Assert.NotNull(current);
        }

        return current!;
    }
}
