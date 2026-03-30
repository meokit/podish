using System.Text;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.Syscalls;

namespace Fiberish.VFS;

public class ProcFileSystem : FileSystem
{
    public ProcFileSystem(DeviceNumberManager? devManager = null) : base(devManager)
    {
        Name = "proc";
    }

    public override SuperBlock ReadSuper(FileSystemType fsType, int flags, string devName, object? data)
    {
        return new ProcSuperBlock(fsType, DevManager);
    }
}

public class ProcSuperBlock : SuperBlock, IDentryCacheDropper
{
    private ulong _nextIno = 1;

    public ProcSuperBlock(FileSystemType type, DeviceNumberManager? devManager = null) :
        base(devManager)
    {
        Type = type;

        var rootInode = new ProcRootInode(this);
        Root = new Dentry("/", rootInode, null, this);
        Root.Parent = Root;
    }

    public long DropDentryCache()
    {
        if (Root == null) return 0;

        long dropped = 0;
        var children = Root.Children.Values.ToList();
        foreach (var child in children)
        {
            if (child.IsMounted) continue;
            dropped += VfsShrinker.DetachCachedSubtree(child);
        }

        return dropped;
    }

    public override Inode AllocInode()
    {
        throw new NotSupportedException();
    }

    public ulong AllocateIno()
    {
        lock (Lock)
        {
            return _nextIno++;
        }
    }
}

file static class ProcContext
{
    public static ProcOpenContext FromTask(FiberTask task)
    {
        return new ProcOpenContext(task.CommonKernel, task, task.Process, task.Process.Syscalls);
    }
}

file sealed class ProcOpenContext
{
    public ProcOpenContext(KernelScheduler? scheduler, FiberTask? task, Process? process,
        SyscallManager? syscallManager)
    {
        Scheduler = scheduler;
        Task = task;
        Process = process;
        SyscallManager = syscallManager;
    }

    public KernelScheduler? Scheduler { get; }
    public FiberTask? Task { get; }
    public Process? Process { get; }
    public SyscallManager? SyscallManager { get; }
}

file sealed class ProcOpenData
{
    public ProcOpenData(ProcOpenContext context, byte[] content)
    {
        Context = context;
        Content = content;
    }

    public ProcOpenContext Context { get; }
    public byte[] Content { get; }
}

