using System.Buffers.Binary;
using System.Reflection;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;
using GuestProcess = Fiberish.Core.Process;

namespace Fiberish.Tests.Syscalls;

internal sealed class SyscallTestEnv : IDisposable
{
    private readonly MemoryRuntimeContext _context = new();

    public SyscallTestEnv(Func<MemoryRuntimeContext, TmpfsSuperBlock>? createSuperBlock = null,
        GuestArchitecture? architecture = null)
    {
        Engine = new Engine(_context);
        if (architecture.HasValue) Engine.SelectArchitecture(architecture.Value);
        Mem = new VMAManager(_context);
        Sm = new SyscallManager(Engine, Mem, 0);
        Process = new GuestProcess(100, Mem, Sm);
        Scheduler = new KernelScheduler();
        Task = new FiberTask(100, Process, Engine, Scheduler);
        Engine.Owner = Task;
        Sb = createSuperBlock?.Invoke(_context) ?? (TmpfsSuperBlock)new Tmpfs(memoryContext: _context)
            .ReadSuper(new FileSystemType { Name = "tmpfs" }, 0, string.Empty, null);
        Sm.MountRoot(Sb);

        for (uint address = 0x10000; address <= 0x14000; address += LinuxConstants.PageSize)
        {
            Mem.Mmap(address, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "[test]", Engine);
            Mem.HandleFault(address, true, Engine);
        }
    }

    public Engine Engine { get; }
    public VMAManager Mem { get; }
    public SyscallManager Sm { get; }
    public GuestProcess Process { get; }
    public KernelScheduler Scheduler { get; }
    public FiberTask Task { get; }
    public TmpfsSuperBlock Sb { get; }

    public void PutString(uint address, string value)
    {
        Assert.True(Engine.CopyToUser(address, System.Text.Encoding.UTF8.GetBytes(value + "\0")));
    }

    public int Invoke(string methodName, uint a1 = 0, uint a2 = 0, uint a3 = 0, uint a4 = 0, uint a5 = 0,
        uint a6 = 0)
    {
        var method = typeof(SyscallManager).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return ((ValueTask<int>)method!.Invoke(Sm, [Engine, a1, a2, a3, a4, a5, a6])!).GetAwaiter().GetResult();
    }

    public InotifyInode Watch(Inode inode, uint mask, int ownerUid = 6100)
    {
        var watch = new InotifyInode(0, Sb, ownerUid);
        Assert.True(watch.AddWatch(mask, inode.Type == InodeType.Directory, inode.SuperBlock, inode.Ino, inode) > 0);
        return watch;
    }

    public uint[] ReadMasks(InotifyInode watch)
    {
        return ReadEvents(watch).Select(e => e.Mask).ToArray();
    }

    public (uint Mask, uint Cookie, byte[] Name)[] ReadEvents(InotifyInode watch)
    {
        var buffer = new byte[65536];
        var length = watch.ReadToHost(null, null!, buffer, 0);
        if (length <= 0)
            return [];

        var events = new List<(uint Mask, uint Cookie, byte[] Name)>();
        for (var offset = 0; offset < length;)
        {
            var mask = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 4, 4));
            var cookie = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 8, 4));
            var nameLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 12, 4));
            var rawName = buffer.AsSpan(offset + 16, nameLength);
            var nul = rawName.IndexOf((byte)0);
            events.Add((mask, cookie, (nul >= 0 ? rawName[..nul] : rawName).ToArray()));
            offset += 16 + nameLength;
        }

        return [.. events];
    }

    public void Dispose()
    {
        Sm.Close();
        Scheduler.Dispose();
        Engine.Dispose();
        _context.Dispose();
    }
}
