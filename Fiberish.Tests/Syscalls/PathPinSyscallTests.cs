using System.Reflection;
using System.Text;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public class PathPinSyscallTests
{
    [Fact]
    public async Task Chdir_SwitchesPathPinBetweenDirectories()
    {
        using var env = new TestEnv();
        var root = env.SyscallManager.Root.Dentry!;
        var rootInode = root.Inode!;
        var work = new Dentry("work", null, root, root.SuperBlock);
        rootInode.Mkdir(work, 0x1ED, 0, 0);
        var workInode = work.Inode!;

        var rootBefore = rootInode.RefCount;
        var workBefore = workInode.RefCount;
        var rootDentryBefore = root.DentryRefCount;
        var workDentryBefore = work.DentryRefCount;

        env.MapUserPage(0x10000);
        env.WriteCString(0x10000, "/work");
        Assert.Equal(0, await env.Call("SysChdir", 0x10000));
        Assert.Equal(rootBefore - 1, rootInode.RefCount);
        Assert.Equal(workBefore + 1, workInode.RefCount);
        Assert.Equal(rootDentryBefore - 1, root.DentryRefCount);
        Assert.Equal(workDentryBefore + 1, work.DentryRefCount);

        env.WriteCString(0x10000, "/");
        Assert.Equal(0, await env.Call("SysChdir", 0x10000));
        Assert.Equal(rootBefore, rootInode.RefCount);
        Assert.Equal(workBefore, workInode.RefCount);
        Assert.Equal(rootDentryBefore, root.DentryRefCount);
        Assert.Equal(workDentryBefore, work.DentryRefCount);
    }

    [Fact]
    public async Task Fchdir_UsesPathPinAndPersistsAfterFdClose()
    {
        using var env = new TestEnv();
        var root = env.SyscallManager.Root.Dentry!;
        var rootInode = root.Inode!;
        var dir = new Dentry("fdcwd", null, root, root.SuperBlock);
        rootInode.Mkdir(dir, 0x1ED, 0, 0);
        var dirInode = dir.Inode!;

        env.MapUserPage(0x11000);
        env.WriteCString(0x11000, "/fdcwd");
        var fd = await env.Call("SysOpen", 0x11000, (uint)(FileFlags.O_RDONLY | FileFlags.O_DIRECTORY));
        Assert.True(fd >= 0);

        var rootBefore = rootInode.RefCount;
        var dirBefore = dirInode.RefCount;

        Assert.Equal(0, await env.Call("SysFchdir", (uint)fd));
        Assert.Equal(rootBefore - 1, rootInode.RefCount);
        Assert.Equal(dirBefore + 1, dirInode.RefCount);

        Assert.Equal(0, await env.Call("SysClose", (uint)fd));
        Assert.Equal(dirBefore, dirInode.RefCount);

        env.WriteCString(0x11000, "/");
        Assert.Equal(0, await env.Call("SysChdir", 0x11000));
        Assert.Equal(rootBefore, rootInode.RefCount);
        Assert.Equal(dirBefore - 1, dirInode.RefCount);
    }

    [Fact]
    public void UpdateProcessRoot_SwitchesPathPin()
    {
        using var env = new TestEnv();
        var root = env.SyscallManager.Root.Dentry!;
        var rootInode = root.Inode!;
        var jail = new Dentry("jail", null, root, root.SuperBlock);
        rootInode.Mkdir(jail, 0x1ED, 0, 0);
        var jailInode = jail.Inode!;

        var rootBefore = rootInode.RefCount;
        var jailBefore = jailInode.RefCount;

        env.SyscallManager.UpdateProcessRoot(new PathLocation(jail, env.SyscallManager.Root.Mount), "test");

        Assert.Equal(rootBefore - 1, rootInode.RefCount);
        Assert.Equal(jailBefore + 1, jailInode.RefCount);
        Assert.Same(jail, env.SyscallManager.ProcessRoot.Dentry);
    }

    [Fact]
    public void Clone_WithoutCloneFs_KeepsCwdIsolated()
    {
        using var env = new TestEnv();
        var root = env.SyscallManager.Root.Dentry!;
        var rootInode = root.Inode!;
        var dir = new Dentry("isolated", null, root, root.SuperBlock);
        rootInode.Mkdir(dir, 0x1ED, 0, 0);

        var child = env.SyscallManager.Clone(new VMAManager(), false, false);
        try
        {
            child.UpdateCurrentWorkingDirectory(new PathLocation(dir, env.SyscallManager.Root.Mount), "CloneNoFs");
            Assert.Same(dir, child.CurrentWorkingDirectory.Dentry);
            Assert.Same(root, env.SyscallManager.CurrentWorkingDirectory.Dentry);
        }
        finally
        {
            child.Close();
        }
    }

    [Fact]
    public void Clone_WithCloneFs_SharesCwdAcrossManagers()
    {
        using var env = new TestEnv();
        var root = env.SyscallManager.Root.Dentry!;
        var rootInode = root.Inode!;
        var dir = new Dentry("shared", null, root, root.SuperBlock);
        rootInode.Mkdir(dir, 0x1ED, 0, 0);

        var child = env.SyscallManager.Clone(new VMAManager(), false, true);
        child.UpdateCurrentWorkingDirectory(new PathLocation(dir, env.SyscallManager.Root.Mount), "CloneFs");
        Assert.Same(dir, child.CurrentWorkingDirectory.Dentry);
        Assert.Same(dir, env.SyscallManager.CurrentWorkingDirectory.Dentry);
        child.Close();
        Assert.Same(dir, env.SyscallManager.CurrentWorkingDirectory.Dentry);
    }

    private sealed class TestEnv : IDisposable
    {
        public TestEnv()
        {
            Engine = new Engine();
            Vma = new VMAManager();
            SyscallManager = new SyscallManager(Engine, Vma, 0);

            var tmpfsType = FileSystemRegistry.Get("tmpfs")!;
            var sb = tmpfsType.CreateFileSystem().ReadSuper(tmpfsType, 0, "pathpin-tmpfs", null);
            var mount = new Mount(sb, sb.Root)
            {
                Source = "tmpfs",
                FsType = "tmpfs",
                Options = "rw"
            };
            SyscallManager.InitializeRoot(sb.Root, mount);
        }

        public Engine Engine { get; }
        public VMAManager Vma { get; }
        public SyscallManager SyscallManager { get; }

        public void Dispose()
        {
            SyscallManager.Close();
        }

        public void MapUserPage(uint addr)
        {
            Vma.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, LinuxConstants.PageSize, "[test]",
                Engine);
            Assert.True(Vma.HandleFault(addr, true, Engine));
        }

        public void WriteCString(uint addr, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value + "\0");
            Assert.True(Engine.CopyToUser(addr, bytes));
        }

        public async ValueTask<int> Call(string methodName, uint a1 = 0, uint a2 = 0, uint a3 = 0, uint a4 = 0,
            uint a5 = 0, uint a6 = 0)
        {
            var method = typeof(SyscallManager).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);
            var task = (ValueTask<int>)method!.Invoke(null, [Engine.State, a1, a2, a3, a4, a5, a6])!;
            return await task;
        }
    }
}