file sealed class ProcRootInode : Inode, IContextualDirectoryInode
{
    private readonly ProcSuperBlock _sb;

    public ProcRootInode(ProcSuperBlock sb)
    {
        _sb = sb;
        SuperBlock = sb;
        Ino = sb.AllocateIno();
        Type = InodeType.Directory;
        Mode = 0x16D; // 0555
        SetInitialLinkCount(2, "ProcRootInode.ctor");
        MTime = ATime = CTime = DateTime.UtcNow;
    }

    public Dentry? Lookup(FiberTask task, string name)
    {
        if (Dentries.Count == 0) return null;
        var root = Dentries[0];

        if (root.TryGetCachedChild(name, out var cached))
        {
            if (int.TryParse(name, out var cachedPid) && task.CommonKernel.GetProcess(cachedPid) == null)
                _ = root.TryUncacheChild(name, "ProcRootInode.Lookup.stale-pid", out _);
            else
                return cached;
        }

        var created = name switch
        {
            "mounts" => CreateFile(root, name, 0x124, ctx => ProcFsManager.GenerateMounts(ctx.SyscallManager)),
            "mountinfo" => CreateFile(root, name, 0x124, ctx => ProcFsManager.GenerateMountInfo(ctx.SyscallManager)),
            "cpuinfo" => CreateFile(root, name, 0x124, _ => ProcFsManager.GenerateCpuInfo()),
            "meminfo" => CreateFile(root, name, 0x124, ctx => ProcFsManager.GenerateMemInfo(ctx.SyscallManager)),
            "version" => CreateFile(root, name, 0x124, _ => ProcFsManager.GenerateVersion()),
            "stat" => CreateFile(root, name, 0x124, ctx => ProcFsManager.GenerateSystemStat(ctx.Scheduler)),
            "uptime" => CreateFile(root, name, 0x124, ctx => ProcFsManager.GenerateUptime(ctx.Scheduler)),
            "loadavg" => CreateFile(root, name, 0x124, ctx => ProcFsManager.GenerateLoadAvg(ctx.Scheduler)),
            "sys" => CreateSysDirectory(root, name),
            "self" => CreateSelfSymlink(root, name),
            _ => null
        };

        if (created != null)
        {
            root.CacheChild(created, "ProcRootInode.Lookup.static");
            return created;
        }

        if (!int.TryParse(name, out var pid)) return null;
        if (task.CommonKernel.GetProcess(pid) == null) return null;

        created = CreatePidDirectory(root, pid);
        root.CacheChild(created, "ProcRootInode.Lookup.pid");
        return created;
    }

    public bool RevalidateCachedChild(FiberTask task, Dentry parent, string name, Dentry cached)
    {
        // /proc/self must always reflect current task.
        if (name == "self") return false;

        // /proc/<pid> must track live task table.
        if (int.TryParse(name, out var pid))
            return task.CommonKernel.GetProcess(pid) != null;

        return true;
    }

    public List<DirectoryEntry> GetEntries(FiberTask task)
    {
        var entries = new List<DirectoryEntry>
        {
            new() { Name = ".", Ino = Ino, Type = InodeType.Directory },
            new() { Name = "..", Ino = Ino, Type = InodeType.Directory }
        };

        entries.Add(new DirectoryEntry { Name = "mounts", Ino = 0, Type = InodeType.File });
        entries.Add(new DirectoryEntry { Name = "mountinfo", Ino = 0, Type = InodeType.File });
        entries.Add(new DirectoryEntry { Name = "cpuinfo", Ino = 0, Type = InodeType.File });
        entries.Add(new DirectoryEntry { Name = "meminfo", Ino = 0, Type = InodeType.File });
        entries.Add(new DirectoryEntry { Name = "version", Ino = 0, Type = InodeType.File });
        entries.Add(new DirectoryEntry { Name = "stat", Ino = 0, Type = InodeType.File });
        entries.Add(new DirectoryEntry { Name = "uptime", Ino = 0, Type = InodeType.File });
        entries.Add(new DirectoryEntry { Name = "loadavg", Ino = 0, Type = InodeType.File });
        entries.Add(new DirectoryEntry { Name = "sys", Ino = 0, Type = InodeType.Directory });
        entries.Add(new DirectoryEntry { Name = "self", Ino = 0, Type = InodeType.Symlink });

        var processes = task.CommonKernel.GetProcessesSnapshot();
        foreach (var process in processes)
            entries.Add(new DirectoryEntry { Name = process.TGID.ToString(), Ino = 0, Type = InodeType.Directory });

        return entries;
    }

    public override Dentry? Lookup(string name)
    {
        return null;
    }

    public override bool RevalidateCachedChild(Dentry parent, string name, Dentry cached)
    {
        return false;
    }

    public override List<DirectoryEntry> GetEntries()
    {
        return
        [
            new DirectoryEntry { Name = ".", Ino = Ino, Type = InodeType.Directory },
            new DirectoryEntry { Name = "..", Ino = Ino, Type = InodeType.Directory },
            new DirectoryEntry { Name = "mounts", Ino = 0, Type = InodeType.File },
            new DirectoryEntry { Name = "mountinfo", Ino = 0, Type = InodeType.File },
            new DirectoryEntry { Name = "cpuinfo", Ino = 0, Type = InodeType.File },
            new DirectoryEntry { Name = "meminfo", Ino = 0, Type = InodeType.File },
            new DirectoryEntry { Name = "version", Ino = 0, Type = InodeType.File },
            new DirectoryEntry { Name = "stat", Ino = 0, Type = InodeType.File },
            new DirectoryEntry { Name = "uptime", Ino = 0, Type = InodeType.File },
            new DirectoryEntry { Name = "loadavg", Ino = 0, Type = InodeType.File },
            new DirectoryEntry { Name = "sys", Ino = 0, Type = InodeType.Directory },
            new DirectoryEntry { Name = "self", Ino = 0, Type = InodeType.Symlink }
        ];
    }

    private Dentry CreatePidDirectory(Dentry parent, int pid)
    {
        var inode = new ProcPidDirectoryInode(_sb, pid);
        return new Dentry(pid.ToString(), inode, parent, _sb);
    }

    private Dentry CreateSelfSymlink(Dentry parent, string name)
    {
        var inode = new ProcSelfSymlinkInode(_sb);
        return new Dentry(name, inode, parent, _sb);
    }

    private Dentry CreateSysDirectory(Dentry parent, string name)
    {
        var inode = new ProcSysRootInode(_sb);
        return new Dentry(name, inode, parent, _sb);
    }

    private Dentry CreateFile(Dentry parent, string name, int mode, Func<ProcOpenContext, string> contentFactory)
    {
        var inode = new ProcDynamicFileInode(_sb, mode, contentFactory);
        return new Dentry(name, inode, parent, _sb);
    }
}

