using System.Reflection;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using GuestProcess = Fiberish.Core.Process;

namespace Podish.Benchmarks;

/// <summary>
///     A single guest runtime with one task and a tmpfs root. Benchmarks reuse it so
///     scope construction is not part of a measurement.
/// </summary>
internal sealed class GuestBenchmarkScope : IDisposable
{
    private readonly MemoryRuntimeContext _context = new();
    private readonly List<InotifyInode> _watches = [];
    private readonly Func<Engine, uint, uint, uint, uint, uint, uint, ValueTask<int>> _sysOpen;
    private readonly Func<Engine, uint, uint, uint, uint, uint, uint, ValueTask<int>> _sysUnlink;

    public GuestBenchmarkScope(int scratchPages = 4)
    {
        Engine = new Engine(_context);
        Mem = new VMAManager(_context);
        Syscalls = new SyscallManager(Engine, Mem, 0);
        Process = new GuestProcess(100, Mem, Syscalls);
        Scheduler = new KernelScheduler();
        Task = new FiberTask(100, Process, Engine, Scheduler);
        Engine.Owner = Task;
        Sb = (TmpfsSuperBlock)new Tmpfs(memoryContext: _context)
            .ReadSuper(new FileSystemType { Name = "tmpfs" }, 0, string.Empty, null);
        Syscalls.MountRoot(Sb);

        for (uint address = PathAddress; address < PathAddress + (uint)scratchPages * LinuxConstants.PageSize;
             address += LinuxConstants.PageSize)
        {
            Mem.Mmap(address, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[bench]", Engine);
            Mem.HandleFault(address, true, Engine);
        }

        _sysOpen = BindSyscall("SysOpen");
        _sysUnlink = BindSyscall("SysUnlink");
    }

    public const uint PathAddress = 0x10000;
    public Engine Engine { get; }
    public VMAManager Mem { get; }
    public SyscallManager Syscalls { get; }
    public GuestProcess Process { get; }
    public KernelScheduler Scheduler { get; }
    public FiberTask Task { get; }
    public TmpfsSuperBlock Sb { get; }

    public void PutString(uint address, string value)
    {
        if (!Engine.CopyToUser(address, System.Text.Encoding.UTF8.GetBytes(value + "\0")))
            throw new InvalidOperationException($"Could not write '{value}' at 0x{address:x}.");
    }

    public int Open(string guestPath, FileFlags flags = FileFlags.O_RDONLY)
    {
        PutString(PathAddress, guestPath);
        return OpenPrepared(flags);
    }

    /// <summary>Opens the path already written at <see cref="PathAddress" />, without allocating.</summary>
    public int OpenPrepared(FileFlags flags = FileFlags.O_RDONLY) =>
        _sysOpen(Engine, PathAddress, (uint)flags, 0, 0, 0, 0).GetAwaiter().GetResult();

    public int Unlink(string guestPath)
    {
        PutString(PathAddress, guestPath);
        return _sysUnlink(Engine, PathAddress, 0, 0, 0, 0, 0).GetAwaiter().GetResult();
    }

    public Inode InodeOf(string guestPath)
    {
        var loc = Syscalls.PathWalk(guestPath);
        return loc.Dentry?.Inode ?? throw new InvalidOperationException($"No inode for '{guestPath}'.");
    }

    public InotifyInode Watch(Inode inode, uint mask, int ownerUid = 7200)
    {
        var watch = new InotifyInode(0, inode.SuperBlock, ownerUid);
        if (watch.AddWatch(mask, inode.Type == InodeType.Directory, inode.SuperBlock, inode.Ino, inode) <= 0)
        {
            watch.Release(null!);
            throw new InvalidOperationException("Could not add benchmark inotify watch.");
        }

        _watches.Add(watch);
        return watch;
    }

    public static int Drain(InotifyInode watch, Span<byte> buffer)
    {
        var length = watch.ReadToHost(null, null!, buffer, 0);
        return length > 0 ? length : 0;
    }

    private Func<Engine, uint, uint, uint, uint, uint, uint, ValueTask<int>> BindSyscall(string name)
    {
        var method = typeof(SyscallManager).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)
                     ?? throw new MissingMethodException(nameof(SyscallManager), name);
        return method.CreateDelegate<Func<Engine, uint, uint, uint, uint, uint, uint, ValueTask<int>>>(Syscalls);
    }

    public void Dispose()
    {
        foreach (var watch in _watches)
            watch.Release(null!);
        _watches.Clear();
        Task.EndGuestCpuAccounting();
        Syscalls.Close();
        Scheduler.Dispose();
        Engine.Dispose();
        _context.Dispose();
    }
}
