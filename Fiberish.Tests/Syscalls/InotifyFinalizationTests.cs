using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public class InotifyFinalizationTests
{
    [Theory]
    [InlineData("SysUnlink", false)]
    [InlineData("SysUnlinkAt", false)]
    [InlineData("SysRmdir", true)]
    [InlineData("SysUnlinkAt", true)]
    [InlineData("SysRename", false)]
    public void BackendException_DoesNotSuppressSubsequentDeletion(string syscall, bool directory)
    {
        using var env = new SyscallTestEnv(CreateFaultingSuperBlock);
        env.PutString(0x10000, "/victim");
        env.PutString(0x11000, "/source");
        if (directory)
            Assert.Equal(0, env.Invoke("SysMkdir", 0x10000, 0x1FF));
        else
            env.Sm.WriteGuestFile("/victim", "v"u8);
        env.Sm.WriteGuestFile("/source", "s"u8);
        var inode = env.Sm.PathWalk("/victim").Dentry!.Inode!;
        var watch = env.Watch(inode, LinuxConstants.IN_DELETE_SELF);
        try
        {
            var sb = (FaultingSuperBlock)env.Sb;
            sb.FailMutation = true;
            Assert.Throws<IOException>(() => InvokeMutation(env, syscall, directory));
            Assert.Empty(env.ReadMasks(watch));
            sb.FailMutation = false;
            Assert.Equal(0, InvokeMutation(env, syscall, directory));
            uint[] expected = [LinuxConstants.IN_DELETE_SELF, LinuxConstants.IN_IGNORED];
            Assert.Equal(expected, env.ReadMasks(watch));
            Assert.True(inode.IsFinalized);
        }
        finally { watch.Release(null!); }
    }

    [Fact]
    public void FinalizationFollowedByBackendException_StillFlushesPendingDeletion()
    {
        using var env = new SyscallTestEnv(CreateFaultingSuperBlock);
        env.Sm.WriteGuestFile("/victim", "v"u8);
        var inode = env.Sm.PathWalk("/victim").Dentry!.Inode!;
        var watch = env.Watch(inode, LinuxConstants.IN_DELETE_SELF);
        try
        {
            ((FaultingSuperBlock)env.Sb).FailAfterUnlink = true;
            env.PutString(0x10000, "/victim");
            Assert.Throws<IOException>(() => env.Invoke("SysUnlink", 0x10000));
            uint[] expected = [LinuxConstants.IN_DELETE_SELF, LinuxConstants.IN_IGNORED];
            Assert.Equal(expected, env.ReadMasks(watch));
            Assert.True(inode.IsFinalized);
        }
        finally { watch.Release(null!); }
    }

    [Fact]
    public void DeferredFinalization_IsPerInode_AndFlushesAfterOutermostScope()
    {
        using var env = new SyscallTestEnv();
        env.Sm.WriteGuestFile("/a", "a"u8);
        env.Sm.WriteGuestFile("/b", "b"u8);
        var a = env.Sm.PathWalk("/a");
        var b = env.Sm.PathWalk("/b");
        var inodeA = a.Dentry!.Inode!;
        var inodeB = b.Dentry!.Inode!;
        var fileA = new LinuxFile(a, FileFlags.O_RDONLY);
        var fileB = new LinuxFile(b, FileFlags.O_RDONLY);
        var watchA = env.Watch(inodeA, LinuxConstants.IN_DELETE_SELF);
        var watchB = env.Watch(inodeB, LinuxConstants.IN_DELETE_SELF);
        try
        {
            env.PutString(0x10000, "/a");
            Assert.Equal(0, env.Invoke("SysUnlink", 0x10000));
            env.PutString(0x10000, "/b");
            Assert.Equal(0, env.Invoke("SysUnlink", 0x10000));
            var state = env.Sb.MemoryContext.Inotify;
            uint[] expected = [LinuxConstants.IN_DELETE_SELF, LinuxConstants.IN_IGNORED];
            using (state.SuspendFinalization(inodeA))
            {
                using (state.SuspendFinalization(inodeA))
                {
                    fileA.Close();
                    fileB.Close();
                    Assert.True(inodeA.IsFinalized);
                    Assert.True(inodeB.IsFinalized);
                    Assert.Empty(env.ReadMasks(watchA));
                    Assert.Equal(expected, env.ReadMasks(watchB));
                }
                Assert.Empty(env.ReadMasks(watchA));
            }
            Assert.Equal(expected, env.ReadMasks(watchA));
            Assert.Empty(env.ReadMasks(watchA)); // delivered only once
        }
        finally
        {
            fileA.Close();
            fileB.Close();
            watchA.Release(null!);
            watchB.Release(null!);
        }
    }

    private static int InvokeMutation(SyscallTestEnv env, string syscall, bool directory) => syscall switch
    {
        "SysUnlinkAt" => env.Invoke(syscall, unchecked((uint)-100), 0x10000, directory ? 0x200U : 0),
        "SysRename" => env.Invoke(syscall, 0x11000, 0x10000),
        _ => env.Invoke(syscall, 0x10000)
    };

    private static TmpfsSuperBlock CreateFaultingSuperBlock(MemoryRuntimeContext context)
    {
        var sb = new FaultingSuperBlock(context);
        var root = sb.AllocInode();
        root.Type = InodeType.Directory;
        root.Mode = 0x1FF;
        root.SetInitialLinkCount(2, "test root");
        sb.Root = new Dentry(FsName.Empty, root, null, sb);
        sb.Root.Parent = sb.Root;
        return sb;
    }

    private sealed class FaultingSuperBlock(MemoryRuntimeContext context)
        : TmpfsSuperBlock(new FileSystemType { Name = "tmpfs" }, new DeviceNumberManager(), memoryContext: context)
    {
        public bool FailMutation { get; set; }
        public bool FailAfterUnlink { get; set; }
        protected override IndexedMemoryInode CreateIndexedInode(ulong ino) => new FaultingInode(ino, this);
    }

    private sealed class FaultingInode(ulong ino, FaultingSuperBlock sb) : TmpfsInode(ino, sb)
    {
        public override int Unlink(ReadOnlySpan<byte> name)
        {
            if (sb.FailMutation) throw new IOException("Injected unlink failure");
            var rc = base.Unlink(name);
            if (sb.FailAfterUnlink) throw new IOException("Injected post-unlink failure");
            return rc;
        }
        public override int Rmdir(ReadOnlySpan<byte> name)
        {
            if (sb.FailMutation) throw new IOException("Injected rmdir failure");
            return base.Rmdir(name);
        }
        public override int Rename(ReadOnlySpan<byte> oldName, Inode newParent, ReadOnlySpan<byte> newName)
        {
            if (sb.FailMutation) throw new IOException("Injected rename failure");
            return base.Rename(oldName, newParent, newName);
        }
    }
}