file sealed class ProcPidDirectoryInode : Inode, IContextualDirectoryInode
{
    private readonly int _pid;
    private readonly ProcSuperBlock _sb;

    public ProcPidDirectoryInode(ProcSuperBlock sb, int pid)
    {
        _sb = sb;
        _pid = pid;
        SuperBlock = sb;
        Ino = sb.AllocateIno();
        Type = InodeType.Directory;
        Mode = 0x16D; // 0555
        SetInitialLinkCount(2, "ProcPidDirectoryInode.ctor");
        MTime = ATime = CTime = DateTime.UtcNow;
    }

    public Dentry? Lookup(FiberTask task, string name)
    {
        if (Dentries.Count == 0) return null;
        if (task.CommonKernel.GetProcess(_pid) == null) return null;

        var dir = Dentries[0];
        if (dir.TryGetCachedChild(name, out var cached))
            return cached;

        var created = name switch
        {
            "status" => CreateFile(dir, name, 0x124, _ =>
            {
                var p = task.CommonKernel.GetProcess(_pid);
                return p == null ? string.Empty : ProcFsManager.GenerateStatus(p);
            }),
            "cmdline" => CreateFile(dir, name, 0x124, _ =>
            {
                var p = task.CommonKernel.GetProcess(_pid);
                return p == null ? string.Empty : ProcFsManager.GenerateCmdline(p);
            }),
            "stat" => CreateFile(dir, name, 0x124, _ =>
            {
                var p = task.CommonKernel.GetProcess(_pid);
                return p == null ? string.Empty : ProcFsManager.GenerateStat(p);
            }),
            "mountinfo" => CreateFile(dir, name, 0x124, ctx => ProcFsManager.GenerateMountInfo(ctx.SyscallManager)),
            "fd" => CreateFdDir(dir, name),
            "fdinfo" => CreateFdInfoDir(dir, name),
            "exe" => CreateSymlink(dir, name, p => p.ExecutablePath),
            "cwd" => CreateSymlink(dir, name, p =>
            {
                var proc = p.Syscalls;
                return proc.GetAbsolutePath(proc.CurrentWorkingDirectory);
            }),
            "root" => CreateSymlink(dir, name, p =>
            {
                var proc = p.Syscalls;
                return proc.GetAbsolutePath(proc.ProcessRoot);
            }),
            _ => null
        };

        if (created != null)
            dir.CacheChild(created, "ProcPidDirectoryInode.Lookup");

        return created;
    }

    public bool RevalidateCachedChild(FiberTask task, Dentry parent, string name, Dentry cached)
    {
        // Whole /proc/<pid> subtree becomes invalid once process is gone.
        if (task.CommonKernel.GetProcess(_pid) == null) return false;

        // Keep fd/fdinfo hot paths fresh because child fd tables are highly dynamic.
        if (name is "fd" or "fdinfo") return false;

        return true;
    }

    public List<DirectoryEntry> GetEntries(FiberTask task)
    {
        var entries = new List<DirectoryEntry>
        {
            new() { Name = ".", Ino = Ino, Type = InodeType.Directory },
            new() { Name = "..", Ino = Ino, Type = InodeType.Directory }
        };

        if (task.CommonKernel.GetProcess(_pid) == null)
            return entries;

        entries.Add(new DirectoryEntry { Name = "status", Ino = 0, Type = InodeType.File });
        entries.Add(new DirectoryEntry { Name = "cmdline", Ino = 0, Type = InodeType.File });
        entries.Add(new DirectoryEntry { Name = "stat", Ino = 0, Type = InodeType.File });
        entries.Add(new DirectoryEntry { Name = "mountinfo", Ino = 0, Type = InodeType.File });
        entries.Add(new DirectoryEntry { Name = "fd", Ino = 0, Type = InodeType.Directory });
        entries.Add(new DirectoryEntry { Name = "fdinfo", Ino = 0, Type = InodeType.Directory });
        entries.Add(new DirectoryEntry { Name = "exe", Ino = 0, Type = InodeType.Symlink });
        entries.Add(new DirectoryEntry { Name = "cwd", Ino = 0, Type = InodeType.Symlink });
        entries.Add(new DirectoryEntry { Name = "root", Ino = 0, Type = InodeType.Symlink });
        return entries;
    }

    public override Dentry? Lookup(string name)
    {
        return null;
    }

    public override bool RevalidateCachedChild(Dentry parent, string name, Dentry cached)
    {
        return false;
    }

    public override List<DirectoryEntry> GetEntries()
    {
        return
        [
            new DirectoryEntry { Name = ".", Ino = Ino, Type = InodeType.Directory },
            new DirectoryEntry { Name = "..", Ino = Ino, Type = InodeType.Directory }
        ];
    }

    private Dentry CreateFile(Dentry parent, string name, int mode, Func<ProcOpenContext, string> contentFactory)
    {
        var inode = new ProcDynamicFileInode(_sb, mode, contentFactory);
        return new Dentry(name, inode, parent, _sb);
    }

    private Dentry CreateFdDir(Dentry parent, string name)
    {
        var inode = new ProcPidFdDirectoryInode(_sb, _pid);
        return new Dentry(name, inode, parent, _sb);
    }

    private Dentry CreateFdInfoDir(Dentry parent, string name)
    {
        var inode = new ProcPidFdInfoDirectoryInode(_sb, _pid);
        return new Dentry(name, inode, parent, _sb);
    }

    private Dentry CreateSymlink(Dentry parent, string name, Func<Process, string> resolver)
    {
        var inode = new ProcPidSymlinkInode(_sb, _pid, resolver);
        return new Dentry(name, inode, parent, _sb);
    }
}

