using System.Text;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;

namespace Fiberish.VFS;

public class ProcFileSystem : FileSystem
{
    public ProcFileSystem(DeviceNumberManager? devManager = null, MemoryRuntimeContext? memoryContext = null)
        : base(devManager, memoryContext)
    {
        Name = "proc";
    }

    public override SuperBlock ReadSuper(FileSystemType fsType, int flags, string devName, object? data)
    {
        return new ProcSuperBlock(fsType, DevManager, MemoryContext);
    }
}

public class ProcSuperBlock : SuperBlock, IDentryCacheDropper
{
    private ulong _nextIno = 1;

    public ProcSuperBlock(FileSystemType type, DeviceNumberManager? devManager = null,
        MemoryRuntimeContext? memoryContext = null) :
        base(devManager, memoryContext ?? new MemoryRuntimeContext())
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

    public Dentry? Lookup(FiberTask task, ReadOnlySpan<byte> name)
    {
        if (Dentries.Count == 0) return null;
        var root = Dentries[0];

        if (root.TryGetCachedChild(name, out var cached))
        {
            if (FsEncoding.TryParseAsciiInt32(name, out var cachedPid) &&
                task.CommonKernel.GetProcess(cachedPid) == null)
                _ = root.TryUncacheChild(name, "ProcRootInode.Lookup.stale-pid", out _);
            else
                return cached;
        }

        var childName = FsName.FromBytes(name);
        Dentry? created = null;
        if (name.SequenceEqual("mounts"u8))
            created = CreateFile(root, childName, 0x124, ctx => ProcFsManager.GenerateMounts(ctx.SyscallManager));
        else if (name.SequenceEqual("mountinfo"u8))
            created = CreateFile(root, childName, 0x124, ctx => ProcFsManager.GenerateMountInfo(ctx.SyscallManager));
        else if (name.SequenceEqual("cpuinfo"u8))
            created = CreateFile(root, childName, 0x124, _ => ProcFsManager.GenerateCpuInfo());
        else if (name.SequenceEqual("meminfo"u8))
            created = CreateFile(root, childName, 0x124, ctx => ProcFsManager.GenerateMemInfo(ctx.SyscallManager));
        else if (name.SequenceEqual("version"u8))
            created = CreateFile(root, childName, 0x124, _ => ProcFsManager.GenerateVersion());
        else if (name.SequenceEqual("stat"u8))
            created = CreateFile(root, childName, 0x124, ctx => ProcFsManager.GenerateSystemStat(ctx.Scheduler));
        else if (name.SequenceEqual("uptime"u8))
            created = CreateFile(root, childName, 0x124, ctx => ProcFsManager.GenerateUptime(ctx.Scheduler));
        else if (name.SequenceEqual("loadavg"u8))
            created = CreateFile(root, childName, 0x124, ctx => ProcFsManager.GenerateLoadAvg(ctx.Scheduler));
        else if (name.SequenceEqual("sys"u8))
            created = CreateSysDirectory(root, childName);
        else if (name.SequenceEqual("self"u8))
            created = CreateSelfSymlink(root, childName);

        if (created != null)
        {
            root.CacheChild(created, "ProcRootInode.Lookup.static");
            return created;
        }

        if (!FsEncoding.TryParseAsciiInt32(name, out var pid)) return null;
        if (task.CommonKernel.GetProcess(pid) == null) return null;

        created = CreatePidDirectory(root, pid);
        root.CacheChild(created, "ProcRootInode.Lookup.pid");
        return created;
    }

    public bool RevalidateCachedChild(FiberTask task, Dentry parent, ReadOnlySpan<byte> name, Dentry cached)
    {
        if (!FsEncoding.IsValidUtf8(name))
            return false;
        // /proc/self must always reflect current task.
        if (name.SequenceEqual("self"u8)) return false;

        // /proc/<pid> must track live task table.
        if (FsEncoding.TryParseAsciiInt32(name, out var pid))
            return task.CommonKernel.GetProcess(pid) != null;

        return true;
    }

