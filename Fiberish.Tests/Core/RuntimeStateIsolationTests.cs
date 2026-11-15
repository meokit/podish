using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Core;

/// <summary>
///     Guest-runtime syscalls must not share quotas, registries or sysctl values with
///     other runtimes hosted in the same process.
/// </summary>
public class RuntimeStateIsolationTests
{
    [Fact]
    public void InotifyInstanceQuota_IsPerRuntime()
    {
        using var first = new MemoryRuntimeContext();
        using var second = new MemoryRuntimeContext();
        first.Inotify.MaxUserInstances = 1;
        second.Inotify.MaxUserInstances = 1;

        var a = new InotifyInode(0, CreateSuperBlock(first), ownerUid: 5001);
        var b = new InotifyInode(0, CreateSuperBlock(second), ownerUid: 5001);

        Assert.True(a.InstanceSlotHeld);
        Assert.True(b.InstanceSlotHeld);

        var aSecond = new InotifyInode(0, CreateSuperBlock(first), ownerUid: 5001);
        Assert.False(aSecond.InstanceSlotHeld);

        a.Release(null!);
        b.Release(null!);
        aSecond.Release(null!);
    }

    [Fact]
    public void InotifyWatchQuota_IsPerRuntime()
    {
        using var first = new MemoryRuntimeContext();
        using var second = new MemoryRuntimeContext();
        first.Inotify.MaxUserWatches = 1;
        second.Inotify.MaxUserWatches = 1;

        var sbA = CreateSuperBlock(first);
        var sbB = CreateSuperBlock(second);
        var inodeA = CreateFileInode(sbA);
        var inodeB = CreateFileInode(sbB);

        var watchA = new InotifyInode(0, sbA, ownerUid: 5002);
        var watchB = new InotifyInode(0, sbB, ownerUid: 5002);

        Assert.True(watchA.AddWatch(LinuxConstants.IN_MODIFY, false, sbA, inodeA.Ino, inodeA) > 0);
        Assert.True(watchB.AddWatch(LinuxConstants.IN_MODIFY, false, sbB, inodeB.Ino, inodeB) > 0);

        var secondInodeA = CreateFileInode(sbA);
        Assert.Equal(-(int)Errno.ENOSPC,
            watchA.AddWatch(LinuxConstants.IN_MODIFY, false, sbA, secondInodeA.Ino, secondInodeA));

        watchA.Release(null!);
        watchB.Release(null!);
    }

    [Fact]
    public void InotifyRegistry_DoesNotDeliverAcrossRuntimes()
    {
        using var first = new MemoryRuntimeContext();
        using var second = new MemoryRuntimeContext();
        var sbA = CreateSuperBlock(first);
        var sbB = CreateSuperBlock(second);
        var inodeA = CreateFileInode(sbA);
        var inodeB = CreateFileInode(sbB);

        var watchA = new InotifyInode(0, sbA, ownerUid: 5003);
        var watchB = new InotifyInode(0, sbB, ownerUid: 5004);
        watchA.AddWatch(LinuxConstants.IN_MODIFY, false, sbA, inodeA.Ino, inodeA);
        watchB.AddWatch(LinuxConstants.IN_MODIFY, false, sbB, inodeB.Ino, inodeB);

        InotifyEvents.Raise(inodeA, LinuxConstants.IN_MODIFY);

        Assert.Single(ReadMasks(watchA));
        Assert.Empty(ReadMasks(watchB));

        watchA.Release(null!);
        watchB.Release(null!);
    }

    [Fact]
    public void RegistryFollowsTheWatchedInodeRuntime_NotTheInotifyInstanceRuntime()
    {
        using var instanceContext = new MemoryRuntimeContext();
        using var fileContext = new MemoryRuntimeContext();
        var instanceSb = CreateSuperBlock(instanceContext);
        var fileSb = CreateSuperBlock(fileContext);
        var fileInode = CreateFileInode(fileSb);

        var watch = new InotifyInode(0, instanceSb, ownerUid: 5005);
        Assert.True(watch.AddWatch(LinuxConstants.IN_MODIFY, false, fileSb, fileInode.Ino, fileInode) > 0);

        InotifyEvents.Raise(fileInode, LinuxConstants.IN_MODIFY);
        Assert.Single(ReadMasks(watch));
        watch.Release(null!);
    }

    [Fact]
    public void NetworkTag_IsPerRuntime()
    {
        using var first = new MemoryRuntimeContext();
        using var second = new MemoryRuntimeContext();

        first.NetworkSysctls.SetTag(ProcSysNetworkInterface.Lo, 27);

        Assert.Equal(27, first.NetworkSysctls.GetTag(ProcSysNetworkInterface.Lo));
        Assert.Equal(0, second.NetworkSysctls.GetTag(ProcSysNetworkInterface.Lo));
    }

    private static TmpfsSuperBlock CreateSuperBlock(MemoryRuntimeContext context)
    {
        return (TmpfsSuperBlock)new Tmpfs(memoryContext: context)
            .ReadSuper(new FileSystemType { Name = "tmpfs" }, 0, string.Empty, null);
    }

    private static Inode CreateFileInode(TmpfsSuperBlock sb)
    {
        var inode = sb.AllocInode();
        inode.Type = InodeType.File;
        return inode;
    }

    private static uint[] ReadMasks(InotifyInode watch)
    {
        var buffer = new byte[256];
        var length = watch.ReadToHost(null, null!, buffer, 0);
        if (length <= 0)
            return [];

        var masks = new List<uint>();
        for (var offset = 0; offset < length;)
        {
            masks.Add(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 4, 4)));
            var nameLength = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                buffer.AsSpan(offset + 12, 4));
            offset += 16 + nameLength;
        }

        return [.. masks];
    }
}
