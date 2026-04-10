using System.Runtime.InteropServices;
using System.Text.Json;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.VFS;
using Fiberish.X86.Native;
using Xunit;

namespace Fiberish.Tests.Core;

public class EngineMmuTransferTests
{
    private const uint CodeAddr = 0x00401000;
    private const uint CrossPageCodeAddr = CodeAddr + LinuxConstants.PageSize - 1;
    private static readonly byte[] SimpleCode = [0x90, 0x90];

    [Fact]
    public void DetachThenAttach_RestoresMappings_AndConsumesHandle()
    {
        using var engine = new Engine();
        const uint addr = 0x00400000;
        var perms = (byte)(Protection.Read | Protection.Write);
        var page = engine.AllocatePage(addr, perms);
        Assert.NotEqual(IntPtr.Zero, page);
        Marshal.WriteByte(page, 0x5A);

        var beforeIdentity = engine.CurrentMmuIdentity;
        var detached = engine.DetachMmu();
        Assert.NotEqual(beforeIdentity, engine.CurrentMmuIdentity);

        var read = new byte[1];
        Assert.False(engine.CopyFromUser(addr, read));

        engine.ReplaceMmu(detached);
        Assert.Equal(beforeIdentity, engine.CurrentMmuIdentity);

        Assert.True(engine.CopyFromUser(addr, read));
        Assert.Equal((byte)0x5A, read[0]);
        detached.Dispose();
        Assert.Throws<ObjectDisposedException>(() => engine.ReplaceMmu(detached));
    }

    [Fact]
    public void ShareMmuFrom_UsesSameMmuIdentity()
    {
        using var parent = new Engine();
        var sharedId = parent.CurrentMmuIdentity;
        Assert.Equal(1, Engine.GetAttachmentCount(sharedId));

        using (var child = new Engine())
        {
            child.ShareMmuFrom(parent);
            Assert.Equal(parent.CurrentMmuIdentity, child.CurrentMmuIdentity);
            Assert.Equal(2, Engine.GetAttachmentCount(sharedId));
        }

        Assert.Equal(1, Engine.GetAttachmentCount(sharedId));
    }

    [Fact]
    public void SharedClone_ReusesSharedCodeCache()
    {
        using var parent = new Engine();
        InstallSimpleCode(parent);
        WarmSimpleCode(parent);

        var parentStats = ReadCodeCacheStats(parent);
        Assert.True(parent.GetBlockCount() > 0);
        Assert.True(parentStats.BlockCacheSize > 0);

        using var child = parent.Clone(true);
        Assert.Equal(parent.CurrentMmuIdentity, child.CurrentMmuIdentity);
        Assert.Equal(parent.GetBlockCount(), child.GetBlockCount());
        Assert.Equal(parentStats.BlockCacheSize, ReadCodeCacheStats(child).BlockCacheSize);

        WarmSimpleCode(child);

        Assert.Equal(parent.GetBlockCount(), child.GetBlockCount());
        Assert.Equal(parentStats.BlockCacheSize, ReadCodeCacheStats(parent).BlockCacheSize);
    }

    [Fact]
    public void SharedClone_ResetAllCodeCache_InvalidatesSharedCore()
    {
        using var parent = new Engine();
        InstallSimpleCode(parent);
        WarmSimpleCode(parent);

        using var child = parent.Clone(true);
        Assert.True(ReadCodeCacheStats(parent).BlockCacheSize > 0);

        child.ResetAllCodeCache();

        Assert.Equal(0, ReadCodeCacheStats(parent).BlockCacheSize);
        Assert.Equal(0, ReadCodeCacheStats(child).BlockCacheSize);
    }

    [Fact]
    public void SharedClone_ResetAllCodeCache_ForcesParentToRehydrateBlocks()
    {
        using var parent = new Engine();
        InstallSimpleCode(parent);
        WarmSimpleCode(parent);

        using var child = parent.Clone(true);

        // Prime the engine-local lookup cache before clearing the shared block map.
        WarmSimpleCode(parent);
        Assert.True(ReadCodeCacheStats(parent).BlockCacheSize > 0);

        child.ResetAllCodeCache();
        Assert.Equal(0, ReadCodeCacheStats(parent).BlockCacheSize);

        WarmSimpleCode(parent);

        Assert.True(ReadCodeCacheStats(parent).BlockCacheSize > 0);
    }