file sealed class ProcPidSymlinkInode : Inode, IContextualSymlinkInode
{
    private readonly int _pid;
    private readonly ProcSuperBlock _sb;
    private readonly Func<Process, string> _targetResolver;

    public ProcPidSymlinkInode(ProcSuperBlock sb, int pid, Func<Process, string> targetResolver)
    {
        _sb = sb;
        _pid = pid;
        _targetResolver = targetResolver;
        SuperBlock = sb;
        Ino = sb.AllocateIno();
        Type = InodeType.Symlink;
        Mode = 0x1FF; // 0777
        SetInitialLinkCount(1, "ProcPidSymlinkInode.ctor");
        MTime = ATime = CTime = DateTime.UtcNow;
    }

    public string Readlink(FiberTask task)
    {
        var process = task.CommonKernel.GetProcess(_pid);
        if (process == null) return string.Empty;

        var target = _targetResolver(process);
        if (string.IsNullOrEmpty(target))
            return string.Empty;
        return target;
    }

    public override string Readlink()
    {
        return string.Empty;
    }
}

file sealed class ProcPidFdDirectoryInode : Inode, IContextualDirectoryInode
{
    private readonly int _pid;
    private readonly ProcSuperBlock _sb;

    public ProcPidFdDirectoryInode(ProcSuperBlock sb, int pid)
    {
        _sb = sb;
        _pid = pid;
        SuperBlock = sb;
        Ino = sb.AllocateIno();
        Type = InodeType.Directory;
        Mode = 0x16D; // 0555
        SetInitialLinkCount(2, "ProcPidFdDirectoryInode.ctor");
        MTime = ATime = CTime = DateTime.UtcNow;
    }

    public Dentry? Lookup(FiberTask task, string name)
    {
        if (Dentries.Count == 0) return null;
        var process = task.CommonKernel.GetProcess(_pid);
        if (process == null) return null;
        if (!int.TryParse(name, out var fd)) return null;
        if (!process.Syscalls.FDs.ContainsKey(fd))
            return null;

        var dir = Dentries[0];
        if (dir.TryGetCachedChild(name, out var cached))
            return cached;

        var inode = new ProcPidFdSymlinkInode(_sb, _pid, fd);
        var dentry = new Dentry(name, inode, dir, _sb);
        dir.CacheChild(dentry, "ProcPidFdDirectoryInode.Lookup");
        return dentry;
    }

    public bool RevalidateCachedChild(FiberTask task, Dentry parent, string name, Dentry cached)
    {
        var process = task.CommonKernel.GetProcess(_pid);
        if (process == null) return false;
        if (!int.TryParse(name, out var fd)) return false;
        return process.Syscalls.FDs.ContainsKey(fd);
    }

    public List<DirectoryEntry> GetEntries(FiberTask task)
    {
        var entries = new List<DirectoryEntry>
        {
            new() { Name = ".", Ino = Ino, Type = InodeType.Directory },
            new() { Name = "..", Ino = Ino, Type = InodeType.Directory }
        };

        var process = task.CommonKernel.GetProcess(_pid);
        if (process == null) return entries;

        foreach (var fd in process.Syscalls.FDs.Keys.OrderBy(k => k))
            entries.Add(new DirectoryEntry { Name = fd.ToString(), Ino = 0, Type = InodeType.Symlink });
        return entries;
    }

    public override Dentry? Lookup(string name)
    {
        return null;
    }

    public override bool RevalidateCachedChild(Dentry parent, string name, Dentry cached)
    {
        return false;
    }

    public override List<DirectoryEntry> GetEntries()
    {
        return
        [
            new DirectoryEntry { Name = ".", Ino = Ino, Type = InodeType.Directory },
            new DirectoryEntry { Name = "..", Ino = Ino, Type = InodeType.Directory }
        ];
    }
}