    public List<DirectoryEntry> GetEntries(FiberTask task)
    {
        var entries = new List<DirectoryEntry>
        {
            new() { Name = FsName.FromString("."), Ino = Ino, Type = InodeType.Directory },
            new() { Name = FsName.FromString(".."), Ino = Ino, Type = InodeType.Directory }
        };

        entries.Add(new DirectoryEntry { Name = FsName.FromString("mounts"), Ino = 0, Type = InodeType.File });
        entries.Add(new DirectoryEntry { Name = FsName.FromString("mountinfo"), Ino = 0, Type = InodeType.File });
        entries.Add(new DirectoryEntry { Name = FsName.FromString("cpuinfo"), Ino = 0, Type = InodeType.File });
        entries.Add(new DirectoryEntry { Name = FsName.FromString("meminfo"), Ino = 0, Type = InodeType.File });
        entries.Add(new DirectoryEntry { Name = FsName.FromString("version"), Ino = 0, Type = InodeType.File });
        entries.Add(new DirectoryEntry { Name = FsName.FromString("stat"), Ino = 0, Type = InodeType.File });
        entries.Add(new DirectoryEntry { Name = FsName.FromString("uptime"), Ino = 0, Type = InodeType.File });
        entries.Add(new DirectoryEntry { Name = FsName.FromString("loadavg"), Ino = 0, Type = InodeType.File });
        entries.Add(new DirectoryEntry { Name = FsName.FromString("sys"), Ino = 0, Type = InodeType.Directory });
        entries.Add(new DirectoryEntry { Name = FsName.FromString("self"), Ino = 0, Type = InodeType.Symlink });

        var processes = task.CommonKernel.GetProcessesSnapshot();
        foreach (var process in processes)
            entries.Add(new DirectoryEntry
                { Name = FsName.FromString(process.TGID.ToString()), Ino = 0, Type = InodeType.Directory });

        return entries;
    }

    public override Dentry? Lookup(string name)
    {
        return null;
    }

    public override bool RevalidateCachedChild(Dentry parent, ReadOnlySpan<byte> name, Dentry cached)
    {
        return false;
    }

    public override List<DirectoryEntry> GetEntries()
    {
        return
        [
            new DirectoryEntry { Name = FsName.FromString("."), Ino = Ino, Type = InodeType.Directory },
            new DirectoryEntry { Name = FsName.FromString(".."), Ino = Ino, Type = InodeType.Directory },
            new DirectoryEntry { Name = FsName.FromString("mounts"), Ino = 0, Type = InodeType.File },
            new DirectoryEntry { Name = FsName.FromString("mountinfo"), Ino = 0, Type = InodeType.File },
            new DirectoryEntry { Name = FsName.FromString("cpuinfo"), Ino = 0, Type = InodeType.File },
            new DirectoryEntry { Name = FsName.FromString("meminfo"), Ino = 0, Type = InodeType.File },
            new DirectoryEntry { Name = FsName.FromString("version"), Ino = 0, Type = InodeType.File },
            new DirectoryEntry { Name = FsName.FromString("stat"), Ino = 0, Type = InodeType.File },
            new DirectoryEntry { Name = FsName.FromString("uptime"), Ino = 0, Type = InodeType.File },
            new DirectoryEntry { Name = FsName.FromString("loadavg"), Ino = 0, Type = InodeType.File },
            new DirectoryEntry { Name = FsName.FromString("sys"), Ino = 0, Type = InodeType.Directory },
            new DirectoryEntry { Name = FsName.FromString("self"), Ino = 0, Type = InodeType.Symlink }
        ];
    }

    private Dentry CreatePidDirectory(Dentry parent, int pid)
    {
        var inode = new ProcPidDirectoryInode(_sb, pid);
        return new Dentry(pid.ToString(), inode, parent, _sb);
    }

    private Dentry CreateSelfSymlink(Dentry parent, FsName name)
    {
        var inode = new ProcSelfSymlinkInode(_sb);
        return new Dentry(name, inode, parent, _sb);
    }

    private Dentry CreateSysDirectory(Dentry parent, FsName name)
    {
        var inode = new ProcSysRootInode(_sb);
        return new Dentry(name, inode, parent, _sb);
    }

    private Dentry CreateFile(Dentry parent, FsName name, int mode, Func<ProcOpenContext, string> contentFactory)
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

