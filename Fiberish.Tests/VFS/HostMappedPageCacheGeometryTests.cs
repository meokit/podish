using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.VFS;

public class HostMappedPageCacheGeometryTests
{
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
            using var engine = new Engine(new MemoryRuntimeContext(Geometry16K));
            var mm = new VMAManager();
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
            using var engine = new Engine(new MemoryRuntimeContext(Geometry16K));
            var mm = new VMAManager();
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
            using (var engine = new Engine(new MemoryRuntimeContext(Geometry16K)))
            {
                var mm = new VMAManager();
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

            using (var engine = new Engine(new MemoryRuntimeContext(Geometry16K)))
            {
                var mm = new VMAManager();
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

            using (var engine = new Engine(new MemoryRuntimeContext(Geometry16K)))
            {
                var mm = new VMAManager();
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
    public void Hostfs_OnUnix_PartialTailPage_UsesDirectMap()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = Path.Combine(Path.GetTempPath(), $"hostfs-tail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "tail.bin");
        var bytes = new byte[LinuxConstants.PageSize + 123];
        Array.Fill(bytes, (byte)'q');
        File.WriteAllBytes(hostFile, bytes);

        try
        {
            using var engine = new Engine();
            var mm = new VMAManager();
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