file sealed class ProcPidFdInfoDirectoryInode : Inode, IContextualDirectoryInode
{
    private readonly int _pid;
    private readonly ProcSuperBlock _sb;

    public ProcPidFdInfoDirectoryInode(ProcSuperBlock sb, int pid)
    {
        _sb = sb;
        _pid = pid;
        SuperBlock = sb;
        Ino = sb.AllocateIno();
        Type = InodeType.Directory;
        Mode = 0x16D; // 0555
        SetInitialLinkCount(2, "ProcPidFdInfoDirectoryInode.ctor");
        MTime = ATime = CTime = DateTime.UtcNow;
    }

    public Dentry? Lookup(FiberTask task, string name)
    {
        if (Dentries.Count == 0) return null;
        var process = task.CommonKernel.GetProcess(_pid);
        if (process == null) return null;
        if (!int.TryParse(name, out var fd)) return null;
        if (!process.Syscalls.FDs.ContainsKey(fd)) return null;

        var dir = Dentries[0];
        if (dir.TryGetCachedChild(name, out var cached))
            return cached;

        var inode = new ProcPidFdInfoFileInode(_sb, _pid, fd);
        var dentry = new Dentry(name, inode, dir, _sb);
        dir.CacheChild(dentry, "ProcPidFdInfoDirectoryInode.Lookup");
        return dentry;
    }

    public bool RevalidateCachedChild(FiberTask task, Dentry parent, string name, Dentry cached)
    {
        var process = task.CommonKernel.GetProcess(_pid);
        if (process == null) return false;
        if (!int.TryParse(name, out var fd)) return false;
        return process.Syscalls.FDs.ContainsKey(fd);
    }

    public List<DirectoryEntry> GetEntries(FiberTask task)
    {
        var entries = new List<DirectoryEntry>
        {
            new() { Name = ".", Ino = Ino, Type = InodeType.Directory },
            new() { Name = "..", Ino = Ino, Type = InodeType.Directory }
        };

        var process = task.CommonKernel.GetProcess(_pid);
        if (process == null) return entries;

        foreach (var fd in process.Syscalls.FDs.Keys.OrderBy(k => k))
            entries.Add(new DirectoryEntry { Name = fd.ToString(), Ino = 0, Type = InodeType.File });
        return entries;
    }

    public override Dentry? Lookup(string name)
    {
        return null;
    }

    public override bool RevalidateCachedChild(Dentry parent, string name, Dentry cached)
    {
        return false;
    }

    public override List<DirectoryEntry> GetEntries()
    {
        return
        [
            new DirectoryEntry { Name = ".", Ino = Ino, Type = InodeType.Directory },
            new DirectoryEntry { Name = "..", Ino = Ino, Type = InodeType.Directory }
        ];
    }
}