    public Dentry? Lookup(FiberTask task, ReadOnlySpan<byte> name)
    {
        if (Dentries.Count == 0) return null;
        if (task.CommonKernel.GetProcess(_pid) == null) return null;

        var dir = Dentries[0];
        if (dir.TryGetCachedChild(name, out var cached))
            return cached;

        var childName = FsName.FromBytes(name);
        Dentry? created = null;
        if (name.SequenceEqual("status"u8))
            created = CreateFile(dir, childName, 0x124, _ =>
            {
                var p = task.CommonKernel.GetProcess(_pid);
                return p == null ? string.Empty : ProcFsManager.GenerateStatus(p);
            });
        else if (name.SequenceEqual("cmdline"u8))
            created = CreateRawFile(dir, childName, 0x124, _ =>
            {
                var p = task.CommonKernel.GetProcess(_pid);
                return p == null ? [] : p.CommandLineRaw;
            });
        else if (name.SequenceEqual("stat"u8))
            created = CreateFile(dir, childName, 0x124, _ =>
            {
                var p = task.CommonKernel.GetProcess(_pid);
                return p == null ? string.Empty : ProcFsManager.GenerateStat(p);
            });
        else if (name.SequenceEqual("mountinfo"u8))
            created = CreateFile(dir, childName, 0x124, ctx => ProcFsManager.GenerateMountInfo(ctx.SyscallManager));
        else if (name.SequenceEqual("fd"u8))
            created = CreateFdDir(dir, childName);
        else if (name.SequenceEqual("fdinfo"u8))
            created = CreateFdInfoDir(dir, childName);
        else if (name.SequenceEqual("exe"u8))
            created = CreateSymlink(dir, childName, p => p.ExecutablePathRaw);
        else if (name.SequenceEqual("cwd"u8))
            created = CreateSymlink(dir, childName, p =>
            {
                var proc = p.Syscalls;
                return proc.GetAbsolutePathBytes(proc.CurrentWorkingDirectory);
            });
        else if (name.SequenceEqual("root"u8))
            created = CreateSymlink(dir, childName, p =>
            {
                var proc = p.Syscalls;
                return proc.GetAbsolutePathBytes(proc.ProcessRoot);
            });

        if (created != null)
            dir.CacheChild(created, "ProcPidDirectoryInode.Lookup");

        return created;
    }

    public bool RevalidateCachedChild(FiberTask task, Dentry parent, ReadOnlySpan<byte> name, Dentry cached)
    {
        // Whole /proc/<pid> subtree becomes invalid once process is gone.
        if (task.CommonKernel.GetProcess(_pid) == null) return false;

        // Keep fd/fdinfo hot paths fresh because child fd tables are highly dynamic.
        if (!FsEncoding.IsValidUtf8(name)) return false;
        if (name.SequenceEqual("fd"u8) || name.SequenceEqual("fdinfo"u8)) return false;

        return true;
    }

    public List<DirectoryEntry> GetEntries(FiberTask task)
    {
        var entries = new List<DirectoryEntry>
        {
            new() { Name = FsName.FromString("."), Ino = Ino, Type = InodeType.Directory },
            new() { Name = FsName.FromString(".."), Ino = Ino, Type = InodeType.Directory }
        };

        if (task.CommonKernel.GetProcess(_pid) == null)
            return entries;

        entries.Add(new DirectoryEntry { Name = FsName.FromString("status"), Ino = 0, Type = InodeType.File });
        entries.Add(new DirectoryEntry { Name = FsName.FromString("cmdline"), Ino = 0, Type = InodeType.File });
        entries.Add(new DirectoryEntry { Name = FsName.FromString("stat"), Ino = 0, Type = InodeType.File });
        entries.Add(new DirectoryEntry { Name = FsName.FromString("mountinfo"), Ino = 0, Type = InodeType.File });
        entries.Add(new DirectoryEntry { Name = FsName.FromString("fd"), Ino = 0, Type = InodeType.Directory });
        entries.Add(new DirectoryEntry { Name = FsName.FromString("fdinfo"), Ino = 0, Type = InodeType.Directory });
        entries.Add(new DirectoryEntry { Name = FsName.FromString("exe"), Ino = 0, Type = InodeType.Symlink });
        entries.Add(new DirectoryEntry { Name = FsName.FromString("cwd"), Ino = 0, Type = InodeType.Symlink });
        entries.Add(new DirectoryEntry { Name = FsName.FromString("root"), Ino = 0, Type = InodeType.Symlink });
        return entries;
    }

