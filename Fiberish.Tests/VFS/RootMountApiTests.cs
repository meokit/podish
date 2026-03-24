using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.VFS;

public class RootMountApiTests
{
    [Fact]
    public void MountRoot_AllowsExternalSuperblockAndSubtreeRoot()
    {
        using var engine = new Engine();
        var sm = new SyscallManager(engine, new VMAManager(), 0);

        var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
        var sb = tmpfsType.CreateAnonymousFileSystem().ReadSuper(tmpfsType, 0, "test-root", null);
        var root = sb.Root;
        var sub = new Dentry("sub", null, root, sb);
        root.Inode!.Mkdir(sub, 0x1FF, 0, 0);

        sm.MountRoot(sb, new SyscallManager.RootMountOptions
        {
            Source = "external",
            FsType = "tmpfs",
            Options = "rw",
            Root = sub
        });

        Assert.True(sm.PathWalkWithFlags("/", LookupFlags.FollowSymlink).IsValid);
        Assert.True(sm.PathWalkWithFlags("/..", LookupFlags.FollowSymlink).IsValid);
        Assert.Equal("tmpfs", sm.RootMount!.FsType);
        Assert.Equal("external", sm.RootMount.Source);
        Assert.Equal("sub", sm.RootMount.Root.Name);

        sm.Close();
    }
}