file sealed class ProcPidFdSymlinkInode : Inode, IMagicSymlinkInode, IContextualMagicSymlinkInode,
    IContextualSymlinkInode
{
    private readonly int _fd;
    private readonly int _pid;
    private readonly ProcSuperBlock _sb;

    public ProcPidFdSymlinkInode(ProcSuperBlock sb, int pid, int fd)
    {
        _sb = sb;
        _pid = pid;
        _fd = fd;
        SuperBlock = sb;
        Ino = sb.AllocateIno();
        Type = InodeType.Symlink;
        Mode = 0x1FF; // 0777
        SetInitialLinkCount(1, "ProcPidFdSymlinkInode.ctor");
        MTime = ATime = CTime = DateTime.UtcNow;
    }

    public bool TryResolveLink(FiberTask task, out LinuxFile file)
    {
        var process = task.CommonKernel.GetProcess(_pid);
        if (process == null)
        {
            file = null!;
            return false;
        }

        if (!process.Syscalls.FDs.TryGetValue(_fd, out var existing) || existing == null)
        {
            file = null!;
            return false;
        }

        file = existing;
        return true;
    }

    public string Readlink(FiberTask task)
    {
        var process = task.CommonKernel.GetProcess(_pid);
        if (process == null) return string.Empty;
        if (!process.Syscalls.FDs.TryGetValue(_fd, out var file)) return string.Empty;

        if (file.Mount?.FsType == "anon_inodefs")
            return $"anon_inode:{file.Dentry.Name}";

        var loc = new PathLocation(file.Dentry, file.Mount);
        return process.Syscalls.GetAbsolutePath(loc);
    }

    public bool TryResolveLink(out LinuxFile file)
    {
        file = null!;
        return false;
    }

    public override string Readlink()
    {
        return string.Empty;
    }
}