    [Fact]
    public void SharedClone_ResetCodeCacheByRange_AndMemUnmap_InvalidateSharedCore()
    {
        using var parent = new Engine();
        InstallSimpleCode(parent);
        WarmSimpleCode(parent);

        using var child = parent.Clone(true);
        child.ResetCodeCacheByRange(CodeAddr, 1);
        Assert.Equal(0, ReadCodeCacheStats(parent).BlockCacheSize);

        WarmSimpleCode(parent);
        Assert.True(ReadCodeCacheStats(child).BlockCacheSize > 0);

        child.MemUnmap(CodeAddr, LinuxConstants.PageSize);

        Assert.Equal(0, ReadCodeCacheStats(parent).BlockCacheSize);
        Assert.False(parent.HasMappedPage(CodeAddr, 1));
        Assert.False(child.HasMappedPage(CodeAddr, 1));
    }

    [Fact]
    public void ResetCodeCacheByRange_ReusesInvalidatedBlock_WhenCodeUnchanged()
    {
        using var engine = new Engine();
        InstallSimpleCode(engine);
        WarmSimpleCode(engine);

        var initialBlockCount = engine.GetBlockCount();
        Assert.True(initialBlockCount > 0);

        engine.ResetCodeCacheByRange(CodeAddr, 1);
        Assert.Equal(0, ReadCodeCacheStats(engine).BlockCacheSize);

        WarmSimpleCode(engine);

        Assert.Equal(initialBlockCount, engine.GetBlockCount());
        Assert.Equal(1, ReadCodeCacheStats(engine).BlockCacheSize);
    }

    [Fact]
    public void ResetCodeCacheByRange_OnTrailingPage_InvalidatesCrossPageBlock()
    {
        using var engine = new Engine();
        InstallCrossPageCode(engine);
        WarmSimpleCode(engine, CrossPageCodeAddr);

        var initialBlockCount = engine.GetBlockCount();
        Assert.True(initialBlockCount > 0);
        Assert.Equal(1, ReadCodeCacheStats(engine).BlockCacheSize);

        engine.ResetCodeCacheByRange(CrossPageCodeAddr + 1, 1);
        Assert.Equal(0, ReadCodeCacheStats(engine).BlockCacheSize);

        WarmSimpleCode(engine, CrossPageCodeAddr);

        Assert.Equal(initialBlockCount, engine.GetBlockCount());
        Assert.Equal(1, ReadCodeCacheStats(engine).BlockCacheSize);
    }

    [Fact]
    public void DetachAndReattach_PreservesOriginalCoreCodeCache()
    {
        using var engine = new Engine();
        InstallSimpleCode(engine);
        WarmSimpleCode(engine);

        var warmedBlockCount = engine.GetBlockCount();
        var warmedStats = ReadCodeCacheStats(engine);
        Assert.True(warmedBlockCount > 0);
        Assert.True(warmedStats.BlockCacheSize > 0);

        using var detached = engine.DetachMmu();
        Assert.Equal(0, engine.GetBlockCount());
        Assert.Equal(0, ReadCodeCacheStats(engine).BlockCacheSize);

        engine.ReplaceMmu(detached);

        Assert.Equal(warmedBlockCount, engine.GetBlockCount());
        Assert.Equal(warmedStats.BlockCacheSize, ReadCodeCacheStats(engine).BlockCacheSize);
    }

