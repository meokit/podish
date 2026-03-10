using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Memory;

public class CowForkCloneTests
{
    [Fact]
    public void ForkClone_PrivateFileCow_SharedUntilWriteThenSplit()
    {
        using var env = new TestEnv();
        const uint mapAddr = 0x46000000;
        env.MapPrivate(mapAddr);

        // Parent creates first private COW page.
        Assert.True(env.ParentEngine.CopyToUser(mapAddr, new byte[] { (byte)'P' }));
        var parentVma = Assert.Single(env.ParentMm.VMAs);
        Assert.NotNull(parentVma.CowObject);
        var pageIndex = parentVma.ViewPageOffset;
        var parentPageBeforeClone = parentVma.CowObject!.GetPage(pageIndex);
        Assert.NotEqual(IntPtr.Zero, parentPageBeforeClone);

        // Fork-style clone: child gets independent CowObject metadata with shared page pointers.
        var childMm = env.ParentMm.Clone();
        using var childEngine = new Engine();
        childEngine.PageFaultResolver =
            (addr, isWrite) => childMm.HandleFaultDetailed(addr, isWrite, childEngine) == FaultResult.Handled;

        var childVma = Assert.Single(childMm.VMAs);
        Assert.NotNull(childVma.CowObject);
        Assert.NotSame(parentVma.CowObject, childVma.CowObject);
        var childPageBeforeWrite = childVma.CowObject!.GetPage(childVma.ViewPageOffset);
        Assert.Equal(parentPageBeforeClone, childPageBeforeWrite);

        var initialChild = new byte[1];
        Assert.True(childEngine.CopyFromUser(mapAddr, initialChild));
        Assert.Equal((byte)'P', initialChild[0]);

        // Child write must split page from parent, preserving isolation.
        Assert.True(childEngine.CopyToUser(mapAddr, new byte[] { (byte)'C' }));

        var parentRead = new byte[1];
        var childRead = new byte[1];
        Assert.True(env.ParentEngine.CopyFromUser(mapAddr, parentRead));
        Assert.True(childEngine.CopyFromUser(mapAddr, childRead));
        Assert.Equal((byte)'P', parentRead[0]);
        Assert.Equal((byte)'C', childRead[0]);

        var parentPageAfterWrite = parentVma.CowObject!.GetPage(pageIndex);
        var childPageAfterWrite = childVma.CowObject!.GetPage(childVma.ViewPageOffset);
        Assert.NotEqual(IntPtr.Zero, parentPageAfterWrite);
        Assert.NotEqual(IntPtr.Zero, childPageAfterWrite);
        Assert.NotEqual(parentPageAfterWrite, childPageAfterWrite);
    }

    private sealed class TestEnv : IDisposable
    {
        public TestEnv()
        {
            ParentEngine = new Engine();
            ParentMm = new VMAManager();
            ParentEngine.PageFaultResolver =
                (addr, isWrite) => ParentMm.HandleFaultDetailed(addr, isWrite, ParentEngine) == FaultResult.Handled;

            var fsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
            var sb = fsType.CreateFileSystem().ReadSuper(fsType, 0, "cow-fork-clone", null);
            var mount = new Mount(sb, sb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
            var dentry = new Dentry("cow.bin", null, sb.Root, sb);
            sb.Root.Inode!.Create(dentry, 0x1A4, 0, 0);

            File = new LinuxFile(dentry, FileFlags.O_RDWR, mount);
            Inode = dentry.Inode!;
            Assert.Equal(1, Inode.Write(File, new byte[] { (byte)'A' }, 0));
        }

        public Engine ParentEngine { get; }
        public VMAManager ParentMm { get; }
        public LinuxFile File { get; }
        public Inode Inode { get; }

        public void MapPrivate(uint addr)
        {
            var mapped = ParentMm.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed, File, 0, (long)Inode.Size, "fork-cow", ParentEngine);
            Assert.Equal(addr, mapped);
        }

        public void Dispose()
        {
            File.Close();
            ParentEngine.Dispose();
        }
    }
}
