using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Memory;

public class RmapTests
{
    [Fact]
    public void SharedFilePage_RmapReturnsAllHoldingVmas()
    {
        using var pageScope = PageManager.BeginIsolatedScope();
        using var cacheScope = AddressSpacePolicy.BeginIsolatedScope();
        using var fixture = new TmpfsFileFixture(new byte[LinuxConstants.PageSize]);
        using var engineA = new Engine();
        using var engineB = new Engine();
        var mmA = new VMAManager();
        var mmB = new VMAManager();
        var fileA = fixture.Open();
        var fileB = fixture.Open();

        try
        {
            const uint addrA = 0x50000000;
            const uint addrB = 0x51000000;
            mmA.Mmap(addrA, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, fileA, 0, "MAP_SHARED_A", engineA);
            mmB.Mmap(addrB, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, fileB, 0, "MAP_SHARED_B", engineB);

            Assert.True(mmA.HandleFault(addrA, false, engineA));
            Assert.True(mmB.HandleFault(addrB, false, engineB));

            var vmaA = Assert.IsType<VmArea>(mmA.FindVmArea(addrA));
            var vmaB = Assert.IsType<VmArea>(mmB.FindVmArea(addrB));
            var pageIndexA = vmaA.GetPageIndex(addrA);
            var filePtr = vmaA.VmMapping!.PeekPage(pageIndexA);
            Assert.NotEqual(IntPtr.Zero, filePtr);

            var hits = ResolveHits(filePtr);

            Assert.Equal(2, hits.Count);
            Assert.Contains(hits, hit => ReferenceEquals(hit.Mm, mmA) && ReferenceEquals(hit.Vma, vmaA));
            Assert.Contains(hits, hit => ReferenceEquals(hit.Mm, mmB) && ReferenceEquals(hit.Vma, vmaB));
            Assert.All(hits, hit => Assert.Equal(HostPageOwnerKind.AddressSpace, hit.OwnerKind));
        }
        finally
        {
            fileA.Close();
            fileB.Close();
        }
    }

