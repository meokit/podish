using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Syscalls;

/// <summary>
///     Inotify lifecycle rules: an unlinked inode is not retired while open or mapped,
///     rename-overwrite reports the replaced target, and mmap shares the fd's file
///     description reference.
/// </summary>
public class InotifyLifecycleTests
{
    [Fact]
    public void UnlinkWhileOpen_DefersDeleteSelfUntilFinalClose()
    {
        using var env = new SyscallTestEnv();
        env.Sm.WriteGuestFile("/open", "x"u8);
        var loc = env.Sm.PathWalk("/open");
        var inode = loc.Dentry!.Inode!;
        using var watch = new WatchHandle(env.Watch(inode,
            LinuxConstants.IN_MODIFY | LinuxConstants.IN_CLOSE_WRITE | LinuxConstants.IN_ATTRIB |
            LinuxConstants.IN_DELETE_SELF));

        var file = new LinuxFile(loc, FileFlags.O_RDWR);
        env.PutString(0x10000, "/open");
        Assert.Equal(0, env.Invoke("SysUnlink", 0x10000));

        uint[] attribOnly = [LinuxConstants.IN_ATTRIB];
        Assert.Equal(attribOnly, env.ReadMasks(watch.Inode));

        InotifyEvents.RaiseChild(file.Dentry, inode, LinuxConstants.IN_MODIFY);
        file.Close();

        uint[] expected = [LinuxConstants.IN_MODIFY, LinuxConstants.IN_CLOSE_WRITE,
            LinuxConstants.IN_DELETE_SELF, LinuxConstants.IN_IGNORED];
        Assert.Equal(expected, env.ReadMasks(watch.Inode));
    }

    [Fact]
    public void HardLinkSurvives_NoDeleteSelfUntilLastLinkIsGone()
    {
        using var env = new SyscallTestEnv();
        env.Sm.WriteGuestFile("/hard-a", "x"u8);
        env.PutString(0x10000, "/hard-a");
        env.PutString(0x11000, "/hard-b");
        Assert.Equal(0, env.Invoke("SysLink", 0x10000, 0x11000));

        var loc = env.Sm.PathWalk("/hard-a");
        var inode = loc.Dentry!.Inode!;
        using var watch = new WatchHandle(env.Watch(inode,
            LinuxConstants.IN_CLOSE_WRITE | LinuxConstants.IN_ATTRIB | LinuxConstants.IN_DELETE_SELF));
        var file = new LinuxFile(loc, FileFlags.O_RDWR);

        Assert.Equal(0, env.Invoke("SysUnlink", 0x10000));
        uint[] attribOnly = [LinuxConstants.IN_ATTRIB];
        Assert.Equal(attribOnly, env.ReadMasks(watch.Inode));

        file.Close();
        uint[] closeWrite = [LinuxConstants.IN_CLOSE_WRITE];
        Assert.Equal(closeWrite, env.ReadMasks(watch.Inode));

        env.PutString(0x12000, "/hard-b");
        Assert.Equal(0, env.Invoke("SysUnlink", 0x12000));
        uint[] destroyed = [LinuxConstants.IN_ATTRIB, LinuxConstants.IN_DELETE_SELF, LinuxConstants.IN_IGNORED];
        Assert.Equal(destroyed, env.ReadMasks(watch.Inode));
    }

    [Fact]
    public void RenameOverwrite_ReportsReplacedTarget()
    {
        using var env = new SyscallTestEnv();
        env.Sm.WriteGuestFile("/victim", "v"u8);
        env.Sm.WriteGuestFile("/source", "s"u8);
        var victim = env.Sm.PathWalk("/victim").Dentry!.Inode!;
        using var watch = new WatchHandle(env.Watch(victim, LinuxConstants.IN_ALL_EVENTS));

        env.PutString(0x10000, "/source");
        env.PutString(0x11000, "/victim");
        Assert.Equal(0, env.Invoke("SysRename", 0x10000, 0x11000));

        uint[] destroyed = [LinuxConstants.IN_ATTRIB, LinuxConstants.IN_DELETE_SELF, LinuxConstants.IN_IGNORED];
        Assert.Equal(destroyed, env.ReadMasks(watch.Inode));
    }