    [Fact]
    public void ForkedOrClonedMmu_StartsWithEmptyCodeCache()
    {
        using var parent = new Engine();
        InstallSimpleCode(parent);
        WarmSimpleCode(parent);
        Assert.True(parent.GetBlockCount() > 0);

        using (var forked = parent.Clone(false))
        {
            Assert.NotEqual(parent.CurrentMmuIdentity, forked.CurrentMmuIdentity);
            Assert.Equal(0, forked.GetBlockCount());
            Assert.Equal(0, ReadCodeCacheStats(forked).BlockCacheSize);
            WarmSimpleCode(forked);
            Assert.True(forked.GetBlockCount() > 0);
        }

        using var freshEngine = new Engine();
        using var cloned = parent.CurrentMmu.CloneSkipExternal();
        freshEngine.ReplaceMmu(cloned);
        Assert.Equal(0, freshEngine.GetBlockCount());
        Assert.Equal(0, ReadCodeCacheStats(freshEngine).BlockCacheSize);
    }

    [Fact]
    public void DisposingSharedPeer_DoesNotCorruptSharedCodeCache()
    {
        using var parent = new Engine();
        InstallSimpleCode(parent);
        WarmSimpleCode(parent);
        var warmedBlockCount = parent.GetBlockCount();

        using (var child = parent.Clone(true))
        {
            WarmSimpleCode(child);
            Assert.Equal(warmedBlockCount, child.GetBlockCount());
        }

        WarmSimpleCode(parent);

        Assert.Equal(EmuStatus.Stopped, parent.Status);
        Assert.Equal(warmedBlockCount, parent.GetBlockCount());
        Assert.True(ReadCodeCacheStats(parent).BlockCacheSize > 0);
    }

    [Fact]
    public void SharedClone_ResetMemory_ClearsMappingsAndSharedCodeCache()
    {
        using var parent = new Engine();
        InstallSimpleCode(parent);
        WarmSimpleCode(parent);

        using var child = parent.Clone(true);
        child.ResetMemory();

        Assert.Equal(0, ReadCodeCacheStats(parent).BlockCacheSize);
        Assert.False(parent.HasMappedPage(CodeAddr, 1));
        Assert.False(child.HasMappedPage(CodeAddr, 1));
    }

    [Fact]
    public void AddressSpaceHandle_BindsSharedClone_AndRejectsForkedMmu()
    {
        using var parent = new Engine();
        var mm = new VMAManager();
        mm.BindOrAssertAddressSpaceHandle(parent);

        using var shared = parent.Clone(true);
        mm.BindOrAssertAddressSpaceHandle(shared);

        using var forked = parent.Clone(false);
        var ex = Assert.Throws<InvalidOperationException>(() => mm.BindOrAssertAddressSpaceHandle(forked));
        Assert.Contains("does not match address-space MMU identity", ex.Message);
    }

    [Fact]
    public void AddressSpaceHandle_DetachFromSharedEngine_CreatesNewIdentity()
    {
        using var parent = new Engine();
        var sharedMm = new VMAManager();
        sharedMm.BindOrAssertAddressSpaceHandle(parent);
        var sharedIdentity = sharedMm.AddressSpaceIdentity;

        using var child = parent.Clone(true);
        Assert.Equal(sharedIdentity, child.CurrentMmuIdentity);

        var detachedMm = new VMAManager();
        detachedMm.BindAddressSpaceHandle(ProcessAddressSpaceHandle.DetachFromSharedEngine(child));

        Assert.Equal(detachedMm.AddressSpaceIdentity, child.CurrentMmuIdentity);
        Assert.NotEqual(sharedIdentity, detachedMm.AddressSpaceIdentity);
        Assert.Equal(sharedIdentity, parent.CurrentMmuIdentity);
    }

    private static void InstallSimpleCode(Engine engine)
    {
        InstallSimpleCode(engine, CodeAddr);
    }

    private static void InstallSimpleCode(Engine engine, uint addr)
    {
        var perms = (byte)(Protection.Read | Protection.Write | Protection.Exec);
        var page = engine.AllocatePage(addr, perms);
        Assert.NotEqual(IntPtr.Zero, page);
        Marshal.Copy(SimpleCode, 0, page, SimpleCode.Length);
    }