    [Fact]
    public void PrivateFilePage_CowMovesRmapFromFilePageToAnonPage()
    {
        using var pageScope = PageManager.BeginIsolatedScope();
        using var cacheScope = AddressSpacePolicy.BeginIsolatedScope();
        using var fixture = new TmpfsFileFixture("hello"u8.ToArray());
        using var engine = new Engine();
        var mm = new VMAManager();
        var file = fixture.Open();
        try
        {
            const uint addr = 0x52000000;
            mm.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
                MapFlags.Private | MapFlags.Fixed, file, 0, "MAP_PRIVATE", engine);

            Assert.True(mm.HandleFault(addr, false, engine));
            var vma = Assert.IsType<VmArea>(mm.FindVmArea(addr));
            var pageIndex = vma.GetPageIndex(addr);
            var filePtr = vma.VmMapping!.PeekPage(pageIndex);
            Assert.NotEqual(IntPtr.Zero, filePtr);
            var filePage = HostPageManager.GetRequired(filePtr);
            Assert.Equal(HostPageOwnerKind.AddressSpace, filePage.OwnerRootKindForDebug);
            Assert.Same(vma.VmMapping, filePage.OwnerAddressSpaceForDebug);
            Assert.Equal(pageIndex, filePage.OwnerPageIndexForDebug);

            var fileHitsBeforeCow = ResolveHits(filePtr);
            Assert.Single(fileHitsBeforeCow);
            Assert.Same(vma, fileHitsBeforeCow[0].Vma);
            Assert.Equal(HostPageOwnerKind.AddressSpace, fileHitsBeforeCow[0].OwnerKind);

            Assert.True(mm.HandleFault(addr, true, engine));
            var anonPtr = vma.VmAnonVma!.PeekPage(pageIndex);
            Assert.NotEqual(IntPtr.Zero, anonPtr);
            Assert.NotEqual(filePtr, anonPtr);
            var anonPage = HostPageManager.GetRequired(anonPtr);
            Assert.Equal(HostPageOwnerKind.AnonVma, anonPage.OwnerRootKindForDebug);
            Assert.Same(vma.VmAnonVma.Root, anonPage.OwnerAnonRootForDebug);
            Assert.Equal(pageIndex, anonPage.OwnerPageIndexForDebug);

            var fileHitsAfterCow = ResolveHits(filePtr);
            Assert.Empty(fileHitsAfterCow);

            var anonHits = ResolveHits(anonPtr);
            Assert.Single(anonHits);
            Assert.Same(vma, anonHits[0].Vma);
            Assert.Equal(HostPageOwnerKind.AnonVma, anonHits[0].OwnerKind);
        }
        finally
        {
            file.Close();
        }
    }

    [Fact]
    public void ForkSharedPrivatePage_RmapTracksParentAndChildUntilCow()
    {
        using var pageScope = PageManager.BeginIsolatedScope();
        using var cacheScope = AddressSpacePolicy.BeginIsolatedScope();
        using var parentEngine = new Engine();
        using var childEngine = new Engine();
        var parentMm = new VMAManager();

        const uint addr = 0x53000000;
        parentMm.Mmap(addr, LinuxConstants.PageSize, Protection.Read | Protection.Write,
            MapFlags.Private | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "MAP_PRIVATE_ANON", parentEngine);
        Assert.True(parentMm.HandleFault(addr, true, parentEngine));

        var parentVma = Assert.IsType<VmArea>(parentMm.FindVmArea(addr));
        var pageIndex = parentVma.GetPageIndex(addr);
        var sharedPtr = parentVma.VmAnonVma!.PeekPage(pageIndex);
        Assert.NotEqual(IntPtr.Zero, sharedPtr);

        var childMm = parentMm.Clone();
        var childVma = Assert.IsType<VmArea>(childMm.FindVmArea(addr));
        Assert.NotSame(parentVma.VmAnonVma, childVma.VmAnonVma);
        Assert.Same(parentVma.VmAnonVma!.Root, childVma.VmAnonVma!.Root);
        var sharedPage = HostPageManager.GetRequired(sharedPtr);
        Assert.Equal(HostPageOwnerKind.AnonVma, sharedPage.OwnerRootKindForDebug);
        Assert.Same(parentVma.VmAnonVma.Root, sharedPage.OwnerAnonRootForDebug);
        Assert.Equal(pageIndex, sharedPage.OwnerPageIndexForDebug);

        var sharedHits = ResolveHits(sharedPtr);
        Assert.Equal(2, sharedHits.Count);
        Assert.Contains(sharedHits, hit => ReferenceEquals(hit.Mm, parentMm) && ReferenceEquals(hit.Vma, parentVma));
        Assert.Contains(sharedHits, hit => ReferenceEquals(hit.Mm, childMm) && ReferenceEquals(hit.Vma, childVma));

        Assert.True(childMm.HandleFault(addr, true, childEngine));
        var childPtr = childVma.VmAnonVma!.PeekPage(pageIndex);
        Assert.NotEqual(IntPtr.Zero, childPtr);
        Assert.NotEqual(sharedPtr, childPtr);

        var parentHits = ResolveHits(sharedPtr);
        Assert.Single(parentHits);
        Assert.Same(parentVma, parentHits[0].Vma);

        var childHits = ResolveHits(childPtr);
        Assert.Single(childHits);
        Assert.Same(childVma, childHits[0].Vma);
    }

    [Fact]
    public void MprotectSplit_UpdatesRmapToNewVmaFragment()
    {
        using var pageScope = PageManager.BeginIsolatedScope();
        using var cacheScope = AddressSpacePolicy.BeginIsolatedScope();
        using var fixture = new TmpfsFileFixture(new byte[LinuxConstants.PageSize * 2]);
        using var engine = new Engine();
        var mm = new VMAManager();
        var file = fixture.Open();
        try
        {
            const uint addr = 0x54000000;
            var secondPageAddr = addr + LinuxConstants.PageSize;
            mm.Mmap(addr, LinuxConstants.PageSize * 2, Protection.Read | Protection.Write,
                MapFlags.Shared | MapFlags.Fixed, file, 0, "MAP_SHARED_2P", engine);
            Assert.True(mm.HandleFault(secondPageAddr, false, engine));

            var originalVma = Assert.IsType<VmArea>(mm.FindVmArea(secondPageAddr));
            Assert.Equal(addr, originalVma.Start);
            Assert.Equal(addr + LinuxConstants.PageSize * 2u, originalVma.End);
            var secondPageIndex = originalVma.GetPageIndex(secondPageAddr);
            var secondPagePtr = originalVma.VmMapping!.PeekPage(secondPageIndex);
            Assert.NotEqual(IntPtr.Zero, secondPagePtr);

            Assert.Equal(0, mm.Mprotect(secondPageAddr, LinuxConstants.PageSize, Protection.Read, engine, out _));

            var hits = ResolveHits(secondPagePtr);
            var hit = Assert.Single(hits);
            Assert.Equal(secondPageAddr, hit.Vma.Start);
            Assert.Equal(secondPageAddr + LinuxConstants.PageSize, hit.Vma.End);
        }
        finally
        {
            file.Close();
        }
    }

    [Fact]
    public void MprotectSplit_RebuildsDirectRefsOnlyForResidentPages()
    {
        using var pageScope = PageManager.BeginIsolatedScope();
        using var engine = new Engine();
        var mm = new VMAManager();

        const uint addr = 0x55000000;
        var secondPageAddr = addr + LinuxConstants.PageSize;
        mm.Mmap(addr, LinuxConstants.PageSize * 2, Protection.Read | Protection.Write,
            MapFlags.Shared | MapFlags.Fixed | MapFlags.Anonymous, null, 0, "MAP_SHARED_ANON_2P", engine);
        Assert.True(mm.HandleFault(secondPageAddr, false, engine));

        var originalSecondVma = Assert.IsType<VmArea>(mm.FindVmArea(secondPageAddr));
        var secondPageIndex = originalSecondVma.GetPageIndex(secondPageAddr);
        Assert.Equal(IntPtr.Zero, originalSecondVma.VmMapping!.PeekPage(originalSecondVma.GetPageIndex(addr)));
        var secondPagePtr = originalSecondVma.VmMapping.PeekPage(secondPageIndex);
        Assert.NotEqual(IntPtr.Zero, secondPagePtr);

        Assert.Equal(0, mm.Mprotect(secondPageAddr, LinuxConstants.PageSize, Protection.Read, engine, out _));

        var secondHitsAfterSplit = ResolveHits(secondPagePtr);
        var secondHit = Assert.Single(secondHitsAfterSplit);
        Assert.Same(mm, secondHit.Mm);
        Assert.Equal(secondPageAddr, secondHit.Vma.Start);
        Assert.Equal(secondPageAddr + LinuxConstants.PageSize, secondHit.Vma.End);

        Assert.True(mm.HandleFault(addr, false, engine));
        var firstVma = Assert.IsType<VmArea>(mm.FindVmArea(addr));
        var firstPagePtr = firstVma.VmMapping!.PeekPage(firstVma.GetPageIndex(addr));
        Assert.NotEqual(IntPtr.Zero, firstPagePtr);

        var firstHits = ResolveHits(firstPagePtr);
        var firstHit = Assert.Single(firstHits);
        Assert.Same(mm, firstHit.Mm);
        Assert.Equal(addr, firstHit.Vma.Start);
        Assert.Equal(addr + LinuxConstants.PageSize, firstHit.Vma.End);

        secondHitsAfterSplit = ResolveHits(secondPagePtr);
        Assert.Single(secondHitsAfterSplit);
    }

    [Fact]
    public void HostPageSlot_ReusesSlotWithNewGeneration_AndStaleHostPageCannotLeakTbCohState()
    {
        using var pageScope = PageManager.BeginIsolatedScope();

        var handle1 = PageManager.AllocAnonPage(AllocationClass.KernelInternal);
        var ptr1 = handle1.Pointer;
        Assert.NotEqual(IntPtr.Zero, ptr1);
        var anonRoot1 = new AnonVma();

        HostPageOwnerBinding owner1 = new()
        {
            OwnerKind = HostPageOwnerKind.AnonVma,
            AnonVmaRoot = anonRoot1.Root,
            PageIndex = 1
        };

        var page1 = HostPageManager.CreateWithBacking(ref handle1, HostPageKind.Anon);
        Assert.True(page1.BindOwnerRoot(owner1));
        Assert.True(page1.HasOwnerRoot);
        Assert.Equal(HostPageOwnerKind.AnonVma, page1.OwnerRootKindForDebug);
        Assert.Same(anonRoot1.Root, page1.OwnerAnonRootForDebug);
        Assert.Equal(1u, page1.OwnerPageIndexForDebug);
        var slotIndex1 = page1.SlotIndexForDebug;
        var generation1 = page1.HandleGenerationForDebug;

        var mm = new VMAManager();
        var vma = new VmArea
        {
            Start = 0x1000,
            End = 0x2000,
            Perms = Protection.Read | Protection.Write | Protection.Exec,
            VmAnonVma = anonRoot1
        };
        Assert.True(page1.AddOrUpdateRmapRef(mm, vma, HostPageOwnerKind.AnonVma, 1, 0x1000));
        Assert.Equal(TbCohApplyKind.SlowScan, HostPageManager.ApplyTbCohPolicyIfChanged(ptr1).Kind);
        Assert.True(page1.RemoveRmapRef(mm, vma, HostPageOwnerKind.AnonVma, 1));

        Assert.True(page1.UnbindOwnerRoot(owner1));
        Assert.False(HostPageManager.TryLookup(ptr1, out _));
        Assert.False(page1.HasOwnerRoot);
        Assert.Null(page1.OwnerRootKindForDebug);
        Assert.Null(page1.OwnerAnonRootForDebug);

        var handle2 = PageManager.AllocatePoolBackedPage(AllocationClass.KernelInternal);
        var ptr2 = handle2.Pointer;
        Assert.NotEqual(IntPtr.Zero, ptr2);
        var mapping2 = new AddressSpace(AddressSpaceKind.File);

        HostPageOwnerBinding owner2 = new()
        {
            OwnerKind = HostPageOwnerKind.AddressSpace,
            Mapping = mapping2,
            PageIndex = 2
        };

        var page2 = HostPageManager.CreateWithBacking(ref handle2, HostPageKind.PageCache);
        Assert.True(page2.BindOwnerRoot(owner2));
        Assert.True(page2.HasOwnerRoot);
        Assert.Equal(slotIndex1, page2.SlotIndexForDebug);
        Assert.NotEqual(generation1, page2.HandleGenerationForDebug);
        Assert.Equal(HostPageOwnerKind.AddressSpace, page2.OwnerRootKindForDebug);
        Assert.Same(mapping2, page2.OwnerAddressSpaceForDebug);
        Assert.Equal(2u, page2.OwnerPageIndexForDebug);
        Assert.Equal(TbCohApplyKind.FastNoWriters, HostPageManager.ApplyTbCohPolicyIfChanged(ptr2).Kind);

        Assert.False(page1.BindOwnerRoot(new HostPageOwnerBinding
        {
            OwnerKind = HostPageOwnerKind.AnonVma,
            AnonVmaRoot = anonRoot1.Root,
            PageIndex = 99
        }));

        Assert.False(page1.HasOwnerRoot);
        Assert.True(page2.HasOwnerRoot);
        Assert.Same(mapping2, page2.OwnerAddressSpaceForDebug);
        Assert.Equal(2u, page2.OwnerPageIndexForDebug);

        Assert.True(page2.UnbindOwnerRoot(owner2));
        mapping2.Release();
        anonRoot1.Release();
    }

    private static List<RmapHit> ResolveHits(IntPtr ptr)
    {
        var hits = new List<RmapHit>();
        VmRmap.ResolveHostPageHolders(ptr, hits);
        return hits;
    }

    private sealed class TmpfsFileFixture : IDisposable
    {
        private readonly SuperBlock _superBlock;
        private readonly Dentry _root;
        public TmpfsFileFixture(byte[] contents)
        {
            var fsType = new FileSystemType { Name = "tmpfs", Factory = static _ => new Tmpfs() };
            _superBlock = fsType.CreateAnonymousFileSystem().ReadSuper(fsType, 0, "tmp", null);
            _root = _superBlock.Root;
            Dentry = new Dentry(FsName.FromString("data.bin"), null, _root, _superBlock);
            _root.Inode!.Create(Dentry, 0x1B6, 0, 0);

            var file = Open();
            try
            {
                Assert.Equal(contents.Length, Dentry.Inode!.WriteFromHost(null, file, contents, 0));
            }
            finally
            {
                file.Close();
            }
        }

        public Dentry Dentry { get; }

        public LinuxFile Open()
        {
            return new LinuxFile(Dentry, FileFlags.O_RDWR, null!);
        }

        public void Dispose()
        {
        }
    }
}
