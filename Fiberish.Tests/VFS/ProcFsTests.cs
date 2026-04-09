using System.Reflection;
using System.Text;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.VFS;

[Collection("ExternalPageManagerSerial")]
public class ProcFsTests
{
    [Fact]
    public void ProcMounts_ShouldReflectNewMountsDynamically()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var hostMountDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(rootDir);
        Directory.CreateDirectory(hostMountDir);

        try
        {
            var runtime = KernelRuntime.Bootstrap(rootDir, false, false);
            var sm = runtime.Syscalls;
            var task = AttachTaskContext(runtime, 0, true);

            var mountsBefore = ReadAll(task, sm.PathWalk("/proc/mounts"));
            Assert.DoesNotContain(" /tests hostfs ", mountsBefore);

            sm.MountHostfs(hostMountDir, "/tests");

            var mountsAfter = ReadAll(task, sm.PathWalk("/proc/mounts"));
            Assert.Contains(" /tests hostfs ", mountsAfter);
            Assert.Contains(hostMountDir, mountsAfter);
        }
        finally
        {
            if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true);
            if (Directory.Exists(hostMountDir)) Directory.Delete(hostMountDir, true);
        }
    }

    [Fact]
    public void ProcPidFiles_ShouldBeGeneratedFromProcessState()
    {
        using var ctx = new ProcTestContext();

        var process = new Process(4242, null!, null!)
        {
            PPID = 1,
            PGID = 4242,
            SID = 4242,
            State = ProcessState.Running
        };
        ctx.Scheduler.RegisterProcess(process);

        var fs = new ProcFileSystem();
        var sb = (ProcSuperBlock)fs.ReadSuper(new FileSystemType { Name = "proc" }, 0, "proc", ctx.SyscallManager);
        var mount = new Mount(sb, sb.Root)
        {
            Source = "proc",
            FsType = "proc",
            Options = "rw,relatime"
        };

        var pidDir = Lookup(ctx.Task, sb.Root, "4242");
        Assert.NotNull(pidDir);
        Assert.Equal(InodeType.Directory, pidDir!.Inode!.Type);

        var status = Lookup(ctx.Task, pidDir, "status");
        var stat = Lookup(ctx.Task, pidDir, "stat");
        var cmdline = Lookup(ctx.Task, pidDir, "cmdline");
        Assert.NotNull(status);
        Assert.NotNull(stat);
        Assert.NotNull(cmdline);

        var statusText = ReadAll(ctx.Task, status!, mount);
        var statText = ReadAll(ctx.Task, stat!, mount);
        var cmdlineText = ReadAll(ctx.Task, cmdline!, mount);

        Assert.Contains("Pid:\t4242", statusText);
        Assert.StartsWith("4242 (process) R 1 4242 4242", statText);
        Assert.Equal(string.Empty, cmdlineText);
    }

    [Fact]
    public void ProcSystemFiles_ShouldExposeStatUptimeLoadavgAndSysctl()
    {
        using var ctx = new ProcTestContext();

        var fs = new ProcFileSystem();
        var sb = (ProcSuperBlock)fs.ReadSuper(new FileSystemType { Name = "proc" }, 0, "proc", ctx.SyscallManager);
        var mount = new Mount(sb, sb.Root)
        {
            Source = "proc",
            FsType = "proc",
            Options = "rw,relatime"
        };

        Assert.Contains("btime ", ReadAll(ctx.Task, Lookup(ctx.Task, sb.Root, "stat")!, mount));
        Assert.Contains("/", ReadAll(ctx.Task, Lookup(ctx.Task, sb.Root, "loadavg")!, mount));
        Assert.Contains(" ", ReadAll(ctx.Task, Lookup(ctx.Task, sb.Root, "uptime")!, mount));

        var sys = Lookup(ctx.Task, sb.Root, "sys");
        Assert.NotNull(sys);
        var kernel = Lookup(ctx.Task, sys!, "kernel");
        Assert.NotNull(kernel);
        Assert.Equal("x86emu\n", ReadAll(ctx.Task, Lookup(ctx.Task, kernel!, "hostname")!, mount));
    }

    [Fact]
    public void ProcPidLookup_ShouldDisappearAfterReap()
    {
        using var ctx = new ProcTestContext();

        var child = new Process(5555, null!, null!) { State = ProcessState.Zombie };
        ctx.Scheduler.RegisterProcess(child);

        var fs = new ProcFileSystem();
        var sb = (ProcSuperBlock)fs.ReadSuper(new FileSystemType { Name = "proc" }, 0, "proc", ctx.SyscallManager);

        var first = Lookup(ctx.Task, sb.Root, "5555");
        Assert.NotNull(first);

        ctx.Scheduler.UnregisterProcess(5555);

        var second = Lookup(ctx.Task, sb.Root, "5555");
        Assert.Null(second);
    }

    [Fact]
    public void ProcPidSymlinksAndFd_ShouldExposeExeCwdRootFdAndFdinfo()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(rootDir);

        using var ctx = new ProcTestContext();

        try
        {
            var runtime = KernelRuntime.Bootstrap(rootDir, false, false);
            var process = new Process(7777, runtime.Memory, runtime.Syscalls)
            {
                PPID = 1,
                PGID = 7777,
                SID = 7777
            };

            var exeProp = typeof(Process).GetProperty(nameof(Process.ExecutablePath),
                BindingFlags.Instance | BindingFlags.Public);
            Assert.NotNull(exeProp);
            exeProp!.SetValue(process, "/bin/test-app");

            var stdinLoc = runtime.Syscalls.PathWalk("/");
            Assert.True(stdinLoc.IsValid);
            var stdinFile = new LinuxFile(stdinLoc.Dentry!, FileFlags.O_RDONLY, stdinLoc.Mount!);
            process.Syscalls.AllocFD(stdinFile);

            ctx.Scheduler.RegisterProcess(process);

            var fs = new ProcFileSystem();
            var sb = (ProcSuperBlock)fs.ReadSuper(new FileSystemType { Name = "proc" }, 0, "proc", ctx.SyscallManager);
            var mount = new Mount(sb, sb.Root)
            {
                Source = "proc",
                FsType = "proc",
                Options = "rw,relatime"
            };

            var pidDir = Lookup(ctx.Task, sb.Root, "7777");
            Assert.NotNull(pidDir);

            var exe = Lookup(ctx.Task, pidDir!, "exe");
            var cwd = Lookup(ctx.Task, pidDir, "cwd");
            var root = Lookup(ctx.Task, pidDir, "root");
            Assert.NotNull(exe);
            Assert.NotNull(cwd);
            Assert.NotNull(root);
            Assert.Equal("/bin/test-app", Readlink(ctx.Task, exe!));
            Assert.Equal("/", Readlink(ctx.Task, cwd!));
            Assert.Equal("/", Readlink(ctx.Task, root!));

            var fdDir = Lookup(ctx.Task, pidDir, "fd");
            var fdInfoDir = Lookup(ctx.Task, pidDir, "fdinfo");
            Assert.NotNull(fdDir);
            Assert.NotNull(fdInfoDir);

            var fd0 = Lookup(ctx.Task, fdDir!, "0");
            Assert.NotNull(fd0);
            var fd0Target = Readlink(ctx.Task, fd0!);
            Assert.False(string.IsNullOrWhiteSpace(fd0Target));

            var fdInfo0 = Lookup(ctx.Task, fdInfoDir!, "0");
            Assert.NotNull(fdInfo0);
            var infoText = ReadAll(ctx.Task, fdInfo0!, mount);
            Assert.Contains("pos:\t", infoText);
            Assert.Contains("flags:\t0", infoText);
            Assert.Contains("mnt_id:\t", infoText);
        }
        finally
        {
            if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true);
        }
    }

    [Fact]
    public void ProcSelf_ShouldResolveToCurrentSchedulerTaskProcess()
    {
        using var ctx = new ProcTestContext();

        var process2 = new Process(2, ctx.Memory, ctx.SyscallManager)
        {
            PPID = 1,
            PGID = 2,
            SID = 2
        };
        ctx.Scheduler.RegisterProcess(process2);

        using var engine2 = new Engine();
        var task2 = new FiberTask(2, process2, engine2, ctx.Scheduler);

        var fs = new ProcFileSystem();
        var sb = (ProcSuperBlock)fs.ReadSuper(new FileSystemType { Name = "proc" }, 0, "proc", ctx.SyscallManager);

        var self = Lookup(ctx.Task, sb.Root, "self");
        Assert.NotNull(self);

        Assert.Equal("2", Readlink(task2, self!));
        Assert.Equal("1", Readlink(ctx.Task, self));
    }

    [Fact]
    public void ProcMemInfo_ShouldUseLiveMemoryStats()
    {
        var oldQuota = PageManager.MemoryQuotaBytes;
        var allocated = IntPtr.Zero;
        PageManager.MemoryQuotaBytes = 64L * 1024 * 1024;
        try
        {
            Assert.True(PageManager.TryAllocateExternalPageStrict(out allocated, AllocationClass.Anonymous));
            var text = ProcFsManager.GenerateMemInfo(null);

            var total = ParseMemInfoKiB(text, "MemTotal");
            var free = ParseMemInfoKiB(text, "MemFree");
            var available = ParseMemInfoKiB(text, "MemAvailable");
            var mapped = ParseMemInfoKiB(text, "Mapped");
            var hostMapped = ParseMemInfoKiB(text, "HostMapped");
            var shmem = ParseMemInfoKiB(text, "Shmem");
            var writeback = ParseMemInfoKiB(text, "Writeback");
            var committed = ParseMemInfoKiB(text, "Committed_AS");
            var active = ParseMemInfoKiB(text, "Active");
            var inactive = ParseMemInfoKiB(text, "Inactive");
            var activeAnon = ParseMemInfoKiB(text, "Active(anon)");
            var inactiveAnon = ParseMemInfoKiB(text, "Inactive(anon)");
            var activeFile = ParseMemInfoKiB(text, "Active(file)");
            var inactiveFile = ParseMemInfoKiB(text, "Inactive(file)");

            Assert.True(total > 0);
            Assert.InRange(free, 0, total);
            Assert.InRange(available, 0, total);
            Assert.True(mapped > 0);
            Assert.True(hostMapped >= 0);
            Assert.True(shmem >= 0);
            Assert.True(writeback >= 0);
            Assert.True(committed >= 0);
            Assert.True(active >= 0);
            Assert.True(inactive >= 0);
            Assert.True(activeAnon >= 0);
            Assert.True(inactiveAnon >= 0);
            Assert.True(activeFile >= 0);
            Assert.True(inactiveFile >= 0);
        }
        finally
        {
            if (allocated != IntPtr.Zero) PageManager.ReleasePtr(allocated);
            PageManager.MemoryQuotaBytes = oldQuota;
        }
    }

    [Fact]
    public void ProcSysVmDropCaches_WriteShouldReclaimPageCache()
    {
        using var cacheScope = AddressSpacePolicy.BeginIsolatedScope();
        var rootDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(rootDir);
        AddressSpace? cache = null;

        try
        {
            var runtime = KernelRuntime.Bootstrap(rootDir, false, false);
            var sm = runtime.Syscalls;
            var task = AttachTaskContext(runtime, 0, true);
            var loc = sm.PathWalk("/proc/sys/vm/drop_caches");
            Assert.True(loc.IsValid);
            Assert.Equal("0\n", ReadAll(task, loc));

            cache = new AddressSpace(AddressSpaceKind.File);
            AddressSpacePolicy.TrackAddressSpace(cache);
            var page = cache.GetOrCreatePage(0, _ => true, out _, true, AllocationClass.PageCache);
            Assert.NotEqual(IntPtr.Zero, page);
            Assert.True(cache.PageCount > 0);

            Assert.Equal(-(int)Errno.EINVAL, WriteAll(task, loc, "9\n"));
            Assert.Equal(2, WriteAll(task, loc, "1\n"));
            Assert.Equal(0, cache.PageCount);
        }
        finally
        {
            cache?.Release();
            if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true);
        }
    }

    [Fact]
    public void ProcSysVmDropCaches_Mode2_ShouldDropDentryCacheAndSweepUnusedInodes()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(rootDir);

        try
        {
            var runtime = KernelRuntime.Bootstrap(rootDir, false, false);
            var sm = runtime.Syscalls;
            var task = AttachTaskContext(runtime, 0, true);

            var dropLoc = sm.PathWalk("/proc/sys/vm/drop_caches");
            Assert.True(dropLoc.IsValid);

            // Warm proc dentry caches.
            var procRoot = sm.PathWalk("/proc");
            Assert.True(procRoot.IsValid);
            var procRootDentry = procRoot.Dentry!;
            Assert.NotNull(Lookup(task, procRootDentry, "sys"));
            Assert.True(procRootDentry.Children.ContainsKey("sys"));
            var procSb = procRoot.Mount!.SB;
            var procTrackedBefore = procSb.Inodes.Count;

            var shmLoc = sm.PathWalk("/dev/shm");
            if (!shmLoc.IsValid)
            {
                sm.MountStandardShm();
                shmLoc = sm.PathWalk("/dev/shm");
            }

            Assert.True(shmLoc.IsValid);
            var shmDir = shmLoc.Dentry!;
            var tmp = new Dentry("drop_inode.tmp", null, shmDir, shmDir.SuperBlock);
            shmDir.Inode!.Create(tmp, 0x1A4, 0, 0);
            shmDir.Inode.Unlink("drop_inode.tmp");

            Assert.Equal(2, WriteAll(task, dropLoc, "2\n"));

            Assert.False(procRootDentry.Children.ContainsKey("sys"));
            Assert.True(procSb.Inodes.Count <= procTrackedBefore);

            var rematerialized = sm.PathWalk("/proc/sys/vm");
            Assert.True(rematerialized.IsValid);
        }
        finally
        {
            if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true);
        }
    }

    [Fact]
    public void ProcSysVmDropCaches_Mode3_ShouldReclaimPagecacheAndVfsCaches()
    {
        using var cacheScope = AddressSpacePolicy.BeginIsolatedScope();
        var rootDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(rootDir);
        AddressSpace? cache = null;

        try
        {
            var runtime = KernelRuntime.Bootstrap(rootDir, false, false);
            var sm = runtime.Syscalls;
            var task = AttachTaskContext(runtime, 0, true);

            var dropLoc = sm.PathWalk("/proc/sys/vm/drop_caches");
            Assert.True(dropLoc.IsValid);

            var procRoot = sm.PathWalk("/proc");
            Assert.True(procRoot.IsValid);
            var procRootDentry = procRoot.Dentry!;
            Assert.NotNull(Lookup(task, procRootDentry, "sys"));
            Assert.True(procRootDentry.TryGetCachedChild("sys", out _));

            cache = new AddressSpace(AddressSpaceKind.File);
            AddressSpacePolicy.TrackAddressSpace(cache);
            var page = cache.GetOrCreatePage(0, _ => true, out _, true, AllocationClass.PageCache);
            Assert.NotEqual(IntPtr.Zero, page);
            Assert.True(cache.PageCount > 0);

            var shmLoc = sm.PathWalk("/dev/shm");
            if (!shmLoc.IsValid)
            {
                sm.MountStandardShm();
                shmLoc = sm.PathWalk("/dev/shm");
            }

            Assert.True(shmLoc.IsValid);
            var shmDir = shmLoc.Dentry!;
            var tmp = new Dentry("drop_mode3.tmp", null, shmDir, shmDir.SuperBlock);
            shmDir.Inode!.Create(tmp, 0x1A4, 0, 0);
            shmDir.Inode.Unlink("drop_mode3.tmp");

            Assert.Equal(2, WriteAll(task, dropLoc, "3\n"));

            Assert.Equal(0, cache.PageCount);
            Assert.False(procRootDentry.TryGetCachedChild("sys", out _));
        }
        finally
        {
            cache?.Release();
            if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true);
        }
    }

    [Fact]
    public void ProcSysVmDropCaches_Mode1_ShouldTrimHostfsMappedWindows_WhenInactive()
    {
        using var cacheScope = AddressSpacePolicy.BeginIsolatedScope();
        var hostRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(hostRoot);
        File.WriteAllBytes(Path.Combine(hostRoot, "data.bin"), new byte[LinuxConstants.PageSize * 2]);

        try
        {
            var runtime = KernelRuntime.Bootstrap(hostRoot, false, false);
            var sm = runtime.Syscalls;
            var task = AttachTaskContext(runtime, 0, true);
            MountHostfsAt(sm, hostRoot, "/mnt");

            var dropLoc = sm.PathWalk("/proc/sys/vm/drop_caches");
            var loc = sm.PathWalkWithFlags("/mnt/data.bin", LookupFlags.FollowSymlink);
            Assert.True(loc.IsValid);

            var file = new LinuxFile(loc.Dentry!, FileFlags.O_RDWR, loc.Mount!);
            loc.Dentry!.Inode!.Open(file);
            const uint mapAddr = 0x4A000000;
            runtime.Memory.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, file, 0, "HOSTFS_DROP_INACTIVE", runtime.Engine);
            Assert.True(runtime.Memory.HandleFault(mapAddr, true, runtime.Engine));
            runtime.Memory.Munmap(mapAddr, LinuxConstants.PageSize, runtime.Engine);

            var inode = Assert.IsType<HostInode>(loc.Dentry!.Inode);
            var cache = Assert.IsType<AddressSpace>(inode.Mapping);
            Assert.True(cache.PageCount > 0);
            Assert.True(inode.GetMappedPageCacheDiagnostics().WindowBytes > 0);

            var before = MemoryStatsSnapshot.Capture(sm);
            Assert.True(before.HostMappedWindowBytes > 0);

            Assert.Equal(2, WriteAll(task, dropLoc, "1\n"));

            Assert.Equal(0, cache.PageCount);
            Assert.Equal(0, inode.GetMappedPageCacheDiagnostics().WindowBytes);
            var after = MemoryStatsSnapshot.Capture(sm);
            Assert.Equal(0, after.HostMappedWindowBytes);
        }
        finally
        {
            if (Directory.Exists(hostRoot)) Directory.Delete(hostRoot, true);
        }
    }

    [Fact]
    public void ProcSysVmDropCaches_Mode1_ShouldPreserveHostfsMappedWindows_WhenActive()
    {
        using var cacheScope = AddressSpacePolicy.BeginIsolatedScope();
        var hostRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(hostRoot);
        File.WriteAllBytes(Path.Combine(hostRoot, "data.bin"), new byte[LinuxConstants.PageSize * 2]);

        try
        {
            var runtime = KernelRuntime.Bootstrap(hostRoot, false, false);
            var sm = runtime.Syscalls;
            var task = AttachTaskContext(runtime, 0, true);
            MountHostfsAt(sm, hostRoot, "/mnt");

            var dropLoc = sm.PathWalk("/proc/sys/vm/drop_caches");
            var loc = sm.PathWalkWithFlags("/mnt/data.bin", LookupFlags.FollowSymlink);
            Assert.True(loc.IsValid);

            var file = new LinuxFile(loc.Dentry!, FileFlags.O_RDWR, loc.Mount!);
            loc.Dentry!.Inode!.Open(file);
            const uint mapAddr = 0x4A100000;
            runtime.Memory.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, file, 0, "HOSTFS_DROP_ACTIVE", runtime.Engine);
            Assert.True(runtime.Memory.HandleFault(mapAddr, true, runtime.Engine));

            var inode = Assert.IsType<HostInode>(loc.Dentry!.Inode);
            var beforeDiag = inode.GetMappedPageCacheDiagnostics();
            Assert.True(beforeDiag.WindowBytes > 0);

            Assert.Equal(2, WriteAll(task, dropLoc, "1\n"));

            var afterDiag = inode.GetMappedPageCacheDiagnostics();
            Assert.Equal(beforeDiag.WindowBytes, afterDiag.WindowBytes);
            Assert.True(runtime.Engine.CopyToUser(mapAddr, "AB"u8.ToArray()));

            runtime.Memory.Munmap(mapAddr, LinuxConstants.PageSize, runtime.Engine);
        }
        finally
        {
            if (Directory.Exists(hostRoot)) Directory.Delete(hostRoot, true);
        }
    }

    [Fact]
    public void ProcSysVmDropCaches_Mode1_ShouldTrimSilkMappedWindows_WhenInactive()
    {
        using var cacheScope = AddressSpacePolicy.BeginIsolatedScope();
        var silkRoot = Path.Combine(Path.GetTempPath(), $"silkfs-drop-{Guid.NewGuid():N}");

        try
        {
            var runtime = CreateTmpfsProcRuntime();
            var sm = runtime.Syscalls;
            var task = AttachTaskContext(runtime, 0, true);
            MountSilkfsAt(sm, silkRoot, "/mnt");

            var dropLoc = sm.PathWalk("/proc/sys/vm/drop_caches");
            var loc = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
            Assert.True(loc.IsValid);

            var file = new Dentry("data.bin", null, loc.Dentry, loc.Dentry!.SuperBlock);
            loc.Dentry.Inode!.Create(file, 0x1A4, 0, 0);
            var wf = new LinuxFile(file, FileFlags.O_WRONLY, loc.Mount!);
            Assert.Equal(LinuxConstants.PageSize * 2,
                file.Inode!.WriteFromHost(null, wf, new byte[LinuxConstants.PageSize * 2], 0));
            wf.Close();

            var mappedFile = new LinuxFile(file, FileFlags.O_RDWR, loc.Mount!);
            file.Inode!.Open(mappedFile);
            const uint mapAddr = 0x4A200000;
            runtime.Memory.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, mappedFile, 0, "SILKFS_DROP_INACTIVE", runtime.Engine);
            Assert.True(runtime.Memory.HandleFault(mapAddr, true, runtime.Engine));
            runtime.Memory.Munmap(mapAddr, LinuxConstants.PageSize, runtime.Engine);

            var inode = Assert.IsType<SilkInode>(file.Inode);
            var cache = Assert.IsType<AddressSpace>(inode.Mapping);
            Assert.True(cache.PageCount > 0);
            Assert.True(inode.GetMappedPageCacheDiagnostics().WindowBytes >= 0);

            var before = MemoryStatsSnapshot.Capture(sm);
            Assert.True(before.HostMappedWindowBytes >= 0);

            Assert.Equal(2, WriteAll(task, dropLoc, "1\n"));

            Assert.Equal(0, cache.PageCount);
            Assert.Equal(0, inode.GetMappedPageCacheDiagnostics().WindowBytes);
            var after = MemoryStatsSnapshot.Capture(sm);
            Assert.Equal(0, after.HostMappedWindowBytes);
        }
        finally
        {
            if (Directory.Exists(silkRoot)) Directory.Delete(silkRoot, true);
        }
    }

    [Fact]
    public void ProcSysVmDropCaches_Mode1_ShouldPreserveSilkMappedWindows_WhenActive()
    {
        using var cacheScope = AddressSpacePolicy.BeginIsolatedScope();
        var silkRoot = Path.Combine(Path.GetTempPath(), $"silkfs-drop-{Guid.NewGuid():N}");

        try
        {
            var runtime = CreateTmpfsProcRuntime();
            var sm = runtime.Syscalls;
            var task = AttachTaskContext(runtime, 0, true);
            MountSilkfsAt(sm, silkRoot, "/mnt");

            var dropLoc = sm.PathWalk("/proc/sys/vm/drop_caches");
            var loc = sm.PathWalkWithFlags("/mnt", LookupFlags.FollowSymlink);
            Assert.True(loc.IsValid);

            var file = new Dentry("active.bin", null, loc.Dentry, loc.Dentry!.SuperBlock);
            loc.Dentry.Inode!.Create(file, 0x1A4, 0, 0);
            var wf = new LinuxFile(file, FileFlags.O_WRONLY, loc.Mount!);
            Assert.Equal(LinuxConstants.PageSize * 2,
                file.Inode!.WriteFromHost(null, wf, new byte[LinuxConstants.PageSize * 2], 0));
            wf.Close();

            var mappedFile = new LinuxFile(file, FileFlags.O_RDWR, loc.Mount!);
            file.Inode!.Open(mappedFile);
            const uint mapAddr = 0x4A300000;
            runtime.Memory.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, mappedFile, 0, "SILKFS_DROP_ACTIVE", runtime.Engine);
            Assert.True(runtime.Memory.HandleFault(mapAddr, true, runtime.Engine));

            var inode = Assert.IsType<SilkInode>(file.Inode);
            var cache = Assert.IsType<AddressSpace>(inode.Mapping);
            Assert.True(cache.PageCount > 0);
            var beforeDiag = inode.GetMappedPageCacheDiagnostics();
            Assert.True(beforeDiag.WindowBytes >= 0);

            Assert.Equal(2, WriteAll(task, dropLoc, "1\n"));

            var afterDiag = inode.GetMappedPageCacheDiagnostics();
            Assert.True(cache.PageCount > 0);
            Assert.True(afterDiag.WindowBytes >= 0);
            Assert.True(runtime.Engine.CopyToUser(mapAddr, "CD"u8.ToArray()));

            var verify = new byte[2];
            Assert.True(runtime.Engine.CopyFromUser(mapAddr, verify));
            Assert.Equal("CD", Encoding.ASCII.GetString(verify));

            runtime.Memory.Munmap(mapAddr, LinuxConstants.PageSize, runtime.Engine);
        }
        finally
        {
            if (Directory.Exists(silkRoot)) Directory.Delete(silkRoot, true);
        }
    }

    [Fact]
    public void ProcSysVmDropCaches_Mode2_ShouldNotBreakTmpfsNamespaceData()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(rootDir);

        try
        {
            var runtime = KernelRuntime.Bootstrap(rootDir, false, false);
            var sm = runtime.Syscalls;
            var task = AttachTaskContext(runtime, 0, true);

            var dropLoc = sm.PathWalk("/proc/sys/vm/drop_caches");
            Assert.True(dropLoc.IsValid);

            var shmLoc = sm.PathWalk("/dev/shm");
            if (!shmLoc.IsValid)
            {
                sm.MountStandardShm();
                shmLoc = sm.PathWalk("/dev/shm");
            }

            Assert.True(shmLoc.IsValid);
            var shmDir = shmLoc.Dentry!;
            var fileDentry = new Dentry("stable.tmp", null, shmDir, shmDir.SuperBlock);
            shmDir.Inode!.Create(fileDentry, 0x1A4, 0, 0);

            var file = new LinuxFile(fileDentry, FileFlags.O_RDWR, shmLoc.Mount!);
            var payload = Encoding.UTF8.GetBytes("keep-tmpfs");
            Assert.Equal(payload.Length, fileDentry.Inode!.WriteFromHost(null, file, payload, 0));
            file.Close();

            Assert.Equal(2, WriteAll(task, dropLoc, "2\n"));

            var rel = sm.PathWalk("/dev/shm/stable.tmp");
            Assert.True(rel.IsValid);
            var readBack = ReadAll(rel);
            Assert.Equal("keep-tmpfs", readBack);
        }
        finally
        {
            if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true);
        }
    }

    [Fact]
    public void ProcSysVmDropCaches_Mode2_ShouldPreserveNestedMountCrossing()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var mountSource = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(rootDir);
        Directory.CreateDirectory(mountSource);
        File.WriteAllText(Path.Combine(mountSource, "hello.txt"), "hello");

        try
        {
            var runtime = KernelRuntime.Bootstrap(rootDir, false, false);
            var sm = runtime.Syscalls;
            var task = AttachTaskContext(runtime, 0, true);

            var root = sm.Root.Dentry!;
            var hold = Lookup(task, root, "hold");
            if (hold == null)
            {
                var holdDentry = new Dentry("hold", null, root, root.SuperBlock);
                root.Inode!.Mkdir(holdDentry, 0x1FF, 0, 0);
                root.Children["hold"] = holdDentry;
                hold = holdDentry;
            }

            var mnt = Lookup(task, hold, "mnt");
            if (mnt == null)
            {
                var mntDentry = new Dentry("mnt", null, hold, hold.SuperBlock);
                hold.Inode!.Mkdir(mntDentry, 0x1FF, 0, 0);
                hold.Children["mnt"] = mntDentry;
            }

            sm.MountHostfs(mountSource, "/hold/mnt");

            var before = sm.PathWalkWithFlags("/hold/mnt/hello.txt", LookupFlags.FollowSymlink);
            Assert.True(before.IsValid);
            Assert.Equal("hello", ReadAll(before));

            var dropLoc = sm.PathWalk("/proc/sys/vm/drop_caches");
            Assert.True(dropLoc.IsValid);
            Assert.Equal(2, WriteAll(task, dropLoc, "2\n"));

            var after = sm.PathWalkWithFlags("/hold/mnt/hello.txt", LookupFlags.FollowSymlink);
            Assert.True(after.IsValid);
            Assert.Equal("hello", ReadAll(after));
        }
        finally
        {
            if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true);
            if (Directory.Exists(mountSource)) Directory.Delete(mountSource, true);
        }
    }

    [Fact]
    public void ProcSysVmDropCaches_Mode2_ShouldPreserveNestedMountCrossing_OnOverlayRoot()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var mountSource = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(rootDir);
        Directory.CreateDirectory(mountSource);
        File.WriteAllText(Path.Combine(mountSource, "hello.txt"), "hello");

        try
        {
            var runtime = KernelRuntime.Bootstrap(rootDir, false, true);
            var sm = runtime.Syscalls;
            var task = AttachTaskContext(runtime, 0, true);

            var root = sm.Root.Dentry!;
            var hold = Lookup(task, root, "hold");
            if (hold == null)
            {
                var holdDentry = new Dentry("hold", null, root, root.SuperBlock);
                root.Inode!.Mkdir(holdDentry, 0x1FF, 0, 0);
                root.Children["hold"] = holdDentry;
                hold = holdDentry;
            }

            var mnt = Lookup(task, hold, "mnt");
            if (mnt == null)
            {
                var mntDentry = new Dentry("mnt", null, hold, hold.SuperBlock);
                hold.Inode!.Mkdir(mntDentry, 0x1FF, 0, 0);
                hold.Children["mnt"] = mntDentry;
            }

            sm.MountHostfs(mountSource, "/hold/mnt");

            var before = sm.PathWalkWithFlags("/hold/mnt/hello.txt", LookupFlags.FollowSymlink);
            Assert.True(before.IsValid);
            Assert.Equal("hello", ReadAll(before));

            var dropLoc = sm.PathWalk("/proc/sys/vm/drop_caches");
            Assert.True(dropLoc.IsValid);
            Assert.Equal(2, WriteAll(task, dropLoc, "2\n"));

            var after = sm.PathWalkWithFlags("/hold/mnt/hello.txt", LookupFlags.FollowSymlink);
            Assert.True(after.IsValid);
            Assert.Equal("hello", ReadAll(after));
        }
        finally
        {
            if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true);
            if (Directory.Exists(mountSource)) Directory.Delete(mountSource, true);
        }
    }

    [Fact]
    public void ProcSysVmDropCaches_WriteWithoutCapSysAdmin_ReturnsEperm()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(rootDir);

        try
        {
            var runtime = KernelRuntime.Bootstrap(rootDir, false, false);
            var sm = runtime.Syscalls;
            var task = AttachTaskContext(runtime, 1000, false);

            var loc = sm.PathWalk("/proc/sys/vm/drop_caches");
            Assert.True(loc.IsValid);
            Assert.Equal(-(int)Errno.EPERM, WriteAll(task, loc, "1\n"));
        }
        finally
        {
            if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true);
        }
    }

    [Fact]
    public void ProcSysVmDropCaches_WriteWithCapSysAdmin_AllowsNonRootCaller()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(rootDir);

        try
        {
            var runtime = KernelRuntime.Bootstrap(rootDir, false, false);
            var sm = runtime.Syscalls;
            var task = AttachTaskContext(runtime, 1000, true);

            var loc = sm.PathWalk("/proc/sys/vm/drop_caches");
            Assert.True(loc.IsValid);
            Assert.Equal(2, WriteAll(task, loc, "1\n"));
        }
        finally
        {
            if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true);
        }
    }

    [Fact]
    public async Task ProcSysVmDropCaches_OpenWithTrunc_ShouldSucceedAndKeepReadValueAtZero()
    {
        using var cacheScope = AddressSpacePolicy.BeginIsolatedScope();
        var rootDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(rootDir);
        AddressSpace? cache = null;

        try
        {
            var runtime = KernelRuntime.Bootstrap(rootDir, false, false);
            var sm = runtime.Syscalls;
            var task = AttachTaskContext(runtime, 0, true);

            const uint pathAddr = 0x10000;
            const uint writeAddr = 0x11000;
            MapUserPage(runtime, pathAddr);
            MapUserPage(runtime, writeAddr);
            WriteCString(runtime, pathAddr, "/proc/sys/vm/drop_caches");
            Assert.True(runtime.Engine.CopyToUser(writeAddr, Encoding.UTF8.GetBytes("1\n")));

            cache = new AddressSpace(AddressSpaceKind.File);
            AddressSpacePolicy.TrackAddressSpace(cache);
            _ = cache.GetOrCreatePage(0, _ => true, out _, true, AllocationClass.PageCache);
            Assert.True(cache.PageCount > 0);

            var fd = await CallSyscall(sm, "SysOpen", pathAddr, (uint)(FileFlags.O_WRONLY | FileFlags.O_TRUNC));
            Assert.True(fd >= 0);
            Assert.Equal(2, await CallSyscall(sm, "SysWrite", (uint)fd, writeAddr, 2u));
            Assert.Equal(0, await CallSyscall(sm, "SysClose", (uint)fd));
            Assert.Equal(0, cache.PageCount);

            var loc = sm.PathWalk("/proc/sys/vm/drop_caches");
            Assert.True(loc.IsValid);
            Assert.Equal("0\n", ReadAll(task, loc));
        }
        finally
        {
            cache?.Release();
            if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true);
        }
    }

    private static string ReadAll(FiberTask task, PathLocation loc)
    {
        Assert.True(loc.IsValid);
        return ReadAll(task, loc.Dentry!, loc.Mount!);
    }

    private static string ReadAll(PathLocation loc)
    {
        Assert.True(loc.IsValid);
        return ReadAll(loc.Dentry!, loc.Mount!);
    }

    private static string ReadAll(FiberTask task, Dentry dentry, Mount mount)
    {
        var file = new LinuxFile(dentry, FileFlags.O_RDONLY, mount);
        try
        {
            BindTaskContext(file, task);
            var sb = new StringBuilder();
            var buffer = new byte[256];
            long offset = 0;
            while (true)
            {
                var n = dentry.Inode!.ReadToHost(null, file, buffer, offset);
                Assert.True(n >= 0);
                if (n == 0) break;

                sb.Append(Encoding.UTF8.GetString(buffer, 0, n));
                offset += n;
            }

            return sb.ToString();
        }
        finally
        {
            file.Close();
        }
    }

    private static string ReadAll(Dentry dentry, Mount mount)
    {
        var file = new LinuxFile(dentry, FileFlags.O_RDONLY, mount);
        try
        {
            var sb = new StringBuilder();
            var buffer = new byte[256];
            long offset = 0;
            while (true)
            {
                var n = dentry.Inode!.ReadToHost(null, file, buffer, offset);
                Assert.True(n >= 0);
                if (n == 0) break;

                sb.Append(Encoding.UTF8.GetString(buffer, 0, n));
                offset += n;
            }

            return sb.ToString();
        }
        finally
        {
            file.Close();
        }
    }

    private static long ParseMemInfoKiB(string text, string field)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(l => l.StartsWith(field + ":", StringComparison.Ordinal));
        Assert.False(string.IsNullOrEmpty(line));
        var right = line!.Split(':', 2)[1].Trim();
        var number = right.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        Assert.True(long.TryParse(number, out var value));
        return value;
    }

    private static int WriteAll(FiberTask task, PathLocation loc, string text, long offset = 0)
    {
        Assert.True(loc.IsValid);
        var file = new LinuxFile(loc.Dentry!, FileFlags.O_WRONLY, loc.Mount!);
        try
        {
            BindTaskContext(file, task);
            return loc.Dentry!.Inode!.WriteFromHost(null, file, Encoding.UTF8.GetBytes(text), offset);
        }
        finally
        {
            file.Close();
        }
    }

    private static void MapUserPage(KernelRuntime runtime, uint addr)
    {
        runtime.Memory.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
            MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[test]", runtime.Engine);
        Assert.True(runtime.Memory.HandleFault(addr, true, runtime.Engine));
    }

    private static void WriteCString(KernelRuntime runtime, uint addr, string value)
    {
        Assert.True(runtime.Engine.CopyToUser(addr, Encoding.UTF8.GetBytes(value + '\0')));
    }

    private static KernelRuntime CreateTmpfsProcRuntime()
    {
        return KernelRuntime.BootstrapWithRoot(false, sys =>
        {
            var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
            var rootSb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "proc-test-root", null);
            var rootMount = new Mount(rootSb, rootSb.Root)
            {
                Source = "tmpfs",
                FsType = "tmpfs",
                Options = "rw"
            };
            sys.InitializeRoot(rootSb.Root, rootMount);
            sys.MountStandardProc();
        });
    }

    private static void EnsureDirectory(SyscallManager sm, string guestPath)
    {
        var loc = sm.PathWalkWithFlags(guestPath, LookupFlags.FollowSymlink);
        if (loc.IsValid)
            return;

        var parentPath = Path.GetDirectoryName(guestPath.Replace('\\', '/'))?.Replace('\\', '/');
        if (string.IsNullOrEmpty(parentPath))
            parentPath = "/";
        var name = Path.GetFileName(guestPath);
        var parent = sm.PathWalkWithFlags(parentPath, LookupFlags.FollowSymlink);
        Assert.True(parent.IsValid);
        var dentry = new Dentry(name, null, parent.Dentry, parent.Dentry!.SuperBlock);
        parent.Dentry.Inode!.Mkdir(dentry, 0x1FF, 0, 0);
        parent.Dentry.Children[name] = dentry;
    }

    private static void MountHostfsAt(SyscallManager sm, string hostPath, string guestPath)
    {
        sm.MountHostfs(hostPath, guestPath);
    }

    private static void MountSilkfsAt(SyscallManager sm, string silkRoot, string guestPath)
    {
        EnsureDirectory(sm, guestPath);
        var fsCtx = sm.BuildFsContextFromLegacyMount("silkfs", silkRoot, 0, null);
        Assert.Equal(0, sm.CreateDetachedMountFromFsContext(fsCtx, 0, out var mount));
        var target = sm.PathWalkWithFlags(guestPath, LookupFlags.FollowSymlink);
        Assert.True(target.IsValid);
        Assert.Equal(0, sm.AttachDetachedMount(mount!, target));
    }

    private static ValueTask<int> CallSyscall(SyscallManager syscallManager, string name, params object[] args)
    {
        var method = typeof(SyscallManager).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var parameters = new object[7];
        parameters[0] = syscallManager.CurrentSyscallEngine;
        for (var i = 0; i < args.Length; i++) parameters[i + 1] = args[i];
        for (var i = args.Length + 1; i < parameters.Length; i++) parameters[i] = 0u;
        return (ValueTask<int>)method!.Invoke(syscallManager, parameters)!;
    }

    private static FiberTask AttachTaskContext(KernelRuntime runtime, int uid, bool grantCapSysAdmin)
    {
        var scheduler = new KernelScheduler();
        var pid = uid == 0 ? 1 : uid + 1000;
        var process = new Process(pid, runtime.Memory, runtime.Syscalls)
        {
            UID = uid,
            GID = uid,
            EUID = uid,
            EGID = uid,
            SUID = uid,
            SGID = uid,
            FSUID = uid,
            FSGID = uid,
            PGID = pid,
            SID = pid
        };
        process.SetCapability(Process.CapabilitySysAdmin, grantCapSysAdmin, grantCapSysAdmin, false);
        scheduler.RegisterProcess(process);
        var task = new FiberTask(process.TGID, process, runtime.Engine, scheduler);
        runtime.Engine.Owner = task;
        return task;
    }

    private static Dentry? Lookup(FiberTask task, Dentry parent, string name)
    {
        return parent.Inode is IContextualDirectoryInode contextual
            ? contextual.Lookup(task, name)
            : parent.Inode?.Lookup(name);
    }

    private static string Readlink(FiberTask task, Dentry dentry)
    {
        if (dentry.Inode is IContextualSymlinkInode contextual)
            return contextual.Readlink(task);

        string? target = null;
        var rc = dentry.Inode?.Readlink(out target) ?? -(int)Errno.ENOENT;
        Assert.True(rc >= 0, $"Readlink failed with rc={rc}");
        return target ?? string.Empty;
    }

    private static void BindTaskContext(LinuxFile file, FiberTask task)
    {
        if (file.OpenedInode is ITaskContextBoundInode taskBoundInode)
            taskBoundInode.BindTaskContext(file, task);
    }

    private sealed class ProcTestContext : IDisposable
    {
        public ProcTestContext()
        {
            Engine = new Engine();
            Memory = new VMAManager();
            SyscallManager = new SyscallManager(Engine, Memory, 0);
            Scheduler = new KernelScheduler();
            Process = new Process(1, Memory, SyscallManager);
            Scheduler.RegisterProcess(Process);
            Task = new FiberTask(1, Process, Engine, Scheduler);
            Engine.Owner = Task;
            Scheduler.CurrentTask = Task;
        }

        public Engine Engine { get; }
        public VMAManager Memory { get; }
        public SyscallManager SyscallManager { get; }
        public KernelScheduler Scheduler { get; }
        public Process Process { get; }
        public FiberTask Task { get; }

        public void Dispose()
        {
            GC.KeepAlive(Task);
        }
    }
}