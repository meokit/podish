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

            var updated = File.ReadAllBytes(hostFile);
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

            var refreshed = File.ReadAllBytes(hostFile);
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
                RandomAccess.SetLength(handle, LinuxConstants.PageSize + 17);

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
                RandomAccess.SetLength(handle, 123);

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

    private static PathLocation MountSilkfs(SyscallManager sm, string silkRoot)
    {
        var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
        Assert.Equal(0, sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
        var target = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
        Assert.Equal(0, sm.AttachDetachedMount(mount!, target));
        return sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
    }
}
