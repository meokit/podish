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

        // Parent creates the first private page overlay.
        Assert.True(env.ParentEngine.CopyToUser(mapAddr, new byte[] { (byte)'P' }));
        var parentVma = Assert.Single(env.ParentMm.VMAs);
        Assert.NotNull(parentVma.PrivateObject);
        var pageIndex = parentVma.ViewPageOffset;
        var parentPageBeforeClone = parentVma.PrivateObject!.GetPage(pageIndex);
        Assert.NotEqual(IntPtr.Zero, parentPageBeforeClone);

        // Fork-style clone: child gets independent PrivateObject metadata with shared page pointers.
        var childMm = env.ParentMm.Clone();
        using var childEngine = new Engine();
        childEngine.PageFaultResolver =
            (addr, isWrite) => childMm.HandleFaultDetailed(addr, isWrite, childEngine) == FaultResult.Handled;

        var childVma = Assert.Single(childMm.VMAs);
        Assert.NotNull(childVma.PrivateObject);
        Assert.NotSame(parentVma.PrivateObject, childVma.PrivateObject);
        var childPageBeforeWrite = childVma.PrivateObject!.GetPage(childVma.ViewPageOffset);
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

        var parentPageAfterWrite = parentVma.PrivateObject!.GetPage(pageIndex);
        var childPageAfterWrite = childVma.PrivateObject!.GetPage(childVma.ViewPageOffset);
        Assert.NotEqual(IntPtr.Zero, parentPageAfterWrite);
        Assert.NotEqual(IntPtr.Zero, childPageAfterWrite);
        Assert.NotEqual(parentPageAfterWrite, childPageAfterWrite);
    }

    [Fact]
    public void ForkClone_PrivateAnonymous_NoEagerCopyUntilWrite()
    {
        using var pageScope = ExternalPageManager.BeginIsolatedScope();
        using var parentEngine = new Engine();
        var parentMm = new VMAManager();
        parentEngine.PageFaultResolver =
            (addr, isWrite) => parentMm.HandleFaultDetailed(addr, isWrite, parentEngine) == FaultResult.Handled;

        const uint mapAddr = 0x47000000;
        Assert.Equal(mapAddr, parentMm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
            MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, 0, "anon-cow", parentEngine));

        var parentVma = Assert.Single(parentMm.VMAs);
        Assert.Equal(FaultResult.Handled, parentMm.HandleFaultDetailed(mapAddr, isWrite: false, parentEngine));
        Assert.Equal(IntPtr.Zero, parentVma.PrivateObject!.GetPage(parentVma.ViewPageOffset));
        Assert.Equal(IntPtr.Zero, parentVma.SharedObject.GetPage(parentVma.ViewPageOffset));

        var childMm = parentMm.Clone();
        using var childEngine = new Engine();
        childEngine.PageFaultResolver =
            (addr, isWrite) => childMm.HandleFaultDetailed(addr, isWrite, childEngine) == FaultResult.Handled;

        var childVma = Assert.Single(childMm.VMAs);
        Assert.Same(parentVma.SharedObject, childVma.SharedObject);
        Assert.NotSame(parentVma.PrivateObject, childVma.PrivateObject);
        Assert.Equal(IntPtr.Zero, childVma.PrivateObject!.GetPage(childVma.ViewPageOffset));

        Assert.True(childEngine.CopyToUser(mapAddr, new byte[] { (byte)'C' }));

        var parentRead = new byte[1];
        var childRead = new byte[1];
        Assert.True(parentEngine.CopyFromUser(mapAddr, parentRead));
        Assert.True(childEngine.CopyFromUser(mapAddr, childRead));
        Assert.Equal(0, parentRead[0]);
        Assert.Equal((byte)'C', childRead[0]);
        Assert.Equal(IntPtr.Zero, parentVma.PrivateObject!.GetPage(parentVma.ViewPageOffset));
        Assert.NotEqual(IntPtr.Zero, childVma.PrivateObject!.GetPage(childVma.ViewPageOffset));
    }

    [Fact]
    public void PrivateAnonymousReadFault_MapsSharedReadOnlyZeroPage()
    {
        using var pageScope = ExternalPageManager.BeginIsolatedScope();
        using var engine = new Engine();
        var mm = new VMAManager();
        engine.PageFaultResolver =
            (addr, isWrite) => mm.HandleFaultDetailed(addr, isWrite, engine) == FaultResult.Handled;

        const uint map1 = 0x47200000;
        const uint map2 = 0x47300000;
        Assert.Equal(map1, mm.Mmap(map1, LinuxConstants.PageSize, Protection.Read | Protection.Write,
            MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, 0, "anon-zero-1", engine));
        Assert.Equal(map2, mm.Mmap(map2, LinuxConstants.PageSize, Protection.Read | Protection.Write,
            MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, 0, "anon-zero-2", engine));

        var buf = new byte[1];
        Assert.True(engine.CopyFromUser(map1, buf));
        Assert.Equal(0, buf[0]);
        Assert.True(engine.CopyFromUser(map2, buf));
        Assert.Equal(0, buf[0]);

        Assert.True(mm.ExternalPages.TryGet(map1, out var zero1));
        Assert.True(mm.ExternalPages.TryGet(map2, out var zero2));
        Assert.NotEqual(IntPtr.Zero, zero1);
        Assert.Equal(zero1, zero2);

        var vmas = mm.VMAs.OrderBy(vma => vma.Start).ToArray();
        Assert.Equal(2, vmas.Length);
        Assert.Equal(IntPtr.Zero, vmas[0].SharedObject.GetPage(vmas[0].ViewPageOffset));
        Assert.Equal(IntPtr.Zero, vmas[1].SharedObject.GetPage(vmas[1].ViewPageOffset));

        Assert.True(engine.CopyToUser(map1, new byte[] { (byte)'Z' }));
        Assert.True(engine.CopyFromUser(map1, buf));
        Assert.Equal((byte)'Z', buf[0]);
        Assert.True(engine.CopyFromUser(map2, buf));
        Assert.Equal(0, buf[0]);
    }

    [Fact]
    public void ForkClone_PrivateAnonymous_AlreadyPrivatePage_SharesUntilWriteWithoutExtraCloneAllocation()
    {
        using var pageScope = ExternalPageManager.BeginIsolatedScope();
        using var parentEngine = new Engine();
        var parentMm = new VMAManager();
        parentEngine.PageFaultResolver =
            (addr, isWrite) => parentMm.HandleFaultDetailed(addr, isWrite, parentEngine) == FaultResult.Handled;

        const uint mapAddr = 0x47100000;
        Assert.Equal(mapAddr, parentMm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
            MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, 0, "anon-cow-private", parentEngine));

        Assert.True(parentEngine.CopyToUser(mapAddr, new byte[] { (byte)'P' }));
        var parentVma = Assert.Single(parentMm.VMAs);
        var pageIndex = parentVma.ViewPageOffset;
        var parentPrivatePage = parentVma.PrivateObject!.GetPage(pageIndex);
        Assert.NotEqual(IntPtr.Zero, parentPrivatePage);
        var allocatedBeforeClone = ExternalPageManager.GetAllocatedBytes();

        var childMm = parentMm.Clone();
        using var childEngine = new Engine();
        childEngine.PageFaultResolver =
            (addr, isWrite) => childMm.HandleFaultDetailed(addr, isWrite, childEngine) == FaultResult.Handled;

        var childVma = Assert.Single(childMm.VMAs);
        Assert.NotSame(parentVma.PrivateObject, childVma.PrivateObject);
        Assert.Equal(parentPrivatePage, childVma.PrivateObject!.GetPage(childVma.ViewPageOffset));
        Assert.Equal(allocatedBeforeClone, ExternalPageManager.GetAllocatedBytes());

        var parentRead = new byte[1];
        var childRead = new byte[1];
        Assert.True(parentEngine.CopyFromUser(mapAddr, parentRead));
        Assert.True(childEngine.CopyFromUser(mapAddr, childRead));
        Assert.Equal((byte)'P', parentRead[0]);
        Assert.Equal((byte)'P', childRead[0]);

        Assert.True(childEngine.CopyToUser(mapAddr, new byte[] { (byte)'C' }));

        Assert.True(parentEngine.CopyFromUser(mapAddr, parentRead));
        Assert.True(childEngine.CopyFromUser(mapAddr, childRead));
        Assert.Equal((byte)'P', parentRead[0]);
        Assert.Equal((byte)'C', childRead[0]);
        Assert.NotEqual(parentVma.PrivateObject!.GetPage(pageIndex), childVma.PrivateObject!.GetPage(childVma.ViewPageOffset));
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
