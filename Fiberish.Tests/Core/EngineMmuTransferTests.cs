using System.Runtime.InteropServices;
using System.Reflection;
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
    private const uint AliasCodeAddrA = 0x00402000;
    private const uint AliasCodeAddrB = 0x00406000;
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
    public void CloneMmu_DropsExternalMappings_ButKeepsOwnedPages()
    {
        using var parent = new Engine();
        using var child = new Engine();
        const uint externalAddr = 0x00500000;
        const uint ownedAddr = 0x00600000;
        var perms = (byte)(Protection.Read | Protection.Write);
        var external = Marshal.AllocHGlobal(LinuxConstants.PageSize);

        try
        {
            var init = new byte[LinuxConstants.PageSize];
            init[0] = 0x11;
            Marshal.Copy(init, 0, external, init.Length);
            Assert.True(parent.MapExternalPage(externalAddr, external, perms));

            var owned = parent.AllocatePage(ownedAddr, perms);
            Assert.NotEqual(IntPtr.Zero, owned);
            Marshal.WriteByte(owned, 0x22);

            using var cloned = parent.CurrentMmu.CloneSkipExternal();
            child.ReplaceMmu(cloned);

            var read = new byte[1];
            Assert.False(child.CopyFromUser(externalAddr, read));
            Assert.True(child.CopyFromUser(ownedAddr, read));
            Assert.Equal((byte)0x22, read[0]);
        }
        finally
        {
            Marshal.FreeHGlobal(external);
        }
    }

    [Fact]
    public void CloneFork_PreservesExternalMappings_ByDefault()
    {
        using var parent = new Engine();
        const uint externalAddr = 0x00700000;
        const uint ownedAddr = 0x00800000;
        var perms = (byte)(Protection.Read | Protection.Write);
        var external = Marshal.AllocHGlobal(LinuxConstants.PageSize);

        try
        {
            var init = new byte[LinuxConstants.PageSize];
            init[0] = 0x44;
            Marshal.Copy(init, 0, external, init.Length);
            Assert.True(parent.MapExternalPage(externalAddr, external, perms));

            var owned = parent.AllocatePage(ownedAddr, perms);
            Assert.NotEqual(IntPtr.Zero, owned);
            Marshal.WriteByte(owned, 0x66);

            using var child = parent.Clone(false);
            Assert.NotEqual(parent.CurrentMmuIdentity, child.CurrentMmuIdentity);

            var read = new byte[1];
            Assert.True(child.CopyFromUser(externalAddr, read));
            Assert.Equal((byte)0x44, read[0]);
            Assert.True(child.CopyFromUser(ownedAddr, read));
            Assert.Equal((byte)0x66, read[0]);
        }
        finally
        {
            Marshal.FreeHGlobal(external);
        }
    }

    [Fact]
    public void CloneForkWithSkipMmu_AllowsManualCloneAttachFlow()
    {
        using var parent = new Engine();
        const uint externalAddr = 0x00900000;
        const uint ownedAddr = 0x00A00000;
        var perms = (byte)(Protection.Read | Protection.Write);
        var external = Marshal.AllocHGlobal(LinuxConstants.PageSize);

        try
        {
            var init = new byte[LinuxConstants.PageSize];
            init[0] = 0x7A;
            Marshal.Copy(init, 0, external, init.Length);
            Assert.True(parent.MapExternalPage(externalAddr, external, perms));

            var owned = parent.AllocatePage(ownedAddr, perms);
            Assert.NotEqual(IntPtr.Zero, owned);
            Marshal.WriteByte(owned, 0x3C);

            using var child = parent.Clone(false, EngineCloneMemoryMode.SkipMmu);
            var read = new byte[1];
            Assert.False(child.CopyFromUser(externalAddr, read));
            Assert.False(child.CopyFromUser(ownedAddr, read));

            using var cloned = parent.CurrentMmu.CloneSkipExternal();
            child.ReplaceMmu(cloned);

            Assert.False(child.CopyFromUser(externalAddr, read));
            Assert.True(child.CopyFromUser(ownedAddr, read));
            Assert.Equal((byte)0x3C, read[0]);
        }
        finally
        {
            Marshal.FreeHGlobal(external);
        }
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
    public void ExternalAlias_ResetCodeCacheByRange_InvalidatesAllAliasBlocks()
    {
        using var engine = new Engine();
        var external = Marshal.AllocHGlobal(LinuxConstants.PageSize);

        try
        {
            InstallExternalCodeAlias(engine, external, AliasCodeAddrA, AliasCodeAddrB);
            WarmSimpleCode(engine, AliasCodeAddrA);
            WarmSimpleCode(engine, AliasCodeAddrB);

            Assert.Equal(2, ReadCodeCacheStats(engine).BlockCacheSize);

            engine.ResetCodeCacheByRange(AliasCodeAddrA, 1);

            var stats = ReadCodeCacheStats(engine);
            Assert.Equal(0, stats.BlockCacheSize);
            Assert.Equal(0, stats.PageToBlocksSize);
        }
        finally
        {
            Marshal.FreeHGlobal(external);
        }
    }

    [Fact]
    public void ExternalAlias_MemUnmap_InvalidatesPeerAliasBlocks()
    {
        using var engine = new Engine();
        var external = Marshal.AllocHGlobal(LinuxConstants.PageSize);

        try
        {
            InstallExternalCodeAlias(engine, external, AliasCodeAddrA, AliasCodeAddrB);
            WarmSimpleCode(engine, AliasCodeAddrA);
            WarmSimpleCode(engine, AliasCodeAddrB);

            engine.MemUnmap(AliasCodeAddrA, LinuxConstants.PageSize);

            Assert.False(engine.HasMappedPage(AliasCodeAddrA, 1));
            Assert.True(engine.HasMappedPage(AliasCodeAddrB, 1));
            Assert.Equal(0, ReadCodeCacheStats(engine).BlockCacheSize);

            WarmSimpleCode(engine, AliasCodeAddrB);
            Assert.Equal(1, ReadCodeCacheStats(engine).BlockCacheSize);
        }
        finally
        {
            Marshal.FreeHGlobal(external);
        }
    }

    [Fact]
    public void RemappingSameGuestPageToDifferentHostPage_RebuildsCodeCacheEntry()
    {
        using var engine = new Engine();
        var external1 = Marshal.AllocHGlobal(LinuxConstants.PageSize);
        var external2 = Marshal.AllocHGlobal(LinuxConstants.PageSize);

        try
        {
            InstallExternalCode(engine, external1, AliasCodeAddrA);
            WarmSimpleCode(engine, AliasCodeAddrA);
            var initialBlockCount = engine.GetBlockCount();
            Assert.Equal(1, ReadCodeCacheStats(engine).BlockCacheSize);

            engine.MemUnmap(AliasCodeAddrA, LinuxConstants.PageSize);
            InstallExternalCode(engine, external2, AliasCodeAddrA);
            WarmSimpleCode(engine, AliasCodeAddrA);

            Assert.True(engine.GetBlockCount() > initialBlockCount);
            Assert.Equal(1, ReadCodeCacheStats(engine).BlockCacheSize);
        }
        finally
        {
            Marshal.FreeHGlobal(external1);
            Marshal.FreeHGlobal(external2);
        }
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

    [Fact]
    public void TryResolveMappedFilePage_DisposesHandle_WhenVmMappingMissing()
    {
        var method = typeof(VMAManager).GetMethod(
            "TryResolveMappedFilePage",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        using var fixture = new MappedPageFixture();
        var args = new object?[] { fixture.Vma, (uint)0, 0L, false, IntPtr.Zero };
        var resolved = Assert.IsType<bool>(method!.Invoke(null, args));

        Assert.False(resolved);
        Assert.Equal(1, fixture.Inode.DisposeCount);
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

    private static void InstallExternalCode(Engine engine, IntPtr externalPage, uint addr)
    {
        var init = new byte[LinuxConstants.PageSize];
        Array.Copy(SimpleCode, init, SimpleCode.Length);
        Marshal.Copy(init, 0, externalPage, init.Length);
        Assert.True(engine.MapExternalPage(addr, externalPage, (byte)(Protection.Read | Protection.Write | Protection.Exec)));
    }

    private static void InstallExternalCodeAlias(Engine engine, IntPtr externalPage, uint addrA, uint addrB)
    {
        InstallExternalCode(engine, externalPage, addrA);
        Assert.True(engine.MapExternalPage(addrB, externalPage, (byte)(Protection.Read | Protection.Write | Protection.Exec)));
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
        private readonly TestSuperBlock _sb;
        private readonly Mount _mount;
        private readonly Dentry _root;
        private readonly Dentry _fileDentry;

        public MappedPageFixture()
        {
            _sb = new TestSuperBlock();
            var rootInode = new TestInode(_sb);
            _root = new Dentry("/", rootInode, null, _sb);
            _sb.Root = _root;

            Inode = new TrackingMappedPageInode(_sb);
            _fileDentry = new Dentry("mapped", Inode, _root, _sb);
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
            bool writable, out IPageHandle? pageHandle)
        {
            pageHandle = new TrackingPageHandle(_ptr, () => DisposeCount++);
            return true;
        }
    }

    private sealed class TrackingPageHandle : IPageHandle
    {
        private readonly Action _onDispose;
        private int _disposed;

        public TrackingPageHandle(IntPtr pointer, Action onDispose)
        {
            Pointer = pointer;
            _onDispose = onDispose;
        }

        public IntPtr Pointer { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _onDispose();
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