    public override Dentry? Lookup(string name)
    {
        return null;
    }

    public override bool RevalidateCachedChild(Dentry parent, ReadOnlySpan<byte> name, Dentry cached)
    {
        return false;
    }

    public override List<DirectoryEntry> GetEntries()
    {
        return
        [
            new DirectoryEntry { Name = FsName.FromString("."), Ino = Ino, Type = InodeType.Directory },
            new DirectoryEntry { Name = FsName.FromString(".."), Ino = Ino, Type = InodeType.Directory }
        ];
    }

    private Dentry CreateFile(Dentry parent, FsName name, int mode, Func<ProcOpenContext, string> contentFactory)
    {
        var inode = new ProcDynamicFileInode(_sb, mode, contentFactory);
        return new Dentry(name, inode, parent, _sb);
    }

    private Dentry CreateRawFile(Dentry parent, FsName name, int mode, Func<ProcOpenContext, byte[]> contentFactory)
    {
        var inode = new ProcRawDynamicFileInode(_sb, mode, contentFactory);
        return new Dentry(name, inode, parent, _sb);
    }

    private Dentry CreateFdDir(Dentry parent, FsName name)
    {
        var inode = new ProcPidFdDirectoryInode(_sb, _pid);
        return new Dentry(name, inode, parent, _sb);
    }

    private Dentry CreateFdInfoDir(Dentry parent, FsName name)
    {
        var inode = new ProcPidFdInfoDirectoryInode(_sb, _pid);
        return new Dentry(name, inode, parent, _sb);
    }

    private Dentry CreateSymlink(Dentry parent, FsName name, Func<Process, byte[]> resolver)
    {
        var inode = new ProcPidSymlinkInode(_sb, _pid, resolver);
        return new Dentry(name, inode, parent, _sb);
    }
}

