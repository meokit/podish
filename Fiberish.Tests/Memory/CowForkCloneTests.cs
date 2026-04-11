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
        Assert.True(env.ParentEngine.CopyToUser(mapAddr, new[] { (byte)'P' }));
        var parentVma = Assert.Single(env.ParentMm.VMAs);
        Assert.NotNull(parentVma.VmAnonVma);
        var pageIndex = parentVma.GetPageIndex(parentVma.Start);
        var parentPageBeforeClone = parentVma.VmAnonVma!.GetPage(pageIndex);
        Assert.NotEqual(IntPtr.Zero, parentPageBeforeClone);

        // Fork-style clone: child gets independent VmAnonVma metadata with shared page pointers.
        var childMm = env.ParentMm.Clone();
        using var childEngine = env.ParentEngine.Clone(false);
        childMm.RebuildExternalMappingsFromNative(childEngine, childMm.VMAs);
        ReprotectPrivateMappings(childMm, childEngine);
        childEngine.PageFaultResolver =
            (addr, isWrite) => childMm.HandleFaultDetailed(addr, isWrite, childEngine) == FaultResult.Handled;

        var childVma = Assert.Single(childMm.VMAs);
        Assert.NotNull(childVma.VmAnonVma);
        Assert.NotSame(parentVma.VmAnonVma, childVma.VmAnonVma);
        var childPageBeforeWrite = childVma.VmAnonVma!.GetPage(childVma.GetPageIndex(childVma.Start));
        Assert.Equal(parentPageBeforeClone, childPageBeforeWrite);

        var initialChild = new byte[1];
        Assert.True(childEngine.CopyFromUser(mapAddr, initialChild));
        Assert.Equal((byte)'P', initialChild[0]);

        // Child write must split page from parent, preserving isolation.
        Assert.True(childEngine.CopyToUser(mapAddr, new[] { (byte)'C' }));

        var parentRead = new byte[1];
        var childRead = new byte[1];
        Assert.True(env.ParentEngine.CopyFromUser(mapAddr, parentRead));
        Assert.True(childEngine.CopyFromUser(mapAddr, childRead));
        Assert.Equal((byte)'P', parentRead[0]);
        Assert.Equal((byte)'C', childRead[0]);

        var parentPageAfterWrite = parentVma.VmAnonVma!.GetPage(pageIndex);
        var childPageAfterWrite = childVma.VmAnonVma!.GetPage(childVma.GetPageIndex(childVma.Start));
        Assert.NotEqual(IntPtr.Zero, parentPageAfterWrite);
        Assert.NotEqual(IntPtr.Zero, childPageAfterWrite);
        Assert.NotEqual(parentPageAfterWrite, childPageAfterWrite);
    }

    [Fact]
    public void ForkClone_PrivateAnonymous_NoEagerCopyUntilWrite()
    {
        using var pageScope = PageManager.BeginIsolatedScope();
        var runtime = new TestRuntimeFactory();
        using var parentEngine = runtime.CreateEngine();
        var parentMm = runtime.CreateAddressSpace();
        parentEngine.PageFaultResolver =
            (addr, isWrite) => parentMm.HandleFaultDetailed(addr, isWrite, parentEngine) == FaultResult.Handled;

        const uint mapAddr = 0x47000000;
        Assert.Equal(mapAddr,
            parentMm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "anon-cow", parentEngine));

        var parentVma = Assert.Single(parentMm.VMAs);
        Assert.Equal(FaultResult.Handled, parentMm.HandleFaultDetailed(mapAddr, false, parentEngine));
        Assert.Null(parentVma.VmAnonVma);
        Assert.NotNull(parentVma.VmMapping);
        Assert.True(parentVma.VmMapping!.IsZeroBacking);

        var childMm = parentMm.Clone();
        using var childEngine = parentEngine.Clone(false);
        childMm.RebuildExternalMappingsFromNative(childEngine, childMm.VMAs);
        childEngine.PageFaultResolver =
            (addr, isWrite) => childMm.HandleFaultDetailed(addr, isWrite, childEngine) == FaultResult.Handled;

        var childVma = Assert.Single(childMm.VMAs);
        Assert.NotNull(parentVma.VmMapping);
        Assert.NotNull(childVma.VmMapping);
        Assert.True(parentVma.VmMapping!.IsZeroBacking);
        Assert.True(childVma.VmMapping!.IsZeroBacking);
        Assert.Null(childVma.VmAnonVma);

        Assert.True(childEngine.CopyToUser(mapAddr, new[] { (byte)'C' }));

        var parentRead = new byte[1];
        var childRead = new byte[1];
        Assert.True(parentEngine.CopyFromUser(mapAddr, parentRead));
        Assert.True(childEngine.CopyFromUser(mapAddr, childRead));
        Assert.Equal(0, parentRead[0]);
        Assert.Equal((byte)'C', childRead[0]);
        Assert.Null(parentVma.VmAnonVma);
        Assert.NotEqual(IntPtr.Zero, childVma.VmAnonVma!.GetPage(childVma.GetPageIndex(childVma.Start)));
    }

    [Fact]
    public void PrivateAnonymousReadFault_MapsSharedReadOnlyZeroPage()
    {
        using var pageScope = PageManager.BeginIsolatedScope();
        var runtime = new TestRuntimeFactory();
        using var engine = runtime.CreateEngine();
        var mm = runtime.CreateAddressSpace();
        engine.PageFaultResolver =
            (addr, isWrite) => mm.HandleFaultDetailed(addr, isWrite, engine) == FaultResult.Handled;

        const uint map1 = 0x47200000;
        const uint map2 = 0x47300000;
        Assert.Equal(map1,
            mm.Mmap(map1, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "anon-zero-1", engine));
        Assert.Equal(map2,
            mm.Mmap(map2, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "anon-zero-2", engine));

        var allocatedBeforeRead = PageManager.GetAllocatedBytes();
        var buf = new byte[1];
        Assert.True(engine.CopyFromUser(map1, buf));
        Assert.Equal(0, buf[0]);
        Assert.True(engine.CopyFromUser(map2, buf));
        Assert.Equal(0, buf[0]);
        Assert.Equal(allocatedBeforeRead, PageManager.GetAllocatedBytes());

        Assert.True(mm.PageMapping.TryGet(map1, out var zero1));
        Assert.True(mm.PageMapping.TryGet(map2, out var zero2));
        Assert.NotEqual(IntPtr.Zero, zero1);
        Assert.Equal(zero1, zero2);

        var vmas = mm.VMAs.OrderBy(vma => vma.Start).ToArray();
        Assert.Equal(2, vmas.Length);
        Assert.NotNull(vmas[0].VmMapping);
        Assert.NotNull(vmas[1].VmMapping);
        Assert.True(vmas[0].VmMapping!.IsZeroBacking);
        Assert.True(vmas[1].VmMapping!.IsZeroBacking);
        Assert.Same(vmas[0].VmMapping, vmas[1].VmMapping);

        Assert.True(engine.CopyToUser(map1, new[] { (byte)'Z' }));
        Assert.True(engine.CopyFromUser(map1, buf));
        Assert.Equal((byte)'Z', buf[0]);
        Assert.True(engine.CopyFromUser(map2, buf));
        Assert.Equal(0, buf[0]);
    }

    [Fact]
    public void PrefaultRange_WithWriteIntent_CreatesPrivatePageWithoutSharedSourceMaterialization()
    {
        using var pageScope = PageManager.BeginIsolatedScope();
        var runtime = new TestRuntimeFactory();
        using var engine = runtime.CreateEngine();
        var mm = runtime.CreateAddressSpace();
        engine.PageFaultResolver =
            (addr, isWrite) => mm.HandleFaultDetailed(addr, isWrite, engine) == FaultResult.Handled;

        const uint mapAddr = 0x47400000;
        Assert.Equal(mapAddr,
            mm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "anon-materialize", engine));

        Assert.True(mm.PrefaultRange(mapAddr, LinuxConstants.PageSize, engine, true));
        var vma = Assert.Single(mm.VMAs);
        var pageIndex = vma.GetPageIndex(vma.Start);
        var privatePage = vma.VmAnonVma!.GetPage(pageIndex);
        Assert.NotEqual(IntPtr.Zero, privatePage);
        Assert.NotNull(vma.VmMapping);
        Assert.True(vma.VmMapping!.IsZeroBacking);

        var probe = new byte[1];
        Assert.True(engine.CopyFromUser(mapAddr, probe));
        Assert.Equal(0, probe[0]);

        Assert.True(engine.CopyToUser(mapAddr, new[] { (byte)'P' }));
        Assert.True(engine.CopyFromUser(mapAddr, probe));
        Assert.Equal((byte)'P', probe[0]);
    }

    [Fact]
    public void ForkClone_PrivateAnonymous_AlreadyPrivatePage_SharesUntilWriteWithoutExtraCloneAllocation()
    {
        using var pageScope = PageManager.BeginIsolatedScope();
        var runtime = new TestRuntimeFactory();
        using var parentEngine = runtime.CreateEngine();
        var parentMm = runtime.CreateAddressSpace();
        parentEngine.PageFaultResolver =
            (addr, isWrite) => parentMm.HandleFaultDetailed(addr, isWrite, parentEngine) == FaultResult.Handled;

        const uint mapAddr = 0x47100000;
        Assert.Equal(mapAddr,
            parentMm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "anon-cow-private", parentEngine));

        Assert.True(parentEngine.CopyToUser(mapAddr, new[] { (byte)'P' }));
        var parentVma = Assert.Single(parentMm.VMAs);
        var pageIndex = parentVma.GetPageIndex(parentVma.Start);
        var parentPrivatePage = parentVma.VmAnonVma!.GetPage(pageIndex);
        Assert.NotEqual(IntPtr.Zero, parentPrivatePage);
        var allocatedBeforeClone = PageManager.GetAllocatedBytes();

        var childMm = parentMm.Clone();
        using var childEngine = parentEngine.Clone(false);
        childMm.RebuildExternalMappingsFromNative(childEngine, childMm.VMAs);
        ReprotectPrivateMappings(childMm, childEngine);
        childEngine.PageFaultResolver =
            (addr, isWrite) => childMm.HandleFaultDetailed(addr, isWrite, childEngine) == FaultResult.Handled;

        var childVma = Assert.Single(childMm.VMAs);
        Assert.NotSame(parentVma.VmAnonVma, childVma.VmAnonVma);
        Assert.Equal(parentPrivatePage, childVma.VmAnonVma!.GetPage(childVma.GetPageIndex(childVma.Start)));
        Assert.Equal(allocatedBeforeClone, PageManager.GetAllocatedBytes());

        var parentRead = new byte[1];
        var childRead = new byte[1];
        Assert.True(parentEngine.CopyFromUser(mapAddr, parentRead));
        Assert.True(childEngine.CopyFromUser(mapAddr, childRead));
        Assert.Equal((byte)'P', parentRead[0]);
        Assert.Equal((byte)'P', childRead[0]);

        Assert.True(childEngine.CopyToUser(mapAddr, new[] { (byte)'C' }));

        Assert.True(parentEngine.CopyFromUser(mapAddr, parentRead));
        Assert.True(childEngine.CopyFromUser(mapAddr, childRead));
        Assert.Equal((byte)'P', parentRead[0]);
        Assert.Equal((byte)'C', childRead[0]);
        Assert.NotEqual(parentVma.VmAnonVma!.GetPage(pageIndex),
            childVma.VmAnonVma!.GetPage(childVma.GetPageIndex(childVma.Start)));
    }

    [Fact]
    public void ForkClone_PrivateAnonymous_AfterProtNoneEviction_ChildWriteStillSplitsCow()
    {
        using var pageScope = PageManager.BeginIsolatedScope();
        var runtime = new TestRuntimeFactory();
        using var parentEngine = runtime.CreateEngine();
        var parentMm = runtime.CreateAddressSpace();
        parentEngine.PageFaultResolver =
            (addr, isWrite) => parentMm.HandleFaultDetailed(addr, isWrite, parentEngine) == FaultResult.Handled;

        const uint mapAddr = 0x47500000;
        Assert.Equal(mapAddr,
            parentMm.Mmap(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "anon-cow-prot-none",
                parentEngine));

        Assert.True(parentEngine.CopyToUser(mapAddr, new[] { (byte)42 }));
        var parentVma = Assert.Single(parentMm.VMAs);
        var pageIndex = parentVma.GetPageIndex(parentVma.Start);
        var sharedPrivatePage = parentVma.VmAnonVma!.GetPage(pageIndex);
        Assert.NotEqual(IntPtr.Zero, sharedPrivatePage);
        Assert.True(parentMm.PageMapping.TryGet(mapAddr, out var mappedBeforeProtNone));
        Assert.Equal(sharedPrivatePage, mappedBeforeProtNone);

        Assert.Equal(0, parentMm.Mprotect(mapAddr, LinuxConstants.PageSize, Protection.None, parentEngine,
            out _));
        Assert.False(parentMm.PageMapping.TryGet(mapAddr, out _));

        using var childEngine = parentEngine.Clone(false);
        var childMm = parentMm.Clone();
        childEngine.PageFaultResolver =
            (addr, isWrite) => childMm.HandleFaultDetailed(addr, isWrite, childEngine) == FaultResult.Handled;

        childMm.RebuildExternalMappingsFromNative(childEngine, childMm.VMAs);
        Assert.False(childMm.PageMapping.TryGet(mapAddr, out _));

        Assert.Equal(0, childMm.Mprotect(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
            childEngine, out _));
        Assert.True(childEngine.CopyToUser(mapAddr, new[] { (byte)100 }));

        Assert.Equal(0, parentMm.Mprotect(mapAddr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
            parentEngine, out _));

        var parentRead = new byte[1];
        var childRead = new byte[1];
        Assert.True(parentEngine.CopyFromUser(mapAddr, parentRead));
        Assert.True(childEngine.CopyFromUser(mapAddr, childRead));
        Assert.Equal((byte)42, parentRead[0]);
        Assert.Equal((byte)100, childRead[0]);
        Assert.NotEqual(parentVma.VmAnonVma!.GetPage(pageIndex),
            Assert.Single(childMm.VMAs).VmAnonVma!.GetPage(pageIndex));
    }

    private static void ReprotectPrivateMappings(VMAManager mm, Engine engine)
    {
        foreach (var vma in mm.VMAs)
        {
            if ((vma.Flags & MapFlags.Private) == 0 || vma.Length == 0) continue;
            var readOnlyPerms = vma.Perms & ~Protection.Write;
            mm.ReprotectNativeMappings(engine, vma.Start, vma.Length, readOnlyPerms, false);
        }
    }

    private sealed class TestEnv : IDisposable
    {
        public TestEnv()
        {
            var runtime = new TestRuntimeFactory();
            ParentEngine = runtime.CreateEngine();
            ParentMm = runtime.CreateAddressSpace();
            ParentEngine.PageFaultResolver =
                (addr, isWrite) => ParentMm.HandleFaultDetailed(addr, isWrite, ParentEngine) == FaultResult.Handled;

            var fsType = new FileSystemType
            {
                Name = "tmpfs",
                Factory = static _ => new Tmpfs(),
                FactoryWithContext = static (_, memoryContext) => new Tmpfs(memoryContext: memoryContext)
            };
            var sb = fsType.CreateAnonymousFileSystem(runtime.MemoryContext).ReadSuper(fsType, 0, "cow-fork-clone",
                null);
            var mount = new Mount(sb, sb.Root) { Source = "tmpfs", FsType = "tmpfs", Options = "rw" };
            var dentry = new Dentry(FsName.FromString("cow.bin"), null, sb.Root, sb);
            sb.Root.Inode!.Create(dentry, 0x1A4, 0, 0);

            File = new LinuxFile(dentry, FileFlags.O_RDWR, mount);
            Inode = dentry.Inode!;
            Assert.Equal(1, Inode.WriteFromHost(null, File, new[] { (byte)'A' }, 0));
        }

        public Engine ParentEngine { get; }
        public VMAManager ParentMm { get; }
        public LinuxFile File { get; }
        public Inode Inode { get; }

        public void Dispose()
        {
            File.Close();
            ParentEngine.Dispose();
        }

        public void MapPrivate(uint addr)
        {
            var mapped = ParentMm.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed, File, 0, "fork-cow", ParentEngine);
            Assert.Equal(addr, mapped);
        }
    }
}