    [Fact]
    public void MmapSharesOpenFileDescription_CloseHappensAtMunmap()
    {
        using var env = new SyscallTestEnv();
        env.Sm.WriteGuestFile("/mapped", new byte[LinuxConstants.PageSize]);
        var loc = env.Sm.PathWalk("/mapped");
        var inode = loc.Dentry!.Inode!;
        using var watch = new WatchHandle(env.Watch(inode, LinuxConstants.IN_CLOSE_WRITE));

        var file = new LinuxFile(loc, FileFlags.O_RDWR);
        file.Get();
        var mapped = ProcessAddressSpaceSync.Mmap(env.Mem, env.Engine, 0x200000, LinuxConstants.PageSize,
            Protection.Read | Protection.Write, MapFlags.Shared | MapFlags.Fixed, file, 0, "TEST");
        Assert.Equal(0x200000u, mapped);

        file.Close();
        Assert.Empty(env.ReadMasks(watch.Inode));

        Assert.Equal(0, ProcessAddressSpaceSync.Munmap(env.Mem, env.Engine, 0x200000, LinuxConstants.PageSize));
        uint[] closeWrite = [LinuxConstants.IN_CLOSE_WRITE];
        Assert.Equal(closeWrite, env.ReadMasks(watch.Inode));
    }

    [Fact]
    public void RenameOverwrite_WithSurvivingHardLink_ReportsAttribWithoutRetiringWatch()
    {
        using var env = new SyscallTestEnv();
        env.Sm.WriteGuestFile("/source", "s"u8);
        env.Sm.WriteGuestFile("/victim", "v"u8);
        env.PutString(0x10000, "/victim");
        env.PutString(0x11000, "/survivor");
        Assert.Equal(0, env.Invoke("SysLink", 0x10000, 0x11000));
        var inode = env.Sm.PathWalk("/victim").Dentry!.Inode!;
        using var watch = new WatchHandle(env.Watch(inode, LinuxConstants.IN_ALL_EVENTS));
        env.PutString(0x10000, "/source");
        env.PutString(0x11000, "/victim");
        Assert.Equal(0, env.Invoke("SysRename", 0x10000, 0x11000));
        Assert.Equal(1U, inode.GetLinkCountForStat());
        Assert.False(inode.IsFinalized);
        uint[] attrib = [LinuxConstants.IN_ATTRIB];
        Assert.Equal(attrib, env.ReadMasks(watch.Inode));
        env.PutString(0x10000, "/survivor");
        Assert.Equal(0, env.Invoke("SysUnlink", 0x10000));
        uint[] deleted = [LinuxConstants.IN_ATTRIB, LinuxConstants.IN_DELETE_SELF, LinuxConstants.IN_IGNORED];
        Assert.Equal(deleted, env.ReadMasks(watch.Inode));
    }

    [Fact]
    public void DirectorySelfWatch_OpenAndClose_IncludeIsDir()
    {
        using var env = new SyscallTestEnv();
        using var watch = new WatchHandle(env.Watch(env.Sb.Root.Inode!, LinuxConstants.IN_ALL_EVENTS));
        env.PutString(0x10000, "/");
        var fd = env.Invoke("SysOpen", 0x10000, (uint)FileFlags.O_RDONLY);
        Assert.True(fd >= 0);
        env.Sm.FreeFD(fd);
        uint[] expected = [LinuxConstants.IN_OPEN | LinuxConstants.IN_ISDIR,
            LinuxConstants.IN_CLOSE_NOWRITE | LinuxConstants.IN_ISDIR];
        Assert.Equal(expected, env.ReadMasks(watch.Inode));
    }

    [Fact]
    public void RenameExchange_ReportsBothMovePairsAndSelfMoves()
    {
        using var env = new SyscallTestEnv();
        env.Sm.WriteGuestFile("/a", "a"u8);
        env.Sm.WriteGuestFile("/b", "b"u8);
        var inodeA = env.Sm.PathWalk("/a").Dentry!.Inode!;
        var inodeB = env.Sm.PathWalk("/b").Dentry!.Inode!;
        var parent = env.Sb.Root.Inode!;
        using var parentWatch = new WatchHandle(env.Watch(parent, LinuxConstants.IN_ALL_EVENTS));
        using var watchA = new WatchHandle(env.Watch(inodeA, LinuxConstants.IN_MOVE_SELF));
        using var watchB = new WatchHandle(env.Watch(inodeB, LinuxConstants.IN_MOVE_SELF));

        env.PutString(0x10000, "/a");
        env.PutString(0x11000, "/b");
        Assert.Equal(0, env.Invoke("SysRenameAt2", 0xFFFFFF9C, 0x10000, 0xFFFFFF9C, 0x11000,
            LinuxConstants.RENAME_EXCHANGE));

        var events = env.ReadEvents(parentWatch.Inode);
        Assert.True(events.Length == 4,
            "events=" + string.Join(",", events.Select(e => $"{e.Mask:x}/{e.Cookie}/{System.Text.Encoding.UTF8.GetString(e.Name)}")));
        Assert.Equal(LinuxConstants.IN_MOVED_FROM, events[0].Mask);
        Assert.Equal("a"u8.ToArray(), events[0].Name);
        Assert.Equal(LinuxConstants.IN_MOVED_TO, events[1].Mask);
        Assert.Equal("b"u8.ToArray(), events[1].Name);
        Assert.Equal(LinuxConstants.IN_MOVED_FROM, events[2].Mask);
        Assert.Equal("b"u8.ToArray(), events[2].Name);
        Assert.Equal(LinuxConstants.IN_MOVED_TO, events[3].Mask);
        Assert.Equal("a"u8.ToArray(), events[3].Name);
        Assert.Equal(events[0].Cookie, events[1].Cookie);
        Assert.Equal(events[2].Cookie, events[3].Cookie);
        Assert.NotEqual(events[0].Cookie, events[2].Cookie);

        uint[] moveSelf = [LinuxConstants.IN_MOVE_SELF];
        Assert.Equal(moveSelf, env.ReadMasks(watchA.Inode));
        Assert.Equal(moveSelf, env.ReadMasks(watchB.Inode));
    }