    private static void InstallCrossPageCode(Engine engine)
    {
        var perms = (byte)(Protection.Read | Protection.Write | Protection.Exec);
        var firstPage = engine.AllocatePage(CodeAddr, perms);
        var secondPage = engine.AllocatePage(CodeAddr + LinuxConstants.PageSize, perms);
        Assert.NotEqual(IntPtr.Zero, firstPage);
        Assert.NotEqual(IntPtr.Zero, secondPage);

        Marshal.WriteByte(firstPage, LinuxConstants.PageSize - 1, SimpleCode[0]);
        Marshal.WriteByte(secondPage, 0, SimpleCode[1]);
    }

    private static void WarmSimpleCode(Engine engine)
    {
        WarmSimpleCode(engine, CodeAddr);
    }

    private static void WarmSimpleCode(Engine engine, uint addr)
    {
        engine.Eip = addr;
        engine.Run(addr + (uint)SimpleCode.Length, 16);
        Assert.Equal(EmuStatus.Stopped, engine.Status);
        Assert.Equal(addr + (uint)SimpleCode.Length, engine.Eip);
    }

    private static CodeCacheStats ReadCodeCacheStats(Engine engine)
    {
        var json = engine.DumpStats();
        Assert.False(string.IsNullOrEmpty(json));

        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;
        return new CodeCacheStats(
            root.GetProperty("all_blocks_count").GetInt32(),
            root.GetProperty("block_cache_size").GetInt32(),
            root.GetProperty("page_to_blocks_size").GetInt32());
    }

    private readonly record struct CodeCacheStats(int AllBlocksCount, int BlockCacheSize, int PageToBlocksSize);

    private sealed class MappedPageFixture : IDisposable
    {
        private readonly Dentry _fileDentry;
        private readonly Mount _mount;
        private readonly Dentry _root;
        private readonly TestSuperBlock _sb;

        public MappedPageFixture()
        {
            _sb = new TestSuperBlock();
            var rootInode = new TestInode(_sb);
            _root = new Dentry(FsName.Empty, rootInode, null, _sb);
            _sb.Root = _root;

            Inode = new TrackingMappedPageInode(_sb);
            _fileDentry = new Dentry(FsName.FromBytes("mapped"u8), Inode, _root, _sb);
            _mount = new Mount(_sb, _root);
            File = new LinuxFile(_fileDentry, FileFlags.O_RDONLY, _mount);
            Vma = new VmArea
            {
                Start = 0x40000000,
                End = 0x40001000,
                Perms = Protection.Read,
                Flags = MapFlags.Shared,
                FileMapping = new VmaFileMapping(File),
                Offset = 0,
                VmPgoff = 0,
                Name = "mapped-test",
                VmMapping = null,
                VmAnonVma = null
            };
        }

        public TrackingMappedPageInode Inode { get; }
        public LinuxFile File { get; }
        public VmArea Vma { get; }

        public void Dispose()
        {
            Vma.FileMapping?.Release();
            _mount.Put();
            Inode.DisposePage();
        }
    }

    private sealed class TrackingMappedPageInode : TestInode
    {
        private readonly IntPtr _ptr = Marshal.AllocHGlobal(LinuxConstants.PageSize);

        public TrackingMappedPageInode(SuperBlock sb) : base(sb)
        {
        }

        public int DisposeCount { get; private set; }

        public void DisposePage()
        {
            Marshal.FreeHGlobal(_ptr);
        }

        public override bool TryAcquireMappedPageHandle(LinuxFile? linuxFile, long pageIndex, long absoluteFileOffset,
            bool writable, out PageHandle pageHandle)
        {
            _ = linuxFile;
            _ = pageIndex;
            _ = absoluteFileOffset;
            _ = writable;
            pageHandle = PageHandle.CreateOwned(_ptr, this, 1);
            return true;
        }

        protected internal override void ReleaseMappedPageHandle(long releaseToken)
        {
            Assert.Equal(1, releaseToken);
            DisposeCount++;
        }
    }

    private sealed class TestSuperBlock : SuperBlock
    {
    }

    private class TestInode : Inode
    {
        public TestInode(SuperBlock sb)
        {
            SuperBlock = sb;
            Type = InodeType.File;
            Mode = 0x1A4;
            sb.EnsureInodeTracked(this);
        }

        public override bool SupportsMmap => true;
    }
}
