using System.Text;
using System.Reflection;
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
            var runtime = KernelRuntime.Bootstrap(rootDir, strace: false, useOverlay: false);
            var sm = runtime.Syscalls;

            var mountsBefore = ReadAll(sm.PathWalk("/proc/mounts"));
            Assert.DoesNotContain(" /tests hostfs ", mountsBefore);

            sm.MountHostfs(hostMountDir, "/tests");

            var mountsAfter = ReadAll(sm.PathWalk("/proc/mounts"));
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
        var scheduler = new KernelScheduler();
        var previous = KernelScheduler.Current;
        KernelScheduler.Current = scheduler;

        try
        {
            var process = new Process(4242, null!, null!)
            {
                PPID = 1,
                PGID = 4242,
                SID = 4242,
                State = ProcessState.Running
            };
            scheduler.RegisterProcess(process);

            var fs = new ProcFileSystem();
            var sb = (ProcSuperBlock)fs.ReadSuper(new FileSystemType { Name = "proc" }, 0, "proc", null);
            var mount = new Mount(sb, sb.Root)
            {
                Source = "proc",
                FsType = "proc",
                Options = "rw,relatime"
            };

            var pidDir = sb.Root.Inode!.Lookup("4242");
            Assert.NotNull(pidDir);
            Assert.Equal(InodeType.Directory, pidDir!.Inode!.Type);

            var status = pidDir.Inode.Lookup("status");
            var stat = pidDir.Inode.Lookup("stat");
            var cmdline = pidDir.Inode.Lookup("cmdline");
            Assert.NotNull(status);
            Assert.NotNull(stat);
            Assert.NotNull(cmdline);

            var statusText = ReadAll(status!, mount);
            var statText = ReadAll(stat!, mount);
            var cmdlineText = ReadAll(cmdline!, mount);

            Assert.Contains("Pid:\t4242", statusText);
            Assert.StartsWith("4242 (process) R 1 4242 4242", statText);
            Assert.Equal(string.Empty, cmdlineText);
        }
        finally
        {
            KernelScheduler.Current = previous;
        }
    }

    [Fact]
    public void ProcSystemFiles_ShouldExposeStatUptimeLoadavgAndSysctl()
    {
        var scheduler = new KernelScheduler();
        var previous = KernelScheduler.Current;
        KernelScheduler.Current = scheduler;

        try
        {
            var process = new Process(1001, null!, null!);
            scheduler.RegisterProcess(process);

            var fs = new ProcFileSystem();
            var sb = (ProcSuperBlock)fs.ReadSuper(new FileSystemType { Name = "proc" }, 0, "proc", null);
            var mount = new Mount(sb, sb.Root)
            {
                Source = "proc",
                FsType = "proc",
                Options = "rw,relatime"
            };

            Assert.Contains("btime ", ReadAll(sb.Root.Inode!.Lookup("stat")!, mount));
            Assert.Contains("/", ReadAll(sb.Root.Inode!.Lookup("loadavg")!, mount));
            Assert.Contains(" ", ReadAll(sb.Root.Inode!.Lookup("uptime")!, mount));

            var sys = sb.Root.Inode.Lookup("sys");
            Assert.NotNull(sys);
            var kernel = sys!.Inode!.Lookup("kernel");
            Assert.NotNull(kernel);
            Assert.Equal("x86emu\n", ReadAll(kernel!.Inode!.Lookup("hostname")!, mount));
        }
        finally
        {
            KernelScheduler.Current = previous;
        }
    }

    [Fact]
    public void ProcPidLookup_ShouldDisappearAfterReap()
    {
        var scheduler = new KernelScheduler();
        var previous = KernelScheduler.Current;
        KernelScheduler.Current = scheduler;

        try
        {
            var child = new Process(5555, null!, null!) { State = ProcessState.Zombie };
            scheduler.RegisterProcess(child);

            var fs = new ProcFileSystem();
            var sb = (ProcSuperBlock)fs.ReadSuper(new FileSystemType { Name = "proc" }, 0, "proc", null);

            var first = sb.Root.Inode!.Lookup("5555");
            Assert.NotNull(first);

            scheduler.UnregisterProcess(5555);

            var second = sb.Root.Inode!.Lookup("5555");
            Assert.Null(second);
        }
        finally
        {
            KernelScheduler.Current = previous;
        }
    }

    [Fact]
    public void ProcPidSymlinksAndFd_ShouldExposeExeCwdRootFdAndFdinfo()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(rootDir);

        var scheduler = new KernelScheduler();
        var previous = KernelScheduler.Current;
        KernelScheduler.Current = scheduler;

        try
        {
            var runtime = KernelRuntime.Bootstrap(rootDir, strace: false, useOverlay: false);
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

            scheduler.RegisterProcess(process);

            var fs = new ProcFileSystem();
            var sb = (ProcSuperBlock)fs.ReadSuper(new FileSystemType { Name = "proc" }, 0, "proc", runtime.Syscalls);
            var mount = new Mount(sb, sb.Root)
            {
                Source = "proc",
                FsType = "proc",
                Options = "rw,relatime"
            };

            var pidDir = sb.Root.Inode!.Lookup("7777");
            Assert.NotNull(pidDir);

            var exe = pidDir!.Inode!.Lookup("exe");
            var cwd = pidDir.Inode.Lookup("cwd");
            var root = pidDir.Inode.Lookup("root");
            Assert.NotNull(exe);
            Assert.NotNull(cwd);
            Assert.NotNull(root);
            Assert.Equal("/bin/test-app", exe!.Inode!.Readlink());
            Assert.Equal("/", cwd!.Inode!.Readlink());
            Assert.Equal("/", root!.Inode!.Readlink());

            var fdDir = pidDir.Inode.Lookup("fd");
            var fdInfoDir = pidDir.Inode.Lookup("fdinfo");
            Assert.NotNull(fdDir);
            Assert.NotNull(fdInfoDir);

            var fd0 = fdDir!.Inode!.Lookup("0");
            Assert.NotNull(fd0);
            var fd0Target = fd0!.Inode!.Readlink();
            Assert.False(string.IsNullOrWhiteSpace(fd0Target));

            var fdInfo0 = fdInfoDir!.Inode!.Lookup("0");
            Assert.NotNull(fdInfo0);
            var infoText = ReadAll(fdInfo0!, mount);
            Assert.Contains("pos:\t", infoText);
            Assert.Contains("flags:\t0", infoText);
            Assert.Contains("mnt_id:\t", infoText);
        }
        finally
        {
            KernelScheduler.Current = previous;
            if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true);
        }
    }

    [Fact]
    public void ProcMemInfo_ShouldUseLiveMemoryStats()
    {
        var oldQuota = ExternalPageManager.MemoryQuotaBytes;
        IntPtr allocated = IntPtr.Zero;
        ExternalPageManager.MemoryQuotaBytes = 64L * 1024 * 1024;
        try
        {
            Assert.True(ExternalPageManager.TryAllocateExternalPageStrict(out allocated, AllocationClass.Anonymous));
            var text = ProcFsManager.GenerateMemInfo(null);

            var total = ParseMemInfoKiB(text, "MemTotal");
            var free = ParseMemInfoKiB(text, "MemFree");
            var available = ParseMemInfoKiB(text, "MemAvailable");
            var mapped = ParseMemInfoKiB(text, "Mapped");
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
            if (allocated != IntPtr.Zero) ExternalPageManager.ReleasePtr(allocated);
            ExternalPageManager.MemoryQuotaBytes = oldQuota;
        }
    }

    [Fact]
    public void ProcSysVmDropCaches_WriteShouldReclaimPageCache()
    {
        using var cacheScope = GlobalPageCacheManager.BeginIsolatedScope();
        var rootDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(rootDir);
        MemoryObject? cache = null;

        try
        {
            var runtime = KernelRuntime.Bootstrap(rootDir, strace: false, useOverlay: false);
            var sm = runtime.Syscalls;
            AttachTaskContext(runtime, uid: 0, grantCapSysAdmin: true);
            var loc = sm.PathWalk("/proc/sys/vm/drop_caches");
            Assert.True(loc.IsValid);
            Assert.Equal("0\n", ReadAll(loc));

            cache = new MemoryObject(MemoryObjectKind.File, null, 0, 0, true);
            GlobalPageCacheManager.TrackPageCache(cache, GlobalPageCacheManager.PageCacheClass.File);
            var page = cache.GetOrCreatePage(0, _ => true, out _, strictQuota: true, AllocationClass.PageCache);
            Assert.NotEqual(IntPtr.Zero, page);
            Assert.True(cache.PageCount > 0);

            Assert.Equal(-(int)Errno.EINVAL, WriteAll(loc, "9\n"));
            Assert.Equal(2, WriteAll(loc, "1\n"));
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
            var runtime = KernelRuntime.Bootstrap(rootDir, strace: false, useOverlay: false);
            var sm = runtime.Syscalls;
            AttachTaskContext(runtime, uid: 0, grantCapSysAdmin: true);

            var dropLoc = sm.PathWalk("/proc/sys/vm/drop_caches");
            Assert.True(dropLoc.IsValid);

            // Warm proc dentry caches.
            var procRoot = sm.PathWalk("/proc");
            Assert.True(procRoot.IsValid);
            Assert.NotNull(procRoot.Dentry!.Inode!.Lookup("sys"));
            Assert.True(procRoot.Dentry.Children.ContainsKey("sys"));
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

            Assert.Equal(2, WriteAll(dropLoc, "2\n"));

            Assert.False(procRoot.Dentry.Children.ContainsKey("sys"));
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
        using var cacheScope = GlobalPageCacheManager.BeginIsolatedScope();
        var rootDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(rootDir);
        MemoryObject? cache = null;

        try
        {
            var runtime = KernelRuntime.Bootstrap(rootDir, strace: false, useOverlay: false);
            var sm = runtime.Syscalls;
            AttachTaskContext(runtime, uid: 0, grantCapSysAdmin: true);

            var dropLoc = sm.PathWalk("/proc/sys/vm/drop_caches");
            Assert.True(dropLoc.IsValid);

            var procRoot = sm.PathWalk("/proc");
            Assert.True(procRoot.IsValid);
            Assert.NotNull(procRoot.Dentry!.Inode!.Lookup("sys"));
            Assert.True(procRoot.Dentry.TryGetCachedChild("sys", out _));

            cache = new MemoryObject(MemoryObjectKind.File, null, 0, 0, true);
            GlobalPageCacheManager.TrackPageCache(cache, GlobalPageCacheManager.PageCacheClass.File);
            var page = cache.GetOrCreatePage(0, _ => true, out _, strictQuota: true, AllocationClass.PageCache);
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

            Assert.Equal(2, WriteAll(dropLoc, "3\n"));

            Assert.Equal(0, cache.PageCount);
            Assert.False(procRoot.Dentry.TryGetCachedChild("sys", out _));
        }
        finally
        {
            cache?.Release();
            if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true);
        }
    }

    [Fact]
    public void ProcSysVmDropCaches_Mode2_ShouldNotBreakTmpfsNamespaceData()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(rootDir);

        try
        {
            var runtime = KernelRuntime.Bootstrap(rootDir, strace: false, useOverlay: false);
            var sm = runtime.Syscalls;
            AttachTaskContext(runtime, uid: 0, grantCapSysAdmin: true);

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
            Assert.Equal(payload.Length, fileDentry.Inode!.Write(file, payload, 0));
            file.Close();

            Assert.Equal(2, WriteAll(dropLoc, "2\n"));

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
    public void ProcSysVmDropCaches_WriteWithoutCapSysAdmin_ReturnsEperm()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(rootDir);

        try
        {
            var runtime = KernelRuntime.Bootstrap(rootDir, strace: false, useOverlay: false);
            var sm = runtime.Syscalls;
            AttachTaskContext(runtime, uid: 1000, grantCapSysAdmin: false);

            var loc = sm.PathWalk("/proc/sys/vm/drop_caches");
            Assert.True(loc.IsValid);
            Assert.Equal(-(int)Errno.EPERM, WriteAll(loc, "1\n"));
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
            var runtime = KernelRuntime.Bootstrap(rootDir, strace: false, useOverlay: false);
            var sm = runtime.Syscalls;
            AttachTaskContext(runtime, uid: 1000, grantCapSysAdmin: true);

            var loc = sm.PathWalk("/proc/sys/vm/drop_caches");
            Assert.True(loc.IsValid);
            Assert.Equal(2, WriteAll(loc, "1\n"));
        }
        finally
        {
            if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true);
        }
    }

    private static string ReadAll(PathLocation loc)
    {
        Assert.True(loc.IsValid);
        return ReadAll(loc.Dentry!, loc.Mount!);
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
                var n = dentry.Inode!.Read(file, buffer, offset);
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

    private static int WriteAll(PathLocation loc, string text, long offset = 0)
    {
        Assert.True(loc.IsValid);
        var file = new LinuxFile(loc.Dentry!, FileFlags.O_WRONLY, loc.Mount!);
        try
        {
            return loc.Dentry!.Inode!.Write(file, Encoding.UTF8.GetBytes(text), offset);
        }
        finally
        {
            file.Close();
        }
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
        process.SetCapability(Process.CapabilitySysAdmin, grantCapSysAdmin, grantCapSysAdmin, inheritable: false);
        scheduler.RegisterProcess(process);
        var task = new FiberTask(process.TGID, process, runtime.Engine, scheduler);
        runtime.Engine.Owner = task;
        return task;
    }
}
