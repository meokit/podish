using System.Text;
using System.Reflection;
using Fiberish.Core;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.VFS;

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
}
