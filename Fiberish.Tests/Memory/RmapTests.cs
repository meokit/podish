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
        using var pageScope = ExternalPageManager.BeginIsolatedScope();
        using var cacheScope = GlobalAddressSpaceCacheManager.BeginIsolatedScope();
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
        using var pageScope = ExternalPageManager.BeginIsolatedScope();
        using var cacheScope = GlobalAddressSpaceCacheManager.BeginIsolatedScope();
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

            var fileHitsBeforeCow = ResolveHits(filePtr);
            Assert.Single(fileHitsBeforeCow);
            Assert.Same(vma, fileHitsBeforeCow[0].Vma);
            Assert.Equal(HostPageOwnerKind.AddressSpace, fileHitsBeforeCow[0].OwnerKind);

            Assert.True(mm.HandleFault(addr, true, engine));
            var anonPtr = vma.VmAnonVma!.PeekPage(pageIndex);
            Assert.NotEqual(IntPtr.Zero, anonPtr);
            Assert.NotEqual(filePtr, anonPtr);

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
        using var pageScope = ExternalPageManager.BeginIsolatedScope();
        using var cacheScope = GlobalAddressSpaceCacheManager.BeginIsolatedScope();
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
        using var pageScope = ExternalPageManager.BeginIsolatedScope();
        using var cacheScope = GlobalAddressSpaceCacheManager.BeginIsolatedScope();
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
            Dentry = new Dentry("data.bin", null, _root, _superBlock);
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