    [Fact]
    public void Fchmod_EmitsInAttrib()
    {
        using var env = new SyscallTestEnv();
        env.Sm.WriteGuestFile("/fchmod", "x"u8);
        using var watch = new WatchHandle(env.Watch(InodeOf(env, "/fchmod"), LinuxConstants.IN_ATTRIB));
        var fd = Open(env, "/fchmod", FileFlags.O_RDWR);
        Assert.True(fd >= 0);

        Assert.Equal(0, env.Invoke("SysFchmod", (uint)fd, 0x1A4));
        uint[] expected = [LinuxConstants.IN_ATTRIB];
        Assert.Equal(expected, env.ReadMasks(watch.Inode));
        env.Sm.FreeFD(fd);
    }

    [Fact]
    public void FchmodAt_EmitsInAttrib()
    {
        using var env = new SyscallTestEnv();
        env.Sm.WriteGuestFile("/fchmodat", "x"u8);
        using var watch = new WatchHandle(env.Watch(InodeOf(env, "/fchmodat"), LinuxConstants.IN_ATTRIB));
        env.PutString(0x10000, "/fchmodat");

        Assert.Equal(0, env.Invoke("SysFchmodAt", unchecked((uint)-100), 0x10000, 0x1A4, 0));
        uint[] expected = [LinuxConstants.IN_ATTRIB];
        Assert.Equal(expected, env.ReadMasks(watch.Inode));
    }

    [Fact]
    public void Fchown_EmitsInAttrib()
    {
        using var env = new SyscallTestEnv();
        env.Sm.WriteGuestFile("/fchown", "x"u8);
        using var watch = new WatchHandle(env.Watch(InodeOf(env, "/fchown"), LinuxConstants.IN_ATTRIB));
        var fd = Open(env, "/fchown", FileFlags.O_RDWR);
        Assert.True(fd >= 0);

        Assert.Equal(0, env.Invoke("SysFchown", (uint)fd, 0, 0));
        uint[] expected = [LinuxConstants.IN_ATTRIB];
        Assert.Equal(expected, env.ReadMasks(watch.Inode));
        env.Sm.FreeFD(fd);
    }

    [Fact]
    public void UtimensAt_EmitsInAttrib()
    {
        using var env = new SyscallTestEnv();
        env.Sm.WriteGuestFile("/utimens", "x"u8);
        using var watch = new WatchHandle(env.Watch(InodeOf(env, "/utimens"), LinuxConstants.IN_ATTRIB));
        env.PutString(0x10000, "/utimens");

        Assert.Equal(0, env.Invoke("SysUtimensAt", unchecked((uint)-100), 0x10000, 0, 0));
        uint[] expected = [LinuxConstants.IN_ATTRIB];
        Assert.Equal(expected, env.ReadMasks(watch.Inode));
    }

    private static Inode InodeOf(SyscallTestEnv env, string path) =>
        env.Sm.PathWalk(path).Dentry!.Inode!;

    private static int Open(SyscallTestEnv env, string path, FileFlags flags)
    {
        env.PutString(0x10000, path);
        return env.Invoke("SysOpen", 0x10000, (uint)flags);
    }

    private sealed class WatchHandle(InotifyInode inode) : IDisposable
    {
        public InotifyInode Inode { get; } = inode;

        public void Dispose()
        {
            Inode.Release(null!);
        }
    }
}
