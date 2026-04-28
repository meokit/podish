using System.Reflection;
using System.Text;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.VFS;

public class HostMappedPageCacheGeometryTests
{
    private readonly TestRuntimeFactory _runtime = new();

    private static readonly HostMemoryMapGeometry Geometry16K =
        new(LinuxConstants.PageSize, 16384, 16384, true,
            true);

    [Fact]
    public void Hostfs_Reuses_Shared_MappingBackedInode_VectoredIo_With_HostHooks()
    {
        Assert.Equal(typeof(MappingBackedInode),
            typeof(HostInode).GetMethod(nameof(HostInode.ReadV), BindingFlags.Public | BindingFlags.Instance)!.DeclaringType);
        Assert.Equal(typeof(MappingBackedInode),
            typeof(HostInode).GetMethod(nameof(HostInode.WriteV), BindingFlags.Public | BindingFlags.Instance)!.DeclaringType);
        Assert.Equal(typeof(HostInode),
            typeof(HostInode).GetMethod("TryAcquireHostMappedPageLeases", BindingFlags.NonPublic | BindingFlags.Instance)!
                .DeclaringType);
        Assert.Equal(typeof(HostInode),
            typeof(HostInode).GetMethod("PrepareHostMappedWrite", BindingFlags.NonPublic | BindingFlags.Instance)!
                .DeclaringType);
        Assert.Equal(typeof(HostInode),
            typeof(HostInode).GetMethod("OnMappingPagesMarkedDirty", BindingFlags.NonPublic | BindingFlags.Instance)!
                .DeclaringType);
        Assert.Equal(typeof(HostInode),
            typeof(HostInode).GetMethod("OnWriteCompleted", BindingFlags.NonPublic | BindingFlags.Instance)!
                .DeclaringType);
        Assert.Equal(typeof(HostInode),
            typeof(HostInode).GetMethod("ReleaseHostMappedPageLease", BindingFlags.NonPublic | BindingFlags.Instance)!
                .DeclaringType);
    }

    [Fact]
    public void Silkfs_Rebases_Directly_On_MappingBackedInode()
    {
        Assert.Equal(typeof(MappingBackedInode), typeof(SilkInode).BaseType);
        Assert.Equal(typeof(MappingBackedInode),
            typeof(SilkInode).GetMethod(nameof(SilkInode.ReadV), BindingFlags.Public | BindingFlags.Instance)!
                .DeclaringType);
        Assert.Equal(typeof(MappingBackedInode),
            typeof(SilkInode).GetMethod(nameof(SilkInode.WriteV), BindingFlags.Public | BindingFlags.Instance)!
                .DeclaringType);
    }

    [Fact]
    public void Hostfs_16KGeometry_CoalescesFourGuestPagesIntoSingleWindow()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hostfs-16k-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        var bytes = new byte[LinuxConstants.PageSize * 4];
        Array.Fill(bytes, (byte)'a');
        File.WriteAllBytes(hostFile, bytes);

