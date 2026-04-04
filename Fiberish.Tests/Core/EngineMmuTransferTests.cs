using System.Runtime.InteropServices;
using System.Reflection;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Core;

public class EngineMmuTransferTests
{
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