file sealed class ProcPidSymlinkInode : Inode, IContextualSymlinkInode
{
    private readonly int _pid;
    private readonly ProcSuperBlock _sb;
    private readonly Func<Process, byte[]> _targetResolver;

    public ProcPidSymlinkInode(ProcSuperBlock sb, int pid, Func<Process, byte[]> targetResolver)
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

    public byte[] Readlink(FiberTask task)
    {
        var process = task.CommonKernel.GetProcess(_pid);
        if (process == null) return [];

        var target = _targetResolver(process);
        if (target.Length == 0)
            return [];
        return target;
    }

    public override int Readlink(out string? target)
    {
        target = null;
        return -(int)Errno.ENOENT;
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

    public Dentry? Lookup(FiberTask task, ReadOnlySpan<byte> name)
    {
        if (Dentries.Count == 0) return null;
        var process = task.CommonKernel.GetProcess(_pid);
        if (process == null) return null;
        if (!FsEncoding.TryParseAsciiInt32(name, out var fd)) return null;
        if (!process.Syscalls.FDs.ContainsKey(fd))
            return null;

        var dir = Dentries[0];
        if (dir.TryGetCachedChild(name, out var cached))
            return cached;

        var inode = new ProcPidFdSymlinkInode(_sb, _pid, fd);
        var dentry = new Dentry(FsName.FromBytes(name), inode, dir, _sb);
        dir.CacheChild(dentry, "ProcPidFdDirectoryInode.Lookup");
        return dentry;
    }

    public bool RevalidateCachedChild(FiberTask task, Dentry parent, ReadOnlySpan<byte> name, Dentry cached)
    {
        var process = task.CommonKernel.GetProcess(_pid);
        if (process == null) return false;
        if (!FsEncoding.TryParseAsciiInt32(name, out var fd)) return false;
        return process.Syscalls.FDs.ContainsKey(fd);
    }

    public List<DirectoryEntry> GetEntries(FiberTask task)
    {
        var entries = new List<DirectoryEntry>
        {
            new() { Name = FsName.FromString("."), Ino = Ino, Type = InodeType.Directory },
            new() { Name = FsName.FromString(".."), Ino = Ino, Type = InodeType.Directory }
        };

        var process = task.CommonKernel.GetProcess(_pid);
        if (process == null) return entries;

        foreach (var fd in process.Syscalls.FDs.Keys.OrderBy(k => k))
            entries.Add(new DirectoryEntry
                { Name = FsName.FromString(fd.ToString()), Ino = 0, Type = InodeType.Symlink });
        return entries;
    }

    public override Dentry? Lookup(string name)
    {
        return null;
    }

    public override bool RevalidateCachedChild(Dentry parent, ReadOnlySpan<byte> name, Dentry cached)
    {
        return false;
    }

    public override List<DirectoryEntry> GetEntries()
    {
        return
        [
            new DirectoryEntry { Name = FsName.FromString("."), Ino = Ino, Type = InodeType.Directory },
            new DirectoryEntry { Name = FsName.FromString(".."), Ino = Ino, Type = InodeType.Directory }
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

    public Dentry? Lookup(FiberTask task, ReadOnlySpan<byte> name)
    {
        if (Dentries.Count == 0) return null;
        var process = task.CommonKernel.GetProcess(_pid);
        if (process == null) return null;
        if (!FsEncoding.TryParseAsciiInt32(name, out var fd)) return null;
        if (!process.Syscalls.FDs.ContainsKey(fd)) return null;

        var dir = Dentries[0];
        if (dir.TryGetCachedChild(name, out var cached))
            return cached;

        var inode = new ProcPidFdInfoFileInode(_sb, _pid, fd);
        var dentry = new Dentry(FsName.FromBytes(name), inode, dir, _sb);
        dir.CacheChild(dentry, "ProcPidFdInfoDirectoryInode.Lookup");
        return dentry;
    }

    public bool RevalidateCachedChild(FiberTask task, Dentry parent, ReadOnlySpan<byte> name, Dentry cached)
    {
        var process = task.CommonKernel.GetProcess(_pid);
        if (process == null) return false;
        if (!FsEncoding.TryParseAsciiInt32(name, out var fd)) return false;
        return process.Syscalls.FDs.ContainsKey(fd);
    }

    public List<DirectoryEntry> GetEntries(FiberTask task)
    {
        var entries = new List<DirectoryEntry>
        {
            new() { Name = FsName.FromString("."), Ino = Ino, Type = InodeType.Directory },
            new() { Name = FsName.FromString(".."), Ino = Ino, Type = InodeType.Directory }
        };

        var process = task.CommonKernel.GetProcess(_pid);
        if (process == null) return entries;

        foreach (var fd in process.Syscalls.FDs.Keys.OrderBy(k => k))
            entries.Add(new DirectoryEntry { Name = FsName.FromString(fd.ToString()), Ino = 0, Type = InodeType.File });
        return entries;
    }

    public override Dentry? Lookup(string name)
    {
        return null;
    }

    public override bool RevalidateCachedChild(Dentry parent, ReadOnlySpan<byte> name, Dentry cached)
    {
        return false;
    }

    public override List<DirectoryEntry> GetEntries()
    {
        return
        [
            new DirectoryEntry { Name = FsName.FromString("."), Ino = Ino, Type = InodeType.Directory },
            new DirectoryEntry { Name = FsName.FromString(".."), Ino = Ino, Type = InodeType.Directory }
        ];
    }
}

file sealed class ProcPidFdSymlinkInode : Inode, IMagicSymlinkInode, IContextualMagicSymlinkInode,
    IContextualSymlinkInode
{
    private static readonly byte[] DeletedSuffixBytes = " (deleted)"u8.ToArray();
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

    public bool TryResolveLink(FiberTask task, out PathLocation path)
    {
        var process = task.CommonKernel.GetProcess(_pid);
        if (process == null)
        {
            path = PathLocation.None;
            return false;
        }

        if (!process.Syscalls.FDs.TryGetValue(_fd, out var existing) || existing == null)
        {
            path = PathLocation.None;
            return false;
        }

        if (!existing.LivePath.IsValid || existing.LivePath.Dentry!.Inode == null)
        {
            path = PathLocation.None;
            return false;
        }

        path = existing.LivePath;
        return true;
    }

    public byte[] Readlink(FiberTask task)
    {
        var process = task.CommonKernel.GetProcess(_pid);
        if (process == null) return [];
        if (!process.Syscalls.FDs.TryGetValue(_fd, out var file)) return [];

        if (file.Mount?.FsType == "anon_inodefs" && file.OpenedInode is not TmpfsInode { IsMemfd: true })
            return FsEncoding.EncodeUtf8($"anon_inode:{file.Dentry.Name.ToDebugString()}");

        if (file.OpenedInode is TmpfsInode { IsMemfd: true } memfdInode)
            return FsEncoding.EncodeUtf8($"/memfd:{memfdInode.MemfdDisplayName}");

        var path = process.Syscalls.GetAbsolutePathBytes(file.LivePath);
        if (!file.LivePath.Dentry!.IsHashed && file.OpenedInode is not TmpfsInode { IsMemfd: true })
        {
            var result = new byte[path.Length + DeletedSuffixBytes.Length];
            path.CopyTo(result, 0);
            DeletedSuffixBytes.CopyTo(result, path.Length);
            return result;
        }

        return path;
    }

    public bool TryResolveLink(out PathLocation path)
    {
        path = PathLocation.None;
        return false;
    }

    public override int Readlink(out string? target)
    {
        target = null;
        return -(int)Errno.ENOENT;
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

    public override int ReadToHost(FiberTask? task, LinuxFile linuxFile, Span<byte> buffer,
        long offset)
    {
        _ = task;
        var data = linuxFile.PrivateData as byte[] ?? [];
        if (offset < 0) return -(int)Errno.EINVAL;
        if (offset >= data.Length) return 0;

        var count = Math.Min(buffer.Length, data.Length - (int)offset);
        data.AsSpan((int)offset, count).CopyTo(buffer);
        return count;
    }

    public override ValueTask<int> ReadV(Engine engine, LinuxFile file, FiberTask? task,
        ArraySegment<Iovec> iovs, long offset, int flags)
    {
        return ReadVViaHostBuffer(engine, file, task, iovs, offset, flags);
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
        return FsEncoding.TryEncodeUtf8(name, out var encoded) ? Lookup(encoded) : null;
    }

    public override Dentry? Lookup(ReadOnlySpan<byte> name)
    {
        if (Dentries.Count == 0) return null;
        var dir = Dentries[0];

        if (dir.TryGetCachedChild(name, out var cached))
            return cached;

        var childName = FsName.FromBytes(name);
        Dentry? created = null;
        if (_kind == ProcSysKind.Root && name.SequenceEqual("kernel"u8))
            created = CreateDir(dir, childName, ProcSysKind.Kernel);
        else if (_kind == ProcSysKind.Root && name.SequenceEqual("vm"u8))
            created = CreateDir(dir, childName, ProcSysKind.Vm);
        else if (_kind == ProcSysKind.Kernel && name.SequenceEqual("hostname"u8))
            created = CreateFile(dir, childName, ctx => ProcFsManager.GenerateSysKernelHostname(ctx.Process));
        else if (_kind == ProcSysKind.Kernel && name.SequenceEqual("osrelease"u8))
            created = CreateFile(dir, childName, ctx => ProcFsManager.GenerateSysKernelOsRelease(ctx.Process));
        else if (_kind == ProcSysKind.Kernel && name.SequenceEqual("ostype"u8))
            created = CreateFile(dir, childName, ctx => ProcFsManager.GenerateSysKernelOstype(ctx.Process));
        else if (_kind == ProcSysKind.Kernel && name.SequenceEqual("version"u8))
            created = CreateFile(dir, childName, ctx => ProcFsManager.GenerateSysKernelVersion(ctx.Process));
        else if (_kind == ProcSysKind.Vm && name.SequenceEqual("overcommit_memory"u8))
            created = CreateFile(dir, childName, _ => ProcFsManager.GenerateSysVmOvercommitMemory());
        else if (_kind == ProcSysKind.Vm && name.SequenceEqual("swappiness"u8))
            created = CreateFile(dir, childName, _ => ProcFsManager.GenerateSysVmSwappiness());
        else if (_kind == ProcSysKind.Vm && name.SequenceEqual("drop_caches"u8))
            created = CreateFile(dir, childName, _ => ProcFsManager.GenerateSysVmDropCaches(), 0x1A4,
                HandleDropCachesWrite);

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

    private Dentry CreateDir(Dentry parent, FsName name, ProcSysKind kind)
    {
        var inode = new ProcSysRootInode(_sb, kind);
        return new Dentry(name, inode, parent, _sb);
    }

    private Dentry CreateFile(Dentry parent, FsName name, Func<ProcOpenContext, string> contentFactory,
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

    public byte[] Readlink(FiberTask task)
    {
        return FsEncoding.EncodeUtf8(task.Process.TGID.ToString());
    }

    public override int Readlink(out string? target)
    {
        target = null;
        return -(int)Errno.ENOENT;
    }

    public override int WriteFromHost(FiberTask? task, LinuxFile linuxFile,
        ReadOnlySpan<byte> buffer, long offset)
    {
        _ = task;
        return -(int)Errno.EPERM;
    }

    public override ValueTask<int> WriteV(Engine engine, LinuxFile file, FiberTask? task,
        ArraySegment<Iovec> iovs, long offset, int flags)
    {
        return WriteVViaHostBuffer(engine, file, task, iovs, offset, flags);
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

    public override int ReadToHost(FiberTask? task, LinuxFile linuxFile, Span<byte> buffer,
        long offset)
    {
        _ = task;
        var openData = linuxFile.PrivateData as ProcOpenData;
        if (openData == null) return 0;
        if (offset < 0) return -(int)Errno.EINVAL;
        if (offset >= openData.Content.Length) return 0;

        var count = Math.Min(buffer.Length, openData.Content.Length - (int)offset);
        openData.Content.AsSpan((int)offset, count).CopyTo(buffer);
        ATime = DateTime.UtcNow;
        return count;
    }

    public override int WriteFromHost(FiberTask? task, LinuxFile linuxFile,
        ReadOnlySpan<byte> buffer, long offset)
    {
        _ = task;
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

    public override ValueTask<int> ReadV(Engine engine, LinuxFile file, FiberTask? task,
        ArraySegment<Iovec> iovs, long offset, int flags)
    {
        return ReadVViaHostBuffer(engine, file, task, iovs, offset, flags);
    }

    public override ValueTask<int> WriteV(Engine engine, LinuxFile file, FiberTask? task,
        ArraySegment<Iovec> iovs, long offset, int flags)
    {
        return WriteVViaHostBuffer(engine, file, task, iovs, offset, flags);
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

file sealed class ProcRawDynamicFileInode : Inode, ITaskContextBoundInode
{
    private readonly Func<ProcOpenContext, byte[]> _contentFactory;

    public ProcRawDynamicFileInode(ProcSuperBlock sb, int mode, Func<ProcOpenContext, byte[]> contentFactory)
    {
        _contentFactory = contentFactory;
        SuperBlock = sb;
        Ino = sb.AllocateIno();
        Type = InodeType.File;
        Mode = mode;
        SetInitialLinkCount(1, "ProcRawDynamicFileInode.ctor");
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

    public override int ReadToHost(FiberTask? task, LinuxFile linuxFile, Span<byte> buffer,
        long offset)
    {
        _ = task;
        var openData = linuxFile.PrivateData as ProcOpenData;
        if (openData == null) return 0;
        if (offset < 0) return -(int)Errno.EINVAL;
        if (offset >= openData.Content.Length) return 0;

        var count = Math.Min(buffer.Length, openData.Content.Length - (int)offset);
        openData.Content.AsSpan((int)offset, count).CopyTo(buffer);
        ATime = DateTime.UtcNow;
        return count;
    }

    public override ValueTask<int> ReadV(Engine engine, LinuxFile file, FiberTask? task,
        ArraySegment<Iovec> iovs, long offset, int flags)
    {
        return ReadVViaHostBuffer(engine, file, task, iovs, offset, flags);
    }

    private void BindContext(LinuxFile linuxFile, ProcOpenContext ctx)
    {
        var content = _contentFactory(ctx);
        linuxFile.PrivateData = new ProcOpenData(ctx, content);
        Size = (ulong)content.Length;
    }
}