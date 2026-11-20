using System.Buffers.Binary;
using Fiberish.Core;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Syscalls;

/// <summary>
///     Event-queue behavior of inotify watches: coalescing rules and IN_EXCL_UNLINK
///     suppression after the inode loses its last namespace link.
/// </summary>
public class InotifyEventTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_DeliversAnEventAcrossGuestPageBoundary(bool vectored)
    {
        using var env = new SyscallTestEnv();
        var fd = (uint)env.Invoke("SysInotifyInit1", LinuxConstants.IN_NONBLOCK);
        env.PutString(0x10000, "/");
        var wd = env.Invoke("SysInotifyAddWatch", fd, 0x10000, LinuxConstants.IN_CREATE);
        Assert.True(wd > 0);
        InotifyEvents.Raise(env.Sb.Root.Inode!, LinuxConstants.IN_CREATE, "user-home.desktop");

        const uint resultAddress = 0x13000;
        Assert.Equal(0, env.Invoke("SysIoctl", fd, LinuxConstants.FIONREAD, resultAddress));
        Span<byte> count = stackalloc byte[4];
        Assert.True(env.Engine.CopyFromUser(resultAddress, count));
        var available = BinaryPrimitives.ReadInt32LittleEndian(count);
        Assert.InRange(available, 34, 64);

        const uint destination = 0x10fec;
        var result = env.Invoke("SysRead", fd, destination, 20);
        Assert.Equal(-(int)Errno.EINVAL, result);
        if (vectored)
        {
            Span<byte> table = stackalloc byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(table, destination);
            BinaryPrimitives.WriteUInt32LittleEndian(table[4..], 20);
            BinaryPrimitives.WriteUInt32LittleEndian(table[8..], 0x12000);
            BinaryPrimitives.WriteUInt32LittleEndian(table[12..], 4096);
            Assert.True(env.Engine.CopyToUser(0x14000, table));
            Assert.Equal(-(int)Errno.EINVAL, env.Invoke("SysReadV", fd, 0x14000, 2));
            BinaryPrimitives.WriteUInt32LittleEndian(table[4..], 4096);
            Assert.True(env.Engine.CopyToUser(0x14000, table));
            result = env.Invoke("SysReadV", fd, 0x14000, 2);
        }
        else
        {
            result = env.Invoke("SysRead", fd, destination, 4096);
        }

        Assert.Equal(available, result);
        Span<byte> record = stackalloc byte[available];
        Assert.True(env.Engine.CopyFromUser(destination, record));
        Assert.Equal(wd, BinaryPrimitives.ReadInt32LittleEndian(record));
        Assert.Equal(LinuxConstants.IN_CREATE, BinaryPrimitives.ReadUInt32LittleEndian(record[4..]));
        Assert.True(record[16..34].SequenceEqual("user-home.desktop\0"u8));
        Assert.Equal(-(int)Errno.EAGAIN, env.Invoke("SysRead", fd, destination, 4096));
    }

    [Theory]
    [InlineData(LinuxConstants.IN_ACCESS, true)]
    [InlineData(LinuxConstants.IN_OPEN, true)]
    [InlineData(LinuxConstants.IN_CLOSE_WRITE, true)]
    [InlineData(LinuxConstants.IN_CLOSE_NOWRITE, true)]
    [InlineData(LinuxConstants.IN_ATTRIB, true)]
    [InlineData(LinuxConstants.IN_MOVE_SELF, false)]
    [InlineData(LinuxConstants.IN_DELETE_SELF, false)]
    public void DirectorySelfEvent_UsesEventSpecificIsDirFlag(uint mask, bool isDir)
    {
        using var env = new SyscallTestEnv();
        var inode = env.Sb.Root.Inode!;
        var watch = env.Watch(inode, LinuxConstants.IN_ALL_EVENTS);
        try
        {
            InotifyEvents.Raise(inode, mask);
            var events = env.ReadEvents(watch);
            Assert.Equal(isDir ? mask | LinuxConstants.IN_ISDIR : mask, events[0].Mask);
            Assert.Empty(events[0].Name);
            if (mask == LinuxConstants.IN_DELETE_SELF)
                Assert.Equal(LinuxConstants.IN_IGNORED, events[1].Mask);
            else
                Assert.Single(events);
        }
        finally { watch.Release(null!); }
    }

    [Fact]
    public void MoveSelf_ConsecutiveEvents_AreCoalesced()
    {
        var sb = CreateSuperBlock();
        var inode = CreateFileInode(sb);
        var watch = new InotifyInode(0, sb);
        watch.AddWatch(LinuxConstants.IN_MOVE_SELF, false, sb, inode.Ino, inode);

        InotifyEvents.Raise(inode, LinuxConstants.IN_MOVE_SELF);
        InotifyEvents.Raise(inode, LinuxConstants.IN_MOVE_SELF);
        InotifyEvents.Raise(inode, LinuxConstants.IN_MOVE_SELF);

        uint[] expectedMoveSelf = [LinuxConstants.IN_MOVE_SELF];
        Assert.Equal(expectedMoveSelf, ReadMasks(watch));
        watch.Release(null!);
    }

    [Fact]
    public void Modify_ConsecutiveEvents_AreCoalesced()
    {
        var sb = CreateSuperBlock();
        var inode = CreateFileInode(sb);
        var watch = new InotifyInode(0, sb);
        watch.AddWatch(LinuxConstants.IN_MODIFY, false, sb, inode.Ino, inode);

        InotifyEvents.Raise(inode, LinuxConstants.IN_MODIFY);
        InotifyEvents.Raise(inode, LinuxConstants.IN_MODIFY);

        uint[] expectedModify = [LinuxConstants.IN_MODIFY];
        Assert.Equal(expectedModify, ReadMasks(watch));
        watch.Release(null!);
    }

    [Fact]
    public void Modify_DifferentNames_AreNotCoalesced()
    {
        var sb = CreateSuperBlock();
        var directory = sb.Root.Inode!;
        var watch = new InotifyInode(0, sb);
        watch.AddWatch(LinuxConstants.IN_CREATE, true, sb, directory.Ino, directory);

        InotifyEvents.Raise(directory, LinuxConstants.IN_CREATE, "first");
        InotifyEvents.Raise(directory, LinuxConstants.IN_CREATE, "first");
        InotifyEvents.Raise(directory, LinuxConstants.IN_CREATE, "second");

        uint[] expected = [LinuxConstants.IN_CREATE, LinuxConstants.IN_CREATE];
        Assert.Equal(expected, ReadMasks(watch));
        watch.Release(null!);
    }

    [Fact]
    public void Modify_AfterRead_IsCoalescedAgain()
    {
        var sb = CreateSuperBlock();
        var inode = CreateFileInode(sb);
        var watch = new InotifyInode(0, sb);
        watch.AddWatch(LinuxConstants.IN_MODIFY, false, sb, inode.Ino, inode);

        InotifyEvents.Raise(inode, LinuxConstants.IN_MODIFY);
        InotifyEvents.Raise(inode, LinuxConstants.IN_MODIFY);
        uint[] first = [LinuxConstants.IN_MODIFY];
        Assert.Equal(first, ReadMasks(watch));

        InotifyEvents.Raise(inode, LinuxConstants.IN_MODIFY);
        InotifyEvents.Raise(inode, LinuxConstants.IN_MODIFY);
        Assert.Equal(first, ReadMasks(watch));
        watch.Release(null!);
    }

    [Fact]
    public void ExclUnlink_ParentWatch_SuppressesUnlinkedChildEvents()
    {
        var sb = CreateSuperBlock();
        var parent = sb.Root.Inode!;
        var child = CreateFileInode(sb);
        var dentry = new Dentry("file", child, sb.Root, sb);
        var watch = new InotifyInode(0, sb);
        watch.AddWatch(LinuxConstants.IN_MODIFY | LinuxConstants.IN_EXCL_UNLINK, true, sb, parent.Ino, parent);

        InotifyEvents.RaiseChild(dentry, child, LinuxConstants.IN_MODIFY);
        uint[] expectedModify = [LinuxConstants.IN_MODIFY];
        Assert.Equal(expectedModify, ReadMasks(watch));

        InotifyEvents.RaiseChild(dentry, child, LinuxConstants.IN_MODIFY, childUnlinked: true);
        Assert.Empty(ReadMasks(watch));
        watch.Release(null!);
    }

    [Fact]
    public void ExclUnlink_IsPerDentry_NotPerInodeLinkCount()
    {
        var sb = CreateSuperBlock();
        var parent = sb.Root.Inode!;
        var inode = CreateFileInode(sb);
        var unlinkedDentry = new Dentry("removed-link", inode, sb.Root, sb);
        var liveDentry = new Dentry("live-link", inode, sb.Root, sb);
        var watch = new InotifyInode(0, sb);
        watch.AddWatch(LinuxConstants.IN_MODIFY | LinuxConstants.IN_EXCL_UNLINK, true, sb, parent.Ino, parent);

        InotifyEvents.RaiseChild(unlinkedDentry, inode, LinuxConstants.IN_MODIFY, childUnlinked: true);
        Assert.Empty(ReadMasks(watch));

        InotifyEvents.RaiseChild(liveDentry, inode, LinuxConstants.IN_MODIFY, childUnlinked: false);
        uint[] expectedModify = [LinuxConstants.IN_MODIFY];
        Assert.Equal(expectedModify, ReadMasks(watch));
        watch.Release(null!);
    }

    [Fact]
    public void ExclUnlink_DirectInodeWatch_StillReportsWhileOpen()
    {
        var sb = CreateSuperBlock();
        var inode = CreateFileInode(sb);
        var watch = new InotifyInode(0, sb);
        watch.AddWatch(LinuxConstants.IN_MODIFY | LinuxConstants.IN_EXCL_UNLINK, false, sb, inode.Ino, inode);

        InotifyEvents.MarkUnlinked(inode);
        InotifyEvents.Raise(inode, LinuxConstants.IN_MODIFY);

        uint[] expectedModify = [LinuxConstants.IN_MODIFY];
        Assert.Equal(expectedModify, ReadMasks(watch));
        watch.Release(null!);
    }

    [Fact]
    public void AddWatch_SameInodeThroughDifferentPaths_ReturnsSameDescriptor()
    {
        var sb = CreateSuperBlock();
        var inode = CreateFileInode(sb);
        var watch = new InotifyInode(0, sb);

        var first = watch.AddWatch(LinuxConstants.IN_MODIFY, false, sb, inode.Ino, inode);
        var second = watch.AddWatch(LinuxConstants.IN_MODIFY, false, sb, inode.Ino, inode);

        Assert.Equal(first, second);
        watch.Release(null!);
    }

    [Fact]
    public void DeleteSelf_RetiresWatchEvenWhenNotSubscribed_AndQueuesIgnored()
    {
        var sb = CreateSuperBlock();
        var inode = CreateFileInode(sb);
        var watch = new InotifyInode(0, sb);
        watch.AddWatch(LinuxConstants.IN_MODIFY, false, sb, inode.Ino, inode);

        InotifyEvents.Raise(inode, LinuxConstants.IN_DELETE_SELF);

        uint[] expectedIgnored = [LinuxConstants.IN_IGNORED];
        Assert.Equal(expectedIgnored, ReadMasks(watch));

        InotifyEvents.Raise(inode, LinuxConstants.IN_MODIFY);
        Assert.Empty(ReadMasks(watch));
        watch.Release(null!);
    }

    [Fact]
    public void RaiseChild_PreservesNonUtf8NameBytes()
    {
        var sb = CreateSuperBlock();
        var parent = sb.Root.Inode!;
        var child = CreateFileInode(sb);
        byte[] rawName = [0xFF, 0xFE, (byte)'x'];
        var dentry = new Dentry(FsName.FromBytes(rawName), child, sb.Root, sb);
        var watch = new InotifyInode(0, sb);
        watch.AddWatch(LinuxConstants.IN_CREATE, true, sb, parent.Ino, parent);

        InotifyEvents.RaiseChild(dentry, child, LinuxConstants.IN_CREATE);

        var events = ReadEvents(watch);
        Assert.Single(events);
        Assert.Equal(LinuxConstants.IN_CREATE, events[0].Mask);
        Assert.Equal(rawName, events[0].Name);
        watch.Release(null!);
    }

    private static TmpfsSuperBlock CreateSuperBlock()
    {
        return (TmpfsSuperBlock)new Tmpfs().ReadSuper(new FileSystemType { Name = "tmpfs" }, 0, string.Empty,
            null);
    }

    private static Inode CreateFileInode(TmpfsSuperBlock sb)
    {
        var inode = sb.AllocInode();
        inode.Type = InodeType.File;
        return inode;
    }

    private static uint[] ReadMasks(InotifyInode watch)
    {
        var buffer = new byte[4096];
        var length = watch.ReadToHost(null, null!, buffer, 0);
        if (length <= 0)
            return [];

        var masks = new List<uint>();
        for (var offset = 0; offset < length;)
        {
            masks.Add(BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 4, 4)));
            var nameLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 12, 4));
            offset += 16 + nameLength;
        }

        return [.. masks];
    }

    private static (uint Mask, byte[] Name)[] ReadEvents(InotifyInode watch)
    {
        var buffer = new byte[4096];
        var length = watch.ReadToHost(null, null!, buffer, 0);
        if (length <= 0)
            return [];

        var events = new List<(uint Mask, byte[] Name)>();
        for (var offset = 0; offset < length;)
        {
            var mask = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 4, 4));
            var nameLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 12, 4));
            var rawName = buffer.AsSpan(offset + 16, nameLength);
            var nul = rawName.IndexOf((byte)0);
            events.Add((mask, (nul >= 0 ? rawName[..nul] : rawName).ToArray()));
            offset += 16 + nameLength;
        }

        return [.. events];
    }
}