        try
        {
            var runtime = new MemoryRuntimeContext(Geometry16K);
            using var engine = new Engine(runtime);
            var mm = new VMAManager(runtime);
            var sm = new SyscallManager(engine, mm, 0);
            sm.MountRootHostfs(root);
            var loc = sm.PathWalkWithFlags("/data.bin", LookupFlags.FollowSymlink);
            Assert.True(loc.IsValid);

            var file = new LinuxFile(loc.Dentry!, FileFlags.O_RDWR, loc.Mount!);
            loc.Dentry!.Inode!.Open(file);
            const uint mapAddr = 0x47000000;
            mm.Mmap(mapAddr, LinuxConstants.PageSize * 4, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, file, 0, "MAP_SHARED", engine);

            for (var i = 0; i < 4; i++)
                Assert.True(mm.HandleFault(mapAddr + (uint)(i * LinuxConstants.PageSize), true, engine));

            var hostInode = Assert.IsType<HostInode>(loc.Dentry!.Inode);
            var diagnostics = hostInode.GetMappedPageCacheDiagnostics();
            Assert.Equal(1, diagnostics.WindowCount);
            Assert.Equal(16384, diagnostics.WindowBytes);
            Assert.Equal(4, diagnostics.GuestPageCount);

            Assert.True(engine.CopyToUser(mapAddr + 1, "XY"u8.ToArray()));
            Assert.True(engine.CopyToUser(mapAddr + 3 * LinuxConstants.PageSize + 2, "ZZ"u8.ToArray()));

            var vma = Assert.Single(mm.VMAs.Where(v => v.Start == mapAddr));
            VMAManager.SyncVmArea(vma, engine, mapAddr, mapAddr + LinuxConstants.PageSize * 4);

            var updated = ReadAllBytesWithUnixCompatibleSharing(hostFile);
            Assert.Equal((byte)'X', updated[1]);
            Assert.Equal((byte)'Y', updated[2]);
            Assert.Equal((byte)'Z', updated[3 * LinuxConstants.PageSize + 2]);
            Assert.Equal((byte)'Z', updated[3 * LinuxConstants.PageSize + 3]);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Hostfs_ReadOnlyMount_UsesReadonlyDirectMappedWindow()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hostfs-ro-16k-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        var bytes = new byte[LinuxConstants.PageSize];
        Array.Fill(bytes, (byte)'r');
        File.WriteAllBytes(hostFile, bytes);

        try
        {
            var runtime = new MemoryRuntimeContext(Geometry16K);
            using var engine = new Engine(runtime);
            var mm = new VMAManager(runtime);
            var sm = new SyscallManager(engine, mm, 0);
            sm.MountRootHostfs(root, "ro");
            var loc = sm.PathWalkWithFlags("/data.bin", LookupFlags.FollowSymlink);
            Assert.True(loc.IsValid);

            var file = new LinuxFile(loc.Dentry!, FileFlags.O_RDONLY, loc.Mount!);
            loc.Dentry!.Inode!.Open(file);
            const uint mapAddr = 0x47100000;
            mm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read, MapFlags.Shared | MapFlags.Fixed, file, 0,
                "MAP_SHARED_RO", engine);
            Assert.True(mm.HandleFault(mapAddr, false, engine));

            var hostInode = Assert.IsType<HostInode>(loc.Dentry!.Inode);
            var diagnostics = hostInode.GetMappedPageCacheDiagnostics();
            Assert.Equal(1, diagnostics.WindowCount);
            Assert.Equal(1, diagnostics.GuestPageCount);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Silkfs_16KGeometry_CoalescesFourGuestPagesIntoSingleWindow_AndPersists()
    {
        var silkRoot = Path.Combine(Path.GetTempPath(), $"silkfs-16k-{Guid.NewGuid():N}");

        try
        {
            {
                var runtime = new MemoryRuntimeContext(Geometry16K);
                using var engine = new Engine(runtime);
                var mm = new VMAManager(runtime);
                var sm = CreateTmpfsRoot(engine, mm);
                var mountLoc = MountSilkfs(sm, silkRoot);

                var file = new Dentry(FsName.FromString("mapped.bin"), null, mountLoc.Dentry, mountLoc.Dentry!.SuperBlock);
                mountLoc.Dentry.Inode!.Create(file, 0x1A4, 0, 0);
                var initial = new byte[LinuxConstants.PageSize * 4];
                Array.Fill(initial, (byte)'s');
                var wf = new LinuxFile(file, FileFlags.O_WRONLY, mountLoc.Mount!);
                Assert.Equal(initial.Length, file.Inode!.WriteFromHost(null, wf, initial, 0));
                wf.Close();
                sm.Close();
            }

            {
                var runtime = new MemoryRuntimeContext(Geometry16K);
                using var engine = new Engine(runtime);
                var mm = new VMAManager(runtime);
                var sm = CreateTmpfsRoot(engine, mm);
                MountSilkfs(sm, silkRoot);
                var fileLoc = sm.PathWalkWithFlags("/mnt/mapped.bin", LookupFlags.FollowSymlink);
                Assert.True(fileLoc.IsValid);
                var file = fileLoc.Dentry!;
                var mappedFile = new LinuxFile(file, FileFlags.O_RDWR, fileLoc.Mount!);
                file.Inode!.Open(mappedFile);
                const uint mapAddr = 0x47200000;
                mm.Mmap(mapAddr, LinuxConstants.PageSize * 4, Protection.Read | Protection.Write,
                    MapFlags.Shared | MapFlags.Fixed, mappedFile, 0, "MAP_SHARED", engine);

                for (var i = 0; i < 4; i++)
                    Assert.True(mm.HandleFault(mapAddr + (uint)(i * LinuxConstants.PageSize), true, engine));

                var silkInode = Assert.IsType<SilkInode>(file.Inode);
                var diagnostics = silkInode.GetMappedPageCacheDiagnostics();
                Assert.Equal(1, diagnostics.WindowCount);
                Assert.Equal(16384, diagnostics.WindowBytes);
                Assert.Equal(4, diagnostics.GuestPageCount);

                Assert.True(engine.CopyToUser(mapAddr + 1, "UV"u8.ToArray()));
                Assert.True(engine.CopyToUser(mapAddr + 3 * LinuxConstants.PageSize + 2, "WX"u8.ToArray()));
                var vma = Assert.Single(mm.VMAs.Where(v => v.Start == mapAddr));
                VMAManager.SyncVmArea(vma, engine, mapAddr, mapAddr + LinuxConstants.PageSize * 4);
                mappedFile.Close();
                sm.Close();
            }

            {
                var runtime = new MemoryRuntimeContext(Geometry16K);
                using var engine = new Engine(runtime);
                var mm = new VMAManager(runtime);
                var sm = CreateTmpfsRoot(engine, mm);
                MountSilkfs(sm, silkRoot);
                var fileLoc = sm.PathWalkWithFlags("/mnt/mapped.bin", LookupFlags.FollowSymlink);
                Assert.True(fileLoc.IsValid);
                var rf = new LinuxFile(fileLoc.Dentry!, FileFlags.O_RDONLY, fileLoc.Mount!);
                var data = new byte[LinuxConstants.PageSize * 4];
                var n = fileLoc.Dentry!.Inode!.ReadToHost(null, rf, data, 0);
                rf.Close();
                Assert.Equal(data.Length, n);
                Assert.Equal((byte)'U', data[1]);
                Assert.Equal((byte)'V', data[2]);
                Assert.Equal((byte)'W', data[3 * LinuxConstants.PageSize + 2]);
                Assert.Equal((byte)'X', data[3 * LinuxConstants.PageSize + 3]);
                sm.Close();
            }
        }
        finally
        {
            if (Directory.Exists(silkRoot)) Directory.Delete(silkRoot, true);
        }
    }

    [Fact]
    public void Hostfs_SparseWrite_PreExtendsAndUsesSingleDirectMappedWindow()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hostfs-sparse-16k-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        File.WriteAllBytes(hostFile, []);

        try
        {
            var runtime = new MemoryRuntimeContext(Geometry16K);
            using var engine = new Engine(runtime);
            var mm = new VMAManager(runtime);
            var sm = new SyscallManager(engine, mm, 0);
            sm.MountRootHostfs(root);
            var loc = sm.PathWalkWithFlags("/data.bin", LookupFlags.FollowSymlink);
            Assert.True(loc.IsValid);

            var file = new LinuxFile(loc.Dentry!, FileFlags.O_RDWR, loc.Mount!);
            loc.Dentry!.Inode!.Open(file);
            var hostInode = Assert.IsType<HostInode>(loc.Dentry!.Inode);

            Assert.Equal(2, hostInode.WriteFromHost(null, file, "p3"u8.ToArray(), 3L * LinuxConstants.PageSize));
            Assert.Equal(2, hostInode.WriteFromHost(null, file, "p2"u8.ToArray(), 2L * LinuxConstants.PageSize));

            var diagnostics = hostInode.GetMappedPageCacheDiagnostics();
            Assert.Equal(1, diagnostics.WindowCount);
            Assert.True(diagnostics.WindowBytes >= LinuxConstants.PageSize);
            Assert.True(diagnostics.GuestPageCount >= 1);

            file.Close();

            var data = ReadAllBytesWithUnixCompatibleSharing(hostFile);
            Assert.Equal(3 * LinuxConstants.PageSize + 2, data.Length);
            Assert.Equal("p2"u8.ToArray(), data.AsSpan(2 * LinuxConstants.PageSize, 2).ToArray());
            Assert.Equal("p3"u8.ToArray(), data.AsSpan(3 * LinuxConstants.PageSize, 2).ToArray());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Hostfs_WriteV_16KGeometry_WritesFourGuestPagesInSingleWindow()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hostfs-writev-16k-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        File.WriteAllBytes(hostFile, []);

        try
        {
            var runtime = new MemoryRuntimeContext(Geometry16K);
            using var engine = new Engine(runtime);
            var mm = new VMAManager(runtime);
            var sm = new SyscallManager(engine, mm, 0);
            sm.MountRootHostfs(root);
            var loc = sm.PathWalkWithFlags("/data.bin", LookupFlags.FollowSymlink);
            Assert.True(loc.IsValid);

            var file = new LinuxFile(loc.Dentry!, FileFlags.O_RDWR, loc.Mount!);
            loc.Dentry!.Inode!.Open(file);
            var inode = Assert.IsType<HostInode>(loc.Dentry!.Inode);

            const uint baseAddr = 0x48000000;
            for (var i = 0; i < 4; i++)
            {
                var pageAddr = baseAddr + (uint)(i * LinuxConstants.PageSize);
                mm.Mmap(pageAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                    MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[writev-test]", engine);
                Assert.True(mm.HandleFault(pageAddr, true, engine));
                var page = new byte[LinuxConstants.PageSize];
                Array.Fill(page, (byte)('A' + i));
                Assert.True(engine.CopyToUser(pageAddr, page));
            }

            var iovs = new[]
            {
                new Iovec { BaseAddr = baseAddr, Len = LinuxConstants.PageSize },
                new Iovec { BaseAddr = baseAddr + LinuxConstants.PageSize, Len = LinuxConstants.PageSize },
                new Iovec { BaseAddr = baseAddr + 2 * LinuxConstants.PageSize, Len = LinuxConstants.PageSize },
                new Iovec { BaseAddr = baseAddr + 3 * LinuxConstants.PageSize, Len = LinuxConstants.PageSize }
            };

            var written = await inode.WriteV(engine, file, null, iovs, 0, 0);
            Assert.Equal(LinuxConstants.PageSize * 4, written);

            var diagnostics = inode.GetMappedPageCacheDiagnostics();
            Assert.Equal(1, diagnostics.WindowCount);
            Assert.Equal(16384, diagnostics.WindowBytes);
            Assert.True(diagnostics.GuestPageCount >= 4);

            file.Close();
            var data = ReadAllBytesWithUnixCompatibleSharing(hostFile);
            Assert.Equal(LinuxConstants.PageSize * 4, data.Length);
            Assert.Equal((byte)'A', data[0]);
            Assert.Equal((byte)'B', data[LinuxConstants.PageSize]);
            Assert.Equal((byte)'C', data[LinuxConstants.PageSize * 2]);
            Assert.Equal((byte)'D', data[LinuxConstants.PageSize * 3]);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Hostfs_ReadV_16KGeometry_ReadsFourGuestPagesInSingleWindow()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hostfs-readv-16k-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        var data = new byte[LinuxConstants.PageSize * 4];
        for (var i = 0; i < 4; i++)
            Array.Fill(data, (byte)('a' + i), i * LinuxConstants.PageSize, LinuxConstants.PageSize);
        File.WriteAllBytes(hostFile, data);

        try
        {
            var runtime = new MemoryRuntimeContext(Geometry16K);
            using var engine = new Engine(runtime);
            var mm = new VMAManager(runtime);
            var sm = new SyscallManager(engine, mm, 0);
            sm.MountRootHostfs(root);
            var loc = sm.PathWalkWithFlags("/data.bin", LookupFlags.FollowSymlink);
            Assert.True(loc.IsValid);

            var file = new LinuxFile(loc.Dentry!, FileFlags.O_RDONLY, loc.Mount!);
            loc.Dentry!.Inode!.Open(file);
            var inode = Assert.IsType<HostInode>(loc.Dentry!.Inode);

            const uint baseAddr = 0x48100000;
            for (var i = 0; i < 4; i++)
            {
                var pageAddr = baseAddr + (uint)(i * LinuxConstants.PageSize);
                mm.Mmap(pageAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                    MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[readv-test]", engine);
                Assert.True(mm.HandleFault(pageAddr, true, engine));
            }

            var iovs = new[]
            {
                new Iovec { BaseAddr = baseAddr, Len = LinuxConstants.PageSize },
                new Iovec { BaseAddr = baseAddr + LinuxConstants.PageSize, Len = LinuxConstants.PageSize },
                new Iovec { BaseAddr = baseAddr + 2 * LinuxConstants.PageSize, Len = LinuxConstants.PageSize },
                new Iovec { BaseAddr = baseAddr + 3 * LinuxConstants.PageSize, Len = LinuxConstants.PageSize }
            };
            var read = await inode.ReadV(engine, file, null, iovs, 0, 0);
            Assert.Equal(LinuxConstants.PageSize * 4, read);

            var diagnostics = inode.GetMappedPageCacheDiagnostics();
            Assert.Equal(1, diagnostics.WindowCount);
            Assert.Equal(16384, diagnostics.WindowBytes);
            Assert.True(diagnostics.GuestPageCount >= 4);

            Span<byte> first = stackalloc byte[1];
            Span<byte> second = stackalloc byte[1];
            Span<byte> third = stackalloc byte[1];
            Span<byte> fourth = stackalloc byte[1];
            Assert.True(engine.CopyFromUser(baseAddr, first));
            Assert.True(engine.CopyFromUser(baseAddr + LinuxConstants.PageSize, second));
            Assert.True(engine.CopyFromUser(baseAddr + 2 * LinuxConstants.PageSize, third));
            Assert.True(engine.CopyFromUser(baseAddr + 3 * LinuxConstants.PageSize, fourth));
            Assert.Equal((byte)'a', first[0]);
            Assert.Equal((byte)'b', second[0]);
            Assert.Equal((byte)'c', third[0]);
            Assert.Equal((byte)'d', fourth[0]);
            file.Close();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Hostfs_ReadThenWriteV_SamePage_UpgradesReadonlyMappedPageBeforeWrite()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hostfs-readwrite-same-page-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        var data = new byte[LinuxConstants.PageSize];
        Array.Fill(data, (byte)'r');
        File.WriteAllBytes(hostFile, data);

        try
        {
            var runtime = new MemoryRuntimeContext(Geometry16K);
            using var engine = new Engine(runtime);
            var mm = new VMAManager(runtime);
            var sm = new SyscallManager(engine, mm, 0);
            sm.MountRootHostfs(root);
            var loc = sm.PathWalkWithFlags("/data.bin", LookupFlags.FollowSymlink);
            Assert.True(loc.IsValid);

            var file = new LinuxFile(loc.Dentry!, FileFlags.O_RDWR, loc.Mount!);
            loc.Dentry!.Inode!.Open(file);
            var inode = Assert.IsType<HostInode>(loc.Dentry!.Inode);

            const uint readAddr = 0x48200000;
            const uint writeAddr = 0x48201000;
            mm.Mmap(readAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[readwrite-read]", engine);
            mm.Mmap(writeAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[readwrite-write]", engine);
            Assert.True(mm.HandleFault(readAddr, true, engine));
            Assert.True(mm.HandleFault(writeAddr, true, engine));

            var readIovs = new[]
            {
                new Iovec { BaseAddr = readAddr, Len = LinuxConstants.PageSize }
            };
            var read = await inode.ReadV(engine, file, null, readIovs, 0, 0);
            Assert.Equal(LinuxConstants.PageSize, read);

            var writePage = new byte[LinuxConstants.PageSize];
            Array.Fill(writePage, (byte)'W');
            Assert.True(engine.CopyToUser(writeAddr, writePage));

            var writeIovs = new[]
            {
                new Iovec { BaseAddr = writeAddr, Len = LinuxConstants.PageSize }
            };
            var written = await inode.WriteV(engine, file, null, writeIovs, 0, 0);
            Assert.Equal(LinuxConstants.PageSize, written);

            file.Close();

            var updated = ReadAllBytesWithUnixCompatibleSharing(hostFile);
            Assert.Equal(LinuxConstants.PageSize, updated.Length);
            Assert.All(updated, b => Assert.Equal((byte)'W', b));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Silkfs_SparseWrite_PreExtendsAndUsesSingleDirectMappedWindow()
    {
        var silkRoot = Path.Combine(Path.GetTempPath(), $"silkfs-sparse-16k-{Guid.NewGuid():N}");

        try
        {
            {
                var runtime = new MemoryRuntimeContext(Geometry16K);
                using var engine = new Engine(runtime);
                var mm = new VMAManager(runtime);
                var sm = CreateTmpfsRoot(engine, mm);
                var mountLoc = MountSilkfs(sm, silkRoot);

                var fileDentry = new Dentry(FsName.FromString("data.bin"), null, mountLoc.Dentry,
                    mountLoc.Dentry!.SuperBlock);
                Assert.Equal(0, mountLoc.Dentry.Inode!.Create(fileDentry, 0x1A4, 0, 0));

                var file = new LinuxFile(fileDentry, FileFlags.O_RDWR, mountLoc.Mount!);
                fileDentry.Inode!.Open(file);
                var silkInode = Assert.IsType<SilkInode>(fileDentry.Inode);

                Assert.Equal(2, silkInode.WriteFromHost(null, file, "p3"u8.ToArray(), 3L * LinuxConstants.PageSize));
                Assert.Equal(2, silkInode.WriteFromHost(null, file, "p2"u8.ToArray(), 2L * LinuxConstants.PageSize));

                var diagnostics = silkInode.GetMappedPageCacheDiagnostics();
                Assert.Equal(1, diagnostics.WindowCount);
                Assert.True(diagnostics.WindowBytes >= LinuxConstants.PageSize);
                Assert.True(diagnostics.GuestPageCount >= 1);

                file.Close();
                sm.Close();
            }

            {
                var runtime = new MemoryRuntimeContext(Geometry16K);
                using var engine = new Engine(runtime);
                var mm = new VMAManager(runtime);
                var sm = CreateTmpfsRoot(engine, mm);
                MountSilkfs(sm, silkRoot);
                var fileLoc = sm.PathWalkWithFlags("/mnt/data.bin", LookupFlags.FollowSymlink);
                Assert.True(fileLoc.IsValid);

                var rf = new LinuxFile(fileLoc.Dentry!, FileFlags.O_RDONLY, fileLoc.Mount!);
                fileLoc.Dentry!.Inode!.Open(rf);
                var page2 = new byte[2];
                var page3 = new byte[2];
                Assert.Equal(2, fileLoc.Dentry.Inode.ReadToHost(null, rf, page2, 2L * LinuxConstants.PageSize));
                Assert.Equal(2, fileLoc.Dentry.Inode.ReadToHost(null, rf, page3, 3L * LinuxConstants.PageSize));
                Assert.Equal("p2"u8.ToArray(), page2);
                Assert.Equal("p3"u8.ToArray(), page3);
                rf.Close();
                sm.Close();
            }
        }
        finally
        {
            if (Directory.Exists(silkRoot)) Directory.Delete(silkRoot, true);
        }
    }

    [Fact]
    public async Task Silkfs_GuestWrite_CloseAndRemount_GuestRead_PreservesData()
    {
        var silkRoot = Path.Combine(Path.GetTempPath(), $"silkfs-guest-rw-16k-{Guid.NewGuid():N}");

        try
        {
            {
                var runtime = new MemoryRuntimeContext(Geometry16K);
                using var engine = new Engine(runtime);
                var mm = new VMAManager(runtime);
                var sm = CreateTmpfsRoot(engine, mm);
                var mountLoc = MountSilkfs(sm, silkRoot);

                var fileDentry = new Dentry(FsName.FromString("data.bin"), null, mountLoc.Dentry,
                    mountLoc.Dentry!.SuperBlock);
                Assert.Equal(0, mountLoc.Dentry.Inode!.Create(fileDentry, 0x1A4, 0, 0));

                var file = new LinuxFile(fileDentry, FileFlags.O_RDWR, mountLoc.Mount!);
                fileDentry.Inode!.Open(file);
                var fd = sm.AllocFD(file);

                const uint writeAddr = 0x48200000;
                MapUserRange(mm, engine, writeAddr, LinuxConstants.PageSize * 2);
                var writeBuffer = CreateFilledBuffer(LinuxConstants.PageSize * 2, (byte)'s');
                Assert.True(engine.CopyToUser(writeAddr, writeBuffer));

                var writeRc = await CallSys(sm, engine, "SysWrite", (uint)fd, writeAddr,
                    (uint)writeBuffer.Length);
                Assert.Equal(writeBuffer.Length, writeRc);

                var closeRc = await CallSys(sm, engine, "SysClose", (uint)fd);
                Assert.Equal(0, closeRc);
                sm.Close();
            }

            {
                var runtime = new MemoryRuntimeContext(Geometry16K);
                using var engine = new Engine(runtime);
                var mm = new VMAManager(runtime);
                var sm = CreateTmpfsRoot(engine, mm);
                MountSilkfs(sm, silkRoot);

                var fileLoc = sm.PathWalkWithFlags("/mnt/data.bin", LookupFlags.FollowSymlink);
                Assert.True(fileLoc.IsValid);

                var file = new LinuxFile(fileLoc.Dentry!, FileFlags.O_RDONLY, fileLoc.Mount!);
                fileLoc.Dentry!.Inode!.Open(file);
                var fd = sm.AllocFD(file);

                const uint readAddr = 0x48300000;
                MapUserRange(mm, engine, readAddr, LinuxConstants.PageSize * 2);

                var readRc = await CallSys(sm, engine, "SysRead", (uint)fd, readAddr,
                    LinuxConstants.PageSize * 2u);
                Assert.Equal(LinuxConstants.PageSize * 2, readRc);

                var readBuffer = new byte[LinuxConstants.PageSize * 2];
                Assert.True(engine.CopyFromUser(readAddr, readBuffer));
                Assert.Equal(CreateFilledBuffer(LinuxConstants.PageSize * 2, (byte)'s'), readBuffer);

                var closeRc = await CallSys(sm, engine, "SysClose", (uint)fd);
                Assert.Equal(0, closeRc);
                sm.Close();
            }
        }
        finally
        {
            if (Directory.Exists(silkRoot)) Directory.Delete(silkRoot, true);
        }
    }

    [Fact]
    public async Task OverlaySilkfs_GuestWrite_CloseAndRemount_GuestRead_PreservesData()
    {
        var silkUpperRoot = Path.Combine(Path.GetTempPath(), $"overlay-silk-upper-16k-{Guid.NewGuid():N}");

        try
        {
            {
                var runtime = new MemoryRuntimeContext(Geometry16K);
                using var engine = new Engine(runtime);
                var mm = new VMAManager(runtime);
                var sm = CreateOverlaySilkRoot(engine, mm, silkUpperRoot);

                var block = new Dentry(FsName.FromString("block"), null, sm.Root.Dentry, sm.Root.Dentry!.SuperBlock);
                Assert.Equal(0, sm.Root.Dentry.Inode!.Create(block, 0x1A4, 0, 0));

                var file = new LinuxFile(block, FileFlags.O_RDWR, sm.Root.Mount!);
                block.Inode!.Open(file);
                var fd = sm.AllocFD(file);

                const uint writeAddr = 0x48400000;
                MapUserRange(mm, engine, writeAddr, LinuxConstants.PageSize * 2);
                var writeBuffer = CreateFilledBuffer(LinuxConstants.PageSize * 2, (byte)'o');
                Assert.True(engine.CopyToUser(writeAddr, writeBuffer));

                var writeRc = await CallSys(sm, engine, "SysWrite", (uint)fd, writeAddr,
                    (uint)writeBuffer.Length);
                Assert.Equal(writeBuffer.Length, writeRc);
                Assert.Equal((ulong)writeBuffer.Length, Assert.IsType<OverlayInode>(block.Inode!).UpperInode!.Size);

                var closeRc = await CallSys(sm, engine, "SysClose", (uint)fd);
                Assert.Equal(0, closeRc);
                sm.Close();
            }

            {
                var runtime = new MemoryRuntimeContext(Geometry16K);
                using var engine = new Engine(runtime);
                var mm = new VMAManager(runtime);
                var sm = CreateTmpfsRoot(engine, mm);
                MountSilkfs(sm, silkUpperRoot);

                var loc = sm.PathWalkWithFlags("/mnt/block", LookupFlags.FollowSymlink);
                Assert.True(loc.IsValid);
                Assert.Equal((ulong)(LinuxConstants.PageSize * 2), loc.Dentry!.Inode!.Size);

                var file = new LinuxFile(loc.Dentry!, FileFlags.O_RDONLY, loc.Mount!);
                loc.Dentry!.Inode!.Open(file);
                var data = new byte[LinuxConstants.PageSize * 2];
                var readRc = loc.Dentry!.Inode!.ReadToHost(null, file, data, 0);
                Assert.Equal(LinuxConstants.PageSize * 2, readRc);
                Assert.Equal(CreateFilledBuffer(LinuxConstants.PageSize * 2, (byte)'o'), data);
                file.Close();
                sm.Close();
            }

            {
                var runtime = new MemoryRuntimeContext(Geometry16K);
                using var engine = new Engine(runtime);
                var mm = new VMAManager(runtime);
                var sm = CreateOverlaySilkRoot(engine, mm, silkUpperRoot);

                var loc = sm.PathWalkWithFlags("/block", LookupFlags.FollowSymlink);
                Assert.True(loc.IsValid);

                var file = new LinuxFile(loc.Dentry!, FileFlags.O_RDONLY, loc.Mount!);
                loc.Dentry!.Inode!.Open(file);
                var fd = sm.AllocFD(file);

                const uint readAddr = 0x48500000;
                MapUserRange(mm, engine, readAddr, LinuxConstants.PageSize * 2);

                var readRc = await CallSys(sm, engine, "SysRead", (uint)fd, readAddr,
                    LinuxConstants.PageSize * 2u);
                Assert.Equal(LinuxConstants.PageSize * 2, readRc);

                var readBuffer = new byte[LinuxConstants.PageSize * 2];
                Assert.True(engine.CopyFromUser(readAddr, readBuffer));
                Assert.Equal(CreateFilledBuffer(LinuxConstants.PageSize * 2, (byte)'o'), readBuffer);

                var closeRc = await CallSys(sm, engine, "SysClose", (uint)fd);
                Assert.Equal(0, closeRc);
                sm.Close();
            }
        }
        finally
        {
            if (Directory.Exists(silkUpperRoot)) Directory.Delete(silkUpperRoot, true);
        }
    }

    [Fact]
    public void Hostfs_PartialTailPage_UsesDirectMap()
    {
        if (!_runtime.MemoryContext.HostMemoryMapGeometry.SupportsDirectMappedTailPage)
            return;

        var root = Path.Combine(Path.GetTempPath(), $"hostfs-tail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "tail.bin");
        var bytes = new byte[LinuxConstants.PageSize + 123];
        Array.Fill(bytes, (byte)'q');
        File.WriteAllBytes(hostFile, bytes);

        try
        {
            using var engine = _runtime.CreateEngine();
            var mm = _runtime.CreateAddressSpace();
            var sm = new SyscallManager(engine, mm, 0);
            sm.MountRootHostfs(root);
            var loc = sm.PathWalkWithFlags("/tail.bin", LookupFlags.FollowSymlink);
            Assert.True(loc.IsValid);

            var file = new LinuxFile(loc.Dentry!, FileFlags.O_RDWR, loc.Mount!);
            const uint mapAddr = 0x47300000;
            mm.Mmap(mapAddr, LinuxConstants.PageSize * 2, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, file, 0, "MAP_SHARED_TAIL", engine);

            var tailPageAddr = mapAddr + LinuxConstants.PageSize;
            Assert.True(mm.HandleFault(tailPageAddr, true, engine));

            var probe = new byte[24];
            Assert.True(engine.CopyFromUser(tailPageAddr + 123, probe));
            Assert.All(probe, b => Assert.Equal((byte)0, b));

            var hostInode = Assert.IsType<HostInode>(loc.Dentry!.Inode);
            var diagnostics = hostInode.GetMappedPageCacheDiagnostics();
            Assert.Equal(1, diagnostics.WindowCount);
            Assert.Equal(1, diagnostics.GuestPageCount);

            Assert.True(engine.CopyToUser(tailPageAddr + 123 + 16, "TAIL!"u8.ToArray()));
            var vma = Assert.Single(mm.VMAs.Where(v => v.Start == mapAddr));
            VMAManager.SyncVmArea(vma, engine, tailPageAddr, tailPageAddr + LinuxConstants.PageSize);

            var refreshed = ReadAllBytesWithUnixCompatibleSharing(hostFile);
            Assert.Equal(bytes.Length, refreshed.Length);
            Assert.All(refreshed.AsSpan(bytes.Length - 8).ToArray(), b => Assert.Equal((byte)'q', b));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Hostfs_ReadOnlyTailPage_ExternalShrink_DropsResidentWindowWithoutHostClear()
    {
        if (!_runtime.MemoryContext.HostMemoryMapGeometry.SupportsDirectMappedTailPage)
            return;

        var root = Path.Combine(Path.GetTempPath(), $"hostfs-tail-shrink-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "tail.bin");
        File.WriteAllBytes(hostFile, CreateFilledBuffer(LinuxConstants.PageSize + 123, (byte)'t'));

        try
        {
            using var engine = _runtime.CreateEngine();
            var mm = _runtime.CreateAddressSpace();
            var sm = new SyscallManager(engine, mm, 0);
            sm.MountRootHostfs(root);
            var loc = sm.PathWalkWithFlags("/tail.bin", LookupFlags.FollowSymlink);
            Assert.True(loc.IsValid);

            var file = new LinuxFile(loc.Dentry!, FileFlags.O_RDONLY, loc.Mount!);
            const uint mapAddr = 0x47400000;
            mm.Mmap(mapAddr, LinuxConstants.PageSize * 2, Protection.Read, MapFlags.Shared | MapFlags.Fixed, file, 0,
                "MAP_SHARED_TAIL_RO", engine);

            var tailPageAddr = mapAddr + LinuxConstants.PageSize;
            Assert.True(mm.HandleFault(tailPageAddr, false, engine));

            var hostInode = Assert.IsType<HostInode>(loc.Dentry!.Inode);
            Assert.Equal(1, hostInode.GetMappedPageCacheDiagnostics().GuestPageCount);
            Assert.True(mm.PageMapping.TryGet(tailPageAddr, out _));

            using (var handle = File.OpenHandle(hostFile, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
                if (OperatingSystem.IsWindows())
                {
                    Assert.Throws<IOException>(() => RandomAccess.SetLength(handle, LinuxConstants.PageSize + 17));
                    Assert.Equal(1, hostInode.GetMappedPageCacheDiagnostics().GuestPageCount);
                    Assert.True(mm.PageMapping.TryGet(tailPageAddr, out _));
                    return;
                }

                RandomAccess.SetLength(handle, LinuxConstants.PageSize + 17);
            }

            using (var reopen = new LinuxFile(loc.Dentry!, FileFlags.O_RDONLY, loc.Mount!))
                loc.Dentry!.Inode!.Open(reopen);

            var diagnostics = hostInode.GetMappedPageCacheDiagnostics();
            Assert.Equal(0, diagnostics.GuestPageCount);
            Assert.False(mm.PageMapping.TryGet(tailPageAddr, out _));
            Assert.Equal(FaultResult.Handled, mm.HandleFaultDetailed(tailPageAddr, false, engine));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Hostfs_ReadOnlyTailPage_ExplicitTruncate_InvalidatesPageAndRefaultsZeroTail()
    {
        if (!_runtime.MemoryContext.HostMemoryMapGeometry.SupportsDirectMappedTailPage)
            return;

        var root = Path.Combine(Path.GetTempPath(), $"hostfs-tail-explicit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "tail.bin");
        File.WriteAllBytes(hostFile, CreateFilledBuffer(LinuxConstants.PageSize + 123, (byte)'u'));

        try
        {
            using var engine = _runtime.CreateEngine();
            var mm = _runtime.CreateAddressSpace();
            var sm = new SyscallManager(engine, mm, 0);
            sm.MountRootHostfs(root);
            var loc = sm.PathWalkWithFlags("/tail.bin", LookupFlags.FollowSymlink);
            Assert.True(loc.IsValid);

            var file = new LinuxFile(loc.Dentry!, FileFlags.O_RDONLY, loc.Mount!);
            const uint mapAddr = 0x47480000;
            mm.Mmap(mapAddr, LinuxConstants.PageSize * 2, Protection.Read, MapFlags.Shared | MapFlags.Fixed, file, 0,
                "MAP_SHARED_TAIL_EXPLICIT_RO", engine);

            var tailPageAddr = mapAddr + LinuxConstants.PageSize;
            Assert.True(mm.HandleFault(tailPageAddr, false, engine));

            var hostInode = Assert.IsType<HostInode>(loc.Dentry!.Inode);
            Assert.Equal(1, hostInode.GetMappedPageCacheDiagnostics().GuestPageCount);
            Assert.True(mm.PageMapping.TryGet(tailPageAddr, out _));

            Assert.Equal(0,
                loc.Dentry!.Inode!.Truncate(LinuxConstants.PageSize + 17, new FileMutationContext(mm, engine)));

            Assert.Equal(0, hostInode.GetMappedPageCacheDiagnostics().GuestPageCount);
            Assert.False(mm.PageMapping.TryGet(tailPageAddr, out _));
            Assert.Equal(FaultResult.Handled, mm.HandleFaultDetailed(tailPageAddr, false, engine));

            var prefix = new byte[1];
            var tail = new byte[32];
            Assert.True(engine.CopyFromUser(tailPageAddr, prefix));
            Assert.True(engine.CopyFromUser(tailPageAddr + 17, tail));
            Assert.Equal((byte)'u', prefix[0]);
            Assert.All(tail, b => Assert.Equal((byte)0, b));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Hostfs_ExternalShrink_PageWhollyBeyondNewEof_FaultsAsBusError()
    {
        if (!_runtime.MemoryContext.HostMemoryMapGeometry.SupportsDirectMappedTailPage)
            return;

        var root = Path.Combine(Path.GetTempPath(), $"hostfs-beyond-eof-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        File.WriteAllBytes(hostFile, CreateFilledBuffer(LinuxConstants.PageSize * 2, (byte)'b'));

        try
        {
            using var engine = _runtime.CreateEngine();
            var mm = _runtime.CreateAddressSpace();
            var sm = new SyscallManager(engine, mm, 0);
            sm.MountRootHostfs(root);
            var loc = sm.PathWalkWithFlags("/data.bin", LookupFlags.FollowSymlink);
            Assert.True(loc.IsValid);

            var file = new LinuxFile(loc.Dentry!, FileFlags.O_RDONLY, loc.Mount!);
            const uint mapAddr = 0x47500000;
            mm.Mmap(mapAddr, LinuxConstants.PageSize * 2, Protection.Read, MapFlags.Shared | MapFlags.Fixed, file, 0,
                "MAP_SHARED_SHRINK", engine);

            var secondPageAddr = mapAddr + LinuxConstants.PageSize;
            Assert.True(mm.HandleFault(secondPageAddr, false, engine));
            Assert.True(mm.PageMapping.TryGet(secondPageAddr, out _));

            using (var handle = File.OpenHandle(hostFile, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
                if (OperatingSystem.IsWindows())
                {
                    Assert.Throws<IOException>(() => RandomAccess.SetLength(handle, 123));
                    Assert.True(mm.PageMapping.TryGet(secondPageAddr, out _));
                    return;
                }

                RandomAccess.SetLength(handle, 123);
            }

            using (var reopen = new LinuxFile(loc.Dentry!, FileFlags.O_RDONLY, loc.Mount!))
                loc.Dentry!.Inode!.Open(reopen);

            Assert.False(mm.PageMapping.TryGet(secondPageAddr, out _));
            Assert.Equal(FaultResult.BusError, mm.HandleFaultDetailed(secondPageAddr, false, engine));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static byte[] CreateFilledBuffer(int length, byte value)
    {
        var buffer = new byte[length];
        Array.Fill(buffer, value);
        return buffer;
    }

    private static async ValueTask<int> CallSys(SyscallManager sm, Engine engine, string methodName, uint a1 = 0,
        uint a2 = 0, uint a3 = 0, uint a4 = 0, uint a5 = 0, uint a6 = 0)
    {
        var method = typeof(SyscallManager).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var task = (ValueTask<int>)method!.Invoke(sm, [engine, a1, a2, a3, a4, a5, a6])!;
        return await task;
    }

    private static void MapUserRange(VMAManager mm, Engine engine, uint addr, int length)
    {
        mm.Mmap(addr, (uint)length, Protection.Read | Protection.Write,
            MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[test]", engine);
        for (var offset = 0; offset < length; offset += LinuxConstants.PageSize)
            Assert.True(mm.HandleFault(addr + (uint)offset, true, engine));
    }

    private static byte[] ReadAllBytesWithUnixCompatibleSharing(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static SyscallManager CreateTmpfsRoot(Engine engine, VMAManager mm)
    {
        var sm = new SyscallManager(engine, mm, 0);
        var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
        var rootSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "test-root", null);
        var rootMount = new Mount(rootSb, rootSb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
        sm.InitializeRoot(rootSb.Root, rootMount);

        var root = sm.Root.Dentry!;
        if (root.Inode!.Lookup("mnt") == null)
        {
            var mntDentry = new Dentry(FsName.FromString("mnt"), null, root, root.SuperBlock);
            root.Inode.Mkdir(mntDentry, 0x1FF, 0, 0);
            root.CacheChild(mntDentry, "test");
        }

        return sm;
    }

    private static SyscallManager CreateOverlaySilkRoot(Engine engine, VMAManager mm, string silkUpperRoot)
    {
        Directory.CreateDirectory(silkUpperRoot);

        var sm = new SyscallManager(engine, mm, 0);
        var lowerFs = new LayerFileSystem();
        var lowerRoot = LayerNode.Directory("/");
        var lowerSb = lowerFs.ReadSuper(
            new FileSystemType { Name = "layerfs" },
            0,
            "overlay-lower",
            new LayerMountOptions { Root = lowerRoot });

        var silkType = FileSystemRegistry.Get("silkfs")!;
        var upperSb = silkType.CreateAnonymousFileSystem().ReadSuper(silkType, 0, silkUpperRoot, null);

        var overlayFs = new OverlayFileSystem();
        var overlaySb = overlayFs.ReadSuper(
            new FileSystemType { Name = "overlay" },
            0,
            "overlay-root",
            new OverlayMountOptions { Lower = lowerSb, Upper = upperSb });

        var rootMount = new Mount(overlaySb, overlaySb.Root) { Source = "overlay", FsType = "overlay", Options = "rw" };
        sm.InitializeRoot(overlaySb.Root, rootMount);
        return sm;
    }

    private static PathLocation MountSilkfs(SyscallManager sm, string silkRoot)
    {
        var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
        Assert.Equal(0, sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
        var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
        Assert.Equal(0, sm.AttachDetachedMount(mount!, target));
        return sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
    }
}