file sealed class ProcPidFdInfoFileInode : Inode, ITaskContextBoundInode
{
    private readonly int _fd;
    private readonly int _pid;
    private readonly ProcSuperBlock _sb;

    public ProcPidFdInfoFileInode(ProcSuperBlock sb, int pid, int fd)
    {
        _sb = sb;
        _pid = pid;
        _fd = fd;
        SuperBlock = sb;
        Ino = sb.AllocateIno();
        Type = InodeType.File;
        Mode = 0x124; // 0444
        SetInitialLinkCount(1, "ProcPidFdInfoFileInode.ctor");
        MTime = ATime = CTime = DateTime.UtcNow;
    }

    public void BindTaskContext(LinuxFile linuxFile, FiberTask task)
    {
        EnsureBoundContent(linuxFile, task);
    }

    public override void Open(LinuxFile linuxFile)
    {
    }

    protected internal override int ReadSpan(LinuxFile linuxFile, Span<byte> buffer, long offset)
    {
        var data = linuxFile.PrivateData as byte[] ?? [];
        if (offset < 0) return -(int)Errno.EINVAL;
        if (offset >= data.Length) return 0;

        var count = Math.Min(buffer.Length, data.Length - (int)offset);
        data.AsSpan((int)offset, count).CopyTo(buffer);
        return count;
    }

    private void EnsureBoundContent(LinuxFile linuxFile, FiberTask? task)
    {
        if (linuxFile.PrivateData is byte[])
            return;

        if (task == null)
            return;

        var process = task.CommonKernel.GetProcess(_pid);
        string text;
        if (process == null || !process.Syscalls.FDs.TryGetValue(_fd, out var file))
        {
            text = string.Empty;
        }
        else
        {
            var flags = Convert.ToString((int)file.Flags, 8) ?? "0";
            var mntId = file.Mount?.Id ?? 0;
            text = $"pos:\t{file.Position}\nflags:\t0{flags}\nmnt_id:\t{mntId}\n";
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        linuxFile.PrivateData = bytes;
        Size = (ulong)bytes.Length;
    }
}

file enum ProcSysKind
{
    Root,
    Kernel,
    Vm
}

file sealed class ProcSysRootInode : Inode
{
    private readonly ProcSysKind _kind;
    private readonly ProcSuperBlock _sb;

    public ProcSysRootInode(ProcSuperBlock sb, ProcSysKind kind = ProcSysKind.Root)
    {
        _sb = sb;
        _kind = kind;
        SuperBlock = sb;
        Ino = sb.AllocateIno();
        Type = InodeType.Directory;
        Mode = 0x16D; // 0555
        SetInitialLinkCount(2, "ProcSysRootInode.ctor");
        MTime = ATime = CTime = DateTime.UtcNow;
    }

    public override Dentry? Lookup(string name)
    {
        if (Dentries.Count == 0) return null;
        var dir = Dentries[0];

        if (dir.TryGetCachedChild(name, out var cached))
            return cached;

        var created = (_kind, name) switch
        {
            (ProcSysKind.Root, "kernel") => CreateDir(dir, name, ProcSysKind.Kernel),
            (ProcSysKind.Root, "vm") => CreateDir(dir, name, ProcSysKind.Vm),

            (ProcSysKind.Kernel, "hostname") => CreateFile(dir, name,
                ctx => ProcFsManager.GenerateSysKernelHostname(ctx.Process)),
            (ProcSysKind.Kernel, "osrelease") => CreateFile(dir, name,
                ctx => ProcFsManager.GenerateSysKernelOsRelease(ctx.Process)),
            (ProcSysKind.Kernel, "ostype") => CreateFile(dir, name,
                ctx => ProcFsManager.GenerateSysKernelOstype(ctx.Process)),
            (ProcSysKind.Kernel, "version") => CreateFile(dir, name,
                ctx => ProcFsManager.GenerateSysKernelVersion(ctx.Process)),

            (ProcSysKind.Vm, "overcommit_memory") => CreateFile(dir, name,
                _ => ProcFsManager.GenerateSysVmOvercommitMemory()),
            (ProcSysKind.Vm, "swappiness") => CreateFile(dir, name, _ => ProcFsManager.GenerateSysVmSwappiness()),
            (ProcSysKind.Vm, "drop_caches") => CreateFile(dir, name,
                _ => ProcFsManager.GenerateSysVmDropCaches(), 0x1A4, HandleDropCachesWrite),
            _ => null
        };

        if (created != null)
            dir.CacheChild(created, "ProcSysRootInode.Lookup");

        return created;
    }

    public override List<DirectoryEntry> GetEntries()
    {
        var entries = new List<DirectoryEntry>
        {
            new() { Name = ".", Ino = Ino, Type = InodeType.Directory },
            new() { Name = "..", Ino = Ino, Type = InodeType.Directory }
        };

        switch (_kind)
        {
            case ProcSysKind.Root:
                entries.Add(new DirectoryEntry { Name = "kernel", Ino = 0, Type = InodeType.Directory });
                entries.Add(new DirectoryEntry { Name = "vm", Ino = 0, Type = InodeType.Directory });
                break;
            case ProcSysKind.Kernel:
                entries.Add(new DirectoryEntry { Name = "hostname", Ino = 0, Type = InodeType.File });
                entries.Add(new DirectoryEntry { Name = "osrelease", Ino = 0, Type = InodeType.File });
                entries.Add(new DirectoryEntry { Name = "ostype", Ino = 0, Type = InodeType.File });
                entries.Add(new DirectoryEntry { Name = "version", Ino = 0, Type = InodeType.File });
                break;
            case ProcSysKind.Vm:
                entries.Add(new DirectoryEntry { Name = "overcommit_memory", Ino = 0, Type = InodeType.File });
                entries.Add(new DirectoryEntry { Name = "swappiness", Ino = 0, Type = InodeType.File });
                entries.Add(new DirectoryEntry { Name = "drop_caches", Ino = 0, Type = InodeType.File });
                break;
        }

        return entries;
    }

    private Dentry CreateDir(Dentry parent, string name, ProcSysKind kind)
    {
        var inode = new ProcSysRootInode(_sb, kind);
        return new Dentry(name, inode, parent, _sb);
    }

    private Dentry CreateFile(Dentry parent, string name, Func<ProcOpenContext, string> contentFactory,
        int mode = 0x124,
        ProcWriteHandler? writeHandler = null)
    {
        var inode = new ProcDynamicFileInode(_sb, mode, contentFactory, writeHandler);
        return new Dentry(name, inode, parent, _sb);
    }

    private static int HandleDropCachesWrite(ProcOpenContext context, ReadOnlySpan<byte> buffer, long offset)
    {
        if (offset != 0) return -(int)Errno.EINVAL;
        if (buffer.Length == 0) return -(int)Errno.EINVAL;

        var text = Encoding.UTF8.GetString(buffer).Trim();
        if (text.Length == 0) return -(int)Errno.EINVAL;
        if (!int.TryParse(text, out var mode) || mode < 0 || mode > 3) return -(int)Errno.EINVAL;

        // Linux requires CAP_SYS_ADMIN.
        if (context.Process == null || !context.Process.HasEffectiveCapability(Process.CapabilitySysAdmin))
            return -(int)Errno.EPERM;

        var shrinkMode = VfsShrinkMode.None;
        if ((mode & 0x1) != 0)
            shrinkMode |= VfsShrinkMode.PageCache;
        if ((mode & 0x2) != 0)
            shrinkMode |= VfsShrinkMode.DentryCache | VfsShrinkMode.InodeCache;

        _ = VfsShrinker.Shrink(context.SyscallManager, shrinkMode);

        return buffer.Length;
    }
}

file delegate int ProcWriteHandler(ProcOpenContext context, ReadOnlySpan<byte> buffer, long offset);

file sealed class ProcSelfSymlinkInode : Inode, IContextualSymlinkInode
{
    private readonly ProcSuperBlock _sb;

    public ProcSelfSymlinkInode(ProcSuperBlock sb)
    {
        _sb = sb;
        SuperBlock = sb;
        Ino = sb.AllocateIno();
        Type = InodeType.Symlink;
        Mode = 0x1FF; // 0777
        SetInitialLinkCount(1, "ProcSelfSymlinkInode.ctor");
        MTime = ATime = CTime = DateTime.UtcNow;
    }

    public string Readlink(FiberTask task)
    {
        return task.Process.TGID.ToString();
    }

    public override string Readlink()
    {
        return string.Empty;
    }

    protected internal override int WriteSpan(LinuxFile linuxFile, ReadOnlySpan<byte> buffer, long offset)
    {
        return -(int)Errno.EPERM;
    }
}

file sealed class ProcDynamicFileInode : Inode, ITaskContextBoundInode
{
    private readonly Func<ProcOpenContext, string> _contentFactory;
    private readonly ProcWriteHandler? _writeHandler;

    public ProcDynamicFileInode(ProcSuperBlock sb, int mode, Func<ProcOpenContext, string> contentFactory,
        ProcWriteHandler? writeHandler = null)
    {
        _contentFactory = contentFactory;
        _writeHandler = writeHandler;
        SuperBlock = sb;
        Ino = sb.AllocateIno();
        Type = InodeType.File;
        Mode = mode;
        SetInitialLinkCount(1, "ProcDynamicFileInode.ctor");
        MTime = ATime = CTime = DateTime.UtcNow;
    }

    public void BindTaskContext(LinuxFile linuxFile, FiberTask task)
    {
        BindContext(linuxFile, ProcContext.FromTask(task));
    }

    public override void Open(LinuxFile linuxFile)
    {
        ATime = DateTime.UtcNow;
    }

    protected internal override int ReadSpan(LinuxFile linuxFile, Span<byte> buffer, long offset)
    {
        var openData = linuxFile.PrivateData as ProcOpenData;
        if (openData == null) return 0;
        if (offset < 0) return -(int)Errno.EINVAL;
        if (offset >= openData.Content.Length) return 0;

        var count = Math.Min(buffer.Length, openData.Content.Length - (int)offset);
        openData.Content.AsSpan((int)offset, count).CopyTo(buffer);
        ATime = DateTime.UtcNow;
        return count;
    }

    protected internal override int WriteSpan(LinuxFile linuxFile, ReadOnlySpan<byte> buffer, long offset)
    {
        if (_writeHandler == null) return -(int)Errno.EPERM;

        var openData = linuxFile.PrivateData as ProcOpenData;
        var context = openData?.Context;
        if (context == null) return -(int)Errno.EIO;
        var rc = _writeHandler(context, buffer, offset);
        if (rc < 0) return rc;

        var refreshed = Encoding.UTF8.GetBytes(_contentFactory(context));
        linuxFile.PrivateData = new ProcOpenData(context, refreshed);
        Size = (ulong)refreshed.Length;
        MTime = CTime = DateTime.UtcNow;
        return rc;
    }

    private void BindContext(LinuxFile linuxFile, ProcOpenContext ctx)
    {
        var content = _contentFactory(ctx);
        var bytes = Encoding.UTF8.GetBytes(content);
        linuxFile.PrivateData = new ProcOpenData(ctx, bytes);
        Size = (ulong)bytes.Length;
    }

    public override int Truncate(long length)
    {
        if (_writeHandler != null && length == 0) return 0;
        return -(int)Errno.EPERM;
    }
}