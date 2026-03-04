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
