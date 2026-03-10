using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Fiberish.Core;
using System.Reflection;
using Xunit;

namespace Fiberish.Tests.VFS;

public class HostfsPageCacheWritebackTests
{
    [Fact]
    public void MapShared_SyncVma_WritesBackPageCacheToHostFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "hostfs-writeback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        File.WriteAllText(hostFile, "hello");

        try
        {
            using var engine = new Engine();
            var mm = new VMAManager();
            var file = OpenHostFile(root, "data.bin");

            const uint mapAddr = 0x41000000;
            mm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, file, 0, (long)file.Dentry.Inode!.Size, "MAP_SHARED", engine);
            Assert.True(mm.HandleFault(mapAddr, false, engine));

            Assert.True(engine.CopyToUser(mapAddr + 1, "ABC"u8.ToArray()));

            var vma = mm.FindVMA(mapAddr);
            Assert.NotNull(vma);
            VMAManager.SyncVMA(vma!, engine, mapAddr, mapAddr + LinuxConstants.PageSize);

            Assert.Equal("hABCo", File.ReadAllText(hostFile));
            file.Dentry.Inode!.Release(file);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void MapShared_Munmap_WritesBackBeforeUnmap()
    {
        var root = Path.Combine(Path.GetTempPath(), "hostfs-munmap-writeback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        File.WriteAllText(hostFile, "world");

        try
        {
            using var engine = new Engine();
            var mm = new VMAManager();
            var file = OpenHostFile(root, "data.bin");

            const uint mapAddr = 0x42000000;
            mm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, file, 0, (long)file.Dentry.Inode!.Size, "MAP_SHARED", engine);
            Assert.True(mm.HandleFault(mapAddr, true, engine));

            Assert.True(engine.CopyToUser(mapAddr + 2, "XY"u8.ToArray()));

            mm.Munmap(mapAddr, LinuxConstants.PageSize, engine);

            Assert.Equal("woXYd", File.ReadAllText(hostFile));
            file.Dentry.Inode!.Release(file);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task MapShared_Fsync_WritesBackMappedDirtyPages()
    {
        var root = Path.Combine(Path.GetTempPath(), "hostfs-fsync-writeback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        File.WriteAllText(hostFile, "abcde");

        try
        {
            using var engine = new Engine();
            var mm = new VMAManager();
            var sm = new SyscallManager(engine, mm, 0);
            sm.MountRootHostfs(root);
            var loc = sm.PathWalkWithFlags("/data.bin", LookupFlags.FollowSymlink);
            Assert.True(loc.IsValid);
            var file = new LinuxFile(loc.Dentry!, FileFlags.O_RDWR, loc.Mount!);
            loc.Dentry!.Inode!.Open(file);
            var fd = sm.AllocFD(file);

            const uint mapAddr = 0x43000000;
            mm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, file, 0, (long)file.Dentry.Inode!.Size, "MAP_SHARED", engine);
            Assert.True(mm.HandleFault(mapAddr, true, engine));
            Assert.True(engine.CopyToUser(mapAddr + 1, "ZZ"u8.ToArray()));

            var rc = await CallSys("SysFsync", engine.State, (uint)fd);
            Assert.Equal(0, rc);
            Assert.Equal("aZZde", File.ReadAllText(hostFile));
            sm.FreeFD(fd);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task MapShared_Fsync_WritesBackDirtyPagesFromPeerThreadEngine()
    {
        var root = Path.Combine(Path.GetTempPath(), "hostfs-fsync-peer-thread-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        File.WriteAllText(hostFile, "abcde");

        var oldCurrent = KernelScheduler.Current;
        try
        {
            using var engine = new Engine();
            var mm = new VMAManager();
            var sm = new SyscallManager(engine, mm, 0);
            sm.MountRootHostfs(root);

            var scheduler = new KernelScheduler();
            KernelScheduler.Current = scheduler;
            var process = new Process(5001, mm, sm);
            scheduler.RegisterProcess(process);
            var task = new FiberTask(5001, process, engine, scheduler);
            engine.Owner = task;

            var loc = sm.PathWalkWithFlags("/data.bin", LookupFlags.FollowSymlink);
            Assert.True(loc.IsValid);
            var file = new LinuxFile(loc.Dentry!, FileFlags.O_RDWR, loc.Mount!);
            loc.Dentry!.Inode!.Open(file);
            var fd = sm.AllocFD(file);

            const uint mapAddr = 0x43100000;
            mm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, file, 0, (long)file.Dentry.Inode!.Size, "MAP_SHARED", engine);
            Assert.True(mm.HandleFault(mapAddr, true, engine));

            var peer = await task.Clone((int)(LinuxConstants.CLONE_VM | LinuxConstants.CLONE_THREAD), 0, 0, 0, 0);
            Assert.True(peer.CPU.CopyToUser(mapAddr + 1, "ZZ"u8.ToArray()));

            var rc = await CallSys("SysFsync", engine.State, (uint)fd);
            Assert.Equal(0, rc);
            Assert.Equal("aZZde", File.ReadAllText(hostFile));

            sm.FreeFD(fd);
            GC.KeepAlive(peer);
        }
        finally
        {
            KernelScheduler.Current = oldCurrent;
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task MapShared_Munmap_WritesBackDirtyPagesFromPeerThreadEngine()
    {
        var root = Path.Combine(Path.GetTempPath(), "hostfs-munmap-peer-thread-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        File.WriteAllText(hostFile, "abcde");

        var oldCurrent = KernelScheduler.Current;
        try
        {
            using var engine = new Engine();
            var mm = new VMAManager();
            var sm = new SyscallManager(engine, mm, 0);
            sm.MountRootHostfs(root);

            var scheduler = new KernelScheduler();
            KernelScheduler.Current = scheduler;
            var process = new Process(5002, mm, sm);
            scheduler.RegisterProcess(process);
            var task = new FiberTask(5002, process, engine, scheduler);
            engine.Owner = task;

            var loc = sm.PathWalkWithFlags("/data.bin", LookupFlags.FollowSymlink);
            Assert.True(loc.IsValid);
            var file = new LinuxFile(loc.Dentry!, FileFlags.O_RDWR, loc.Mount!);
            loc.Dentry!.Inode!.Open(file);

            const uint mapAddr = 0x43200000;
            mm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, file, 0, (long)file.Dentry.Inode!.Size, "MAP_SHARED", engine);
            Assert.True(mm.HandleFault(mapAddr, true, engine));

            var peer = await task.Clone((int)(LinuxConstants.CLONE_VM | LinuxConstants.CLONE_THREAD), 0, 0, 0, 0);
            Assert.True(peer.CPU.CopyToUser(mapAddr + 1, "ZZ"u8.ToArray()));

            ProcessAddressSpaceSync.Munmap(mm, engine, mapAddr, LinuxConstants.PageSize, process);
            Assert.Equal("aZZde", File.ReadAllText(hostFile));

            GC.KeepAlive(peer);
        }
        finally
        {
            KernelScheduler.Current = oldCurrent;
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task MapShared_Fsync_AfterPeerThreadExit_StillWritesBackDirtyPages()
    {
        var root = Path.Combine(Path.GetTempPath(), "hostfs-fsync-peer-exit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        File.WriteAllText(hostFile, "abcde");

        var oldCurrent = KernelScheduler.Current;
        try
        {
            using var engine = new Engine();
            var mm = new VMAManager();
            var sm = new SyscallManager(engine, mm, 0);
            sm.MountRootHostfs(root);

            var scheduler = new KernelScheduler();
            KernelScheduler.Current = scheduler;
            var process = new Process(5003, mm, sm);
            scheduler.RegisterProcess(process);
            var task = new FiberTask(5003, process, engine, scheduler);
            engine.Owner = task;

            var loc = sm.PathWalkWithFlags("/data.bin", LookupFlags.FollowSymlink);
            Assert.True(loc.IsValid);
            var file = new LinuxFile(loc.Dentry!, FileFlags.O_RDWR, loc.Mount!);
            loc.Dentry!.Inode!.Open(file);
            var fd = sm.AllocFD(file);

            const uint mapAddr = 0x43300000;
            mm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, file, 0, (long)file.Dentry.Inode!.Size, "MAP_SHARED", engine);
            Assert.True(mm.HandleFault(mapAddr, true, engine));

            var peer = await task.Clone((int)(LinuxConstants.CLONE_VM | LinuxConstants.CLONE_THREAD), 0, 0, 0, 0);
            Assert.True(peer.CPU.CopyToUser(mapAddr + 1, "ZZ"u8.ToArray()));

            _ = scheduler.DetachTask(peer);

            var rc = await CallSys("SysFsync", engine.State, (uint)fd);
            Assert.Equal(0, rc);
            Assert.Equal("aZZde", File.ReadAllText(hostFile));

            sm.FreeFD(fd);
        }
        finally
        {
            KernelScheduler.Current = oldCurrent;
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Sync_WritesBackMappedDirtyPagesAcrossFds()
    {
        var root = Path.Combine(Path.GetTempPath(), "hostfs-sync-writeback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        File.WriteAllText(hostFile, "12345");

        try
        {
            using var engine = new Engine();
            var mm = new VMAManager();
            var sm = new SyscallManager(engine, mm, 0);
            sm.MountRootHostfs(root);
            var loc = sm.PathWalkWithFlags("/data.bin", LookupFlags.FollowSymlink);
            Assert.True(loc.IsValid);
            var file = new LinuxFile(loc.Dentry!, FileFlags.O_RDWR, loc.Mount!);
            loc.Dentry!.Inode!.Open(file);
            _ = sm.AllocFD(file);

            const uint mapAddr = 0x44000000;
            mm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, file, 0, (long)file.Dentry.Inode!.Size, "MAP_SHARED", engine);
            Assert.True(mm.HandleFault(mapAddr, true, engine));
            Assert.True(engine.CopyToUser(mapAddr + 2, "QQ"u8.ToArray()));

            var rc = await CallSys("SysSync", engine.State);
            Assert.Equal(0, rc);
            Assert.Equal("12QQ5", File.ReadAllText(hostFile));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Sync_WritesBackMappedDirtyPagesFromOtherProcessInSameContainer()
    {
        var root = Path.Combine(Path.GetTempPath(), "hostfs-sync-container-wide-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        File.WriteAllText(hostFile, "ABCDE");

        var oldCurrent = KernelScheduler.Current;
        try
        {
            using var engine1 = new Engine();
            using var engine2 = new Engine();
            var mm1 = new VMAManager();
            var mm2 = new VMAManager();
            var sm1 = new SyscallManager(engine1, mm1, 0);
            var sm2 = new SyscallManager(engine2, mm2, 0);
            sm1.MountRootHostfs(root);
            sm2.MountRootHostfs(root);

            var scheduler = new KernelScheduler();
            KernelScheduler.Current = scheduler;
            scheduler.RegisterProcess(new Process(1001, mm1, sm1));
            scheduler.RegisterProcess(new Process(1002, mm2, sm2));

            var loc2 = sm2.PathWalkWithFlags("/data.bin", LookupFlags.FollowSymlink);
            Assert.True(loc2.IsValid);
            var file2 = new LinuxFile(loc2.Dentry!, FileFlags.O_RDWR, loc2.Mount!);
            loc2.Dentry!.Inode!.Open(file2);
            _ = sm2.AllocFD(file2);

            const uint mapAddr = 0x45000000;
            mm2.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, file2, 0, (long)file2.Dentry.Inode!.Size, "MAP_SHARED", engine2);
            Assert.True(mm2.HandleFault(mapAddr, true, engine2));
            Assert.True(engine2.CopyToUser(mapAddr + 1, "xy"u8.ToArray()));

            var rc = await CallSys("SysSync", engine1.State);
            Assert.Equal(0, rc);
            Assert.Equal("AxyDE", File.ReadAllText(hostFile));
        }
        finally
        {
            KernelScheduler.Current = oldCurrent;
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Sync_WritesBackMappedDirtyPagesFromSharedAddressSpacePeer()
    {
        var root = Path.Combine(Path.GetTempPath(), "hostfs-sync-shared-mm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        File.WriteAllText(hostFile, "ABCDE");

        var oldCurrent = KernelScheduler.Current;
        try
        {
            using var engine1 = new Engine();
            using var engine2 = new Engine();
            var mm = new VMAManager();
            var sm1 = new SyscallManager(engine1, mm, 0);
            var sm2 = new SyscallManager(engine2, mm, 0);
            sm1.MountRootHostfs(root);
            sm2.MountRootHostfs(root);

            var scheduler = new KernelScheduler();
            KernelScheduler.Current = scheduler;

            var process1 = new Process(1011, mm, sm1);
            var process2 = new Process(1012, mm, sm2);
            scheduler.RegisterProcess(process1);
            scheduler.RegisterProcess(process2);

            var task1 = new FiberTask(1011, process1, engine1, scheduler);
            var task2 = new FiberTask(1012, process2, engine2, scheduler);
            engine1.Owner = task1;
            engine2.Owner = task2;

            var loc2 = sm2.PathWalkWithFlags("/data.bin", LookupFlags.FollowSymlink);
            Assert.True(loc2.IsValid);
            var file2 = new LinuxFile(loc2.Dentry!, FileFlags.O_RDWR, loc2.Mount!);
            loc2.Dentry!.Inode!.Open(file2);
            _ = sm2.AllocFD(file2);

            const uint mapAddr = 0x45200000;
            mm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, file2, 0, (long)file2.Dentry.Inode!.Size, "MAP_SHARED", engine2);
            Assert.True(mm.HandleFault(mapAddr, true, engine2));
            Assert.True(engine2.CopyToUser(mapAddr + 1, "xy"u8.ToArray()));

            var rc = await CallSys("SysSync", engine1.State);
            Assert.Equal(0, rc);
            Assert.Equal("AxyDE", File.ReadAllText(hostFile));
        }
        finally
        {
            KernelScheduler.Current = oldCurrent;
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Fsync_WritesBackMappedDirtyPagesFromOtherProcessInSameContainer()
    {
        var root = Path.Combine(Path.GetTempPath(), "hostfs-fsync-container-wide-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        File.WriteAllText(hostFile, "ABCDE");

        try
        {
            using var engine1 = new Engine();
            using var engine2 = new Engine();
            var mm1 = new VMAManager();
            var mm2 = new VMAManager();
            var sm1 = new SyscallManager(engine1, mm1, 0);
            sm1.MountRootHostfs(root);
            _ = new SyscallManager(engine2, mm2, 0);

            var loc1 = sm1.PathWalkWithFlags("/data.bin", LookupFlags.FollowSymlink);
            Assert.True(loc1.IsValid);
            var file1 = new LinuxFile(loc1.Dentry!, FileFlags.O_RDWR, loc1.Mount!);
            loc1.Dentry!.Inode!.Open(file1);
            var fd1 = sm1.AllocFD(file1);

            // Use the same dentry/mount object so both mappings target the exact same inode identity.
            var file2 = new LinuxFile(loc1.Dentry!, FileFlags.O_RDWR, loc1.Mount!);
            loc1.Dentry!.Inode!.Open(file2);
            var fd2 = sm1.AllocFD(file2);

            const uint mapAddr = 0x45100000;
            mm2.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, file2, 0, (long)file2.Dentry.Inode!.Size, "MAP_SHARED", engine2);
            Assert.True(mm2.HandleFault(mapAddr, true, engine2));
            Assert.True(engine2.CopyToUser(mapAddr + 1, "xy"u8.ToArray()));

            var rc = await CallSys("SysFsync", engine1.State, (uint)fd1);
            Assert.Equal(0, rc);
            Assert.Equal("AxyDE", File.ReadAllText(hostFile));

            sm1.FreeFD(fd1);
            sm1.FreeFD(fd2);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Fsync_MustFlushBufferedWritePageCacheForSameFd()
    {
        var root = Path.Combine(Path.GetTempPath(), "hostfs-fsync-write-buffered-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        File.WriteAllText(hostFile, "abcde");

        var manager = new MemoryObjectManager();
        try
        {
            using var engine = new Engine();
            var mm = new VMAManager();
            var sm = new SyscallManager(engine, mm, 0);
            sm.MountRootHostfs(root);
            var loc = sm.PathWalkWithFlags("/data.bin", LookupFlags.FollowSymlink);
            Assert.True(loc.IsValid);
            var file = new LinuxFile(loc.Dentry!, FileFlags.O_RDWR, loc.Mount!);
            loc.Dentry!.Inode!.Open(file);
            var fd = sm.AllocFD(file);

            _ = manager.GetOrCreateInodePageCache(file.Dentry.Inode!);
            var writeRc = file.Dentry.Inode!.Write(file, "XY"u8.ToArray(), 1);
            Assert.Equal(2, writeRc);
            Assert.Equal("abcde", File.ReadAllText(hostFile));

            var fsyncRc = await CallSys("SysFsync", engine.State, (uint)fd);
            Assert.Equal(0, fsyncRc);
            Assert.Equal("aXYde", File.ReadAllText(hostFile));

            sm.FreeFD(fd);
            manager.ReleaseInodePageCache(file.Dentry.Inode);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Mmap_HoldsFileReference_AfterFdCloseUntilMunmap()
    {
        var root = Path.Combine(Path.GetTempPath(), "hostfs-mmap-file-ref-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hostFile = Path.Combine(root, "data.bin");
        File.WriteAllText(hostFile, "abcde");

        try
        {
            using var engine = new Engine();
            var mm = new VMAManager();
            var sm = new SyscallManager(engine, mm, 0);
            sm.MountRootHostfs(root);
            var loc = sm.PathWalkWithFlags("/data.bin", LookupFlags.FollowSymlink);
            Assert.True(loc.IsValid);
            var file = new LinuxFile(loc.Dentry!, FileFlags.O_RDWR, loc.Mount!);
            loc.Dentry!.Inode!.Open(file);
            var fd = sm.AllocFD(file);
            var inode = loc.Dentry.Inode!;
            var refBeforeMmap = inode.RefCount;

            const uint mapAddr = 0x46000000;
            var mmapRc = await CallSys(
                "SysMmap2",
                engine.State,
                mapAddr,
                LinuxConstants.PageSize,
                (uint)(Protection.Read | Protection.Write),
                (uint)(MapFlags.Shared | MapFlags.Fixed),
                (uint)fd,
                0);
            Assert.Equal((int)mapAddr, mmapRc);
            Assert.Equal(refBeforeMmap + 1, inode.RefCount);

            sm.FreeFD(fd);
            Assert.Equal(refBeforeMmap, inode.RefCount);

            Assert.True(mm.HandleFault(mapAddr, true, engine));
            Assert.True(engine.CopyToUser(mapAddr + 1, "ZZ"u8.ToArray()));

            mm.Munmap(mapAddr, LinuxConstants.PageSize, engine);
            Assert.Equal(refBeforeMmap - 1, inode.RefCount);
            Assert.Equal("aZZde", File.ReadAllText(hostFile));
        }
        finally
        {
            Directory.Delete(root, true);
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

    private static async ValueTask<int> CallSys(string methodName, IntPtr state, uint a1 = 0, uint a2 = 0, uint a3 = 0,
        uint a4 = 0, uint a5 = 0, uint a6 = 0)
    {
        var method = typeof(SyscallManager).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var task = (ValueTask<int>)method!.Invoke(null, [state, a1, a2, a3, a4, a5, a6])!;
        return await task;
    }
}
